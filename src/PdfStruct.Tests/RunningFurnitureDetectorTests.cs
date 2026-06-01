// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Analysis;
using PdfStruct.Models;
using Xunit;

namespace PdfStruct.Tests;

public class RunningFurnitureDetectorTests
{
    private const double PageHeight = 800.0;

    [Fact]
    public void IsPageNumberSequence_AscendingDigits_ReturnsTrue()
    {
        var occurrences = new[]
        {
            (1, "법제처 1 국가법령정보센터"),
            (2, "법제처 2 국가법령정보센터"),
            (3, "법제처 3 국가법령정보센터"),
        };
        Assert.True(RunningFurnitureDetector.IsPageNumberSequence(occurrences));
    }

    [Fact]
    public void IsPageNumberSequence_IdenticalText_ReturnsFalse()
    {
        // No varying digit slot means no sequence — this is the verbatim-repeat
        // case, handled by the coverage and adjacency signals instead.
        var occurrences = new[]
        {
            (1, "Annual Report"),
            (2, "Annual Report"),
        };
        Assert.False(RunningFurnitureDetector.IsPageNumberSequence(occurrences));
    }

    [Fact]
    public void IsPageNumberSequence_Descending_ReturnsFalse()
    {
        var occurrences = new[]
        {
            (1, "page 9"),
            (2, "page 8"),
            (3, "page 7"),
        };
        Assert.False(RunningFurnitureDetector.IsPageNumberSequence(occurrences));
    }

    [Fact]
    public void IsPageNumberSequence_HonoursPageOrderNotListOrder()
    {
        // Out-of-order input but tracks the page number once sorted by page.
        var occurrences = new[]
        {
            (3, "p. 3"),
            (1, "p. 1"),
            (2, "p. 2"),
        };
        Assert.True(RunningFurnitureDetector.IsPageNumberSequence(occurrences));
    }

    [Fact]
    public void IsPageNumberSequence_StepDoesNotTrackPageNumber_ReturnsFalse()
    {
        // 10, 20, 30 on consecutive pages steps by ten, not by one — it is a
        // simply increasing number, not a page number.
        var occurrences = new[]
        {
            (1, "10"),
            (2, "20"),
            (3, "30"),
        };
        Assert.False(RunningFurnitureDetector.IsPageNumberSequence(occurrences));
    }

    [Fact]
    public void IsPageNumberSequence_ConstantOffsetFrontMatter_ReturnsTrue()
    {
        // Front matter unnumbered: printed "1" first appears on physical page 3,
        // so value − page is the constant −2. Still a page-number sequence.
        var occurrences = new[]
        {
            (3, "1"),
            (4, "2"),
            (5, "3"),
        };
        Assert.True(RunningFurnitureDetector.IsPageNumberSequence(occurrences));
    }

    [Fact]
    public void IsPageNumberSequence_OneSlotIncreasingAmongMany_ReturnsTrue()
    {
        // Constant year, increasing page number — the second slot sequences.
        var occurrences = new[]
        {
            (1, "2026 · 1"),
            (2, "2026 · 2"),
            (3, "2026 · 3"),
        };
        Assert.True(RunningFurnitureDetector.IsPageNumberSequence(occurrences));
    }

    [Fact]
    public void DetectRepeatingIds_NumberedFooterAcrossAllPages_IsDetected()
    {
        var elements = new List<ContentElement>();
        var pageHeights = new Dictionary<int, double>();
        for (var page = 1; page <= 6; page++)
        {
            pageHeights[page] = PageHeight;
            elements.Add(Footer(id: page, page: page, text: $"법제처 {page} 국가법령정보센터"));
        }

        var rejected = RunningFurnitureDetector.DetectRepeatingIds(elements, pageHeights);

        Assert.Equal(Enumerable.Range(1, 6).ToHashSet(), rejected);
    }

    [Fact]
    public void DetectRepeatingIds_LoneAdjacentPair_IsNotDetected()
    {
        // Ten-page document, "DRAFT" footer only on pages 3 and 4. A single
        // adjacent pair below the coverage threshold is too weak to remove.
        var pageHeights = Enumerable.Range(1, 10).ToDictionary(p => p, _ => PageHeight);
        var elements = new List<ContentElement>
        {
            Footer(id: 3, page: 3, text: "DRAFT"),
            Footer(id: 4, page: 4, text: "DRAFT"),
        };

        var rejected = RunningFurnitureDetector.DetectRepeatingIds(elements, pageHeights);

        Assert.Empty(rejected);
    }

