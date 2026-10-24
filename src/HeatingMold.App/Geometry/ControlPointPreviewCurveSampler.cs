using System;
using System.Collections.Generic;
using System.Linq;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Routing;

namespace HeatingMold.App.Geometry;

/// <summary>지원 곡선의 편집 후보를 뷰어용 다중선으로 표본화합니다.</summary>
internal static class ControlPointPreviewCurveSampler
{
    private const double MaximumArcChordErrorMm = 0.05;
    private const int MaximumArcSegments = 2048;

    /// <summary>영향받은 경로 조각만 서로 연결하지 않은 표시용 좌표 배열로 변환합니다.</summary>
    internal static double[][] SampleAffectedSegments(
        HeatingPath path,
        IEnumerable<HeatingPathSegmentId> affectedSegmentIds)
    {
        ArgumentNullException.ThrowIfNull(path);
        HeatingPathSegmentId[] ids = affectedSegmentIds?.Distinct().ToArray()
            ?? throw new ArgumentNullException(nameof(affectedSegmentIds));
        HashSet<HeatingPathSegmentId> affectedIds = ids.ToHashSet();
        HeatingPathSegment[] segments = path.Segments
            .Where(segment => affectedIds.Contains(segment.Id))
            .OrderBy(segment => segment.Sequence)
            .ToArray();

        if (segments.Length != affectedIds.Count)
            throw new InvalidOperationException("미리보기 대상 경로 조각을 후보에서 찾을 수 없습니다.");

        return segments
            .Select(segment => Sample(segment.Curve))
            .ToArray();
    }

    /// <summary>직선·다중선·원호를 표시용 좌표로 바꾸고 미지원 곡선은 거부합니다.</summary>
    internal static double[] Sample(CurveDefinition curve)
    {
        ArgumentNullException.ThrowIfNull(curve);
        Vec3[] points = curve switch
        {
            LineCurveDefinition line => [line.Start, line.End],
            PolylineCurveDefinition polyline => polyline.Points.ToArray(),
            ArcCurveDefinition arc => SampleArc(arc),
            _ => throw new NotSupportedException("이 곡선 형식의 편집 미리보기 표본화는 지원하지 않습니다.")
        };

        if (curve.Direction == CurveDirection.Reversed)
            Array.Reverse(points);

        return points.SelectMany(point => new[] { point.X, point.Y, point.Z }).ToArray();
    }

    /// <summary>원호의 현오차를 제한하며 프레임 축 기준의 점을 생성합니다.</summary>
    private static Vec3[] SampleArc(ArcCurveDefinition arc)
    {
        double startAngle = arc.StartAngleRad;
        double endAngle = arc.EndAngleRad;
        double sweep = endAngle - startAngle;
        double radius = arc.RadiusMm.Value;
        double maximumStep = 2 * Math.Acos(Math.Clamp(
            1 - MaximumArcChordErrorMm / radius,
            -1,
            1));
        if (!double.IsFinite(maximumStep) || maximumStep <= 0)
            maximumStep = Math.PI / 180;

        maximumStep = Math.Min(maximumStep, Math.PI / 12);
        int segmentCount = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / maximumStep));
        if (segmentCount > MaximumArcSegments)
            segmentCount = MaximumArcSegments;

        var points = new Vec3[segmentCount + 1];
        for (int index = 0; index <= segmentCount; index++)
        {
            double amount = (double)index / segmentCount;
            double angle = startAngle + sweep * amount;
            points[index] = arc.Center +
                arc.Plane.AxisX * (radius * Math.Cos(angle)) +
                arc.Plane.AxisY * (radius * Math.Sin(angle));
        }

        return points;
    }
}
