using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevHub.Board.Core;
using DevHub.Board.Core.Model;
using DevHub.Board.Core.Storage;
using DevHub.Sdk.Core.Charts;
using DevHub.Sdk.Core.Tables;
using DevHub.Sdk.Core.Localization;

namespace DevHub.Board.Risks.Core;

/// <summary>Where a risk stands.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RiskStatus>))]
public enum RiskStatus
{
    /// <summary>Identified, nothing done yet.</summary>
    Open,

    /// <summary>The mitigation plan is under way.</summary>
    Mitigating,

    /// <summary>It happened: the contingency plan applies.</summary>
    Occurred,

    /// <summary>No longer a risk.</summary>
    Closed,
}

/// <summary>
/// <c>risks/&lt;id&gt;.json</c>: a risk of the project. Probability and impact from 1 (very low) to 5 (very high), their
/// product the score (1–25); an owner, what's done to make it less likely or less bad (mitigation) and what's done if it
/// happens (contingency); issues that carry the work.
/// </summary>
public sealed record Risk
{
    public int Schema { get; init; } = BoardJson.SchemaVersion;
    public string Id { get; init; } = "";

    /// <summary>"R-3": numbered in the order they were raised.</summary>
    public int Number { get; init; }

    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Category { get; init; } = "";
    public int Probability { get; init; } = 3;
    public int Impact { get; init; } = 3;
    public RiskStatus Status { get; init; } = RiskStatus.Open;
    public string? Owner { get; init; }
    public string Mitigation { get; init; } = "";
    public string Contingency { get; init; } = "";
    public DateOnly? Review { get; init; }
    public IReadOnlyList<string> Issues { get; init; } = [];
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Updated { get; init; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    [JsonIgnore] public int Score => Probability * Impact;
    [JsonIgnore] public string Key => "R-" + Number.ToString(CultureInfo.InvariantCulture);

    /// <summary>Low (1–4), Medium (5–9), High (10–15), Critical (16–25).</summary>
    [JsonIgnore] public string Level => RiskRegister.LevelOf(Score);

    public override string ToString() => $"{Key} {Title}";
}

/// <summary>Reading the register, the matrix and the table.</summary>
public static class RiskRegister
{
    public const string Folder = "risks/";

    public static string PathOf(string id) => Folder + id + ".json";

    /// <summary>The scale's words, 1 to 5.</summary>
    public static readonly IReadOnlyList<string> Scale = [L.T("Very low"), L.T("Low"), L.T("Medium"), L.T("High"), L.T("Very high")];

    public static readonly IReadOnlyList<string> Categories = [L.T("Team"), L.T("Technical"), L.T("Schedule"), L.T("Scope"), L.T("External"), L.T("Budget"), L.T("Quality")];

    public static string LevelOf(int score) => score switch
    {
        >= 16 => L.T("Critical"),
        >= 10 => L.T("High"),
        >= 5 => L.T("Medium"),
        _ => L.T("Low"),
    };

    /// <summary>Every risk, highest score first, then by number.</summary>
    public static IReadOnlyList<Risk> All(BoardSnapshot board) =>
        board.FilesIn(Folder)
            .Select(f => (Id: BoardPaths.IdOf(f.Key, Folder, ".json"), Risk: TryRead(f.Value)))
            .Where(r => r.Id is not null && r.Risk is not null)
            .Select(r => r.Risk! with { Id = r.Id! })
            .OrderByDescending(r => r.Status is RiskStatus.Closed ? -1 : r.Score)
            .ThenBy(r => r.Number)
            .ToList();

    public static Risk? Get(BoardSnapshot board, string id) =>
        board.Files.TryGetValue(PathOf(id), out var bytes) && TryRead(bytes) is { } risk ? risk with { Id = id } : null;

    /// <summary>
    /// The probability × impact matrix as heatmap data: rows are probability (5 at the top), columns impact (1 to 5),
    /// each cell's value its score and its text the keys of the risks in it (closed ones left out).
    /// </summary>
    public static ChartData Matrix(IReadOnlyList<Risk> risks)
    {
        var open = risks.Where(r => r.Status != RiskStatus.Closed).ToList();
        var rows = new List<ChartSeries>();
        var texts = new List<IReadOnlyList<string?>>();
        for (var p = 5; p >= 1; p--)
        {
            rows.Add(new ChartSeries($"{p} · {Scale[p - 1]}", Enumerable.Range(1, 5).Select(i => (double?)(p * i)).ToList()));
            texts.Add(Enumerable.Range(1, 5).Select(i =>
            {
                var here = open.Where(r => r.Probability == p && r.Impact == i).Select(r => r.Key).ToList();
                return here.Count == 0 ? null : string.Join(" ", here);
            }).ToList());
        }
        return new ChartData(Enumerable.Range(1, 5).Select(i => $"{i} · {Scale[i - 1]}").ToList(), rows)
        {
            Title = L.T("Probability × impact"),
            XTitle = L.T("Impact"),
            YTitle = L.T("Probability"),
            CellText = texts,
            TrafficColors = true,
        };
    }

