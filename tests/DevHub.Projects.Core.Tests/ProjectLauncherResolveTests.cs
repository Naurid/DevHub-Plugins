using DevHub.Projects.Core;
using DevHub.Sdk.Core.Settings;
using Xunit;

namespace DevHub.Projects.Core.Tests;

public class ProjectLauncherResolveTests
{
    [Fact]
    public void Rider_runs_rider_on_the_folder()
    {
        using var repo = new RepoDir(KnownLanguages.DotNet);
        var ctx = Context(riderPath: @"C:\rider64.exe");

        var spec = ProjectLauncher.Resolve(repo.Repo, Choice(ToolIds.Rider), ctx);

        Assert.Equal(@"C:\rider64.exe", spec.FileName);
        Assert.Equal(new[] { repo.Path }, spec.Arguments);
        Assert.True(spec.UseShellExecute); // launched detached so the IDE's logs don't leak into ours
    }

    [Fact]
    public void VsCode_runs_code_on_the_folder()
    {
        using var repo = new RepoDir(KnownLanguages.Python);
        var ctx = Context(vsCodePath: @"C:\Code.exe");

        var spec = ProjectLauncher.Resolve(repo.Repo, Choice(ToolIds.VsCode), ctx);

        Assert.Equal(@"C:\Code.exe", spec.FileName);
        Assert.Equal(new[] { repo.Path }, spec.Arguments);
    }

    [Fact]
    public void OsDefault_shell_opens_the_sln_when_present()
    {
        using var repo = new RepoDir(KnownLanguages.DotNet);
        var sln = repo.Touch("MyApp.sln");

        var spec = ProjectLauncher.Resolve(repo.Repo, Choice(ToolIds.OsDefault), Context());

        Assert.Equal(sln, spec.FileName);
        Assert.Empty(spec.Arguments);
        Assert.True(spec.UseShellExecute);
    }

    [Fact]
    public void OsDefault_shell_opens_the_folder_when_no_project_file()
    {
        using var repo = new RepoDir(KnownLanguages.Python);

        var spec = ProjectLauncher.Resolve(repo.Repo, Choice(ToolIds.OsDefault), Context());

        Assert.Equal(repo.Path, spec.FileName);
        Assert.True(spec.UseShellExecute);
    }

    [Fact]
    public void Unity_launches_the_pinned_editor_when_installed()
    {
        using var repo = new RepoDir(KnownLanguages.Unity);
        repo.WriteUnityVersion("6000.3.13f1");
        using var editors = new EditorsRoot();
        var unityExe = editors.InstallEditor("6000.3.13f1");

        var spec = ProjectLauncher.Resolve(repo.Repo, Choice(ToolIds.UnityHub), Context(unityEditorsRoot: editors.Root));

        Assert.Equal(unityExe, spec.FileName);
        Assert.Equal(new[] { "-projectPath", repo.Path }, spec.Arguments);
    }

    [Fact]
    public void Unity_falls_back_to_hub_when_editor_missing()
    {
        using var repo = new RepoDir(KnownLanguages.Unity);
        repo.WriteUnityVersion("1234.5.6f7"); // not installed
        using var editors = new EditorsRoot();

        var ctx = Context(unityHubPath: @"C:\Unity Hub.exe", unityEditorsRoot: editors.Root);
        var spec = ProjectLauncher.Resolve(repo.Repo, Choice(ToolIds.UnityHub), ctx);

        Assert.Equal(@"C:\Unity Hub.exe", spec.FileName);
        Assert.Equal(new[] { "--", "--projectPath", repo.Path }, spec.Arguments);
    }

    [Fact]
    public void Custom_runs_the_exe_on_the_folder()
    {
        using var repo = new RepoDir(KnownLanguages.Go);

        var choice = new LanguageToolChoice
        {
            ToolId = ToolIds.Custom,
            CustomExe = @"C:\tools\editor.exe"
        };

        var spec = ProjectLauncher.Resolve(repo.Repo, choice, Context());

        Assert.Equal(@"C:\tools\editor.exe", spec.FileName);
        Assert.Equal(new[] { repo.Path }, spec.Arguments);
        Assert.True(spec.UseShellExecute);
    }

    [Fact]
    public void Custom_without_exe_falls_back_to_os_default()
    {
        using var repo = new RepoDir(KnownLanguages.Go);

        var spec = ProjectLauncher.Resolve(repo.Repo, Choice(ToolIds.Custom), Context());

        // No custom exe set → safe fallback (shell-open the folder, since no project file).
        Assert.Equal(repo.Path, spec.FileName);
        Assert.True(spec.UseShellExecute);
    }

    // ---- Opening one file (diff pane "Open in Editor") ------------------------------------

    [Fact]
    public void File_in_rider_jumps_to_the_line()
    {
        using var repo = new RepoDir(KnownLanguages.DotNet);
        var file = repo.Touch("App.cs");

        var spec = ProjectLauncher.ResolveFile(repo.Repo, file, 12, Choice(ToolIds.Rider), Context(riderPath: @"C:\rider64.exe"));

        Assert.Equal(@"C:\rider64.exe", spec.FileName);
        Assert.Equal(["--line", "12", file], spec.Arguments);
    }

    [Fact]
    public void File_in_rider_without_a_line()
    {
        using var repo = new RepoDir(KnownLanguages.DotNet);
        var file = repo.Touch("App.cs");

        var spec = ProjectLauncher.ResolveFile(repo.Repo, file, null, Choice(ToolIds.Rider), Context(riderPath: @"C:\rider64.exe"));

        Assert.Equal([file], spec.Arguments);
    }

