// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PdfStruct.Models;

namespace PdfStruct.Rendering;

/// <summary>
/// Defines a renderer that converts a parsed PDF document to a specific output format.
/// </summary>
public interface IDocumentRenderer
{
    /// <summary>Renders the document to a string in the target format.</summary>
    string Render(Models.PdfDocument document);
}

/// <summary>
/// Renders a <see cref="Models.PdfDocument"/> to Markdown, preserving heading hierarchy,
/// table structure, and list formatting. Ideal for LLM context and RAG chunking.
/// </summary>
public sealed class MarkdownRenderer : IDocumentRenderer
{
    /// <inheritdoc />
    public string Render(Models.PdfDocument document)
    {
        var sb = new StringBuilder();
        foreach (var element in document.Kids)
        {
            RenderElement(element, sb);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static void RenderElement(ContentElement element, StringBuilder sb)
    {
        switch (element)
        {
            case HeadingElement h:
                sb.Append(new string('#', Math.Clamp(h.HeadingLevel, 1, 6)));
                sb.Append(' ');
                sb.AppendLine(h.Text.Content);
                break;

            case ParagraphElement p:
                sb.AppendLine(p.Text.Content);
                break;

            case TableElement t:
                RenderTable(t, sb);
                break;

            case RegionElement region:
                if (region.TextLines.Count > 0)
                {
                    sb.AppendLine("[region]");
                    foreach (var line in region.TextLines) sb.AppendLine(line);
                }
                break;

            case ListElement l:
                RenderList(l, sb);
                break;

            case FigureElement fig:
                sb.AppendLine(RenderFigureMarkdown(fig));
                break;

            case CaptionElement cap:
                sb.Append('*').Append(cap.Text.Content).AppendLine("*");
                break;

            case SourceNoteElement note:
                sb.Append('*').Append(note.Text.Content).AppendLine("*");
                break;
        }
    }

    private static void RenderTable(TableElement table, StringBuilder sb)
    {
        // Until cell structure is recovered, a detected region carries only its
        // claimed raw text. Render it as a delimited text block so the table's
        // content survives for retrieval without a fabricated grid.
        if (table.Rows.Count == 0)
        {
            if (table.TextLines.Count == 0) return;
            sb.AppendLine("[table]");
            foreach (var line in table.TextLines) sb.AppendLine(line);
            return;
        }

        // GFM has no colspan, so a multi-level header is flattened: each leaf
        // column's header is the group labels above it joined top to bottom, so
        // the column stays self-describing even when the table is chunked. The
        // full hierarchy with spans is preserved in the JSON; the Markdown is a
        // lossy reading projection.
        var leafCount = LeafColumnCount(table);
        if (leafCount == 0) return;

        var headerRows = Analysis.Tables.TableCellRecovery.HeaderRowCount(table.Rows);
        var labels = new string[leafCount];
        Array.Fill(labels, string.Empty);
        for (var i = 0; i < headerRows; i++)
        {
            var cover = LeafCover(table.Rows[i], leafCount, fillSpan: true);
            for (var j = 0; j < leafCount; j++)
            {
                if (cover[j].Length == 0) continue;
                labels[j] = labels[j] is { Length: > 0 } existing ? $"{existing} · {cover[j]}" : cover[j];
            }
        }

        AppendRow(sb, labels);
        sb.Append('|');
        for (var j = 0; j < leafCount; j++) sb.Append(" --- |");
        sb.AppendLine();

        // A row-label cell that spans several body rows is recovered on the first
        // of them, leaving the rest with an empty leftmost column. Carry the last
        // row label down so every chunked row stays self-describing, mirroring how
        // the column headers above each leaf are repeated. Only the leftmost
        // (row-label) column is carried — a blank value cell is left blank.
        var carriedLabel = string.Empty;
        for (var i = headerRows; i < table.Rows.Count; i++)
        {
            var cover = LeafCover(table.Rows[i], leafCount, fillSpan: false);
            if (cover[0].Length == 0) cover[0] = carriedLabel;
            else carriedLabel = cover[0];
            AppendRow(sb, cover);
        }
    }

    /// <summary>The number of leaf columns the table spans — its asserted column count, or the widest cell reach when none was set.</summary>
    private static int LeafColumnCount(TableElement table)
    {
        if (table.NumberOfColumns > 0) return table.NumberOfColumns;
        var max = 0;
        foreach (var row in table.Rows)
            foreach (var cell in row.Cells)
                max = Math.Max(max, cell.ColumnNumber + cell.ColumnSpan - 1);
        return max;
    }

    /// <summary>Projects a row's cells onto its leaf columns: a header row fills every leaf a spanning cell covers, a body row places the value at the cell's first leaf only.</summary>
    private static string[] LeafCover(TableRow row, int leafCount, bool fillSpan)
    {
        var cover = new string[leafCount];
        for (var j = 0; j < leafCount; j++) cover[j] = string.Empty;

        foreach (var cell in row.Cells)
        {
            var text = string.Join(" ", cell.Kids.OfType<ParagraphElement>().Select(p => p.Text.Content)).Trim();
            var start = cell.ColumnNumber - 1;
            var end = fillSpan ? start + cell.ColumnSpan - 1 : start;
            for (var j = Math.Max(0, start); j <= Math.Min(leafCount - 1, end); j++)
                cover[j] = text;
        }
        return cover;
    }

    /// <summary>Writes one GFM table row, escaping pipes so a cell value never breaks the column structure.</summary>
    private static void AppendRow(StringBuilder sb, IReadOnlyList<string> cells)
    {
        sb.Append('|');
        foreach (var cell in cells)
        {
            var text = cell.Replace("|", "\\|").Replace('\n', ' ');
            sb.Append(' ').Append(text.Length == 0 ? " " : text).Append(" |");
        }
        sb.AppendLine();
    }

    private static void RenderList(ListElement list, StringBuilder sb)
    {
        // Numbered paragraphs (patent [0001] runs) are prose, not list items:
        // each renders as its own paragraph keeping its printed marker, never
        // collapsed to a Markdown ordered-list counter.
        if (list.NumberingStyle == "numbered-paragraph")
        {
            foreach (var item in list.ListItems)
            {
                var marker = item.Label ?? item.Number?.ToString() ?? string.Empty;
                sb.Append(marker).Append(' ').AppendLine(item.Text.Content);
                sb.AppendLine();
                // Continuation text is paragraph prose, not nested list
                // content, so it renders flush — a four-space indent would
                // turn it into a Markdown code block.
                foreach (var child in item.Kids)
                {
                    if (child is ParagraphElement p)
                    {
                        sb.AppendLine(p.Text.Content);
                        sb.AppendLine();
                    }
                    else
                    {
                        RenderListItemChild(child, sb);
                    }
                }
            }
            return;
        }

        var ordered = list.NumberingStyle is "ordered" or "decimal" or "roman";
        for (int i = 0; i < list.ListItems.Count; i++)
        {
            var item = list.ListItems[i];
            sb.Append(ordered ? $"{item.Number ?? i + 1}." : "-");
            sb.Append(' ').AppendLine(item.Text.Content);
            foreach (var child in item.Kids)
                RenderListItemChild(child, sb);
        }
    }

    /// <summary>
    /// Renders a child element of a list item with a four-space indent so
    /// the nesting is visually evident in the resulting Markdown.
    /// </summary>
    private static void RenderListItemChild(ContentElement child, StringBuilder sb)
    {
        switch (child)
        {
            case ParagraphElement p:
                foreach (var line in p.Text.Content.Split('\n'))
                {
                    sb.Append("    ");
                    sb.AppendLine(line);
                }
                break;
            case CaptionElement c:
                sb.Append("    *").Append(c.Text.Content).AppendLine("*");
                break;
            case SourceNoteElement n:
                sb.Append("    *").Append(n.Text.Content).AppendLine("*");
                break;
            case FigureElement fig:
                sb.Append("    ").AppendLine(RenderFigureMarkdown(fig));
                break;
        }
    }

    /// <summary>
    /// Renders a figure to its Markdown form. Machine-readable codes render as
    /// a labelled placeholder carrying the decoded value (the payload is what
    /// matters for retrieval, not the glyph); other figures render as an image
    /// link when a source path is present, or a bare placeholder.
    /// </summary>
    private static string RenderFigureMarkdown(FigureElement fig)
    {
        var label = fig.Role switch
        {
            "qr-code" => "QR code",
            "barcode" => "Barcode",
            _ => null
        };

        if (label is not null)
            return fig.DecodedText is { Length: > 0 } text ? $"[{label}: {text}]" : $"[{label}]";

        return fig.Source is not null ? $"![figure]({fig.Source})" : "[Figure]";
    }
}

/// <summary>
/// Renders a <see cref="Models.PdfDocument"/> to a JSON tree of
/// bounding-box-annotated content elements.
/// </summary>
public sealed class JsonRenderer : IDocumentRenderer
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <inheritdoc />
    public string Render(Models.PdfDocument document)
    {
        var output = new Dictionary<string, object?>
        {
            ["file name"] = document.FileName,
            ["number of pages"] = document.NumberOfPages,
            ["author"] = document.Author,
            ["title"] = document.Title,
            ["creation date"] = document.CreationDate,
            ["modification date"] = document.ModificationDate,
            ["kids"] = document.Kids.Select(ToOdlElement).ToList()
        };
        return JsonSerializer.Serialize(output, s_options);
    }

    private static Dictionary<string, object?> ToOdlElement(ContentElement element)
    {
        var dict = new Dictionary<string, object?>
        {
            ["type"] = element.Type,
            ["id"] = element.Id,
            ["page number"] = element.PageNumber,
            ["bounding box"] = element.BoundingBox.ToArray()
        };

        switch (element)
        {
            case HeadingElement h:
                dict["level"] = h.Level;
                dict["heading level"] = h.HeadingLevel;
                AddText(dict, h.Text);
                break;
            case ParagraphElement p:
                AddText(dict, p.Text);
                break;
            case RegionElement region:
                if (region.TextLines.Count > 0) dict["text lines"] = region.TextLines;
                break;
            case TableElement t:
                dict["number of rows"] = t.NumberOfRows;
                dict["number of columns"] = t.NumberOfColumns;
                if (t.PreviousTableId.HasValue) dict["previous table id"] = t.PreviousTableId;
                if (t.NextTableId.HasValue) dict["next table id"] = t.NextTableId;
                if (t.TextLines.Count > 0) dict["text lines"] = t.TextLines;
                dict["rows"] = t.Rows.Select(r => new Dictionary<string, object?>
                {
                    ["type"] = "table row",
                    ["row number"] = r.RowNumber,
                    ["cells"] = r.Cells.Select(c => new Dictionary<string, object?>
                    {
                        ["type"] = "table cell",
                        ["row number"] = c.RowNumber,
                        ["column number"] = c.ColumnNumber,
                        ["row span"] = c.RowSpan,
                        ["column span"] = c.ColumnSpan,
                        ["page number"] = c.PageNumber,
                        ["bounding box"] = c.BoundingBox.ToArray(),
                        ["kids"] = c.Kids.Select(ToOdlElement).ToList()
                    }).ToList()
                }).ToList();
                break;
            case ListElement list:
                dict["level"] = "List";
                dict["numbering style"] = list.NumberingStyle;
                dict["number of list items"] = list.NumberOfListItems;
                if (list.PreviousListId.HasValue) dict["previous list id"] = list.PreviousListId;
                if (list.NextListId.HasValue) dict["next list id"] = list.NextListId;
                dict["list items"] = list.ListItems.Select(item =>
                {
                    var itemDict = new Dictionary<string, object?>
                    {
                        ["type"] = item.Type,
                        ["level"] = "List Item",
                        ["page number"] = item.PageNumber,
                        ["bounding box"] = item.BoundingBox.ToArray(),
                        ["font"] = item.Text.Font,
                        ["font size"] = item.Text.FontSize,
                        ["text color"] = item.Text.TextColor,
                        ["content"] = item.Text.Content
                    };
                    if (item.Number.HasValue) itemDict["number"] = item.Number.Value;
                    if (item.Label is not null) itemDict["label"] = item.Label;
                    if (item.Kids.Count > 0)
                        itemDict["kids"] = item.Kids.Select(ToOdlElement).ToList();
                    return itemDict;
                }).ToList();
                break;
            case FigureElement fig:
                dict["role"] = fig.Role;
                dict["representation"] = fig.Representation;
                if (fig.Source is not null) dict["source"] = fig.Source;
                if (fig.Data is not null) dict["data"] = fig.Data;
                dict["format"] = fig.Format;
                if (fig.ExtractedText is not null) dict["extracted text"] = fig.ExtractedText;
                if (fig.CodeType is not null) dict["code type"] = fig.CodeType;
                if (fig.DecodedText is not null) dict["decoded text"] = fig.DecodedText;
                if (fig.AltSource is not null) dict["alt source"] = fig.AltSource;
                break;
            case CaptionElement cap:
                AddText(dict, cap.Text);
                if (cap.LinkedContentId.HasValue) dict["linked content id"] = cap.LinkedContentId;
                break;
            case SourceNoteElement note:
                AddText(dict, note.Text);
                if (note.LinkedContentId.HasValue) dict["linked content id"] = note.LinkedContentId;
                break;
        }
        return dict;
    }

    private static void AddText(Dictionary<string, object?> dict, TextProperties text)
    {
        dict["font"] = text.Font;
        dict["font size"] = text.FontSize;
        dict["text color"] = text.TextColor;
        dict["content"] = text.Content;
        if (text.HiddenText) dict["hidden text"] = true;
    }
}
