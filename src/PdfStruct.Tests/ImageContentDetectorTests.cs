// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct;
using PdfStruct.Analysis;
using PdfStruct.Models;
using Xunit;

namespace PdfStruct.Tests;

public class ImageContentDetectorTests
{
    private static readonly BoundingBox Page = new(0, 0, 600, 800);

    [Fact]
    public void TryAcceptImage_InsidePage_AcceptedUnchanged()
    {
        var raw = new BoundingBox(100, 100, 300, 300);
        Assert.True(ImageContentDetector.TryAcceptImage(raw, Page, out var clamped));
        Assert.Equal(raw, clamped);
    }

    [Fact]
    public void TryAcceptImage_BleedingPastPage_ClampedToCropBox()
    {
        // Placement rectangle bleeds left and below the page; the emitted box
        // is the visible intersection, not the drawn rectangle.
        var raw = new BoundingBox(-50, -40, 200, 300);
        Assert.True(ImageContentDetector.TryAcceptImage(raw, Page, out var clamped));
        Assert.Equal(new BoundingBox(0, 0, 200, 300), clamped);
    }

    [Fact]
    public void TryAcceptImage_FullyOffPage_Rejected()
    {
        var raw = new BoundingBox(700, 100, 820, 300);
        Assert.False(ImageContentDetector.TryAcceptImage(raw, Page, out _));
    }

    [Fact]
    public void TryAcceptImage_PageCoveringBackdrop_Rejected()
    {
        var raw = new BoundingBox(0, 0, 600, 800);
        Assert.False(ImageContentDetector.TryAcceptImage(raw, Page, out _));
    }

    [Fact]
    public void TryAcceptImage_HairlineRule_Rejected()
    {
        // 400pt wide, 1pt tall: aspect ratio 1/400 < 0.01.
        var raw = new BoundingBox(100, 400, 500, 401);
        Assert.False(ImageContentDetector.TryAcceptImage(raw, Page, out _));
    }

    [Fact]
    public void TryAcceptImage_Speck_Rejected()
    {
        var raw = new BoundingBox(100, 100, 105, 105);
        Assert.False(ImageContentDetector.TryAcceptImage(raw, Page, out _));
    }

    [Fact]
    public void TryAcceptImage_DegeneratePageBounds_Rejected()
    {
        Assert.False(ImageContentDetector.TryAcceptImage(new BoundingBox(0, 0, 100, 100), new BoundingBox(0, 0, 0, 0), out _));
    }

    [Fact]
    public void Parse_DefaultOptions_EmitsNoImages()
    {
        // ImageOutput defaults to Off — extraction stays text-only, so existing
        // output is unchanged.
        var result = ParseFixture("plos_game_based_education.pdf", ImageOutputMode.Off);
        Assert.DoesNotContain(result.Document.Kids, e => e is ImageElement);
    }

    [Fact]
    public void Parse_WithImageOutput_EmitsImageElementsWithinPage()
    {
        var result = ParseFixture("plos_game_based_education.pdf", ImageOutputMode.External);

        var images = result.Document.Kids.OfType<ImageElement>().ToList();
        Assert.NotEmpty(images);
        foreach (var image in images)
        {
            Assert.True(image.BoundingBox.Area > 0, "Image bounding box must have positive area.");
            Assert.True(image.PageNumber >= 1);
            Assert.Null(image.Source); // Phase A does not persist bytes yet.
        }
    }

    private static PdfStructResult ParseFixture(string fixtureName, ImageOutputMode mode)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName);
        Assert.True(File.Exists(path), $"Fixture missing on disk: {path}");
        var parser = new PdfStructParser(new PdfStructOptions { ImageOutput = mode });
        return parser.Parse(path);
    }
}
