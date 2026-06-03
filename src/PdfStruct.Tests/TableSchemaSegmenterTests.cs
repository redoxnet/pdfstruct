// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Analysis.Tables;
using PdfStruct.Models;
using Xunit;

namespace PdfStruct.Tests;

public class TableSchemaSegmenterTests
{
    private static readonly BoundingBox Region = new(0, 0, 100, 100);

    // Three bands at y = [70,100], [40,70], [0,40].
    private static readonly IReadOnlyList<double> RuleYs = [100, 70, 40, 0];

    [Fact]
    public void Split_AdjacentBandsWithConflictingColumns_CutsBetweenThem()
    {
        // Top band has columns {30,60}; the lower two share {20,50,80}. Neither set
        // is a subset of the other, so the schema changes — a cut. The lower two
        // are identical, so they stay one pane.
        var rules = new List<BoundingBox>
        {
            V(30, 70, 100), V(60, 70, 100),
            V(20, 40, 70), V(50, 40, 70), V(80, 40, 70),
            V(20, 0, 40), V(50, 0, 40), V(80, 0, 40),
        };

        var boxes = TableSchemaSegmenter.Split(Region, RuleYs, rules);

        Assert.Equal(2, boxes.Count);
        // First pane spans the region top down to the cut at y = 70; the second
        // runs from the cut to the region bottom.
        Assert.Equal(100, boxes[0].Top, 3);
        Assert.Equal(70, boxes[0].Bottom, 3);
        Assert.Equal(70, boxes[1].Top, 3);
        Assert.Equal(0, boxes[1].Bottom, 3);
    }

    [Fact]
    public void Split_ConstantColumnSchema_DoesNotSplit()
    {
        // Every band has the same columns — one schema, returned whole.
        var rules = new List<BoundingBox>
        {
            V(30, 70, 100), V(60, 70, 100),
            V(30, 40, 70), V(60, 40, 70),
            V(30, 0, 40), V(60, 0, 40),
        };

        var boxes = TableSchemaSegmenter.Split(Region, RuleYs, rules);

        Assert.Single(boxes);
    }

    [Fact]
    public void Split_GroupSeparatorsNestInsideLeafColumns_DoesNotSplit()
    {
        // A multi-level header: the top band's group separator {50} is a subset of
        // the leaf columns {25,50,75} below. Nesting is one schema, not a
        // transition, so the region must not be cut — this protects ruled
        // multi-level headers from being split between their header and body.
        var rules = new List<BoundingBox>
        {
            V(50, 70, 100),
            V(25, 40, 70), V(50, 40, 70), V(75, 40, 70),
            V(25, 0, 40), V(50, 0, 40), V(75, 0, 40),
        };

        var boxes = TableSchemaSegmenter.Split(Region, RuleYs, rules);

        Assert.Single(boxes);
    }

    [Fact]
    public void Split_FewerThanTwoBands_DoesNotSplit()
    {
        var boxes = TableSchemaSegmenter.Split(Region, [100, 0], [V(50, 0, 100)]);
        Assert.Single(boxes);
    }

    /// <summary>A vertical rule of negligible width at <paramref name="x"/> spanning the y-range it rules.</summary>
    private static BoundingBox V(double x, double bottom, double top) => new(x - 0.5, bottom, x + 0.5, top);
}
