// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Generic;
using System.Linq;
using PdfStruct.Analysis;
using PdfStruct.Models;
using Xunit;

namespace PdfStruct.Tests;

/// <summary>
/// Content (classifier) regression tests for <see cref="LineNumberRailDetector"/>:
/// a gutter rail between body columns and a margin rail are removed in full,
/// while ordinary numeric content (a table column, a short cluster, a single
/// number) is left untouched.
/// </summary>
public class LineNumberRailDetectorTests
{
    private const double PageWidth = 612.0;
    private const double PageHeight = 792.0;

    [Fact]
    public void RemovesGutterRailBetweenTwoColumns()
    {
        var lines = new List<TextLineBlock>();
        lines.AddRange(BodyColumn(left: 70, right: 290));
        lines.AddRange(BodyColumn(left: 320, right: 540));
        var railIndices = new List<int>();
        for (var value = 5; value <= 60; value += 5)
        {
            railIndices.Add(lines.Count);
            lines.Add(Digit(value, centerX: 302, top: 700 - (value / 5 - 1) * 50));
        }

        var rail = LineNumberRailDetector.Detect(lines, PageWidth, PageHeight);

        Assert.Equal(railIndices.OrderBy(i => i), rail.OrderBy(i => i));
    }

    [Fact]
    public void RemovesLeftMarginRail()
    {
        var lines = new List<TextLineBlock>();
        lines.AddRange(BodyColumn(left: 110, right: 540));
        var railIndices = new List<int>();
        for (var value = 1; value <= 12; value++)
        {
            railIndices.Add(lines.Count);
            lines.Add(Digit(value, centerX: 40, top: 720 - (value - 1) * 55));
        }

        var rail = LineNumberRailDetector.Detect(lines, PageWidth, PageHeight);

        Assert.Equal(railIndices.OrderBy(i => i), rail.OrderBy(i => i));
    }

    [Fact]
    public void KeepsLeadingNumericColumnOfTable()
    {
        // A numeric index column with content only to its right (no flanking
        // text on the left) is a table column, not a gutter rail.
        var lines = new List<TextLineBlock>();
        for (var value = 1; value <= 8; value++)
        {
            var top = 700 - (value - 1) * 70;
            lines.Add(Digit(value, centerX: 90, top: top));
            lines.Add(Text("Some row description text", left: 130, right: 520, top: top));
        }

        var rail = LineNumberRailDetector.Detect(lines, PageWidth, PageHeight);

        Assert.Empty(rail);
    }

    [Fact]
    public void KeepsShortNumericCluster()
    {
        var lines = new List<TextLineBlock>
        {
            Digit(5, centerX: 302, top: 700),
            Digit(10, centerX: 302, top: 650),
            Digit(15, centerX: 302, top: 600)
        };
        lines.AddRange(BodyColumn(left: 70, right: 290));
        lines.AddRange(BodyColumn(left: 320, right: 540));

        var rail = LineNumberRailDetector.Detect(lines, PageWidth, PageHeight);

        Assert.Empty(rail);
    }

    [Fact]
    public void KeepsNonMonotonicGutterDigits()
    {
        // Digits in a gutter that do not increase downward are not a rail
        // (e.g. equation tags, reference markers).
        var lines = new List<TextLineBlock>();
        lines.AddRange(BodyColumn(left: 70, right: 290));
        lines.AddRange(BodyColumn(left: 320, right: 540));
        int[] values = { 12, 7, 33, 4, 18 };
        for (var i = 0; i < values.Length; i++)
            lines.Add(Digit(values[i], centerX: 302, top: 700 - i * 120));

        var rail = LineNumberRailDetector.Detect(lines, PageWidth, PageHeight);

        Assert.Empty(rail);
    }

    [Fact]
    public void KeepsSingleStandaloneNumber()
    {
        var lines = new List<TextLineBlock>();
        lines.AddRange(BodyColumn(left: 70, right: 540));
        lines.Add(Digit(42, centerX: 302, top: 400));

        var rail = LineNumberRailDetector.Detect(lines, PageWidth, PageHeight);

        Assert.Empty(rail);
    }

    private static IEnumerable<TextLineBlock> BodyColumn(double left, double right)
    {
        for (var i = 0; i < 12; i++)
            yield return Text("Body text on this line of the column", left, right, top: 700 - i * 50);
    }

    private static TextLineBlock Digit(int value, double centerX, double top, double fontSize = 9.0)
    {
        var width = value >= 10 ? 7.0 : 4.0;
        var bbox = new BoundingBox(centerX - width / 2, top - fontSize, centerX + width / 2, top);
        return new TextLineBlock(bbox, value.ToString(), "Courier", fontSize, false, top - fontSize, fontSize);
    }

    private static TextLineBlock Text(string text, double left, double right, double top, double fontSize = 10.0)
    {
        var bbox = new BoundingBox(left, top - fontSize, right, top);
        return new TextLineBlock(bbox, text, "Times", fontSize, false, top - fontSize, fontSize);
    }
}
