using DevHub.Sdk.Core.Settings;
using Xunit;

namespace DevHub.Projects.Core.Tests;

public class ProjectsSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "devhub-projects-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Folders_and_open_with_overrides_round_trip()
    {
        var store = new JsonSettingsStore(_dir);

        store.UpdateSection<ProjectsSettings>(s =>
        {
            s.RepositoryFolders = [@"C:\Projects", @"D:\Work"];
            s.LanguageTools["dotnet"] = new LanguageToolChoice { ToolId = ToolIds.VsCode };
            s.LanguageTools["go"] = new LanguageToolChoice { ToolId = ToolIds.Custom, CustomExe = @"C:\e.exe" };
        });

        var loaded = new JsonSettingsStore(_dir).LoadSection<ProjectsSettings>();
        Assert.Equal([@"C:\Projects", @"D:\Work"], loaded.RepositoryFolders);
        Assert.Equal(ToolIds.VsCode, loaded.LanguageTools["dotnet"].ToolId);
        Assert.Equal(@"C:\e.exe", loaded.LanguageTools["go"].CustomExe);
    }

    [Fact]
    public void Settings_written_before_the_plugin_split_are_read_into_the_section()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "DevHub"));
        File.WriteAllText(Path.Combine(_dir, "DevHub", "settings.json"), """
            {
              "languageTools": { "unity": { "toolId": "custom", "customExe": "C:\\u.exe" } },
              "repositoryFolders": [ "C:\\Projects" ]
            }
            """);

        var section = new JsonSettingsStore(_dir).LoadSection<ProjectsSettings>();

        Assert.Equal([@"C:\Projects"], section.RepositoryFolders);
        Assert.Equal(ToolIds.Custom, section.LanguageTools["unity"].ToolId);
        Assert.Equal(@"C:\u.exe", section.LanguageTools["unity"].CustomExe);
    }
}
