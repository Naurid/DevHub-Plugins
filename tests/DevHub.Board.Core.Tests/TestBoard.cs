using DevHub.Board.Core;
using DevHub.Board.Core.Model;
using DevHub.Board.Core.Storage;

namespace DevHub.Board.Core.Tests;

/// <summary>A clock tests move by hand.</summary>
public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Predictable ids: a0000001, a0000002, …</summary>
public sealed class SequentialIds
{
    private int _next;
    public string Next() => "a" + (++_next).ToString("D7", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A throw-away folder in %TEMP%; Dispose deletes it.</summary>
public sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "devhub-board-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>A board in a temp folder with a manual clock and predictable ids.</summary>
public sealed class TestBoard : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    public static readonly BoardUser Ada = new("Ada Lovelace", "ada@example.com");

    private TestBoard(TempFolder folder, BoardSession session, ManualTime time, SequentialIds ids)
    {
        Folder = folder;
        Session = session;
        Time = time;
        Ids = ids;
    }

    public TempFolder Folder { get; }
    public BoardSession Session { get; }
    public ManualTime Time { get; }
    public SequentialIds Ids { get; }
    public BoardSnapshot Snapshot => Session.Snapshot;

    public static TestBoard Create(BoardPreset preset = BoardPreset.Scrum)
    {
        var folder = new TempFolder();
        var time = new ManualTime(Start);
        var ids = new SequentialIds();
        var session = BoardSession.CreateAsync(new FolderBoardStore(folder.Path), new CreateBoard("DEV", "DevHub", preset, Ada),
            time, ids.Next).GetAwaiter().GetResult();
        return new TestBoard(folder, session, time, ids);
    }

    public BoardChange Apply(IBoardOperation operation) => Session.ApplyAsync(operation).GetAwaiter().GetResult();

    /// <summary>Creates an issue and returns it.</summary>
    public Issue Create(string type, string summary, string? parent = null)
    {
        var id = Ids.Next();
        Apply(new CreateIssue(type, summary) { Id = id, Parent = parent });
        return Snapshot.Issue(id)!;
    }

    public Issue Get(string id) => Snapshot.Issue(id)!;

    /// <summary>Writes files straight to the store (as another program would), then reloads the session.</summary>
    public void WriteRaw(string path, byte[]? bytes)
    {
        Session.Store.WriteAsync(new BoardChange("raw", new Dictionary<string, byte[]?> { [path] = bytes }), Snapshot.Version)
            .GetAwaiter().GetResult();
        Session.ReloadAsync().GetAwaiter().GetResult();
    }

    public void WriteRaw<T>(string path, T value) => WriteRaw(path, BoardJson.Serialize(value));

    /// <summary>The same board loaded fresh from disk.</summary>
    public BoardSnapshot Reload() =>
        BoardSnapshot.Load(new FolderBoardStore(Folder.Path).LoadAsync().GetAwaiter().GetResult()!);

    public void Dispose()
    {
        Session.Dispose();
        Folder.Dispose();
    }
}
