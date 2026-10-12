using DevHub.Git.Core;
using DevHub.TestKit;
using Xunit;

namespace DevHub.Git.Core.Tests;

public class RemoteTests
{
    // ---- Parsing / explaining (no git needed) ---------------------------------------------------

    [Theory]
    [InlineData("Receiving objects:  45% (450/1000), 1.20 MiB | 2.00 MiB/s", "Receiving objects", 45)]
    [InlineData("remote: Counting objects: 100% (5/5), done.", "Counting objects (remote)", 100)]
    [InlineData("Writing objects: 100% (3/3), 250 bytes | 250.00 KiB/s, done.", "Writing objects", 100)]
    [InlineData("remote: Enumerating objects: 5, done.", "Enumerating objects (remote)", null)]
    public void Progress_lines_are_parsed(string line, string phase, int? percent)
    {
        Assert.Equal(new GitProgress(phase, percent), ProgressParser.Parse(line));
    }

    [Theory]
    [InlineData("From github.com:me/repo")]
    [InlineData("   1a2b3c4..5d6e7f8  main       -> origin/main")]
    [InlineData("")]
    public void Other_lines_are_not_progress(string line) => Assert.Null(ProgressParser.Parse(line));

    [Theory]
    [InlineData("remote: Invalid username or password.\nfatal: Authentication failed for 'https://github.com/me/x.git/'", RemoteErrorKind.Authentication)]
    [InlineData("fatal: could not read Username for 'https://github.com': terminal prompts disabled", RemoteErrorKind.Authentication)]
    [InlineData("git@github.com: Permission denied (publickey).\nfatal: Could not read from remote repository.", RemoteErrorKind.Authentication)]
    [InlineData("remote: Permission to me/x.git denied to other.\nfatal: unable to access 'https://github.com/me/x.git/': The requested URL returned error: 403", RemoteErrorKind.Authentication)]
    [InlineData("Host key verification failed.\nfatal: Could not read from remote repository.", RemoteErrorKind.HostKey)]
    [InlineData("fatal: unable to access 'https://nohost.example/x.git/': Could not resolve host: nohost.example", RemoteErrorKind.Network)]
    [InlineData("remote: Repository not found.\nfatal: repository 'https://github.com/me/nope.git/' not found", RemoteErrorKind.RepositoryNotFound)]
    [InlineData(" ! [rejected]        main -> main (fetch first)\nerror: failed to push some refs", RemoteErrorKind.Rejected)]
    [InlineData("fatal: The current branch topic has no upstream branch.", RemoteErrorKind.NoUpstream)]
    [InlineData("There is no tracking information for the current branch.", RemoteErrorKind.NoUpstream)]
    [InlineData("hint: Diverging branches can't be fast-forwarded\nfatal: Not possible to fast-forward, aborting.", RemoteErrorKind.Diverged)]
    [InlineData("error: Your local changes to the following files would be overwritten by merge:\n\ta.txt", RemoteErrorKind.LocalChangesInTheWay)]
    [InlineData("CONFLICT (content): Merge conflict in a.txt\nerror: could not apply 1a2b3c4... from alice", RemoteErrorKind.Conflicts)]
    [InlineData("fatal: something nobody expected", RemoteErrorKind.Other)]
    public void Errors_are_recognised(string stderr, RemoteErrorKind kind)
    {
        var error = RemoteErrorExplainer.Explain(stderr);

        Assert.Equal(kind, error.Kind);
        Assert.False(string.IsNullOrWhiteSpace(error.Explanation));
    }

    [Fact]
    public void An_unknown_error_keeps_gits_own_message()
    {
        var error = RemoteErrorExplainer.Explain("hint: some hint\nfatal: something nobody expected\n");

        Assert.Equal("fatal: something nobody expected", error.Explanation);
    }

    // ---- Real git: two clones of one bare "server" repository -----------------------------------

    /// <summary>A bare repository (the "server") with one commit on main, and two clones of it.</summary>
    private sealed class TwoClones : IDisposable
    {
        public TwoClones()
        {
            Server = new TempGitRepo(init: false);
            Server.Run("init", "--bare", "-b", "main", ".");

            // file:// makes git use its real transport (with progress), not the local shortcut.
            ServerUrl = new Uri(Server.Path).AbsoluteUri;
            Alice = Clone();
            Alice.WriteFile("a.txt", "1\n");
            Alice.CommitAll("first");
            Alice.Run("push", "-q", "-u", "origin", "main");
            Bob = Clone();
        }

        public TempGitRepo Server { get; }
        public string ServerUrl { get; }
        public TempGitRepo Alice { get; }
        public TempGitRepo Bob { get; }

        private TempGitRepo Clone()
        {
            var repo = new TempGitRepo(init: false);
            repo.Run("clone", "-q", ServerUrl, ".");
            return repo;
        }

        public void Dispose()
        {
            Alice.Dispose();
            Bob.Dispose();
            Server.Dispose();
        }
    }

