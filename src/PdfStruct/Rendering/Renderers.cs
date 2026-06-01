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

            case ListElement l:
                RenderList(l, sb);
                break;

            case ImageElement img:
                sb.AppendLine(RenderImageMarkdown(img));
                break;

            case CaptionElement cap:
                sb.Append('*').Append(cap.Text.Content).AppendLine("*");
                break;
        }
    }

    private static void RenderTable(TableElement table, StringBuilder sb)
    {
        foreach (var row in table.Rows)
        {
            sb.Append('|');
            foreach (var cell in row.Cells)
            {
                var text = string.Join(" ", cell.Kids.OfType<ParagraphElement>().Select(p => p.Text.Content));
                sb.Append($" {(string.IsNullOrEmpty(text) ? " " : text)} |");
            }
            sb.AppendLine();

            if (row.RowNumber == 1)
            {
                sb.Append('|');
                for (int i = 0; i < row.Cells.Count; i++) sb.Append(" --- |");
                sb.AppendLine();
            }
        }
    }

    private static void RenderList(ListElement list, StringBuilder sb)
    {
        var ordered = list.NumberingStyle is "ordered" or "decimal" or "roman";
        for (int i = 0; i < list.ListItems.Count; i++)
        {
            var item = list.ListItems[i];
            sb.Append(ordered ? $"{i + 1}." : "-");
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
            case ImageElement img:
                sb.Append("    ").AppendLine(RenderImageMarkdown(img));
                break;
        }
    }

    /// <summary>
    /// Renders an image to its Markdown form. Machine-readable codes render as
    /// a labelled placeholder carrying the decoded value (the payload is what
    /// matters for retrieval, not the glyph); ordinary figures render as an
    /// image link when a source path is present, or a bare placeholder.
    /// </summary>
    private static string RenderImageMarkdown(ImageElement img)
    {
        var label = img.Role switch
        {
            "qr-code" => "QR code",
            "barcode" => "Barcode",
            _ => null
        };

        if (label is not null)
            return img.DecodedText is { Length: > 0 } text ? $"[{label}: {text}]" : $"[{label}]";

        return img.Source is not null ? $"![image]({img.Source})" : "[Image]";
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
            case TableElement t:
                dict["number of rows"] = t.NumberOfRows;
                dict["number of columns"] = t.NumberOfColumns;
                if (t.PreviousTableId.HasValue) dict["previous table id"] = t.PreviousTableId;
                if (t.NextTableId.HasValue) dict["next table id"] = t.NextTableId;
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
                    if (item.Kids.Count > 0)
                        itemDict["kids"] = item.Kids.Select(ToOdlElement).ToList();
                    return itemDict;
                }).ToList();
                break;
            case ImageElement img:
                dict["role"] = img.Role;
                if (img.Source is not null) dict["source"] = img.Source;
                if (img.Data is not null) dict["data"] = img.Data;
                dict["format"] = img.Format;
                if (img.CodeType is not null) dict["code type"] = img.CodeType;
                if (img.DecodedText is not null) dict["decoded text"] = img.DecodedText;
                if (img.AltSource is not null) dict["alt source"] = img.AltSource;
                break;
            case CaptionElement cap:
                AddText(dict, cap.Text);
                if (cap.LinkedContentId.HasValue) dict["linked content id"] = cap.LinkedContentId;
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
