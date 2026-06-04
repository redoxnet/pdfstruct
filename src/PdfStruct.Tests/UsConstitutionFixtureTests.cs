// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.RegularExpressions;
using PdfStruct.Models;
using Xunit;

namespace PdfStruct.Tests;

/// <summary>
/// Content regression tests anchored to <c>us_constitution.pdf</c>. The
/// U.S. Constitution provides a second quantitative reading-order anchor:
/// the document's structural markers run as Article I, II, ..., VII, then
/// Amendment I, II, ..., XXVII — a 34-element sequence that exercises
/// multi-column XY-Cut handling and Roman-numeral-aware heading detection.
/// </summary>
public class UsConstitutionFixtureTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    /// <summary>
    /// All seven Articles (I–VII) and all twenty-seven Amendments (I–XXVII)
    /// must be present somewhere in the extracted document, and the first
    /// Article reference must appear before the first Amendment reference.
    /// </summary>
    /// <remarks>
    /// Strict marker-by-marker monotonicity (as in
    /// <see cref="KoreanConstitutionFixtureTests.ArticleNumbersAreMonotonicInMainBody"/>)
    /// would fail on this fixture for two structural reasons that are not
    /// reading-order regressions:
    /// <list type="bullet">
    /// <item>Amendments contain "(Note: A portion of Article II ...)"
    /// cross-references in their bodies, re-using earlier numbers.</item>
    /// <item>Adjacent Article and Amendment headings get merged into one
    /// block by the layout grouper (e.g. "Article. IV. Article. V.",
    /// "Amendment VI. Amendment XII."), which surfaces a higher Roman
    /// numeral inside the same block as a lower one.</item>
    /// </list>
    /// The completeness assertion (every number from 1..N is present)
    /// still catches reading-order or extraction failures that drop
    /// entire markers, while remaining tolerant of these known structural
    /// quirks. Tighten this test once the layout grouper separates
    /// adjacent same-style headings.
    /// </remarks>
    [Fact]
    public void AllArticlesAndAmendmentsAreExtracted()
    {
        var path = FixturePath("us_constitution.pdf");
        var parser = new PdfStructParser();
        var result = parser.Parse(path);

        var pattern = new Regex(
            @"\b(Article|Amendment)\.?\s+([IVXLCDM]+)\b\.?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        var markers = result.Document.Kids
            .SelectMany(e => pattern.Matches(GetText(e)))
            .Cast<Match>()
            .Where(m => m.Success)
            .Select(m => (
                Kind: m.Groups[1].Value.ToLowerInvariant(),
                Number: ParseRoman(m.Groups[2].Value)))
            .ToList();

        Assert.NotEmpty(markers);

        var distinctArticles = markers
            .Where(x => x.Kind == "article")
            .Select(x => x.Number)
            .Distinct()
            .OrderBy(n => n)
            .ToList();
        var distinctAmendments = markers
            .Where(x => x.Kind == "amendment")
            .Select(x => x.Number)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        Assert.Equal(Enumerable.Range(1, 7), distinctArticles);
        Assert.Equal(Enumerable.Range(1, 27), distinctAmendments);

        var firstArticleIndex = markers.FindIndex(x => x.Kind == "article");
        var firstAmendmentIndex = markers.FindIndex(x => x.Kind == "amendment");
        Assert.True(firstArticleIndex >= 0 && firstAmendmentIndex > firstArticleIndex,
            "First Article reference must appear before first Amendment reference in document order.");
    }

    /// <summary>
    /// Section headings at the top of a page or column are content, not running
    /// furniture. They must survive with usable bounding boxes so JSON consumers
    /// can cite the structural boundary directly.
    /// </summary>
    [Fact]
    public void TopOfColumnSectionHeadingsKeepBoundingBoxes()
    {
        var path = FixturePath("us_constitution.pdf");
        var result = new PdfStructParser().Parse(path);

        AssertHasBox(result.Document.Kids, page: 5, text: "SECTION. 1");
        AssertHasBox(result.Document.Kids, page: 7, text: "SECTION. 1");
        AssertHasBox(result.Document.Kids, page: 8, text: "SECTION. 1");
        AssertHasBox(result.Document.Kids, page: 13, text: "SECTION 2", minLeft: 300);
        AssertHasBox(result.Document.Kids, page: 15, text: "SECTION 4", minLeft: 300);
    }

    /// <summary>Extracts the text content of any text-bearing element, or empty for non-text element types.</summary>
    private static string GetText(ContentElement element) => element switch
    {
        ParagraphElement p => p.Text.Content,
        HeadingElement h => h.Text.Content,
        CaptionElement c => c.Text.Content,
        _ => string.Empty
    };

    private static void AssertHasBox(IEnumerable<ContentElement> elements, int page, string text, double minLeft = 0)
    {
        var element = elements.FirstOrDefault(e =>
            e.PageNumber == page
            && e.BoundingBox.Left >= minLeft
            && string.Equals(GetText(e).Trim(), text, StringComparison.Ordinal));

        Assert.NotNull(element);
        Assert.True(element.BoundingBox.Width > 0 && element.BoundingBox.Height > 0,
            $"{text} on page {page} must have a positive-area bounding box.");
    }

    /// <summary>Parses a Roman numeral (I..MMMM) into its integer value. Throws on invalid input.</summary>
    private static int ParseRoman(string roman)
    {
        var total = 0;
        for (var i = 0; i < roman.Length; i++)
        {
            var current = ValueOf(roman[i]);
            var next = i + 1 < roman.Length ? ValueOf(roman[i + 1]) : 0;
            total += current < next ? -current : current;
        }
        return total;

        static int ValueOf(char c) => char.ToUpperInvariant(c) switch
        {
            'I' => 1,
            'V' => 5,
            'X' => 10,
            'L' => 50,
            'C' => 100,
            'D' => 500,
            'M' => 1000,
            _ => throw new ArgumentException($"'{c}' is not a Roman numeral character.")
        };
    }
}
