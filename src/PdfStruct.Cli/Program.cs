// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Analysis;

namespace PdfStruct.Cli;

/// <summary>Process entry point for the <c>pdfstruct</c> CLI.</summary>
internal static class Program
{
    /// <summary>Forwards command-line arguments to <see cref="App.Run"/>.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>Process exit code: <c>0</c> on success, <c>1</c> on runtime error, <c>2</c> on usage error.</returns>
    public static int Main(string[] args) => App.Run(args);
}

/// <summary>Top-level command dispatcher for the CLI.</summary>
internal static class App
{
    /// <summary>Parses the leading verb and dispatches to the matching command.</summary>
    /// <param name="args">Command-line arguments. The first token is treated as the verb.</param>
    /// <returns>Process exit code: <c>0</c> on success, <c>1</c> on runtime error, <c>2</c> on usage error.</returns>
    public static int Run(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintUsage(Console.Out);
            return 0;
        }

        var verb = args[0];
        var rest = args.Skip(1).ToArray();

        try
        {
            return verb.ToLowerInvariant() switch
            {
                "extract" => Extract(ExtractOptions.Parse(rest)),
                "diagnose" => Diagnose(DiagnoseOptions.Parse(rest)),
                "bench-segmenter" => BenchSegmenterCommand.Run(BenchSegmenterOptions.Parse(rest)),
                _ => UnknownCommand(verb)
            };
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintUsage(Console.Error);
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>Reports an unknown verb and prints usage to stderr.</summary>
    private static int UnknownCommand(string verb)
    {
        Console.Error.WriteLine($"Unknown command: {verb}");
        PrintUsage(Console.Error);
        return 2;
    }

    /// <summary>Runs the <c>extract</c> command: parses the input PDF and writes Markdown or JSON output.</summary>
    private static int Extract(ExtractOptions options)
    {
        if (options.ShowHelp)
        {
            PrintUsage(Console.Out);
            return 0;
        }

        var format = options.ResolveFormat();
        var parserOptions = new PdfStructOptions
        {
            Format = format == OutputKind.Json ? OutputFormat.Json : OutputFormat.Markdown,
            ExcludeHeadersFooters = !options.IncludeRunningHeaders,
            SanitizeText = options.SanitizeText,
            ImageOutput = options.ImageOutput
        };
        // When images are surfaced, decode any QR codes and barcodes among them.
        var codeDecoder = options.ImageOutput != ImageOutputMode.Off
            ? new PdfStruct.ZXing.ZXingCodeDecoder()
            : null;
        var parser = new PdfStructParser(parserOptions, codeDecoder);

        var result = parser.Parse(options.InputPath);
        var content = format == OutputKind.Json
            ? result.Json ?? string.Empty
            : result.Markdown ?? string.Empty;

        if (options.OutputPath is null)
        {
            Console.Out.Write(content);
            if (!content.EndsWith(Environment.NewLine, StringComparison.Ordinal))
            {
                Console.Out.WriteLine();
            }
        }
        else
        {
            var outputPath = Path.GetFullPath(options.OutputPath);
            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            File.WriteAllText(outputPath, content);
        }

        if (options.DebugImageDirectory is not null)
        {
            IReadOnlyDictionary<int, IReadOnlyList<TextBlock>>? textLinesByPage = null;
            if (options.DebugLines)
            {
                textLinesByPage = parser.AnalyzeTextLines(options.InputPath)
                    .GroupBy(row => row.PageNumber)
                    .ToDictionary(
                        group => group.Key,
                        group => (IReadOnlyList<TextBlock>)group.Select(row => row.Line).ToList());
            }

            var files = DebugImageRenderer.Render(
                options.InputPath,
                result.Document,
                options.DebugImageDirectory,
                textLinesByPage);

            Console.Error.WriteLine($"Wrote {files.Count} debug image(s) to {Path.GetFullPath(options.DebugImageDirectory)}");
        }

        return 0;
    }

    /// <summary>
    /// Runs the <c>diagnose</c> command: scores every block on the heading
    /// probability axis and writes a CSV row per block. Used to calibrate
    /// the heading-probability threshold against fixture expectations.
    /// </summary>
    private static int Diagnose(DiagnoseOptions options)
    {
        if (options.ShowHelp)
        {
            PrintUsage(Console.Out);
            return 0;
        }

        var parser = new PdfStructParser();
        var rows = parser.AnalyzeHeadingProbabilities(options.InputPath);

        TextWriter writer;
        FileStream? fileStream = null;
        StreamWriter? fileWriter = null;
        if (options.OutputPath is null)
        {
            writer = Console.Out;
        }
        else
        {
            var outputPath = Path.GetFullPath(options.OutputPath);
            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            fileStream = File.Create(outputPath);
            fileWriter = new StreamWriter(fileStream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer = fileWriter;
        }

        try
        {
            WriteDiagnosticCsv(writer, rows);
            writer.Flush();
        }
        finally
        {
            fileWriter?.Dispose();
            fileStream?.Dispose();
        }

        return 0;
    }

    /// <summary>Writes heading-probability diagnostic rows as CSV.</summary>
    private static void WriteDiagnosticCsv(TextWriter writer, IReadOnlyList<HeadingDiagnosticRow> rows)
    {
        writer.WriteLine("page,classified,total,neighbour,initial,standalone,center,vertical_gap,all_caps,next_page_penalty,size_rarity,weight_rarity,bulleted,line_decay,sentence_flow,font_size,is_bold,is_standalone,line_count,font,content");
        foreach (var row in rows)
        {
            var b = row.Block;
            var bd = row.Breakdown;
            var content = Csv(Truncate(b.Text.ReplaceLineEndings(" ⏎ "), 80));
            writer.Write($"{row.PageNumber},");
            writer.Write($"{(row.ClassifiedAsHeading ? "H" : "P")},");
            writer.Write($"{bd.Total:F3},");
            writer.Write($"{bd.Neighbour:F3},");
            writer.Write($"{bd.Initial:F3},");
            writer.Write($"{bd.Standalone:F3},");
            writer.Write($"{bd.CenterAligned:F3},");
            writer.Write($"{bd.VerticalGap:F3},");
            writer.Write($"{bd.AllCaps:F3},");
            writer.Write($"{bd.NextPagePenalty:F3},");
            writer.Write($"{bd.FontSizeRarity:F3},");
            writer.Write($"{bd.FontWeightRarity:F3},");
            writer.Write($"{bd.Bulleted:F3},");
            writer.Write($"{bd.LineDecay:F3},");
            writer.Write($"{bd.SentenceFlowDemotion:F3},");
            writer.Write($"{b.FontSize:F1},");
            writer.Write($"{(b.IsBold ? "1" : "0")},");
            writer.Write($"{(b.IsStandalone ? "1" : "0")},");
            writer.Write($"{b.LineCount},");
            writer.Write($"{Csv(b.FontName)},");
            writer.WriteLine(content);
        }
    }

    /// <summary>Trims a string to <paramref name="max"/> characters.</summary>
    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    /// <summary>Quotes a CSV field if it contains the delimiter, a quote, or a newline.</summary>
    private static string Csv(string value)
    {
        if (value.IndexOfAny([',', '"', '\n', '\r']) < 0) return value;
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    /// <summary>Returns <c>true</c> when the value is any recognised help token (<c>-h</c>, <c>--help</c>, or the standalone verb <c>help</c>).</summary>
    internal static bool IsHelp(string value) =>
        value is "-h" or "--help" or "help";

    /// <summary>Writes the top-level usage banner to the supplied writer.</summary>
    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  pdfstruct extract <input.pdf> [options]");
        writer.WriteLine("  pdfstruct diagnose <input.pdf> [options]");
        writer.WriteLine("  pdfstruct bench-segmenter <input.pdf> -o <output-dir>");
        writer.WriteLine();
        writer.WriteLine("Extract options:");
        writer.WriteLine("  -o, --output <path>       Write output to a file instead of stdout.");
        writer.WriteLine("      --format <format>     Output format: markdown, json. Default: markdown, or inferred from -o.");
        writer.WriteLine("      --debug-image <dir>   Write per-page PNG overlays with extracted element bounding boxes.");
        writer.WriteLine("      --debug-lines         Include pre-paragraph text-line boxes in --debug-image overlays.");
        writer.WriteLine("      --images <mode>       Surface embedded images as content: off (default), embedded, external.");
        writer.WriteLine("      --include-running-headers");
        writer.WriteLine("                            Keep detected running headers, footers, and page furniture.");
        writer.WriteLine("      --sanitize            Mask common sensitive values in extracted text.");
        writer.WriteLine();
        writer.WriteLine("Diagnose options:");
        writer.WriteLine("  -o, --output <path>       Write the heading-probability CSV to a file. Default: stdout.");
        writer.WriteLine();
        writer.WriteLine("Bench-segmenter options:");
        writer.WriteLine("  -o, --output <dir>        Directory under which per-algorithm PNGs and summary.txt are written.");
        writer.WriteLine();
        writer.WriteLine("  -h, --help                Show help.");
        writer.WriteLine();
        writer.WriteLine("Examples:");
        writer.WriteLine("  pdfstruct extract document.pdf");
        writer.WriteLine("  pdfstruct extract document.pdf -o out.md");
        writer.WriteLine("  pdfstruct extract document.pdf -o out.json --format json");
        writer.WriteLine("  pdfstruct extract document.pdf --debug-image out --debug-lines");
        writer.WriteLine("  pdfstruct diagnose document.pdf -o scores.csv");
        writer.WriteLine("  pdfstruct bench-segmenter document.pdf -o bench-out/");
    }
}

/// <summary>Parsed options for the <c>extract</c> command.</summary>
internal sealed class ExtractOptions
{
    private string? _format;

    /// <summary>Absolute path to the input PDF.</summary>
    public string InputPath { get; private set; } = string.Empty;

    /// <summary>Output file path, or <c>null</c> to write to stdout.</summary>
    public string? OutputPath { get; private set; }

    /// <summary>Directory where per-page debug PNG overlays are written, or <c>null</c> to skip rendering.</summary>
    public string? DebugImageDirectory { get; private set; }

    /// <summary><c>true</c> when debug PNG overlays should include pre-paragraph text lines.</summary>
    public bool DebugLines { get; private set; }

    /// <summary><c>true</c> when detected running headers, footers, and page furniture should be retained.</summary>
    public bool IncludeRunningHeaders { get; private set; }

    /// <summary><c>true</c> when sensitive text should be masked in the extracted output.</summary>
    public bool SanitizeText { get; private set; }

    /// <summary>How embedded images should be surfaced: off (default), embedded, or external.</summary>
    public ImageOutputMode ImageOutput { get; private set; } = ImageOutputMode.Off;

    /// <summary><c>true</c> when the parsed arguments only request that help be displayed.</summary>
    public bool ShowHelp { get; private set; }

    /// <summary>Parses arguments that follow the <c>extract</c> verb.</summary>
    /// <param name="args">Arguments after the leading verb.</param>
    /// <returns>The parsed options, or a help-only instance when <c>-h</c>/<c>--help</c> is present.</returns>
    /// <exception cref="CliException">Thrown when an option is missing a required value, an unknown option is supplied, or the input PDF cannot be located.</exception>
    public static ExtractOptions Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw new CliException("Missing input PDF path.");
        }

        if (args.Any(App.IsHelp))
        {
            return new ExtractOptions { InputPath = string.Empty, ShowHelp = true };
        }

        string? inputPath = null;
        var options = new ExtractOptions { InputPath = string.Empty };

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "-o":
                case "--output":
                    options.OutputPath = ReadValue(args, ref index, arg);
                    break;

                case "--format":
                    options._format = ReadValue(args, ref index, arg);
                    break;

                case "--debug-image":
                    options.DebugImageDirectory = ReadValue(args, ref index, arg);
                    break;

                case "--debug-lines":
                    options.DebugLines = true;
                    break;

                case "--include-running-headers":
                    options.IncludeRunningHeaders = true;
                    break;

                case "--sanitize":
                    options.SanitizeText = true;
                    break;

                case "--images":
                    options.ImageOutput = ParseImageMode(ReadValue(args, ref index, arg));
                    break;

                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new CliException($"Unknown option: {arg}");
                    }

