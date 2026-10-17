using DevHub.Charts.Core;
using DevHub.Sdk.Core.Charts;
using Xunit;

namespace DevHub.Charts.Core.Tests;

public class TableTextTests
{
    [Fact]
    public void Pasted_cells_split_on_tabs()
    {
        var rows = TableText.Parse("Sprint\tDone\r\nS1\t13\r\n\r\nS2\t 18 \r\n");
        Assert.Equal(3, rows.Count);
        Assert.Equal(["S2", "18"], rows[2]);
    }

    [Fact]
    public void Csv_has_quotes_and_semicolons()
    {
        Assert.Equal(["Epic, big", "He said \"hi\"", "3"], TableText.Parse("\"Epic, big\",\"He said \"\"hi\"\"\",3")[0]);
        Assert.Equal(["a", "1,5"], TableText.Parse("a;1,5\nb;2")[0]);   // semicolons: a European CSV
    }

    [Theory]
    [InlineData("12.5", 12.5)]
    [InlineData("12,5", 12.5)]
    [InlineData("1,250.5", 1250.5)]
    [InlineData("1 250", 1250)]
    [InlineData("45%", 45)]
    [InlineData("-3", -3)]
    [InlineData("€ 12", 12)]
    public void Numbers_are_read_as_people_type_them(string cell, double value) => Assert.Equal(value, TableText.Number(cell));

    [Fact]
    public void Text_is_not_a_number()
    {
        Assert.Null(TableText.Number("n/a"));
        Assert.Null(TableText.Number(""));
    }
}

public class ChartBuilderTests
{
    [Fact]
    public void Columns_become_series_with_their_styles()
    {
        var (data, problem) = ChartBuilder.Build(ChartSpec.Sample);

        Assert.Null(problem);
        Assert.Equal(["Sprint 1", "Sprint 2", "Sprint 3", "Sprint 4"], data.Labels);
        Assert.Equal(["Committed", "Completed"], data.Series.Select(s => s.Name));
        Assert.Equal(SeriesKind.Bar, data.Series[0].Style.Kind);
        Assert.True(data.Series[1].Style.ValueLabels);
        Assert.Equal("Story points", data.YTitle);
    }

    [Fact]
    public void Rows_can_be_the_series()
    {
        var (data, _) = ChartBuilder.Build(new ChartSpec { Table = "Team\tQ1\tQ2\nWeb\t3\t5\nApp\t2\t", SeriesInRows = true });

        Assert.Equal(["Q1", "Q2"], data.Labels);
        Assert.Equal(["Web", "App"], data.Series.Select(s => s.Name));
        Assert.Null(data.Series[1].Values[1]);
    }

    [Fact]
    public void Problems_are_explained()
    {
        Assert.NotNull(ChartBuilder.Build(new ChartSpec()).Problem);
        Assert.NotNull(ChartBuilder.Build(new ChartSpec { Table = "a\tb" }).Problem);
        Assert.Contains("No numbers", ChartBuilder.Build(new ChartSpec { Table = "a\tb\nx\ty" }).Problem);
    }

    [Fact]
    public void A_report_chart_round_trips_through_a_spec_and_its_file()
    {
        var report = new ChartData(["S1", "S2"], [new ChartSeries("Done", [13, 18.5]) { Style = new SeriesStyle { Dash = LineDash.Dashed, Color = 2 } }])
        {
            Title = "Velocity", XTitle = "Sprint", ValueSuffix = " pts",
        };

        var spec = ChartSpec.FromJson(ChartSpec.From(ChartKind.Line, report).ToJson());
        var (data, _) = ChartBuilder.Build(spec);

        Assert.Equal(ChartKind.Line, spec.Kind);
        Assert.Equal(["S1", "S2"], data.Labels);
        Assert.Equal([13, 18.5], data.Series[0].Values.Select(v => v!.Value));
        Assert.Equal((LineDash.Dashed, 2), (data.Series[0].Style.Dash, data.Series[0].Style.Color));
        Assert.Equal(("Velocity", " pts"), (data.Title, data.ValueSuffix));
    }
}
