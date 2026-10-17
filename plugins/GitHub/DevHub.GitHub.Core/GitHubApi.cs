using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DevHub.Sdk.Core.Localization;

namespace DevHub.GitHub.Core;

/// <summary>A GitHub user (the signed-in account).</summary>
/// <param name="AvatarUrl">Their picture (GitHub makes one up when none was uploaded), or null.</param>
public sealed record GitHubUser(string Login, string? Name, string? AvatarUrl = null);

/// <summary>A repository on GitHub, as listed for Clone and returned by Publish.</summary>
public sealed record GitHubRepository(
    string FullName,
    string Name,
    string? Description,
    bool IsPrivate,
    string CloneUrl,
    string HtmlUrl,
    DateTimeOffset? PushedAt);

/// <summary>A GitHub API call failed; <see cref="Exception.Message"/> says why in plain words (never the token).</summary>
/// <summary>An API answer as it came (<see cref="GitHubApi.SendRawAsync"/>).</summary>
/// <param name="NextPageUrl">The full URL of the next page (Link header), or null on the last one.</param>
public sealed record GitHubRawResponse(HttpStatusCode Status, string Body, string? ETag, string? NextPageUrl,
    int? RateLimitRemaining, DateTimeOffset? RateLimitReset);

public sealed class GitHubException(string message, HttpStatusCode? status = null, Exception? inner = null)
    : Exception(message, inner)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// The few GitHub REST calls DevHub makes (api.github.com, version 2022-11-28): who am I, list my
/// repositories, create a repository. Each call takes the token (from <see cref="GitCredentialManager"/>);
/// nothing is cached.
/// </summary>
/// <param name="http">Shared client; tests pass one with a fake handler.</param>
public sealed class GitHubApi(HttpClient http)
{
    public static readonly Uri BaseAddress = new("https://api.github.com/");

