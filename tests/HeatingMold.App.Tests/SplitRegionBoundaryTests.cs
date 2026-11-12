using System;
using System.Collections.Generic;
using System.Linq;
using HeatingMold.App;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Tests;

/// <summary>
/// 분할 영역 경계의 artificial 절단선 provenance와 포함 판정을 검증한다.
/// CAD edge 참조는 네이티브 모델이 필요하므로 비워 두고, 절단선과 외곽 폴리곤만으로
/// 결정되는 동작(분할 구간, parameter 왕복, 자식 영역 포함 판정)을 확인한다.
/// </summary>
public class SplitRegionBoundaryTests
{
    private static readonly Vec2[] Parent =
    {
        new(0, 0), new(200, 0), new(200, 100), new(0, 100)
    };

    private static readonly (double MinX, double MinY, double MaxX, double MaxY)[] NoExclusions =
        Array.Empty<(double, double, double, double)>();

    private static SplitRegionBoundary2D CreateBoundary(
        IReadOnlyList<Vec2> region, params (Vec2 A, Vec2 B)[] cuts)
    {
        var cad = new CadExitBoundary2D(1, 1, Array.Empty<ExitEdgeReference>());
        bool created = SplitRegionBoundary2D.TryCreate(cad, Parent, region, NoExclusions, cuts,
            out SplitRegionBoundary2D boundary, out string failure);
        Assert.True(created, failure);
        return boundary;
    }

    private static AreaSplitResult Split(int count) =>
        AreaSplitter.Split(Parent, NoExclusions, PatternDirection.FrameX, 0, 50, count);

    [Fact]
    public void HorizontalCut_CreatesOneSplitSegmentPerChild()
    {
        AreaSplitResult split = Split(2);
        Assert.True(split.Succeeded, split.Failure);
        Assert.Equal(2, split.Bands.Count);

        var cuts = new (Vec2, Vec2)[] { (new Vec2(-10, 50), new Vec2(210, 50)) };
        SplitRegionBoundary2D lower = CreateBoundary(split.Bands[0].Outer, cuts);
        SplitRegionBoundary2D upper = CreateBoundary(split.Bands[1].Outer, cuts);

        Assert.Single(lower.Segments);
        Assert.Single(upper.Segments);
        Assert.Equal(1, lower.Segments[0].Binding.WorkingContextId);
        Assert.Equal(1, lower.Segments[0].Binding.WorkingContextRevision);

        // 절단선은 자식 외곽선 구간으로 잘린다: (0,50)-(200,50).
        Assert.Equal(0.0, Math.Min(lower.Segments[0].A.X, lower.Segments[0].B.X), 6);
        Assert.Equal(200.0, Math.Max(lower.Segments[0].A.X, lower.Segments[0].B.X), 6);
        Assert.Equal(50.0, lower.Segments[0].A.Y, 6);
        Assert.Equal(50.0, lower.Segments[0].B.Y, 6);
    }

    [Fact]
    public void VerticalCut_CreatesLeftRightSplitSegments()
    {
        AreaSplitResult split = AreaSplitter.Split(Parent, NoExclusions, PatternDirection.FrameY, 0, 50, 2);
        Assert.True(split.Succeeded, split.Failure);
        Assert.False(split.SplitAxisIsY);

        var cuts = new (Vec2, Vec2)[] { (new Vec2(100, -10), new Vec2(100, 110)) };
        foreach (AreaSplitBand band in split.Bands)
        {
            SplitRegionBoundary2D boundary = CreateBoundary(band.Outer, cuts);
            Assert.Single(boundary.Segments);
            Assert.Equal(100.0, boundary.Segments[0].A.X, 6);
            Assert.Equal(100.0, boundary.Segments[0].B.X, 6);
        }
    }

    [Fact]
    public void ThreeBands_MiddleBandHasTwoSplitSegments()
    {
        AreaSplitResult split = Split(3);
        Assert.True(split.Succeeded, split.Failure);
        Assert.Equal(2, split.Cuts.Count);

        var cuts = new List<(Vec2, Vec2)>();
        foreach (double cut in split.Cuts)
            cuts.Add((new Vec2(-10, cut), new Vec2(210, cut)));

        AreaSplitBand middle = split.Bands[1];
        SplitRegionBoundary2D boundary = CreateBoundary(middle.Outer, cuts.ToArray());
        Assert.Equal(2, boundary.Segments.Count);
        Assert.NotEqual(boundary.Segments[0].A.Y, boundary.Segments[1].A.Y);
    }

    [Fact]
    public void NonIntersectingCut_ProducesNoSplitSegment()
    {
        AreaSplitResult split = Split(2);
        var cuts = new (Vec2, Vec2)[] { (new Vec2(-10, 500), new Vec2(210, 500)) };
        SplitRegionBoundary2D boundary = CreateBoundary(split.Bands[0].Outer, cuts);
        Assert.Empty(boundary.Segments);
    }

