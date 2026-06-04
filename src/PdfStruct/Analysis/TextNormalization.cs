// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.RegularExpressions;

namespace PdfStruct.Analysis;

/// <summary>
/// Text fixes applied where fragments from different layout lines are joined.
/// </summary>
internal static partial class TextNormalization
{
    /// <summary>
    /// Repairs a word hyphenated across a line break. When a line ends with a word
    /// fragment and a trailing hyphen and the next line continues it in lowercase
    /// ("Differ-" + "ence" → "Differ-ence"), joining the fragments leaves a stray
    /// space (or newline) after the hyphen. The space is removed; the hyphen is
    /// kept, since it may be a real hyphen in the source word. A spaced dash
    /// ("A - B") is untouched — the hyphen must sit against the preceding letter —
    /// and a number range ("3- 5") is untouched, since the fragment must be a word.
    /// </summary>
    /// <param name="text">The joined text.</param>
    /// <returns>The text with hyphenated line breaks closed up.</returns>
    public static string RepairHyphenatedBreak(string text) =>
        string.IsNullOrEmpty(text) ? text : HyphenatedBreakPattern().Replace(text, "$1-$2");

    [GeneratedRegex(@"([A-Za-z])-\s+([a-z])")]
    private static partial Regex HyphenatedBreakPattern();
}