    /// <summary>The register as a table (annex): every risk with its scores, owner and plans.</summary>
    public static TableData Table(BoardSnapshot board, IReadOnlyList<Risk> risks) =>
        new([L.T("Risk"), L.T("Title"), L.T("Category"), L.T("Probability"), L.T("Impact"), L.T("Score"), L.T("Level"), L.T("Status"), L.T("Owner"), L.T("Mitigation"), L.T("Contingency"), L.T("Review"), L.T("Issues")],
            risks.Select(r => (IReadOnlyList<string>)
            [
                r.Key, r.Title, r.Category, r.Probability.ToString(CultureInfo.InvariantCulture), r.Impact.ToString(CultureInfo.InvariantCulture),
                r.Score.ToString(CultureInfo.InvariantCulture), r.Level, r.Status.ToString(), board.Project.Person(r.Owner)?.Name ?? "",
                r.Mitigation, r.Contingency, r.Review?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                string.Join("; ", r.Issues.Select(i => board.Issue(i)?.Key).OfType<string>()),
            ]).ToList()) { Title = L.T("Risk register") };

    private static Risk? TryRead(byte[] bytes)
    {
        try
        {
            return BoardJson.Deserialize<Risk>(bytes);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Raises a risk: numbered after the others, open, with the scores given (checked).</summary>
public sealed record AddRisk(string Title) : IBoardOperation
{
    public string? Id { get; init; }
    public int Probability { get; init; } = 3;
    public int Impact { get; init; } = 3;
    public string Category { get; init; } = "";
    public string? Owner { get; init; }

    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var all = RiskRegister.All(board);
        var change = new ChangeBuilder(board, context);
        var risk = RiskRules.Check(board, new Risk
        {
            Id = Id ?? change.NewId(),
            Number = all.Count == 0 ? 1 : all.Max(r => r.Number) + 1,
            Title = Title,
            Probability = Probability,
            Impact = Impact,
            Category = Category,
            Owner = Owner,
            Created = context.Now,
            Updated = context.Now,
        });
        if (all.Any(r => r.Id == risk.Id))
            throw new BoardRuleException(L.T("That risk id is taken."));
        change.Write(RiskRegister.PathOf(risk.Id), risk);
        return change.Build(L.T($"{risk.Key} raised: {risk.Title}"));
    }
}

/// <summary>Saves a risk as edited (every field; the number and creation time stay).</summary>
public sealed record EditRisk(Risk Risk) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var before = RiskRegister.Get(board, Risk.Id) ?? throw new BoardRuleException(L.T("That risk doesn't exist any more."));
        var risk = RiskRules.Check(board, Risk with { Number = before.Number, Created = before.Created, Updated = context.Now });
        var change = new ChangeBuilder(board, context);
        change.Write(RiskRegister.PathOf(risk.Id), risk);
        return change.Build(L.T($"{risk.Key} changed"));
    }
}

public sealed record DeleteRisk(string RiskId) : IBoardOperation
{
    public BoardChange Apply(BoardSnapshot board, OperationContext context)
    {
        var risk = RiskRegister.Get(board, RiskId) ?? throw new BoardRuleException(L.T("That risk doesn't exist any more."));
        var change = new ChangeBuilder(board, context);
        change.Delete(RiskRegister.PathOf(RiskId));
        return change.Build(L.T($"{risk.Key} deleted"));
    }
}

internal static class RiskRules
{
    public static Risk Check(BoardSnapshot board, Risk risk)
    {
        var title = risk.Title.Trim();
        if (title.Length == 0)
            throw new BoardRuleException(L.T("Give the risk a title."));
        if (risk.Probability is < 1 or > 5 || risk.Impact is < 1 or > 5)
            throw new BoardRuleException(L.T("Probability and impact go from 1 to 5."));
        if (risk.Owner is not null && board.Project.Person(risk.Owner) is null)
            throw new BoardRuleException(L.T("The owner isn't on the board."));
        return risk with
        {
            Title = title,
            Description = risk.Description.Trim(),
            Category = risk.Category.Trim(),
            Mitigation = risk.Mitigation.Trim(),
            Contingency = risk.Contingency.Trim(),
            Issues = risk.Issues.Where(i => board.Issue(i) is not null).Distinct().ToList(),
        };
    }
}
