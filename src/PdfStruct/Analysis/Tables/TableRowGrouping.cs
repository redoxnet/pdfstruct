// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Word = PdfStruct.Analysis.Tables.TableCellRecovery.Word;

namespace PdfStruct.Analysis.Tables;

/// <summary>
/// Word-row grouping shared by the table recovery passes: clustering words into
/// baseline rows, and splitting a group whose stacked baselines each open a whole
/// record into separate logical rows. The record test differs per caller, so it
/// is supplied as a delegate while the clustering and the split walk are common.
/// </summary>
internal static class TableRowGrouping
{
    /// <summary>Two words share a baseline when their centres differ by at most this factor of the smaller height.</summary>
    private const double RowBaselineToleranceFactor = 0.4;

    /// <summary>Absolute floor, in PDF points, for the same-baseline tolerance.</summary>
    private const double MinRowBaselineTolerance = 1.5;

    /// <summary>A set of words sharing a baseline, with that baseline's y-position.</summary>
    internal sealed record BaselineRow(double Baseline, IReadOnlyList<Word> Words);

    /// <summary>Clusters words into baseline rows, top to bottom.</summary>
    /// <param name="words">The words to group.</param>
    /// <returns>The baseline rows, top to bottom; each row's <see cref="BaselineRow.Baseline"/> is its mean centre-y.</returns>
    public static List<BaselineRow> ClusterBaselineRows(IReadOnlyList<Word> words)
    {
        var ordered = words.OrderByDescending(w => w.BoundingBox.CenterY).ToList();
        var rows = new List<BaselineRow>();
        var current = new List<Word>();
        var baseline = 0.0;

        foreach (var word in ordered)
        {
            if (current.Count == 0)
            {
                current.Add(word);
                baseline = word.BoundingBox.CenterY;
                continue;
            }

            var smaller = Math.Min(current.Min(w => w.BoundingBox.Height), word.BoundingBox.Height);
            var tolerance = Math.Max(MinRowBaselineTolerance, RowBaselineToleranceFactor * smaller);
            if (baseline - word.BoundingBox.CenterY <= tolerance)
            {
                current.Add(word);
            }
            else
            {
                rows.Add(new BaselineRow(current.Average(w => w.BoundingBox.CenterY), current));
                current = [word];
                baseline = word.BoundingBox.CenterY;
            }
        }
        if (current.Count > 0) rows.Add(new BaselineRow(current.Average(w => w.BoundingBox.CenterY), current));
        return rows;
    }

    /// <summary>
    /// Splits each group whose stacked baselines hold more than one whole record
    /// into separate rows. A baseline begins a new row when the caller's per-group
    /// predicate says it opens a record and the row being built already holds one;
    /// a label-only or wrapped-value continuation stays attached to the row above.
    /// </summary>
    /// <param name="groups">The word groups to split — rule bands, or already-built logical rows.</param>
    /// <param name="recordStartFactory">Given a group's words, returns the predicate deciding whether a baseline's words open a record.</param>
    /// <returns>The groups, each split into one or more rows, top to bottom.</returns>
    public static List<List<Word>> SplitStackedRecords(
        IEnumerable<IReadOnlyList<Word>> groups,
        Func<IReadOnlyList<Word>, Func<IReadOnlyList<Word>, bool>> recordStartFactory)
    {
        var result = new List<List<Word>>();
        foreach (var group in groups)
        {
            var baselines = ClusterBaselineRows(group);
            if (baselines.Count <= 1)
            {
                result.Add(group.ToList());
                continue;
            }

            var opensRecord = recordStartFactory(group);
            var current = new List<Word>();
            var currentHasRecordStart = false;
            foreach (var baseline in baselines)
            {
                var startsRecord = opensRecord(baseline.Words);
                if (current.Count > 0 && currentHasRecordStart && startsRecord)
                {
                    result.Add(current);
                    current = [];
                    currentHasRecordStart = false;
                }

                current.AddRange(baseline.Words);
                currentHasRecordStart |= startsRecord;
            }

            if (current.Count > 0) result.Add(current);
        }

        return result;
    }
}
