using System.Net;
using System.Text;
using System.Text.Json;
using DevHub.Git.Core;
using DevHub.GitHub.Core;
using DevHub.TestKit;
using Xunit;

namespace DevHub.GitHub.Core.Tests;

public class GitHubApiTests
{
    /// <summary>Answers requests from a queue and records them; no network.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = new();
        public Queue<HttpResponseMessage> Responses { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request, body));
            return Responses.Dequeue();
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string Repo(string name, bool isPrivate = false) =>
        $$"""{"full_name":"me/{{name}}","name":"{{name}}","description":null,"private":{{(isPrivate ? "true" : "false")}},"clone_url":"https://github.com/me/{{name}}.git","html_url":"https://github.com/me/{{name}}","pushed_at":"2026-10-01T12:00:00Z"}""";

    [Fact]
    public async Task Requests_carry_the_token_and_GitHubs_headers()
    {
        var handler = new FakeHandler();
        handler.Responses.Enqueue(Json("""{"login":"Naurid","name":null,"avatar_url":"https://avatars.githubusercontent.com/u/42?v=4"}"""));
        var api = new GitHubApi(new HttpClient(handler));

        var user = await api.GetUserAsync("gho_secret");

        Assert.Equal(new GitHubUser("Naurid", null, "https://avatars.githubusercontent.com/u/42?v=4"), user);
        var request = handler.Requests.Single().Request;
        Assert.Equal("https://api.github.com/user", request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer gho_secret", request.Headers.Authorization!.ToString());
        Assert.Equal("2022-11-28", request.Headers.GetValues("X-GitHub-Api-Version").Single());
        Assert.Contains("DevHub", request.Headers.UserAgent.ToString());
        Assert.Contains("application/vnd.github+json", request.Headers.Accept.ToString());
    }

    [Fact]
    public async Task Avatars_are_fetched_without_the_token_and_only_from_GitHubs_avatar_host()
    {
        var handler = new FakeHandler();
        handler.Responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        var api = new GitHubApi(new HttpClient(handler));

        Assert.Equal([1, 2, 3], await api.GetAvatarAsync("https://avatars.githubusercontent.com/u/42?v=4", 64));
        Assert.Null(await api.GetAvatarAsync("https://evil.example.com/a.png"));
        Assert.Null(await api.GetAvatarAsync("http://avatars.githubusercontent.com/u/42"));

        var request = handler.Requests.Single().Request; // the other two never went out
        Assert.Equal("https://avatars.githubusercontent.com/u/42?v=4&s=64", request.RequestUri!.AbsoluteUri);
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task Repository_listing_follows_the_next_page_links()
    {
        var handler = new FakeHandler();
        var first = Json($"[{Repo("one", isPrivate: true)},{Repo("two")}]");
        first.Headers.Add("Link", "<https://api.github.com/user/repos?per_page=100&page=2>; rel=\"next\", <https://api.github.com/user/repos?per_page=100&page=2>; rel=\"last\"");
        handler.Responses.Enqueue(first);
        handler.Responses.Enqueue(Json($"[{Repo("three")}]"));
        var api = new GitHubApi(new HttpClient(handler));

        var repos = await api.ListRepositoriesAsync("t");

        Assert.Equal(["one", "two", "three"], repos.Select(r => r.Name));
        Assert.True(repos[0].IsPrivate);
        Assert.Equal("https://github.com/me/one.git", repos[0].CloneUrl);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), repos[0].PushedAt);
        Assert.Equal("https://api.github.com/user/repos?per_page=100&page=2", handler.Requests[1].Request.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task A_next_link_to_another_host_is_not_followed()
    {
        var handler = new FakeHandler();
        var first = Json($"[{Repo("one")}]");
        first.Headers.Add("Link", "<https://evil.example.com/steal>; rel=\"next\"");
        handler.Responses.Enqueue(first);
        var api = new GitHubApi(new HttpClient(handler));

        Assert.Single(await api.ListRepositoriesAsync("t"));
        Assert.Single(handler.Requests); // the token never went anywhere else
    }

    [Fact]
    public async Task Creating_a_repository_posts_name_privacy_and_no_readme()
    {
        var handler = new FakeHandler();
        handler.Responses.Enqueue(Json(Repo("new-app", isPrivate: true), HttpStatusCode.Created));
        var api = new GitHubApi(new HttpClient(handler));

        var created = await api.CreateRepositoryAsync("t", "new-app", "  My app ", isPrivate: true);

        Assert.Equal("me/new-app", created.FullName);
        var (request, body) = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.github.com/user/repos", request.RequestUri!.AbsoluteUri);
        using var json = JsonDocument.Parse(body!);
        Assert.Equal("new-app", json.RootElement.GetProperty("name").GetString());
        Assert.Equal("My app", json.RootElement.GetProperty("description").GetString());
        Assert.True(json.RootElement.GetProperty("private").GetBoolean());
        Assert.False(json.RootElement.GetProperty("auto_init").GetBoolean());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Sign in again")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "already")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task Errors_are_explained_without_the_token(HttpStatusCode status, string expected)
    {
        var handler = new FakeHandler();
        handler.Responses.Enqueue(Json("""{"message":"x"}""", status));
        var api = new GitHubApi(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<GitHubException>(() => api.CreateRepositoryAsync("gho_secret", "taken", null, false));

        Assert.Contains(expected, ex.Message);
        Assert.DoesNotContain("gho_secret", ex.Message);
        Assert.Equal(status, ex.Status);
    }

    [Theory]
    [InlineData("my-app", true)]
    [InlineData("My_App.v2", true)]
    [InlineData("has space", false)]
    [InlineData("..", false)]
    [InlineData("", false)]
    [InlineData("café", false)]
    public void Repository_names_follow_GitHubs_rules(string name, bool valid) =>
        Assert.Equal(valid, GitHubService.IsValidRepositoryName(name));

    [Fact]
    public void Credential_manager_output_is_parsed()
    {
        Assert.Equal(["Naurid", "work-account"], GitCredentialManager.ParseAccounts("Naurid\r\n\nwork-account\n"));
        Assert.Equal("gho_abc", GitCredentialManager.ParsePassword("protocol=https\nhost=github.com\nusername=Naurid\npassword=gho_abc\n"));
        Assert.Null(GitCredentialManager.ParsePassword("protocol=https\nhost=github.com\n"));
    }

    [GitFact]
    public async Task Sensitive_commands_keep_their_output_out_of_the_git_console()
    {
        using var repo = new TempGitRepo();

        await repo.Runner.RunAsync(repo.Path, ["--version"], new GitRunOptions { Sensitive = true });

        var entry = repo.Log.Entries.Last();
        Assert.Equal("git --version", entry.CommandLine);
        Assert.DoesNotContain("git version", entry.Output);
    }
}
