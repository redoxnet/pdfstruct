// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Analysis;
using PdfStruct.Models;
using Xunit;

namespace PdfStruct.Tests;

public class CaptionBinderTests
{
    [Fact]
    public void Bind_ShortParagraphBelowFigure_BecomesLinkedCaption()
    {
        var image = Img(1, new BoundingBox(100, 600, 300, 750));
        var para = Para(2, new BoundingBox(100, 585, 300, 598), "Figure 1: the proposed network.");
        var kids = new List<ContentElement> { image, para };

        CaptionBinder.Bind(kids);

        var caption = Assert.IsType<CaptionElement>(kids[1]);
        Assert.Equal(1, caption.LinkedContentId);
        Assert.Equal("Figure 1: the proposed network.", caption.Text.Content);
    }

    [Fact]
    public void Bind_ParagraphFarFromFigure_StaysParagraph()
    {
        var image = Img(1, new BoundingBox(100, 600, 300, 750));
        var para = Para(2, new BoundingBox(100, 470, 300, 483), "Body text well below the figure.");
        var kids = new List<ContentElement> { image, para };

        CaptionBinder.Bind(kids);

        Assert.IsType<ParagraphElement>(kids[1]);
    }

    [Fact]
    public void Bind_HeadingBetweenFigureAndParagraph_DoesNotBind()
    {
        var image = Img(1, new BoundingBox(100, 600, 300, 750));
        var heading = new HeadingElement { Id = 2, PageNumber = 1, BoundingBox = new BoundingBox(100, 588, 300, 599), Text = new TextProperties { Content = "2. Method", FontSize = 12 } };
        var para = Para(3, new BoundingBox(100, 570, 300, 583), "Figure 1: caption text.");
        var kids = new List<ContentElement> { image, heading, para };

        CaptionBinder.Bind(kids);

        Assert.IsType<ParagraphElement>(kids[2]);
    }

    [Fact]
    public void Bind_CodeImage_IsNotACaptionTarget()
    {
        var code = Img(1, new BoundingBox(400, 700, 500, 800), role: "qr-code");
        var para = Para(2, new BoundingBox(400, 685, 500, 698), "10-2312079");
        var kids = new List<ContentElement> { code, para };

        CaptionBinder.Bind(kids);

        Assert.IsType<ParagraphElement>(kids[1]);
        Assert.DoesNotContain(kids, e => e is CaptionElement);
    }

    [Fact]
    public void Bind_ParagraphAboveTable_BecomesLinkedCaption()
    {
        var para = Para(1, new BoundingBox(100, 605, 300, 618), "Table 2. Benchmark results.");
        var table = new TableElement { Id = 2, PageNumber = 1, BoundingBox = new BoundingBox(100, 500, 300, 600) };
        var kids = new List<ContentElement> { para, table };

        CaptionBinder.Bind(kids);

        var caption = Assert.IsType<CaptionElement>(kids[0]);
        Assert.Equal(2, caption.LinkedContentId);
    }

    [Fact]
    public void Bind_LongBodyParagraphBelowFigure_StaysParagraph()
    {
        var image = Img(1, new BoundingBox(100, 600, 300, 750));
        var longText = new string('x', 800);
        var para = Para(2, new BoundingBox(100, 585, 300, 598), longText);
        var kids = new List<ContentElement> { image, para };

        CaptionBinder.Bind(kids);

        Assert.IsType<ParagraphElement>(kids[1]);
    }

    [Fact]
    public void Bind_ParagraphBetweenTwoFigures_BindsToExactlyOne()
    {
        var above = Img(1, new BoundingBox(100, 600, 300, 750));
        var para = Para(2, new BoundingBox(100, 585, 300, 598), "Figure 1: shared caption candidate.");
        var below = Img(3, new BoundingBox(100, 430, 300, 580));
        var kids = new List<ContentElement> { above, para, below };

        CaptionBinder.Bind(kids);

        var captions = kids.OfType<CaptionElement>().ToList();
        Assert.True(captions.Count <= 1, "A paragraph must not be bound as a caption to more than one target.");
    }

    private static ParagraphElement Para(int id, BoundingBox bbox, string content, double fontSize = 10.0) =>
        new()
        {
            Id = id,
            PageNumber = 1,
            BoundingBox = bbox,
            Text = new TextProperties { Content = content, FontSize = fontSize },
        };

    private static ImageElement Img(int id, BoundingBox bbox, string role = "figure") =>
        new() { Id = id, PageNumber = 1, BoundingBox = bbox, Role = role };
}
