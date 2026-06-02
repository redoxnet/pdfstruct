// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.RegularExpressions;
using PdfStruct.Models;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics;

namespace PdfStruct.Analysis.Tables;

/// <summary>
/// Detects ruled table regions from a joint model of ruling lines and text: a
/// table is where horizontal rules bracket a band of text rows that show
/// repeated column structure, not merely where line segments happen to sit near
/// one another.
/// </summary>
/// <remarks>
/// A table's horizontal rules (booktabs top/mid/bottom, or thick/thin patent
/// rules) are separated by whole rows of text, so proximity clustering of the
/// lines alone never recovers the table. Instead the rules that share a
/// horizontal extent are grouped into a stack; the text rows the stack brackets
/// are the table payload — column headers included, since they sit inside the
/// outermost rules. The region is a structural enclosure: horizontal rules are
/// the row-group top and bottom, vertical rules and repeated text-column anchors
/// are the columns, and the bracketed text rows are the contents. Captions are
/// not payload — a caption line between two stacks is a barrier, and captions
/// are linked separately by the caption binder.
///
/// <para>
/// Validation is structural, so a boxed paragraph or a centred display equation
/// — ruled or not — is rejected for lacking repeated columns rather than by any
/// keyword. Thresholds are font- and extent-relative.
/// </para>
/// </remarks>
internal static class TextAwareRuledTableDetector
{
    /// <summary>A rule's short side is at most this many PDF points.</summary>
    public const double RuleMaxThickness = 2.5;

    /// <summary>A horizontal rule must be at least this many points long to bound a table.</summary>
    public const double MinHorizontalRuleLength = 24.0;

    /// <summary>A vertical rule must be at least this many points tall to count as a column separator.</summary>
    public const double MinVerticalRuleLength = 6.0;

    /// <summary>Two horizontal rules share a stack when their horizontal extents overlap by at least this share of the shorter.</summary>
    public const double StackOverlapShare = 0.5;

    /// <summary>A cell joins a column when its left edge is within this factor of the font size of the column's anchor.</summary>
    public const double ColumnToleranceFactor = 0.33;

    /// <summary>Two blocks belong to the same row when their baselines differ by at most this factor of the smaller font size.</summary>
    public const double RowBaselineToleranceFactor = 0.4;

    /// <summary>Absolute floor, in PDF points, for the same-row baseline tolerance.</summary>
    public const double MinRowBaselineTolerance = 1.5;

    /// <summary>Minimum text rows a ruled band must bracket to be a table.</summary>
    public const int MinPayloadRows = 2;

    /// <summary>Minimum columns (text anchors or vertical rules) a ruled table must show.</summary>
    public const int MinColumns = 2;

    /// <summary>At least this share of a run's rows must span multiple columns, unless interior vertical rules witness the grid; below it the run is a multi-column page layout, not a table.</summary>
    public const double MinMultiColumnRowShare = 0.5;

    private static readonly Regex CaptionPattern = new(
        @"^\s*(Table|Tab\.|Figure|Fig\.|표|그림|도)\s*\d",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Splits a page's non-clipping paths into horizontal and vertical ruling lines.</summary>
    /// <param name="page">The page to inspect.</param>
    /// <returns>The horizontal and vertical rule boxes, clamped to the crop box.</returns>
    public static (IReadOnlyList<BoundingBox> Horizontal, IReadOnlyList<BoundingBox> Vertical) ExtractRules(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var pageBounds = ToBoundingBox(page.CropBox.Bounds);

        var horizontal = new List<BoundingBox>();
        var vertical = new List<BoundingBox>();
        foreach (var path in page.Paths)
        {
            if (path.IsClipping) continue;
            var raw = path.GetBoundingRectangle();
            if (!raw.HasValue) continue;
            if (!TryClampRule(ToBoundingBox(raw.Value), pageBounds, out var box)) continue;

            if (box.Height <= RuleMaxThickness && box.Width >= MinHorizontalRuleLength)
                horizontal.Add(box);
            else if (box.Width <= RuleMaxThickness && box.Height >= MinVerticalRuleLength)
                vertical.Add(box);
        }
        return (horizontal, vertical);
    }

    /// <summary>
    /// Detects ruled table regions on a page from its rules and text lines.
    /// </summary>
    /// <param name="lines">All text lines on the page.</param>
    /// <param name="horizontalRules">Horizontal ruling lines from <see cref="ExtractRules"/>.</param>
    /// <param name="verticalRules">Vertical ruling lines from <see cref="ExtractRules"/>.</param>
    /// <returns>The ruled table regions.</returns>
    public static IReadOnlyList<DetectedTable> Detect(
        IReadOnlyList<TextLineBlock> lines,
        IReadOnlyList<BoundingBox> horizontalRules,
        IReadOnlyList<BoundingBox> verticalRules)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(horizontalRules);
        ArgumentNullException.ThrowIfNull(verticalRules);
        if (horizontalRules.Count < 2) return [];

        var rows = GroupIntoRows(lines);
        var regions = new List<DetectedTable>();

        foreach (var stack in BuildRuleStacks(horizontalRules))
            foreach (var segment in SplitAtCaptionBarriers(stack, rows))
                regions.AddRange(ConvertSegmentToRegions(segment, rows, verticalRules));

        return regions;
    }

