// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Analysis;
using PdfStruct.Models;
using PdfStruct.Rendering;
using UglyToad.PdfPig.Content;
using ZXing;
using ZXing.Common;

namespace PdfStruct.ZXing;

/// <summary>
/// An <see cref="ICodeDecoder"/> backed by ZXing.Net that reads codes from a
/// rasterised page.
/// </summary>
/// <remarks>
/// Codes are frequently drawn as vector graphics or stored in image filters
/// PdfPig cannot export, so reading the extracted image XObjects misses them.
/// Instead the page is rendered to pixels through a shared
/// <see cref="IPageRasterizer"/> and scanned whole by ZXing; each result's pixel
/// position is mapped back to PDF space so <see cref="MachineReadableCodeDetector"/>
/// can tag the matching raster image or synthesise a code element for a
/// vector-drawn code.
/// </remarks>
public sealed class ZXingCodeDecoder : ICodeDecoder
{
    /// <summary>Minimum emitted code dimension in PDF points, so a 1-D barcode (whose decoder returns two collinear endpoints) gets a usable box.</summary>
    private const double MinCodeDimension = 6.0;

    // Document-oriented formats only. Retail symbologies (UPC/EAN) and the
    // loosely-checksummed 1-D formats (ITF, Codabar) are excluded: they are
    // absent from documents and notorious for matching runs of body text and
    // table rules, producing phantom codes when the whole page is scanned.
    private static readonly IReadOnlyList<BarcodeFormat> SupportedFormats =
    [
        BarcodeFormat.QR_CODE,
        BarcodeFormat.DATA_MATRIX,
        BarcodeFormat.AZTEC,
        BarcodeFormat.PDF_417,
        BarcodeFormat.CODE_128,
        BarcodeFormat.CODE_39,
        BarcodeFormat.CODE_93,
    ];

    private readonly IPageRasterizer _rasterizer;

    private readonly BarcodeReaderGeneric _reader = new()
    {
        AutoRotate = true,
        Options = new DecodingOptions
        {
            TryHarder = true,
            PossibleFormats = [.. SupportedFormats],
        },
    };

    /// <summary>Initializes the decoder with a default PDFium-backed rasteriser.</summary>
    public ZXingCodeDecoder() : this(new PdfPageRenderer()) { }

    /// <summary>Initializes the decoder with a shared page rasteriser.</summary>
    /// <param name="rasterizer">The rasteriser used to render pages for scanning.</param>
    public ZXingCodeDecoder(IPageRasterizer rasterizer) => _rasterizer = rasterizer;

    /// <inheritdoc />
    public IReadOnlyList<DecodedCode> Decode(byte[] pdfBytes, int pageNumber, Page page)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        ArgumentNullException.ThrowIfNull(page);
        if (page.Width <= 0 || page.Height <= 0) return [];

        var raster = _rasterizer.Render(pdfBytes, pageNumber);
        if (raster is null) return [];

        var results = _reader.DecodeMultiple(raster.Bgra, raster.Width, raster.Height, RGBLuminanceSource.BitmapFormat.BGRA32);
        if (results is null || results.Length == 0) return [];

        var scaleX = raster.Width / page.Width;
        var scaleY = raster.Height / page.Height;

        var codes = new List<DecodedCode>(results.Length);
        foreach (var result in results)
        {
            if (string.IsNullOrEmpty(result.Text) || result.ResultPoints is not { Length: > 0 }) continue;
            var (role, codeType, text) = MapFormat(result.BarcodeFormat, result.Text);
            codes.Add(new DecodedCode(ToPdfBox(result.ResultPoints, scaleX, scaleY, page.Height), role, codeType, text));
        }
        return codes;
    }

    /// <summary>
    /// Maps a code's pixel-space result points (origin top-left) back to a
    /// PDF-space bounding box (origin bottom-left), padding to a minimum size so
    /// that 1-D barcodes — reported as two collinear endpoints — get a box with
    /// real area.
    /// </summary>
    private static BoundingBox ToPdfBox(ResultPoint[] points, double scaleX, double scaleY, double pageHeight)
    {
        float minX = points[0].X, maxX = points[0].X, minY = points[0].Y, maxY = points[0].Y;
        foreach (var p in points)
        {
            minX = Math.Min(minX, p.X);
            maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y);
            maxY = Math.Max(maxY, p.Y);
        }

        var left = minX / scaleX;
        var right = maxX / scaleX;
        // Pixel Y grows downward; PDF Y grows upward, so the smaller pixel Y is the higher (top) edge.
        var top = pageHeight - (minY / scaleY);
        var bottom = pageHeight - (maxY / scaleY);

        if (right - left < MinCodeDimension)
        {
            var cx = (left + right) / 2.0;
            left = cx - MinCodeDimension / 2.0;
            right = cx + MinCodeDimension / 2.0;
        }
        if (top - bottom < MinCodeDimension)
        {
            var cy = (top + bottom) / 2.0;
            bottom = cy - MinCodeDimension / 2.0;
            top = cy + MinCodeDimension / 2.0;
        }

        return new BoundingBox(left, bottom, right, top);
    }

    /// <summary>Maps a ZXing format to PdfStruct's image role and code-type label.</summary>
    internal static (string Role, string CodeType, string Text) MapFormat(BarcodeFormat format, string text)
    {
        var codeType = format.ToString().ToLowerInvariant().Replace('_', '-');
        var role = format == BarcodeFormat.QR_CODE ? "qr-code" : "barcode";
        return (role, codeType, text);
    }
}
