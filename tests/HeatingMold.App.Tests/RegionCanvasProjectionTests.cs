using System;
using HeatingMold.App.Presentation;
using HeatingMold.Core.Geometry;

namespace HeatingMold.App.Tests;

/// <summary>발열 영역 미리보기의 작업 좌표 투영 규칙을 검사합니다.</summary>
public sealed class RegionCanvasProjectionTests
{
    /// <summary>작업 평면 경계를 여백 안에 맞추면서 가로·세로 비율을 보존합니다.</summary>
    [Fact]
    public void TryCreate_PreservesAspectRatioAndCentersBoundary()
    {
        Vec2[] boundary =
        [
            new(0, 0),
            new(100, 0),
            new(100, 50),
            new(0, 50)
        ];

        bool created = RegionCanvasProjection.TryCreate(
            boundary,
            canvasWidth: 216,
            canvasHeight: 116,
            padding: 8,
            out RegionCanvasProjection projection);

        Assert.True(created);
        Assert.Equal(new RegionCanvasPoint(8, 108), projection.Project(new Vec2(0, 0)));
        Assert.Equal(new RegionCanvasPoint(208, 8), projection.Project(new Vec2(100, 50)));
    }

    /// <summary>작업 평면의 위쪽이 화면 위쪽에 오도록 Y축 방향을 반전합니다.</summary>
    [Fact]
    public void Project_ReversesVerticalAxisAndUsesCanvasCentering()
    {
        Vec2[] boundary =
        [
            new(0, 0),
            new(100, 0),
            new(100, 100),
            new(0, 100)
        ];
        Assert.True(RegionCanvasProjection.TryCreate(
            boundary,
            canvasWidth: 300,
            canvasHeight: 200,
            padding: 10,
            out RegionCanvasProjection projection));

        Assert.Equal(new RegionCanvasPoint(60, 190), projection.Project(new Vec2(0, 0)));
        Assert.Equal(new RegionCanvasPoint(240, 10), projection.Project(new Vec2(100, 100)));
    }

    /// <summary>빈 경계와 여백을 수용하지 못하는 Canvas 크기를 거부합니다.</summary>
    [Fact]
    public void TryCreate_RejectsEmptyBoundaryAndInsufficientCanvasSize()
    {
        Assert.False(RegionCanvasProjection.TryCreate(
            Array.Empty<Vec2>(),
            canvasWidth: 100,
            canvasHeight: 100,
            padding: 8,
            out _));
        Assert.False(RegionCanvasProjection.TryCreate(
            [new Vec2(0, 0)],
            canvasWidth: 16,
            canvasHeight: 100,
            padding: 8,
            out _));
        Assert.False(RegionCanvasProjection.TryCreate(
            [new Vec2(0, 0)],
            canvasWidth: 100,
            canvasHeight: 100,
            padding: -1,
            out _));
    }
}
