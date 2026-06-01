// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Runtime.InteropServices;
using Docnet.Core;
using Docnet.Core.Models;
using PdfStruct.Analysis;
using PdfStruct.Models;
using SkiaSharp;

namespace PdfStruct.Rendering;

/// <summary>
/// A PDFium-backed (<see cref="Docnet.Core"/>) page rasteriser that renders a
/// page to BGRA pixels and crops regions to PNG via <see cref="SKBitmap"/>.
/// Implements both <see cref="IPageRasterizer"/> (whole-page pixels, for code
/// scanning) and <see cref="IImageRasterizer"/> (region crop, for image
/// persistence), caching the most recently rendered page so several images on
/// one page and a code scan share a single render.
/// </summary>
public sealed class PdfPageRenderer : IPageRasterizer, IImageRasterizer
{
    /// <summary>Render scale (page points → pixels). ~3x ≈ 216 DPI.</summary>
    private const double RenderScale = 3.0;

    private readonly object _gate = new();
    private byte[]? _cachedPdf;
    private int _cachedPage = -1;
    private RasterPage? _cached;

    /// <inheritdoc />
    public RasterPage? Render(byte[] pdfBytes, int pageNumber)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        if (pdfBytes.Length == 0) return null;

        lock (_gate)
        {
            if (_cached is not null && ReferenceEquals(_cachedPdf, pdfBytes) && _cachedPage == pageNumber)
                return _cached;

            try
            {
                using var docReader = DocLib.Instance.GetDocReader(pdfBytes, new PageDimensions(RenderScale));
                using var pageReader = docReader.GetPageReader(pageNumber - 1);
                var width = pageReader.GetPageWidth();
                var height = pageReader.GetPageHeight();
                var pixels = pageReader.GetImage();
                if (width <= 0 || height <= 0 || pixels.Length < (long)width * height * 4)
                    return null;

                _cached = new RasterPage(pixels, width, height);
                _cachedPdf = pdfBytes;
                _cachedPage = pageNumber;
                return _cached;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <inheritdoc />
    public byte[]? RenderRegionPng(byte[] pdfBytes, int pageNumber, BoundingBox region, double pageWidth, double pageHeight)
    {
        if (pageWidth <= 0 || pageHeight <= 0) return null;
        var page = Render(pdfBytes, pageNumber);
        if (page is null) return null;

        var scaleX = page.Width / pageWidth;
        var scaleY = page.Height / pageHeight;

        // PDF Y grows upward; pixel Y grows downward.
        var left = (int)Math.Floor(region.Left * scaleX);
        var right = (int)Math.Ceiling(region.Right * scaleX);
        var top = (int)Math.Floor((pageHeight - region.Top) * scaleY);
        var bottom = (int)Math.Ceiling((pageHeight - region.Bottom) * scaleY);

        left = Math.Clamp(left, 0, page.Width);
        right = Math.Clamp(right, 0, page.Width);
        top = Math.Clamp(top, 0, page.Height);
        bottom = Math.Clamp(bottom, 0, page.Height);
        if (right - left < 1 || bottom - top < 1) return null;

        var info = new SKImageInfo(page.Width, page.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var full = new SKBitmap();
        var handle = GCHandle.Alloc(page.Bgra, GCHandleType.Pinned);
        try
        {
            full.InstallPixels(info, handle.AddrOfPinnedObject(), info.RowBytes);
            using var subset = new SKBitmap();
            if (!full.ExtractSubset(subset, new SKRectI(left, top, right, bottom))) return null;
            using var image = SKImage.FromBitmap(subset);
            using var data = image.Encode(SKEncodedImageFormat.Png, 95);
            return data?.ToArray();
        }
        catch
        {
            return null;
        }
        finally
        {
            handle.Free();
        }
    }
}