    [Fact]
    public void Containment_AcceptsSegmentInsideChildOnly()
    {
        AreaSplitResult split = Split(2);
        var cuts = new (Vec2, Vec2)[] { (new Vec2(-10, 50), new Vec2(210, 50)) };
        SplitRegionBoundary2D lower = CreateBoundary(split.Bands[0].Outer, cuts);
        SplitRegionBoundary2D upper = CreateBoundary(split.Bands[1].Outer, cuts);

        ExactRegionContainment lowerContainment = new(lower.CreateContainment());
        ExactRegionContainment upperContainment = new(upper.CreateContainment());

        var insideLower = new (Vec2 A, Vec2 B)[] { (new Vec2(20, 10), new Vec2(180, 10)) };
        Assert.True(lowerContainment.ValidateSegments(insideLower, NoExclusions, out string insideFailure),
            insideFailure);

        var insideUpper = new (Vec2 A, Vec2 B)[] { (new Vec2(20, 90), new Vec2(180, 90)) };
        Assert.True(upperContainment.ValidateSegments(insideUpper, NoExclusions, out string upperFailure),
            upperFailure);

        // 다른 쪽 영역의 구간은 이 자식 영역 밖이다.
        Assert.False(lowerContainment.ValidateSegments(insideUpper, NoExclusions, out _));
        Assert.False(upperContainment.ValidateSegments(insideLower, NoExclusions, out _));
    }

    [Fact]
    public void Containment_RejectsSegmentCrossingCutLine()
    {
        AreaSplitResult split = Split(2);
        var cuts = new (Vec2, Vec2)[] { (new Vec2(-10, 50), new Vec2(210, 50)) };
        SplitRegionBoundary2D lower = CreateBoundary(split.Bands[0].Outer, cuts);

        var crossing = new (Vec2 A, Vec2 B)[] { (new Vec2(20, 10), new Vec2(180, 90)) };

        Assert.False(new ExactRegionContainment(lower.CreateContainment())
            .ValidateSegments(crossing, NoExclusions, out string failure));
        Assert.False(string.IsNullOrEmpty(failure));
    }

    [Fact]
    public void ProjectSplitLine_SnapsOntoLineAndKeepsBinding()
    {
        AreaSplitResult split = Split(2);
        var cuts = new (Vec2, Vec2)[] { (new Vec2(-10, 50), new Vec2(210, 50)) };
        SplitRegionBoundary2D boundary = CreateBoundary(split.Bands[0].Outer, cuts);
        SplitLineBinding binding = boundary.Segments[0].Binding;

        ExitBoundaryResolution projected = boundary.ProjectSplitLine(binding, new Vec2(50, 54), 5.0);

        Assert.True(projected.Succeeded, projected.Failure.ToString());
        Assert.Equal(50.0, projected.Point!.Value.Point.X, 6);
        Assert.Equal(50.0, projected.Point.Value.Point.Y, 6);
        SplitLineBinding saved = projected.Point.Value.SplitBinding!.Value;
        Assert.Equal(binding.AX, saved.AX, 9);
        Assert.Equal(binding.AY, saved.AY, 9);
        Assert.Equal(binding.BX, saved.BX, 9);
        Assert.Equal(binding.BY, saved.BY, 9);
        Assert.Equal(binding.FirstParameter, saved.FirstParameter, 9);
        Assert.Equal(binding.LastParameter, saved.LastParameter, 9);
        Assert.InRange(saved.Parameter, saved.FirstParameter, saved.LastParameter);
    }

    [Fact]
    public void EvaluateSplitLine_RoundTripsProjectedPoint()
    {
        AreaSplitResult split = Split(2);
        var cuts = new (Vec2, Vec2)[] { (new Vec2(-10, 50), new Vec2(210, 50)) };
        SplitRegionBoundary2D boundary = CreateBoundary(split.Bands[0].Outer, cuts);

        ExitBoundaryResolution projected = boundary.ProjectSplitLine(
            boundary.Segments[0].Binding, new Vec2(120, 47), 5.0);
        Assert.True(projected.Succeeded);

        ExitBoundaryResolution evaluated = boundary.EvaluateSplitLine(
            projected.Point!.Value.SplitBinding!.Value);

        Assert.True(evaluated.Succeeded);
        Assert.Equal(projected.Point.Value.Point.X, evaluated.Point!.Value.Point.X, 6);
        Assert.Equal(projected.Point.Value.Point.Y, evaluated.Point.Value.Point.Y, 6);
    }

    [Fact]
    public void EvaluateSplitLine_RejectsDifferentLineAndStaleContext()
    {
        AreaSplitResult split = Split(2);
        var cuts = new (Vec2, Vec2)[] { (new Vec2(-10, 50), new Vec2(210, 50)) };
        SplitRegionBoundary2D boundary = CreateBoundary(split.Bands[0].Outer, cuts);
        SplitLineBinding binding = boundary.Segments[0].Binding;

        SplitLineBinding moved = binding with { AY = binding.AY + 5 };
        Assert.False(boundary.EvaluateSplitLine(moved).Succeeded);

        SplitLineBinding stale = binding with { WorkingContextRevision = binding.WorkingContextRevision + 1 };
        Assert.False(boundary.EvaluateSplitLine(stale).Succeeded);
    }

