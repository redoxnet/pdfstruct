// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Models;

namespace PdfStruct.Analysis.Tables;

/// <summary>
/// Locates borderless table regions — those drawn with no ruling lines, the
/// dominant style in academic and booktabs documents — by reading repeated
/// column alignment out of the text-line stream. Emits the whole-table bounding
/// box only; cell structure is recovered later.
/// </summary>
/// <remarks>
/// The word-to-line grouper already slices a baseline-line wherever an outlier
/// inter-word gap or a vertical structural cut crosses it, so a gapped table
/// row such as <c>"Apple   3   Red"</c> arrives as several
/// <see cref="TextLineBlock"/>s sharing a baseline. The detector groups those
/// blocks back into rows, marks a row tabular when it has several short,
/// gap-separated cells, then bridges nearby tabular rows — absorbing the
/// occasional single-cell row between them — into one region. Bounding the
/// region by its first and last tabular row is what stops a real table from
/// fragmenting at every blank or section row.
///
/// <para>
/// The policy is precision-first and font-relative. Two adjacent body-text
/// columns are the dominant false positive; their "cells" are whole prose lines,
/// so a median-cell-length bound rejects them, and the caller supplies a single
/// column slab, which removes the multi-column-body ambiguity up front.
/// </para>
/// </remarks>
internal static class BorderlessTableDetector
{
    /// <summary>Two blocks belong to the same row when their baselines differ by at most this factor of the smaller font size (floored by an absolute minimum).</summary>
    public const double RowBaselineToleranceFactor = 0.4;

    /// <summary>Absolute floor, in PDF points, for the same-row baseline tolerance.</summary>
    public const double MinRowBaselineTolerance = 1.5;

    /// <summary>A cell joins a column when its left edge is within this factor of the font size of the column's anchor.</summary>
    public const double ColumnToleranceFactor = 0.33;

    /// <summary>Successive tabular rows up to this factor of the font size apart are bridged into one region, spanning any single-cell rows between them.</summary>
    public const double BridgeGapFactor = 3.5;

    /// <summary>
    /// A tabular row's median cell text length must not exceed this many
    /// characters. Table cells are terse data tokens; the dominant false
    /// positive — two adjacent body-text columns sharing baselines — has cells
    /// that are whole prose lines, well above this bound.
    /// </summary>
    public const int MaxMedianCellTextLength = 30;

    /// <summary>
    /// Minimum columns a tabular row must show. Held at three for precision: a
    /// two-column requirement also fires on tables of contents (title + page
    /// number) and on two adjacent prose columns, the two dominant false
    /// positives. Two-column borderless tables are deferred to a later
    /// recall-oriented pass.
    /// </summary>
    public const int MinColumns = 3;

    /// <summary>Minimum tabular rows a region must contain.</summary>
    public const int MinTabularRows = 2;

    /// <summary>
    /// Detects borderless table regions within a single column slab.
    /// </summary>
    /// <param name="lines">The slab's text lines, in any order; the detector sorts internally.</param>
    /// <returns>The detected table regions, top to bottom.</returns>
    public static IReadOnlyList<DetectedTable> Detect(IReadOnlyList<TextLineBlock> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count < MinTabularRows * MinColumns) return [];

        var rows = GroupIntoRows(lines);
        var tabular = new bool[rows.Count];
        for (var i = 0; i < rows.Count; i++)
            tabular[i] = IsTabularRow(rows[i]);

        var regions = new List<DetectedTable>();
        var i0 = 0;
        while (i0 < rows.Count)
        {
            if (!tabular[i0]) { i0++; continue; }

            // Extend the cluster while the next tabular row is within bridge range.
            var lastTabular = i0;
            var j = i0 + 1;
            while (j < rows.Count)
            {
                if (tabular[j])
                {
                    var gap = rows[lastTabular].Baseline - rows[j].Baseline;
                    if (gap > BridgeGapFactor * rows[j].FontSize) break;
                    lastTabular = j;
                }
                else if (rows[lastTabular].Baseline - rows[j].Baseline > BridgeGapFactor * rows[lastTabular].FontSize)
                {
                    break;
                }
                j++;
            }

            var tabularCount = 0;
            for (var k = i0; k <= lastTabular; k++) if (tabular[k]) tabularCount++;

            if (tabularCount >= MinTabularRows && TryRegionBounds(rows, i0, lastTabular, out var box))
                regions.Add(new DetectedTable(box, "borderless"));

            i0 = lastTabular + 1;
        }

