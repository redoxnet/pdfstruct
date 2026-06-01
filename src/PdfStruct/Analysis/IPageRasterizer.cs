// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Models;

namespace PdfStruct.Analysis;

/// <summary>
/// Renders a PDF page to pixels. The core declares the contract; a concrete
/// rasteriser (PDFium-backed) lives in a separate adapter so the rendering
/// dependency stays out of the dependency-light core. Both machine-readable
/// code scanning and image-region extraction consume this.
/// </summary>
public interface IPageRasterizer
{
    /// <summary>
    /// Renders a page to a BGRA pixel buffer.
    /// </summary>
    /// <param name="pdfBytes">The whole PDF document's bytes.</param>
    /// <param name="pageNumber">The 1-indexed page to render.</param>
    /// <returns>The rendered page, or <c>null</c> when rendering fails.</returns>
    RasterPage? Render(byte[] pdfBytes, int pageNumber);
}

/// <summary>A rendered page as a BGRA8888 pixel buffer with its pixel dimensions.</summary>
/// <param name="Bgra">Row-major BGRA pixels, length <c>Width * Height * 4</c>.</param>
/// <param name="Width">Rendered width in pixels.</param>
/// <param name="Height">Rendered height in pixels.</param>
public sealed record RasterPage(byte[] Bgra, int Width, int Height);

/// <summary>
/// Extracts a page region as an encoded image. Used to persist an image
/// element's pixels by cropping the rendered page to the element's placement
/// box — universal across PDF image filters, and emitting exactly the visible
/// region rather than the raw (possibly oversized) source bitmap.
/// </summary>
public interface IImageRasterizer
{
    /// <summary>
    /// Renders the page and crops <paramref name="region"/> to a PNG.
    /// </summary>
    /// <param name="pdfBytes">The whole PDF document's bytes.</param>
    /// <param name="pageNumber">The 1-indexed page.</param>
    /// <param name="region">The region to crop, in PDF user space.</param>
    /// <param name="pageWidth">Page width in PDF points, for pixel scaling.</param>
    /// <param name="pageHeight">Page height in PDF points, for pixel scaling and the Y-axis flip.</param>
    /// <returns>PNG bytes of the cropped region, or <c>null</c> when rendering or cropping fails.</returns>
    byte[]? RenderRegionPng(byte[] pdfBytes, int pageNumber, BoundingBox region, double pageWidth, double pageHeight);
}
