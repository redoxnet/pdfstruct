// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using UglyToad.PdfPig.Content;

namespace PdfStruct.Analysis;

/// <summary>
/// Recognises machine-readable codes (QR codes, barcodes) on a page and
/// promotes them from generic figures to coded images carrying their decoded
/// payload.
/// </summary>
/// <remarks>
/// The core library defines only this contract; concrete implementations live
/// in a separate adapter so the imaging and decoding dependencies (a barcode
/// engine, a page rasteriser) stay out of the dependency-light core. A decoder
/// receives the page's already-detected raster images and returns an augmented
/// list: raster entries that decode come back with their role and decoded text
/// set, and codes drawn as vector graphics — which never appear among the
/// page's raster images — are appended as new entries located by rasterising
/// the page. Because the augmented list is produced before reading-order
/// analysis, synthesised vector codes are placed in reading order alongside
/// every other element.
/// </remarks>
public interface ICodeDecoder
{
    /// <summary>
    /// Recognises machine-readable codes on a page.
    /// </summary>
    /// <param name="page">The source page, available for rasterising vector-drawn codes.</param>
    /// <param name="images">The page's detected content images, in page order.</param>
    /// <returns>
    /// The images with any recognised codes tagged (role, symbology, decoded
    /// text, provenance), plus appended entries for vector-drawn codes. Returns
    /// the input unchanged when no codes are found.
    /// </returns>
    IReadOnlyList<DetectedImage> Decode(Page page, IReadOnlyList<DetectedImage> images);
}
