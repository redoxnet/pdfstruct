// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

#if NET7_0_OR_GREATER
using System.Text.Json.Serialization;
#endif

namespace PdfStruct.Models;

/// <summary>
/// Base class for all content elements extracted from a PDF page.
/// </summary>
#if NET7_0_OR_GREATER
[JsonDerivedType(typeof(ParagraphElement), "paragraph")]
[JsonDerivedType(typeof(HeadingElement), "heading")]
[JsonDerivedType(typeof(TableElement), "table")]
[JsonDerivedType(typeof(ListElement), "list")]
[JsonDerivedType(typeof(ImageElement), "image")]
[JsonDerivedType(typeof(CaptionElement), "caption")]
[JsonDerivedType(typeof(HeaderFooterElement), "header")]
#endif
public abstract class ContentElement
{
    /// <summary>Gets the semantic type of this element.</summary>
    public abstract string Type { get; }

    /// <summary>Gets or sets the unique identifier for cross-referencing.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the 1-indexed page number.</summary>
    public int PageNumber { get; set; }

    /// <summary>Gets or sets the bounding box in PDF points.</summary>
    public BoundingBox BoundingBox { get; set; }
}

/// <summary>
/// Text styling properties shared by text-bearing elements.
/// </summary>
public class TextProperties
{
    /// <summary>Gets or sets the font name.</summary>
    public string Font { get; set; } = string.Empty;

    /// <summary>Gets or sets the font size in points.</summary>
    public double FontSize { get; set; }

    /// <summary>Gets or sets the text color as an RGB string.</summary>
    public string TextColor { get; set; } = "[0.0]";

    /// <summary>Gets or sets the raw text content.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Gets or sets whether this text is hidden (e.g., OCR layer).</summary>
    public bool HiddenText { get; set; }
}

/// <summary>A paragraph element.</summary>
public sealed class ParagraphElement : ContentElement
{
    /// <inheritdoc />
    public override string Type => "paragraph";

    /// <summary>Gets or sets the text properties.</summary>
    public TextProperties Text { get; set; } = new();
}

/// <summary>A heading element with hierarchical level.</summary>
public sealed class HeadingElement : ContentElement
{
    /// <inheritdoc />
    public override string Type => "heading";

    /// <summary>Gets or sets the heading level (1 = h1, 2 = h2, ...). Uncapped on the data model; renderers clamp to their target as needed.</summary>
    public int HeadingLevel { get; set; } = 1;

    /// <summary>Gets or sets the coarse structural label: <c>Doctitle</c> at level 1, <c>Subtitle</c> at level 2 and below.</summary>
    public string Level { get; set; } = "Doctitle";

    /// <summary>Gets or sets the text properties.</summary>
    public TextProperties Text { get; set; } = new();
}

/// <summary>A table element with rows, cells, and optional cross-page linking.</summary>
public sealed class TableElement : ContentElement
{
    /// <inheritdoc />
    public override string Type => "table";

    /// <summary>Gets or sets the row count.</summary>
    public int NumberOfRows { get; set; }

    /// <summary>Gets or sets the column count.</summary>
    public int NumberOfColumns { get; set; }

    /// <summary>Gets the table rows.</summary>
    public List<TableRow> Rows { get; set; } = [];

    /// <summary>Gets or sets the previous table fragment ID (cross-page).</summary>
    public int? PreviousTableId { get; set; }

    /// <summary>Gets or sets the next table fragment ID (cross-page).</summary>
    public int? NextTableId { get; set; }
}

/// <summary>A row within a <see cref="TableElement"/>.</summary>
public sealed class TableRow
{
    /// <summary>Gets the type identifier.</summary>
    public string Type => "table row";

    /// <summary>Gets or sets the 1-indexed row number.</summary>
    public int RowNumber { get; set; }

    /// <summary>Gets the cells in this row.</summary>
    public List<TableCell> Cells { get; set; } = [];
}

/// <summary>A cell within a <see cref="TableRow"/>.</summary>
public sealed class TableCell
{
    /// <summary>Gets the type identifier.</summary>
    public string Type => "table cell";

    /// <summary>Gets or sets the 1-indexed row number.</summary>
    public int RowNumber { get; set; }

    /// <summary>Gets or sets the 1-indexed column number.</summary>
    public int ColumnNumber { get; set; }

    /// <summary>Gets or sets the row span.</summary>
    public int RowSpan { get; set; } = 1;

    /// <summary>Gets or sets the column span.</summary>
    public int ColumnSpan { get; set; } = 1;

    /// <summary>Gets or sets the bounding box.</summary>
    public BoundingBox BoundingBox { get; set; }

    /// <summary>Gets or sets the page number.</summary>
    public int PageNumber { get; set; }

    /// <summary>Gets the nested content elements.</summary>
    public List<ContentElement> Kids { get; set; } = [];
}

/// <summary>A list element (ordered or unordered).</summary>
public sealed class ListElement : ContentElement
{
    /// <inheritdoc />
    public override string Type => "list";

    /// <summary>Gets or sets the numbering style.</summary>
    public string NumberingStyle { get; set; } = "bullet";

    /// <summary>Gets or sets the item count.</summary>
    public int NumberOfListItems { get; set; }

    /// <summary>Gets the list items.</summary>
    public List<ListItem> ListItems { get; set; } = [];

    /// <summary>Gets or sets the previous list fragment ID.</summary>
    public int? PreviousListId { get; set; }

    /// <summary>Gets or sets the next list fragment ID.</summary>
    public int? NextListId { get; set; }
}

/// <summary>An item within a <see cref="ListElement"/>.</summary>
public sealed class ListItem
{
    /// <summary>Gets the type identifier.</summary>
    public string Type => "list item";

    /// <summary>Gets or sets the bounding box.</summary>
    public BoundingBox BoundingBox { get; set; }

    /// <summary>Gets or sets the page number.</summary>
    public int PageNumber { get; set; }

    /// <summary>Gets or sets the text properties.</summary>
    public TextProperties Text { get; set; } = new();

    /// <summary>Gets nested content elements (e.g., sub-lists).</summary>
    public List<ContentElement> Kids { get; set; } = [];
}

/// <summary>An image element.</summary>
public sealed class ImageElement : ContentElement
{
    /// <inheritdoc />
    public override string Type => "image";

    /// <summary>Gets or sets the relative path to the extracted image.</summary>
    public string? Source { get; set; }

    /// <summary>Gets or sets the Base64-encoded data URI.</summary>
    public string? Data { get; set; }

    /// <summary>Gets or sets the image format ("png" or "jpeg").</summary>
    public string Format { get; set; } = "png";
}

/// <summary>A caption element linked to a table, image, or other content.</summary>
public sealed class CaptionElement : ContentElement
{
    /// <inheritdoc />
    public override string Type => "caption";

    /// <summary>Gets or sets the text properties.</summary>
    public TextProperties Text { get; set; } = new();

    /// <summary>Gets or sets the linked content element ID.</summary>
    public int? LinkedContentId { get; set; }
}

/// <summary>A header or footer element.</summary>
public sealed class HeaderFooterElement : ContentElement
{
    private readonly string _type;

    /// <summary>
    /// Initializes a new instance of <see cref="HeaderFooterElement"/>.
    /// </summary>
    /// <param name="isHeader"><c>true</c> for header; <c>false</c> for footer.</param>
    public HeaderFooterElement(bool isHeader = true) => _type = isHeader ? "header" : "footer";

    /// <inheritdoc />
    public override string Type => _type;

    /// <summary>Gets the content elements within this header/footer.</summary>
    public List<ContentElement> Kids { get; set; } = [];
}
