// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.RegularExpressions;
using PdfStruct.Models;

namespace PdfStruct.Analysis;

/// <summary>
/// A pairing of a regex pattern, the heading level to assign when a block's
/// first line matches it, and an optional structural label.
/// </summary>
/// <param name="Match">Regex applied to the block's first (trimmed) line.</param>
/// <param name="HeadingLevel">Heading level to assign on match (1 = h1, 2 = h2, ...). Uncapped on the data model.</param>
/// <param name="Label">
/// Optional <see cref="HeadingElement.Level"/> string. If null, a default
/// label is derived from <paramref name="HeadingLevel"/>: <c>Doctitle</c>
/// at level 1, <c>Subtitle</c> at level 2 and below.
/// </param>
public readonly record struct HeadingPattern(
    Regex Match,
    int HeadingLevel,
    string? Label = null);

/// <summary>
/// Classifies text blocks as headings when the block's first line matches
/// any caller-supplied regex pattern. Language- and domain-agnostic — all
/// pattern knowledge is injected at construction time, so callers can wire
/// in patterns appropriate to their corpus (legal, scientific, contracts,
/// etc.) without the library hard-coding any of them.
/// </summary>
/// <remarks>
/// Designed to be composed in front of <see cref="FontBasedElementClassifier"/>
/// via <see cref="CompositeElementClassifier"/> when font-size signals alone
/// are insufficient — for example, in documents whose chapter and section
/// headings are typeset at the same size as body text and rely on textual
/// markers ("Chapter 1.", "제1장 총강", "Article I.") for hierarchy.
/// </remarks>
public sealed class RegexHeadingClassifier : IElementClassifier
{
    private readonly HeadingPattern[] _patterns;

    /// <summary>
    /// Initializes a new <see cref="RegexHeadingClassifier"/> with the supplied
    /// patterns. Patterns are evaluated in order; the first match wins.
    /// </summary>
    /// <param name="patterns">Regex patterns paired with their target heading levels.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="patterns"/> is <c>null</c>.</exception>
    public RegexHeadingClassifier(IEnumerable<HeadingPattern> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        _patterns = patterns.ToArray();
    }

    /// <inheritdoc />
    public IReadOnlyList<ContentElement> Classify(
        IReadOnlyList<DocumentTextBlock> documentBlocks, ref int startId)
    {
        var results = new List<ContentElement>(documentBlocks.Count);
        foreach (var entry in documentBlocks)
        {
            if (entry.IsStatsOnly) continue;

            ContentElement element = TryClassifyHeading(entry.Block, entry.PageNumber, ref startId)
                ?? (ContentElement)CreateParagraph(entry.Block, entry.PageNumber, ref startId);
            results.Add(element);
        }
        return results;
    }

    /// <summary>Returns a <see cref="HeadingElement"/> when the block's first line matches a configured pattern, otherwise <c>null</c>.</summary>
    private HeadingElement? TryClassifyHeading(TextBlock block, int pageNumber, ref int id)
    {
        var firstLine = FirstLine(block.Text);
        if (firstLine.Length == 0) return null;

        foreach (var pattern in _patterns)
        {
            if (!pattern.Match.IsMatch(firstLine)) continue;

            return new HeadingElement
            {
                Id = id++,
                PageNumber = pageNumber,
                BoundingBox = block.BoundingBox,
                HeadingLevel = pattern.HeadingLevel,
                Level = pattern.Label ?? DefaultLevelLabel(pattern.HeadingLevel),
                Text = new TextProperties
                {
                    Font = block.FontName,
                    FontSize = block.FontSize,
                    Content = block.Text.Trim()
                }
            };
        }
        return null;
    }

    /// <summary>Creates a fallback <see cref="ParagraphElement"/> for blocks that did not match any heading pattern.</summary>
    private static ParagraphElement CreateParagraph(TextBlock block, int pageNumber, ref int id) => new()
    {
        Id = id++,
        PageNumber = pageNumber,
        BoundingBox = block.BoundingBox,
        Text = new TextProperties
        {
            Font = block.FontName,
            FontSize = block.FontSize,
            Content = block.Text.Trim()
        }
    };

    /// <summary>Extracts the first non-empty line of a block's text, trimmed.</summary>
    private static string FirstLine(string text)
    {
        var newline = text.IndexOf('\n');
        return (newline >= 0 ? text[..newline] : text).Trim();
    }

    /// <summary>
    /// Returns the default <see cref="HeadingElement.Level"/> string for a
    /// heading level when the pattern does not supply its own label:
    /// <c>Doctitle</c> at level 1, <c>Subtitle</c> at level 2 and below.
    /// </summary>
    private static string DefaultLevelLabel(int level) => level == 1 ? "Doctitle" : "Subtitle";
}
