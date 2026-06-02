// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Runtime.InteropServices;
using Docnet.Core;
using Docnet.Core.Models;
using PdfStruct.Analysis;
using PdfStruct.Models;
using SkiaSharp;
using UglyToad.PdfPig.Content;

namespace PdfStruct.Cli;

/// <summary>
/// Renders per-page PNG overlays of extracted layout for debugging.
/// Each output image rasterises the source PDF page with PDFium (via
/// Docnet.Core) as the background and overlays the bounding boxes of
/// detected <see cref="ContentElement"/>s, color-coded by element type
/// and labeled <c>{id}:{type}</c>. Using the actual page raster makes
/// bbox positions verifiable against the visible layout — fonts,
/// embedded images, and vector graphics all render exactly as the PDF
/// would display them.
/// </summary>
internal static class DebugImageRenderer
{
    private const int TargetPageWidth = 1600;

    /// <summary>Renders one debug image per page of the supplied PDF.</summary>
    /// <param name="inputPdfPath">Path to the source PDF, opened to obtain page geometry and the rendered raster.</param>
    /// <param name="document">The parsed structured document whose elements are overlaid.</param>
    /// <param name="outputDirectory">Directory to write <c>page-NNN.png</c> files to. Created if it does not exist.</param>
    /// <returns>The output paths of every PNG written, in page order.</returns>
    public static IReadOnlyList<string> Render(
        string inputPdfPath,
        Models.PdfDocument document,
        string outputDirectory,
        IReadOnlyDictionary<int, IReadOnlyList<TextBlock>>? textLinesByPage = null)
    {
        Directory.CreateDirectory(outputDirectory);

        using var pdf = UglyToad.PdfPig.PdfDocument.Open(inputPdfPath);
        var pdfiumLib = DocLib.Instance;
        var outputFiles = new List<string>(pdf.NumberOfPages);

        for (var pageNumber = 1; pageNumber <= pdf.NumberOfPages; pageNumber++)
        {
            var page = pdf.GetPage(pageNumber);
            var elements = document.Kids
                .Where(element => element.PageNumber == pageNumber)
                .OrderBy(element => element.Id)
                .ToList();
            var textLines = textLinesByPage is not null && textLinesByPage.TryGetValue(pageNumber, out var pageTextLines)
                ? pageTextLines
                : [];

            var outputPath = Path.Combine(outputDirectory, $"page-{pageNumber:000}.png");
            RenderPage(outputPath, inputPdfPath, pdfiumLib, pageNumber, page, elements, textLines);
            outputFiles.Add(outputPath);
        }

        return outputFiles;
    }

    /// <summary>Renders a single page's overlay PNG to <paramref name="outputPath"/>.</summary>
    private static void RenderPage(
        string outputPath,
        string inputPdfPath,
        IDocLib pdfiumLib,
        int pageNumber,
        Page page,
        IReadOnlyList<ContentElement> elements,
        IReadOnlyList<TextBlock> textLines)
    {
        var mediaBox = page.MediaBox.Bounds;
        var pageWidth = mediaBox.Width;
        var pageHeight = mediaBox.Height;
        var requestedWidth = Math.Max(1, (int)Math.Ceiling(pageWidth * (float)Math.Min(2.0, TargetPageWidth / pageWidth)));
        var requestedHeight = Math.Max(1, (int)Math.Ceiling(pageHeight * (float)Math.Min(2.0, TargetPageWidth / pageWidth)));

        using var bitmap = RasterizePage(pdfiumLib, inputPdfPath, pageNumber - 1, requestedWidth, requestedHeight);
        var actualWidth = bitmap.Width;
        var actualHeight = bitmap.Height;
        // Re-derive the page-to-canvas scale from PDFium's actual returned
        // dimensions: PDFium can shave a pixel off either axis when fitting
        // the page into the requested box, and using the requested scale
        // would offset every overlaid bbox by a row/column per scan line —
        // which manifests as a diagonal drift across the page.
        var scale = (float)((double)actualWidth / pageWidth);
        using var canvas = new SKCanvas(bitmap);

        DrawPageBorder(canvas, actualWidth, actualHeight);

        foreach (var line in textLines)
        {
            DrawTextLine(canvas, line, mediaBox.Left, mediaBox.Bottom, mediaBox.Top, scale);
        }

        foreach (var element in elements)
        {
            DrawElement(canvas, element, mediaBox.Left, mediaBox.Bottom, mediaBox.Top, scale);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 95);
        using var stream = File.Create(outputPath);
        data.SaveTo(stream);
    }

