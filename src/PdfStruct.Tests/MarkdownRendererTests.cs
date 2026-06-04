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

    [Fact]
    public void RenderList_UsesItemLabelNumber_NotPositionalCounter()
    {
        // A reference list that resumes after an interruption keeps its true
        // numbering ([6], [7], ...) instead of restarting at 1.
        var list = new ListElement { NumberingStyle = "ordered" };
        list.ListItems.Add(ListItemWith(6, "Sixth author. A paper."));
        list.ListItems.Add(ListItemWith(7, "Seventh author. Another paper."));
        var document = new PdfDocument();
        document.Kids.Add(list);

        var markdown = new MarkdownRenderer().Render(document);

        Assert.Contains("6. Sixth author. A paper.", markdown);
        Assert.Contains("7. Seventh author. Another paper.", markdown);
        Assert.DoesNotContain("1. Sixth author", markdown);
    }

    [Fact]
    public void RenderList_FallsBackToPosition_WhenNumberMissing()
    {
        var list = new ListElement { NumberingStyle = "ordered" };
        list.ListItems.Add(ListItemWith(null, "First"));
        list.ListItems.Add(ListItemWith(null, "Second"));
        var document = new PdfDocument();
        document.Kids.Add(list);

        var markdown = new MarkdownRenderer().Render(document);

        Assert.Contains("1. First", markdown);
        Assert.Contains("2. Second", markdown);
    }

    [Fact]
    public void RenderList_NumberedParagraph_PreservesPrintedMarker()
    {
        var list = new ListElement { NumberingStyle = "numbered-paragraph" };
        list.ListItems.Add(NumberedParagraph("[0001]", 1, "본 기술은 MRI 기반 전도도 측정에 관한 것이다."));
        list.ListItems.Add(NumberedParagraph("[0002]", 2, "암 치료필드는 중간 주파수 범위의 전기필드이다."));
        var document = new PdfDocument();
        document.Kids.Add(list);

        var markdown = new MarkdownRenderer().Render(document);

        Assert.Contains("[0001] 본 기술은 MRI 기반 전도도 측정에 관한 것이다.", markdown);
        Assert.Contains("[0002] 암 치료필드는 중간 주파수 범위의 전기필드이다.", markdown);
        Assert.DoesNotContain("1. 본 기술은", markdown);
    }

    private static ListItem ListItemWith(int? number, string content) => new()
    {
        Number = number,
        Text = new TextProperties { Content = content }
    };

    private static ListItem NumberedParagraph(string label, int number, string content) => new()
    {
        Number = number,
        Label = label,
        Text = new TextProperties { Content = content }
    };

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
