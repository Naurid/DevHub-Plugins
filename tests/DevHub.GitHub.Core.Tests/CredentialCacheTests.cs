using System.Net;
using System.Text;
using DevHub.Git.Core;
using DevHub.GitHub.Core;
using Xunit;

namespace DevHub.GitHub.Core.Tests;

public class CredentialCacheTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Counts the Git Credential Manager lookups the cache makes.</summary>
    private sealed class Lookups
    {
        public int AccountLists, TokenFills;
        public string Token = "t1";

        public CredentialCache Cache(TimeProvider time) => new(
            _ => { AccountLists++; return Task.FromResult<IReadOnlyList<string>>(["me"]); },
            (_, _) => { TokenFills++; return Task.FromResult<string?>(Token); },
            time);
    }

    [Fact]
    public async Task A_burst_of_calls_asks_Git_Credential_Manager_once()
    {
        var lookups = new Lookups();
        var cache = lookups.Cache(new Clock());
        for (var i = 0; i < 20; i++)
        {
            await cache.AccountsAsync();
            await cache.TokenAsync("me");
        }

        Assert.Equal((1, 1), (lookups.AccountLists, lookups.TokenFills));
    }

    [Fact]
    public async Task Kept_values_expire_and_signing_in_or_out_forgets_them()
    {
        var clock = new Clock();
        var lookups = new Lookups();
        var cache = lookups.Cache(clock);
        await cache.TokenAsync("me");

        clock.Now += CredentialCache.DefaultLifetime + TimeSpan.FromSeconds(1);
        await cache.TokenAsync("me");
        Assert.Equal(2, lookups.TokenFills);

        cache.Forget();
        await cache.AccountsAsync();
        await cache.TokenAsync("me");
        Assert.Equal((1, 3), (lookups.AccountLists, lookups.TokenFills));
    }

    [Fact]
    public async Task No_stored_sign_in_is_not_kept()
    {
        var lookups = new Lookups { Token = null! };
        var cache = lookups.Cache(new Clock());
        Assert.Null(await cache.TokenAsync("me"));

        lookups.Token = "t2";   // the user signs in elsewhere (another app)
        Assert.Equal("t2", await cache.TokenAsync("me"));
    }

    [Fact]
    public async Task A_refused_token_is_borrowed_again_once()
    {
        var lookups = new Lookups();
        var handler = new Handler(HttpStatusCode.Unauthorized, HttpStatusCode.OK);
        var git = new GitRunner(new GitExecutable("git", new Version(2, 45)), new GitCommandLog());
        var service = new GitHubService(new GitCredentialManager(git), new GitHubApi(new HttpClient(handler)), lookups.Cache(new Clock()));

        var response = await service.SendRawAsync(null, "GET", "user");

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal(2, lookups.TokenFills);
        Assert.Equal(2, handler.Calls);
    }

    private sealed class Handler(params HttpStatusCode[] answers) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(answers[Math.Min(Calls++, answers.Length - 1)])
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
    }
}
