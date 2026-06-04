// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Models;
using BaselineRow = PdfStruct.Analysis.Tables.TableRowGrouping.BaselineRow;
using Word = PdfStruct.Analysis.Tables.TableCellRecovery.Word;

namespace PdfStruct.Analysis.Tables;

/// <summary>
/// Groups a table region's words into <em>logical rows</em> — the rows a reader
/// sees — rather than raw baseline rows. A logical row may span several text
/// baselines: a header cell or a data cell that wraps to a second line is one
/// row, not two. Resolving this before column assignment is what keeps a wrapped
/// header or cell from fragmenting the grid.
/// </summary>
/// <remarks>
/// Three sources of row structure, in order of trust:
/// <list type="number">
/// <item>Drawn horizontal rules. When full-width rules partition the region into
/// several regular bands, each band is one logical row — the rules are the
/// row boundaries, however many text lines a cell wraps to.</item>
/// <item>Baseline rows. Absent a per-row rule grid (the booktabs and borderless
/// case), words are first clustered by shared baseline.</item>
/// <item>Continuation merge. A baseline row that carries no row-label in the
/// anchor column and whose words all fall under cells already opened by the row
/// above is a wrapped continuation, merged upward. A new record — which opens a
/// fresh label — or a finer header level — which introduces new columns — is
/// kept separate, so data records and multi-level headers are preserved.</item>
/// </list>
/// </remarks>
internal static class LogicalRowBuilder
{
    /// <summary>Two word centres within this factor of the word height are the same column.</summary>
    private const double ColumnToleranceFactor = 0.6;

    /// <summary>Absolute floor, in PDF points, for the column-centre tolerance.</summary>
    private const double MinColumnTolerance = 1.0;

    /// <summary>A column is stable, and so part of the schema, when its centre recurs in at least this many baseline rows.</summary>
    private const int MinRowsForStableColumn = 2;

    /// <summary>Horizontal rules whose centres are within this many points are the one boundary.</summary>
    private const double RuleLevelTolerance = 2.5;

    /// <summary>A per-row rule grid needs at least this many interior bands; fewer is a booktabs header/body split, not a row grid.</summary>
    private const int MinRuleBands = 3;

    /// <summary>Interior band heights must be this regular (coefficient of variation at most) to read as a per-row grid.</summary>
    private const double MaxBandHeightCv = 0.5;

    /// <summary>A baseline gap wider than this factor of the median gap is a record boundary that a continuation never crosses.</summary>
    private const double RecordGapFactor = 1.6;

    /// <summary>
    /// Builds the region's logical rows, top to bottom.
    /// </summary>
    /// <param name="words">The words inside the region, in any order.</param>
    /// <param name="region">The region's bounding box.</param>
    /// <param name="horizontalRuleYs">The y-centres of full-width horizontal rules inside the region.</param>
    /// <returns>The logical rows, each a list of words, top to bottom.</returns>
    public static List<List<Word>> Build(
        IReadOnlyList<Word> words, BoundingBox region, IReadOnlyList<double> horizontalRuleYs,
        bool trustDrawnGrid = false)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(horizontalRuleYs);

        var baselineRows = TableRowGrouping.ClusterBaselineRows(words);
        if (baselineRows.Count <= 1) return baselineRows.Select(r => r.Words.ToList()).ToList();

        if (TryRuleBands(words, region, horizontalRuleYs, baselineRows.Count, trustDrawnGrid, out var bandRows))
            return bandRows;

