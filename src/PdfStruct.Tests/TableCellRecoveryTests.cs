// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct;
using PdfStruct.Analysis.Tables;
using PdfStruct.Models;
using Xunit;

namespace PdfStruct.Tests;

public class TableCellRecoveryTests
{
    [Fact]
    public void Parse_AcademicFixture_TablesAreEitherRecoveredOrRawText()
    {
        // Precision-first: every table region ends in exactly one consistent
        // state — a recovered cell grid (rows present, raw text cleared, column
        // count asserted) or a raw-text fallback (rows empty, text kept). A
        // half-built grid would mean a recovery that was neither trusted nor
        // cleanly rejected.
        var result = ParseFixture("plos_game_based_education.pdf");

        foreach (var table in result.Document.Kids.OfType<TableElement>())
        {
            if (table.Rows.Count > 0)
            {
                Assert.Empty(table.TextLines);
                Assert.True(table.NumberOfColumns >= 2);
                Assert.All(table.Rows, r => Assert.NotEmpty(r.Cells));
            }
            else
            {
                Assert.NotEmpty(table.TextLines);
            }
        }
    }

    private static PdfStructResult ParseFixture(string fixtureName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName);
        Assert.True(File.Exists(path), $"Fixture missing on disk: {path}");
        return new PdfStructParser(new PdfStructOptions()).Parse(path);
    }

    [Fact]
    public void Recover_MultiLevelHeader_GroupCellsCarryColumnSpans()
    {
        // The four-group academic results table that validated the algorithm: a
        // group-label row over a leaf-header row over three data rows. Vertical
        // rules are drawn only at the group boundaries; the leaf columns recur as
        // centre-aligned value clusters. The group labels must span their leaves.
        var (words, region, groupBoundaries) = MultiLevelTable();

        var rows = TableCellRecovery.Recover(words, region, groupBoundaries, [], pageNumber: 1);

        Assert.Equal(5, rows.Count);

        // Method(1) + IIIT5K(3) + SVT(2) + IC03(3) + IC13(1) = 10 leaf columns.
        var groupRow = rows[0];
        Assert.Equal([1, 3, 2, 3, 1], groupRow.Cells.Select(c => c.ColumnSpan).ToArray());
        Assert.Equal([1, 2, 5, 7, 10], groupRow.Cells.Select(c => c.ColumnNumber).ToArray());
    }

    [Fact]
    public void Recover_LeafHeaderAndData_AreAllSingleSpanCells()
    {
        var (words, region, groupBoundaries) = MultiLevelTable();

        var rows = TableCellRecovery.Recover(words, region, groupBoundaries, [], pageNumber: 1);

        // The leaf header (row 2) and every data row are entirely single-span —
        // no cell merges leaves once the group labels are past.
        foreach (var row in rows.Skip(1))
            Assert.All(row.Cells, c => Assert.Equal(1, c.ColumnSpan));

        // Each data row fills all ten leaves; the leaf-header row has nine, the
        // stub (method) column carrying no sub-header.
        foreach (var dataRow in rows.Skip(2))
            Assert.Equal(10, dataRow.Cells.Count);
        Assert.Equal(9, rows[1].Cells.Count);
    }

    [Fact]
    public void Recover_FreeTextLabelColumn_CollapsesToOneCell()
    {
        var (words, region, groupBoundaries) = MultiLevelTable();

        var rows = TableCellRecovery.Recover(words, region, groupBoundaries, [], pageNumber: 1);

        // The multi-word method name "Almazan et al" never aligns down the rows,
        // so its band is a single leaf and the three words form one cell.
        var firstDataRow = rows[2];
        var methodCell = firstDataRow.Cells[0];
        Assert.Equal(1, methodCell.ColumnNumber);
        Assert.Equal("Almazan et al", CellText(methodCell));
    }

    [Fact]
    public void Recover_NoInteriorRules_SingleBandRecoversLeafColumns()
    {
        // A borderless single-level table: no group boundaries, one band, three
        // leaf columns read from the recurring word centres.
        var words = new List<TableCellRecovery.Word>
        {
            W("Name", 70, 700, 40), W("Age", 170, 700, 30), W("City", 270, 700, 34),
            W("Anna", 70, 686, 40), W("31", 170, 686, 30), W("Seoul", 270, 686, 34),
            W("Bob", 70, 672, 40), W("27", 170, 672, 30), W("Busan", 270, 672, 34),
        };
        var region = new BoundingBox(45, 660, 320, 712);

        var rows = TableCellRecovery.Recover(words, region, [], [], pageNumber: 1);

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(3, r.Cells.Count));
        Assert.All(rows, r => Assert.All(r.Cells, c => Assert.Equal(1, c.ColumnSpan)));
    }

    [Fact]
    public void Recover_RuledTable_TrimsCompletelyEmptyLeadingSlot()
    {
        // A ruled table can carry a decorative or over-wide leading slot before
        // the real stub column. The recovered model should not preserve a
        // permanently empty first column, because Markdown would render it as a
        // blank column with no information.
        var words = new List<TableCellRecovery.Word>
        {
            W("Method", 170, 700, 42), W("SVT", 270, 700, 28), W("IC15", 350, 700, 34),
            W("ABBYY", 170, 686, 44), W("40.5", 270, 686, 30), W("-", 350, 686, 12),
            W("Ours", 170, 672, 32), W("94.3", 270, 672, 30), W("68.8", 350, 672, 30),
        };
        var region = new BoundingBox(50, 660, 400, 712);
        var boundaries = new List<double> { 120, 230, 320 };

        var rows = TableCellRecovery.Recover(words, region, boundaries, [], pageNumber: 1);

        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Equal([1, 2, 3], row.Cells.Select(c => c.ColumnNumber).ToArray()));
    }

    [Fact]
    public void Recover_RuledTable_SplitsStackedCompleteRecords()
    {
        // Dense ruled tables may have a rule band that visually contains more
        // than one data record. Each baseline that has both a stub label and
        // value columns must become its own table row.
        var words = new List<TableCellRecovery.Word>
        {
            W("Alice", 90, 705, 34), W("91", 190, 705, 20),
            W("Bob", 90, 697, 28), W("82", 190, 697, 20),
            W("Cara", 90, 679, 34), W("77", 190, 679, 20),
            W("Dana", 90, 657, 34), W("73", 190, 657, 20),
        };
        var region = new BoundingBox(60, 646, 230, 712);
        var boundaries = new List<double> { 140 };
        var ruleYs = new List<double> { 712, 690, 668, 646 };

        var rows = TableCellRecovery.Recover(words, region, boundaries, ruleYs, pageNumber: 1);

        Assert.Equal(4, rows.Count);
        Assert.Equal(["Alice", "Bob", "Cara", "Dana"], rows.Select(r => CellText(r.Cells[0])).ToArray());
        Assert.Equal(["91", "82", "77", "73"], rows.Select(r => CellText(r.Cells[1])).ToArray());
    }

    [Fact]
    public void Recover_RuledTable_MergesDashOnlyContinuationIntoPreviousRecord()
    {
        // Some PDFs draw placeholder dashes on a slightly lower baseline. They
        // are empty-value markers for the labelled record above, not standalone
        // table rows.
        var words = new List<TableCellRecovery.Word>
        {
            W("Alice", 90, 705, 34), W("91", 190, 705, 20),
            W("-", 250, 697, 12),
            W("Bob", 90, 679, 28), W("82", 190, 679, 20), W("73", 250, 679, 20),
        };
        var region = new BoundingBox(60, 668, 290, 712);
        var boundaries = new List<double> { 140, 220 };
        var ruleYs = new List<double> { 712, 701, 690, 668 };

        var rows = TableCellRecovery.Recover(words, region, boundaries, ruleYs, pageNumber: 1);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Alice", CellText(rows[0].Cells[0]));
        Assert.Equal("-", CellText(rows[0].Cells[2]));
        Assert.Equal("Bob", CellText(rows[1].Cells[0]));
    }

    [Fact]
    public void HeaderRowCount_OrphanStubLabelBetweenGroupAndLeafRows_CountsLeafRowAsHeader()
    {
        // The real multi-level academic table: the row-label header ("Method") is
        // vertically centred over the two-line column header, so it lands on its
        // own baseline between the group-label row and the leaf-header row. The
        // header must still run through the leaf row — otherwise the leaf
        // sub-headers ("50", "1k", "None" …) are mistaken for the first data row.
        var (words, region, groupBoundaries) = MultiLevelTableWithOrphanStub();

        var rows = TableCellRecovery.Recover(words, region, groupBoundaries, [], pageNumber: 1);

        Assert.True(rows.Count >= 3);
        Assert.Contains(rows[0].Cells, c => c.ColumnSpan > 1);
        Assert.Equal("Method", CellText(rows[1].Cells[0]).Trim());
        Assert.Equal(3, TableCellRecovery.HeaderRowCount(rows));
    }

    /// <summary>
    /// The validated four-group table, but with the "Method" stub label dropped
    /// onto its own baseline between the group-label row and the leaf-header row —
    /// the layout the real PDF produces and the flat-baseline synthetic does not.
    /// </summary>
    private static (List<TableCellRecovery.Word> Words, BoundingBox Region,
        List<double> GroupBoundaries) MultiLevelTableWithOrphanStub()
    {
        var words = new List<TableCellRecovery.Word>
        {
            // Group-label row (y = 636) — without the "Method" stub.
            W("IIIT5K", 251, 636), W("SVT", 334, 636), W("IC03", 418, 636), W("IC13", 486, 636),

            // Stub label on its own middle baseline (y = 629).
            W("Method", 146, 629),

            // Leaf-header row (y = 622).
            W("50", 218, 622), W("1k", 249, 622), W("None", 282, 622),
            W("50", 316, 622), W("None", 350, 622),
            W("50", 384, 622), W("Full", 415, 622), W("None", 449, 622),
            W("None", 486, 622),

            // Data rows (y = 600, 586, 572) — distinct, non-aligning method labels.
            W("Almazan", 130, 600), W("et", 165, 600), W("al", 185, 600),
            W("91.2", 218, 600), W("82.1", 249, 600), W("-", 282, 600),
            W("89.2", 316, 600), W("-", 350, 600),
            W("88.4", 384, 600), W("-", 415, 600), W("90.1", 449, 600),
            W("-", 486, 600),

            W("Jaderberg", 140, 586), W("et", 178, 586), W("al", 195, 586),
            W("95.5", 218, 586), W("89.6", 249, 586), W("80.3", 282, 586),
            W("93.2", 316, 586), W("71.7", 350, 586),
            W("97.8", 384, 586), W("97.0", 415, 586), W("93.1", 449, 586),
            W("90.8", 486, 586),

            W("Ours", 120, 572),
            W("96.8", 218, 572), W("94.4", 249, 572), W("83.7", 282, 572),
            W("95.7", 316, 572), W("82.2", 350, 572),
            W("98.7", 384, 572), W("98.0", 415, 572), W("95.0", 449, 572),
            W("92.4", 486, 572),
        };
        var region = new BoundingBox(91, 565, 504, 645);
        var groupBoundaries = new List<double> { 202, 300, 368, 467 };
        return (words, region, groupBoundaries);
    }

    [Fact]
    public void Recover_RuledTableWithWrappedHeader_KeepsHeaderAsOneRow()
    {
        // A ruled table whose header wraps to two baselines: "Control" / "Mean SE)"
        // in one column, "Diff" / "ence (95%" in another. The drawn row rules put
        // both header baselines in one band, so the header is one row. The
        // continuation baseline carries letter words on the left and a value-like
        // token ("(95%") on the right, which must not be mistaken for a new record
        // that splits the merged header.
        var words = new List<TableCellRecovery.Word>
        {
            // Header line 1 (y = 702) and its wrapped line 2 (y = 692).
            W("Method", 70, 702, 44), W("Control", 150, 702, 40), W("Diff", 250, 702, 28), W("p", 320, 702, 10),
            W("Mean", 148, 692, 30), W("SE)", 172, 692, 20), W("ence", 245, 692, 26), W("(95%", 315, 692, 26),

            // Data rows (y = 679, 665, 651).
            W("Alpha", 70, 679, 40), W("10.0", 150, 679, 26), W("1.0", 250, 679, 22), W("0.01", 320, 679, 26),
            W("Beta", 70, 665, 34), W("20.0", 150, 665, 26), W("2.0", 250, 665, 22), W("0.02", 320, 665, 26),
            W("Gamma", 70, 651, 44), W("30.0", 150, 651, 26), W("3.0", 250, 651, 22), W("0.03", 320, 651, 26),
        };
        var region = new BoundingBox(50, 644, 350, 712);
        var groupBoundaries = new List<double> { 110, 210, 290 };
        var ruleYs = new List<double> { 710, 686, 672, 658, 644 };

        var rows = TableCellRecovery.Recover(words, region, groupBoundaries, ruleYs, pageNumber: 1);

        // One header row plus three data rows — the wrapped header line did not
        // become a fifth row.
        Assert.Equal(4, rows.Count);
        Assert.Equal("Control Mean SE)", CellText(rows[0].Cells[1]));
        Assert.Equal("Alpha", CellText(rows[1].Cells[0]));
    }

    private static string CellText(TableCell cell) =>
        string.Join(" ", cell.Kids.OfType<ParagraphElement>().Select(p => p.Text.Content));

    /// <summary>
    /// Builds the validated four-group results table as words plus the asserted
    /// group (vertical-rule) boundaries. Group separators sit at
    /// x = 202, 300, 368, 467; leaf values are centre-aligned and recur down the
    /// rows; the method column is free text that never aligns. Logical rows are
    /// recovered from the word baselines, so no row boundaries are supplied.
    /// </summary>
    private static (List<TableCellRecovery.Word> Words, BoundingBox Region,
        List<double> GroupBoundaries) MultiLevelTable()
    {
        var words = new List<TableCellRecovery.Word>
        {
            // Group-label row (y = 636).
            W("Method", 146, 636), W("IIIT5K", 251, 636), W("SVT", 334, 636), W("IC03", 418, 636), W("IC13", 486, 636),

            // Leaf-header row (y = 622).
            W("50", 218, 622), W("1k", 249, 622), W("None", 282, 622),
            W("50", 316, 622), W("None", 350, 622),
            W("50", 384, 622), W("Full", 415, 622), W("None", 449, 622),
            W("None", 486, 622),

            // Data rows (y = 600, 586, 572) — distinct, non-aligning method labels.
            W("Almazan", 130, 600), W("et", 165, 600), W("al", 185, 600),
            W("91.2", 218, 600), W("82.1", 249, 600), W("-", 282, 600),
            W("89.2", 316, 600), W("-", 350, 600),
            W("88.4", 384, 600), W("-", 415, 600), W("90.1", 449, 600),
            W("-", 486, 600),

            W("Jaderberg", 140, 586), W("et", 178, 586), W("al", 195, 586),
            W("95.5", 218, 586), W("89.6", 249, 586), W("80.3", 282, 586),
            W("93.2", 316, 586), W("71.7", 350, 586),
            W("97.8", 384, 586), W("97.0", 415, 586), W("93.1", 449, 586),
            W("90.8", 486, 586),

            W("Ours", 120, 572),
            W("96.8", 218, 572), W("94.4", 249, 572), W("83.7", 282, 572),
            W("95.7", 316, 572), W("82.2", 350, 572),
            W("98.7", 384, 572), W("98.0", 415, 572), W("95.0", 449, 572),
            W("92.4", 486, 572),
        };
        var region = new BoundingBox(91, 565, 504, 645);
        var groupBoundaries = new List<double> { 202, 300, 368, 467 };
        return (words, region, groupBoundaries);
    }

    private static TableCellRecovery.Word W(string text, double centerX, double centerY, double width = 20, double height = 8) =>
        new(new BoundingBox(centerX - width / 2, centerY - height / 2, centerX + width / 2, centerY + height / 2), text);
}
