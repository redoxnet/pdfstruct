// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Analysis;
using PdfStruct.Analysis.Tables;
using PdfStruct.Models;
using Xunit;

namespace PdfStruct.Tests;

public class StructuredRegionClassifierTests
{
    [Fact]
    public void Classify_InteriorVerticalRule_IsGrid()
    {
        // A drawn column boundary is grid evidence even when the cell text never
        // gap-split, so single-cell rows still classify as a grid (1901 style).
        var region = new BoundingBox(50, 100, 350, 250);
        var vrules = new[] { VRule(200, 110, 240) };
        var lines = new List<TextLineBlock>
        {
            Cell("row one whole line", 60, 220, width: 200),
            Cell("row two whole line", 60, 190, width: 200),
            Cell("row three whole line", 60, 160, width: 200),
        };

        Assert.Equal(RegionStructure.Grid, StructuredRegionClassifier.Classify(lines, region, vrules, columnsAlreadyValidated: false).Kind);
    }

    [Fact]
    public void Classify_BorderlessStableColumns_IsGrid()
    {
        // No rules, but the cells repeat the same three column anchors down the
        // rows — a borderless data table (us_patent style).
        var region = new BoundingBox(50, 100, 350, 200);
        var lines = new List<TextLineBlock>
        {
            Cell("a", 60, 180), Cell("b", 160, 180), Cell("c", 260, 180),
            Cell("d", 60, 160), Cell("e", 160, 160), Cell("f", 260, 160),
            Cell("g", 60, 140), Cell("h", 160, 140), Cell("i", 260, 140),
        };

        var result = StructuredRegionClassifier.Classify(lines, region, [], columnsAlreadyValidated: false);
        Assert.Equal(RegionStructure.Grid, result.Kind);
        Assert.Equal(3, result.Columns.Count);
    }

    [Fact]
    public void Classify_KeyValueForm_IsBlock()
    {
        // No interior vertical rules; a stable label column but values scattered
        // at unrepeated positions — a key-value form, not a grid (kr_patent).
        var region = new BoundingBox(50, 100, 350, 260);
        var lines = new List<TextLineBlock>
        {
            Cell("(19)", 60, 220), Cell("value one", 180, 220, width: 60),
            Cell("(45)", 60, 200), Cell("value two", 260, 200, width: 60),
            Cell("(51)", 60, 180), Cell("value three", 150, 180, width: 60),
            Cell("(73)", 60, 160), Cell("value four", 300, 160, width: 40),
        };

        var result = StructuredRegionClassifier.Classify(lines, region, [], columnsAlreadyValidated: false);
        Assert.Equal(RegionStructure.Block, result.Kind);
        Assert.Empty(result.Columns);
    }

    [Fact]
    public void Classify_BorderlessDetected_TrustedAsGridDespiteHeaderDilution()
    {
        // The borderless detector only fires after proving a multi-column schema.
        // A header band added above the body can dilute anchor stability below the
        // block threshold, but the region must stay a grid (us_patent header fix).
        var region = new BoundingBox(50, 100, 350, 230);
        var lines = new List<TextLineBlock>
        {
            Cell("LR AP", 120, 220, width: 80),
            Cell("a", 60, 180), Cell("b", 160, 180), Cell("c", 260, 180),
            Cell("longer label", 110, 160, width: 70), Cell("e", 160, 160),
            Cell("g", 60, 140), Cell("h", 160, 140), Cell("i", 260, 140),
        };

        var result = StructuredRegionClassifier.Classify(lines, region, [], columnsAlreadyValidated: true);
        Assert.Equal(RegionStructure.Grid, result.Kind);
    }

    private static BoundingBox VRule(double x, double bottom, double top) => new(x, bottom, x + 0.5, top);

    private static TextLineBlock Cell(string text, double left, double baseline, double font = 10, double width = 40) =>
        new(
            new BoundingBox(left, baseline, left + width, baseline + font),
            text,
            FontName: "Body",
            FontSize: font,
            IsBold: false,
            BaselineY: baseline,
            AvgHeight: font);
}
