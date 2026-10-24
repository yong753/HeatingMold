using System;
using HeatingMold.App.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;

namespace HeatingMold.App.Tests;

/// <summary>제어점 편집 미리보기 표본이 원래 곡선 형태와 방향을 따르는지 확인합니다.</summary>
public sealed class ControlPointPreviewCurveSamplerTests
{
    /// <summary>원호 표본은 양 끝점을 포함하고 역방향 선분은 좌표 순서를 뒤집습니다.</summary>
    [Fact]
    public void Sample_PreservesArcEndpointsAndReversedLineDirection()
    {
        var plane = new ProjectionFrame(
            new Vec3(0, 0, 0),
            new Vec3(0, 0, 1),
            new Vec3(1, 0, 0));
        var arc = new ArcCurveDefinition(
            plane,
            new Vec3(0, 0, 0),
            new LengthMm(10),
            0,
            Math.PI / 2);
        var line = new LineCurveDefinition(
            new Vec3(1, 2, 3),
            new Vec3(4, 5, 6),
            CurveDirection.Reversed);

        double[] arcPoints = ControlPointPreviewCurveSampler.Sample(arc);
        double[] linePoints = ControlPointPreviewCurveSampler.Sample(line);

        Assert.True(arcPoints.Length > 6);
        Assert.Equal(10, arcPoints[0], precision: 8);
        Assert.Equal(0, arcPoints[1], precision: 8);
        Assert.Equal(0, arcPoints[^3], precision: 8);
        Assert.Equal(10, arcPoints[^2], precision: 8);
        Assert.Equal(new[] { 4d, 5d, 6d, 1d, 2d, 3d }, linePoints);
    }

    /// <summary>정의하지 않은 곡선 형식을 안전하게 표시 후보에서 거부합니다.</summary>
    [Fact]
    public void Sample_RejectsUnsupportedCurveKind()
    {
        var spline = new BSplineCurveDefinition(
            degree: 1,
            poles: [new Vec3(0, 0, 0), new Vec3(1, 0, 0)],
            weights: [1, 1],
            knots: [0, 1],
            multiplicities: [2, 2],
            isPeriodic: false,
            trimStart: 0,
            trimEnd: 1);

        Assert.Throws<NotSupportedException>(() => ControlPointPreviewCurveSampler.Sample(spline));
    }
}
