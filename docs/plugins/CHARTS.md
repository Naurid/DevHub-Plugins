# Charts (`devhub.charts`)

A chart maker: paste a table (or open a CSV), pick a chart type and a style, and save it as an image for your
documents. Requires nothing. In the Planning preset.

Code: `plugins/Charts/DevHub.Charts` (the Chart Maker tab) and `DevHub.Charts.Core` (`ChartSpec`, table parsing;
tested in `tests/DevHub.Charts.Core.Tests`). The charts themselves are the Sdk's (`DevHub.Controls` charts,
`ChartData` in Sdk.Core): any plugin can draw them without this plugin.

## Using it

- **Chart Maker** (File menu, Welcome, Find Action): one tab.
  - **The table**: paste from Excel, Google Sheets or Word (tab-separated) or a CSV (comma or semicolon, quotes),
    or type it. The first row names the series, the first column holds the labels; **Rows are series** flips that.
    Numbers are read the way people type them ("12.5", "12,5", "1 250", "45%", "€ 12").
  - **The chart**: every kind the Sdk draws that fits the data (line, area, stacked area, bar, stacked bar,
    horizontal bar, combo, scatter, pie, donut, heatmap, gauge), a title and axis titles, a value suffix, and per
    series: colour (palette 1–6), dashed or dotted lines, markers, value labels, curved lines, bars or a line in a
    combo chart. Heatmaps can use traffic colours (green → amber → red).
  - **Save** as `*.devhub-chart.json` to come back to it; **export** as PNG, copy as an image, or the table as CSV /
    cells.
- **Open in Chart Maker** from Board Reports (and any plugin that uses `IChartMaker`) opens a report's chart here
  with its numbers, to restyle or change it.

## Documents

`ChartMaker` (single tab; `Navigate.ChartMaker`, File menu).

## Settings

None of its own.

## The file format

`<name>.devhub-chart.json` (`ChartSpec`): the table as text, the kind, whether rows are series, titles, the value
suffix, traffic colours and each series' style.

## For other plugins

| Offers | Hook | Used by |
|---|---|---|
| Open a chart's data in the Chart Maker | `IChartMaker.Open(kind, data)` | Board Reports |

Inject it as `IChartMaker? chartMaker = null` and show "Open in Chart Maker" only when it's there.

## Diagrams

`docs/diagrams/Charts/`: use cases, the activity from a table to an image, and the classes.
