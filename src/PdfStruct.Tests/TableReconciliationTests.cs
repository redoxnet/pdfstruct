// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct;
using PdfStruct.Models;
using Xunit;

namespace PdfStruct.Tests;

public class TableReconciliationTests
{
    [Theory]
    [InlineData("plos_game_based_education.pdf")]
    [InlineData("plos_utilizing_llm.pdf")]
    public void Parse_DetectedTables_AreRecoveredOrCarryClaimedRawText(string fixtureName)
    {
        var doc = ParseFixture(fixtureName);

        var tables = doc.Kids.OfType<TableElement>().ToList();
        Assert.NotEmpty(tables);
        Assert.All(tables, t =>
        {
            if (t.Rows.Count > 0)
            {
                Assert.Empty(t.TextLines);
                Assert.True(t.NumberOfRows > 0);
                Assert.True(t.NumberOfColumns > 0);
            }
            else
            {
                Assert.NotEmpty(t.TextLines);
            }
        });
    }

    [Theory]
    [InlineData("plos_game_based_education.pdf")]
    [InlineData("plos_utilizing_llm.pdf")]
    public void Parse_NoParagraphOverlapsTableRegion(string fixtureName)
    {
        var doc = ParseFixture(fixtureName);

        var tables = doc.Kids.OfType<TableElement>().ToList();
        var paragraphs = doc.Kids.OfType<ParagraphElement>().ToList();

        foreach (var table in tables)
            foreach (var paragraph in paragraphs)
            {
                if (paragraph.PageNumber != table.PageNumber) continue;
                var area = paragraph.BoundingBox.Area;
                if (area <= 0) continue;
                var share = paragraph.BoundingBox.IntersectionArea(table.BoundingBox) / area;
                Assert.True(share <= 0.5,
                    $"Paragraph {paragraph.Id} overlaps table {table.Id} by {share:P0}; the table should own that text.");
            }
    }

    [Fact]
    public void Parse_TableCaptionContinuation_IsNotClaimedAsTableRow()
    {
        var doc = ParseFixture("plos_game_based_education.pdf");

        var caption = doc.Kids.OfType<CaptionElement>()
            .Single(c => c.PageNumber == 6 && c.Text.Content.StartsWith("Table 1.", StringComparison.Ordinal));
        Assert.Contains("and post-intervention.", caption.Text.Content);

        var table = doc.Kids.OfType<TableElement>()
            .Single(t => t.PageNumber == 6 && t.NumberOfColumns == 3);
        Assert.True(caption.Id < table.Id);
        Assert.Equal(table.Id, caption.LinkedContentId);
        var cellTexts = table.Rows
            .SelectMany(r => r.Cells)
            .SelectMany(c => c.Kids.OfType<ParagraphElement>())
            .Select(p => p.Text.Content)
            .ToList();

        Assert.DoesNotContain("and post-intervention.", cellTexts);
        Assert.Contains("Scale/ Subscale", cellTexts);
    }

    [Fact]
    public void Parse_TableTrailingNotes_AreReleasedNotClaimedAsTableContent()
    {
        // A table's footnotes, explanatory sentences, and DOI line sit below the
        // body but inside the detected box. They must be released to normal
        // elements, never claimed as table rows or raw table text.
        var doc = ParseFixture("plos_game_based_education.pdf");
        const string doi = "doi.org/10.1371/journal.pone.0345292.t003";

        foreach (var table in doc.Kids.OfType<TableElement>())
        {
            Assert.DoesNotContain(table.TextLines, line => line.Contains(doi, StringComparison.Ordinal));
            var cellTexts = table.Rows
                .SelectMany(r => r.Cells)
                .SelectMany(c => c.Kids.OfType<ParagraphElement>())
                .Select(p => p.Text.Content);
            Assert.DoesNotContain(cellTexts, text => text.Contains(doi, StringComparison.Ordinal));
        }

        // The released note survives as a non-table element rather than vanishing.
        var nonTableText = doc.Kids
            .Where(e => e is not TableElement)
            .Select(ElementText)
            .ToList();
        Assert.Contains(nonTableText, text => text.Contains(doi, StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_UtilizingLlm_RuledTablesRecoverCells()
    {
        var doc = ParseFixture("plos_utilizing_llm.pdf");

        var tables = doc.Kids.OfType<TableElement>().ToList();
        Assert.Equal(5, tables.Count);
        Assert.All(tables, table =>
        {
            Assert.NotEmpty(table.Rows);
            Assert.Empty(table.TextLines);
            Assert.True(table.NumberOfRows >= 4);
            Assert.True(table.NumberOfColumns >= 3);
        });
    }

    [Fact]
    public void Parse_GameBasedEducation_RuledResultTablesRecoverCells()
    {
        var doc = ParseFixture("plos_game_based_education.pdf");

        var tables = doc.Kids.OfType<TableElement>().ToList();
        Assert.Equal(5, tables.Count);
        Assert.Equal(4, tables.Count(t => t.Rows.Count > 0));
        Assert.Single(tables, t => t.Rows.Count == 0);

        var beck = tables.Single(t => t.Rows
            .SelectMany(r => r.Cells)
            .Any(c => CellText(c).Contains("Beck Anxiety Inventory", StringComparison.Ordinal)));
        Assert.Equal(3, beck.NumberOfRows);
        Assert.Equal(6, beck.NumberOfColumns);
        Assert.Contains(beck.Rows.SelectMany(r => r.Cells), c => CellText(c) == "Post-test");
        Assert.Contains(beck.Rows.SelectMany(r => r.Cells), c => CellText(c).Contains("−7.04", StringComparison.Ordinal));
    }

    private static string ElementText(ContentElement element) => element switch
    {
        ParagraphElement p => p.Text.Content,
        SourceNoteElement n => n.Text.Content,
        CaptionElement c => c.Text.Content,
        HeadingElement h => h.Text.Content,
        _ => string.Empty
    };

    private static string CellText(TableCell cell) =>
        string.Join(" ", cell.Kids.OfType<ParagraphElement>().Select(p => p.Text.Content));

    private static PdfDocument ParseFixture(string fixtureName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName);
        Assert.True(File.Exists(path), $"Fixture missing on disk: {path}");
        return new PdfStructParser(new PdfStructOptions()).Parse(path).Document;
    }
}
