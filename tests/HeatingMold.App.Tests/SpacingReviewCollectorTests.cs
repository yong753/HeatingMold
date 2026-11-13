using System;
using System.Collections.Generic;
using System.Linq;
using HeatingMold.App;
using HeatingMold.Core.Geometry;
using Xunit;

namespace HeatingMold.App.Tests;

public sealed class SpacingReviewCollectorTests
{
    private static readonly Func<Vec3, Vec2> PlanarProjection = point => new Vec2(point.X, point.Y);

    [Fact]
    public void Collector_PreservesRunMetadataAcrossAreasSharingTheSameFace()
    {
        SerpentineResult patternA = Pattern(
            0.0,
            HeatingRun(0, new Vec2(0, 0), new Vec2(100, 0)),
            new SerpentineRoutePiece(
                SerpentineRoutePieceKind.TurnConnector, 0, new[] { new Vec2(100, 0), new Vec2(100, 15) }));
        SerpentineResult patternB = Pattern(0.0, HeatingRun(0, new Vec2(0, 15), new Vec2(100, 15)));

        var areas = new List<SpacingAreaCandidate>
        {
            Candidate("A", patternA, new[] { 10, 11 }, new[] { 100.0, 15.0 }, Inputs(14.7, 0.30)),
            Candidate("B", patternB, new[] { 12 }, new[] { 100.0 }, Inputs(24.7, 0.30)),
        };

        var chains = new Dictionary<int, double[]>
        {
            [10] = FlatChain(new Vec2(0, 0), new Vec2(100, 0)),
            [11] = FlatChain(new Vec2(100, 0), new Vec2(100, 15)),
            [12] = FlatChain(new Vec2(0, 15), new Vec2(100, 15)),
        };

        bool collected = SpacingReviewCollector.TryCollect(
            7, 3, PlanarProjection, areas, Chains(chains),
            out List<SpacingRunEntry> runs, out List<string> failures);

        Assert.True(collected);
        Assert.Empty(failures);
        Assert.Equal(2, runs.Count);
        Assert.Equal("A", runs[0].AreaId);
        Assert.Equal(0, runs[0].PieceIndex);
        Assert.Equal(14.7, runs[0].ConfiguredGapMm, 9);
        Assert.Equal(0.30, runs[0].WireDiameterMm, 9);
        Assert.Equal(0.0, runs[0].DirectionDeg, 9);
        Assert.Equal(2, runs[0].Points.Count);
        Assert.Equal("B", runs[1].AreaId);
        Assert.Equal(0, runs[1].PieceIndex);
        Assert.Equal(24.7, runs[1].ConfiguredGapMm, 9);
    }

    [Fact]
    public void Collector_AnalyzesNeighbouringAreasWithDifferentConfiguredGaps()
    {
        SerpentineResult patternA = Pattern(0.0, HeatingRun(0, new Vec2(0, 0), new Vec2(100, 0)));
        SerpentineResult patternB = Pattern(0.0, HeatingRun(0, new Vec2(0, 15), new Vec2(100, 15)));

        var areas = new List<SpacingAreaCandidate>
        {
            Candidate("A", patternA, new[] { 20 }, new[] { 100.0 }, Inputs(10.0, 0.30)),
            Candidate("B", patternB, new[] { 21 }, new[] { 100.0 }, Inputs(30.0, 0.30)),
        };

        var chains = new Dictionary<int, double[]>
        {
            [20] = FlatChain(new Vec2(0, 0), new Vec2(100, 0)),
            [21] = FlatChain(new Vec2(0, 15), new Vec2(100, 15)),
        };

        Assert.True(SpacingReviewCollector.TryCollect(
            7, 3, PlanarProjection, areas, Chains(chains),
            out List<SpacingRunEntry> runs, out List<string> failures));
        Assert.Empty(failures);

        RouteSpacingResult analysis = Analyze(runs);
        Assert.Equal(RouteSpacingStatus.Completed, analysis.Status);

        IReadOnlyList<SpacingPairSample> markers = Collapse(analysis, runs);
        Assert.Single(markers);
        Assert.Equal(14.7, markers[0].ActualOuterGapMm, 6);
    }

    [Fact]
    public void Collector_ReportsMissingCommittedChainsInsteadOfPartialAnalysis()
    {
        SerpentineResult pattern = Pattern(0.0, HeatingRun(0, new Vec2(0, 0), new Vec2(100, 0)));
        var areas = new List<SpacingAreaCandidate>
        {
            Candidate("A", pattern, new[] { 30 }, new[] { 100.0 }, Inputs(5.0, 0.30)),
        };

        bool collected = SpacingReviewCollector.TryCollect(
            7, 3, PlanarProjection, areas, (contextId, revision, pieceIds) => null,
            out List<SpacingRunEntry> runs, out List<string> failures);

        Assert.False(collected);
        Assert.Empty(runs);
        Assert.Contains(failures, failure => failure.Contains("조각 수가 일치하지 않습니다", StringComparison.Ordinal));
    }

