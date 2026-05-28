// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Globalization;
using System.Runtime.InteropServices;
using Docnet.Core;
using Docnet.Core.Models;
using PdfStruct;
using PdfStruct.Analysis;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using PdfPigTextBlock = UglyToad.PdfPig.DocumentLayoutAnalysis.TextBlock;
using PdfStructTextBlock = PdfStruct.Analysis.TextBlock;

namespace PdfStruct.Cli;

/// <summary>
/// Implements the <c>bench-segmenter</c> verb: runs PdfPig's bundled page
/// segmenters (RecursiveXYCut, Docstrum, WhitespaceCover) and PdfStruct's own
/// block partition against the same PDF, sharing the <see cref="LetterGrouper"/>
/// word stream as input. Writes a per-page debug PNG per algorithm and a
/// summary stats table, so the relative quality of the candidates can be
/// inspected visually before any of them is wired into the production pipeline.
/// </summary>
/// <remarks>
/// Step 0 scope: validate that the harness runs end-to-end on a single fixture
/// and produces the four output trees. Apples-to-apples metrics
/// (column-bleed, fragmentation) are added in Step 1; for now the summary
/// only reports block / whitespace counts and basic geometry.
/// </remarks>
internal static class BenchSegmenterCommand
{
    private const int TargetPageWidth = 1600;

    /// <summary>Identifier-to-display-name mapping for the candidates the verb evaluates.</summary>
    private static readonly (string Id, string Display)[] Algorithms =
    {
        ("recursive-xycut", "RecursiveXYCut"),
        ("docstrum", "DocstrumBoundingBoxes"),
        ("whitespace-cover", "WhitespaceCoverExtractor"),
        ("structural-cuts", "StructuralCuts"),
        ("ours", "PdfStruct"),
    };

    /// <summary>Stroke color per algorithm overlay.</summary>
    private static readonly Dictionary<string, SKColor> AlgorithmColors = new()
    {
        ["recursive-xycut"] = new SKColor(214, 69, 65),
        ["docstrum"] = new SKColor(45, 120, 210),
        ["whitespace-cover"] = new SKColor(0, 188, 212),
        ["structural-cuts"] = new SKColor(170, 50, 50),
        ["ours"] = new SKColor(38, 166, 91),
    };

    /// <summary>Color used to highlight whitespace rectangles classified as candidate vertical (column) cuts.</summary>
    private static readonly SKColor VerticalCutColor = new(214, 50, 50);

    /// <summary>Color used to highlight whitespace rectangles classified as candidate horizontal (paragraph-break) cuts.</summary>
    private static readonly SKColor HorizontalCutColor = new(40, 90, 200);

    /// <summary>Color used to draw whitespace rectangles that are neither structural verticals nor horizontals.</summary>
    private static readonly SKColor BackgroundWhitespaceColor = new(140, 140, 140);

