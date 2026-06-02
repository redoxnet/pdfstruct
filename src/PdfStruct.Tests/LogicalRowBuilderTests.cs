// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Analysis.Tables;
using PdfStruct.Models;
using Xunit;

namespace PdfStruct.Tests;

public class LogicalRowBuilderTests
{
    [Fact]
    public void Build_WrappedCellContinuation_MergesIntoRowAbove()
    {
        // A data cell wraps to a second line ("good" / "result"); the continuation
        // carries no row label and aligns to the column above, so it is the same
        // logical row.
        var words = new List<TableCellRecovery.Word>
        {
            W("Name", 70, 700), W("Score", 200, 700), W("Notes", 320, 700),
            W("Alice", 70, 684), W("91", 200, 684), W("good", 320, 684),
            W("result", 320, 674),
            W("Bob", 70, 658), W("82", 200, 658), W("ok", 320, 658),
        };
        var region = new BoundingBox(50, 648, 360, 712);

        var rows = LogicalRowBuilder.Build(words, region, []);

        Assert.Equal(3, rows.Count);
        var middle = rows[1].Select(w => w.Text).ToList();
        Assert.Contains("good", middle);
        Assert.Contains("result", middle);
        Assert.Contains("Alice", middle);
    }

    [Fact]
    public void Build_DistinctRecords_StaySeparateRows()
    {
        // Each record opens a label in the anchor column, so none merges.
        var words = new List<TableCellRecovery.Word>
        {
            W("Name", 70, 700), W("Score", 200, 700),
            W("Alice", 70, 684), W("91", 200, 684),
            W("Bob", 70, 668), W("82", 200, 668),
            W("Cara", 70, 652), W("77", 200, 652),
        };
        var region = new BoundingBox(50, 642, 240, 712);

        var rows = LogicalRowBuilder.Build(words, region, []);

        Assert.Equal(4, rows.Count);
    }

    [Fact]
    public void Build_MultiLevelHeader_KeepsGroupAndLeafRowsSeparate()
    {
        // A group label row sits above a leaf header that introduces finer columns;
        // the leaf row is not a continuation of the group row.
        var words = new List<TableCellRecovery.Word>
        {
            W("GroupA", 135, 700), W("GroupB", 285, 700),
            W("x", 100, 688), W("y", 170, 688), W("z", 250, 688), W("w", 320, 688),
            W("1", 100, 672), W("2", 170, 672), W("3", 250, 672), W("4", 320, 672),
            W("5", 100, 656), W("6", 170, 656), W("7", 250, 656), W("8", 320, 656),
        };
        var region = new BoundingBox(80, 646, 340, 712);

        var rows = LogicalRowBuilder.Build(words, region, []);

        Assert.Equal(4, rows.Count);
        Assert.Equal(["GroupA", "GroupB"], rows[0].Select(w => w.Text).ToArray());
        Assert.Equal(["x", "y", "z", "w"], rows[1].Select(w => w.Text).ToArray());
    }

    [Fact]
    public void Build_RuledGrid_BandsAreLogicalRowsAcrossWrappedLines()
    {
        // Three regular full-width rule bands; the first cell of a band wraps to two
        // lines, but the band is one logical row.
        var ruleYs = new List<double> { 712, 690, 668, 646 };
        var words = new List<TableCellRecovery.Word>
        {
            W("Long", 80, 705), W("alpha", 200, 705),
            W("label", 80, 697),
            W("Short", 80, 679), W("beta", 200, 679),
            W("Tiny", 80, 657), W("gamma", 200, 657),
        };
        var region = new BoundingBox(60, 646, 260, 712);

        var rows = LogicalRowBuilder.Build(words, region, ruleYs);

        Assert.Equal(3, rows.Count);
        Assert.Contains("Long", rows[0].Select(w => w.Text));
        Assert.Contains("label", rows[0].Select(w => w.Text));
    }

    [Fact]
    public void Build_RuledGrid_SplitsMultipleCompleteRecordsInsideOneBand()
    {
        // Dense tables may omit the rule between adjacent body rows. If each
        // baseline opens the row-label column and carries data columns, they are
        // separate records, not a wrapped cell.
        var ruleYs = new List<double> { 712, 690, 668, 646 };
        var words = new List<TableCellRecovery.Word>
        {
            W("Alice", 80, 705), W("91", 200, 705),
            W("Bob", 80, 697), W("82", 200, 697),
            W("Cara", 80, 679), W("77", 200, 679),
            W("Dana", 80, 657), W("73", 200, 657),
        };
        var region = new BoundingBox(60, 646, 260, 712);

        var rows = LogicalRowBuilder.Build(words, region, ruleYs);

        Assert.Equal(4, rows.Count);
        Assert.Equal(["Alice", "91"], rows[0].Select(w => w.Text).ToArray());
        Assert.Equal(["Bob", "82"], rows[1].Select(w => w.Text).ToArray());
    }

    private static TableCellRecovery.Word W(string text, double centerX, double centerY, double width = 24, double height = 8) =>
        new(new BoundingBox(centerX - width / 2, centerY - height / 2, centerX + width / 2, centerY + height / 2), text);
}
