// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.RegularExpressions;
using PdfStruct.Models;

namespace PdfStruct.Analysis;

/// <summary>
/// Rejoins numbered-paragraph markers that the page splits into their own
/// margin column with the body line they label. Korean patents (and others)
/// print the paragraph number <c>[0001]</c> in a narrow left rail, on the same
/// row as the first line of its paragraph at <c>x ≈ 64</c>. Column-based
/// reading order would otherwise emit the whole marker rail first and the
/// bodies afterwards, severing each number from its text.
/// </summary>
/// <remarks>
/// The associator runs before list detection and paragraph merging, so once a
/// marker is folded back onto its body line — <c>"[0001] 본 기술은…"</c> — the
/// run flows downstream as ordinary inline-marked paragraphs and
/// <see cref="ListDetector"/> recognises it as a numbered-paragraph run.
///
/// <para>
/// Precision is favoured: a rail must be a narrow, left-aligned column of at
/// least two bracketed zero-padded markers whose numbers increase down the
/// page, and each marker must have exactly one body line sharing its row to
/// its right. A marker with no body line on its row is left untouched rather
/// than risk attaching it to the wrong text.
/// </para>
/// </remarks>
internal static partial class NumberedParagraphRailAssociator
{
    /// <summary>Fewest aligned markers that form a rail.</summary>
    private const int MinRailSize = 2;

    /// <summary>Maximum spread, in points, of the markers' left edges for them to count as one aligned rail.</summary>
    private const double MaxLeftSpread = 8.0;

    /// <summary>Minimum clear horizontal gap, in points, between a marker and the body line it labels.</summary>
    private const double MinBodyGap = 2.0;

    /// <summary>
    /// Returns a line stream in which each split numbered-paragraph marker has
    /// been folded onto its same-row body line; the original line list is
    /// returned unchanged when the page carries no such rail.
    /// </summary>
    /// <param name="lines">The page's text lines in document order.</param>
    /// <returns>The rejoined line stream, or <paramref name="lines"/> unchanged.</returns>
    public static IReadOnlyList<TextLineBlock> Associate(IReadOnlyList<TextLineBlock> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count < MinRailSize) return lines;

        var markers = new List<(int Index, int Number)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var match = MarkerPattern().Match(lines[i].Text.Trim());
            if (match.Success)
                markers.Add((i, int.Parse(match.Groups[1].Value)));
        }
        if (markers.Count < MinRailSize) return lines;

        var minLeft = markers.Min(m => lines[m.Index].Left);
        var maxLeft = markers.Max(m => lines[m.Index].Left);
        if (maxLeft - minLeft > MaxLeftSpread) return lines;

        var ordered = markers.OrderByDescending(m => lines[m.Index].Top).ToList();
        for (var i = 1; i < ordered.Count; i++)
            if (ordered[i].Number <= ordered[i - 1].Number) return lines;

        var markerIndices = new HashSet<int>(markers.Select(m => m.Index));
        var bodyForMarker = new Dictionary<int, int>();
        var relabel = new Dictionary<int, string>();
        foreach (var (index, _) in markers)
        {
            var bodyIndex = FindRowBody(lines, index, markerIndices, bodyForMarker.Values);
            if (bodyIndex < 0) continue;
            bodyForMarker[index] = bodyIndex;
            relabel[bodyIndex] = lines[index].Text.Trim();
        }
        if (bodyForMarker.Count == 0) return lines;

        var consumedMarkers = new HashSet<int>(bodyForMarker.Keys);
        var result = new List<TextLineBlock>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            if (consumedMarkers.Contains(i)) continue;
            if (relabel.TryGetValue(i, out var marker))
            {
                var body = lines[i];
                var markerLine = lines[bodyForMarker.First(p => p.Value == i).Key];
                result.Add(body with
                {
                    Text = $"{marker} {body.Text}",
                    BoundingBox = body.BoundingBox.Merge(markerLine.BoundingBox)
                });
            }
            else
            {
                result.Add(lines[i]);
            }
        }
        return result;
    }

    /// <summary>
    /// Finds the body line that shares a marker's row: a non-marker line, not
    /// already claimed by another marker, whose vertical span contains the
    /// marker's centre and which begins to the right of the marker. Returns
    /// the nearest such line's index, or <c>-1</c> when none qualifies.
    /// </summary>
    private static int FindRowBody(
        IReadOnlyList<TextLineBlock> lines,
        int markerIndex,
        HashSet<int> markerIndices,
        IEnumerable<int> alreadyClaimed)
    {
        var marker = lines[markerIndex];
        var markerCenterY = (marker.Top + marker.Bottom) / 2.0;
        var claimed = new HashSet<int>(alreadyClaimed);

        var best = -1;
        var bestLeft = double.MaxValue;
        for (var i = 0; i < lines.Count; i++)
        {
            if (i == markerIndex || markerIndices.Contains(i) || claimed.Contains(i)) continue;
            var line = lines[i];
            if (markerCenterY < line.Bottom || markerCenterY > line.Top) continue;
            if (line.Left < marker.Right + MinBodyGap) continue;
            if (line.Left < bestLeft)
            {
                bestLeft = line.Left;
                best = i;
            }
        }
        return best;
    }

    [GeneratedRegex(@"^\[(\d{4,})\]$", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerPattern();
}
