// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Models;

namespace PdfStruct.Analysis.Tables;

/// <summary>
/// Merges the table regions found by the two detectors into one per-page set,
/// resolving overlaps so a table sensed by both its rules and its text alignment
/// is reported once.
/// </summary>
internal static class TableDetector
{
    /// <summary>A borderless region overlapping a ruled region by at least this share of the smaller box is the same table; the ruled region wins.</summary>
    public const double OverlapShare = 0.5;

    /// <summary>
    /// Combines ruled and borderless regions for a page. Ruled regions are kept
    /// as found; a borderless region is added only when it does not substantially
    /// overlap a ruled region, since the ruling lines bound a table more reliably
    /// than text alignment alone.
    /// </summary>
    /// <param name="ruled">Regions from <see cref="TextAwareRuledTableDetector"/>.</param>
    /// <param name="borderless">Regions from <see cref="BorderlessTableDetector"/>.</param>
    /// <returns>The merged, de-duplicated regions.</returns>
    public static IReadOnlyList<DetectedTable> Merge(
        IReadOnlyList<DetectedTable> ruled, IReadOnlyList<DetectedTable> borderless)
    {
        ArgumentNullException.ThrowIfNull(ruled);
        ArgumentNullException.ThrowIfNull(borderless);

        // Ruled regions are considered first, so a table sensed by both its rules
        // and its text keeps the ruled kind. Any region — from either source, or
        // two ruled regions for the one table whose rules split into width-disjoint
        // stacks — that substantially overlaps a kept region is fused into it by
        // union, so the table is reported once and spans all its parts.
        var merged = new List<DetectedTable>();
        foreach (var region in ruled.Concat(borderless))
        {
            var index = merged.FindIndex(kept => Overlaps(kept.BoundingBox, region.BoundingBox));
            if (index < 0)
                merged.Add(region);
            else
                merged[index] = merged[index] with { BoundingBox = merged[index].BoundingBox.Merge(region.BoundingBox) };
        }
        return merged;
    }

    private static bool Overlaps(BoundingBox a, BoundingBox b)
    {
        var intersection = a.IntersectionArea(b);
        if (intersection <= 0) return false;
        var smaller = Math.Min(a.Area, b.Area);
        return smaller > 0 && intersection / smaller >= OverlapShare;
    }
}
