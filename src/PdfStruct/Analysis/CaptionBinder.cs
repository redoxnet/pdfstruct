// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.RegularExpressions;
using PdfStruct.Models;

namespace PdfStruct.Analysis;

/// <summary>
/// Links figure and table captions to their target element. A caption is found
/// by a deterministic score over geometry and reading order — vertical
/// proximity, horizontal alignment, reading-order adjacency, and candidate
/// shape — with keyword, italic, and small-font signals as additive boosts
/// rather than requirements. Machine-readable codes, headings, and distant or
/// structurally-separated paragraphs are never bound.
/// </summary>
/// <remarks>
/// Runs after element IDs are final so a <see cref="CaptionElement.LinkedContentId"/>
/// references a stable target ID, and consumes the reading-ordered element list
/// so adjacency is a positional check. Each caption binds to one target and each
/// target keeps one caption; a paragraph that sits ambiguously between two
/// equally-plausible targets is left unbound.
/// </remarks>
public static partial class CaptionBinder
{
    private const double StrongThreshold = 0.75;
    private const double WeakThreshold = 0.60;
    private const double AmbiguityDelta = 0.05;
    private const double DefaultFontSize = 10.0;
    private const int MaxNeighbourDistance = 2;

    /// <summary>
    /// Converts the best-matching paragraph next to each figure or table into a
    /// <see cref="CaptionElement"/> linked to that target, mutating
    /// <paramref name="kids"/> in place.
    /// </summary>
    /// <param name="kids">The document's content elements in reading order.</param>
    public static void Bind(List<ContentElement> kids)
    {
        ArgumentNullException.ThrowIfNull(kids);
        if (kids.Count < 2) return;

        var bodyFontSize = MedianBodyFontSize(kids);

        var pairs = new List<Pairing>();
        for (var t = 0; t < kids.Count; t++)
        {
            if (!IsTarget(kids[t])) continue;

            for (var d = 1; d <= MaxNeighbourDistance; d++)
            {
                TryPair(kids, t, t - d, d, bodyFontSize, pairs);
                TryPair(kids, t, t + d, d, bodyFontSize, pairs);
            }
        }

        if (pairs.Count == 0) return;

        DropAmbiguousCandidates(pairs);

        var usedTarget = new HashSet<int>();
        var usedCandidate = new HashSet<int>();
        foreach (var pair in pairs.OrderByDescending(p => p.Score))
        {
            if (usedTarget.Contains(pair.TargetIndex) || usedCandidate.Contains(pair.CandidateIndex)) continue;
            if (pair.Score < StrongThreshold && !(pair.Score >= WeakThreshold && pair.HasBoost)) continue;

            usedTarget.Add(pair.TargetIndex);
            usedCandidate.Add(pair.CandidateIndex);

            var paragraph = (ParagraphElement)kids[pair.CandidateIndex];
            kids[pair.CandidateIndex] = new CaptionElement
            {
                Id = paragraph.Id,
                PageNumber = paragraph.PageNumber,
                BoundingBox = paragraph.BoundingBox,
                Text = paragraph.Text,
                LinkedContentId = kids[pair.TargetIndex].Id,
            };
        }
    }

    /// <summary>Scores a single (target, candidate) pairing and records it when it clears the weak threshold and no hard blocker rejects it.</summary>
    private static void TryPair(
        List<ContentElement> kids, int targetIndex, int candidateIndex, int distance, double bodyFontSize, List<Pairing> pairs)
    {
        if (candidateIndex < 0 || candidateIndex >= kids.Count) return;

        var target = kids[targetIndex];
        if (kids[candidateIndex] is not ParagraphElement candidate) return;
        if (candidate.PageNumber != target.PageNumber) return;

        // A blocker between target and a distance-2 candidate severs the link.
        if (distance == 2)
        {
            var between = kids[(targetIndex + candidateIndex) / 2];
            if (between.PageNumber == target.PageNumber && IsBlocker(between)) return;
        }

        var em = candidate.Text.FontSize > 0 ? candidate.Text.FontSize : bodyFontSize;
        var targetBox = target.BoundingBox;
        var candidateBox = candidate.BoundingBox;

        var vertical = VerticalProximity(targetBox, candidateBox, em);
        if (vertical <= 0) return;
        var horizontal = HorizontalAlignment(targetBox, candidateBox);
        var adjacency = distance == 1 ? 1.0 : 0.6;
        var shape = CandidateShape(candidate.Text.Content);

        var score = vertical * horizontal * adjacency * shape;

        var (boost, hasBoost) = ContentBoost(target, candidate, candidateBox, targetBox, bodyFontSize);
        score += boost;

        if (score < WeakThreshold) return;
        pairs.Add(new Pairing(targetIndex, candidateIndex, score, hasBoost));
    }

