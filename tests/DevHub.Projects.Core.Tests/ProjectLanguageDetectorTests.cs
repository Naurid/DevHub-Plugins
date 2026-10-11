using DevHub.Projects.Core;
using Xunit;

namespace DevHub.Projects.Core.Tests;

public class ProjectLanguageDetectorTests
{
    private static readonly ProjectLanguageDetector Detector = new();

    [Fact]
    public void Detects_csharp_from_csproj()
    {
        using var dir = new TempDir();
        dir.Touch("App.csproj");

        Assert.Equal("dotnet", Detector.Detect(dir.Path).Id);
    }

    [Fact]
    public void Unity_wins_over_stray_csproj()
    {
        // A Unity project also contains generated .csproj files; the Unity signal must win.
        using var dir = new TempDir();
        dir.MakeDir("Assets");
        dir.MakeDir("ProjectSettings");
        dir.Touch("Assembly-CSharp.csproj");

        Assert.Equal("unity", Detector.Detect(dir.Path).Id);
    }

    [Fact]
    public void Detects_rust_from_cargo_toml()
    {
        using var dir = new TempDir();
        dir.Touch("Cargo.toml");

        Assert.Equal("rust", Detector.Detect(dir.Path).Id);
    }

    [Fact]
    public void Detects_go_from_go_mod()
    {
        using var dir = new TempDir();
        dir.Touch("go.mod");

        Assert.Equal("go", Detector.Detect(dir.Path).Id);
    }

    [Fact]
    public void Detects_javascript_from_package_json()
    {
        using var dir = new TempDir();
        dir.Touch("package.json");

        Assert.Equal("javascript", Detector.Detect(dir.Path).Id);
    }

    [Fact]
    public void Detects_typescript_when_tsconfig_present()
    {
        using var dir = new TempDir();
        dir.Touch("package.json");
        dir.Touch("tsconfig.json");

        Assert.Equal("typescript", Detector.Detect(dir.Path).Id);
    }

    [Fact]
    public void Detects_python_from_pyproject()
    {
        using var dir = new TempDir();
        dir.Touch("pyproject.toml");

        Assert.Equal("python", Detector.Detect(dir.Path).Id);
    }

    [Fact]
    public void Detects_tauri_from_src_tauri_folder()
    {
        using var dir = new TempDir();
        dir.MakeDir("src-tauri");
        dir.Touch("src-tauri/tauri.conf.json");

        Assert.Equal("tauri", Detector.Detect(dir.Path).Id);
    }

    [Fact]
    public void Tauri_wins_over_frontend_package_json()
    {
        // A Tauri project also has a JS/TS frontend; the src-tauri signal must win.
        using var dir = new TempDir();
        dir.Touch("package.json");
        dir.MakeDir("src-tauri");

        Assert.Equal("tauri", Detector.Detect(dir.Path).Id);
    }

    [Fact]
    public void Unknown_when_no_markers()
    {
        using var dir = new TempDir();
        dir.Touch("README.md");

        Assert.Equal(ProjectLanguage.Unknown, Detector.Detect(dir.Path));
    }

    /// <summary>Unique temp directory, deleted on dispose.</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "devhub-lang-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Touch(string name) => File.WriteAllText(System.IO.Path.Combine(Path, name), "");
        public void MakeDir(string name) => Directory.CreateDirectory(System.IO.Path.Combine(Path, name));

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best-effort */ }
        }
    }
}
