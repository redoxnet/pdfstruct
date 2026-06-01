// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Analysis;
using PdfStruct.Models;
using SkiaSharp;
using UglyToad.PdfPig.Content;
using ZXing;
using ZXing.Common;

namespace PdfStruct.ZXing;

/// <summary>
/// An <see cref="ICodeDecoder"/> backed by ZXing.Net.
/// </summary>
/// <remarks>
/// This pass reads codes stored as raster image XObjects by rendering each to
/// BGRA pixels through <see cref="SKBitmap"/>. It does not yet cover codes drawn
/// as vector graphics or stored in image filters PdfPig cannot export; a
/// page-rasterising pass for those is added separately.
/// </remarks>
public sealed class ZXingCodeDecoder : ICodeDecoder
{
    private static readonly IReadOnlyList<BarcodeFormat> SupportedFormats =
    [
        BarcodeFormat.QR_CODE,
        BarcodeFormat.DATA_MATRIX,
        BarcodeFormat.AZTEC,
        BarcodeFormat.PDF_417,
        BarcodeFormat.CODE_128,
        BarcodeFormat.CODE_39,
        BarcodeFormat.CODE_93,
        BarcodeFormat.CODABAR,
        BarcodeFormat.ITF,
        BarcodeFormat.EAN_13,
        BarcodeFormat.EAN_8,
        BarcodeFormat.UPC_A,
        BarcodeFormat.UPC_E,
    ];

    private readonly BarcodeReaderGeneric _reader = new()
    {
        AutoRotate = true,
        Options = new DecodingOptions
        {
            TryHarder = true,
            PossibleFormats = [.. SupportedFormats],
        },
    };

    /// <inheritdoc />
    public IReadOnlyList<DecodedCode> Decode(byte[] pdfBytes, int pageNumber, Page page)
    {
        ArgumentNullException.ThrowIfNull(page);

        List<DecodedCode>? codes = null;
        foreach (var image in page.GetImages())
        {
            var hit = TryDecode(image);
            if (hit is null) continue;

            var b = image.BoundingBox;
            codes ??= [];
            codes.Add(new DecodedCode(
                new BoundingBox(b.Left, b.Bottom, b.Right, b.Top),
                hit.Value.Role,
                hit.Value.CodeType,
                hit.Value.Text));
        }

        return codes ?? (IReadOnlyList<DecodedCode>)[];
    }

    /// <summary>Renders an image to BGRA pixels and attempts to read a single code from it.</summary>
    private (string Role, string CodeType, string Text)? TryDecode(IPdfImage image)
    {
        byte[]? png;
        try
        {
            if (!image.TryGetPng(out png) || png is null || png.Length == 0) return null;
        }
        catch
        {
            return null;
        }

        using var decoded = SKBitmap.Decode(png);
        if (decoded is null) return null;

        SKBitmap? converted = null;
        var bgra = decoded;
        if (decoded.ColorType != SKColorType.Bgra8888)
        {
            converted = decoded.Copy(SKColorType.Bgra8888);
            if (converted is null) return null;
            bgra = converted;
        }

        try
        {
            var result = _reader.Decode(bgra.Bytes, bgra.Width, bgra.Height, RGBLuminanceSource.BitmapFormat.BGRA32);
            if (result is null || string.IsNullOrEmpty(result.Text)) return null;
            return MapFormat(result.BarcodeFormat, result.Text);
        }
        finally
        {
            converted?.Dispose();
        }
    }

    /// <summary>Maps a ZXing format to PdfStruct's image role and code-type label.</summary>
    internal static (string Role, string CodeType, string Text) MapFormat(BarcodeFormat format, string text)
    {
        var codeType = format.ToString().ToLowerInvariant().Replace('_', '-');
        var role = format == BarcodeFormat.QR_CODE ? "qr-code" : "barcode";
        return (role, codeType, text);
    }
}