    [Fact]
    public void Collector_ReportsCommittedChainReadFailure()
    {
        SerpentineResult pattern = Pattern(0.0, HeatingRun(0, new Vec2(0, 0), new Vec2(100, 0)));
        var areas = new List<SpacingAreaCandidate>
        {
            Candidate("A", pattern, new[] { 31 }, new[] { 100.0 }, Inputs(5.0, 0.30)),
        };

        bool collected = SpacingReviewCollector.TryCollect(
            7, 3, PlanarProjection, areas,
            (contextId, revision, pieceIds) => throw new InvalidOperationException("체인 조회 실패"),
            out List<SpacingRunEntry> runs, out List<string> failures);

        Assert.False(collected);
        Assert.Empty(runs);
        Assert.Contains(failures, failure => failure.Contains("체인 조회 실패", StringComparison.Ordinal));
    }

    [Fact]
    public void Collector_ReportsInconsistentPieceMetadata()
    {
        SerpentineResult pattern = Pattern(
            0.0,
            HeatingRun(0, new Vec2(0, 0), new Vec2(100, 0)),
            HeatingRun(1, new Vec2(0, 5), new Vec2(100, 5)));
        var areas = new List<SpacingAreaCandidate>
        {
            Candidate("A", pattern, new[] { 40 }, new[] { 100.0, 100.0 }, Inputs(5.0, 0.30)),
        };

        bool collected = SpacingReviewCollector.TryCollect(
            7, 3, PlanarProjection, areas, Chains(new Dictionary<int, double[]>()),
            out List<SpacingRunEntry> runs, out List<string> failures);

        Assert.False(collected);
        Assert.Empty(runs);
        Assert.Contains(failures, failure => failure.Contains("조각 ID/길이", StringComparison.Ordinal));
    }

    [Fact]
    public void Collector_ReportsMissingAppliedInputs()
    {
        SerpentineResult pattern = Pattern(0.0, HeatingRun(0, new Vec2(0, 0), new Vec2(100, 0)));
        var areas = new List<SpacingAreaCandidate>
        {
            Candidate("A", pattern, new[] { 50 }, new[] { 100.0 }, appliedInputs: null),
        };

        bool collected = SpacingReviewCollector.TryCollect(
            7, 3, PlanarProjection, areas, Chains(new Dictionary<int, double[]>()),
            out List<SpacingRunEntry> runs, out List<string> failures);

        Assert.False(collected);
        Assert.Empty(runs);
        Assert.Contains(failures, failure => failure.Contains("확정 당시 간격·직경 정보가 없습니다", StringComparison.Ordinal));
    }

    [Fact]
    public void Collector_RejectsNonFiniteRouteCoordinates()
    {
        SerpentineResult pattern = Pattern(0.0, HeatingRun(0, new Vec2(0, 0), new Vec2(100, 0)));
        var areas = new List<SpacingAreaCandidate>
        {
            Candidate("A", pattern, new[] { 60 }, new[] { 100.0 }, Inputs(5.0, 0.30)),
        };
        var chains = new Dictionary<int, double[]>
        {
            [60] = new[] { 0.0, 0.0, 0.0, double.NaN, 0.0, 0.0 },
        };

        bool collected = SpacingReviewCollector.TryCollect(
            7, 3, PlanarProjection, areas, Chains(chains),
            out List<SpacingRunEntry> runs, out List<string> failures);

        Assert.False(collected);
        Assert.Empty(runs);
        Assert.Contains(failures, failure => failure.Contains("좌표", StringComparison.Ordinal));
    }

    [Fact]
    public void Collector_SkipsAreasThatAreNotDisplayed()
    {
        var areas = new List<SpacingAreaCandidate>
        {
            new()
            {
                AreaId = "draft",
                AreaName = "draft",
                Displayed = false,
                Pattern = null,
                NativePieceIds = null,
                RoutePieceLengths = null,
                AppliedInputs = null,
            },
        };

        bool collected = SpacingReviewCollector.TryCollect(
            7, 3, PlanarProjection, areas, Chains(new Dictionary<int, double[]>()),
            out List<SpacingRunEntry> runs, out List<string> failures);

        Assert.True(collected);
        Assert.Empty(runs);
        Assert.Empty(failures);
    }

    [Fact]
    public void Collapse_SeparatesPerpendicularDirections()
    {
        SerpentineResult horizontal = Pattern(0.0, HeatingRun(0, new Vec2(0, 0), new Vec2(100, 0)));
        SerpentineResult vertical = Pattern(90.0, HeatingRun(0, new Vec2(200, 0), new Vec2(200, 100)));
        var areas = new List<SpacingAreaCandidate>
        {
            Candidate("A", horizontal, new[] { 70 }, new[] { 100.0 }, Inputs(5.0, 0.30)),
            Candidate("B", vertical, new[] { 71 }, new[] { 100.0 }, Inputs(5.0, 0.30)),
        };
        var chains = new Dictionary<int, double[]>
        {
            [70] = FlatChain(new Vec2(0, 0), new Vec2(100, 0)),
            [71] = FlatChain(new Vec2(200, 0), new Vec2(200, 100)),
        };

        Assert.True(SpacingReviewCollector.TryCollect(
            7, 3, PlanarProjection, areas, Chains(chains),
            out List<SpacingRunEntry> runs, out List<string> failures));
        Assert.Empty(failures);

        RouteSpacingResult analysis = Analyze(runs);
        Assert.Equal(RouteSpacingStatus.DirectionSeparated, analysis.Status);
        Assert.Equal(2, analysis.DirectionGroups.Count);
        Assert.All(analysis.DirectionGroups, group => Assert.Empty(group.Pairs));
        Assert.Empty(Collapse(analysis, runs));
    }