    [Fact]
    public void ProjectSplitLine_RejectsInvalidInput()
    {
        AreaSplitResult split = Split(2);
        var cuts = new (Vec2, Vec2)[] { (new Vec2(-10, 50), new Vec2(210, 50)) };
        SplitRegionBoundary2D boundary = CreateBoundary(split.Bands[0].Outer, cuts);
        SplitLineBinding binding = boundary.Segments[0].Binding;

        Assert.False(boundary.ProjectSplitLine(binding, new Vec2(double.NaN, 0), 1.0).Succeeded);
        Assert.False(boundary.ProjectSplitLine(binding, new Vec2(0, 0), 0).Succeeded);
        Assert.False(boundary.ProjectSplitLine(binding, new Vec2(0, 0), double.NaN).Succeeded);
    }

    [Fact]
    public void TryCreate_RejectsDegenerateChildRing()
    {
        var cad = new CadExitBoundary2D(1, 1, Array.Empty<ExitEdgeReference>());
        var collapsed = new Vec2[] { new(0, 0), new(10, 0), new(20, 0) };

        Assert.False(SplitRegionBoundary2D.TryCreate(cad, Parent, collapsed, NoExclusions,
            new (Vec2, Vec2)[] { (new Vec2(-10, 50), new Vec2(210, 50)) },
            out _, out string failure));
        Assert.False(string.IsNullOrEmpty(failure));
    }

    [Fact]
    public void TryCreate_RequiresCadBoundary()
    {
        Assert.False(SplitRegionBoundary2D.TryCreate(null!, Parent, Parent, NoExclusions,
            new (Vec2, Vec2)[] { (new Vec2(-10, 50), new Vec2(210, 50)) },
            out _, out string failure));
        Assert.False(string.IsNullOrEmpty(failure));
    }

    [Fact]
    public void SplitCutLines_RoundTripThroughDistinctSegments()
    {
        AreaSplitResult split = Split(3);
        var cuts = split.Cuts
            .Select(cut => (new Vec2(-10, cut), new Vec2(210, cut)))
            .ToArray();
        SplitRegionBoundary2D middle = CreateBoundary(split.Bands[1].Outer, cuts);

        var cutLines = middle.Segments.Select(segment => (segment.A, segment.B)).Distinct().ToList();

        Assert.Equal(2, cutLines.Count);
        Assert.All(cutLines, line => Assert.NotEqual(line.Item1, line.Item2));
    }

    // 밴드 폴리곤은 0.01 mm 격자로 만들어져 절단선보다 반 격자(0.005 mm) 바깥에 놓일 수 있다.
    // 이 어긋남은 외곽을 가로지르는 것이 아니므로 유지 측 판정이 성공해야 한다.
    [Fact]
    public void RetainedHalfPlanes_AcceptsRingOffsetByHalfGrid()
    {
        var cuts = new (Vec2, Vec2)[] { (new Vec2(-10, 50), new Vec2(210, 50)) };
        var lower = new Vec2[]
        {
            new(0, 0), new(200, 0), new(200, 50.005), new(0, 50.005)
        };
        var upper = new Vec2[]
        {
            new(0, 49.995), new(200, 49.995), new(200, 100), new(0, 100)
        };

        SplitRegionBoundary2D lowerBoundary = CreateBoundary(lower, cuts);
        Assert.True(lowerBoundary.TryGetRetainedHalfPlanes(out List<SplitRegionBoundary2D.HalfPlane> lowerPlanes,
            out string lowerFailure), lowerFailure);
        SplitRegionBoundary2D.HalfPlane lowerPlane = Assert.Single(lowerPlanes);
        Assert.Equal(1, lowerPlane.Axis);
        Assert.Equal(50.0, lowerPlane.Limit, 6);
        Assert.True(lowerPlane.KeepLess);

        SplitRegionBoundary2D upperBoundary = CreateBoundary(upper, cuts);
        Assert.True(upperBoundary.TryGetRetainedHalfPlanes(out List<SplitRegionBoundary2D.HalfPlane> upperPlanes,
            out string upperFailure), upperFailure);
        SplitRegionBoundary2D.HalfPlane upperPlane = Assert.Single(upperPlanes);
        Assert.Equal(1, upperPlane.Axis);
        Assert.False(upperPlane.KeepLess);
    }

    // 실제로 외곽을 가로지르는 링은 절단선이 자식 외곽에 남지 않으므로, 경계 객체를
    // 만들 때부터 명시적으로 거부한다(조용히 통과시키지 않는다).
    [Fact]
    public void TryCreate_RejectsRingCrossedByCutLine()
    {
        var cuts = new (Vec2, Vec2)[] { (new Vec2(-10, 50), new Vec2(210, 50)) };
        var crossing = new Vec2[]
        {
            new(0, 40), new(200, 40), new(200, 60), new(0, 60)
        };

        var cad = new CadExitBoundary2D(1, 1, Array.Empty<ExitEdgeReference>());
        Assert.False(SplitRegionBoundary2D.TryCreate(cad, Parent, crossing, NoExclusions, cuts,
            out _, out string failure));
        Assert.Contains("절단선", failure);
    }
}
