// Copyright (c) Jong Hyun Kim. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using PdfStruct;
using PdfStruct.Analysis;
using PdfStruct.Models;
using Xunit;

namespace PdfStruct.Tests;

public class VectorFigureDetectorTests
{
    private const double PageArea = 600 * 800;

    [Fact]
    public void SelectFigureRegions_RichCluster_Accepted()
    {
        var region = new BoundingBox(100, 100, 400, 400);
        var regions = VectorFigureDetector.SelectFigureRegions(Cluster(region, total: 6, rich: 5), PageArea, []);

        var box = Assert.Single(regions);
        Assert.Equal(region, box);
    }

    [Fact]
    public void SelectFigureRegions_RuleLattice_RejectedByRichShare()
    {
        // A table grid: twenty axis-aligned rules, only two rich paths
        // (rich share 0.1 < the floor) — the lattice must not surface as a figure.
        var region = new BoundingBox(100, 100, 400, 400);
        var regions = VectorFigureDetector.SelectFigureRegions(Cluster(region, total: 20, rich: 2), PageArea, []);

        Assert.Empty(regions);
    }

    [Fact]
    public void SelectFigureRegions_TooFewRichPaths_Rejected()
    {
        var region = new BoundingBox(100, 100, 400, 400);
        var regions = VectorFigureDetector.SelectFigureRegions(Cluster(region, total: 6, rich: 3), PageArea, []);

        Assert.Empty(regions);
    }

    [Fact]
    public void SelectFigureRegions_TooFewPaths_Rejected()
    {
        var region = new BoundingBox(100, 100, 400, 400);
        var regions = VectorFigureDetector.SelectFigureRegions(Cluster(region, total: 5, rich: 5), PageArea, []);

        Assert.Empty(regions);
    }

    [Fact]
    public void SelectFigureRegions_RegionNarrowerThanFloor_Rejected()
    {
        // 20x30 points: shorter side below the dimension floor — a rule fragment.
        var region = new BoundingBox(100, 100, 120, 130);
        var regions = VectorFigureDetector.SelectFigureRegions(Cluster(region, total: 8, rich: 8), PageArea, []);

        Assert.Empty(regions);
    }

    [Fact]
    public void SelectFigureRegions_PageCoveringRegion_Rejected()
    {
        var region = new BoundingBox(10, 10, 590, 790);
        var regions = VectorFigureDetector.SelectFigureRegions(Cluster(region, total: 8, rich: 8), PageArea, []);

        Assert.Empty(regions);
    }

    [Fact]
    public void SelectFigureRegions_OverlapsRasterImage_Rejected()
    {
        var region = new BoundingBox(100, 100, 400, 400);
        var raster = new[] { new BoundingBox(120, 120, 380, 380) };
        var regions = VectorFigureDetector.SelectFigureRegions(Cluster(region, total: 6, rich: 6), PageArea, raster);

        Assert.Empty(regions);
    }

    [Fact]
    public void SuppressRepeatingFigures_RecurringRegion_RemovedEverywhere()
    {
        var logo = new BoundingBox(36, 734, 118, 774);
        var pages = new Dictionary<int, List<DetectedImage>>
        {
            [1] = [Fig(logo)],
            [2] = [Fig(logo)],
            [3] = [Fig(logo)],
            [4] = [Fig(logo)],
        };

        VectorFigureDetector.SuppressRepeatingFigures(pages);

        Assert.All(pages.Values, list => Assert.Empty(list));
    }

    [Fact]
    public void SuppressRepeatingFigures_OneOffRegion_Kept()
    {
        var logo = new BoundingBox(36, 734, 118, 774);
        var chart = new BoundingBox(100, 100, 400, 400);
        var pages = new Dictionary<int, List<DetectedImage>>
        {
            [1] = [Fig(chart), Fig(logo)],
            [2] = [Fig(logo)],
            [3] = [Fig(logo)],
        };

        VectorFigureDetector.SuppressRepeatingFigures(pages);

        Assert.Equal(chart, Assert.Single(pages[1]).BoundingBox);
        Assert.Empty(pages[2]);
        Assert.Empty(pages[3]);
    }

    [Fact]
    public void SuppressRepeatingFigures_TitlePageLogoVariant_RemovedByOverlap()
    {
        // The masthead logo recurs identically on the body pages and appears once,
        // larger, on the title page; the larger variant overlaps the confirmed
        // furniture region and is removed too.
        var recurring = new BoundingBox(36, 734, 118, 774);
        var titleVariant = new BoundingBox(37, 702, 151, 757);
        var pages = new Dictionary<int, List<DetectedImage>>
        {
            [1] = [Fig(titleVariant)],
            [2] = [Fig(recurring)],
            [3] = [Fig(recurring)],
            [4] = [Fig(recurring)],
        };

        VectorFigureDetector.SuppressRepeatingFigures(pages);

        Assert.All(pages.Values, list => Assert.Empty(list));
    }

    [Fact]
    public void SuppressRepeatingFigures_DistinctOverlappingRegions_Kept()
    {
        // Real content figures across pages: a large region on one page contains
        // the area of smaller regions on others. They overlap but no single region
        // recurs at the same place, so none is treated as furniture.
        var big = new BoundingBox(57, 185, 539, 743);
        var small1 = new BoundingBox(140, 185, 539, 462);
        var small2 = new BoundingBox(141, 183, 539, 325);
        var pages = new Dictionary<int, List<DetectedImage>>
        {
            [2] = [Fig(small1)],
            [3] = [Fig(small2)],
            [4] = [Fig(big)],
        };

        VectorFigureDetector.SuppressRepeatingFigures(pages);

        Assert.Single(pages[2]);
        Assert.Single(pages[3]);
        Assert.Single(pages[4]);
    }

    [Fact]
    public void Parse_RulesOnlyFixture_EmitsNoVectorFigures()
    {
        // kr_constitution draws only header/footer rules — no charts or diagrams.
        // The detector must stay silent rather than promote rules to figures.
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "kr_constitution.pdf");
        Assert.True(File.Exists(path), $"Fixture missing on disk: {path}");

        var parser = new PdfStructParser(new PdfStructOptions { ImageOutput = ImageOutputMode.External });
        var result = parser.Parse(path);

        Assert.DoesNotContain(
            result.Document.Kids.OfType<FigureElement>(),
            f => f.Representation == "vector");
    }

    private static List<VectorFigureDetector.CandidatePath> Cluster(BoundingBox region, int total, int rich)
    {
        // All boxes coincide with the region, so they single-link into one cluster
        // whose merged bounds equal the region — isolating the gating policy from
        // the proximity clustering.
        var list = new List<VectorFigureDetector.CandidatePath>(total);
        for (var i = 0; i < total; i++)
            list.Add(new VectorFigureDetector.CandidatePath(region, i < rich));
        return list;
    }

    private static DetectedImage Fig(BoundingBox box) =>
        new(box, Source: null, Representation: "vector");
}
