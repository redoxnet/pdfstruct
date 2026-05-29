// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Models;

namespace PdfStruct.Analysis;

/// <summary>
/// Defines a layout analyzer that determines reading order of text blocks on a PDF page.
/// </summary>
public interface ILayoutAnalyzer
{
    /// <summary>
    /// Sorts a collection of text blocks into correct reading order.
    /// </summary>
    IReadOnlyList<TextBlock> DetermineReadingOrder(IReadOnlyList<TextBlock> blocks);
}

/// <summary>
/// Represents a block of text with spatial coordinates, used as input to layout analysis.
/// </summary>
/// <param name="BoundingBox">Block bounds in PDF points.</param>
/// <param name="Text">Block text content (may contain embedded line breaks for wrapped lines).</param>
/// <param name="FontName">Dominant font name within the block.</param>
/// <param name="FontSize">Average font point size within the block.</param>
/// <param name="IsBold">Whether the dominant font is bold-styled.</param>
/// <param name="LineCount">Number of constituent lines in the block; <c>1</c> for a single-line block.</param>
/// <param name="IsStandalone">
/// <c>true</c> when no other block on the page shares this block's vertical row by more
/// than 50%. Used as a strong "this is a heading or pull-quote, not body text" signal.
/// </param>
/// <param name="FirstLineLeft">
/// Left x-coordinate of the block's first constituent line. Preserved
/// separately from <see cref="BoundingBox"/> because the merged bbox uses
/// the minimum left across all lines, which masks first-line-indented or
/// hanging-indented paragraphs and discards the indentation signal that
/// distinguishes structural levels in documents like the Korean
/// Constitution. Defaults to <see cref="double.NaN"/> for blocks
/// constructed without this signal (e.g. short-form test fixtures);
/// consumers that need the value should fall back to <see cref="BoundingBox"/>
/// in that case.
/// </param>
/// <param name="MedianLineLeft">Median (upper-median for even line counts) left x-coordinate across the block's constituent lines. NaN when not populated.</param>
/// <param name="LastLineLeft">Left x-coordinate of the block's last constituent line. NaN when not populated.</param>
/// <param name="FirstLineRight">Right x-coordinate of the block's first constituent line. NaN when not populated.</param>
/// <param name="MedianLineRight">Median (upper-median for even line counts) right x-coordinate across the block's constituent lines. NaN when not populated.</param>
/// <param name="LastLineRight">Right x-coordinate of the block's last constituent line. NaN when not populated.</param>
/// <param name="IsItalic">Whether the dominant font signals italic style, sourced from PdfPig's <c>FontDetails.IsItalic</c>. Defaults to <c>false</c> for blocks constructed without this signal.</param>
/// <param name="FontWeight">Numeric font weight (typically 100..900 in 100 increments), sourced from PdfPig's <c>FontDetails.Weight</c>. Defaults to <c>400</c> (regular).</param>
public sealed record TextBlock(
    BoundingBox BoundingBox,
    string Text,
    string FontName = "",
    double FontSize = 0,
    bool IsBold = false,
    int LineCount = 1,
    bool IsStandalone = false,
    double FirstLineLeft = double.NaN,
    double MedianLineLeft = double.NaN,
    double LastLineLeft = double.NaN,
    double FirstLineRight = double.NaN,
    double MedianLineRight = double.NaN,
    double LastLineRight = double.NaN,
    bool IsItalic = false,
    int FontWeight = 400);

/// <summary>
/// Implements the XY-Cut++ algorithm for determining reading order on PDF pages.
/// Recursively partitions the page into logical blocks by finding the largest
/// horizontal/vertical gaps, then orders partitions in natural reading sequence.
/// </summary>
public sealed class XyCutLayoutAnalyzer : ILayoutAnalyzer
{
    private const double MinGapThreshold = 5.0;
    private const double NarrowElementWidthRatio = 0.1;

    /// <summary>
    /// Initializes a new instance of <see cref="XyCutLayoutAnalyzer"/>.
    /// </summary>
    /// <param name="minGapRatioX">Retained for API compatibility; cut detection uses an absolute gap threshold in PDF points.</param>
    /// <param name="minGapRatioY">Retained for API compatibility; cut detection uses an absolute gap threshold in PDF points.</param>
    public XyCutLayoutAnalyzer(double minGapRatioX = 0.01, double minGapRatioY = 0.005)
    {
        _ = minGapRatioX;
        _ = minGapRatioY;
    }

    /// <inheritdoc />
    public IReadOnlyList<TextBlock> DetermineReadingOrder(IReadOnlyList<TextBlock> blocks)
    {
        if (blocks.Count <= 1)
            return blocks;

        var result = new List<TextBlock>(blocks.Count);
        RecursiveCut(blocks, result);
        return result;
    }

    private void RecursiveCut(
        IReadOnlyList<TextBlock> blocks,
        List<TextBlock> result)
    {
        if (blocks.Count <= 1)
        {
            result.AddRange(blocks);
            return;
        }

        var yCut = FindBestYCut(blocks);
        var xCut = FindBestXCut(blocks);

        var hasValidYCut = yCut.Gap >= MinGapThreshold;
        var hasValidXCut = xCut.Gap >= MinGapThreshold;
        if (hasValidYCut && hasValidXCut)
        {
            if (yCut.Gap > xCut.Gap)
            {
                SplitYOrFallback(blocks, yCut.Position, result);
            }
            else
            {
                SplitXOrFallback(blocks, xCut.Position, result);
            }
        }
        else if (hasValidYCut)
        {
            SplitYOrFallback(blocks, yCut.Position, result);
        }
        else if (hasValidXCut)
        {
            SplitXOrFallback(blocks, xCut.Position, result);
        }
        else
        {
            AddFallbackOrder(blocks, result);
        }
    }