                    if (inputPath is not null)
                    {
                        throw new CliException($"Unexpected argument: {arg}");
                    }

                    inputPath = arg;
                    break;
            }
        }

        if (inputPath is null)
        {
            throw new CliException("Missing input PDF path.");
        }

        var fullInputPath = Path.GetFullPath(inputPath);
        if (!File.Exists(fullInputPath))
        {
            throw new CliException($"Input PDF not found: {inputPath}");
        }

        if (options.DebugLines && options.DebugImageDirectory is null)
        {
            throw new CliException("--debug-lines requires --debug-image <dir>.");
        }

        options.InputPath = fullInputPath;
        return options;
    }

    /// <summary>Resolves the requested output format, defaulting to Markdown unless a JSON extension is implied by <see cref="OutputPath"/>.</summary>
    /// <returns>The resolved output kind.</returns>
    /// <exception cref="CliException">Thrown when an explicit <c>--format</c> value is not recognized.</exception>
    public OutputKind ResolveFormat()
    {
        if (_format is null && OutputPath is not null)
        {
            var extension = Path.GetExtension(OutputPath);
            if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
            {
                return OutputKind.Json;
            }
        }

        return (_format ?? "markdown").ToLowerInvariant() switch
        {
            "markdown" or "md" => OutputKind.Markdown,
            "json" => OutputKind.Json,
            var value => throw new CliException($"Unsupported format: {value}")
        };
    }

    /// <summary>Reads the value following an option token, treating values that begin with <c>-</c> as missing.</summary>
    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal))
        {
            throw new CliException($"Missing value for {option}.");
        }

        index++;
        return args[index];
    }

    /// <summary>Parses an <c>--images</c> value into an <see cref="ImageOutputMode"/>.</summary>
    /// <exception cref="CliException">Thrown when the value is not a recognized mode.</exception>
    private static ImageOutputMode ParseImageMode(string value) => value.ToLowerInvariant() switch
    {
        "off" => ImageOutputMode.Off,
        "embedded" => ImageOutputMode.Embedded,
        "external" => ImageOutputMode.External,
        _ => throw new CliException($"Unknown --images mode: {value}. Use off, embedded, or external.")
    };
}

