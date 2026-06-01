// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Models;
using UglyToad.PdfPig.Content;

namespace PdfStruct.Analysis;

/// <summary>
/// Locates and decodes machine-readable codes (QR codes, barcodes) on a page.
/// </summary>
/// <remarks>
/// The core library defines only this contract and the
/// <see cref="DecodedCode"/> result; a concrete decoder lives in a separate
/// adapter so its imaging and decoding dependencies (a barcode engine, a page
/// rasteriser) stay out of the dependency-light core. The decoder is handed the
/// page's raw PDF bytes — codes are frequently drawn as vector graphics or
/// stored in image filters PdfPig cannot export, so reliable recognition needs
/// a rendered page, not the extracted image XObjects. Attaching the decoded
/// codes back onto the page's images (tagging a raster image, or synthesising
/// an element for a vector code) is the decoder-independent job of
/// <see cref="MachineReadableCodeDetector"/>.
/// </remarks>
public interface ICodeDecoder
{
    /// <summary>
    /// Finds the machine-readable codes on a single page.
    /// </summary>
    /// <param name="pdfBytes">The whole PDF document's bytes, for rasterising the page.</param>
    /// <param name="pageNumber">The 1-indexed page to decode.</param>
    /// <param name="page">The parsed page, supplying its point dimensions for mapping decoded pixel positions back to PDF space.</param>
    /// <returns>The codes found on the page, each with a PDF-space bounding box; empty when none are found.</returns>
    IReadOnlyList<DecodedCode> Decode(byte[] pdfBytes, int pageNumber, Page page);
}

/// <summary>
/// A machine-readable code recognised on a page: where it sits, what kind it
/// is, and its decoded payload.
/// </summary>
/// <param name="BoundingBox">The code's bounding box in PDF user space.</param>
/// <param name="Role">The image role to assign: <c>"qr-code"</c> or <c>"barcode"</c>.</param>
/// <param name="CodeType">The specific symbology (e.g. <c>"qr-code"</c>, <c>"code-128"</c>, <c>"code-39"</c>).</param>
/// <param name="DecodedText">The decoded payload.</param>
public readonly record struct DecodedCode(BoundingBox BoundingBox, string Role, string CodeType, string DecodedText);
