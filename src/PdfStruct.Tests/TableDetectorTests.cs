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

    private static DetectedTable Region(BoundingBox box, string kind) => new(box, kind);
}