        return regions;
    }

    /// <summary>Validates the cluster's columns and returns the region bounding box spanning its first to last tabular row.</summary>
    private static bool TryRegionBounds(List<Row> rows, int first, int last, out BoundingBox box)
    {
        box = default;

        var tabularCells = new List<(double Left, double FontSize)>();
        var lengths = new List<double>();
        var leaderRows = 0;
        for (var k = first; k <= last; k++)
        {
            if (rows[k].Cells.Count < MinColumns) continue;
            if (rows[k].Cells.Any(c => IsLeaderCell(c.Text))) leaderRows++;
            foreach (var cell in rows[k].Cells)
            {
                tabularCells.Add((cell.Left, cell.FontSize));
                lengths.Add(cell.Text.Length);
            }
        }

        // Dot-leader cells (".....") are a table-of-contents / index signature,
        // never a data cell — reject the region when several rows carry one.
        if (leaderRows >= 2) return false;

        var fontSize = Median(tabularCells.Select(c => c.FontSize));
        var anchors = ClusterAnchors(tabularCells.Select(c => c.Left), ColumnToleranceFactor * fontSize);
        if (anchors.Count < MinColumns) return false;
        if (Median(lengths) > MaxMedianCellTextLength) return false;

        // A monotonic integer column is a page-number / index column — the
        // table-of-contents signature — not a data column.
        if (HasMonotonicIntegerColumn(rows, first, last, anchors, ColumnToleranceFactor * fontSize)) return false;

        BoundingBox? bounds = null;
        for (var k = first; k <= last; k++)
            foreach (var cell in rows[k].Cells)
                bounds = bounds is null ? cell.BoundingBox : bounds.Value.Merge(cell.BoundingBox);

        if (bounds is null) return false;
        box = bounds.Value;
        return true;
    }

    /// <summary>True when some column reads as a run of integers that only ever increases or only ever decreases — a page-number column.</summary>
    private static bool HasMonotonicIntegerColumn(List<Row> rows, int first, int last, List<double> anchors, double tolerance)
    {
        var columns = new List<int?>[anchors.Count];
        for (var c = 0; c < anchors.Count; c++) columns[c] = [];

        for (var k = first; k <= last; k++)
        {
            if (rows[k].Cells.Count < MinColumns) continue;
            foreach (var cell in rows[k].Cells)
            {
                var col = NearestColumn(cell.Left, anchors, tolerance);
                if (col < 0) continue;
                columns[col].Add(int.TryParse(cell.Text.Trim(), out var value) ? value : null);
            }
        }

        foreach (var column in columns)
        {
            var values = column.Where(v => v.HasValue).Select(v => v!.Value).ToList();
            if (values.Count < MinTabularRows || values.Count != column.Count) continue;
            var increasing = true;
            var decreasing = true;
            for (var i = 1; i < values.Count; i++)
            {
                if (values[i] < values[i - 1]) increasing = false;
                if (values[i] > values[i - 1]) decreasing = false;
            }
            if (increasing || decreasing) return true;
        }
        return false;
    }

    /// <summary>Returns the index of the column anchor within tolerance of a left edge, or -1 when none fits.</summary>
    private static int NearestColumn(double left, List<double> anchors, double tolerance)
    {
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < anchors.Count; i++)
        {
            var distance = Math.Abs(left - anchors[i]);
            if (distance <= tolerance && distance < bestDistance) { best = i; bestDistance = distance; }
        }
        return best;
    }

    /// <summary>True when a cell is a dot leader (a run of dots/periods), the hallmark of a table-of-contents row.</summary>
    private static bool IsLeaderCell(string text)
    {
        var stripped = text.Replace(" ", "");
        if (stripped.Length < 2) return false;
        foreach (var c in stripped)
            if (c is not ('.' or '·' or '…' or '•' or '‥' or '⋯' or '∙')) return false;
        return true;
    }

    /// <summary>A row is tabular when it has several short, gap-separated cells.</summary>
    private static bool IsTabularRow(Row row)
    {
        if (row.Cells.Count < MinColumns) return false;
        var median = Median(row.Cells.Select(c => (double)c.Text.Length));
        return median <= MaxMedianCellTextLength;
    }

    /// <summary>Groups the slab's lines into rows by shared baseline, top to bottom.</summary>
    private static List<Row> GroupIntoRows(IReadOnlyList<TextLineBlock> lines)
    {
        var indexed = lines.OrderByDescending(l => l.BaselineY).ToList();
        var rows = new List<Row>();
        var current = new List<TextLineBlock>();
        var currentBaseline = 0.0;

        foreach (var line in indexed)
        {
            if (current.Count == 0)
            {
                current.Add(line);
                currentBaseline = line.BaselineY;
                continue;
            }

            var smallerFont = Math.Min(SmallestFont(current), line.FontSize);
            var tolerance = Math.Max(MinRowBaselineTolerance, RowBaselineToleranceFactor * smallerFont);
            if (currentBaseline - line.BaselineY <= tolerance)
            {
                current.Add(line);
            }
            else
            {
                rows.Add(MakeRow(current));
                current = [line];
                currentBaseline = line.BaselineY;
            }
        }
        if (current.Count > 0) rows.Add(MakeRow(current));
        return rows;
    }

    /// <summary>Greedily clusters left edges into ascending column anchors; returns the anchor count.</summary>
    private static List<double> ClusterAnchors(IEnumerable<double> lefts, double tolerance)
    {
        var anchors = new List<double>();
        var clusterStart = double.NaN;
        foreach (var left in lefts.OrderBy(x => x))
        {
            if (anchors.Count == 0 && double.IsNaN(clusterStart))
            {
                clusterStart = left;
                anchors.Add(left);
            }
            else if (left - clusterStart > tolerance)
            {
                clusterStart = left;
                anchors.Add(left);
            }
        }
        return anchors;
    }

    private static Row MakeRow(List<TextLineBlock> cells)
    {
        var ordered = cells.OrderBy(c => c.Left).ToList();
        var baseline = ordered.Average(c => c.BaselineY);
        var fontSize = Median(ordered.Select(c => c.FontSize));
        return new Row(baseline, fontSize, ordered);
    }

    private static double SmallestFont(List<TextLineBlock> cells)
    {
        var smallest = double.MaxValue;
        foreach (var block in cells)
            if (block.FontSize > 0 && block.FontSize < smallest) smallest = block.FontSize;
        return smallest == double.MaxValue ? 0.0 : smallest;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Where(v => v > 0).OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0.0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>A baseline-grouped row of cell blocks, ordered left to right.</summary>
    private sealed record Row(double Baseline, double FontSize, IReadOnlyList<TextLineBlock> Cells);
}