    private void SplitYOrFallback(IReadOnlyList<TextBlock> blocks, double cutY, List<TextBlock> result)
    {
        var (top, bottom) = PartitionByY(blocks, cutY);
        if (top.Count == 0 || bottom.Count == 0)
        {
            AddFallbackOrder(blocks, result);
            return;
        }

        RecursiveCut(top, result);
        RecursiveCut(bottom, result);
    }

    private void SplitXOrFallback(IReadOnlyList<TextBlock> blocks, double cutX, List<TextBlock> result)
    {
        var (left, right) = PartitionByX(blocks, cutX);
        if (left.Count == 0 || right.Count == 0)
        {
            AddFallbackOrder(blocks, result);
            return;
        }

        RecursiveCut(left, result);
        RecursiveCut(right, result);
    }

    private static void AddFallbackOrder(IReadOnlyList<TextBlock> blocks, List<TextBlock> result)
    {
        result.AddRange(blocks
            .OrderByDescending(b => b.BoundingBox.Top)
            .ThenBy(b => b.BoundingBox.Left));
    }

    private static CutResult FindBestYCut(IReadOnlyList<TextBlock> blocks)
    {
        var sorted = blocks
            .OrderByDescending(b => b.BoundingBox.Top)
            .ThenByDescending(b => b.BoundingBox.Bottom)
            .ToList();

        var largestGap = 0.0;
        var cutPosition = 0.0;
        double? previousBottom = null;
        foreach (var block in sorted)
        {
            var top = block.BoundingBox.Top;
            var bottom = block.BoundingBox.Bottom;

            if (previousBottom is not null && previousBottom.Value > top)
            {
                var gap = previousBottom.Value - top;
                if (gap > largestGap)
                {
                    largestGap = gap;
                    cutPosition = (previousBottom.Value + top) / 2.0;
                }
            }

            previousBottom = previousBottom is null
                ? bottom
                : Math.Min(previousBottom.Value, bottom);
        }

        return new CutResult(cutPosition, largestGap);
    }

    private static CutResult FindBestXCut(IReadOnlyList<TextBlock> blocks)
    {
        var edgeCut = FindVerticalCutByEdges(blocks);
        if (edgeCut.Gap >= MinGapThreshold)
            return edgeCut;

        if (blocks.Count < 3)
            return edgeCut;

        var region = ComputeBounds(blocks);
        var narrowThreshold = region.Width * NarrowElementWidthRatio;
        var filtered = blocks
            .Where(b => b.BoundingBox.Width >= narrowThreshold)
            .ToList();

        if (filtered.Count < 2 || filtered.Count == blocks.Count)
            return edgeCut;

        var filteredCut = FindVerticalCutByEdges(filtered);
        return filteredCut.Gap > edgeCut.Gap && filteredCut.Gap >= MinGapThreshold
            ? filteredCut
            : edgeCut;
    }

    private static CutResult FindVerticalCutByEdges(IReadOnlyList<TextBlock> blocks)
    {
        var sorted = blocks
            .OrderBy(b => b.BoundingBox.Left)
            .ThenBy(b => b.BoundingBox.Right)
            .ToList();

        var largestGap = 0.0;
        var cutPosition = 0.0;
        double? previousRight = null;
        foreach (var block in sorted)
        {
            var left = block.BoundingBox.Left;
            var right = block.BoundingBox.Right;

            if (previousRight is not null && left > previousRight.Value)
            {
                var gap = left - previousRight.Value;
                if (gap > largestGap)
                {
                    largestGap = gap;
                    cutPosition = (previousRight.Value + left) / 2.0;
                }
            }

            previousRight = previousRight is null
                ? right
                : Math.Max(previousRight.Value, right);
        }

        return new CutResult(cutPosition, largestGap);
    }

    private static (List<TextBlock>, List<TextBlock>) PartitionByY(IReadOnlyList<TextBlock> blocks, double cutY)
    {
        var top = new List<TextBlock>();
        var bottom = new List<TextBlock>();
        foreach (var b in blocks)
            (b.BoundingBox.CenterY > cutY ? top : bottom).Add(b);
        return (top, bottom);
    }

    private static (List<TextBlock>, List<TextBlock>) PartitionByX(IReadOnlyList<TextBlock> blocks, double cutX)
    {
        var left = new List<TextBlock>();
        var right = new List<TextBlock>();
        foreach (var b in blocks)
            (b.BoundingBox.CenterX < cutX ? left : right).Add(b);
        return (left, right);
    }

    private static BoundingBox ComputeBounds(IReadOnlyList<TextBlock> blocks)
    {
        var left = double.MaxValue;
        var bottom = double.MaxValue;
        var right = double.MinValue;
        var top = double.MinValue;

        foreach (var b in blocks)
        {
            left = Math.Min(left, b.BoundingBox.Left);
            bottom = Math.Min(bottom, b.BoundingBox.Bottom);
            right = Math.Max(right, b.BoundingBox.Right);
            top = Math.Max(top, b.BoundingBox.Top);
        }
        return new BoundingBox(left, bottom, right, top);
    }

    private readonly record struct CutResult(double Position, double Gap);
}
