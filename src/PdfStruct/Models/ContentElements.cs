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
[JsonDerivedType(typeof(FigureElement), "figure")]
[JsonDerivedType(typeof(CaptionElement), "caption")]
[JsonDerivedType(typeof(SourceNoteElement), "source note")]
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

    /// <summary>
    /// Gets or sets the x-positions, in PDF user space, of the columns the
    /// detector is confident about — interior vertical rules where the table is
    /// ruled, or the stable text column anchors where it is borderless. Asserted
    /// only for a grid; a <see cref="RegionElement"/> records none. Used to reveal
    /// the structure being claimed (e.g. in debug overlays); a column we are not
    /// confident about is never recorded, so no false structure is shown.
    /// </summary>
    public List<double> ColumnAnchors { get; set; } = [];

    /// <summary>
    /// Gets or sets the y-positions, in PDF user space, of the row boundaries the
    /// detector asserts — the horizontal rules of a per-row grid, or the cuts
    /// between baseline rows otherwise. Recorded only for a grid, paired with
    /// <see cref="ColumnAnchors"/> to reveal the row×column structure being
    /// claimed; a <see cref="RegionElement"/> asserts none.
    /// </summary>
    public List<double> RowAnchors { get; set; } = [];

    /// <summary>
    /// Gets or sets the table's raw text rows, top to bottom, captured from the
    /// text lines that fall inside the table region. The region claims this text
    /// so it is not also emitted as overlapping sibling paragraphs; it is
    /// preserved here for retrieval until cell structure is recovered in a later
    /// pass, during which <see cref="Rows"/> stays empty. Linked captions and
    /// source notes are external elements and are not stored here.
    /// </summary>
    public List<string> TextLines { get; set; } = [];

    /// <summary>Gets or sets the previous table fragment ID (cross-page).</summary>
    public int? PreviousTableId { get; set; }

    /// <summary>Gets or sets the next table fragment ID (cross-page).</summary>
    public int? NextTableId { get; set; }
}

/// <summary>
/// A bordered structured region whose internal structure could not be defended
/// as a grid (<see cref="TableElement"/>) — for example a key-value form or
/// front matter whose rules separate sections rather than repeating rows. Its
/// claimed text is preserved verbatim, in reading order, with no asserted rows
/// or cells, so a detected enclosure is never reported as a table it is not.
/// </summary>
public sealed class RegionElement : ContentElement
{
    /// <inheritdoc />
    public override string Type => "region";

    /// <summary>Gets or sets the region's raw text rows, top to bottom, claimed from the lines inside it.</summary>
    public List<string> TextLines { get; set; } = [];
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

    /// <summary>
    /// Gets or sets the integer label recovered from this item's marker
    /// (<c>6</c> for <c>[6]</c> or <c>6.</c>), or <c>null</c> for an
    /// unnumbered (bullet) item. Renderers emit this rather than a positional
    /// counter, so a list that resumes after an interruption keeps its true
    /// numbering instead of restarting at one.
    /// </summary>
    public int? Number { get; set; }

    /// <summary>
    /// Gets or sets the original label token exactly as printed
    /// (<c>[0001]</c>, <c>[6]</c>, <c>6.</c>), or <c>null</c> for a bullet
    /// item. Lets a consumer recover the verbatim marker — essential for
    /// numbered paragraphs, whose zero-padded number is meaningful.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>Gets nested content elements (e.g., sub-lists).</summary>
    public List<ContentElement> Kids { get; set; } = [];
}

/// <summary>
/// A figure: any embedded picture, chart, diagram, or machine-readable code,
/// regardless of whether it is stored as a raster bitmap or drawn as vector
/// graphics. <see cref="Representation"/> records how it was carried in the PDF
/// and <see cref="Role"/> its semantic kind.
/// </summary>
public sealed class FigureElement : ContentElement
{
    /// <inheritdoc />
    public override string Type => "figure";

    /// <summary>
    /// Gets or sets the semantic kind: <c>"chart"</c>, <c>"flowchart"</c>,
    /// <c>"diagram"</c>, <c>"photo"</c>, <c>"screenshot"</c>, <c>"qr-code"</c>,
    /// <c>"barcode"</c>, or <c>"unknown"</c>. Codes are not caption targets.
    /// </summary>
    public string Role { get; set; } = "unknown";

    /// <summary>
    /// Gets or sets how the figure is carried in the PDF: <c>"raster"</c> (an
    /// image XObject), <c>"vector"</c> (path-drawn), <c>"mixed"</c>, or
    /// <c>"rendered-crop"</c> (reconstructed by rasterising the page region).
    /// </summary>
    public string Representation { get; set; } = "raster";

    /// <summary>Gets or sets the relative path to the extracted image file.</summary>
    public string? Source { get; set; }

    /// <summary>Gets or sets the Base64-encoded data URI.</summary>
    public string? Data { get; set; }

    /// <summary>Gets or sets the image format ("png" or "jpeg").</summary>
    public string Format { get; set; } = "png";

    /// <summary>Gets or sets text drawn inside the figure (axis labels, node text, legends); <c>null</c> when none was captured.</summary>
    public string? ExtractedText { get; set; }

    /// <summary>Gets or sets the specific code symbology when <see cref="Role"/> is a code (e.g. <c>"qr-code"</c>, <c>"code-128"</c>, <c>"code-39"</c>); <c>null</c> otherwise.</summary>
    public string? CodeType { get; set; }

    /// <summary>Gets or sets the decoded payload of a machine-readable code; <c>null</c> when not a code or decoding failed.</summary>
    public string? DecodedText { get; set; }

    /// <summary>Gets or sets the provenance of any associated text: <c>"original"</c> (from the PDF), <c>"machine-decoded"</c> (from a code decoder), or <c>"missing"</c>.</summary>
    public string? AltSource { get; set; }
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

/// <summary>
/// A source-attribution note linked to a figure or table — a line such as
/// "자료: …", "출처: …", or "Source: …". Kept distinct from a
/// <see cref="CaptionElement"/> so the caption text and the data-source credit
/// are not conflated.
/// </summary>
public sealed class SourceNoteElement : ContentElement
{
    /// <inheritdoc />
    public override string Type => "source note";

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
