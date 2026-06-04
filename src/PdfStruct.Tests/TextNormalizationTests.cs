// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Analysis;
using Xunit;

namespace PdfStruct.Tests;

public class TextNormalizationTests
{
    [Theory]
    [InlineData("Differ- ence", "Differ-ence")]
    [InlineData("Adjusted Mean Differ- ence†", "Adjusted Mean Differ-ence†")]
    [InlineData("demo-\ngraphic", "demo-graphic")]
    [InlineData("well- known", "well-known")]
    public void RepairHyphenatedBreak_HyphenatedLineBreak_ClosesUpTheSpace(string input, string expected) =>
        Assert.Equal(expected, TextNormalization.RepairHyphenatedBreak(input));

    [Theory]
    [InlineData("A - B")]          // spaced dash: hyphen not against the letter
    [InlineData("range 3- 5")]     // number range: fragment is not a word
    [InlineData("X- Ray")]         // continuation capitalised, not a wrapped word
    [InlineData("no hyphen here")]
    [InlineData("")]
    public void RepairHyphenatedBreak_NonHyphenation_LeavesTextUnchanged(string input) =>
        Assert.Equal(input, TextNormalization.RepairHyphenatedBreak(input));
}