    /// <summary>Groups horizontal rules that share a horizontal extent into vertically ordered stacks.</summary>
    private static List<List<BoundingBox>> BuildRuleStacks(IReadOnlyList<BoundingBox> horizontalRules)
    {
        var sorted = horizontalRules.OrderByDescending(r => r.Top).ToList();
        var stacks = new List<List<BoundingBox>>();
        foreach (var rule in sorted)
        {
            var stack = stacks.FirstOrDefault(s => XOverlapShare(s[^1], rule) >= StackOverlapShare);
            if (stack is null) stacks.Add([rule]);
            else stack.Add(rule);
        }
        return stacks.Where(s => s.Count >= 2).ToList();
    }

    /// <summary>Splits a rule stack into segments wherever a caption line falls between two of its rules — a caption separates two tables.</summary>
    private static IEnumerable<List<BoundingBox>> SplitAtCaptionBarriers(List<BoundingBox> stack, List<Row> rows)
    {
        var segment = new List<BoundingBox> { stack[0] };
        for (var i = 1; i < stack.Count; i++)
        {
            var upper = stack[i - 1];
            var lower = stack[i];
            var captionBetween = rows.Any(r =>
                r.Baseline < upper.Bottom && r.Baseline > lower.Top && IsCaptionRow(r));
            if (captionBetween)
            {
                if (segment.Count >= 2) yield return segment;
                segment = [lower];
            }
            else
            {
                segment.Add(lower);
            }
        }
        if (segment.Count >= 2) yield return segment;
    }

    /// <summary>
    /// Walks the consecutive rule gaps of a width-matched stack and emits a
    /// region for each maximal run of gaps that bracket multi-column text rows.
    /// A gap with no multi-column rows — between a header rule and a title, or
    /// around a footer rule — is not part of a table and breaks the run, so page
    /// furniture rules that merely share the table's width are excluded.
    /// </summary>
    private static IEnumerable<DetectedTable> ConvertSegmentToRegions(
        List<BoundingBox> segment, List<Row> rows, IReadOnlyList<BoundingBox> verticalRules)
    {
        var rules = segment.OrderByDescending(r => r.Top).ToList();
        var top = rules.Max(r => r.Top);
        var bottom = rules.Min(r => r.Bottom);
        var left = rules.Min(r => r.Left);
        var right = rules.Max(r => r.Right);

        var payload = rows
            .Where(r => r.Baseline <= top && r.Baseline >= bottom && HorizontallyInside(r, left, right) && !IsCaptionRow(r))
            .ToList();
        if (payload.Count < MinPayloadRows) yield break;

        var fontSize = Median(payload.SelectMany(r => r.Cells).Select(c => c.FontSize));
        if (fontSize <= 0) yield break;
        var tolerance = ColumnToleranceFactor * fontSize;
        var anchors = ClusterAnchors(payload.SelectMany(r => r.Cells).Select(c => c.Left), tolerance);
        if (anchors.Count < MinColumns) yield break;

        // A gap between two consecutive rules is tabular when the text rows it
        // brackets carry cells in two or more columns.
        var gapTabular = new bool[rules.Count - 1];
        for (var i = 0; i < rules.Count - 1; i++)
        {
            var gapTop = rules[i].Bottom;
            var gapBottom = rules[i + 1].Top;
            var gapRows = payload.Where(r => r.Baseline <= gapTop && r.Baseline >= gapBottom);
            var verticalSeparator = verticalRules.Any(v =>
                v.Bottom < gapTop && v.Top > gapBottom && v.Left >= left - tolerance && v.Right <= right + tolerance);
            gapTabular[i] = CountMultiColumnRows(gapRows, anchors, tolerance) >= 1 || verticalSeparator;
        }

        var start = 0;
        while (start < gapTabular.Length)
        {
            if (!gapTabular[start]) { start++; continue; }
            var end = start;
            while (end + 1 < gapTabular.Length && gapTabular[end + 1]) end++;

            var runTop = rules[start].Top;
            var runBottom = rules[end + 1].Bottom;
            var runRows = payload.Where(r => r.Baseline <= runTop && r.Baseline >= runBottom).ToList();

            // A real table has most rows spanning multiple columns; a two-column
            // page layout (sidebar plus body, or parallel text columns) bracketed
            // by header/footer rules yields only sparse, incidental multi-column
            // rows. Interior vertical rules are an independent grid witness that
            // exempts a ruled grid whose cell text did not gap-split.
            var multiColumnRows = CountMultiColumnRows(runRows, anchors, tolerance);
            var verticalSeparators = verticalRules.Count(v =>
                v.Bottom < runTop && v.Top > runBottom && v.Left >= left - tolerance && v.Right <= right + tolerance);
            if (multiColumnRows < runRows.Count * MinMultiColumnRowShare && verticalSeparators < 2)
            {
                start = end + 1;
                continue;
            }

            var headerTop = ExpandHeaderAbove(rows, runTop, left, right, anchors, tolerance);
            var box = new BoundingBox(left, runBottom, right, Math.Max(runTop, headerTop));
            foreach (var row in runRows)
                foreach (var cell in row.Cells)
                    box = box.Merge(cell.BoundingBox);

            yield return new DetectedTable(box, "ruled");
            start = end + 1;
        }
    }

