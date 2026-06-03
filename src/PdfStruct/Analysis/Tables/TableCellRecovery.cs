// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Models;

namespace PdfStruct.Analysis.Tables;

/// <summary>
/// Recovers a grid region's row × column cell structure — including the column
/// span of a merged group header — from the words inside it. Run after a region
/// has been confirmed a grid; the whole-table box, its asserted row boundaries,
/// and its group (interior vertical rule) boundaries are the only structure the
/// detector hands forward, and the cell layout is rebuilt here from word
/// geometry.
/// </summary>
/// <remarks>
/// The unit is the <em>word</em>, not the text line: a borderless multi-level
/// header serialises its leaf columns into one gap-split line that never
/// recovers at the line level, but reading word centres back out resolves it.
/// The model, validated on a four-group academic results table:
///
/// <list type="number">
/// <item>Words are bucketed into rows by the asserted row boundaries.</item>
/// <item>The region is split into <em>group bands</em> by the interior vertical
/// rules — the drawn group separators. With no interior rule the whole region is
/// one band, which is the single-level-header case.</item>
/// <item>Within each band the <em>leaf columns</em> are the word-centre clusters
/// that recur down the rows; a band whose words never align (a free-text row
/// label) collapses to one leaf.</item>
/// <item>Each row's words in a band are assigned to that band's leaves by
/// nearest centre. One word covering the band spans all its leaves (a group
/// label); one word per leaf spans one each (the leaf header and the data). The
/// span falls out of the assignment with no header/data branch.</item>
/// </list>
///
/// <para>
/// Column alignment is read from word centres, so the pass is at its most
/// reliable on centre-aligned numeric grids; row spans are not yet recovered.
/// </para>
/// </remarks>
internal static class TableCellRecovery
{
    /// <summary>A word inside a region: its box and text, the only geometry the recovery reads.</summary>
    /// <param name="BoundingBox">The word's bounding box in PDF user space.</param>
    /// <param name="Text">The word's text.</param>
    internal readonly record struct Word(BoundingBox BoundingBox, string Text);

    /// <summary>Two word centres within this factor of the word height are the same leaf column.</summary>
    private const double ColumnCenterToleranceFactor = 0.6;

    /// <summary>Absolute floor, in PDF points, for the leaf-column centre tolerance.</summary>
    private const double MinColumnCenterTolerance = 1.0;

    /// <summary>A leaf column is asserted only when its centre recurs in at least this many rows; a once-off alignment is not a column.</summary>
    private const int MinRowsForStableColumn = 2;

    /// <summary>An interior vertical rule must sit at least this far inside the region edges to be a group separator rather than the border.</summary>
    private const double InteriorMargin = 2.0;

    /// <summary>At least this share of leaf columns must be filled in a majority of body rows for the recovered grid to be trusted; below it the words did not form a grid.</summary>
    private const double SolidColumnShare = 0.7;

    /// <summary>
    /// Recovers the region's rows and cells from its interior words.
    /// </summary>
    /// <param name="words">The words inside the region, in any order.</param>
    /// <param name="region">The region's bounding box.</param>
    /// <param name="groupBoundaries">The x-positions of the interior vertical rules — the group-band separators; empty for a single-level header.</param>
    /// <param name="horizontalRuleYs">The y-centres of full-width horizontal rules inside the region, used to build logical rows.</param>
    /// <param name="pageNumber">The 1-indexed page the region sits on.</param>
    /// <param name="columnAnchors">Optional detector-provided stable column anchors, used to seed a borderless single-band grid.</param>
    /// <param name="trustDrawnGrid">When the region's columns are witnessed by drawn vertical rules spanning its height, the grid is trusted without the column-occupancy gate — a ruled sub-table with a single sparse data row is still a grid.</param>
    /// <returns>The recovered rows, top to bottom, or an empty list when no structure could be read.</returns>
    public static List<TableRow> Recover(
        IReadOnlyList<Word> words,
        BoundingBox region,
        IReadOnlyList<double> groupBoundaries,
        IReadOnlyList<double> horizontalRuleYs,
        int pageNumber,
        IReadOnlyList<double>? columnAnchors = null,
        bool trustDrawnGrid = false)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(groupBoundaries);
        ArgumentNullException.ThrowIfNull(horizontalRuleYs);
        if (words.Count == 0) return [];

