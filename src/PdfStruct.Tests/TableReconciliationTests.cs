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
        var cellTexts = table.Rows
            .SelectMany(r => r.Cells)
            .SelectMany(c => c.Kids.OfType<ParagraphElement>())
            .Select(p => p.Text.Content)
            .ToList();

        Assert.DoesNotContain("and post-intervention.", cellTexts);
        Assert.Contains("Scale/ Subscale", cellTexts);
    }

    private static PdfDocument ParseFixture(string fixtureName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName);
        Assert.True(File.Exists(path), $"Fixture missing on disk: {path}");
        return new PdfStructParser(new PdfStructOptions()).Parse(path).Document;
    }
}
