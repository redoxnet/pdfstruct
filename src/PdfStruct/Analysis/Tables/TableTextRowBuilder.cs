// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Models;

namespace PdfStruct.Analysis.Tables;

/// <summary>The raw text rows a region serialised to, and the y-positions of the boundaries between them.</summary>
/// <param name="Rows">One string per row, top to bottom.</param>
/// <param name="Boundaries">The y-positions, in PDF user space, of the cuts between consecutive rows.</param>
internal readonly record struct TableRows(IReadOnlyList<string> Rows, IReadOnlyList<double> Boundaries);

/// <summary>
/// Serialises the text lines a table region claimed into raw text rows. The
/// default is baseline grouping — one row per baseline. A row per baseline is
/// wrong only for a <em>per-row ruled grid</em> whose cells wrap to several
/// lines (each logical row spans several baselines): there the horizontal rules,
/// not the baselines, are the row boundaries. Such a grid is used only when its
/// structure is confirmed — repeated row bands, a repeated column schema, and a
/// multi-line cell to gain from. A booktabs table (top/mid/bottom rules only)
/// and a key-value form or front matter (rules are section separators, no
/// repeated row schema) keep baseline grouping.
/// </summary>
internal static class TableTextRowBuilder
{
    /// <summary>A rule must span at least this share of the region width to be a row boundary, not an incidental short segment.</summary>
    private const double RuleWidthShare = 0.5;

    /// <summary>Rules whose centres are within this many points are the one boundary, split into segments or doubled.</summary>
    private const double RuleLevelTolerance = 2.5;

    /// <summary>A per-row grid needs at least this many interior bands (rules bracketing rows); two bands is the booktabs header/body split, not a grid.</summary>
    private const int MinInteriorBands = 3;

    /// <summary>Interior band heights must be this regular (coefficient of variation at most) — a per-row grid steps evenly; a form has one tall body band among thin separators.</summary>
    private const double MaxBandHeightCv = 0.5;

    /// <summary>A column anchor is stable when this share of interior bands carry a cell at it; two stable anchors are a repeated column schema.</summary>
    private const double AnchorStabilityShare = 0.6;

    /// <summary>A cell joins a column anchor within this factor of the font size.</summary>
    private const double ColumnToleranceFactor = 0.33;

    /// <summary>Same-row baseline tolerance as a factor of font size, floored by an absolute minimum.</summary>
    private const double RowBaselineToleranceFactor = 0.4;

    /// <summary>Absolute floor, in points, for the same-row baseline tolerance.</summary>
    private const double MinRowBaselineTolerance = 1.5;

    /// <summary>
    /// Builds the region's raw text rows, top to bottom.
    /// </summary>
    /// <param name="lines">The lines the region claimed.</param>
    /// <param name="region">The table region's bounding box.</param>
    /// <param name="horizontalRules">The page's horizontal rules.</param>
    /// <param name="verticalRules">The page's vertical rules.</param>
    /// <returns>The raw text rows and the y-positions of the boundaries between them.</returns>
    public static TableRows Build(
        IReadOnlyList<TextLineBlock> lines,
        BoundingBox region,
        IReadOnlyList<BoundingBox> horizontalRules,
        IReadOnlyList<BoundingBox> verticalRules)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(horizontalRules);
        ArgumentNullException.ThrowIfNull(verticalRules);
        if (lines.Count == 0) return new TableRows([], []);

        var baselineRows = GroupBaselineRows(lines);
        if (TryRuleBandRows(lines, baselineRows.Count, region, horizontalRules, verticalRules, out var bandRows, out var bandBoundaries))
            return new TableRows(bandRows, bandBoundaries);