    [GitFact]
    public async Task Fetch_then_fast_forward_pull_brings_in_the_other_clones_commit()
    {
        using var t = new TwoClones();
        t.Bob.WriteFile("b.txt", "bob\n");
        t.Bob.CommitAll("from bob");
        await t.Bob.Client.PushAsync();

        var progress = new List<GitProgress>();
        await t.Alice.Client.FetchAsync(new SyncProgress<GitProgress>(progress.Add));

        Assert.Equal(1, (await t.Alice.Client.GetStatusAsync()).Behind);
        Assert.NotEmpty(progress);

        await t.Alice.Client.PullAsync();

        var status = await t.Alice.Client.GetStatusAsync();
        Assert.Equal((0, 0), (status.Ahead, status.Behind));
        Assert.True(File.Exists(Path.Combine(t.Alice.Path, "b.txt")));
    }

    [GitFact]
    public async Task Diverged_branches_fail_fast_forward_and_rebase_fixes_it()
    {
        using var t = new TwoClones();
        t.Bob.WriteFile("b.txt", "bob\n");
        t.Bob.CommitAll("from bob");
        await t.Bob.Client.PushAsync();
        t.Alice.WriteFile("c.txt", "alice\n");
        t.Alice.CommitAll("from alice");
        t.Alice.WriteFile("a.txt", "uncommitted edit\n"); // --autostash keeps it

        var ex = await Assert.ThrowsAsync<RemoteOperationException>(() => t.Alice.Client.PullAsync());
        Assert.Equal(RemoteErrorKind.Diverged, ex.Error.Kind);

        await t.Alice.Client.PullAsync(PullMode.Rebase);

        var status = await t.Alice.Client.GetStatusAsync();
        Assert.Equal((1, 0), (status.Ahead, status.Behind));
        Assert.Equal("uncommitted edit\n", File.ReadAllText(Path.Combine(t.Alice.Path, "a.txt")).Replace("\r\n", "\n"));
        var log = await t.Alice.Client.GetLogAsync(10);
        Assert.Equal(["from alice", "from bob", "first"], log.Select(c => c.Subject));
    }

    [GitFact]
    public async Task A_push_behind_the_remote_is_rejected_and_explained()
    {
        using var t = new TwoClones();
        t.Bob.WriteFile("b.txt", "bob\n");
        t.Bob.CommitAll("from bob");
        await t.Bob.Client.PushAsync();
        t.Alice.WriteFile("c.txt", "alice\n");
        t.Alice.CommitAll("from alice");

        Assert.Equal(["from alice"], (await t.Alice.Client.GetUnpushedCommitsAsync()).Select(c => c.Subject));

        var ex = await Assert.ThrowsAsync<RemoteOperationException>(() => t.Alice.Client.PushAsync());

        Assert.Equal(RemoteErrorKind.Rejected, ex.Error.Kind);
        Assert.Contains("rejected", ex.GitOutput);
    }

    [GitFact]
    public async Task Force_with_lease_replaces_a_rewritten_commit()
    {
        using var t = new TwoClones();
        t.Alice.WriteFile("a.txt", "2\n");
        t.Alice.CommitAll("second");
        await t.Alice.Client.PushAsync();
        t.Alice.Run("commit", "-q", "--amend", "-m", "second, reworded");

        await Assert.ThrowsAsync<RemoteOperationException>(() => t.Alice.Client.PushAsync());
        await t.Alice.Client.PushAsync(forceWithLease: true);

        var remoteSubject = t.Server.Run("log", "-1", "--format=%s", "main").StandardOutput.Trim();
        Assert.Equal("second, reworded", remoteSubject);
    }

    [GitFact]
    public async Task A_new_branch_is_pushed_with_its_upstream_set()
    {
        using var t = new TwoClones();
        t.Alice.Run("switch", "-q", "-c", "topic");
        t.Alice.WriteFile("t.txt", "topic\n");
        t.Alice.CommitAll("topic work");

        var noUpstream = await Assert.ThrowsAsync<RemoteOperationException>(() => t.Alice.Client.PullAsync());
        Assert.Equal(RemoteErrorKind.NoUpstream, noUpstream.Error.Kind);

        await t.Alice.Client.PushAsync(newUpstreamRemote: "origin");

        var status = await t.Alice.Client.GetStatusAsync();
        Assert.Equal("origin/topic", status.Upstream);
        Assert.Equal(0, status.Ahead);
        Assert.Contains("topic", t.Server.Run("branch", "--list", "topic").StandardOutput);
        Assert.Equal(["origin"], await t.Alice.Client.GetRemotesAsync());
    }

    [GitFact]
    public async Task A_remote_that_isnt_a_repository_is_explained()
    {
        using var repo = new TempGitRepo();
        repo.WriteFile("a.txt", "1");
        repo.CommitAll("first");
        repo.Run("remote", "add", "origin", Path.Combine(repo.Path, "..", "no-such-repo"));

        var ex = await Assert.ThrowsAsync<RemoteOperationException>(() => repo.Client.FetchAsync());

        Assert.Equal(RemoteErrorKind.RepositoryNotFound, ex.Error.Kind);
    }

    /// <summary>Calls back immediately (Progress&lt;T&gt; would post to a thread pool and race the asserts).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