    [Fact]
    public void DetectRepeatingIds_ThreeConsecutiveExactRepeats_IsDetected()
    {
        // "DRAFT" on pages 3, 4, 5 spans two nearby pairs ((3,4) and (4,5)),
        // corroborating the exact-repeat signal below the coverage threshold.
        var pageHeights = Enumerable.Range(1, 10).ToDictionary(p => p, _ => PageHeight);
        var elements = new List<ContentElement>
        {
            Footer(id: 3, page: 3, text: "DRAFT"),
            Footer(id: 4, page: 4, text: "DRAFT"),
            Footer(id: 5, page: 5, text: "DRAFT"),
        };

        var rejected = RunningFurnitureDetector.DetectRepeatingIds(elements, pageHeights);

        Assert.Equal(new HashSet<int> { 3, 4, 5 }, rejected);
    }

    [Fact]
    public void DetectRepeatingIds_AlternatingHeader_IsDetected()
    {
        // An even/odd running header on pages 1, 3, 5, 7 of a ten-page document
        // sits below 70% coverage but forms three nearby (gap-2) pairs.
        var pageHeights = Enumerable.Range(1, 10).ToDictionary(p => p, _ => PageHeight);
        var elements = new List<ContentElement>
        {
            Header(id: 1, page: 1, text: "Part One"),
            Header(id: 3, page: 3, text: "Part One"),
            Header(id: 5, page: 5, text: "Part One"),
            Header(id: 7, page: 7, text: "Part One"),
        };

        var rejected = RunningFurnitureDetector.DetectRepeatingIds(elements, pageHeights);

        Assert.Equal(new HashSet<int> { 1, 3, 5, 7 }, rejected);
    }

    [Fact]
    public void DetectRepeatingIds_FarApartCoincidentalRepeat_IsNotDetected()
    {
        // Same "DRAFT" text on pages 3 and 8 (gap 5): not broad coverage, not a
        // sequence, not adjacent — a coincidence, not running furniture.
        var pageHeights = Enumerable.Range(1, 10).ToDictionary(p => p, _ => PageHeight);
        var elements = new List<ContentElement>
        {
            Footer(id: 3, page: 3, text: "DRAFT"),
            Footer(id: 8, page: 8, text: "DRAFT"),
        };

        var rejected = RunningFurnitureDetector.DetectRepeatingIds(elements, pageHeights);

        Assert.Empty(rejected);
    }

    [Fact]
    public void DetectRepeatingIds_NumberSequenceOnFrontMatterOnly_IsDetected()
    {
        // "Page N" on the first three pages of a ten-page document: coverage is
        // below threshold but the digits sequence.
        var pageHeights = Enumerable.Range(1, 10).ToDictionary(p => p, _ => PageHeight);
        var elements = new List<ContentElement>
        {
            Footer(id: 1, page: 1, text: "Page 1"),
            Footer(id: 2, page: 2, text: "Page 2"),
            Footer(id: 3, page: 3, text: "Page 3"),
        };

        var rejected = RunningFurnitureDetector.DetectRepeatingIds(elements, pageHeights);

        Assert.Equal(new HashSet<int> { 1, 2, 3 }, rejected);
    }

    [Fact]
    public void DetectRepeatingIds_LargeCentredTitleDistinctFromRunningHeader_TitleSurvives()
    {
        // Page 1 carries a large title; pages 2..10 carry a smaller running
        // header with the same text at the same position. Folding font size
        // into the signature keeps them in separate groups, so the title is
        // not removed alongside the header.
        var pageHeights = Enumerable.Range(1, 10).ToDictionary(p => p, _ => PageHeight);
        var elements = new List<ContentElement>
        {
            Header(id: 1, page: 1, text: "Constitution", fontSize: 24.0),
        };
        for (var page = 2; page <= 10; page++)
            elements.Add(Header(id: page, page: page, text: "Constitution", fontSize: 10.0));

        var rejected = RunningFurnitureDetector.DetectRepeatingIds(elements, pageHeights);

        Assert.DoesNotContain(1, rejected);
        Assert.Equal(Enumerable.Range(2, 9).ToHashSet(), rejected);
    }

    [Fact]
    public void DetectRepeatingIds_SinglePageDocument_DetectsNothing()
    {
        var pageHeights = new Dictionary<int, double> { [1] = PageHeight };
        var elements = new List<ContentElement> { Footer(id: 1, page: 1, text: "Page 1") };

        Assert.Empty(RunningFurnitureDetector.DetectRepeatingIds(elements, pageHeights));
    }

    private static ParagraphElement Footer(int id, int page, string text, double fontSize = 10.0) =>
        TextElement(id, page, text, fontSize, top: 40.0, bottom: 28.0);

    private static ParagraphElement Header(int id, int page, string text, double fontSize = 10.0) =>
        TextElement(id, page, text, fontSize, top: 780.0, bottom: 768.0);

    private static ParagraphElement TextElement(int id, int page, string text, double fontSize, double top, double bottom) =>
        new()
        {
            Id = id,
            PageNumber = page,
            BoundingBox = new BoundingBox(72.0, bottom, 300.0, top),
            Text = new TextProperties { Content = text, FontSize = fontSize },
        };
}
