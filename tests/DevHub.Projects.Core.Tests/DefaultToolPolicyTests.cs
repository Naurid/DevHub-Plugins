using DevHub.Projects.Core;
using Xunit;

namespace DevHub.Projects.Core.Tests;

public class DefaultToolPolicyTests
{
    private static readonly string[] AllTools =
        { ToolIds.Rider, ToolIds.VsCode, ToolIds.VisualStudio, ToolIds.UnityHub };

    [Theory]
    [InlineData("unity", ToolIds.UnityHub)]
    [InlineData("dotnet", ToolIds.Rider)]
    [InlineData("tauri", ToolIds.VsCode)]
    [InlineData("python", ToolIds.VsCode)]
    [InlineData("rust", ToolIds.VsCode)]
    [InlineData("go", ToolIds.VsCode)]
    [InlineData("typescript", ToolIds.VsCode)]
    [InlineData("unknown", ToolIds.OsDefault)]
    public void Picks_expected_default_when_all_tools_available(string language, string expected)
    {
        Assert.Equal(expected, DefaultToolPolicy.DefaultFor(language, AllTools));
    }

    [Fact]
    public void Falls_back_to_os_default_when_preferred_tool_missing()
    {
        var onlyVsCode = new[] { ToolIds.VsCode };

        Assert.Equal(ToolIds.OsDefault, DefaultToolPolicy.DefaultFor("dotnet", onlyVsCode)); // no Rider
        Assert.Equal(ToolIds.OsDefault, DefaultToolPolicy.DefaultFor("unity", onlyVsCode));  // no Unity Hub
        Assert.Equal(ToolIds.VsCode, DefaultToolPolicy.DefaultFor("python", onlyVsCode));
        Assert.Equal(ToolIds.VsCode, DefaultToolPolicy.DefaultFor("tauri", onlyVsCode));
    }
}
