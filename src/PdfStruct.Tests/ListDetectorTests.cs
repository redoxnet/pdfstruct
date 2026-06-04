// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Analysis;
using PdfStruct.Models;
using Xunit;

namespace PdfStruct.Tests;

/// <summary>
/// Unit tests covering the Phase 1 list-detection behaviour specified in
/// <c>docs/list-detection-spec.md</c>. Tests target the spec sections in
/// order: label parsing (§ 4), run grouping (§ 5), the decimal sanity
/// filter (§ 6), and continuation absorption (§ 7).
/// </summary>
public class ListDetectorTests
{
    [Theory]
    [InlineData("1. The first item", "", 1, '.')]
    [InlineData("12) Second", "", 12, ')')]
    [InlineData("3: Third", "", 3, ':')]
    [InlineData("(1) Parenthesised", "(", 1, ')')]
    [InlineData("[1] Bracketed", "[", 1, ']')]
    [InlineData("(  12  ) Allows interior whitespace", "(", 12, ')')]
    public void TryParseLabel_RecognisesAllThreeShapes(string text, string expectedPrefix, int expectedNumber, char expectedTerminator)
    {
        var parsed = ListDetector.TryParseLabel(text);
        Assert.NotNull(parsed);
        var label = parsed!.Value.Label;
        Assert.Equal(expectedPrefix, label.Prefix);
        Assert.Equal(expectedNumber, label.Number);
        Assert.Equal(expectedTerminator, label.Terminator);
    }

    [Theory]
    [InlineData("1.5 Discussion")]
    [InlineData("3.14 Pi")]
    [InlineData("1.0 Zero")]
    public void TryParseLabel_RejectsDecimalsLikeSectionNumbers(string text)
    {
        Assert.Null(ListDetector.TryParseLabel(text));
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("")]
    [InlineData("1.")]
    [InlineData("1)")]
    [InlineData("(1)")]
    [InlineData(" 1. leading whitespace")]
    public void TryParseLabel_RejectsNonLabels(string text)
    {
        Assert.Null(ListDetector.TryParseLabel(text));
    }

    [Fact]
    public void TryParseLabel_PreservesPrintedDigitsIncludingLeadingZeros()
    {
        var parsed = ListDetector.TryParseLabel("[0001] Patent paragraph body");
        Assert.NotNull(parsed);
        Assert.Equal("0001", parsed!.Value.Label.DigitText);
        Assert.Equal(1, parsed.Value.Label.Number);
    }

    [Fact]
    public void Detect_BracketedZeroPaddedRun_IsDetectedWithVerbatimLabels()
    {
        // Patent paragraph numbers go through the same detection as any numbered
        // run; downstream they are materialised as marker-tagged paragraphs.
        var lines = new[]
        {
            Line("[0001] First patent paragraph.", left: 64, baseline: 700),
            Line("[0002] Second patent paragraph.", left: 64, baseline: 660),
            Line("[0003] Third patent paragraph.", left: 64, baseline: 620)
        };

        var result = ListDetector.Detect(lines);

        var run = Assert.Single(result.Lists);
        Assert.Equal("[0001]", run.Items[0].RawLabel);
        Assert.Equal("[0003]", run.Items[2].RawLabel);
    }

    [Fact]
    public void Detect_BracketedSingleDigitReferences_StayOrderedWithVerbatimLabels()
    {
        var lines = new[]
        {
            Line("[1] Almazan et al. Word spotting.", left: 50, baseline: 700),
            Line("[2] Bahdanau et al. Neural machine translation.", left: 50, baseline: 660),
            Line("[3] Bissacco et al. Photoocr.", left: 50, baseline: 620)
        };

        var result = ListDetector.Detect(lines);

        var list = Assert.Single(result.Lists);
        Assert.Equal("[1]", list.Items[0].RawLabel);
        Assert.Equal("[3]", list.Items[2].RawLabel);
    }

    [Fact]
    public void Detect_LargerFontHeadingBetweenItems_IsNotAbsorbed()
    {
        var lines = new[]
        {
            Line("1. First list item.", left: 64, baseline: 700, fontSize: 9),
            Line("continuation of first.", left: 64, baseline: 688, fontSize: 9),
            Line("Section Heading", left: 64, baseline: 660, fontSize: 11),
            Line("2. Second list item.", left: 64, baseline: 632, fontSize: 9),
            Line("continuation of second.", left: 64, baseline: 620, fontSize: 9)
        };

        var result = ListDetector.Detect(lines);

        var list = Assert.Single(result.Lists);
        Assert.Equal(2, list.Items.Count);
        var firstItemClaimed = list.Items[0].ClaimedLineIndices.ToHashSet();
        Assert.DoesNotContain(2, firstItemClaimed); // the heading line index
        Assert.Contains(result.ResidualLines, l => l.Text == "Section Heading");
    }

