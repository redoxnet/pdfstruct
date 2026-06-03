// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Models;

namespace PdfStruct.Analysis.Tables;

/// <summary>
/// Splits a large ruled enclosure that stacks several sub-tables of different
/// column schemas — a form or a receipt where the bands change from a key-value
/// block to a summary row to a line-item grid — into its schema-stable
/// sub-regions, using the drawn vertical rules as the signal.
/// </summary>
/// <remarks>
/// Each band between two full-width horizontal rules carries a signature: the
/// set of interior vertical-rule x-positions that traverse it (the outer borders
/// excluded). A boundary where the signature changes to a <em>conflicting</em>
/// partition — both bands name columns and neither set is a subset of the other —
/// is a schema transition and becomes a cut. A constant signature down the
/// enclosure, or one where a sparser band's columns nest inside a denser band's
/// (a multi-level header's group separators sitting inside its leaf separators),
/// is one schema and is returned unsplit.
///
/// <para>
/// This is meant only as a fallback for an enclosure that failed to recover as a
/// single grid; a table that already recovers is one consistent schema and is
/// never handed here.
/// </para>
/// </remarks>
internal static class TableSchemaSegmenter
{
    /// <summary>An interior vertical rule sits at least this far inside the region edges; nearer is the outer border.</summary>
    private const double InteriorMargin = 2.0;

    /// <summary>Two vertical-rule x-positions within this many points are the same column separator.</summary>
    private const double VerticalMatchTolerance = 3.0;

    /// <summary>A vertical rule belongs to a band's signature when it covers at least this share of the band height.</summary>
    private const double BandCoverageShare = 0.6;

    /// <summary>Fewer than this many bands cannot show a schema transition.</summary>
    private const int MinBands = 2;

    /// <summary>
    /// Splits a region at its vertical-schema transitions.
    /// </summary>
    /// <param name="region">The enclosure's bounding box.</param>
    /// <param name="fullWidthRuleYs">The y-centres of the region's full-width horizontal rules, top to bottom — the band boundaries.</param>
    /// <param name="verticalRules">The page's vertical rules; the interior ones inside the region carry the per-band column schema.</param>
    /// <returns>The sub-region boxes top to bottom, or the region unchanged when no schema transition divides it.</returns>
    public static IReadOnlyList<BoundingBox> Split(
        BoundingBox region,
        IReadOnlyList<double> fullWidthRuleYs,
        IReadOnlyList<BoundingBox> verticalRules)
    {
        ArgumentNullException.ThrowIfNull(fullWidthRuleYs);
        ArgumentNullException.ThrowIfNull(verticalRules);

        var levels = fullWidthRuleYs
            .Where(y => y <= region.Top && y >= region.Bottom)
            .OrderByDescending(y => y)
            .ToList();
        if (levels.Count < MinBands + 1) return [region];

        var interior = verticalRules
            .Where(v => v.Left > region.Left + InteriorMargin && v.Right < region.Right - InteriorMargin
                        && v.Bottom < region.Top && v.Top > region.Bottom)
            .ToList();

        var signatures = new List<List<double>>(levels.Count - 1);
        for (var b = 0; b < levels.Count - 1; b++)
            signatures.Add(BandSignature(interior, bottom: levels[b + 1], top: levels[b]));

        var paneStarts = new List<int> { 0 };
        for (var b = 0; b + 1 < signatures.Count; b++)
            if (Conflict(signatures[b], signatures[b + 1]))
                paneStarts.Add(b + 1);
        if (paneStarts.Count <= 1) return [region];

        var boxes = new List<BoundingBox>(paneStarts.Count);
        for (var p = 0; p < paneStarts.Count; p++)
        {
            var firstBand = paneStarts[p];
            var lastBand = (p + 1 < paneStarts.Count ? paneStarts[p + 1] : signatures.Count) - 1;
            var top = p == 0 ? region.Top : levels[firstBand];
            var bottom = p == paneStarts.Count - 1 ? region.Bottom : levels[lastBand + 1];
            boxes.Add(new BoundingBox(region.Left, bottom, region.Right, top));
        }
        return boxes;
    }

    /// <summary>The clustered x-centres of the interior vertical rules that traverse a band.</summary>
    private static List<double> BandSignature(IReadOnlyList<BoundingBox> interior, double bottom, double top)
    {
        var bandHeight = top - bottom;
        if (bandHeight <= 0) return [];
        var centers = interior
            .Where(v => Math.Min(v.Top, top) - Math.Max(v.Bottom, bottom) >= BandCoverageShare * bandHeight)
            .Select(v => (v.Left + v.Right) / 2.0)
            .OrderBy(x => x)
            .ToList();
        return Cluster(centers);
    }

    private static List<double> Cluster(List<double> sortedCenters)
    {
        var clusters = new List<double>();
        var clusterStart = double.NaN;
        var sum = 0.0;
        var count = 0;
        foreach (var x in sortedCenters)
        {
            if (count > 0 && x - clusterStart > VerticalMatchTolerance)
            {
                clusters.Add(sum / count);
                sum = 0;
                count = 0;
            }
            if (count == 0) clusterStart = x;
            sum += x;
            count++;
        }
        if (count > 0) clusters.Add(sum / count);
        return clusters;
    }

    /// <summary>Two adjacent band signatures conflict when both name columns and neither is a subset of the other — distinct partitions, not a multi-level header's nesting.</summary>
    private static bool Conflict(List<double> a, List<double> b) =>
        a.Count > 0 && b.Count > 0 && !IsSubset(a, b) && !IsSubset(b, a);

    private static bool IsSubset(List<double> a, List<double> b) =>
        a.All(x => b.Any(y => Math.Abs(x - y) <= VerticalMatchTolerance));
}
