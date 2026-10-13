using DevHub.Sdk.Core.Settings;

namespace DevHub.Board.Core;

/// <summary>The Board plugin's settings section: who you are on boards, recent boards, display choices.</summary>
public sealed class BoardSettings : ISettingsSection
{
    public static string SectionId => "devhub.board";

    /// <summary>Your name on boards; empty = the Windows user name.</summary>
    public string UserName { get; set; } = "";

    /// <summary>Your email on boards (matches you to git authors and avatars); optional.</summary>
    public string UserEmail { get; set; } = "";

    /// <summary>Boards opened lately, most recent first.</summary>
    public List<RecentBoard> RecentBoards { get; set; } = new();

    /// <summary>Per board location: the board definition shown last.</summary>
    public Dictionary<string, string> LastBoardView { get; set; } = new();

    /// <summary>Smaller cards (key, summary and icons only).</summary>
    public bool CompactCards { get; set; }

    public const int MaxRecent = 15;

    /// <summary>The user as boards see them.</summary>
    public BoardUser User =>
        new(string.IsNullOrWhiteSpace(UserName) ? Environment.UserName : UserName.Trim(),
            string.IsNullOrWhiteSpace(UserEmail) ? null : UserEmail.Trim());

    /// <summary>Puts a board first in <see cref="RecentBoards"/> (adding or moving it).</summary>
    public void Remember(RecentBoard board)
    {
        RecentBoards.RemoveAll(b => b.SameLocation(board));
        RecentBoards.Insert(0, board);
        if (RecentBoards.Count > MaxRecent)
            RecentBoards.RemoveRange(MaxRecent, RecentBoards.Count - MaxRecent);
    }
}

/// <summary>A board you opened: its store kind and location, and its name and key when you last saw it.</summary>
public sealed record RecentBoard(string StoreKind, string Location, string Key, string Name)
{
    public bool SameLocation(RecentBoard other) =>
        StoreKind == other.StoreKind && string.Equals(Location, other.Location, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{Key} · {Name}";
}