    [Fact]
    public void File_in_vscode_opens_the_repo_window_at_the_line()
    {
        using var repo = new RepoDir(KnownLanguages.Python);
        var file = repo.Touch("main.py");

        var spec = ProjectLauncher.ResolveFile(repo.Repo, file, 3, Choice(ToolIds.VsCode), Context(vsCodePath: @"C:\Code.exe"));

        Assert.Equal(@"C:\Code.exe", spec.FileName);
        Assert.Equal([repo.Path, "-g", file + ":3"], spec.Arguments);
    }

    [Fact]
    public void File_in_visual_studio_uses_edit()
    {
        using var repo = new RepoDir(KnownLanguages.DotNet);
        var file = repo.Touch("App.cs");

        var spec = ProjectLauncher.ResolveFile(repo.Repo, file, 5, Choice(ToolIds.VisualStudio), Context(devenvPath: @"C:\devenv.exe"));

        Assert.Equal(["/Edit", file], spec.Arguments);
    }

    [Fact]
    public void File_in_a_unity_repo_goes_to_a_code_editor_not_unity()
    {
        using var repo = new RepoDir(KnownLanguages.Unity);
        var file = repo.Touch("Player.cs");

        var withRider = ProjectLauncher.ResolveFile(repo.Repo, file, 7, Choice(ToolIds.UnityHub),
            Context(riderPath: @"C:\rider64.exe", vsCodePath: @"C:\Code.exe", unityHubPath: @"C:\Hub.exe"));
        var withCodeOnly = ProjectLauncher.ResolveFile(repo.Repo, file, 7, Choice(ToolIds.UnityHub),
            Context(vsCodePath: @"C:\Code.exe", unityHubPath: @"C:\Hub.exe"));
        var withNeither = ProjectLauncher.ResolveFile(repo.Repo, file, 7, Choice(ToolIds.UnityHub),
            Context(unityHubPath: @"C:\Hub.exe"));

        Assert.Equal(@"C:\rider64.exe", withRider.FileName);
        Assert.Equal(@"C:\Code.exe", withCodeOnly.FileName);
        Assert.Equal(file, withNeither.FileName); // the file's default app
        Assert.True(withNeither.UseShellExecute);
    }

    [Fact]
    public void File_with_os_default_or_a_missing_tool_opens_with_its_default_app()
    {
        using var repo = new RepoDir(KnownLanguages.Go);
        var file = repo.Touch("main.go");

        var osDefault = ProjectLauncher.ResolveFile(repo.Repo, file, 1, Choice(ToolIds.OsDefault), Context());
        var missingRider = ProjectLauncher.ResolveFile(repo.Repo, file, 1, Choice(ToolIds.Rider), Context());

        Assert.Equal(file, osDefault.FileName);
        Assert.Empty(osDefault.Arguments);
        Assert.Equal(file, missingRider.FileName);
    }

    [Fact]
    public void File_in_explorer_is_selected()
    {
        using var repo = new RepoDir(KnownLanguages.Go);
        var file = repo.Touch("main.go");

        var spec = ProjectLauncher.ResolveFile(repo.Repo, file, null, Choice(ToolIds.Explorer), Context());

        Assert.Equal("explorer.exe", spec.FileName);
        Assert.Equal(["/select,", file], spec.Arguments);
    }

    [Fact]
    public void File_with_a_custom_tool_passes_the_file()
    {
        using var repo = new RepoDir(KnownLanguages.Rust);
        var file = repo.Touch("main.rs");
        var choice = new LanguageToolChoice { ToolId = ToolIds.Custom, CustomExe = @"C:\tools\editor.exe" };

        var spec = ProjectLauncher.ResolveFile(repo.Repo, file, 9, choice, Context());

        Assert.Equal(@"C:\tools\editor.exe", spec.FileName);
        Assert.Equal([file], spec.Arguments);
    }

    private static LanguageToolChoice Choice(string toolId) => new() { ToolId = toolId };

    private static ToolContext Context(
        string? riderPath = null, string? vsCodePath = null, string? devenvPath = null,
        string? unityHubPath = null, string? unityEditorsRoot = null) =>
        new(riderPath, vsCodePath, devenvPath, unityHubPath, unityEditorsRoot ?? @"C:\none");

    /// <summary>A temp repo folder with a chosen language.</summary>
    private sealed class RepoDir : IDisposable
    {
        public string Path { get; }
        public GitRepository Repo { get; }

        public RepoDir(ProjectLanguage language)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "devhub-launch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Repo = new GitRepository(System.IO.Path.GetFileName(Path), Path, language);
        }

        public string Touch(string name)
        {
            var full = System.IO.Path.Combine(Path, name);
            File.WriteAllText(full, "");
            return full;
        }

        public void WriteUnityVersion(string version)
        {
            var settings = System.IO.Path.Combine(Path, "ProjectSettings");
            Directory.CreateDirectory(settings);
            File.WriteAllText(System.IO.Path.Combine(settings, "ProjectVersion.txt"),
                $"m_EditorVersion: {version}\n");
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }

    /// <summary>A temp Unity Hub editors-root into which we "install" editor versions.</summary>
    private sealed class EditorsRoot : IDisposable
    {
        public string Root { get; }

        public EditorsRoot()
        {
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "devhub-editors-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string InstallEditor(string version)
        {
            var dir = System.IO.Path.Combine(Root, version, "Editor");
            Directory.CreateDirectory(dir);
            var exe = System.IO.Path.Combine(dir, "Unity.exe");
            File.WriteAllText(exe, "");
            return exe;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
