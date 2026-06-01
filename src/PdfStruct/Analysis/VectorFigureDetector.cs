// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct.Models;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics;

namespace PdfStruct.Analysis;

/// <summary>
/// Locates regions of vector path graphics — charts, flowcharts, diagrams —
/// that should surface as first-class <see cref="Models.FigureElement"/> content
/// with <see cref="Models.FigureElement.Representation"/> <c>"vector"</c>, the
/// vector counterpart to <see cref="ImageContentDetector"/>'s raster images.
/// </summary>
/// <remarks>
/// A page carries vector graphics as a flat list of <see cref="PdfPath"/>s with
/// no grouping into figures. The detector recovers figure regions by clustering
/// nearby paths, then keeps only clusters that look like genuine artwork rather
/// than the three things path graphics are otherwise used for: clipping frames,
/// table grids (regular lattices of axis-aligned hairline rules), and page
/// furniture (a logo redrawn at the same spot on every page).
///
/// The chart-versus-table discriminator is the share of <em>rich</em> paths — a
/// path that is filled, contains a Bézier curve, or contains a diagonal line.
/// Table grids and rule separators are composed almost entirely of axis-aligned
/// straight strokes (rich share ≈ 0); charts and diagrams carry filled bars,
/// curved plots, and connector lines (rich share well above zero). Clusters are
/// classified by pure geometry so the policy is unit-testable without a PDF.
/// </remarks>
public static class VectorFigureDetector
{
    /// <summary>A figure region's shorter side must be at least this many PDF points; narrower clusters are rules or separators, not artwork.</summary>
    public const double MinDimensionPoints = 36.0;

    /// <summary>A figure region must cover at least this fraction of the visible page; smaller clusters are bullets, icons, or rule fragments.</summary>
    public const double MinAreaRatio = 0.01;

    /// <summary>A figure region must cover no more than this fraction of the visible page; larger clusters are backdrops or full-page frames.</summary>
    public const double MaxAreaRatio = 0.85;

    /// <summary>A figure region must contain at least this many clustered paths; fewer is a stray rule or box outline.</summary>
    public const int MinPathCount = 6;

    /// <summary>A figure region must contain at least this many rich (filled, curved, or diagonal) paths; this is the floor that excludes pure rule lattices.</summary>
    public const int MinRichPathCount = 4;

    /// <summary>A figure region's rich-path share must be at least this; below it the cluster is a table grid or rule lattice dressed with a few decorations.</summary>
    public const double MinRichShare = 0.15;

    /// <summary>Paths whose clamped boxes are within this many PDF points of one another join the same cluster.</summary>
    public const double ClusterGapPoints = 14.0;

    /// <summary>A vector cluster overlapping an already-detected raster image by at least this share of the smaller box is dropped; the raster carries real pixels.</summary>
    public const double RasterOverlapShare = 0.5;

    /// <summary>A figure region repeated at the same place on at least this many pages is treated as furniture (a logo), not content.</summary>
    public const int RepeatPageThreshold = 3;

    /// <summary>Tolerance, in PDF points, for treating two figure boxes on different pages as the same recurring region.</summary>
    public const double RepeatPositionTolerance = 8.0;

    private const double Epsilon = 0.5;

    /// <summary>
    /// Returns the bounding boxes of the page's vector figure regions, paired
    /// with a <c>null</c> source and <see cref="DetectedImage.Representation"/>
    /// <c>"vector"</c>, excluding any region that overlaps a raster image.
    /// </summary>
    /// <param name="page">The page whose path graphics to inspect.</param>
    /// <param name="rasterImages">Raster images already detected on the page, used to suppress vector clusters that merely re-trace a bitmap's frame.</param>
    /// <returns>One entry per accepted vector figure; empty when the page has none.</returns>
    public static IReadOnlyList<DetectedImage> DetectContentVectorFigures(Page page, IReadOnlyList<DetectedImage> rasterImages)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(rasterImages);

        var pageBounds = ToBoundingBox(page.CropBox.Bounds);
        var pageArea = pageBounds.Area;
        if (pageArea <= 0) return [];

