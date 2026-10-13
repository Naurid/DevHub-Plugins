using DevHub.Board.Core.Model;
using Xunit;

namespace DevHub.Board.Core.Tests;

public class RankTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("i", null)]
    [InlineData(null, "i")]
    [InlineData("a", "b")]
    [InlineData("h", "i")]
    [InlineData("hz", "i")]
    [InlineData("i", "i1")]
    [InlineData("0001", "01")]
    [InlineData("z", null)]
    [InlineData("zzzz", null)]
    [InlineData(null, "0001")]
    public void Between_is_strictly_between_and_valid(string? before, string? after)
    {
        var rank = Rank.Between(before, after);

        Assert.True(Rank.IsValid(rank), rank);
        if (before is not null)
            Assert.True(string.CompareOrdinal(before, rank) < 0, $"{before} < {rank}");
        if (after is not null)
            Assert.True(string.CompareOrdinal(rank, after) < 0, $"{rank} < {after}");
    }

    [Fact]
    public void Many_random_insertions_keep_the_order()
    {
        var random = new Random(42);
        var ranks = new List<string> { Rank.Middle };
        for (var i = 0; i < 3000; i++)
        {
            var at = random.Next(ranks.Count + 1);
            var rank = Rank.Between(at > 0 ? ranks[at - 1] : null, at < ranks.Count ? ranks[at] : null);
            ranks.Insert(at, rank);
        }

        Assert.All(ranks, r => Assert.True(Rank.IsValid(r), r));
        for (var i = 1; i < ranks.Count; i++)
            Assert.True(string.CompareOrdinal(ranks[i - 1], ranks[i]) < 0, $"{ranks[i - 1]} < {ranks[i]}");
    }

    [Fact]
    public void Inserting_at_the_same_place_again_and_again_still_works()
    {
        // Always at the top, then always just below a fixed issue: the ranks grow, but never collide.
        var top = Rank.Middle;
        for (var i = 0; i < 500; i++)
        {
            var rank = Rank.Between(null, top);
            Assert.True(string.CompareOrdinal(rank, top) < 0);
            top = rank;
        }
        var below = Rank.Middle;
        var upper = Rank.Between(null, Rank.Middle);
        for (var i = 0; i < 500; i++)
        {
            var rank = Rank.Between(upper, below);
            Assert.True(string.CompareOrdinal(upper, rank) < 0 && string.CompareOrdinal(rank, below) < 0);
            below = rank;
        }
    }

    [Fact]
    public void Spread_gives_sorted_valid_unique_ranks()
    {
        foreach (var count in new[] { 0, 1, 2, 10, 35, 36, 1000 })
        {
            var ranks = Rank.Spread(count);
            Assert.Equal(count, ranks.Count);
            Assert.All(ranks, r => Assert.True(Rank.IsValid(r), r));
            for (var i = 1; i < ranks.Count; i++)
                Assert.True(string.CompareOrdinal(ranks[i - 1], ranks[i]) < 0);
        }
    }

    [Theory]
    [InlineData("b", "a")]
    [InlineData("a", "a")]
    [InlineData("a0", null)]
    [InlineData("A", null)]
    public void Between_refuses_bad_input(string? before, string? after) =>
        Assert.Throws<ArgumentException>(() => Rank.Between(before, after));
}

public class IssueKeyTests
{
    [Theory]
    [InlineData("DEV-12", "DEV", 12)]
    [InlineData("A1-1", "A1", 1)]
    public void Parses(string text, string key, int number)
    {
        Assert.True(IssueKey.TryParse(text, out var k, out var n));
        Assert.Equal(key, k);
        Assert.Equal(number, n);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dev-12")]
    [InlineData("DEV-")]
    [InlineData("DEV-0")]
    [InlineData("-12")]
    [InlineData("DEV-+1")]
    [InlineData("1DEV-2")]
    public void Refuses(string? text) => Assert.False(IssueKey.TryParse(text, out _, out _));

    [Theory]
    [InlineData("DEV", true)]
    [InlineData("D", true)]
    [InlineData("ABCDEFGHIJ", true)]
    [InlineData("ABCDEFGHIJK", false)]
    [InlineData("dev", false)]
    [InlineData("1A", false)]
    [InlineData("DE-V", false)]
    public void Project_keys(string key, bool valid) => Assert.Equal(valid, IssueKey.IsValidProjectKey(key));
}

public class WorkDurationTests
{
    private static readonly ProjectSettings Settings = new() { HoursPerDay = 8, DaysPerWeek = 5 };

    [Theory]
    [InlineData("30m", 30)]
    [InlineData("2h", 120)]
    [InlineData("2h 30m", 150)]
    [InlineData("1d", 480)]
    [InlineData("1.5d", 720)]
    [InlineData("1,5h", 90)]
    [InlineData("1w", 2400)]
    [InlineData("1w 2d 4h 30m", 2400 + 960 + 240 + 30)]
    [InlineData("4", 240)]
    [InlineData(" 3H ", 180)]
    [InlineData("2h30m", 150)]
    public void Parses(string text, int minutes)
    {
        Assert.True(WorkDuration.TryParse(text, Settings, out var parsed));
        Assert.Equal(minutes, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("2x")]
    [InlineData("0h")]
    [InlineData("h")]
    public void Refuses(string text) => Assert.False(WorkDuration.TryParse(text, Settings, out _));

    [Theory]
    [InlineData(0, "0m")]
    [InlineData(30, "30m")]
    [InlineData(150, "2h 30m")]
    [InlineData(480, "1d")]
    [InlineData(2400 + 960 + 240 + 30, "1w 2d 4h 30m")]
    public void Formats(int minutes, string text) => Assert.Equal(text, WorkDuration.Format(minutes, Settings));

    [Fact]
    public void Uses_the_projects_working_day()
    {
        var six = new ProjectSettings { HoursPerDay = 6, DaysPerWeek = 4 };
        Assert.True(WorkDuration.TryParse("1d", six, out var day));
        Assert.Equal(360, day);
        Assert.Equal("1w", WorkDuration.Format(4 * 360, six));
    }
}