        var rows = baselineRows.Select(JoinReadingOrder).Where(row => row.Length > 0).ToList();
        return new TableRows(rows, BaselineBoundaries(baselineRows));
    }

    /// <summary>Returns the y-position of each cut between consecutive baseline rows — the midpoint of the gap.</summary>
    private static List<double> BaselineBoundaries(List<List<TextLineBlock>> baselineRows)
    {
        var baselines = baselineRows
            .Where(row => row.Count > 0)
            .Select(row => row.Average(c => c.BaselineY))
            .ToList();

        var boundaries = new List<double>();
        for (var i = 1; i < baselines.Count; i++) boundaries.Add((baselines[i - 1] + baselines[i]) / 2.0);
        return boundaries;
    }

    /// <summary>
    /// Segments rows by horizontal-rule bands when the region is a confirmed
    /// per-row ruled grid, returning <c>false</c> to fall back to baseline rows
    /// otherwise.
    /// </summary>
    private static bool TryRuleBandRows(
        IReadOnlyList<TextLineBlock> lines,
        int baselineRowCount,
        BoundingBox region,
        IReadOnlyList<BoundingBox> horizontalRules,
        IReadOnlyList<BoundingBox> verticalRules,
        out List<string> rows,
        out List<double> boundaries)
    {
        rows = [];
        boundaries = [];

        var ruleBoundaries = RuleRowBoundaries(horizontalRules, region);
        var interiorBandCount = ruleBoundaries.Count - 1;
        if (interiorBandCount < MinInteriorBands) return false;

        // Bucket every line: index = number of boundaries above its baseline, so
        // bucket 0 is above the first rule, buckets 1..n-1 are between rules, and
        // bucket n is below the last. No line is dropped.
        var buckets = new List<List<TextLineBlock>>();
        for (var i = 0; i <= ruleBoundaries.Count; i++) buckets.Add([]);
        foreach (var line in lines)
        {
            var index = 0;
            while (index < ruleBoundaries.Count && line.BaselineY < ruleBoundaries[index]) index++;
            buckets[index].Add(line);
        }

        var interior = buckets.GetRange(1, interiorBandCount);
        if (interior.Any(band => band.Count == 0)) return false;

        var heights = new List<double>();
        for (var i = 1; i < ruleBoundaries.Count; i++) heights.Add(ruleBoundaries[i - 1] - ruleBoundaries[i]);
        if (CoefficientOfVariation(heights) > MaxBandHeightCv) return false;

        var ruleRowCount = buckets.Count(band => band.Count > 0);
        if (ruleRowCount >= baselineRowCount) return false;

        if (!HasRepeatedColumnSchema(interior, region, verticalRules)) return false;

        rows = buckets
            .Where(band => band.Count > 0)
            .Select(JoinReadingOrder)
            .Where(row => row.Length > 0)
            .ToList();
        boundaries = ruleBoundaries;
        return rows.Count > 0;
    }

    /// <summary>
    /// True when the interior bands share a repeated column schema: at least two
    /// column anchors each carry a cell in a majority of bands, or a vertical
    /// rule spans most of the region's height as a column separator. A key-value
    /// form has neither — its rules separate sections, not repeating rows.
    /// </summary>
    private static bool HasRepeatedColumnSchema(
        List<List<TextLineBlock>> interiorBands, BoundingBox region, IReadOnlyList<BoundingBox> verticalRules)
    {
        var spanningSeparators = verticalRules.Count(v =>
            v.Left >= region.Left && v.Right <= region.Right && v.Height >= 0.5 * region.Height);
        if (spanningSeparators >= 1) return true;

        var fontSize = Median(interiorBands.SelectMany(b => b).Select(c => c.FontSize));
        if (fontSize <= 0) return false;
        var tolerance = ColumnToleranceFactor * fontSize;

        var anchors = ClusterAnchors(interiorBands.SelectMany(b => b).Select(c => c.Left), tolerance);
        if (anchors.Count < 2) return false;

        var stable = 0;
        foreach (var anchor in anchors)
        {
            var bandsAtAnchor = interiorBands.Count(band =>
                band.Any(cell => Math.Abs(cell.Left - anchor) <= tolerance));
            if (bandsAtAnchor >= AnchorStabilityShare * interiorBands.Count) stable++;
        }
        return stable >= 2;
    }

    /// <summary>Returns the rule row boundaries within the region, top to bottom: distinct y-levels whose merged segments span most of the region width.</summary>
    private static List<double> RuleRowBoundaries(IReadOnlyList<BoundingBox> horizontalRules, BoundingBox region)
    {
        var inside = horizontalRules
            .Where(r => RuleCenterY(r) <= region.Top && RuleCenterY(r) >= region.Bottom
                        && r.Left < region.Right && r.Right > region.Left)
            .OrderByDescending(RuleCenterY)
            .ToList();

        var levels = new List<(double Y, double Left, double Right)>();
        foreach (var rule in inside)
        {
            var y = RuleCenterY(rule);
            if (levels.Count > 0 && levels[^1].Y - y <= RuleLevelTolerance)
            {
                var last = levels[^1];
                levels[^1] = (last.Y, Math.Min(last.Left, rule.Left), Math.Max(last.Right, rule.Right));
            }
            else
            {
                levels.Add((y, rule.Left, rule.Right));
            }
        }

        return levels
            .Where(l => l.Right - l.Left >= RuleWidthShare * region.Width)
            .Select(l => l.Y)
            .ToList();
    }

    /// <summary>Groups lines into baseline rows, top to bottom.</summary>
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

    /// <summary>Joins a row's cells in reading order — left column first, then top to bottom within a column — into one space-separated string.</summary>
    private static string JoinReadingOrder(List<TextLineBlock> cells) =>
        string.Join(" ", cells
            .OrderBy(c => c.Left)
            .ThenByDescending(c => c.BaselineY)
            .Select(c => c.Text.Trim()))
            .Trim();

    /// <summary>Greedily clusters left edges into ascending column anchors.</summary>
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

    private static double CoefficientOfVariation(List<double> values)
    {
        if (values.Count == 0) return double.MaxValue;
        var mean = values.Average();
        if (mean <= 0) return double.MaxValue;
        var variance = values.Average(v => (v - mean) * (v - mean));
        return Math.Sqrt(variance) / mean;
    }

    private static double RuleCenterY(BoundingBox rule) => (rule.Top + rule.Bottom) / 2.0;

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Where(v => v > 0).OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0.0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
