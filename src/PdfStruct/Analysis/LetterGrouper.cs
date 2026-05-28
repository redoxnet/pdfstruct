// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Util;

namespace PdfStruct.Analysis;

/// <summary>
/// Word extractor that drops PdfPig's unconditional <c>gap &gt; height * 0.39</c>
/// word-boundary heuristic, while preserving every other signal the default
/// extractor uses. Designed to recover letter-spaced display titles whose
/// underlying letter stream still carries explicit space characters between
/// words.
/// </summary>
/// <remarks>
/// PdfPig's <c>DefaultWordExtractor</c> classifies any pair whose gap exceeds
/// roughly 39% of the glyph height as a word boundary, even when the
/// underlying letter stream contains an explicit <c>U+0020</c>. Letter-spaced
/// titles (set with non-zero text-character spacing, <c>Tc</c>) emit gaps
/// around 46% of glyph height between every glyph, so the default extractor
/// fragments a title like <c>MY DEAREST</c> into nine single-letter words.
/// This is the dominant failure mode behind letter-spaced headings being
/// misclassified downstream.
///
/// <para>
/// The grouper is otherwise byte-for-byte equivalent to PdfPig's
/// <c>DefaultWordExtractor</c>: it preserves whitespace splitting, font name
/// and size transitions, text orientation flips, reverse cursor moves, and
/// the suspect-gap fallback that catches PDFs which omit the explicit space
/// character. The line-spanning gap histogram is intentionally retained
/// across line breaks — the suspect-gap check needs document-wide gap
/// statistics to be robust on short heading lines.
/// </para>
/// </remarks>
public sealed class LetterGrouper : IWordExtractor
{
    /// <summary>Singleton instance, matching the convention used by PdfPig's bundled extractors.</summary>
    public static readonly IWordExtractor Instance = new LetterGrouper();

    private LetterGrouper() { }

    /// <summary>
    /// Generates words from a flat letter stream. The implementation mirrors
    /// PdfPig's default extractor in ordering and per-letter bookkeeping; the
    /// behavioural divergence is the absence of the <c>gap &gt; height * 0.39</c>
    /// word-boundary trigger.
    /// </summary>
    /// <remarks>
    /// Only horizontally oriented letters flow through the in-tree path; the
    /// algorithm's Y-descending sort and X-ordered gap arithmetic assume
    /// left-to-right text. Rotated and non-axis-aligned glyphs are delegated
    /// to PdfPig's bbox-driven <see cref="NearestNeighbourWordExtractor"/>,
    /// which clusters letters in their own reading direction. Rotated glyphs
    /// fed through the horizontal path would line-break on every Y change
    /// and emit a separate word per glyph (the canonical failure is the
    /// rotated arXiv watermark in the page margin).
    /// </remarks>
    /// <param name="letters">The page's letters in extraction order.</param>
    /// <returns>Words formed by sequential letter accumulation.</returns>
    public IEnumerable<Word> GetWords(IReadOnlyList<Letter> letters)
    {
        if (letters.Count == 0) yield break;

        var horizontal = new List<Letter>();
        var rotated = new List<Letter>();
        foreach (var letter in letters)
        {
            if (letter.TextOrientation == TextOrientation.Horizontal)
                horizontal.Add(letter);
            else
                rotated.Add(letter);
        }

        if (rotated.Count > 0)
        {
            foreach (var word in NearestNeighbourWordExtractor.Instance.GetWords(rotated))
                yield return word;
        }

        if (horizontal.Count == 0) yield break;

        foreach (var word in GroupHorizontalLetters(horizontal))
            yield return word;
    }

    /// <summary>
    /// Applies the in-tree horizontal letter-grouping algorithm to letters
    /// confirmed to be in <see cref="TextOrientation.Horizontal"/>.
    /// </summary>
    private static IEnumerable<Word> GroupHorizontalLetters(IReadOnlyList<Letter> letters)
    {
        var ordered = letters
            .OrderByDescending(l => l.Location.Y)
            .ThenBy(l => l.Location.X);

        var lettersSoFar = new List<Letter>(10);
        var gapCountsSoFarByFontSize = new Dictionary<double, Dictionary<double, int>>();

        double? y = null;
        double? lastX = null;
        Letter? lastLetter = null;

        foreach (var letter in ordered)
        {
            y ??= letter.Location.Y;
            lastX ??= letter.Location.X;

            if (lastLetter is null)
            {
                if (string.IsNullOrWhiteSpace(letter.Value))
                {
                    continue;
                }

                lettersSoFar.Add(letter);
                lastLetter = letter;
                y = letter.Location.Y;
                lastX = letter.Location.X;
                continue;
            }

            if (letter.Location.Y < y.Value - 0.5)
            {
                if (lettersSoFar.Count > 0)
                {
                    yield return new Word(lettersSoFar.ToList());
                    lettersSoFar.Clear();
                }

                if (!string.IsNullOrWhiteSpace(letter.Value))
                {
                    lettersSoFar.Add(letter);
                }

                y = letter.Location.Y;
                lastX = letter.Location.X;
                lastLetter = letter;

                continue;
            }

            var letterHeight = Math.Max(lastLetter.BoundingBox.Height, letter.BoundingBox.Height);

            var gap = letter.Location.X - (lastLetter.Location.X + lastLetter.Width);
            var nextToLeft = letter.Location.X < lastX.Value - 1;
            var nextIsWhiteSpace = string.IsNullOrWhiteSpace(letter.Value);
            var nextFontDiffers =
                !string.Equals(letter.FontName, lastLetter.FontName, StringComparison.OrdinalIgnoreCase)
                && gap > letter.Width * 0.1;
            var nextFontSizeDiffers = Math.Abs(letter.FontSize - lastLetter.FontSize) > 0.1;
            var nextTextOrientationDiffers = letter.TextOrientation != lastLetter.TextOrientation;

            var suspectGap = false;
            if (!nextFontSizeDiffers && letter.FontSize > 0 && gap >= 0)
            {
                var fontSize = Math.Round(letter.FontSize);
                if (!gapCountsSoFarByFontSize.TryGetValue(fontSize, out var gapCounts))
                {
                    gapCounts = new Dictionary<double, int>();
                    gapCountsSoFarByFontSize[fontSize] = gapCounts;
                }

                var gapRounded = Math.Round(gap, 2);
                gapCounts[gapRounded] = gapCounts.TryGetValue(gapRounded, out var existing)
                    ? existing + 1
                    : 1;

                if (gapCounts.Count > 1 && gap > letterHeight * 0.16)
                {
                    var mostCommonGap = gapCounts.OrderByDescending(x => x.Value).First();
                    if (gap > mostCommonGap.Key * 5 && mostCommonGap.Value > 1)
                    {
                        suspectGap = true;
                    }
                }
            }

            if (nextToLeft || nextIsWhiteSpace || nextFontDiffers
                || nextFontSizeDiffers || nextTextOrientationDiffers || suspectGap)
            {
                if (lettersSoFar.Count > 0)
                {
                    yield return new Word(lettersSoFar.ToList());
                    lettersSoFar.Clear();
                }
            }

            if (!string.IsNullOrWhiteSpace(letter.Value))
            {
                lettersSoFar.Add(letter);
            }

            lastLetter = letter;
            lastX = letter.Location.X;
        }

        if (lettersSoFar.Count > 0)
        {
            yield return new Word(lettersSoFar.ToList());
        }
    }
}
