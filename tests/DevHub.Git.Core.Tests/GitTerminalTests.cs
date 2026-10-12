using DevHub.Git.Core;
using Xunit;

namespace DevHub.Git.Core.Tests;

public class GitTerminalTests
{
    private const string Repo = @"C:\work\app";

    [Theory]
    [InlineData(@"C:\Program Files\Git\cmd\git.exe")]
    [InlineData(@"C:\Program Files\Git\bin\git.exe")]
    [InlineData(@"C:\Program Files\Git\mingw64\bin\git.exe")]
    public void Git_Bash_is_found_next_to_any_git_exe_of_the_installation(string git)
    {
        var bash = @"C:\Program Files\Git\git-bash.exe";

        var command = GitTerminal.Choose(Repo, git, path => path == bash, _ => @"C:\apps\wt.exe");

        Assert.Equal("Git Bash", command.Name);
        Assert.Equal(bash, command.FileName);
        Assert.Equal(["--cd=" + Repo], command.Arguments);
        Assert.Equal(Repo, command.WorkingDirectory);
    }

    [Fact]
    public void Without_Git_Bash_Windows_Terminal_opens_in_the_repository()
    {
        var command = GitTerminal.Choose(Repo, @"C:\tools\git.exe", _ => false,
            program => program == "wt.exe" ? @"C:\apps\wt.exe" : null);

        Assert.Equal(("Windows Terminal", @"C:\apps\wt.exe"), (command.Name, command.FileName));
        Assert.Equal(["-d", Repo], command.Arguments);
    }

    [Fact]
    public void PowerShell_is_the_last_resort()
    {
        var command = GitTerminal.Choose(Repo, null, _ => false, _ => null);

        Assert.Equal(("PowerShell", "powershell.exe"), (command.Name, command.FileName));
        Assert.Equal(Repo, command.WorkingDirectory);
    }
}
