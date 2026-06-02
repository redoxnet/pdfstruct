// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Models;

namespace PdfStruct.Analysis.Tables;

/// <summary>What internal structure a detected region can defensibly assert.</summary>
internal enum RegionStructure
{
    /// <summary>A row-major data table: columns repeat the same schema across rows.</summary>
    Grid,

    /// <summary>A bordered region without a repeated column schema — a key-value form or front matter — kept as raw text, not a table.</summary>
    Block,
}

/// <summary>The structure a region can defend, plus the column x-positions backing a grid decision (empty for a block).</summary>
/// <param name="Kind">The defensible structure.</param>
/// <param name="Columns">For a grid, the confident column x-positions in PDF user space; empty otherwise.</param>
internal readonly record struct RegionClassification(RegionStructure Kind, IReadOnlyList<double> Columns);

/// <summary>
/// Decides whether a detected region is a real data grid or only a bordered
/// block of text. A grid is asserted when an interior vertical rule draws a
/// column boundary, or — for a borderless table — when several column anchors
/// each carry a cell in a majority of rows. A key-value form (no interior
/// vertical rules, fields independent, no anchor repeats down the rows) is a
/// block, so it is never reported as a table it is not.
/// </summary>
internal static class StructuredRegionClassifier
{
    /// <summary>A cell joins a column anchor within this factor of the font size.</summary>
    private const double ColumnToleranceFactor = 0.33;

    /// <summary>A column anchor is stable when this share of rows carry a cell at it.</summary>
    private const double AnchorStabilityShare = 0.6;

    /// <summary>A grid needs at least this many stable column anchors.</summary>
    private const int MinStableColumns = 2;

    /// <summary>An interior vertical rule must sit at least this far inside the region edges to count as a column separator rather than a border.</summary>
    private const double InteriorMargin = 2.0;

    /// <summary>Same-row baseline tolerance as a factor of font size.</summary>
    private const double RowBaselineToleranceFactor = 0.4;

    /// <summary>Absolute floor, in points, for the same-row baseline tolerance.</summary>
    private const double MinRowBaselineTolerance = 1.5;

    /// <summary>
    /// Classifies a region from the lines it claimed and the page's vertical rules.
    /// </summary>
    /// <param name="lines">The lines inside the region.</param>
    /// <param name="region">The region's bounding box.</param>
    /// <param name="verticalRules">The page's vertical rules.</param>
    /// <param name="columnsAlreadyValidated">True when the region came from the borderless detector, which already proved a repeated multi-column schema; such a region is always a grid and only its columns are derived here.</param>
    /// <returns>The structure the region can defend and the columns backing a grid.</returns>
    public static RegionClassification Classify(
        IReadOnlyList<TextLineBlock> lines, BoundingBox region, IReadOnlyList<BoundingBox> verticalRules,
        bool columnsAlreadyValidated)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(verticalRules);

        var fontSize = Median(lines.Select(l => l.FontSize));
        var tolerance = ColumnToleranceFactor * (fontSize > 0 ? fontSize : 10.0);

        // An interior vertical rule is a drawn column boundary — strong grid
        // evidence even when the cell text never gap-split into column anchors.
        var ruleColumns = verticalRules
            .Where(v => v.Left > region.Left + InteriorMargin && v.Right < region.Right - InteriorMargin
                        && v.Bottom < region.Top && v.Top > region.Bottom)
            .Select(v => (v.Left + v.Right) / 2.0);
        var ruleAnchors = ClusterAnchors(ruleColumns, Math.Max(InteriorMargin, tolerance));
        if (ruleAnchors.Count >= 1) return new RegionClassification(RegionStructure.Grid, ruleAnchors);

        var rows = GroupBaselineRows(lines);
        var anchors = fontSize > 0
            ? ClusterAnchors(lines.Select(l => l.Left), tolerance)
            : [];
        var stable = anchors
            .Where(anchor => rows.Count(row => row.Any(cell => Math.Abs(cell.Left - anchor) <= tolerance))
                >= AnchorStabilityShare * rows.Count)
            .ToList();

        // The borderless detector only fires once it has found a repeated multi-
        // column schema, so trust it as a grid; a header band added above the body
        // can otherwise dilute the anchor stability and look like a block.
        if (columnsAlreadyValidated)
            return new RegionClassification(RegionStructure.Grid, stable);

        if (rows.Count < 2 || fontSize <= 0) return new RegionClassification(RegionStructure.Block, []);

        return stable.Count >= MinStableColumns
            ? new RegionClassification(RegionStructure.Grid, stable)
            : new RegionClassification(RegionStructure.Block, []);
    }

    private static List<List<TextLineBlock>> GroupBaselineRows(IReadOnlyList<TextLineBlock> lines)
    {
        var ordered = lines.OrderByDescending(l => l.BaselineY).ToList();
        var rows = new List<List<TextLineBlock>>();
        var current = new List<TextLineBlock>();
        var baseline = 0.0;
        foreach (var line in ordered)
        {
            if (current.Count == 0)
            {
                current.Add(line);
                baseline = line.BaselineY;
                continue;
            }

            var smaller = Math.Min(current.Min(c => c.FontSize), line.FontSize);
            var tolerance = Math.Max(MinRowBaselineTolerance, RowBaselineToleranceFactor * smaller);
            if (baseline - line.BaselineY <= tolerance)
            {
                current.Add(line);
            }
            else
            {
                rows.Add(current);
                current = [line];
                baseline = line.BaselineY;
            }
        }
        if (current.Count > 0) rows.Add(current);
        return rows;
    }

    private static List<double> ClusterAnchors(IEnumerable<double> lefts, double tolerance)
    {
        var anchors = new List<double>();
        var clusterStart = double.NaN;
        foreach (var left in lefts.OrderBy(x => x))
        {
            if (anchors.Count == 0 || left - clusterStart > tolerance)
            {
                clusterStart = left;
                anchors.Add(left);
            }
        }
        return anchors;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Where(v => v > 0).OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0.0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