/// <summary>Parsed options for the <c>diagnose</c> command.</summary>
internal sealed class DiagnoseOptions
{
    /// <summary>Absolute path to the input PDF.</summary>
    public string InputPath { get; private set; } = string.Empty;

    /// <summary>CSV output path, or <c>null</c> to write to stdout.</summary>
    public string? OutputPath { get; private set; }

    /// <summary><c>true</c> when the parsed arguments only request that help be displayed.</summary>
    public bool ShowHelp { get; private set; }

    /// <summary>Parses arguments that follow the <c>diagnose</c> verb.</summary>
    /// <exception cref="CliException">Thrown when arguments are malformed or the input PDF cannot be located.</exception>
    public static DiagnoseOptions Parse(string[] args)
    {
        if (args.Length == 0)
            throw new CliException("Missing input PDF path.");

        if (args.Any(App.IsHelp))
        {
            return new DiagnoseOptions { ShowHelp = true };
        }

        string? inputPath = null;
        var options = new DiagnoseOptions();

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "-o":
                case "--output":
                    if (index + 1 >= args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal))
                        throw new CliException($"Missing value for {arg}.");
                    options.OutputPath = args[++index];
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

        var fullInputPath = Path.GetFullPath(inputPath);
        if (!File.Exists(fullInputPath))
            throw new CliException($"Input PDF not found: {inputPath}");

        options.InputPath = fullInputPath;
        return options;
    }
}

/// <summary>Output formats supported by the <c>extract</c> command.</summary>
internal enum OutputKind
{
    /// <summary>Render as Markdown.</summary>
    Markdown,

    /// <summary>Render as structured JSON with per-element bounding boxes.</summary>
    Json
}

/// <summary>Signals a usage or argument error to the top-level dispatcher, which converts it into exit code <c>2</c> plus a usage banner.</summary>
internal sealed class CliException(string message) : Exception(message);
