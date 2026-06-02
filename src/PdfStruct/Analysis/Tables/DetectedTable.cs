// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Models;

namespace PdfStruct.Analysis.Tables;

/// <summary>
/// A detected table region — the whole-table bounding box, before any cell
/// structure is recovered. Produced by <see cref="TextAwareRuledTableDetector"/> or
/// <see cref="BorderlessTableDetector"/>, merged by <see cref="TableDetector"/>,
/// and emitted as a region-only <see cref="Models.TableElement"/>.
/// </summary>
/// <param name="BoundingBox">The table's bounding box in PDF user space, covering its rules and contained text.</param>
/// <param name="Kind">How the region was found: <c>"ruled"</c> (from ruling lines) or <c>"borderless"</c> (from text-column alignment).</param>
internal sealed record DetectedTable(BoundingBox BoundingBox, string Kind);
