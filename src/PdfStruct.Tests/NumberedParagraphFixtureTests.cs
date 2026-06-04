// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PdfStruct;
using PdfStruct.Models;
using Xunit;

namespace PdfStruct.Tests;

/// <summary>
/// Content regression tests for unified numbered-item handling: every numeric
/// run (references, "1." enumerations, patent "[0001]" paragraphs) is emitted
/// as a <see cref="ParagraphElement"/> carrying its printed
/// <see cref="ParagraphElement.Marker"/> — never a list, and never a heading.
/// </summary>
public class NumberedParagraphFixtureTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void References_RenderAsMarkerParagraphs_WithJoinedContinuation()
    {
        var result = new PdfStructParser().Parse(FixturePath("plos_utilizing_llm.pdf"));

        // Numbered runs no longer materialise as lists.
        Assert.Empty(result.Document.Kids.OfType<ListElement>());

        var markerParagraphs = result.Document.Kids
            .OfType<ParagraphElement>()
            .Where(p => p.Marker is not null)
            .ToList();

        // Consecutive references survive the single→double digit transition.
        Assert.Contains(markerParagraphs, p => p.Marker == "9.");
        Assert.Contains(markerParagraphs, p => p.Marker == "14.");

        // The wrapped continuation line is absorbed into its reference, not orphaned.
        var ninth = markerParagraphs.First(p => p.Marker == "9.");
        Assert.Contains("global environmental change", ninth.Text.Content);

        // No reference line is misclassified as a heading.
        var numberedHeadings = result.Document.Kids
            .OfType<HeadingElement>()
            .Where(h => Regex.IsMatch(h.Text.Content, @"^\d+\.\s"));
        Assert.Empty(numberedHeadings);
    }

    [Fact]
    public void ConstitutionEnumeration_RendersAsMarkerParagraphs()
    {
        var result = new PdfStructParser().Parse(FixturePath("kr_constitution.pdf"));

        Assert.Empty(result.Document.Kids.OfType<ListElement>());

        // Article 89 enumerates State Council matters as "1." … "9." items.
        var markers = result.Document.Kids
            .OfType<ParagraphElement>()
            .Where(p => p.Marker is not null)
            .Select(p => p.Marker)
            .ToList();

        Assert.Contains("1.", markers);
        Assert.Contains("9.", markers);
    }
}
