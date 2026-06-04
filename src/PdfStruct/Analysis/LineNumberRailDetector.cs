// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Models;

namespace PdfStruct.Analysis;

/// <summary>
/// Detects vertical rails of standalone line-number labels — the narrow
/// columns of bare digits that legal documents print in their margin and
/// that US patents print in the gutter between body columns (<c>5</c>,
/// <c>10</c>, <c>15</c>, …). These are page furniture: they carry no reading
/// content and must leave the text stream before list detection and
/// paragraph merging see them.
/// </summary>
/// <remarks>
/// The detector is strictly page-local — a rail is recognised from the
/// intrinsic pattern of one page, never from cross-page repetition. The
/// generic running-furniture filter cannot do this job: it only inspects the
/// top and bottom <c>25%</c> bands, so it can reach only a rail's endpoints,
/// and it groups by a font-size signature that the never-drawn (Tr 3) glyphs
/// patents use for line numbers report unstably — fragmenting one rail across
/// several groups and removing its members raggedly. This detector instead
/// keys on the defining property of a line-number rail: a tall, narrow,
/// horizontally isolated column of short integers whose value grows linearly
/// with downward position.
///
/// <para>
/// Precision is favoured over recall (see the repository table-detection
/// directive): every candidate rail must clear all of the structural guards
/// below, so an ordinary numeric table column or a bare-digit list is left
/// untouched. Ordered-list labels never reach this detector as candidates
/// because their punctuation (<c>1.</c>, <c>(1)</c>, <c>[1]</c>) fails the
/// pure-digit test.
/// </para>
/// </remarks>
internal static class LineNumberRailDetector
{
    /// <summary>Fewest aligned digit lines that can form a rail. Below this a coincidental column of numbers is not enough evidence.</summary>
    private const int MinRailSize = 4;

    /// <summary>Most digits a rail label may carry. Line numbers stay small; four-digit runs (years, identifiers) are not rails.</summary>
    private const int MaxDigits = 3;

    /// <summary>Maximum width, in points, of a single rail label. A three-digit number at body size stays well under this; wider digit lines are table figures, not rail labels.</summary>
    private const double MaxMemberWidth = 26.0;

    /// <summary>Maximum horizontal spread, in points, of a rail's left-to-right extent. Rail labels are right- or left-aligned into a thin column.</summary>
    private const double MaxClusterWidth = 26.0;

    /// <summary>Minimum fraction of page height a rail's vertical extent must cover. A rail runs the length of its body column; a short numeric cluster does not.</summary>
    private const double MinVerticalCoverageRatio = 0.40;

    /// <summary>Lower bound on each consecutive value-per-descent ratio relative to the rail's median ratio.</summary>
    private const double RatioRegularityLow = 0.55;

    /// <summary>Upper bound on each consecutive value-per-descent ratio relative to the rail's median ratio.</summary>
    private const double RatioRegularityHigh = 1.8;

    /// <summary>Minimum clear horizontal gap, in points, between a gutter rail and the body columns flanking it.</summary>
    private const double MinFlankGap = 2.0;

    /// <summary>
    /// Returns the indices, into <paramref name="lines"/>, of every line that
    /// belongs to a line-number rail on the page. The set is empty when the
    /// page carries no rail.
    /// </summary>
    /// <param name="lines">The page's text lines in document order.</param>
    /// <param name="pageWidth">Page width in PDF points.</param>
    /// <param name="pageHeight">Page height in PDF points.</param>
    /// <returns>The line indices to drop as rail furniture; empty when none qualify.</returns>
    public static IReadOnlySet<int> Detect(
        IReadOnlyList<TextLineBlock> lines,
        double pageWidth,
        double pageHeight)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var rail = new HashSet<int>();
        if (lines.Count < MinRailSize || pageWidth <= 0 || pageHeight <= 0)
            return rail;