    [Fact]
    public void Detect_GroupsSequentialArabicLabels()
    {
        var lines = new[]
        {
            Line("1. Apple", left: 50, baseline: 700),
            Line("2. Banana", left: 50, baseline: 685),
            Line("3. Cherry", left: 50, baseline: 670)
        };

        var result = ListDetector.Detect(lines);

        Assert.Single(result.Lists);
        var list = result.Lists[0];
        Assert.Equal(3, list.Items.Count);
        Assert.Equal(1, list.Items[0].Number);
        Assert.Equal(2, list.Items[1].Number);
        Assert.Equal(3, list.Items[2].Number);
        Assert.Empty(result.ResidualLines);
    }

    [Fact]
    public void Detect_DropsSingletonLabel()
    {
        var lines = new[]
        {
            Line("1. Lonely", left: 50, baseline: 700),
            Line("Body paragraph that follows", left: 50, baseline: 685)
        };

        var result = ListDetector.Detect(lines);

        Assert.Empty(result.Lists);
        Assert.Equal(2, result.ResidualLines.Count);
    }

    [Fact]
    public void Detect_DoesNotMergeAcrossSkippedNumber()
    {
        var lines = new[]
        {
            Line("1. First", left: 50, baseline: 700),
            Line("2. Second", left: 50, baseline: 685),
            Line("4. Fourth — skip 3", left: 50, baseline: 670),
            Line("5. Fifth", left: 50, baseline: 655)
        };

        var result = ListDetector.Detect(lines);

        Assert.Equal(2, result.Lists.Count);
        Assert.Equal(2, result.Lists[0].Items.Count);
        Assert.Equal(2, result.Lists[1].Items.Count);
        Assert.Equal(1, result.Lists[0].Items[0].Number);
        Assert.Equal(4, result.Lists[1].Items[0].Number);
    }

    [Fact]
    public void Detect_DoesNotMergeAcrossDifferentTerminator()
    {
        var lines = new[]
        {
            Line("1. With period", left: 50, baseline: 700),
            Line("2) With paren", left: 50, baseline: 685),
            Line("3. Period again", left: 50, baseline: 670)
        };

        var result = ListDetector.Detect(lines);

        // "1." and "3." have matching terminator but skip "2.", so each is singleton.
        // "2)" is its own family with one item.
        Assert.Empty(result.Lists);
    }

    [Fact]
    public void Detect_DoesNotMergeAcrossDifferentPrefix()
    {
        var lines = new[]
        {
            Line("(1) Paren wrapped", left: 50, baseline: 700),
            Line("2. Different shape", left: 50, baseline: 685),
            Line("(2) Continues paren run", left: 50, baseline: 670)
        };

        var result = ListDetector.Detect(lines);

        Assert.Single(result.Lists);
        var list = result.Lists[0];
        Assert.Equal("(", list.CommonPrefix);
        Assert.Equal(2, list.Items.Count);
    }

    [Fact]
    public void Detect_RejectsSectionNumberRuns()
    {
        // 1.5 / 1.6 / 1.7 / 1.8 — none should be parsed as labels because
        // they look like decimal numbers; the run never forms.
        var lines = new[]
        {
            Line("1.5 Discussion", left: 50, baseline: 700),
            Line("1.6 Conclusion", left: 50, baseline: 685),
            Line("1.7 Future work", left: 50, baseline: 670)
        };

        var result = ListDetector.Detect(lines);

        Assert.Empty(result.Lists);
        Assert.Equal(3, result.ResidualLines.Count);
    }

    [Fact]
    public void Detect_RespectsAlignmentLockOnceSameLeftEstablished()
    {
        // Two items at left=50, then a third item at left=200 (way past
        // tolerance). Once two items shared left=50 within tolerance the
        // candidate locks; the third item is rejected and starts a new run.
        var lines = new[]
        {
            Line("1. Aligned", left: 50, baseline: 700),
            Line("2. Aligned", left: 50, baseline: 685),
            Line("3. Drift right", left: 200, baseline: 670)
        };

        var result = ListDetector.Detect(lines);

        Assert.Single(result.Lists);
        Assert.Equal(2, result.Lists[0].Items.Count);
    }

