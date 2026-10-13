using DevHub.Sdk.Core.Settings;
using Xunit;

namespace DevHub.Board.Core.Tests;

public class BoardSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "devhub-board-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Identity_recent_boards_and_choices_round_trip()
    {
        var store = new JsonSettingsStore(_dir);

        store.UpdateSection<BoardSettings>(s =>
        {
            s.UserName = "Ada Lovelace";
            s.UserEmail = "ada@example.com";
            s.Remember(new RecentBoard("folder", @"C:\Boards\DEV", "DEV", "DevHub"));
            s.LastBoardView[@"C:\Boards\DEV"] = "b1";
            s.CompactCards = true;
        });

        var loaded = new JsonSettingsStore(_dir).LoadSection<BoardSettings>();
        Assert.Equal(new BoardUser("Ada Lovelace", "ada@example.com"), loaded.User);
        Assert.Equal(new RecentBoard("folder", @"C:\Boards\DEV", "DEV", "DevHub"), Assert.Single(loaded.RecentBoards));
        Assert.Equal("b1", loaded.LastBoardView[@"C:\Boards\DEV"]);
        Assert.True(loaded.CompactCards);
    }

    [Fact]
    public void Remember_moves_a_board_to_the_front_and_keeps_the_list_short()
    {
        var settings = new BoardSettings();
        for (var i = 0; i < BoardSettings.MaxRecent + 3; i++)
            settings.Remember(new RecentBoard("folder", $@"C:\B{i}", "K", "N"));
        settings.Remember(new RecentBoard("folder", @"c:\b5", "K", "Renamed"));

        Assert.Equal(BoardSettings.MaxRecent, settings.RecentBoards.Count);
        Assert.Equal("Renamed", settings.RecentBoards[0].Name);
        Assert.Single(settings.RecentBoards, b => b.Location.Equals(@"C:\B5", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Without_a_name_you_are_the_windows_user()
    {
        Assert.Equal(Environment.UserName, new BoardSettings().User.Name);
        Assert.Null(new BoardSettings().User.Email);
    }
}
