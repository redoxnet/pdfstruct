// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace PdfStruct.Analysis;

/// <summary>
/// Document-wide statistics derived from every <see cref="TextBlock"/> in the
/// document. Computed once before per-page classification and consumed by
/// classifiers that need cross-page signals (font-size and font-weight
/// frequency, median body size).
/// </summary>
/// <remarks>
/// Rarity is positional rank among values strictly above the document mode,
/// not raw frequency. A value appearing once in the rare tail receives the
/// same boost as one appearing many times when both occupy the same rank
/// position — what matters for heading detection is "how unusual is this
/// size for this document," not how many times it happens to appear.
/// </remarks>
public sealed class DocumentStatistics
{
    /// <summary>Rarity table over rounded font sizes across the document.</summary>
    public RarityTable FontSizeRarity { get; }

    /// <summary>
    /// Rarity table over numeric font weights (typically 100..900 in 100
    /// increments) across the document. Weights come from PdfPig's
    /// <c>FontDetails.Weight</c> via <see cref="TextBlock.FontWeight"/>, so
    /// the table can distinguish three or more typographic weight levels —
    /// e.g. body 400, semibold 600, bold 700 — rather than collapsing
    /// everything to a binary regular/bold flag.
    /// </summary>
    public RarityTable FontWeightRarity { get; }

    /// <summary>Median rounded font size across the document — useful as a body-size baseline.</summary>
    public double MedianFontSize { get; }

    /// <summary>The document-wide mode (most frequent rounded font size). Treated as the body-size baseline.</summary>
    public double ModeFontSize => FontSizeRarity.Mode;

    private const double FontSizeRoundingPrecision = 0.5;
    private const double MinFontWeightForRarity = 100.0;
    private const double MaxFontWeightForRarity = 900.0;

    /// <summary>
    /// Initializes statistics from every block in the document.
    /// </summary>
    /// <param name="blocks">All blocks extracted from the document, in any order.</param>
    public DocumentStatistics(IEnumerable<TextBlock> blocks)
    {
        var blockArray = blocks?.ToArray() ?? throw new ArgumentNullException(nameof(blocks));

        var sizes = blockArray
            .Select(b => RoundTo(b.FontSize, FontSizeRoundingPrecision))
            .Where(s => s > 0)
            .ToArray();
        var weights = blockArray
            .Select(b => (double)b.FontWeight)
            .Where(w => w > 0)
            .ToArray();

        FontSizeRarity = new RarityTable(sizes, scoreMin: 6.0, scoreMax: 200.0);
        FontWeightRarity = new RarityTable(weights, scoreMin: MinFontWeightForRarity, scoreMax: MaxFontWeightForRarity);
        MedianFontSize = Median(sizes);
    }

    /// <summary>Returns the rounded font size used for rarity lookup, matching the rounding applied during construction.</summary>
    public double RoundFontSize(double value) => RoundTo(value, FontSizeRoundingPrecision);

    /// <summary>Rounds a value to the supplied precision (e.g. <c>0.5</c> rounds to half-points).</summary>
    private static double RoundTo(double value, double precision) =>
        Math.Round(value / precision) * precision;

    /// <summary>Computes the lower-median of a sample.</summary>
    private static double Median(double[] values)
    {
        if (values.Length == 0) return 0.0;
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted[sorted.Length / 2];
    }
}

/// <summary>
/// Tracks the frequency distribution of a numeric signal (font size or
/// derived font weight) across a document and assigns rank-based rarity
/// boosts to values that exceed the document mode.
/// </summary>
/// <remarks>
/// The rarity boost is <c>(rank + 1) / n</c> where <c>rank</c> is the value's
/// position among rare values sorted ascending and <c>n</c> is the count of
/// rare values. The largest rare value receives boost <c>1.0</c>; the
/// smallest above-mode value receives <c>1/n</c>. Below-mode values receive
/// <c>0</c>.
/// </remarks>
public sealed class RarityTable
{
    private readonly Dictionary<double, int> _frequency = new();
    private readonly double[] _higherScores;

    /// <summary>The most frequent value in the input — treated as the body baseline.</summary>
    public double Mode { get; }

    /// <summary>The number of distinct rare values (above mode, within score window).</summary>
    public int RareCount => _higherScores.Length;

    /// <summary>
    /// Initializes a rarity table from a sample of values.
    /// </summary>
    /// <param name="values">All occurrences of the signal across the document.</param>
    /// <param name="scoreMin">Inclusive lower bound on the score range — values below this never receive a boost.</param>
    /// <param name="scoreMax">Inclusive upper bound on the score range — values above this never receive a boost.</param>
    public RarityTable(IEnumerable<double> values, double scoreMin, double scoreMax)
    {
        ArgumentNullException.ThrowIfNull(values);

        foreach (var v in values)
        {
            _frequency[v] = _frequency.TryGetValue(v, out var count) ? count + 1 : 1;
        }

        Mode = ComputeMode(_frequency);
        _higherScores = _frequency.Keys
            .Where(v => v > Mode && v >= scoreMin && v <= scoreMax)
            .OrderBy(v => v)
            .ToArray();
    }

    /// <summary>
    /// Returns a boost in <c>[0, 1]</c> for a given value. The most modest
    /// rare value receives <c>1/n</c>; the highest rare value receives
    /// <c>1.0</c>. Values at or below the mode, or outside the score window,
    /// receive <c>0</c>.
    /// </summary>
    public double GetBoost(double value)
    {
        if (_higherScores.Length == 0) return 0.0;
        for (var i = 0; i < _higherScores.Length; i++)
        {
            if (_higherScores[i] == value)
                return (double)(i + 1) / _higherScores.Length;
        }
        return 0.0;
    }

    /// <summary>Returns the most frequent key, breaking ties by smaller key (deterministic).</summary>
    private static double ComputeMode(Dictionary<double, int> frequency)
    {
        if (frequency.Count == 0) return double.NaN;
        return frequency
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .First()
            .Key;
    }
}
