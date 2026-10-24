using System;
using System.Collections.Generic;
using HeatingMold.Core.Geometry;

namespace HeatingMold.App.Presentation;

/// <summary>작업 평면 좌표를 종횡비를 유지한 Canvas 좌표로 바꾸는 투영입니다.</summary>
internal sealed class RegionCanvasProjection
{
    private const double MinimumWorldSpan = 1e-9;

    private readonly double _minimumX;
    private readonly double _maximumY;
    private readonly double _scale;
    private readonly double _originX;
    private readonly double _originY;

    private RegionCanvasProjection(
        double minimumX,
        double maximumY,
        double scale,
        double originX,
        double originY)
    {
        _minimumX = minimumX;
        _maximumY = maximumY;
        _scale = scale;
        _originX = originX;
        _originY = originY;
    }

    /// <summary>경계 점과 Canvas 크기로 유효한 투영을 만듭니다.</summary>
    public static bool TryCreate(
        IEnumerable<Vec2> boundaryPoints,
        double canvasWidth,
        double canvasHeight,
        double padding,
        out RegionCanvasProjection projection)
    {
        ArgumentNullException.ThrowIfNull(boundaryPoints);
        projection = null;

        if (!double.IsFinite(canvasWidth) ||
            !double.IsFinite(canvasHeight) ||
            !double.IsFinite(padding) ||
            padding < 0 ||
            canvasWidth <= padding * 2 ||
            canvasHeight <= padding * 2)
        {
            return false;
        }

        using IEnumerator<Vec2> enumerator = boundaryPoints.GetEnumerator();
        if (!enumerator.MoveNext())
            return false;

        Vec2 firstPoint = enumerator.Current;
        double minimumX = firstPoint.X;
        double maximumX = firstPoint.X;
        double minimumY = firstPoint.Y;
        double maximumY = firstPoint.Y;

        while (enumerator.MoveNext())
        {
            Vec2 point = enumerator.Current;
            minimumX = Math.Min(minimumX, point.X);
            maximumX = Math.Max(maximumX, point.X);
            minimumY = Math.Min(minimumY, point.Y);
            maximumY = Math.Max(maximumY, point.Y);
        }

        double worldWidth = Math.Max(MinimumWorldSpan, maximumX - minimumX);
        double worldHeight = Math.Max(MinimumWorldSpan, maximumY - minimumY);
        double availableWidth = canvasWidth - padding * 2;
        double availableHeight = canvasHeight - padding * 2;
        double scale = Math.Min(availableWidth / worldWidth, availableHeight / worldHeight);
        if (!double.IsFinite(scale) || scale <= 0)
            return false;

        double originX = (canvasWidth - worldWidth * scale) / 2;
        double originY = (canvasHeight - worldHeight * scale) / 2;
        if (!double.IsFinite(originX) || !double.IsFinite(originY))
            return false;

        projection = new RegionCanvasProjection(minimumX, maximumY, scale, originX, originY);
        return true;
    }

    /// <summary>작업 평면 점을 좌우 중심·위쪽 양의 Y 화면 좌표로 투영합니다.</summary>
    public RegionCanvasPoint Project(Vec2 point) => new(
        _originX + (point.X - _minimumX) * _scale,
        _originY + (_maximumY - point.Y) * _scale);
}

/// <summary>Canvas 투영 결과를 담는 화면 픽셀 좌표입니다.</summary>
internal readonly record struct RegionCanvasPoint(double X, double Y);