        var rowWords = MergeDashOnlyContinuations(SplitStackedRecordRows(LogicalRowBuilder.Build(words, region, horizontalRuleYs)));
        var bands = BuildBands(region, groupBoundaries);
        var tolerance = Math.Max(MinColumnCenterTolerance, ColumnCenterToleranceFactor * Median(words.Select(w => w.BoundingBox.Height)));

        AssignLeafColumns(bands, rowWords, tolerance, region, columnAnchors ?? []);

        var rows = new List<TableRow>();
        var rowNumber = 0;
        foreach (var inRow in rowWords)
        {
            if (inRow.Count == 0) continue;
            rowNumber++;
            var row = new TableRow { RowNumber = rowNumber };
            foreach (var band in bands)
                AddBandCells(row, band, inRow, rowNumber, pageNumber);
            if (row.Cells.Count > 0) rows.Add(row);
        }

        NormalizeLeadingEmptyColumns(rows);
        if (IsConfidentGrid(rows, requireOccupancy: !trustDrawnGrid)) return rows;

        var slotRows = RecoverFromRuledSlots(rowWords, region, groupBoundaries, pageNumber);
        NormalizeLeadingEmptyColumns(slotRows);
        return IsConfidentGrid(slotRows, requireOccupancy: !trustDrawnGrid) ? slotRows : [];
    }

    /// <summary>
    /// Splits dense ruled-table rows where a detector band swallowed adjacent
    /// records. A stacked baseline is a new record only when it opens the
    /// leftmost label area and also carries values in later columns; label-only
    /// continuation lines stay with the row above.
    /// </summary>
    private static List<List<Word>> SplitStackedRecordRows(List<List<Word>> rows) =>
        TableRowGrouping.SplitStackedRecords(rows, row =>
        {
            var tolerance = Math.Max(MinColumnCenterTolerance, ColumnCenterToleranceFactor * Median(row.Select(w => w.BoundingBox.Height)));
            var firstValueCenter = FirstValueColumnCenter(row);
            var anchorLimit = firstValueCenter is null
                ? row.Min(w => w.BoundingBox.CenterX) + tolerance
                : firstValueCenter.Value - Math.Max(3 * tolerance, 12.0);
            return words => OpensRecord(words, anchorLimit, firstValueCenter, tolerance);
        });

    /// <summary>Returns the centre of the leftmost short-value word in the row, or <c>null</c> when the row carries none.</summary>
    private static double? FirstValueColumnCenter(IReadOnlyList<Word> words)
    {
        var values = words
            .Where(w => LooksLikeShortValue(w.Text))
            .Select(w => w.BoundingBox.CenterX)
            .OrderBy(x => x)
            .ToList();
        return values.Count == 0 ? null : values[0];
    }

    /// <summary>True when a baseline opens a new record: it carries a label in the anchor area and a value in a later column.</summary>
    private static bool OpensRecord(IReadOnlyList<Word> words, double anchorLimit, double? firstValueCenter, double tolerance)
    {
        var hasAnchor = words.Any(w => w.BoundingBox.CenterX <= anchorLimit && w.Text.Any(char.IsLetter));
        var hasDataColumn = firstValueCenter is null
            ? words.Any(w => w.BoundingBox.CenterX > anchorLimit + Math.Max(3 * tolerance, 12.0))
            : words.Any(w => w.BoundingBox.CenterX >= firstValueCenter.Value - tolerance);
        return hasAnchor && hasDataColumn;
    }

    /// <summary>Merges a row made entirely of dashes into the row above — placeholder empty-value markers drawn on a lower baseline, not a record of their own.</summary>
    private static List<List<Word>> MergeDashOnlyContinuations(List<List<Word>> rows)
    {
        var result = new List<List<Word>>();
        foreach (var row in rows)
        {
            if (result.Count > 0 && row.Count > 0 && row.All(w => IsDash(w.Text)))
            {
                result[^1].AddRange(row);
                continue;
            }

            result.Add(row);
        }

        return result;
    }

    /// <summary>True when the text is a single dash glyph (hyphen, en dash, or em dash).</summary>
    private static bool IsDash(string text)
    {
        var trimmed = text.Trim();
        return trimmed is "-" or "–" or "—";
    }

    /// <summary>
    /// The count of leading header rows. A table with a spanning cell carries a
    /// multi-level header — the group-label row(s) down through the leaf sub-header
    /// row beneath them; one without spans has a single header row.
    /// </summary>
    /// <remarks>
    /// The leaf sub-header is not always the row immediately below the spans: a
    /// vertically centred row-label header (a stub such as "Method") can land on
    /// its own baseline between the group-label row and the leaf-header row. So the
    /// header is taken to run through every leading row that is not yet a data
    /// record — a stub-only label row or a leaf-header row that carries no row
    /// label — stopping at the first record that opens the row-label column with a
    /// value beside it.
    /// </remarks>
    /// <param name="rows">The recovered rows, top to bottom.</param>
    /// <returns>The number of leading rows that form the header.</returns>
    public static int HeaderRowCount(IReadOnlyList<TableRow> rows)
    {
        if (rows.Count == 0) return 0;
        var lastSpan = -1;
        var sawSpan = false;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Cells.Any(c => c.ColumnSpan > 1)) { lastSpan = i; sawSpan = true; }
            else if (i > lastSpan + 1) break;
        }
        if (!sawSpan) return 1;

        var header = lastSpan + 1;
        while (header < rows.Count && !IsDataRecord(rows[header])) header++;
        return Math.Min(Math.Max(header, lastSpan + 2), rows.Count);
    }

    /// <summary>True when a row reads as a data record: it opens the row-label column and also carries a value in a later column.</summary>
    private static bool IsDataRecord(TableRow row) =>
        row.Cells.Any(c => c.ColumnNumber == 1) && row.Cells.Any(c => c.ColumnNumber > 1);

    /// <summary>
    /// Removes structural columns at the left edge that no recovered cell covers.
    /// A ruled detector may include a wide empty margin or a decorative leading
    /// slot in the region; keeping it would render a permanently blank first
    /// Markdown column without adding table information.
    /// </summary>
    private static void NormalizeLeadingEmptyColumns(List<TableRow> rows)
    {
        var firstCovered = rows
            .SelectMany(r => r.Cells)
            .DefaultIfEmpty()
            .Min(c => c is null ? int.MaxValue : c.ColumnNumber);
        if (firstCovered <= 1 || firstCovered == int.MaxValue) return;

        var shift = firstCovered - 1;
        foreach (var cell in rows.SelectMany(r => r.Cells))
            cell.ColumnNumber -= shift;
    }

    /// <summary>
    /// Decides whether a recovered grid is trustworthy enough to assert, keeping
    /// the pass precision-first. A real grid fills the same leaf columns down its
    /// body and devotes more rows to data than to headers. A region whose words do
    /// not form a grid — wrapped multi-line headers split into many sparse rows,
    /// or a misaligned block — fails here and falls back to its raw text rather
    /// than emitting a fabricated structure.
    /// </summary>
    /// <param name="rows">The recovered rows.</param>
    /// <param name="requireOccupancy">When false, the column-occupancy share is not required — used when the columns are witnessed by drawn vertical rules, so a ruled sub-table with a single sparse data row still asserts as a grid.</param>
    private static bool IsConfidentGrid(List<TableRow> rows, bool requireOccupancy = true)
    {
        var leafCount = rows
            .SelectMany(r => r.Cells)
            .DefaultIfEmpty()
            .Max(c => c is null ? 0 : c.ColumnNumber + c.ColumnSpan - 1);
        if (rows.Count < 2 || leafCount < 2) return false;

        var headerRows = HeaderRowCount(rows);
        var body = rows.Skip(headerRows).ToList();
        if (body.Count == 0 || headerRows > body.Count) return false;
        if (!requireOccupancy) return true;

        var occupancy = new int[leafCount];
        foreach (var row in body)
            foreach (var cell in row.Cells)
                for (var j = cell.ColumnNumber - 1; j <= cell.ColumnNumber - 2 + cell.ColumnSpan; j++)
                    if (j >= 0 && j < leafCount) occupancy[j]++;

        var solid = occupancy.Count(o => o >= 0.5 * body.Count);
        return solid >= SolidColumnShare * leafCount;
    }

    /// <summary>Splits the region into group bands at the interior vertical rules, left to right.</summary>
    private static List<Band> BuildBands(BoundingBox region, IReadOnlyList<double> groupBoundaries)
    {
        var edges = new List<double> { region.Left };
        edges.AddRange(groupBoundaries
            .Where(x => x > region.Left + InteriorMargin && x < region.Right - InteriorMargin)
            .OrderBy(x => x));
        edges.Add(region.Right);

        var bands = new List<Band>();
        for (var i = 0; i < edges.Count - 1; i++)
            bands.Add(new Band(edges[i], edges[i + 1]));
        return bands;
    }

    /// <summary>
    /// Fills each band's leaf columns and their global column offset. A leaf is a
    /// word-centre cluster recurring in <see cref="MinRowsForStableColumn"/> rows;
    /// a band whose words never align that way (a free-text label column) is one
    /// leaf spanning the band.
    /// </summary>
    private static void AssignLeafColumns(
        List<Band> bands,
        List<List<Word>> rowWords,
        double tolerance,
        BoundingBox region,
        IReadOnlyList<double> columnAnchors)
    {
        var globalStart = 0;
        for (var bandIndex = 0; bandIndex < bands.Count; bandIndex++)
        {
            var band = bands[bandIndex];
            var centersByRow = new List<List<double>>();
            foreach (var row in rowWords)
            {
                var centers = row
                    .Where(w => InBand(w, band))
                    .Select(w => w.BoundingBox.CenterX)
                    .ToList();
                if (centers.Count > 0) centersByRow.Add(centers);
            }

            var stable = StableColumnCenters(centersByRow, tolerance);
            if (bands.Count == 1)
            {
                var seeded = SeededLeafCenters(region, rowWords, columnAnchors, tolerance);
                band.Leaves = seeded.Count > 0 ? seeded : stable.Count >= 1 ? stable : [(band.Left + band.Right) / 2.0];
            }
            else if (bandIndex == 0 && LooksLikeStubLabelBand(rowWords, band, stable.Count))
            {
                band.Leaves = [(band.Left + band.Right) / 2.0];
            }
            else
            {
                band.Leaves = stable.Count >= 1 ? stable : [(band.Left + band.Right) / 2.0];
            }
            band.GlobalStart = globalStart;
            globalStart += band.Leaves.Count;
        }
    }

    /// <summary>
    /// In a borderless single-band grid, reuse the classifier's stable column
    /// anchors as leaf centres so free-text row labels do not create spurious
    /// word-centre leaves. When the first value column is asserted but a stub
    /// label sits to its left, synthesize that stub as the first leaf.
    /// </summary>
    private static List<double> SeededLeafCenters(
        BoundingBox region,
        List<List<Word>> rowWords,
        IReadOnlyList<double> columnAnchors,
        double tolerance)
    {
        var anchors = columnAnchors
            .Where(x => x > region.Left + InteriorMargin && x < region.Right - InteriorMargin)
            .OrderBy(x => x)
            .ToList();
        if (anchors.Count < 2) return [];

        var first = anchors[0];
        var leftWords = rowWords
            .SelectMany(row => row)
            .Where(w => w.BoundingBox.CenterX < first - Math.Max(InteriorMargin, tolerance))
            .Select(w => w.BoundingBox.CenterX)
            .ToList();
        if (leftWords.Count > 0)
            anchors.Insert(0, Median(leftWords));

        return anchors;
    }

    /// <summary>
    /// Detects the common left stub column in a ruled academic table. Method or
    /// item labels often repeat tokens such as "et", "al", and citations at
    /// similar x positions; treating those as leaf columns bloats the grid and
    /// makes the precision gate reject an otherwise valid table. Numeric/value
    /// bands keep their stable leaves.
    /// </summary>
    private static bool LooksLikeStubLabelBand(
        List<List<Word>> rowWords,
        Band band,
        int stableLeafCount)
    {
        if (stableLeafCount <= 1) return false;

        var rows = rowWords
            .Select(row => row.Where(w => InBand(w, band)).ToList())
            .Where(row => row.Count > 0)
            .ToList();
        if (rows.Count < 3) return false;

        var multiWordRows = rows.Count(row => row.Count >= 2);
        var alphabeticRows = rows.Count(row => row.Any(w => w.Text.Any(char.IsLetter)));
        var shortValueRows = rows.Count(row => row.All(w => LooksLikeShortValue(w.Text)));

        return multiWordRows >= 0.45 * rows.Count
               && alphabeticRows >= 0.45 * rows.Count
               && shortValueRows < 0.5 * rows.Count;
    }

    private static bool LooksLikeShortValue(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return false;
        if (trimmed is "-" or "–" or "—") return true;
        return trimmed.All(c => char.IsDigit(c) || c is '.' or ',' or '%' or '*' or '+' or '-' or '(' or ')');
    }

    /// <summary>Clusters word centres across rows and returns the cluster means whose centre recurs in enough rows to be a column.</summary>
    private static List<double> StableColumnCenters(List<List<double>> centersByRow, double tolerance)
    {
        var all = new List<(double Center, int Row)>();
        for (var r = 0; r < centersByRow.Count; r++)
            foreach (var center in centersByRow[r])
                all.Add((center, r));
        all.Sort((a, b) => a.Center.CompareTo(b.Center));
        if (all.Count == 0) return [];

        var stable = new List<double>();
        var clusterCenters = new List<double>();
        var clusterRows = new HashSet<int>();
        var clusterStart = all[0].Center;

        void Flush()
        {
            if (clusterRows.Count >= MinRowsForStableColumn) stable.Add(clusterCenters.Average());
        }

        foreach (var (center, row) in all)
        {
            if (center - clusterStart > tolerance)
            {
                Flush();
                clusterCenters = [];
                clusterRows = [];
                clusterStart = center;
            }
            clusterCenters.Add(center);
            clusterRows.Add(row);
        }
        Flush();
        return stable;
    }

    /// <summary>Assigns a row's words within a band to that band's leaves and appends the resulting cells to the row.</summary>
    private static void AddBandCells(TableRow row, Band band, List<Word> rowWords, int rowNumber, int pageNumber)
    {
        var cellWords = rowWords.Where(w => InBand(w, band)).OrderBy(w => w.BoundingBox.CenterX).ToList();
        if (cellWords.Count == 0) return;

        var byLeaf = new List<Word>[band.Leaves.Count];
        for (var i = 0; i < byLeaf.Length; i++) byLeaf[i] = [];
        foreach (var word in cellWords)
            byLeaf[NearestLeaf(word.BoundingBox.CenterX, band.Leaves)].Add(word);

        var occupied = Enumerable.Range(0, band.Leaves.Count).Where(i => byLeaf[i].Count > 0).ToList();

        // One occupied leaf — a group label, or a band that is a single column —
        // is one cell spanning the whole band. Otherwise each occupied leaf is a
        // cell that extends across the empty leaves up to the next occupied one.
        if (occupied.Count <= 1)
        {
            row.Cells.Add(MakeCell(cellWords, rowNumber, band.GlobalStart + 1, band.Leaves.Count, pageNumber));
            return;
        }

        for (var k = 0; k < occupied.Count; k++)
        {
            var start = occupied[k];
            var end = k + 1 < occupied.Count ? occupied[k + 1] - 1 : band.Leaves.Count - 1;
            row.Cells.Add(MakeCell(byLeaf[start], rowNumber, band.GlobalStart + start + 1, end - start + 1, pageNumber));
        }
    }

    /// <summary>
    /// Conservative fallback for ruled tables: when drawn vertical separators
    /// already define column slots, place each word into its containing x-range
    /// instead of rediscovering leaf columns from repeated word centres.
    /// </summary>
    private static List<TableRow> RecoverFromRuledSlots(
        List<List<Word>> rowWords,
        BoundingBox region,
        IReadOnlyList<double> columnBoundaries,
        int pageNumber)
    {
        var edges = new List<double> { region.Left };
        edges.AddRange(columnBoundaries
            .Where(x => x > region.Left + InteriorMargin && x < region.Right - InteriorMargin)
            .OrderBy(x => x));
        edges.Add(region.Right);
        if (edges.Count < 3) return [];

        var rows = new List<TableRow>();
        var rowNumber = 0;
        foreach (var inRow in rowWords)
        {
            if (inRow.Count == 0) continue;
            rowNumber++;

            var bySlot = new List<Word>[edges.Count - 1];
            for (var i = 0; i < bySlot.Length; i++) bySlot[i] = [];
            foreach (var word in inRow)
            {
                var slot = SlotIndex(word.BoundingBox.CenterX, edges);
                if (slot >= 0) bySlot[slot].Add(word);
            }

            var row = new TableRow { RowNumber = rowNumber };
            for (var slot = 0; slot < bySlot.Length; slot++)
                if (bySlot[slot].Count > 0)
                    row.Cells.Add(MakeCell(bySlot[slot].OrderBy(w => w.BoundingBox.CenterX).ToList(), rowNumber, slot + 1, 1, pageNumber));
            if (row.Cells.Count > 0) rows.Add(row);
        }

        return rows;
    }

    /// <summary>Builds a cell from the words it holds, joined in reading order.</summary>
    private static TableCell MakeCell(List<Word> words, int rowNumber, int columnNumber, int columnSpan, int pageNumber)
    {
        var box = words[0].BoundingBox;
        foreach (var word in words.Skip(1)) box = box.Merge(word.BoundingBox);
        var text = string.Join(" ", WordsInReadingOrder(words)
            .Select(w => w.Text.Trim())
            .Where(t => t.Length > 0));

        return new TableCell
        {
            RowNumber = rowNumber,
            ColumnNumber = columnNumber,
            RowSpan = 1,
            ColumnSpan = columnSpan,
            BoundingBox = box,
            PageNumber = pageNumber,
            Kids =
            {
                new ParagraphElement
                {
                    PageNumber = pageNumber,
                    BoundingBox = box,
                    Text = new TextProperties { Content = text }
                }
            }
        };
    }

    /// <summary>Returns the index of the leaf column whose centre is nearest the given x.</summary>
    private static int NearestLeaf(double centerX, IReadOnlyList<double> leaves)
    {
        var best = 0;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < leaves.Count; i++)
        {
            var distance = Math.Abs(centerX - leaves[i]);
            if (distance < bestDistance) { bestDistance = distance; best = i; }
        }
        return best;
    }

    /// <summary>Returns the index of the column slot whose edge range contains the given x, or -1 when none does. The last slot includes its right edge.</summary>
    private static int SlotIndex(double centerX, IReadOnlyList<double> edges)
    {
        for (var i = 0; i < edges.Count - 1; i++)
        {
            var isLast = i == edges.Count - 2;
            if (centerX >= edges[i] && (centerX < edges[i + 1] || isLast && centerX <= edges[i + 1]))
                return i;
        }
        return -1;
    }

    private static bool InBand(Word word, Band band)
    {
        var center = word.BoundingBox.CenterX;
        return center >= band.Left && center < band.Right;
    }

    /// <summary>Orders a cell's words in reading order — top baseline first, then left to right within each baseline — so a wrapped cell reads correctly.</summary>
    private static IEnumerable<Word> WordsInReadingOrder(IReadOnlyList<Word> words) =>
        TableRowGrouping.ClusterBaselineRows(words).SelectMany(row => row.Words.OrderBy(w => w.BoundingBox.CenterX));

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Where(v => v > 0).OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0.0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>A group band: its x-extent, its leaf columns, and the 0-based global index its first leaf occupies.</summary>
    private sealed class Band(double left, double right)
    {
        public double Left { get; } = left;
        public double Right { get; } = right;
        public IReadOnlyList<double> Leaves { get; set; } = [];
        public int GlobalStart { get; set; }
    }
}
