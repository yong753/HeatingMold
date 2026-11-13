using System;
using System.Collections.Generic;
using HeatingMold.App;
using HeatingMold.Core.Geometry;
using Xunit;

namespace HeatingMold.App.Tests;

public sealed class SpacingReviewCoordinatorTests
{
    private static readonly ProjectionFrame Frame = new(
        new Vec3(0, 0, 0),
        new Vec3(0, 0, 1),
        new Vec3(1, 0, 0));

    [Fact]
    public void Fingerprint_IsStableForTheSameSnapshot()
    {
        SpacingRunEntry[] runs = { Run("A", 0, 0.0, 14.7, 0.30, new Vec2(0, 0), new Vec2(50, 5), new Vec2(100, 0)) };

        string first = SpacingAnalysisFingerprint.Build(1, 2, 3, Frame, runs);
        string second = SpacingAnalysisFingerprint.Build(1, 2, 3, Frame, runs);

        Assert.Equal(first, second);
        Assert.StartsWith(SpacingAnalysisFingerprint.PolicyVersion, first, StringComparison.Ordinal);
    }

    [Fact]
    public void Fingerprint_ChangesWhenOnlyAMiddleCoordinateChanges()
    {
        SpacingRunEntry[] before =
        {
            Run("A", 0, 0.0, 14.7, 0.30, new Vec2(0, 0), new Vec2(50, 5), new Vec2(100, 0)),
        };
        SpacingRunEntry[] after =
        {
            Run("A", 0, 0.0, 14.7, 0.30, new Vec2(0, 0), new Vec2(50, 7), new Vec2(100, 0)),
        };

        Assert.NotEqual(
            SpacingAnalysisFingerprint.Build(1, 2, 3, Frame, before),
            SpacingAnalysisFingerprint.Build(1, 2, 3, Frame, after));
    }

    [Fact]
    public void Fingerprint_ChangesWithModelContextFrameAndRunMetadata()
    {
        SpacingRunEntry[] runs = { Run("A", 0, 0.0, 14.7, 0.30, new Vec2(0, 0), new Vec2(100, 0)) };
        string baseline = SpacingAnalysisFingerprint.Build(1, 2, 3, Frame, runs);

        Assert.NotEqual(baseline, SpacingAnalysisFingerprint.Build(2, 2, 3, Frame, runs));
        Assert.NotEqual(baseline, SpacingAnalysisFingerprint.Build(1, 3, 3, Frame, runs));
        Assert.NotEqual(baseline, SpacingAnalysisFingerprint.Build(1, 2, 4, Frame, runs));
        Assert.NotEqual(
            baseline,
            SpacingAnalysisFingerprint.Build(
                1, 2, 3, new ProjectionFrame(new Vec3(1, 0, 0), new Vec3(0, 0, 1), new Vec3(1, 0, 0)), runs));

        SpacingRunEntry[] otherGap = { Run("A", 0, 0.0, 19.7, 0.30, new Vec2(0, 0), new Vec2(100, 0)) };
        SpacingRunEntry[] otherDiameter = { Run("A", 0, 0.0, 14.7, 0.50, new Vec2(0, 0), new Vec2(100, 0)) };
        SpacingRunEntry[] otherDirection = { Run("A", 0, 90.0, 14.7, 0.30, new Vec2(0, 0), new Vec2(100, 0)) };
        SpacingRunEntry[] otherArea = { Run("B", 0, 0.0, 14.7, 0.30, new Vec2(0, 0), new Vec2(100, 0)) };
        SpacingRunEntry[] otherPiece = { Run("A", 5, 0.0, 14.7, 0.30, new Vec2(0, 0), new Vec2(100, 0)) };
        SpacingRunEntry[] otherColor = { Run("A", 0, 0.0, 14.7, 0.30, new[] { new Vec2(0, 0), new Vec2(100, 0) }, 0, 128, 0) };

        Assert.NotEqual(baseline, SpacingAnalysisFingerprint.Build(1, 2, 3, Frame, otherGap));
        Assert.NotEqual(baseline, SpacingAnalysisFingerprint.Build(1, 2, 3, Frame, otherDiameter));
        Assert.NotEqual(baseline, SpacingAnalysisFingerprint.Build(1, 2, 3, Frame, otherDirection));
        Assert.NotEqual(baseline, SpacingAnalysisFingerprint.Build(1, 2, 3, Frame, otherArea));
        Assert.NotEqual(baseline, SpacingAnalysisFingerprint.Build(1, 2, 3, Frame, otherPiece));
        Assert.NotEqual(baseline, SpacingAnalysisFingerprint.Build(1, 2, 3, Frame, otherColor));
    }

