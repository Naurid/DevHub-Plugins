using DevHub.Projects.Core;
using Xunit;

namespace DevHub.Projects.Core.Tests;

public class UnityEditorResolverTests
{
    [Fact]
    public void Resolves_installed_editor_from_version_file()
    {
        using var tree = new TempTree();
        tree.WriteProjectVersion("6000.3.13f1");
        var exe = tree.InstallEditor("6000.3.13f1");

        var resolved = UnityEditorResolver.Resolve(tree.RepoPath, tree.EditorsRoot);

        Assert.Equal(exe, resolved);
    }

    [Fact]
    public void Returns_null_when_editor_not_installed()
    {
        using var tree = new TempTree();
        tree.WriteProjectVersion("1234.5.6f7"); // nothing installed

        Assert.Null(UnityEditorResolver.Resolve(tree.RepoPath, tree.EditorsRoot));
    }

    [Fact]
    public void Returns_null_when_version_file_missing()
    {
        using var tree = new TempTree();

        Assert.Null(UnityEditorResolver.Resolve(tree.RepoPath, tree.EditorsRoot));
    }

    [Fact]
    public void Reads_the_editor_version_string()
    {
        using var tree = new TempTree();
        tree.WriteProjectVersion("6000.2.7f2");

        Assert.Equal("6000.2.7f2", UnityEditorResolver.ReadEditorVersion(tree.RepoPath));
    }

    private sealed class TempTree : IDisposable
    {
        public string RepoPath { get; }
        public string EditorsRoot { get; }

        public TempTree()
        {
            var baseDir = Path.Combine(Path.GetTempPath(), "devhub-unity-" + Guid.NewGuid().ToString("N"));
            RepoPath = Path.Combine(baseDir, "repo");
            EditorsRoot = Path.Combine(baseDir, "editors");
            Directory.CreateDirectory(RepoPath);
            Directory.CreateDirectory(EditorsRoot);
        }

        public void WriteProjectVersion(string version)
        {
            var dir = Path.Combine(RepoPath, "ProjectSettings");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "ProjectVersion.txt"),
                $"m_EditorVersion: {version}\nm_EditorVersionWithRevision: {version} (abc123)\n");
        }

        public string InstallEditor(string version)
        {
            var dir = Path.Combine(EditorsRoot, version, "Editor");
            Directory.CreateDirectory(dir);
            var exe = Path.Combine(dir, "Unity.exe");
            File.WriteAllText(exe, "");
            return exe;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path.GetDirectoryName(RepoPath)!, true); } catch { }
        }
    }
}
