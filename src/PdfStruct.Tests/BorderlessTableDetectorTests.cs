// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct;
using PdfStruct.Analysis;
using PdfStruct.Analysis.Tables;
using PdfStruct.Models;
using Xunit;

namespace PdfStruct.Tests;

public class BorderlessTableDetectorTests
{
    [Fact]
    public void Detect_CleanGrid_EmitsOneRegion()
    {
        var lines = Grid(
            ["Name", "Age", "City"],
            ["Anna", "31", "Seoul"],
            ["Bob", "27", "Busan"]);

        var regions = BorderlessTableDetector.Detect(lines);

        var region = Assert.Single(regions);
        Assert.Equal("borderless", region.Kind);
        Assert.True(region.BoundingBox.Area > 0);
        Assert.True(region.BoundingBox.Width > 200, "Region should span all three columns.");
    }

    [Fact]
    public void Detect_FragmentedTable_MergedIntoOneRegion()
    {
        // A single-cell section row between tabular rows must not split the table.
        var lines = Grid(
            ["Female", "16", "18"],
            ["Section"],
            ["Married", "33", "29"],
            ["Literate", "2", "4"]);

        var regions = BorderlessTableDetector.Detect(lines);

        var region = Assert.Single(regions);
        Assert.True(region.BoundingBox.Height > 40, "Region should span all four rows.");
    }

    [Fact]
    public void Detect_MultiLevelHeader_IncludedAboveBody()
    {
        // A spanning group label ("GROUP", covering all columns) and an aligned
        // sub-header sit above the body; both belong to the table.
        var lines = new List<TextLineBlock>
        {
            Cell("GROUP", 50, 728, width: 260),
            Cell("h1", 50, 714), Cell("h2", 150, 714), Cell("h3", 250, 714),
            Cell("a", 50, 700), Cell("p", 150, 700), Cell("x", 250, 700),
            Cell("b", 50, 686), Cell("q", 150, 686), Cell("y", 250, 686),
            Cell("c", 50, 672), Cell("r", 250, 672), Cell("z", 350, 672),
        };

        var region = Assert.Single(BorderlessTableDetector.Detect(lines));
        Assert.True(region.BoundingBox.Top >= 728, "Region should extend up to include the spanning group header.");
    }

    [Fact]
    public void Detect_CaptionAboveHeader_Excluded()
    {
        // The "Table 1" caption is an external label, not part of the table.
        var lines = new List<TextLineBlock>
        {
            Cell("Table 1", 50, 726),
            Cell("h1", 50, 714), Cell("h2", 150, 714), Cell("h3", 250, 714),
            Cell("a", 50, 700), Cell("p", 150, 700), Cell("x", 250, 700),
            Cell("b", 50, 686), Cell("q", 150, 686), Cell("y", 250, 686),
        };

        var region = Assert.Single(BorderlessTableDetector.Detect(lines));
        Assert.True(region.BoundingBox.Top < 726, "Caption must not be absorbed into the table region.");
    }

    [Fact]
    public void Detect_TwoColumnProse_Rejected()
    {
        const string left = "the study examined patient compliance over twelve weeks";
        const string right = "results were analysed with a paired two tailed t test";
        var lines = Grid([left, right], [left, right], [left, right]);

        Assert.Empty(BorderlessTableDetector.Detect(lines));
    }

    [Fact]
    public void Detect_SingleColumn_Rejected()
    {
        var lines = new List<TextLineBlock>
        {
            Cell("First paragraph line", 50, 700),
            Cell("Second paragraph line", 50, 686),
            Cell("Third paragraph line", 50, 672),
        };

        Assert.Empty(BorderlessTableDetector.Detect(lines));
    }

    [Fact]
    public void Detect_SingleRow_Rejected()
    {
        Assert.Empty(BorderlessTableDetector.Detect(Grid(["Name", "Age", "City"])));
    }

    [Fact]
    public void Detect_NumberedDisplayEquation_Rejected()
    {
        // A display equation: scattered symbolic fragments, a flush-right equation
        // number, and no numeric data column — not a table (1901 page 6).
        var lines = new List<TextLineBlock>
        {
            Cell("s = GRU", 109, 160, width: 58), Cell("prev t", 168, 160, width: 40),
            Cell("g s", 200, 160, width: 20), Cell("(5)", 274, 160, width: 14),
            Cell("where y", 50, 134, width: 38), Cell("prev", 90, 134, width: 16),
            Cell("denotes the vectors", 110, 134, width: 176),
            Cell("previous y", 50, 120, width: 80), Cell("t-1", 131, 120, width: 28),
            Cell("and g glimpse", 150, 120, width: 130),
        };

        Assert.Empty(BorderlessTableDetector.Detect(lines));
    }

    [Fact]
    public void Detect_NumericTableWithStrayEquationNumber_StillDetected()
    {
        // A genuine numeric table carrying a lone "(1)" marker keeps its numeric
        // data column, so the formula guard does not fire.
        var lines = new List<TextLineBlock>
        {
            Cell("Model", 50, 160), Cell("Acc", 120, 160), Cell("F1", 200, 160),
            Cell("A", 50, 146), Cell("87.5", 120, 146), Cell("0.81", 200, 146),
            Cell("B", 50, 132), Cell("90.1", 120, 132), Cell("0.88", 200, 132),
            Cell("note", 50, 118), Cell("see", 120, 118), Cell("(1)", 200, 118),
        };

        Assert.NotEmpty(BorderlessTableDetector.Detect(lines));
    }

    [Fact]
    public void Parse_RulesOnlyFixture_EmitsNoTables()
    {
        var result = ParseFixture("kr_constitution.pdf");
        Assert.DoesNotContain(result.Document.Kids, e => e is TableElement);
    }

    [Fact]
    public void Parse_AcademicFixture_EmitsTableRegions()
    {
        var result = ParseFixture("plos_game_based_education.pdf");

        var tables = result.Document.Kids.OfType<TableElement>().ToList();
        Assert.NotEmpty(tables);
        Assert.All(tables, t => Assert.True(t.BoundingBox.Area > 0));
    }

    private static PdfStructResult ParseFixture(string fixtureName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName);
        Assert.True(File.Exists(path), $"Fixture missing on disk: {path}");
        return new PdfStructParser(new PdfStructOptions()).Parse(path);
    }

    private static List<TextLineBlock> Grid(params string[][] rows)
    {
        const double font = 10;
        const double lineGap = 14;
        var lines = new List<TextLineBlock>();
        var baseline = 700.0;
        foreach (var row in rows)
        {
            var left = 50.0;
            foreach (var text in row)
            {
                lines.Add(Cell(text, left, baseline, font));
                left += 100;
            }
            baseline -= lineGap;
        }
        return lines;
    }

    private static TextLineBlock Cell(string text, double left, double baseline, double font = 10, double width = 60) =>
        new(
            new BoundingBox(left, baseline, left + width, baseline + font),
            text,
            FontName: "Body",
            FontSize: font,
            IsBold: false,
            BaselineY: baseline,
            AvgHeight: font);
}
