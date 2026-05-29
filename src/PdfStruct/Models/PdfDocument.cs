// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace PdfStruct.Models;

/// <summary>
/// Represents the structured extraction result of an entire PDF document.
/// Serialises to a JSON tree of bounding-box-annotated content elements.
/// </summary>
public sealed class PdfDocument
{
    /// <summary>Gets or sets the original file name.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the total page count.</summary>
    public int NumberOfPages { get; set; }

    /// <summary>Gets or sets the PDF author metadata.</summary>
    public string? Author { get; set; }

    /// <summary>Gets or sets the PDF title metadata.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the PDF creation timestamp.</summary>
    public string? CreationDate { get; set; }

    /// <summary>Gets or sets the PDF modification timestamp.</summary>
    public string? ModificationDate { get; set; }

    /// <summary>
    /// Gets the ordered list of all content elements across all pages,
    /// sorted in reading order as determined by the layout analyzer.
    /// </summary>
    public List<ContentElement> Kids { get; set; } = [];
}
