// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Analysis.Tables;
using PdfStruct.Models;
using Xunit;

namespace PdfStruct.Tests;

public class TableDetectorTests
{
    [Fact]
    public void Merge_NonOverlappingRegions_KeepsBoth()
    {
        var ruled = new[] { Region(new BoundingBox(50, 600, 300, 700), "ruled") };
        var borderless = new[] { Region(new BoundingBox(50, 100, 300, 200), "borderless") };

        var merged = TableDetector.Merge(ruled, borderless);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Merge_OverlappingRegions_RuledWins()
    {
        var ruled = new[] { Region(new BoundingBox(50, 600, 300, 700), "ruled") };
        var borderless = new[] { Region(new BoundingBox(60, 610, 290, 695), "borderless") };

        var merged = TableDetector.Merge(ruled, borderless);

        var region = Assert.Single(merged);
        Assert.Equal("ruled", region.Kind);
    }

    [Fact]
    public void Merge_TwoOverlappingRuledRegions_FusedEvenWithNoBorderless()
    {
        // One table whose rules split into two width-disjoint stacks must be
        // reported once, spanning the union — self-dedup is not skipped just
        // because the borderless list is empty.
        var ruled = new[]
        {
            Region(new BoundingBox(57, 322, 399, 743), "ruled"),
            Region(new BoundingBox(78, 267, 539, 743), "ruled"),
        };

        var region = Assert.Single(TableDetector.Merge(ruled, []));
        Assert.Equal(57, region.BoundingBox.Left);
        Assert.Equal(539, region.BoundingBox.Right);
        Assert.Equal(267, region.BoundingBox.Bottom);
    }

    private static DetectedTable Region(BoundingBox box, string kind) => new(box, kind);
}