    /// <summary>Runs the bench-segmenter verb end-to-end.</summary>
    /// <param name="options">Parsed command options.</param>
    /// <returns>Process exit code.</returns>
    public static int Run(BenchSegmenterOptions options)
    {
        if (options.ShowHelp)
        {
            PrintHelp(Console.Out);
            return 0;
        }

        Directory.CreateDirectory(options.OutputDirectory);
        foreach (var (id, _) in Algorithms)
            Directory.CreateDirectory(Path.Combine(options.OutputDirectory, id));

        var oursRowsByPage = new PdfStructParser()
            .AnalyzePageBlocks(options.InputPath)
            .GroupBy(row => row.PageNumber)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PageBlockDiagnosticRow>)g.ToList());

        var pageStats = new List<PageStat>();
        var pdfiumLib = DocLib.Instance;

        using (var pdf = PdfDocument.Open(options.InputPath))
        {
            for (var p = 1; p <= pdf.NumberOfPages; p++)
            {
                var page = pdf.GetPage(p);
                var words = LetterGrouper.Instance.GetWords(page.Letters).ToList();

                var xyBlocks = RecursiveXYCut.Instance.GetBlocks(words);
                var docstrumBlocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
                var allImages = page.GetImages().ToList();
                var pageArea = page.Width * page.Height;
                var maxImageAreaRatio = pageArea > 0 && allImages.Count > 0
                    ? allImages.Max(img => (img.BoundingBox.Width * img.BoundingBox.Height) / pageArea)
                    : 0.0;

                var whitespaces = words.Count > 0
                    ? WhitespaceCoverExtractor.GetWhitespaces(words, images: PdfStructParser.GetLayoutObstacleImages(page))
                    : Array.Empty<PdfRectangle>();
                var oursBlocks = oursRowsByPage.TryGetValue(p, out var rows)
                    ? (IReadOnlyList<PdfStructTextBlock>)rows.Select(r => r.Block).ToList()
                    : [];

                var wordBoxes = words.Select(w => w.BoundingBox).ToList();
                var verticalCuts = whitespaces
                    .Where(ws => StructuralCutClassifier.IsVerticalCutCandidate(ws, page.Width, page.Height))
                    .ToList();
                var horizontalCuts = whitespaces
                    .Where(ws => StructuralCutClassifier.IsHorizontalCutCandidate(ws, page.Width, page.Height))
                    .Where(ws => StructuralCutClassifier.IsCleanHorizontalGap(ws, wordBoxes))
                    .ToList();

                pageStats.Add(new PageStat(
                    PageNumber: p,
                    WordCount: words.Count,
                    ImageCount: allImages.Count,
                    MaxImageAreaRatio: maxImageAreaRatio,
                    XyCutBlocks: xyBlocks.Count,
                    DocstrumBlocks: docstrumBlocks.Count,
                    Whitespaces: whitespaces.Count,
                    VerticalCuts: verticalCuts.Count,
                    HorizontalCuts: horizontalCuts.Count,
                    OurBlocks: oursBlocks.Count));

                using var raster = RasterizePage(pdfiumLib, options.InputPath, p, page);

                RenderRectangleOverlay(
                    raster, page,
                    Path.Combine(options.OutputDirectory, "recursive-xycut", $"page-{p:000}.png"),
                    words, RectsFromPdfPig(xyBlocks),
                    AlgorithmColors["recursive-xycut"]);

                RenderRectangleOverlay(
                    raster, page,
                    Path.Combine(options.OutputDirectory, "docstrum", $"page-{p:000}.png"),
                    words, RectsFromPdfPig(docstrumBlocks),
                    AlgorithmColors["docstrum"]);

                RenderRectangleOverlay(
                    raster, page,
                    Path.Combine(options.OutputDirectory, "whitespace-cover", $"page-{p:000}.png"),
                    words, whitespaces,
                    AlgorithmColors["whitespace-cover"],
                    fillStyle: true);

                RenderStructuralCutsOverlay(
                    raster, page,
                    Path.Combine(options.OutputDirectory, "structural-cuts", $"page-{p:000}.png"),
                    words, whitespaces, verticalCuts, horizontalCuts);

                RenderRectangleOverlay(
                    raster, page,
                    Path.Combine(options.OutputDirectory, "ours", $"page-{p:000}.png"),
                    words, RectsFromPdfStruct(oursBlocks),
                    AlgorithmColors["ours"]);
            }
        }

        WriteSummary(options.OutputDirectory, pageStats);
        Console.Error.WriteLine($"Wrote bench output to {Path.GetFullPath(options.OutputDirectory)}");
        return 0;
    }

    /// <summary>Builds and writes the per-fixture summary table to <c>summary.txt</c> and stdout, followed by a per-page block-count table.</summary>
    private static void WriteSummary(string outputDirectory, IReadOnlyList<PageStat> stats)
    {
        var lines = new List<string>
        {
            $"pages: {stats.Count}",
            $"words (mean/page): {Mean(stats.Select(s => (double)s.WordCount)):F1}",
            string.Empty,
            "algorithm                blocks_total  blocks_mean/page  blocks_min  blocks_max",
            $"RecursiveXYCut           {Sum(stats.Select(s => s.XyCutBlocks)),12}  {Mean(stats.Select(s => (double)s.XyCutBlocks)),16:F2}  {Min(stats.Select(s => s.XyCutBlocks)),10}  {Max(stats.Select(s => s.XyCutBlocks)),10}",
            $"Docstrum                 {Sum(stats.Select(s => s.DocstrumBlocks)),12}  {Mean(stats.Select(s => (double)s.DocstrumBlocks)),16:F2}  {Min(stats.Select(s => s.DocstrumBlocks)),10}  {Max(stats.Select(s => s.DocstrumBlocks)),10}",
            $"WhitespaceCover (gutters){Sum(stats.Select(s => s.Whitespaces)),12}  {Mean(stats.Select(s => (double)s.Whitespaces)),16:F2}  {Min(stats.Select(s => s.Whitespaces)),10}  {Max(stats.Select(s => s.Whitespaces)),10}",
            $"  vertical-cut candidates{Sum(stats.Select(s => s.VerticalCuts)),12}  {Mean(stats.Select(s => (double)s.VerticalCuts)),16:F2}  {Min(stats.Select(s => s.VerticalCuts)),10}  {Max(stats.Select(s => s.VerticalCuts)),10}",
            $"  horizontal-cut candidates{Sum(stats.Select(s => s.HorizontalCuts)),10}  {Mean(stats.Select(s => (double)s.HorizontalCuts)),16:F2}  {Min(stats.Select(s => s.HorizontalCuts)),10}  {Max(stats.Select(s => s.HorizontalCuts)),10}",
            $"PdfStruct (ours)         {Sum(stats.Select(s => s.OurBlocks)),12}  {Mean(stats.Select(s => (double)s.OurBlocks)),16:F2}  {Min(stats.Select(s => s.OurBlocks)),10}  {Max(stats.Select(s => s.OurBlocks)),10}",
            string.Empty,
            "Note: WhitespaceCoverExtractor returns whitespace gutters, not blocks. Counts are not directly comparable to the other rows.",
            "vertical-cut/horizontal-cut rows count subsets of the WhitespaceCover output, classified by the StructuralCuts criteria.",
            string.Empty,
            "page  words  imgs  maxImg%   xycut  docstrum  whitespace  v-cuts  h-cuts  ours",
        };

        foreach (var s in stats)
            lines.Add($"{s.PageNumber,4}  {s.WordCount,5}  {s.ImageCount,4}  {s.MaxImageAreaRatio * 100,7:F1}  {s.XyCutBlocks,6}  {s.DocstrumBlocks,8}  {s.Whitespaces,10}  {s.VerticalCuts,6}  {s.HorizontalCuts,6}  {s.OurBlocks,4}");

        File.WriteAllLines(Path.Combine(outputDirectory, "summary.txt"), lines);
        foreach (var line in lines)
            Console.Out.WriteLine(line);
    }

    /// <summary>Sums an integer sequence; safe on empty input.</summary>
    private static int Sum(IEnumerable<int> values) => values.DefaultIfEmpty(0).Sum();

    /// <summary>Returns the arithmetic mean, or 0 for an empty input.</summary>
    private static double Mean(IEnumerable<double> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? 0 : list.Average();
    }

    /// <summary>Returns the minimum, or 0 for an empty input.</summary>
    private static int Min(IEnumerable<int> values) => values.DefaultIfEmpty(0).Min();

    /// <summary>Returns the maximum, or 0 for an empty input.</summary>
    private static int Max(IEnumerable<int> values) => values.DefaultIfEmpty(0).Max();

    /// <summary>Maps a sequence of PdfPig <see cref="PdfPigTextBlock"/> values to their bounding rectangles.</summary>
    private static IReadOnlyList<PdfRectangle> RectsFromPdfPig(IReadOnlyList<PdfPigTextBlock> blocks) =>
        blocks.Select(b => b.BoundingBox).ToList();

    /// <summary>Maps a sequence of PdfStruct <see cref="PdfStructTextBlock"/> values to PdfPig-shaped rectangles for the renderer.</summary>
    private static IReadOnlyList<PdfRectangle> RectsFromPdfStruct(IReadOnlyList<PdfStructTextBlock> blocks) =>
        blocks.Select(b => new PdfRectangle(
            b.BoundingBox.Left, b.BoundingBox.Bottom,
            b.BoundingBox.Right, b.BoundingBox.Top)).ToList();

    /// <summary>Renders a per-page debug PNG: PDFium-rastered page + light gray word boxes + colored block/gutter overlays.</summary>
    private static void RenderRectangleOverlay(
        SKBitmap pageRaster,
        Page page,
        string outputPath,
        IReadOnlyList<Word> words,
        IReadOnlyList<PdfRectangle> rectangles,
        SKColor color,
        bool fillStyle = false)
    {
        var bounds = page.MediaBox.Bounds;
        var scale = (float)((double)pageRaster.Width / bounds.Width);

        using var bitmap = new SKBitmap(new SKImageInfo(pageRaster.Width, pageRaster.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.DrawBitmap(pageRaster, 0, 0);

            using var wordStroke = new SKPaint
            {
                Color = new SKColor(120, 120, 120, 90),
                IsAntialias = true,
                StrokeWidth = 1,
                Style = SKPaintStyle.Stroke
            };
            foreach (var word in words)
            {
                var r = ToCanvasRect(word.BoundingBox, bounds, scale);
                if (r.Width > 0 && r.Height > 0)
                    canvas.DrawRect(r, wordStroke);
            }

            using var fill = new SKPaint
            {
                Color = color.WithAlpha((byte)(fillStyle ? 70 : 30)),
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
            for (var i = 0; i < rectangles.Count; i++)
            {
                var r = ToCanvasRect(rectangles[i], bounds, scale);
                if (r.Width <= 0 || r.Height <= 0) continue;
                canvas.DrawRect(r, fill);
                canvas.DrawRect(r, stroke);
                DrawIndexLabel(canvas, r, i, color);
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 95);
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);
        using var stream = File.Create(outputPath);
        data.SaveTo(stream);
    }

    /// <summary>
    /// Renders a single overlay PNG that visualises the structural-cut
    /// classification: all whitespace rectangles are drawn in a faint
    /// neutral colour as a backdrop, then candidate vertical cuts and
    /// horizontal cuts are drawn over them in distinct colours so the
    /// human reviewer can see which whitespace rectangles the criteria
    /// accept as page structure and which they reject.
    /// </summary>
    private static void RenderStructuralCutsOverlay(
        SKBitmap pageRaster,
        Page page,
        string outputPath,
        IReadOnlyList<Word> words,
        IReadOnlyList<PdfRectangle> allWhitespaces,
        IReadOnlyList<PdfRectangle> verticalCuts,
        IReadOnlyList<PdfRectangle> horizontalCuts)
    {
        var bounds = page.MediaBox.Bounds;
        var scale = (float)((double)pageRaster.Width / bounds.Width);

        using var bitmap = new SKBitmap(new SKImageInfo(pageRaster.Width, pageRaster.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.DrawBitmap(pageRaster, 0, 0);

            using var wordStroke = new SKPaint
            {
                Color = new SKColor(120, 120, 120, 90),
                IsAntialias = true,
                StrokeWidth = 1,
                Style = SKPaintStyle.Stroke
            };
            foreach (var word in words)
            {
                var r = ToCanvasRect(word.BoundingBox, bounds, scale);
                if (r.Width > 0 && r.Height > 0)
                    canvas.DrawRect(r, wordStroke);
            }

            using var backdropFill = new SKPaint
            {
                Color = BackgroundWhitespaceColor.WithAlpha(40),
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };
            using var backdropStroke = new SKPaint
            {
                Color = BackgroundWhitespaceColor.WithAlpha(140),
                IsAntialias = true,
                StrokeWidth = 1,
                Style = SKPaintStyle.Stroke
            };
            foreach (var ws in allWhitespaces)
            {
                var r = ToCanvasRect(ws, bounds, scale);
                if (r.Width <= 0 || r.Height <= 0) continue;
                canvas.DrawRect(r, backdropFill);
                canvas.DrawRect(r, backdropStroke);
            }

            DrawClassifiedCuts(canvas, bounds, scale, verticalCuts, VerticalCutColor);
            DrawClassifiedCuts(canvas, bounds, scale, horizontalCuts, HorizontalCutColor);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 95);
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);
        using var stream = File.Create(outputPath);
        data.SaveTo(stream);
    }

    /// <summary>Draws a set of classified whitespace rectangles (vertical or horizontal cut candidates) with a filled overlay, bold stroke, and per-rectangle index labels.</summary>
    private static void DrawClassifiedCuts(
        SKCanvas canvas,
        PdfRectangle bounds,
        float scale,
        IReadOnlyList<PdfRectangle> cuts,
        SKColor color)
    {
        using var fill = new SKPaint
        {
            Color = color.WithAlpha(70),
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
        for (var i = 0; i < cuts.Count; i++)
        {
            var r = ToCanvasRect(cuts[i], bounds, scale);
            if (r.Width <= 0 || r.Height <= 0) continue;
            canvas.DrawRect(r, fill);
            canvas.DrawRect(r, stroke);
            DrawIndexLabel(canvas, r, i, color);
        }
    }

    /// <summary>Draws a small index tab above an overlay rectangle, matching the labelling style used by the production debug renderer.</summary>
    private static void DrawIndexLabel(SKCanvas canvas, SKRect rect, int index, SKColor color)
    {
        var label = index.ToString(CultureInfo.InvariantCulture);
        using var font = new SKFont(SKTypeface.Default, 12);
        using var textPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var background = new SKPaint
        {
            Color = color.WithAlpha(230),
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };

        var width = font.MeasureText(label);
        var labelRect = new SKRect(
            rect.Left,
            Math.Max(0, rect.Top - 16),
            rect.Left + width + 6,
            Math.Max(16, rect.Top));

        canvas.DrawRect(labelRect, background);
        canvas.DrawText(label, labelRect.Left + 3, labelRect.Bottom - 3, SKTextAlign.Left, font, textPaint);
    }

    /// <summary>
    /// Converts a PDF-space rectangle (origin bottom-left, MediaBox-absolute)
    /// to a canvas-space rectangle (origin top-left, MediaBox-relative). Mirrors
    /// the conversion in <see cref="DebugImageRenderer"/> so overlay placement
    /// stays consistent across the two harnesses.
    /// </summary>
    private static SKRect ToCanvasRect(PdfRectangle box, PdfRectangle mediaBox, float scale)
    {
        var left = (float)((box.Left - mediaBox.Left) * scale);
        var right = (float)((box.Right - mediaBox.Left) * scale);
        var top = (float)((mediaBox.Top - box.Top) * scale);
        var bottom = (float)((mediaBox.Top - box.Bottom) * scale);
        return new SKRect(left, top, right, bottom);
    }

    /// <summary>Rasterises the supplied PDF page via PDFium onto an opaque white-backed bitmap.</summary>
    private static SKBitmap RasterizePage(IDocLib pdfiumLib, string pdfPath, int pageNumber, Page page)
    {
        var pageWidth = page.MediaBox.Bounds.Width;
        var pageHeight = page.MediaBox.Bounds.Height;
        var requestedWidth = Math.Max(1, (int)Math.Ceiling(pageWidth * (float)Math.Min(2.0, TargetPageWidth / pageWidth)));
        var requestedHeight = Math.Max(1, (int)Math.Ceiling(pageHeight * (float)Math.Min(2.0, TargetPageWidth / pageWidth)));

        var dimensions = new PageDimensions(requestedWidth, requestedHeight);
        using var docReader = pdfiumLib.GetDocReader(pdfPath, dimensions);
        using var pageReader = docReader.GetPageReader(pageNumber - 1);
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

    /// <summary>Writes the bench-segmenter usage banner to the supplied writer.</summary>
    public static void PrintHelp(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  pdfstruct bench-segmenter <input.pdf> -o <output-dir>");
        writer.WriteLine();
        writer.WriteLine("Runs RecursiveXYCut, Docstrum, WhitespaceCover, and PdfStruct's own");
        writer.WriteLine("block partition against the same input. Writes a per-page PNG per");
        writer.WriteLine("algorithm and a summary table.");
    }

    /// <summary>Per-page block / whitespace counts collected during a bench run.</summary>
    private readonly record struct PageStat(
        int PageNumber,
        int WordCount,
        int ImageCount,
        double MaxImageAreaRatio,
        int XyCutBlocks,
        int DocstrumBlocks,
        int Whitespaces,
        int VerticalCuts,
        int HorizontalCuts,
        int OurBlocks);
}

/// <summary>Parsed options for the <c>bench-segmenter</c> command.</summary>
internal sealed class BenchSegmenterOptions
{
    /// <summary>Absolute path to the input PDF.</summary>
    public string InputPath { get; private set; } = string.Empty;

    /// <summary>Directory under which per-algorithm subfolders and the summary file are written.</summary>
    public string OutputDirectory { get; private set; } = string.Empty;

    /// <summary><c>true</c> when the parsed arguments only request that help be displayed.</summary>
    public bool ShowHelp { get; private set; }

    /// <summary>Parses arguments that follow the <c>bench-segmenter</c> verb.</summary>
    /// <exception cref="CliException">Thrown when arguments are malformed or the input PDF cannot be located.</exception>
    public static BenchSegmenterOptions Parse(string[] args)
    {
        if (args.Length == 0)
            throw new CliException("Missing input PDF path.");

        if (args.Any(App.IsHelp))
            return new BenchSegmenterOptions { ShowHelp = true };

        string? inputPath = null;
        string? outputDirectory = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-o":
                case "--output":
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("-", StringComparison.Ordinal))
                        throw new CliException($"Missing value for {arg}.");
                    outputDirectory = args[++i];
                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                        throw new CliException($"Unknown option: {arg}");
                    if (inputPath is not null)
                        throw new CliException($"Unexpected argument: {arg}");
                    inputPath = arg;
                    break;
            }
        }

        if (inputPath is null)
            throw new CliException("Missing input PDF path.");
        if (outputDirectory is null)
            throw new CliException("Missing output directory (-o <dir>).");

        var fullInputPath = Path.GetFullPath(inputPath);
        if (!File.Exists(fullInputPath))
            throw new CliException($"Input PDF not found: {inputPath}");

        return new BenchSegmenterOptions
        {
            InputPath = fullInputPath,
            OutputDirectory = Path.GetFullPath(outputDirectory),
        };
    }
}
