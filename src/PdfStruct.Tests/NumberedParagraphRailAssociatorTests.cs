// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Linq;
using PdfStruct.Analysis;
using PdfStruct.Models;
using Xunit;

namespace PdfStruct.Tests;

/// <summary>
/// Content regression tests for <see cref="NumberedParagraphRailAssociator"/>:
/// a split left-rail of patent paragraph markers is rejoined to the body
/// lines sharing its rows, while ordinary content is left untouched.
/// </summary>
public class NumberedParagraphRailAssociatorTests
{
    [Fact]
    public void RejoinsLeftRailMarkersWithSameRowBodies()
    {
        var lines = new[]
        {
            Marker("[0001]", top: 700),
            Marker("[0002]", top: 650),
            Body("First paragraph body.", top: 700),
            Body("Second paragraph body.", top: 650)
        };

        var result = NumberedParagraphRailAssociator.Associate(lines);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, l => l.Text == "[0001] First paragraph body.");
        Assert.Contains(result, l => l.Text == "[0002] Second paragraph body.");
        Assert.DoesNotContain(result, l => l.Text.Trim() == "[0001]");
    }

    [Fact]
    public void MergedLineBoundingBoxSpansMarkerAndBody()
    {
        var lines = new[]
        {
            Marker("[0001]", top: 700),
            Marker("[0002]", top: 650),
            Body("First.", top: 700),
            Body("Second.", top: 650)
        };

        var result = NumberedParagraphRailAssociator.Associate(lines);

        var merged = result.First(l => l.Text.StartsWith("[0001]"));
        Assert.Equal(9.0, merged.Left, 3); // reaches back to the marker's left edge
    }

    [Fact]
    public void LeavesNonBracketedNumbersUntouched()
    {
        var lines = new[]
        {
            Marker("5", top: 700),
            Marker("10", top: 650),
            Body("Body one.", top: 700),
            Body("Body two.", top: 650)
        };

        var result = NumberedParagraphRailAssociator.Associate(lines);

        Assert.Equal(lines.Length, result.Count);
        Assert.Contains(result, l => l.Text.Trim() == "5");
    }

    [Fact]
    public void LeavesUntouchedWhenMarkerHasNoSameRowBody()
    {
        var lines = new[]
        {
            Marker("[0001]", top: 700),
            Marker("[0002]", top: 650),
            Body("Body far below.", top: 200)
        };

        var result = NumberedParagraphRailAssociator.Associate(lines);

        // Markers without a same-row body are preserved, not dropped.
        Assert.Contains(result, l => l.Text.Trim() == "[0001]");
        Assert.Contains(result, l => l.Text.Trim() == "[0002]");
    }

    private static TextLineBlock Marker(string text, double top, double left = 9.0)
    {
        var bbox = new BoundingBox(left, top - 6, left + 25, top);
        return new TextLineBlock(bbox, text, "Batang", 9.0, false, top - 6, 6.0);
    }

    private static TextLineBlock Body(string text, double top, double left = 64.0)
    {
        var bbox = new BoundingBox(left, top - 7, 494, top);
        return new TextLineBlock(bbox, text, "Batang", 10.0, false, top - 7, 7.0);
    }
}