    /// <summary>Removes pairings for any candidate whose two best target scores are within <see cref="AmbiguityDelta"/> — it sits ambiguously between targets.</summary>
    private static void DropAmbiguousCandidates(List<Pairing> pairs)
    {
        var ambiguous = pairs
            .GroupBy(p => p.CandidateIndex)
            .Where(g =>
            {
                var top = g.OrderByDescending(p => p.Score).Take(2).ToList();
                return top.Count == 2 && top[0].Score - top[1].Score < AmbiguityDelta;
            })
            .Select(g => g.Key)
            .ToHashSet();

        if (ambiguous.Count > 0)
            pairs.RemoveAll(p => ambiguous.Contains(p.CandidateIndex));
    }

    /// <summary>Returns the vertical-proximity factor in (0,1], or 0 when the candidate overlaps the target vertically or sits too far above/below it.</summary>
    private static double VerticalProximity(BoundingBox target, BoundingBox candidate, double em)
    {
        double gap;
        if (candidate.Top <= target.Bottom)
            gap = target.Bottom - candidate.Top; // caption below target
        else if (candidate.Bottom >= target.Top)
            gap = candidate.Bottom - target.Top; // caption above target
        else
            return 0.2; // vertically overlapping — likely an overlay label, not a caption

        if (gap < 0) return 0.2;

        var ratio = em > 0 ? gap / em : double.MaxValue;
        if (ratio <= 2.5) return 1.0 - 0.12 * ratio;   // 1.0 .. 0.7  (strong band)
        if (ratio <= 5.0) return 0.7 - 0.22 * (ratio - 2.5); // 0.7 .. 0.15 (weak band)
        return 0.0;
    }

    /// <summary>Returns the horizontal-alignment factor in (0,1]: 1 when the candidate sits within the target's width, otherwise falling off with centre distance.</summary>
    private static double HorizontalAlignment(BoundingBox target, BoundingBox candidate)
    {
        var tolerance = Math.Max(2.0, target.Width * 0.05);
        if (candidate.Left >= target.Left - tolerance && candidate.Right <= target.Right + tolerance)
            return 1.0;

        var width = Math.Max(target.Width, 1.0);
        var centreDelta = Math.Abs(candidate.CenterX - target.CenterX);
        return Math.Clamp(1.0 - centreDelta / width, 0.05, 1.0);
    }

    /// <summary>Returns the candidate-shape factor: short paragraphs (typical of captions) score high, long body paragraphs low.</summary>
    private static double CandidateShape(string content)
    {
        var length = content.Trim().Length;
        if (length == 0) return 0.05;
        if (length <= 120) return 1.0;
        if (length <= 300) return 1.0 - 0.5 * (length - 120) / 180.0; // 1.0 .. 0.5
        if (length <= 600) return 0.5 - 0.4 * (length - 300) / 300.0; // 0.5 .. 0.1
        return 0.1;
    }

    /// <summary>Returns the additive content boost and whether any boost signal was present (keyword, italic, or below-body font size), plus a small directional prior.</summary>
    private static (double Boost, bool HasBoost) ContentBoost(
        ContentElement target, ParagraphElement candidate, BoundingBox candidateBox, BoundingBox targetBox, double bodyFontSize)
    {
        var boost = 0.0;
        var hasBoost = false;

        if (CaptionKeyword().IsMatch(candidate.Text.Content))
        {
            boost += 0.15;
            hasBoost = true;
        }
        if (candidate.Text is { } text && text.FontSize > 0 && text.FontSize < bodyFontSize * 0.95)
        {
            boost += 0.10;
            hasBoost = true;
        }

        // Directional prior: tables are usually captioned above, figures below.
        var candidateAbove = candidateBox.Bottom >= targetBox.Top;
        if (target is TableElement && candidateAbove) boost += 0.05;
        else if (target is ImageElement && !candidateAbove) boost += 0.05;

        return (boost, hasBoost);
    }

    /// <summary>Returns the median font size of paragraph elements, used as the body-text reference for proximity and boosts.</summary>
    private static double MedianBodyFontSize(List<ContentElement> kids)
    {
        var sizes = kids
            .OfType<ParagraphElement>()
            .Select(p => p.Text.FontSize)
            .Where(s => s > 0)
            .OrderBy(s => s)
            .ToList();
        if (sizes.Count == 0) return DefaultFontSize;
        return sizes[sizes.Count / 2];
    }

    /// <summary>A figure (not a machine-readable code) or a table is a caption target.</summary>
    private static bool IsTarget(ContentElement element) => element switch
    {
        ImageElement image => image.Role == "figure",
        TableElement => true,
        _ => false,
    };

    /// <summary>Elements that, sitting between a target and a distance-2 candidate, sever the caption link.</summary>
    private static bool IsBlocker(ContentElement element) =>
        element is HeadingElement or ListElement or TableElement or ImageElement or CaptionElement;

    [GeneratedRegex(@"(?i)\b(fig(ure|\.)?|table|image|photo|chart)\b|그림|사진|도면|^\s*(도|표)\s*\d", RegexOptions.CultureInvariant)]
    private static partial Regex CaptionKeyword();

    private readonly record struct Pairing(int TargetIndex, int CandidateIndex, double Score, bool HasBoost);
}
