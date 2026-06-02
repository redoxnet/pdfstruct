// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Analysis;
using PdfStruct.Analysis.Tables;
using PdfStruct.Models;
using Xunit;

namespace PdfStruct.Tests;

public class TableTextRowBuilderTests
{
    [Fact]
    public void Build_PerRowGridWithMultiLineCell_MergesCellIntoOneRow()
    {
        // A per-row ruled grid (full-width rules every row, a vertical separator,
        // a repeated two-column schema) whose first data row has a cell wrapping
        // to two lines: the wrap must join into one row, not three (google p4).
        var region = new BoundingBox(50, 100, 350, 250);
        var rules = new[] { HRule(50, 350, 240), HRule(50, 350, 210), HRule(50, 350, 180), HRule(50, 350, 150), HRule(50, 350, 120) };
        var vrules = new[] { VRule(150, 100, 250) };
        var lines = new List<TextLineBlock>
        {
            Cell("Name", 60, 245), Cell("Detail", 160, 245),
            Cell("Alpha", 60, 222), Cell("alpha detail one", 160, 230, width: 120), Cell("alpha detail two", 160, 214, width: 120),
            Cell("Beta", 60, 195), Cell("beta detail", 160, 195, width: 120),
            Cell("Gamma", 60, 165), Cell("gamma detail", 160, 165, width: 120),
            Cell("Delta", 60, 135), Cell("delta detail", 160, 135, width: 120),
        };

        var rows = TableTextRowBuilder.Build(lines, region, rules, vrules);

        Assert.Equal(5, rows.Count);
        Assert.Contains(rows, r => r.Contains("Alpha") && r.Contains("alpha detail one") && r.Contains("alpha detail two"));
    }

    [Fact]
    public void Build_BooktabsTable_DoesNotCollapseBodyIntoOneRow()
    {
        // Only top/mid/bottom rules: the body band holds several single-line
        // records. Rule-band segmentation must not fire (two bands is not a grid),
        // so each record stays its own row.
        var region = new BoundingBox(50, 100, 350, 250);
        var rules = new[] { HRule(50, 350, 240), HRule(50, 350, 210), HRule(50, 350, 110) };
        var lines = new List<TextLineBlock>
        {
            Cell("H1", 60, 245), Cell("H2", 160, 245), Cell("H3", 260, 245),
            Cell("a", 60, 195), Cell("1", 160, 195), Cell("2", 260, 195),
            Cell("b", 60, 180), Cell("3", 160, 180), Cell("4", 260, 180),
            Cell("c", 60, 165), Cell("5", 160, 165), Cell("6", 260, 165),
            Cell("d", 60, 150), Cell("7", 160, 150), Cell("8", 260, 150),
        };

        var rows = TableTextRowBuilder.Build(lines, region, rules, []);

        Assert.Equal(5, rows.Count);
    }

    [Fact]
    public void Build_FormWithSectionSeparatorRules_KeepsBaselineRows()
    {
        // A key-value form: the rules separate sections (one tall body band among
        // thin ones), no repeated row schema. It must not be read as a per-row
        // grid — the body keeps its baseline rows rather than collapsing.
        var region = new BoundingBox(50, 100, 350, 260);
        var rules = new[] { HRule(50, 350, 250), HRule(50, 350, 230), HRule(50, 350, 130), HRule(50, 350, 115) };
        var lines = new List<TextLineBlock>
        {
            Cell("Header", 60, 255, width: 80),
            Cell("(51) key", 60, 220, width: 80), Cell("value one", 200, 220, width: 100),
            Cell("(52) key", 60, 200, width: 80), Cell("value two", 200, 200, width: 100),
            Cell("(21) key", 60, 180, width: 80), Cell("value three", 200, 180, width: 100),
            Cell("(22) key", 60, 160, width: 80), Cell("value four", 200, 160, width: 100),
            Cell("(56) key", 60, 140, width: 80), Cell("value five", 200, 140, width: 100),
            Cell("footer", 60, 122, width: 80),
        };

        var rows = TableTextRowBuilder.Build(lines, region, rules, []);

        Assert.Equal(7, rows.Count);
    }

    private static BoundingBox HRule(double left, double right, double y) => new(left, y, right, y + 0.5);

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