        return MergeContinuations(baselineRows);
    }

    /// <summary>
    /// Buckets words into the bands a regular full-width rule grid draws. Returns
    /// <c>false</c> when the rules are not a per-row grid (too few bands, irregular
    /// heights, or no fewer rows than baseline grouping already found), so the
    /// booktabs and borderless cases fall through to baseline rows. A trusted drawn
    /// grid — one whose columns are ruled full height — lowers the band-count floor:
    /// its horizontal rules <em>are</em> the row boundaries, so a small ruled cell
    /// spanning several text baselines (a stacked footer cell) is one row, not one
    /// row per baseline.
    /// </summary>
    private static bool TryRuleBands(
        IReadOnlyList<Word> words, BoundingBox region, IReadOnlyList<double> horizontalRuleYs,
        int baselineRowCount, bool trustDrawnGrid, out List<List<Word>> bandRows)
    {
        bandRows = [];

        var boundaries = new List<double>();
        foreach (var y in horizontalRuleYs.Where(y => y <= region.Top && y >= region.Bottom).OrderByDescending(y => y))
            if (boundaries.Count == 0 || boundaries[^1] - y > RuleLevelTolerance) boundaries.Add(y);

        var interiorBandCount = boundaries.Count - 1;
        if (interiorBandCount < (trustDrawnGrid ? 1 : MinRuleBands)) return false;

        var buckets = new List<List<Word>>();
        for (var i = 0; i <= boundaries.Count; i++) buckets.Add([]);
        foreach (var word in words)
        {
            var centerY = word.BoundingBox.CenterY;
            var index = 0;
            while (index < boundaries.Count && centerY < boundaries[index]) index++;
            buckets[index].Add(word);
        }

        var interior = buckets.GetRange(1, interiorBandCount);
        if (interior.Any(band => band.Count == 0)) return false;

        var heights = new List<double>();
        for (var i = 1; i < boundaries.Count; i++) heights.Add(boundaries[i - 1] - boundaries[i]);
        if (CoefficientOfVariation(heights) > MaxBandHeightCv) return false;

        bandRows = SplitRuleBands(buckets.Where(band => band.Count > 0).ToList());
        if (bandRows.Count >= baselineRowCount) return false;

        return bandRows.Count > 0;
    }

    /// <summary>
    /// A rule band is usually one logical row, but dense ruled tables sometimes
    /// omit the interior rule between consecutive body records. Split only when
    /// multiple baselines in the same band each open the anchor column and at
    /// least one data column; label-only baselines remain wrapped cell text.
    /// A band that carries no short value cells is a single multi-line text cell —
    /// a wrapped label and its prose description, where the second line also opens
    /// the left column — and is never split, so a two-line entry stays one row.
    /// </summary>
    private static List<List<Word>> SplitRuleBands(List<List<Word>> bands) =>
        TableRowGrouping.SplitStackedRecords(bands, band =>
        {
            if (!band.Any(w => TableRowGrouping.LooksLikeShortValue(w.Text))) return _ => false;

            var tolerance = Math.Max(MinColumnTolerance, ColumnToleranceFactor * Median(band.Select(w => w.BoundingBox.Height)));
            var anchor = band.Min(w => w.BoundingBox.CenterX);
            return words =>
                words.Any(w => w.BoundingBox.CenterX <= anchor + tolerance)
                && words.Any(w => w.BoundingBox.CenterX > anchor + Math.Max(3 * tolerance, 12.0));
        });

    /// <summary>Merges wrapped continuation rows into the row above, leaving new records and finer header levels as their own rows.</summary>
    private static List<List<Word>> MergeContinuations(List<BaselineRow> baselineRows)
    {
        var tolerance = Math.Max(MinColumnTolerance, ColumnToleranceFactor * Median(baselineRows.SelectMany(r => r.Words).Select(w => w.BoundingBox.Height)));
        var stable = StableColumnCenters(baselineRows, tolerance);
        if (stable.Count == 0) return baselineRows.Select(r => r.Words.ToList()).ToList();

        var anchor = stable[0];
        var medianGap = MedianBaselineGap(baselineRows);

        var groups = new List<List<BaselineRow>>();
        foreach (var row in baselineRows)
        {
            if (groups.Count == 0) { groups.Add([row]); continue; }

            var group = groups[^1];
            var gap = group[^1].Baseline - row.Baseline;
            if (gap <= RecordGapFactor * medianGap && IsContinuation(row, group, anchor, tolerance))
                group.Add(row);
            else
                groups.Add([row]);
        }

        return groups.Select(g => g.SelectMany(r => r.Words).ToList()).ToList();
    }

    /// <summary>
    /// True when a row is a wrapped continuation of the group above: it opens no
    /// row-label in the anchor column, and each of its words sits under a column
    /// the group has already opened. A new record (carrying a label at the anchor)
    /// or a finer header level (introducing columns the group lacks) fails both
    /// tests and stays a separate row.
    /// </summary>
    private static bool IsContinuation(BaselineRow row, List<BaselineRow> group, double anchor, double tolerance)
    {
        if (row.Words.Any(w => w.BoundingBox.CenterX <= anchor + tolerance)) return false;

        var groupCenters = group.SelectMany(r => r.Words).Select(w => w.BoundingBox.CenterX).ToList();
        return row.Words.All(w => groupCenters.Any(c => Math.Abs(c - w.BoundingBox.CenterX) <= tolerance));
    }

    /// <summary>Returns the centres of word columns that recur across baseline rows, left to right.</summary>
    private static List<double> StableColumnCenters(List<BaselineRow> rows, double tolerance)
    {
        var all = new List<(double Center, int Row)>();
        for (var r = 0; r < rows.Count; r++)
            foreach (var word in rows[r].Words)
                all.Add((word.BoundingBox.CenterX, r));
        all.Sort((a, b) => a.Center.CompareTo(b.Center));
        if (all.Count == 0) return [];

        var stable = new List<double>();
        var clusterCenters = new List<double>();
        var clusterRows = new HashSet<int>();
        var clusterStart = all[0].Center;

        void Flush()
        {
            if (clusterRows.Count >= MinRowsForStableColumn) stable.Add(clusterCenters.Average());
        }

        foreach (var (center, row) in all)
        {
            if (center - clusterStart > tolerance)
            {
                Flush();
                clusterCenters = [];
                clusterRows = [];
                clusterStart = center;
            }
            clusterCenters.Add(center);
            clusterRows.Add(row);
        }
        Flush();
        return stable;
    }

    private static double MedianBaselineGap(List<BaselineRow> rows)
    {
        var gaps = new List<double>();
        for (var i = 1; i < rows.Count; i++) gaps.Add(rows[i - 1].Baseline - rows[i].Baseline);
        return Median(gaps);
    }

    private static double CoefficientOfVariation(List<double> values)
    {
        if (values.Count == 0) return double.MaxValue;
        var mean = values.Average();
        if (mean <= 0) return double.MaxValue;
        var variance = values.Average(v => (v - mean) * (v - mean));
        return Math.Sqrt(variance) / mean;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Where(v => v > 0).OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0.0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
