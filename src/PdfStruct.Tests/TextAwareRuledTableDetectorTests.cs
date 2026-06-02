// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Analysis;
using PdfStruct.Analysis.Tables;
using PdfStruct.Models;
using Xunit;

namespace PdfStruct.Tests;

public class TextAwareRuledTableDetectorTests
{
    [Fact]
    public void Detect_DoubledMidRule_DoesNotSplitHeaderFromBody()
    {
        // A booktabs mid rule drawn as two near-coincident segments must not sever
        // the header band from the body — the table is one region (1901 page 10).
        var rules = new[]
        {
            HRule(50, 250, 200),
            HRule(50, 250, 170),
            HRule(120, 250, 169.4),
            HRule(50, 250, 140),
        };
        var lines = new List<TextLineBlock>
        {
            Cell("Aaa", 50, 185), Cell("Bbb", 120, 185), Cell("Ccc", 200, 185),
            Cell("1", 50, 158), Cell("2", 120, 158), Cell("3", 200, 158),
            Cell("4", 50, 148), Cell("5", 120, 148), Cell("6", 200, 148),
        };

        var region = Assert.Single(TextAwareRuledTableDetector.Detect(lines, rules, []));
        Assert.True(region.BoundingBox.Top >= 195, "Header band must be inside the single region.");
        Assert.True(region.BoundingBox.Bottom <= 145, "Body must be inside the single region.");
    }

    [Fact]
    public void Detect_MergedSpanningHeaderAboveTopRule_Included_CaptionExcluded()
    {
        // The column header "Type Configurations Size" never gap-split: it is a
        // single cell spanning the body columns, sitting above the top rule. It
        // belongs to the table; the "Table 1" caption above it does not (page 4).
        var rules = new[] { HRule(309, 550, 180), HRule(309, 550, 160), HRule(309, 550, 140) };
        var lines = new List<TextLineBlock>
        {
            Cell("Table 1 Architecture", 315, 200, width: 130),
            Cell("Type Configurations Size", 331, 188, width: 196),
            Cell("Conv", 315, 170), Cell("k3", 400, 170), Cell("64", 490, 170),
            Cell("Pool", 315, 150), Cell("k2", 400, 150), Cell("32", 490, 150),
        };

        var region = Assert.Single(TextAwareRuledTableDetector.Detect(lines, rules, []));
        Assert.True(region.BoundingBox.Top >= 195, "Merged spanning header must be included.");
        Assert.True(region.BoundingBox.Top < 209, "Caption must stay outside the region.");
    }

    [Fact]
    public void Detect_RowsSharingBaselineWithOtherColumn_ClippedToRuleExtent()
    {
        // Page-wide grouping fuses each right-column table row with a left-column
        // prose line on the same baseline. Clipping to the rule extent keeps the
        // table alive and stops its box bleeding left (1901 page 10, Table 6).
        var rules = new[] { HRule(309, 550, 200), HRule(309, 550, 170), HRule(309, 550, 140) };
        var lines = new List<TextLineBlock>
        {
            Cell("left column prose line", 50, 185, width: 236), Cell("H1", 315, 185), Cell("H2", 400, 185), Cell("H3", 490, 185),
            Cell("more left column prose", 50, 158, width: 236), Cell("a", 315, 158), Cell("b", 400, 158), Cell("c", 490, 158),
            Cell("yet more prose text here", 50, 148, width: 236), Cell("d", 315, 148), Cell("e", 400, 148), Cell("f", 490, 148),
        };

        var region = Assert.Single(TextAwareRuledTableDetector.Detect(lines, rules, []));
        Assert.True(region.BoundingBox.Left >= 300, "Box must not bleed into the left column.");
    }

    [Fact]
    public void Detect_RulesSplitAtColumnSeparator_FormOneFullWidthTable()
    {
        // A shaded/cell-bordered table draws each row rule as left and right
        // segments broken at the column separator. They must fuse into one
        // full-width table spanning every column, not two width-disjoint ones
        // (google p4 reported twice; google p5 lost its first column).
        var rules = new[]
        {
            HRule(50, 150, 200), HRule(151, 350, 200),
            HRule(50, 150, 170), HRule(151, 350, 170),
            HRule(50, 150, 140), HRule(151, 350, 140),
        };
        var lines = new List<TextLineBlock>
        {
            Cell("H1", 55, 185), Cell("H2", 160, 185), Cell("H3", 260, 185),
            Cell("a", 55, 158), Cell("b", 160, 158), Cell("c", 260, 158),
            Cell("d", 55, 148), Cell("e", 160, 148), Cell("f", 260, 148),
        };

        var region = Assert.Single(TextAwareRuledTableDetector.Detect(lines, rules, []));
        Assert.True(region.BoundingBox.Left <= 60 && region.BoundingBox.Right >= 340,
            "Region must span both column groups, not just one segment's width.");
    }

    [Fact]
    public void Detect_GridColumnsWitnessedByVerticalRulesOnly_StillDetected()
    {
        // A grid whose cell text never gap-split shows too few text anchors, but
        // its interior vertical rules witness the columns (plos p6/p10).
        var rules = new[] { HRule(50, 350, 200), HRule(50, 350, 170), HRule(50, 350, 140) };
        var vrules = new[] { VRule(150, 140, 200), VRule(250, 140, 200) };
        var lines = new List<TextLineBlock>
        {
            Cell("one whole header cell", 55, 185, width: 280),
            Cell("one whole body row cell here", 55, 158, width: 280),
            Cell("another whole body row cell", 55, 148, width: 280),
        };

        var region = Assert.Single(TextAwareRuledTableDetector.Detect(lines, rules, vrules));
        Assert.True(region.BoundingBox.Width > 250, "Region should span the ruled grid.");
    }

    [Fact]
    public void Detect_WideLeadingRuleOverhang_TrimsToPayloadStart()
    {
        // 1901 page 12 Table 9 has horizontal rules that start far left of the
        // real payload. Other tables have only ordinary border padding; this
        // wide empty overhang should not become a blank first table column.
        var rules = new[] { HRule(50, 400, 200), HRule(50, 400, 170), HRule(50, 400, 140) };
        var lines = new List<TextLineBlock>
        {
            Cell("Method", 140, 185), Cell("SVT", 240, 185), Cell("IC15", 330, 185),
            Cell("ABBYY", 140, 158), Cell("40.5", 240, 158), Cell("-", 330, 158),
            Cell("Ours", 140, 148), Cell("94.3", 240, 148), Cell("68.8", 330, 148),
        };

        var region = Assert.Single(TextAwareRuledTableDetector.Detect(lines, rules, []));

        Assert.True(region.BoundingBox.Left > 100, "Wide empty leading rule overhang should be trimmed.");
        Assert.True(region.BoundingBox.Left <= 140, "Trimmed box must still include the first payload column.");
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