    [Fact]
    public void Collector_DoesNotModifyCollectedInputs()
    {
        SerpentineResult pattern = Pattern(
            0.0,
            HeatingRun(0, new Vec2(0, 0), new Vec2(100, 0)),
            HeatingRun(1, new Vec2(0, 15), new Vec2(100, 15)));
        int[] pieceIds = { 80, 81 };
        double[] lengths = { 100.0, 100.0 };
        var areas = new List<SpacingAreaCandidate>
        {
            Candidate("A", pattern, pieceIds, lengths, Inputs(14.7, 0.30)),
        };
        var chains = new Dictionary<int, double[]>
        {
            [80] = FlatChain(new Vec2(0, 0), new Vec2(100, 0)),
            [81] = FlatChain(new Vec2(0, 15), new Vec2(100, 15)),
        };

        Assert.True(SpacingReviewCollector.TryCollect(
            7, 3, PlanarProjection, areas, Chains(chains),
            out List<SpacingRunEntry> runs, out List<string> failures));
        Assert.Empty(failures);

        RouteSpacingResult analysis = Analyze(runs);
        Collapse(analysis, runs);

        Assert.Equal(new[] { 80, 81 }, pieceIds);
        Assert.Equal(new[] { 100.0, 100.0 }, lengths);
        Assert.Equal(2, pattern.RoutePieces.Count);
        Assert.Equal(new Vec2(0, 0), pattern.RoutePieces[0].Points[0]);
        Assert.Equal(new Vec2(100, 15), pattern.RoutePieces[1].Points[1]);
        Assert.Equal(14.7, runs[0].ConfiguredGapMm, 9);
    }

    private static RouteSpacingResult Analyze(IReadOnlyList<SpacingRunEntry> runs)
    {
        var inputs = new List<RouteSpacingRunInput>(runs.Count);
        foreach (SpacingRunEntry run in runs)
        {
            inputs.Add(new RouteSpacingRunInput(
                run.AreaId, run.PieceIndex, run.Points, run.ConfiguredGapMm, run.WireDiameterMm, run.DirectionDeg));
        }
        return RouteSpacingAnalyzer.Analyze(inputs, 5.0);
    }

    private static IReadOnlyList<SpacingPairSample> Collapse(RouteSpacingResult result, IReadOnlyList<SpacingRunEntry> runs)
        => SpacingMarkerSelection.Collapse(result, runs.Select(run => run.Points).ToList());

    private static SpacingAreaCandidate Candidate(
        string areaId,
        SerpentineResult pattern,
        int[] pieceIds,
        double[] lengths,
        AppliedGenerationInputs? appliedInputs,
        bool displayed = true)
        => new()
        {
            AreaId = areaId,
            AreaName = areaId,
            Displayed = displayed,
            Pattern = pattern,
            NativePieceIds = pieceIds,
            RoutePieceLengths = lengths,
            AppliedInputs = appliedInputs,
            R = 255,
            G = 0,
            B = 0,
        };

    private static AppliedGenerationInputs Inputs(double outerGapMm, double wireDiameterMm)
        => new() { OuterGapMm = outerGapMm, WireDiameterMm = wireDiameterMm };

    private static SerpentineResult Pattern(double angleDeg, params SerpentineRoutePiece[] pieces)
        => new()
        {
            Path = Array.Empty<Vec2>(),
            Segments = Array.Empty<SerpentineSegment>(),
            LaneCount = pieces.Length,
            AngleDeg = angleDeg,
            TotalLength = pieces.Sum(piece => piece.Length),
            RoutePieces = pieces,
        };

    private static SerpentineRoutePiece HeatingRun(int laneIndex, params Vec2[] points)
        => new(SerpentineRoutePieceKind.HeatingRun, laneIndex, points);

    private static double[] FlatChain(params Vec2[] points)
    {
        var flattened = new double[points.Length * 3];
        for (int i = 0; i < points.Length; i++)
        {
            flattened[i * 3] = points[i].X;
            flattened[i * 3 + 1] = points[i].Y;
            flattened[i * 3 + 2] = 0.0;
        }
        return flattened;
    }

    private static SpacingReviewCollector.CommittedChainReader Chains(Dictionary<int, double[]> byPieceId)
        => (contextId, revision, pieceIds) =>
        {
            var chains = new double[pieceIds.Length][];
            for (int i = 0; i < pieceIds.Length; i++)
            {
                chains[i] = byPieceId.TryGetValue(pieceIds[i], out double[]? chain) ? chain : null!;
            }
            return chains;
        };
}