    /// <summary>Strokes a pre-paragraph text-line bounding box for line-level pipeline debugging.</summary>
    private static void DrawTextLine(
        SKCanvas canvas,
        TextBlock line,
        double mediaBoxLeft,
        double mediaBoxBottom,
        double mediaBoxTop,
        float scale)
    {
        var rect = ToCanvasRect(line.BoundingBox, mediaBoxLeft, mediaBoxBottom, mediaBoxTop, scale);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        using var stroke = new SKPaint
        {
            Color = new SKColor(0, 150, 136, 190),
            IsAntialias = true,
            StrokeWidth = Math.Max(1, scale * 0.8f),
            Style = SKPaintStyle.Stroke
        };

        canvas.DrawRect(rect, stroke);
    }

    /// <summary>
    /// Rasterises a single PDF page to a fresh opaque white-backed
    /// <see cref="SKBitmap"/>. PDFium emits BGRA with the page background
    /// fully transparent, which made the resulting PNG render unreadably
    /// against any dark viewer chrome (e.g. the Windows Photos app dark
    /// theme); compositing onto an opaque white surface produces a PNG
    /// that looks the same as the source PDF would in a viewer.
    /// </summary>
    private static SKBitmap RasterizePage(IDocLib pdfiumLib, string pdfPath, int pageIndex, int width, int height)
    {
        var dimensions = new PageDimensions(width, height);
        using var docReader = pdfiumLib.GetDocReader(pdfPath, dimensions);
        using var pageReader = docReader.GetPageReader(pageIndex);
        // Use PDFium's actual rendered page size, not the requested dimensions.
        // PDFium may produce a slightly smaller image (off by 1 pixel on either
        // axis) while still filling the byte buffer at its own dimensions —
        // allocating against the requested size leaves stale bytes per row and
        // shears the content diagonally.
        var actualWidth = pageReader.GetPageWidth();
        var actualHeight = pageReader.GetPageHeight();
        var rawBytes = pageReader.GetImage();

        using var pageBitmap = new SKBitmap(new SKImageInfo(actualWidth, actualHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        Marshal.Copy(rawBytes, 0, pageBitmap.GetPixels(), rawBytes.Length);

        var canvasBitmap = new SKBitmap(new SKImageInfo(actualWidth, actualHeight, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(canvasBitmap);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(pageBitmap, 0, 0);
        return canvasBitmap;
    }

    /// <summary>Strokes a thin rectangle around the page bounds.</summary>
    private static void DrawPageBorder(SKCanvas canvas, int width, int height)
    {
        using var paint = new SKPaint
        {
            Color = new SKColor(30, 30, 30),
            IsAntialias = true,
            StrokeWidth = 2,
            Style = SKPaintStyle.Stroke
        };

        canvas.DrawRect(new SKRect(1, 1, width - 2, height - 2), paint);
    }

    /// <summary>Fills, strokes, and labels the bounding box of one structured element. Skips elements with a non-positive area on the canvas.</summary>
    private static void DrawElement(
        SKCanvas canvas,
        ContentElement element,
        double mediaBoxLeft,
        double mediaBoxBottom,
        double mediaBoxTop,
        float scale)
    {
        var rect = ToCanvasRect(element.BoundingBox, mediaBoxLeft, mediaBoxBottom, mediaBoxTop, scale);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        var color = GetColor(element.Type);
        using var fill = new SKPaint
        {
            Color = color.WithAlpha(45),
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };
        using var stroke = new SKPaint
        {
            Color = color,
            IsAntialias = true,
            StrokeWidth = Math.Max(2, scale),
            Style = SKPaintStyle.Stroke
        };

        canvas.DrawRect(rect, fill);
        canvas.DrawRect(rect, stroke);

        // Reveal the structure we actually assert: a grid table's confident column
        // anchors (vertical) and row boundaries (horizontal) as guide lines. A
        // region/block asserts neither, so nothing is drawn inside it — an
        // uncertain row or column is never visualised as if it were real structure.
        if (element is TableElement table)
        {
            DrawStructureGuides(canvas, table, rect, mediaBoxLeft, mediaBoxTop, scale, color);
        }

        DrawLabel(canvas, rect, element, color);
    }

    /// <summary>Draws dashed guides at a grid table's confident column anchors (vertical) and row boundaries (horizontal), clipped to its bounding box.</summary>
    private static void DrawStructureGuides(
        SKCanvas canvas, TableElement table, SKRect rect, double mediaBoxLeft, double mediaBoxTop, float scale, SKColor color)
    {
        using var guide = new SKPaint
        {
            Color = color.WithAlpha(160),
            IsAntialias = true,
            StrokeWidth = Math.Max(1, scale * 0.6f),
            Style = SKPaintStyle.Stroke,
            PathEffect = SKPathEffect.CreateDash([scale * 4, scale * 3], 0)
        };

        foreach (var anchor in table.ColumnAnchors)
        {
            var x = (float)((anchor - mediaBoxLeft) * scale);
            if (x <= rect.Left || x >= rect.Right) continue;
            canvas.DrawLine(x, rect.Top, x, rect.Bottom, guide);
        }

        foreach (var anchor in table.RowAnchors)
        {
            var y = (float)((mediaBoxTop - anchor) * scale);
            if (y <= rect.Top || y >= rect.Bottom) continue;
            canvas.DrawLine(rect.Left, y, rect.Right, y, guide);
        }
    }

    /// <summary>Draws the <c>{id}:{type}</c> label tab above an element's bounding box.</summary>
    private static void DrawLabel(SKCanvas canvas, SKRect rect, ContentElement element, SKColor color)
    {
        var label = $"{element.Id}:{element.Type}";
        using var font = new SKFont(SKTypeface.Default, 14);
        using var textPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true
        };
        using var backgroundPaint = new SKPaint
        {
            Color = color.WithAlpha(230),
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };

        var textWidth = font.MeasureText(label);
        var labelRect = new SKRect(
            rect.Left,
            Math.Max(0, rect.Top - 18),
            rect.Left + textWidth + 8,
            Math.Max(18, rect.Top));

        canvas.DrawRect(labelRect, backgroundPaint);
        canvas.DrawText(label, labelRect.Left + 4, labelRect.Bottom - 4, SKTextAlign.Left, font, textPaint);
    }

    /// <summary>
    /// Converts a PDF-space bounding box (origin bottom-left, absolute
    /// user-space coordinates) to a canvas-space rectangle (origin top-left,
    /// MediaBox-relative). Subtracts the MediaBox origin so PDFs whose page
    /// is offset from <c>(0, 0)</c> in user space still align with the
    /// PDFium raster.
    /// </summary>
    private static SKRect ToCanvasRect(BoundingBox box, double mediaBoxLeft, double mediaBoxBottom, double mediaBoxTop, float scale)
    {
        var left = (float)((box.Left - mediaBoxLeft) * scale);
        var right = (float)((box.Right - mediaBoxLeft) * scale);
        // Y inversion: the canvas origin is at the top, PDF origin is at
        // bottom-left of the MediaBox. Distance from canvas top equals
        // (mediaBoxTop - boxEdge) since both are measured in user space.
        var top = (float)((mediaBoxTop - box.Top) * scale);
        var bottom = (float)((mediaBoxTop - box.Bottom) * scale);
        return new SKRect(left, top, right, bottom);
    }

    /// <summary>Returns the overlay color for a given element type. Falls back to teal for unrecognized types.</summary>
    private static SKColor GetColor(string elementType) =>
        elementType switch
        {
            "heading" => new SKColor(214, 69, 65),
            "paragraph" => new SKColor(45, 120, 210),
            "table" => new SKColor(38, 166, 91),
            "region" => new SKColor(127, 140, 141),
            "list" => new SKColor(142, 68, 173),
            "image" => new SKColor(90, 90, 90),
            "caption" => new SKColor(230, 126, 34),
            "header" or "footer" => new SKColor(120, 90, 50),
            _ => new SKColor(20, 150, 140)
        };
}
