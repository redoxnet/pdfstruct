// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Models;
using PdfStruct.Rendering;
using Xunit;

namespace PdfStruct.Tests;

public class MarkdownRendererTests
{
    [Fact]
    public void RenderTable_RowLabelSpanningSubRows_CarriesLabelDown()
    {
        // The row label "Total" spans two sub-rows; only the first carries it, the
        // second's leftmost column is empty. Markdown has no rowspan, so the label
        // is carried down to keep each chunked row self-describing.
        var table = new TableElement
        {
            NumberOfColumns = 3,
            Rows =
            {
                Row(1, Cell(1, 1, "Item"), Cell(1, 2, "Group"), Cell(1, 3, "Value")),
                Row(2, Cell(2, 1, "Total"), Cell(2, 2, "Control"), Cell(2, 3, "10")),
                Row(3, Cell(3, 2, "Intervention"), Cell(3, 3, "20")),
            }
        };
        var document = new PdfDocument();
        document.Kids.Add(table);

        var markdown = new MarkdownRenderer().Render(document);

        Assert.Contains("| Total | Control | 10 |", markdown);
        Assert.Contains("| Total | Intervention | 20 |", markdown);
    }

    [Fact]
    public void RenderTable_LeadingEmptyLabelWithNoPriorLabel_StaysEmpty()
    {
        // A value cell left blank, with no row label above to carry, is left blank
        // rather than filled — carry-down applies only to a real preceding label.
        var table = new TableElement
        {
            NumberOfColumns = 2,
            Rows =
            {
                Row(1, Cell(1, 1, "Metric"), Cell(1, 2, "Value")),
                Row(2, Cell(2, 2, "42")),
            }
        };
        var document = new PdfDocument();
        document.Kids.Add(table);

        var markdown = new MarkdownRenderer().Render(document);

        Assert.Contains("|   | 42 |", markdown);
    }

    private static TableRow Row(int number, params TableCell[] cells)
    {
        var row = new TableRow { RowNumber = number };
        row.Cells.AddRange(cells);
        return row;
    }

    private static TableCell Cell(int row, int column, string text) => new()
    {
        RowNumber = row,
        ColumnNumber = column,
        RowSpan = 1,
        ColumnSpan = 1,
        Kids = { new ParagraphElement { Text = new TextProperties { Content = text } } }
    };
}