    [Fact]
    public void Detect_AbsorbsContinuationLineAtSameOrGreaterIndent()
    {
        var lines = new[]
        {
            Line("1. Apple", left: 50, baseline: 700, fontSize: 10, height: 12),
            Line("    pie", left: 70, baseline: 688, fontSize: 10, height: 12),
            Line("2. Banana", left: 50, baseline: 670, fontSize: 10, height: 12)
        };

        var result = ListDetector.Detect(lines);

        Assert.Single(result.Lists);
        var first = result.Lists[0].Items[0];
        Assert.Contains("pie", first.Body);
        Assert.Equal(2, first.BodyLineIndices.Count);
        Assert.Empty(first.ChildrenLineIndices);
    }

    [Fact]
    public void Detect_DoesNotAbsorbContinuationFurtherLeftThanItem()
    {
        var lines = new[]
        {
            Line("1. Apple", left: 50, baseline: 700, fontSize: 10, height: 12),
            Line("under-indented runaway", left: 20, baseline: 688, fontSize: 10, height: 12),
            Line("2. Banana", left: 50, baseline: 670, fontSize: 10, height: 12)
        };

        var result = ListDetector.Detect(lines);

        Assert.Single(result.Lists);
        Assert.Single(result.Lists[0].Items[0].ClaimedLineIndices);
        Assert.Empty(result.Lists[0].Items[0].ChildrenLineIndices);
    }

    [Fact]
    public void Detect_DoesNotAbsorbCandidateThatIsItselfALabel()
    {
        var lines = new[]
        {
            Line("1. Apple", left: 50, baseline: 700, fontSize: 10, height: 12),
            Line("(99) Different family", left: 50, baseline: 688, fontSize: 10, height: 12),
            Line("2. Banana", left: 50, baseline: 670, fontSize: 10, height: 12)
        };

        var result = ListDetector.Detect(lines);

        Assert.Single(result.Lists);
        Assert.Single(result.Lists[0].Items[0].ClaimedLineIndices);
        Assert.Empty(result.Lists[0].Items[0].ChildrenLineIndices);
    }

    [Fact]
    public void Detect_StripsLabelFromItemBody()
    {
        var lines = new[]
        {
            Line("1. The first item", left: 50, baseline: 700),
            Line("2. The second item", left: 50, baseline: 685)
        };

        var result = ListDetector.Detect(lines);

        Assert.Equal("The first item", result.Lists[0].Items[0].Body);
        Assert.Equal("The second item", result.Lists[0].Items[1].Body);
    }

    [Fact]
    public void Detect_PreservesUnclaimedLinesInResidualOrder()
    {
        var lines = new[]
        {
            Line("Heading text", left: 50, baseline: 720),
            Line("1. First", left: 50, baseline: 700),
            Line("2. Second", left: 50, baseline: 685),
            Line("Trailing paragraph", left: 50, baseline: 660)
        };

        var result = ListDetector.Detect(lines);

        Assert.Equal(2, result.ResidualLines.Count);
        Assert.Equal("Heading text", result.ResidualLines[0].Text);
        Assert.Equal("Trailing paragraph", result.ResidualLines[1].Text);
    }

    [Fact]
    public void Detect_AbsorbsNonContinuationLineAsChildOfPreviousItem()
    {
        // Body absorption fails on item 1 because the third line is too far down
        // (gap > 1.2 * line height). Phase 2 routes the third line into item 1's
        // children buffer, so the run survives and item 1 has a child paragraph.
        var lines = new[]
        {
            Line("1. Apple", left: 50, baseline: 700, fontSize: 10, height: 12),
            Line("apple body wrap", left: 50, baseline: 688, fontSize: 10, height: 12),
            Line("Far-down child paragraph that elaborates apple", left: 50, baseline: 640, fontSize: 10, height: 12),
            Line("2. Banana", left: 50, baseline: 615, fontSize: 10, height: 12)
        };

        var result = ListDetector.Detect(lines);

        Assert.Single(result.Lists);
        var list = result.Lists[0];
        Assert.Equal(2, list.Items.Count);

        var first = list.Items[0];
        Assert.Equal(2, first.BodyLineIndices.Count);
        Assert.Single(first.ChildrenLineIndices);
        Assert.Contains("apple body wrap", first.Body);
        Assert.DoesNotContain("Far-down child paragraph", first.Body);
    }