    public async Task<GitHubUser> GetUserAsync(string token, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "user", token, null, ct);
        using var doc = await ReadJsonAsync(response, ct);
        var root = doc.RootElement;
        return new GitHubUser(root.GetProperty("login").GetString() ?? "", OptionalString(root, "name"),
            OptionalString(root, "avatar_url"));
    }

    /// <summary>
    /// Downloads an avatar picture (PNG / JPEG bytes) at <paramref name="size"/> pixels. Avatars are
    /// public: the request carries no token, and only GitHub's avatar host is accepted. Null when
    /// the URL isn't GitHub's or the download fails (callers show initials instead).
    /// </summary>
    public async Task<byte[]?> GetAvatarAsync(string avatarUrl, int size = 96, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(avatarUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals(AvatarHost, StringComparison.OrdinalIgnoreCase))
            return null;

        var sized = new UriBuilder(uri) { Query = (uri.Query.Length > 1 ? uri.Query[1..] + "&" : "") + "s=" + size }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, sized);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DevHub", "1.0"));
        try
        {
            using var response = await http.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(ct) : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>Where GitHub serves avatars.</summary>
    public const string AvatarHost = "avatars.githubusercontent.com";

    /// <summary>
    /// Repositories the account owns, collaborates on or can see through its organisations,
    /// most recently pushed first. Follows GitHub's paging (100 per page) up to <paramref name="maxPages"/>.
    /// </summary>
    public async Task<IReadOnlyList<GitHubRepository>> ListRepositoriesAsync(string token, int maxPages = 10,
        CancellationToken ct = default)
    {
        var repositories = new List<GitHubRepository>();
        string? next = "user/repos?per_page=100&sort=pushed&affiliation=owner,collaborator,organization_member";
        for (var page = 0; next is not null && page < maxPages; page++)
        {
            using var response = await SendAsync(HttpMethod.Get, next, token, null, ct);
            using (var doc = await ReadJsonAsync(response, ct))
                repositories.AddRange(doc.RootElement.EnumerateArray().Select(ParseRepository));
            next = NextPage(response);
        }
        return repositories;
    }

    /// <summary>
    /// Creates an empty repository under the account (no README, so the first push needs no
    /// merge). Throws <see cref="GitHubException"/> with "already exists" when the name is taken.
    /// </summary>
    public async Task<GitHubRepository> CreateRepositoryAsync(string token, string name, string? description,
        bool isPrivate, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["name"] = name,
            ["description"] = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            ["private"] = isPrivate,
            ["auto_init"] = false,
        });
        using var response = await SendAsync(HttpMethod.Post, "user/repos", token, body, ct,
            unprocessable: L.T($"GitHub refused the name \"{name}\": a repository with that name probably exists on your account already."));
        using var doc = await ReadJsonAsync(response, ct);
        return ParseRepository(doc.RootElement);
    }

    // ---- Any API call, for other plugins (IGitHubApi in the Sdk) -------------------------------------------

    private static readonly string[] AllowedMethods = ["GET", "POST", "PATCH", "PUT", "DELETE"];

    /// <summary>
    /// Where a request may go: <paramref name="pathOrUrl"/> relative to <see cref="BaseAddress"/>, or a full URL on it;
    /// null for anything that would end up elsewhere (another host, http, "//host/…"): the token only ever goes to the API.
    /// </summary>
    public static Uri? ApiUri(string pathOrUrl)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl))
            return null;
        Uri uri;
        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var absolute) && absolute.Scheme is "https" or "http")
            uri = absolute;
        else if (pathOrUrl.StartsWith("//", StringComparison.Ordinal) || pathOrUrl.StartsWith(@"\\", StringComparison.Ordinal))
            return null; // protocol-relative: another host
        else
            uri = new Uri(BaseAddress, pathOrUrl.TrimStart('/'));
        return uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
               && uri.Host.Equals(BaseAddress.Host, StringComparison.OrdinalIgnoreCase) ? uri : null;
    }

    /// <summary>
    /// Sends any REST call as <paramref name="token"/>'s user and returns GitHub's answer as it is (an HTTP error is a
    /// response, not an exception), with the ETag, the next page's full URL and the rate limit. Throws
    /// <see cref="GitHubException"/> only when the call can't be made: a target outside the API, a method other than
    /// GET / POST / PATCH / PUT / DELETE, or no connection.
    /// </summary>
    public async Task<GitHubRawResponse> SendRawAsync(string token, string method, string pathOrUrl, string? json = null,
        string? ifNoneMatch = null, string? accept = null, CancellationToken ct = default)
    {
        var verb = method.ToUpperInvariant();
        if (!AllowedMethods.Contains(verb))
            throw new GitHubException(L.T($"\"{method}\" isn't a method DevHub sends to GitHub."));
        var uri = ApiUri(pathOrUrl) ?? throw new GitHubException(L.T($"\"{pathOrUrl}\" isn't on api.github.com; DevHub sends your GitHub sign-in nowhere else."));

        using var request = new HttpRequestMessage(new HttpMethod(verb), uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DevHub", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept ?? "application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (ifNoneMatch is not null)
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using var response = await http.SendAsync(request, ct);
            var body = response.Content is null ? "" : await response.Content.ReadAsStringAsync(ct);
            return new GitHubRawResponse(response.StatusCode, body,
                response.Headers.ETag?.ToString(),
                NextPage(response) is { } next ? new Uri(BaseAddress, next).AbsoluteUri : null,
                Header(response, "x-ratelimit-remaining") is { } remaining && int.TryParse(remaining, out var r) ? r : null,
                Header(response, "x-ratelimit-reset") is { } reset && long.TryParse(reset, out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null);
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubException(L.T("Couldn't reach GitHub. Check your internet connection."), null, ex);
        }
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string token, string? json,
        CancellationToken ct, string? unprocessable = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(BaseAddress, path));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DevHub", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubException(L.T("Couldn't reach GitHub. Check your internet connection."), null, ex);
        }

        if (response.IsSuccessStatusCode)
            return response;

        var status = response.StatusCode;
        response.Dispose();
        throw status switch
        {
            HttpStatusCode.Unauthorized => new GitHubException(
                L.T("GitHub didn't accept the sign-in (it expired or was revoked). Sign in again in Settings → GitHub."), status),
            HttpStatusCode.Forbidden => new GitHubException(
                L.T("GitHub refused the request: the sign-in lacks the permission, or the API rate limit was hit. Try again later or sign in again."), status),
            HttpStatusCode.UnprocessableEntity when unprocessable is not null => new GitHubException(unprocessable, status),
            _ => new GitHubException(L.T($"GitHub answered {(int)status} {status}."), status),
        };
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static GitHubRepository ParseRepository(JsonElement e) => new(
        e.GetProperty("full_name").GetString() ?? "",
        e.GetProperty("name").GetString() ?? "",
        OptionalString(e, "description"),
        e.TryGetProperty("private", out var p) && p.ValueKind == JsonValueKind.True,
        e.GetProperty("clone_url").GetString() ?? "",
        e.GetProperty("html_url").GetString() ?? "",
        e.TryGetProperty("pushed_at", out var pushed) && pushed.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(pushed.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null);

    private static string? OptionalString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// The next page from GitHub's Link header (<c>&lt;https://api.github.com/user/repos?page=2&gt;; rel="next"</c>),
    /// as a path relative to the API, or null on the last page.
    /// </summary>
    public static string? NextPage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var values))
            return null;
        foreach (var part in string.Join(',', values).Split(','))
        {
            var pieces = part.Split(';');
            if (pieces.Length < 2 || !pieces.Skip(1).Any(p => p.Trim() == "rel=\"next\""))
                continue;
            var url = pieces[0].Trim().TrimStart('<').TrimEnd('>');
            // Only follow links back to the API itself (the token goes with every request).
            return url.StartsWith(BaseAddress.AbsoluteUri, StringComparison.OrdinalIgnoreCase)
                ? url[BaseAddress.AbsoluteUri.Length..]
                : null;
        }
        return null;
    }
}
