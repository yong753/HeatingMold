using HeatingMold.Application.Ports.Export;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;

namespace HeatingMold.Infrastructure.Export;

/// <summary>확정된 3차원 경로와 영역 경계를 작업 좌표계 평면의 DXF 도형으로 만듭니다.</summary>
public sealed class DxfProjectionBuilder
{
    private const double ConnectionPointMarkerHalfSizeMm = 2;
    private const double ConnectionPointLabelOffsetMm = 3;
    private const double ConnectionPointLabelHeightMm = 2;

    /// <summary>경계와 측정 완료 경로를 작업 좌표계로 투영합니다.</summary>
    public DxfProjectedDesignData Build(ExportSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var polylines = new List<DxfPolylineData>();
        var labels = new List<DxfTextData>();

        foreach (HeatingRegion region in snapshot.Project.HeatingRegions)
        {
            foreach (BoundaryLoop loop in region.EffectiveBoundaryLoops)
            {
                Vec2[] points = loop.Segments.Select(segment => segment.Start).ToArray();
                if (points.Length >= 2)
                    polylines.Add(new DxfPolylineData(DxfLayerCatalog.MoldOutline, points, true));
            }
        }

        foreach (CurveGeometrySnapshot curve in snapshot.Geometry.Curves)
        {
            string? layer = curve.HeatingPathSegmentId.HasValue
                ? DxfLayerCatalog.HeatingWire
                : curve.ConnectionPathSegmentId.HasValue
                    ? DxfLayerCatalog.ConnectionWire
                    : null;
            if (layer is null || curve.Samples.Count < 2)
                continue;

            var points = new List<Vec2>(curve.Samples.Count);
            foreach (Vec3 sample in curve.Samples)
            {
                Vec2 projected = ProjectToWorkingPlane(sample);
                if (points.Count == 0 || points[^1] != projected)
                    points.Add(projected);
            }

            if (points.Count >= 2)
                polylines.Add(new DxfPolylineData(layer, points.ToArray(), false));
        }

        Dictionary<CircuitId, Circuit> circuits = snapshot.Project.Circuits
            .ToDictionary(circuit => circuit.Id);
        foreach (ConnectionPoint connectionPoint in snapshot.Project.ConnectionPoints)
        {
            if (!circuits.TryGetValue(connectionPoint.CircuitId, out Circuit? circuit))
                throw new InvalidOperationException("DXF 연결점이 현재 프로젝트에 없는 회로를 참조합니다.");

            Vec2 location = ProjectToWorkingPlane(connectionPoint.ExternalConnectionPoint);
            AddConnectionPointMarker(location);

            string role = connectionPoint.Role == ConnectionPointRole.Start ? "START" : "END";
            labels.Add(new DxfTextData(
                DxfLayerCatalog.ConnectionPoint,
                $"C{circuit.Sequence + 1}-{role}",
                new Vec2(
                    location.X + ConnectionPointLabelOffsetMm,
                    location.Y + ConnectionPointLabelOffsetMm),
                ConnectionPointLabelHeightMm));
        }

        return new DxfProjectedDesignData(polylines, labels);

        void AddConnectionPointMarker(Vec2 location)
        {
            double halfSize = ConnectionPointMarkerHalfSizeMm;
            polylines.Add(new DxfPolylineData(
                DxfLayerCatalog.ConnectionPoint,
                [new Vec2(location.X - halfSize, location.Y), new Vec2(location.X + halfSize, location.Y)],
                false));
            polylines.Add(new DxfPolylineData(
                DxfLayerCatalog.ConnectionPoint,
                [new Vec2(location.X, location.Y - halfSize), new Vec2(location.X, location.Y + halfSize)],
                false));
        }

        Vec2 ProjectToWorkingPlane(Vec3 point)
        {
            WorkingCoordinateSystemDefinition frame = snapshot.WorkingCoordinateSystem;
            Vec3 delta = point - frame.Origin;
            return new Vec2(Vec3.Dot(delta, frame.AxisX), Vec3.Dot(delta, frame.AxisY));
        }
    }
}

/// <summary>한 레이어에서 출력할 작업 평면 다중선의 점과 닫힘 여부입니다.</summary>
public sealed record DxfPolylineData(string LayerName, IReadOnlyList<Vec2> Points, bool IsClosed);

/// <summary>도면에 출력할 작업 평면 텍스트와 글자 높이입니다.</summary>
public sealed record DxfTextData(string LayerName, string Value, Vec2 Position, double HeightMm);

/// <summary>DXF writer가 소비할 투영 다중선과 설명 텍스트입니다.</summary>
public sealed record DxfProjectedDesignData(
    IReadOnlyList<DxfPolylineData> Polylines,
    IReadOnlyList<DxfTextData> Labels);
