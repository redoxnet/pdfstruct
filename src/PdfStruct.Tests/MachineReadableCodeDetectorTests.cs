// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Analysis;
using PdfStruct.Models;
using Xunit;

namespace PdfStruct.Tests;

public class MachineReadableCodeDetectorTests
{
    [Fact]
    public void Apply_NoCodes_ReturnsInputUnchanged()
    {
        var images = new[] { Image(0, 0, 100, 100) };
        var result = MachineReadableCodeDetector.Apply(images, []);
        Assert.Same(images, result);
    }

    [Fact]
    public void Apply_CodeInsideRasterImage_TagsThatImage()
    {
        var images = new[]
        {
            Image(0, 0, 50, 50),       // unrelated figure
            Image(400, 700, 480, 780), // the code's raster image
        };
        var code = new DecodedCode(new BoundingBox(410, 710, 470, 770), "qr-code", "qr-code", "10-2312079");

        var result = MachineReadableCodeDetector.Apply(images, [code]);

        Assert.Equal(2, result.Count);
        Assert.Equal("figure", result[0].Role);
        Assert.Equal("qr-code", result[1].Role);
        Assert.Equal("qr-code", result[1].CodeType);
        Assert.Equal("10-2312079", result[1].DecodedText);
        Assert.Equal("machine-decoded", result[1].AltSource);
    }

    [Fact]
    public void Apply_CodeMatchingNoImage_SynthesisesSourcelessEntry()
    {
        // A vector-drawn barcode: no raster image contains it.
        var images = new[] { Image(0, 0, 100, 100) };
        var code = new DecodedCode(new BoundingBox(400, 760, 540, 800), "barcode", "code-128", "US011013909B2");

        var result = MachineReadableCodeDetector.Apply(images, [code]);

        Assert.Equal(2, result.Count);
        var synthesised = result[1];
        Assert.Null(synthesised.Source);
        Assert.Equal("barcode", synthesised.Role);
        Assert.Equal("code-128", synthesised.CodeType);
        Assert.Equal("US011013909B2", synthesised.DecodedText);
        Assert.Equal(new BoundingBox(400, 760, 540, 800), synthesised.BoundingBox);
    }

    [Fact]
    public void Apply_TwoCodesOneImage_TagsOnceAndSynthesisesTheOther()
    {
        // Both codes fall inside the same image bbox; one image can carry only
        // one code, so the second is synthesised rather than overwriting.
        var images = new[] { Image(400, 700, 500, 800) };
        var a = new DecodedCode(new BoundingBox(410, 710, 440, 740), "qr-code", "qr-code", "A");
        var b = new DecodedCode(new BoundingBox(460, 760, 490, 790), "qr-code", "qr-code", "B");

        var result = MachineReadableCodeDetector.Apply(images, [a, b]);

        Assert.Equal(2, result.Count);
        Assert.Equal("A", result[0].DecodedText);
        Assert.Null(result[1].Source);
        Assert.Equal("B", result[1].DecodedText);
    }

    private static DetectedImage Image(double left, double bottom, double right, double top) =>
        new(new BoundingBox(left, bottom, right, top), Source: null);
}