    /// <summary>Counts rows whose cells fall into two or more distinct columns.</summary>
    private static int CountMultiColumnRows(IEnumerable<Row> rows, List<double> anchors, double tolerance)
    {
        var count = 0;
        foreach (var row in rows)
        {
            var columns = new HashSet<int>();
            foreach (var cell in row.Cells)
            {
                var col = NearestColumn(cell.Left, anchors, tolerance);
                if (col >= 0) columns.Add(col);
            }
            if (columns.Count >= 2) count++;
        }
        return count;
    }

    /// <summary>Extends the region top to absorb header rows above the top rule whose cells align to the body column anchors, stopping at a caption or a non-aligned row.</summary>
    private static double ExpandHeaderAbove(
        List<Row> rows, double top, double left, double right, List<double> anchors, double tolerance)
    {
        if (anchors.Count < MinColumns) return top;

        var above = rows
            .Where(r => r.Baseline > top && HorizontallyInside(r, left, right))
            .OrderBy(r => r.Baseline)
            .ToList();

        var headerTop = top;
        foreach (var row in above)
        {
            if (IsCaptionRow(row)) break;
            var aligned = row.Cells.Count(c => NearestColumn(c.Left, anchors, tolerance) >= 0);
            if (aligned < MinColumns) break;
            headerTop = Math.Max(headerTop, row.Cells.Max(c => c.BoundingBox.Top));
        }
        return headerTop;
    }

    private static bool IsCaptionRow(Row row) => CaptionPattern.IsMatch(row.Text);

    private static bool HorizontallyInside(Row row, double left, double right)
    {
        var center = (row.Left + row.Right) / 2.0;
        return center >= left && center <= right;
    }

    private static double XOverlapShare(BoundingBox a, BoundingBox b)
    {
        var overlap = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        var shorter = Math.Min(a.Width, b.Width);
        return shorter > 0 ? overlap / shorter : 0;
    }

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

    private static Row MakeRow(List<TextLineBlock> cells)
    {
        var ordered = cells.OrderBy(c => c.Left).ToList();
        return new Row(ordered.Average(c => c.BaselineY), Median(ordered.Select(c => c.FontSize)), ordered);
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

    /// <summary>Clamps a rule box to the page, allowing the zero-thickness extent of a true line (a horizontal rule has no height, a vertical rule no width).</summary>
    private static bool TryClampRule(BoundingBox raw, BoundingBox pageBounds, out BoundingBox clamped)
    {
        clamped = default;
        var left = Math.Max(raw.Left, pageBounds.Left);
        var bottom = Math.Max(raw.Bottom, pageBounds.Bottom);
        var right = Math.Min(raw.Right, pageBounds.Right);
        var top = Math.Min(raw.Top, pageBounds.Top);
        if (right < left || top < bottom) return false;
        clamped = new BoundingBox(left, bottom, right, top);
        return true;
    }

    private static BoundingBox ToBoundingBox(PdfRectangle rect) => new(rect.Left, rect.Bottom, rect.Right, rect.Top);

    private sealed record Row(double Baseline, double FontSize, IReadOnlyList<TextLineBlock> Cells)
    {
        public string Text => string.Join(" ", Cells.OrderBy(c => c.Left).Select(c => c.Text));
        public double Left => Cells.Min(c => c.Left);
        public double Right => Cells.Max(c => c.Right);
    }
}