    [Fact]
    public void Coordinator_ReusesOnlyTheMatchingFingerprint()
    {
        var coordinator = new SpacingReviewCoordinator();

        long generation = coordinator.BeginRequest("A");
        Assert.False(coordinator.TryGetPublished("A", out SpacingMarker[] missing, out string missingSummary));
        Assert.Empty(missing);
        Assert.Equal(string.Empty, missingSummary);

        Assert.True(coordinator.TryPublish(generation, "A", new[] { Marker() }, "요약"));
        Assert.True(coordinator.TryGetPublished("A", out SpacingMarker[] cached, out string summary));
        Assert.Single(cached);
        Assert.Equal("요약", summary);
        Assert.False(coordinator.TryGetPublished("B", out _, out _));
    }

    [Fact]
    public void Coordinator_RejectsResultsFromAnOlderRequest()
    {
        var coordinator = new SpacingReviewCoordinator();

        long first = coordinator.BeginRequest("A");
        long second = coordinator.BeginRequest("B");

        Assert.True(second > first);
        Assert.False(coordinator.TryPublish(first, "A", new[] { Marker() }, "이전"));
        Assert.False(coordinator.TryGetPublished("A", out _, out _));

        Assert.True(coordinator.TryPublish(second, "B", new[] { Marker() }, "새 결과"));
        Assert.True(coordinator.IsCurrent(second, "B"));
        Assert.False(coordinator.IsCurrent(first, "A"));
        Assert.True(coordinator.TryGetPublished("B", out _, out _));
    }

    [Fact]
    public void Coordinator_Invalidate_DropsCacheAndPendingResults()
    {
        var coordinator = new SpacingReviewCoordinator();
        long generation = coordinator.BeginRequest("A");
        Assert.True(coordinator.TryPublish(generation, "A", new[] { Marker() }, "요약"));

        coordinator.Invalidate();

        Assert.False(coordinator.TryGetPublished("A", out _, out _));
        Assert.False(coordinator.TryPublish(generation, "A", new[] { Marker() }, "요약"));
        Assert.False(coordinator.IsCurrent(generation, "A"));
        Assert.NotEqual(generation, coordinator.Generation);
    }

    private static SpacingRunEntry Run(
        string areaId,
        int pieceIndex,
        double directionDeg,
        double gapMm,
        double diameterMm,
        params Vec2[] points)
        => new()
        {
            AreaId = areaId,
            AreaName = areaId,
            PieceIndex = pieceIndex,
            Points = points,
            ConfiguredGapMm = gapMm,
            WireDiameterMm = diameterMm,
            DirectionDeg = directionDeg,
            R = 255,
            G = 0,
            B = 0,
        };

    private static SpacingRunEntry Run(
        string areaId,
        int pieceIndex,
        double directionDeg,
        double gapMm,
        double diameterMm,
        Vec2[] points,
        byte r,
        byte g,
        byte b)
        => new()
        {
            AreaId = areaId,
            AreaName = areaId,
            PieceIndex = pieceIndex,
            Points = points,
            ConfiguredGapMm = gapMm,
            WireDiameterMm = diameterMm,
            DirectionDeg = directionDeg,
            R = r,
            G = g,
            B = b,
        };

    private static SpacingMarker Marker()
        => new()
        {
            AreaId = "A",
            RunIndex = 0,
            OtherRunIndex = 1,
            At = new Vec2(0, 0),
            Other = new Vec2(0, 10),
            ConfiguredOuterGapMm = 14.7,
            ActualOuterGapMm = 14.5,
            R = 255,
            G = 0,
            B = 0,
        };
}
