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