        var candidates = new List<int>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            if (IsRailLabel(lines[i]))
                candidates.Add(i);
        }
        if (candidates.Count < MinRailSize) return rail;

        candidates.Sort((a, b) => lines[a].BoundingBox.CenterX.CompareTo(lines[b].BoundingBox.CenterX));

        var cluster = new List<int>();
        var clusterMinCenter = double.NaN;
        foreach (var index in candidates)
        {
            var center = lines[index].BoundingBox.CenterX;
            if (cluster.Count == 0)
            {
                cluster.Add(index);
                clusterMinCenter = center;
            }
            else if (center - clusterMinCenter <= MaxClusterWidth)
            {
                cluster.Add(index);
            }
            else
            {
                if (TryConfirmRail(cluster, lines, pageWidth, pageHeight))
                    rail.UnionWith(cluster);
                cluster = [index];
                clusterMinCenter = center;
            }
        }
        if (TryConfirmRail(cluster, lines, pageWidth, pageHeight))
            rail.UnionWith(cluster);

        return rail;
    }

    /// <summary>Returns <c>true</c> for a line whose visible text is a short run of ASCII digits in a narrow box.</summary>
    private static bool IsRailLabel(TextLineBlock line)
    {
        var text = line.Text.AsSpan().Trim();
        if (text.Length is < 1 or > MaxDigits) return false;
        foreach (var c in text)
            if (!char.IsAsciiDigit(c)) return false;
        return line.Width <= MaxMemberWidth;
    }

    /// <summary>
    /// Confirms that an x-aligned cluster of digit lines is a line-number
    /// rail: enough members, a narrow band, tall vertical coverage, strictly
    /// increasing values top-to-bottom, a regular value-per-descent ratio,
    /// and either a page margin or a genuine inter-column gutter around it.
    /// </summary>
    private static bool TryConfirmRail(
        IReadOnlyList<int> cluster,
        IReadOnlyList<TextLineBlock> lines,
        double pageWidth,
        double pageHeight)
    {
        if (cluster.Count < MinRailSize) return false;

        var members = cluster
            .Select(i => (Index: i, Line: lines[i], Value: int.Parse(lines[i].Text.Trim())))
            .OrderByDescending(m => m.Line.Top)
            .ToList();

        var left = members.Min(m => m.Line.Left);
        var right = members.Max(m => m.Line.Right);
        if (right - left > MaxClusterWidth) return false;

        var top = members[0].Line.Top;
        var bottom = members[^1].Line.Bottom;
        if ((top - bottom) / pageHeight < MinVerticalCoverageRatio) return false;

        var ratios = new List<double>(members.Count - 1);
        for (var i = 1; i < members.Count; i++)
        {
            if (members[i].Value <= members[i - 1].Value) return false;
            var descent = members[i - 1].Line.Top - members[i].Line.Top;
            if (descent <= 0) return false;
            ratios.Add((members[i].Value - members[i - 1].Value) / descent);
        }

        var sortedRatios = ratios.OrderBy(r => r).ToList();
        var median = sortedRatios[sortedRatios.Count / 2];
        if (median <= 0) return false;
        foreach (var ratio in ratios)
        {
            if (ratio < median * RatioRegularityLow || ratio > median * RatioRegularityHigh)
                return false;
        }

        return HasMarginOrGutter(cluster, lines, left, right, top, bottom, pageWidth);
    }

    /// <summary>
    /// Returns <c>true</c> when the rail either hugs a left/right page margin
    /// or sits in a gutter with body text on both sides, separated by a clear
    /// gap. This is the guard that distinguishes a furniture rail from the
    /// leading numeric column of a table, which has no content to one side.
    /// </summary>
    private static bool HasMarginOrGutter(
        IReadOnlyList<int> cluster,
        IReadOnlyList<TextLineBlock> lines,
        double left,
        double right,
        double top,
        double bottom,
        double pageWidth)
    {
        if (left <= pageWidth * 0.10 || right >= pageWidth * 0.90)
            return true;

        var members = new HashSet<int>(cluster);
        var textLeft = false;
        var textRight = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (members.Contains(i)) continue;
            var line = lines[i];
            if (line.Top <= bottom || line.Bottom >= top) continue;
            if (line.Right <= left - MinFlankGap) textLeft = true;
            else if (line.Left >= right + MinFlankGap) textRight = true;
            if (textLeft && textRight) return true;
        }
        return false;
    }
}
