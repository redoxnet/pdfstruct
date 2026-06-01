// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Models;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace PdfStruct.Analysis;

/// <summary>
/// Selects the embedded images on a page that should surface as first-class
/// <see cref="Models.ImageElement"/> content, and reconciles each one's
/// placement rectangle with the visible page so the emitted bounding box is
/// citation-grounded.
/// </summary>
/// <remarks>
/// PdfPig reports an image's placement rectangle in page space — the size it
/// is drawn at, not its intrinsic sample dimensions — which may extend past
/// the crop box ("bleeding"). The detector clamps that rectangle to the crop
/// bounds and then rejects three classes of non-content image: full-page
/// backdrops (scanned-page rasters, paper textures), hairline rules disguised
/// as images, and sub-glyph specks. The same backdrop ratio is used by the
/// layout-obstacle filter so content emission and whitespace analysis agree on
/// what counts as a page-covering image.
/// </remarks>
public static class ImageContentDetector
{
    /// <summary>Images whose clamped area is at least this fraction of the visible page are treated as backdrops, not content.</summary>
    public const double BackdropAreaRatio = 0.8;

    /// <summary>Images whose shorter side is less than this fraction of their longer side are treated as hairline rules, not content.</summary>
    public const double SubtleAspectRatio = 0.01;

    /// <summary>Images whose clamped width or height is below this many PDF points are treated as specks, not content.</summary>
    public const double MinimumDimensionPoints = 8.0;

    /// <summary>
    /// Returns the clamped bounding boxes of the page's content images, paired
    /// with their source <see cref="IPdfImage"/>, in page order.
    /// </summary>
    /// <param name="page">The page whose images to inspect.</param>
    /// <returns>One entry per accepted image; empty when the page has none or its bounds are degenerate.</returns>
    public static IReadOnlyList<DetectedImage> DetectContentImages(Page page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var pageBounds = ToBoundingBox(page.CropBox.Bounds);
        if (pageBounds.Area <= 0) return [];

        var result = new List<DetectedImage>();
        foreach (var image in page.GetImages())
        {
            if (TryAcceptImage(ToBoundingBox(image.BoundingBox), pageBounds, out var clamped))
                result.Add(new DetectedImage(clamped, image));
        }
        return result;
    }

    /// <summary>
    /// Clamps a raw image placement rectangle to the visible page and decides
    /// whether it is content. Pure geometry so the policy can be unit-tested
    /// without a PDF.
    /// </summary>
    /// <param name="raw">The image's placement rectangle in page space, possibly bleeding past the page.</param>
    /// <param name="pageBounds">The visible page (crop-box) bounds.</param>
    /// <param name="clamped">The placement rectangle intersected with the page; valid only when the method returns <c>true</c>.</param>
    /// <returns><c>true</c> when the clamped image should surface as content.</returns>
    internal static bool TryAcceptImage(BoundingBox raw, BoundingBox pageBounds, out BoundingBox clamped)
    {
        clamped = default;
        if (pageBounds.Area <= 0) return false;

        var left = Math.Max(raw.Left, pageBounds.Left);
        var bottom = Math.Max(raw.Bottom, pageBounds.Bottom);
        var right = Math.Min(raw.Right, pageBounds.Right);
        var top = Math.Min(raw.Top, pageBounds.Top);

        // Off-page: the placement rectangle does not intersect the visible page.
        if (right <= left || top <= bottom) return false;

        var box = new BoundingBox(left, bottom, right, top);

        // Speck: a sub-glyph dot, decorative bullet, or extraction artefact.
        if (box.Width < MinimumDimensionPoints || box.Height < MinimumDimensionPoints) return false;

        // Hairline rule masquerading as an image.
        var longSide = Math.Max(box.Width, box.Height);
        var shortSide = Math.Min(box.Width, box.Height);
        if (longSide > 0 && shortSide / longSide < SubtleAspectRatio) return false;

        // Backdrop: a page-covering raster (scanned page, paper texture).
        if (box.Area >= pageBounds.Area * BackdropAreaRatio) return false;

        clamped = box;
        return true;
    }

    /// <summary>Converts a PdfPig <see cref="PdfRectangle"/> to a model <see cref="BoundingBox"/>.</summary>
    private static BoundingBox ToBoundingBox(PdfRectangle rect) =>
        new(rect.Left, rect.Bottom, rect.Right, rect.Top);
}

/// <summary>
/// A page image accepted as content: its bounding box clamped to the visible
/// page, the source <see cref="IPdfImage"/> for later byte extraction (absent
/// for codes synthesised from vector graphics), and the semantic-role fields an
/// <see cref="ICodeDecoder"/> fills in when the image is a machine-readable code.
/// </summary>
/// <param name="BoundingBox">The placement rectangle clamped to the crop box.</param>
/// <param name="Source">The originating PdfPig image, or <c>null</c> for a vector-drawn code located by rasterising the page.</param>
/// <param name="Role">The semantic role: <c>"figure"</c> (default), <c>"qr-code"</c>, or <c>"barcode"</c>.</param>
/// <param name="CodeType">The code symbology (e.g. <c>"qr-code"</c>, <c>"code-128"</c>) when this is a code; otherwise <c>null</c>.</param>
/// <param name="DecodedText">The decoded code payload, or <c>null</c>.</param>
/// <param name="AltSource">Provenance of any associated text: <c>"original"</c>, <c>"machine-decoded"</c>, or <c>"missing"</c>.</param>
public readonly record struct DetectedImage(
    BoundingBox BoundingBox,
    IPdfImage? Source,
    string Role = "figure",
    string? CodeType = null,
    string? DecodedText = null,
    string? AltSource = null);