        var candidates = CollectCandidatePaths(page.Paths, pageBounds, pageArea);
        var rasterBoxes = rasterImages.Select(i => i.BoundingBox).ToList();
        var result = new List<DetectedImage>();
        foreach (var region in SelectFigureRegions(candidates, pageArea, rasterBoxes))
            result.Add(new DetectedImage(region, Source: null, Representation: "vector"));
        return result;
    }

    /// <summary>
    /// Clusters candidate paths and applies the figure gates — pure geometry, so
    /// the detection policy is unit-testable without a PDF. Each candidate is its
    /// clamped on-page box paired with whether it is a rich (figure-like) path.
    /// </summary>
    /// <param name="candidates">Non-clipping path boxes with their rich flag.</param>
    /// <param name="pageArea">The visible page area, for the area-ratio gates.</param>
    /// <param name="rasterBoxes">Boxes of raster images on the page; clusters overlapping one are dropped so the raster wins.</param>
    /// <returns>The accepted vector figure regions.</returns>
    internal static IReadOnlyList<BoundingBox> SelectFigureRegions(
        IReadOnlyList<CandidatePath> candidates, double pageArea, IReadOnlyList<BoundingBox> rasterBoxes)
    {
        if (candidates.Count < MinPathCount) return [];

        var result = new List<BoundingBox>();
        foreach (var cluster in BuildClusters(candidates))
        {
            if (!IsFigure(cluster, pageArea)) continue;
            if (OverlapsAnyRaster(cluster.Bounds, rasterBoxes)) continue;
            result.Add(cluster.Bounds);
        }
        return result;
    }

    /// <summary>
    /// Removes vector figures that recur at the same place across at least
    /// <see cref="RepeatPageThreshold"/> pages — a logo or watermark redrawn as
    /// vector furniture rather than a one-off content figure. Operates in place
    /// on the per-page lists.
    /// </summary>
    /// <param name="pageFigures">Per-page detected vector figures, keyed by 1-indexed page number; mutated to drop the recurring regions.</param>
    public static void SuppressRepeatingFigures(IReadOnlyDictionary<int, List<DetectedImage>> pageFigures)
    {
        ArgumentNullException.ThrowIfNull(pageFigures);

        // Group near-identical boxes (strict, so distinct content figures that
        // merely overlap across pages stay separate), then treat any region seen
        // on enough pages as furniture.
        var buckets = new List<(BoundingBox Box, List<int> Pages)>();
        foreach (var (pageNumber, figures) in pageFigures)
            foreach (var figure in figures)
            {
                var hit = buckets.FirstOrDefault(b => SamePlace(b.Box, figure.BoundingBox));
                if (hit.Pages is null)
                    buckets.Add((figure.BoundingBox, [pageNumber]));
                else if (!hit.Pages.Contains(pageNumber))
                    hit.Pages.Add(pageNumber);
            }

        var furniture = buckets.Where(b => b.Pages.Count >= RepeatPageThreshold).Select(b => b.Box).ToList();
        if (furniture.Count == 0) return;

        // Remove the recurring regions and any one-off variant that overlaps one
        // (a logo redrawn slightly larger on the title page), but only once a
        // recurring region has been confirmed — overlap alone never groups figures.
        foreach (var figures in pageFigures.Values)
            figures.RemoveAll(f => furniture.Any(box => SamePlace(f.BoundingBox, box) || Overlaps(f.BoundingBox, box)));
    }

    /// <summary>Collects non-clipping paths with a usable on-page box, tagged as rich or rule.</summary>
    private static List<CandidatePath> CollectCandidatePaths(IReadOnlyList<PdfPath> paths, BoundingBox pageBounds, double pageArea)
    {
        var candidates = new List<CandidatePath>();
        foreach (var path in paths)
        {
            if (path.IsClipping) continue;

            var raw = path.GetBoundingRectangle();
            if (!raw.HasValue) continue;
            if (!TryClamp(ToBoundingBox(raw.Value), pageBounds, out var box)) continue;

            // A single path spanning almost the whole page is a backdrop fill or
            // border, never one figure among several.
            if (box.Area >= pageArea * MaxAreaRatio) continue;

            candidates.Add(new CandidatePath(box, IsRich(path)));
        }
        return candidates;
    }

    /// <summary>
    /// A path is rich — figure-like rather than rule-like — when it is filled or
    /// draws any curve or diagonal segment. Pure axis-aligned straight strokes
    /// (the stuff of table grids and separators) are not rich.
    /// </summary>
    private static bool IsRich(PdfPath path)
    {
        if (path.IsFilled) return true;

        foreach (var subpath in path)
            foreach (var command in subpath.Commands)
                switch (command)
                {
                    case PdfSubpath.CubicBezierCurve:
                    case PdfSubpath.QuadraticBezierCurve:
                        return true;
                    case PdfSubpath.Line line
                        when Math.Abs(line.From.X - line.To.X) > Epsilon
                          && Math.Abs(line.From.Y - line.To.Y) > Epsilon:
                        return true;
                }

        return false;
    }

    /// <summary>Single-link clusters candidate boxes whose gap is within <see cref="ClusterGapPoints"/>.</summary>
    private static List<Cluster> BuildClusters(IReadOnlyList<CandidatePath> candidates)
    {
        var n = candidates.Count;
        var parent = new int[n];
        for (var i = 0; i < n; i++) parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }

        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
                if (Near(candidates[i].Box, candidates[j].Box))
                    parent[Find(i)] = Find(j);

        var groups = new Dictionary<int, Cluster>();
        for (var i = 0; i < n; i++)
        {
            var root = Find(i);
            var c = candidates[i];
            if (groups.TryGetValue(root, out var g))
                groups[root] = new Cluster(g.Bounds.Merge(c.Box), g.Total + 1, g.Rich + (c.Rich ? 1 : 0));
            else
                groups[root] = new Cluster(c.Box, 1, c.Rich ? 1 : 0);
        }
        return [.. groups.Values];
    }

    /// <summary>Applies the geometric and rich-share gates that separate artwork from rules, lattices, and backdrops.</summary>
    private static bool IsFigure(Cluster cluster, double pageArea)
    {
        if (cluster.Total < MinPathCount) return false;
        if (cluster.Rich < MinRichPathCount) return false;
        if ((double)cluster.Rich / cluster.Total < MinRichShare) return false;

        var box = cluster.Bounds;
        if (box.Width < MinDimensionPoints || box.Height < MinDimensionPoints) return false;

        var frac = box.Area / pageArea;
        return frac is >= MinAreaRatio and <= MaxAreaRatio;
    }

    /// <summary>True when a vector cluster overlaps a raster image enough that the raster should win.</summary>
    private static bool OverlapsAnyRaster(BoundingBox cluster, IReadOnlyList<BoundingBox> rasterBoxes)
    {
        foreach (var raster in rasterBoxes)
            if (Overlaps(cluster, raster)) return true;
        return false;
    }

    /// <summary>Clamps a path box to the visible page; returns <c>false</c> when it does not intersect.</summary>
    private static bool TryClamp(BoundingBox raw, BoundingBox pageBounds, out BoundingBox clamped)
    {
        clamped = default;
        var left = Math.Max(raw.Left, pageBounds.Left);
        var bottom = Math.Max(raw.Bottom, pageBounds.Bottom);
        var right = Math.Min(raw.Right, pageBounds.Right);
        var top = Math.Min(raw.Top, pageBounds.Top);
        if (right <= left || top <= bottom) return false;
        clamped = new BoundingBox(left, bottom, right, top);
        return true;
    }

    private static bool Near(BoundingBox a, BoundingBox b)
    {
        var dx = Math.Max(0, Math.Max(a.Left - b.Right, b.Left - a.Right));
        var dy = Math.Max(0, Math.Max(a.Bottom - b.Top, b.Bottom - a.Top));
        return dx <= ClusterGapPoints && dy <= ClusterGapPoints;
    }

    private static bool SamePlace(BoundingBox a, BoundingBox b) =>
        Math.Abs(a.Left - b.Left) <= RepeatPositionTolerance &&
        Math.Abs(a.Bottom - b.Bottom) <= RepeatPositionTolerance &&
        Math.Abs(a.Right - b.Right) <= RepeatPositionTolerance &&
        Math.Abs(a.Top - b.Top) <= RepeatPositionTolerance;

    private static bool Overlaps(BoundingBox a, BoundingBox b)
    {
        var intersection = a.IntersectionArea(b);
        if (intersection <= 0) return false;
        var smaller = Math.Min(a.Area, b.Area);
        return smaller > 0 && intersection / smaller >= RasterOverlapShare;
    }

    private static BoundingBox ToBoundingBox(PdfRectangle rect) =>
        new(rect.Left, rect.Bottom, rect.Right, rect.Top);

    /// <summary>A non-clipping path's clamped on-page box paired with whether it is a rich (filled, curved, or diagonal) path.</summary>
    /// <param name="Box">The path's bounding box clamped to the visible page.</param>
    /// <param name="Rich">Whether the path is figure-like rather than a plain axis-aligned rule.</param>
    internal readonly record struct CandidatePath(BoundingBox Box, bool Rich);

    private readonly record struct Cluster(BoundingBox Bounds, int Total, int Rich);
}