    [Fact]
    public void Detect_LastItemReceivesNoChildrenInPhase2()
    {
        // Last item's territory is unbounded (extends to end of page); to avoid
        // over-absorbing post-list content, Phase 2 leaves the last item with
        // body absorption only. The far-down line after item 2's continuation
        // remains in residual.
        var lines = new[]
        {
            Line("1. Apple", left: 50, baseline: 700, fontSize: 10, height: 12),
            Line("2. Banana", left: 50, baseline: 685, fontSize: 10, height: 12),
            Line("banana wrap", left: 50, baseline: 673, fontSize: 10, height: 12),
            Line("Far-down line after the list", left: 50, baseline: 600, fontSize: 10, height: 12)
        };

        var result = ListDetector.Detect(lines);

        Assert.Single(result.Lists);
        var list = result.Lists[0];
        Assert.Equal(2, list.Items.Count);
        var last = list.Items[1];
        Assert.Empty(last.ChildrenLineIndices);
        Assert.Single(result.ResidualLines);
        Assert.Equal("Far-down line after the list", result.ResidualLines[0].Text);
    }

    [Fact]
    public void Detect_ChildAbsorptionStopsOnSiblingLabel()
    {
        // After body absorption fails on a far-down line, child mode begins.
        // But once a sibling-style label appears, the territory walk breaks
        // and the rest stays in residual.
        var lines = new[]
        {
            Line("1. Apple", left: 50, baseline: 700, fontSize: 10, height: 12),
            Line("Far-down child paragraph", left: 50, baseline: 640, fontSize: 10, height: 12),
            Line("(99) different family label", left: 50, baseline: 625, fontSize: 10, height: 12),
            Line("2. Banana", left: 50, baseline: 600, fontSize: 10, height: 12)
        };

        var result = ListDetector.Detect(lines);

        Assert.Single(result.Lists);
        var first = result.Lists[0].Items[0];
        Assert.Single(first.ChildrenLineIndices);
        Assert.Single(result.ResidualLines);
        Assert.Equal("(99) different family label", result.ResidualLines[0].Text);
    }

    [Fact]
    public void Detect_ChildAbsorptionStopsOnLineLeftOfItem()
    {
        // After body absorption fails, child mode begins; but a line that's
        // too far to the left breaks the walk and stays in residual.
        var lines = new[]
        {
            Line("1. Apple", left: 50, baseline: 700, fontSize: 10, height: 12),
            Line("Far-down child paragraph", left: 50, baseline: 640, fontSize: 10, height: 12),
            Line("under-indented runaway", left: 10, baseline: 625, fontSize: 10, height: 12),
            Line("2. Banana", left: 50, baseline: 600, fontSize: 10, height: 12)
        };

        var result = ListDetector.Detect(lines);

        Assert.Single(result.Lists);
        var first = result.Lists[0].Items[0];
        Assert.Single(first.ChildrenLineIndices);
        Assert.Single(result.ResidualLines);
        Assert.Equal("under-indented runaway", result.ResidualLines[0].Text);
    }

    [Fact]
    public void Detect_ChildBoundingBoxExtendsItemBoundingBox()
    {
        // Item 1's bounding box must grow to cover its absorbed children, so
        // the parent list bbox transitively covers all rendered content.
        var lines = new[]
        {
            Line("1. Apple", left: 50, baseline: 700, fontSize: 10, height: 12),
            Line("Far-down child paragraph", left: 50, baseline: 640, fontSize: 10, height: 12),
            Line("2. Banana", left: 50, baseline: 600, fontSize: 10, height: 12)
        };

        var result = ListDetector.Detect(lines);

        var first = result.Lists[0].Items[0];
        Assert.True(first.BoundingBox.Bottom <= 640.0,
            $"Item 1's bounding box should reach down to its child line; bottom={first.BoundingBox.Bottom}");
    }

    private static TextLineBlock Line(
        string text,
        double left,
        double baseline,
        double fontSize = 10.0,
        double height = 12.0)
    {
        var bbox = new BoundingBox(left, baseline - 1.0, left + 200.0, baseline - 1.0 + height);
        return new TextLineBlock(
            BoundingBox: bbox,
            Text: text,
            FontName: "TestFont",
            FontSize: fontSize,
            IsBold: false,
            BaselineY: baseline,
            AvgHeight: height);
    }
}
