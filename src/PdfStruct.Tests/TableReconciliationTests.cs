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
    public void Parse_DetectedTables_CarryClaimedRawText(string fixtureName)
    {
        var doc = ParseFixture(fixtureName);

        var tables = doc.Kids.OfType<TableElement>().ToList();
        Assert.NotEmpty(tables);
        Assert.All(tables, t => Assert.NotEmpty(t.TextLines));
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

    private static PdfDocument ParseFixture(string fixtureName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName);
        Assert.True(File.Exists(path), $"Fixture missing on disk: {path}");
        return new PdfStructParser(new PdfStructOptions()).Parse(path).Document;
    }
}
