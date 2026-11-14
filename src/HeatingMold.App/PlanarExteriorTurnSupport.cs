using System;
using System.Collections.Generic;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

/// <summary>
/// 평면 턴 외곽 돌출 예외(사용자 확정 2026-11-14)의 제품 판정.
///
/// <para>턴 연결선의 두 끝점이 <b>같은 원본 평면</b> 위의 검증된 실제 3D 점이고,
/// 확정 작업평면 2D 중심선에서 <b>원본 CAD 외곽</b>까지의 돌출이 전 구간
/// <see cref="PlanarExteriorTurnPolicy.MaxExteriorProtrusionMm"/> 이하임이 증명될 때만 승인한다.</para>
///
/// <para>거리는 제외영역·절단선이 아니라 <b>원본 CAD 경계 edge</b>로만 계산한다. 제외영역은
/// 연결선이 통과할 수 있는 제품 규칙이므로 표면 틈으로 취급하지 않는다. 외곽 내부에서
/// 표면이 없거나 다른 깊이의 면만 만나면 <see cref="PlanarExteriorProbeStatus.InteriorGap"/>으로
/// 거부한다.</para>
/// </summary>
internal static class PlanarExteriorTurnSupport
{
    private const double PlaneToleranceMm = GeometryTolerancePolicy.DistanceTolerance;
    private const double NormalDotTolerance = 1e-6;
    private const double AmbiguityEpsilon = ExitBoundaryConstraint.Tolerance;
    private const double NearestSearchTolerance = 1e9;

    internal sealed class Evidence
    {
        public Vec2 Start;
        public Vec2 End;
        public Vec3 Start3D;
        public Vec3 End3D;
        public int StartFaceId;
        public int EndFaceId;
        public double CertifiedUpperBoundMm;
        public double ObservedMaximumMm;
        public int ProbeCount;
        public int BoundaryEdgeCount;
    }

    /// <summary>예외가 성립하는지 판정한다. 성립하지 않으면 status/detail로 이유를 돌려준다.</summary>
    internal static bool TryBuild(
        WorkingArea area,
        WorkingSideDefinition side,
        CommittedRouteContext context,
        SerpentineRoutePiece piece,
        out Evidence evidence,
        out PlanarExteriorTurnStatus status,
        out string detail)
    {
        if (side == null)
        {
            evidence = null;
            status = PlanarExteriorTurnStatus.InvalidInput;
            detail = "판정 입력이 없습니다";
            return false;
        }

        return TryBuildCore(area, side.Id.Value, side.Revision.Value, context?.FaceIds, piece,
            out evidence, out status, out detail);
    }

    /// <summary>
    /// 작업 컨텍스트 id/revision만으로 판정한다. 프로젝트 복원처럼 WorkingSideDefinition이
    /// 없는 경로에서도 같은 규칙을 재적용하기 위한 진입점이다.
    /// </summary>
    internal static bool TryBuildCore(
        WorkingArea area,
        long id,
        long revision,
        int[] faceIds,
        SerpentineRoutePiece piece,
        out Evidence evidence,
        out PlanarExteriorTurnStatus status,
        out string detail)
    {
        evidence = null;
        detail = "";

        if (area == null || faceIds == null || faceIds.Length == 0 || piece == null)
        {
            status = PlanarExteriorTurnStatus.InvalidInput;
            detail = "판정 입력이 없습니다";
            return false;
        }
        if (!PlanarExteriorTurnPolicy.AppliesTo(piece.Kind))
        {
            status = PlanarExteriorTurnStatus.NotApplicable;
            detail = "턴 연결선이 아닙니다";
            return false;
        }
        if (piece.Points == null || piece.Points.Count != 2)
        {
            status = PlanarExteriorTurnStatus.NotApplicable;
            detail = "턴 연결선이 두 점 직선이 아닙니다";
            return false;
        }

        CadExitBoundary2D cad = area.RegionCadBoundary;
        if (cad == null || cad.Edges.Count == 0)
        {
            status = PlanarExteriorTurnStatus.NotApplicable;
            detail = "원본 CAD 인출 경계가 없습니다";
            return false;
        }
        List<Vec2> ring = area.Region != null && area.Region.Count == 1 ? area.Region[0] : null;
        if (ring == null || ring.Count < 3)
        {
            status = PlanarExteriorTurnStatus.NotApplicable;
            detail = "적용영역 외곽선이 없습니다";
            return false;
        }

        int[] faces = faceIds;

        Vec2 start = piece.Points[0];
        Vec2 end = piece.Points[piece.Points.Count - 1];

        WorkingHit startHit = QuerySurface(id, revision, faces, start, out _);
        if (startHit == null)
        {
            status = PlanarExteriorTurnStatus.NotApplicable;
            detail = "시작점의 실제 3D 표면 점을 찾지 못했습니다";
            return false;
        }
        WorkingHit endHit = QuerySurface(id, revision, faces, end, out _);
        if (endHit == null)
        {
            status = PlanarExteriorTurnStatus.NotApplicable;
            detail = "끝점의 실제 3D 표면 점을 찾지 못했습니다";
            return false;
        }

        var start3D = new Vec3(startHit.Point[0], startHit.Point[1], startHit.Point[2]);
        var end3D = new Vec3(endHit.Point[0], endHit.Point[1], endHit.Point[2]);
        var normal = new Vec3(startHit.Normal[0], startHit.Normal[1], startHit.Normal[2]);
        double normalLength = normal.Length;
        if (!double.IsFinite(normalLength) || normalLength <= 0)
        {
            status = PlanarExteriorTurnStatus.NotApplicable;
            detail = "시작점의 표면 법선을 읽지 못했습니다";
            return false;
        }
        normal = normal * (1.0 / normalLength);

        if (endHit.Normal == null || endHit.Normal.Length < 3)
        {
            status = PlanarExteriorTurnStatus.NotApplicable;
            detail = "끝점의 표면 법선을 읽지 못했습니다";
            return false;
        }
        var endNormal = new Vec3(endHit.Normal[0], endHit.Normal[1], endHit.Normal[2]);
        double endNormalLength = endNormal.Length;
        if (!double.IsFinite(endNormalLength) || endNormalLength <= 0)
        {
            status = PlanarExteriorTurnStatus.NotApplicable;
            detail = "끝점의 표면 법선을 읽지 못했습니다";
            return false;
        }
        endNormal = endNormal * (1.0 / endNormalLength);
        double normalDot = Math.Abs(normal.X * endNormal.X + normal.Y * endNormal.Y + normal.Z * endNormal.Z);
        if (!double.IsFinite(normalDot) || normalDot < 1.0 - NormalDotTolerance)
        {
            status = PlanarExteriorTurnStatus.NotApplicable;
            detail = "두 끝점의 원본 면이 같은 방향의 평면이 아닙니다";
            return false;
        }
        double planeOffset = Math.Abs(
            normal.X * (end3D.X - start3D.X) +
            normal.Y * (end3D.Y - start3D.Y) +
            normal.Z * (end3D.Z - start3D.Z));
        if (!double.IsFinite(planeOffset) || planeOffset > PlaneToleranceMm)
        {
            status = PlanarExteriorTurnStatus.NotApplicable;
            detail = "두 끝점이 같은 원본 평면 위에 있지 않습니다 (오차 " + planeOffset.ToString("0.###") + " mm)";
            return false;
        }

        var witnessCache = new Dictionary<long, WitnessSegment>();
        string queryFailure = string.Empty;
        PlanarExteriorProbeSample Probe(Vec2 point)
        {
            bool inside = PointInRing(ring, point);
            if (inside)
            {
                WorkingHit hit = QuerySurface(id, revision, faces, point, out WorkingQueryStatus status2);
                if (hit == null) return PlanarExteriorProbeSample.Failed(PlanarExteriorProbeStatus.InteriorGap);
                double depth = Math.Abs(
                    normal.X * (hit.Point[0] - start3D.X) +
                    normal.Y * (hit.Point[1] - start3D.Y) +
                    normal.Z * (hit.Point[2] - start3D.Z));
                if (!double.IsFinite(depth) || depth > PlaneToleranceMm)
                    return PlanarExteriorProbeSample.Failed(PlanarExteriorProbeStatus.InteriorGap);
                if (status2 == WorkingQueryStatus.StaleContext || status2 == WorkingQueryStatus.GeometryFailure ||
                    status2 == WorkingQueryStatus.InvalidInput)
                    return PlanarExteriorProbeSample.Failed(PlanarExteriorProbeStatus.QueryFailed);
                return PlanarExteriorProbeSample.Inside();
            }

            double bestDistance = double.PositiveInfinity;
            ExitEdgeReference bestEdge = null;
            Vec2 bestDatum = default;
            bool ambiguous = false;
            int degenerateEdges = 0;
            foreach (ExitEdgeReference edge in cad.Edges)
            {
                ExitEdgeHit hit;
                try
                {
                    hit = Bridge.ProjectExitEdge(id, revision, edge, point.X, point.Y, NearestSearchTolerance);
                }
                catch (Exception exception)
                {
                    if (queryFailure.Length == 0)
                        queryFailure = "edge " + edge.EdgeKey + " 예외: " + exception.Message;
                    return PlanarExteriorProbeSample.Failed(PlanarExteriorProbeStatus.QueryFailed);
                }
                if (hit == null)
                {
                    if (queryFailure.Length == 0) queryFailure = "edge " + edge.EdgeKey + " null 반환";
                    return PlanarExteriorProbeSample.Failed(PlanarExteriorProbeStatus.QueryFailed);
                }
                if (hit.Status == ExitEdgeQueryStatus.Miss) continue;
                // 작업평면에서 한 점으로 접히는 원본 경계는 2D 거리를 정의할 수 없다.
                // 건너뛰면 최근접 거리가 실제보다 커질 수만 있으므로(보수적) 승인 쪽으로 새지 않는다.
                if (hit.Status == ExitEdgeQueryStatus.DegenerateProjection)
                {
                    degenerateEdges++;
                    continue;
                }
                if (hit.Status != ExitEdgeQueryStatus.Hit || hit.DatumPoint == null)
                {
                    if (queryFailure.Length == 0)
                        queryFailure = "edge " + edge.EdgeKey + " status=" + hit.Status +
                                        " datum=" + (hit.DatumPoint == null ? "null" : "ok");
                    return PlanarExteriorProbeSample.Failed(PlanarExteriorProbeStatus.QueryFailed);
                }
                var datum = new Vec2(hit.DatumPoint[0], hit.DatumPoint[1]);
                double distance = datum.DistanceTo(point);
                if (!double.IsFinite(distance))
                {
                    if (queryFailure.Length == 0) queryFailure = "edge " + edge.EdgeKey + " datum 비유한";
                    return PlanarExteriorProbeSample.Failed(PlanarExteriorProbeStatus.QueryFailed);
                }
                if (distance < bestDistance - AmbiguityEpsilon)
                {
                    bestDistance = distance;
                    bestEdge = edge;
                    bestDatum = datum;
                    ambiguous = false;
                }
                else if (bestEdge != null && Math.Abs(distance - bestDistance) <= AmbiguityEpsilon &&
                         edge.EdgeKey != bestEdge.EdgeKey &&
                         datum.DistanceTo(bestDatum) > AmbiguityEpsilon)
                {
                    ambiguous = true;
                }
            }

            if (bestEdge == null)
            {
                if (queryFailure.Length == 0) queryFailure = "모든 원본 경계가 Miss (요청 좌표 " + point.X.ToString("0.###") + ", " + point.Y.ToString("0.###") + ") · 접힘 경계 " + degenerateEdges;
                return PlanarExteriorProbeSample.Failed(PlanarExteriorProbeStatus.QueryFailed);
            }
            if (ambiguous) return PlanarExteriorProbeSample.Failed(PlanarExteriorProbeStatus.Ambiguous);

            if (!witnessCache.TryGetValue(bestEdge.EdgeKey, out WitnessSegment witness))
                witness = ResolveWitness(id, revision, bestEdge);
            witnessCache[bestEdge.EdgeKey] = witness;
            if (!witness.Resolved)
            {
                if (queryFailure.Length == 0) queryFailure = "edge " + bestEdge.EdgeKey + " witness 평가 실패";
                return PlanarExteriorProbeSample.Failed(PlanarExteriorProbeStatus.QueryFailed);
            }

            return PlanarExteriorProbeSample.Outside(
                bestDistance, witness.Start, witness.End, witness.IsStraight, witness.IsOuterBoundary, bestDistance);
        }

        PlanarExteriorTurnValidation validation =
            PlanarExteriorTurnValidator.Validate(start, end, Probe, PlanarExteriorTurnPolicy.MaxExteriorProtrusionMm);

        if (!validation.IsValid)
        {
            status = validation.Status;
            detail = validation.Detail + (queryFailure.Length > 0 ? " [" + queryFailure + "]" : string.Empty);
            return false;
        }

        evidence = new Evidence
        {
            Start = start,
            End = end,
            Start3D = start3D,
            End3D = end3D,
            StartFaceId = startHit.FaceId,
            EndFaceId = endHit.FaceId,
            CertifiedUpperBoundMm = validation.CertifiedUpperBoundMm,
            ObservedMaximumMm = validation.ObservedMaximumMm,
            ProbeCount = validation.ProbeCount,
            BoundaryEdgeCount = cad.Edges.Count,
        };
        status = PlanarExteriorTurnStatus.Valid;
        detail = validation.Detail;
        return true;
    }

    /// <summary>선택 면 우선, 없으면 전역 표면에서 해당 2D 위치의 실제 3D 점을 얻는다.</summary>
    private static WorkingHit QuerySurface(long id, long revision, int[] faces, Vec2 point, out WorkingQueryStatus status)
    {
        WorkingHit direct = Bridge.MapWorkingPointOntoFaces(id, revision, faces, point.X, point.Y);
        if (direct != null && direct.Status == WorkingQueryStatus.Hit && direct.Point != null)
        {
            status = direct.Status;
            return direct;
        }

        WorkingHit global = Bridge.QueryWorkingSurface(id, revision, point.X, point.Y);
        if (global != null &&
            (global.Status == WorkingQueryStatus.Hit || global.Status == WorkingQueryStatus.Tangent) &&
            global.Point != null && global.Normal != null)
        {
            status = global.Status;
            return global;
        }

        status = direct?.Status ?? global?.Status ?? WorkingQueryStatus.Miss;
        if (status == WorkingQueryStatus.Hit) status = WorkingQueryStatus.Miss;
        return null;
    }

    private readonly struct WitnessSegment
    {
        public WitnessSegment(bool resolved, Vec2 start, Vec2 end, bool isStraight)
        {
            Resolved = resolved;
            Start = start;
            End = end;
            IsStraight = isStraight;
        }

        public bool Resolved { get; }
        public Vec2 Start { get; }
        public Vec2 End { get; }
        public bool IsStraight { get; }
        public bool IsOuterBoundary => Resolved;
    }

    private static WitnessSegment ResolveWitness(long id, long revision, ExitEdgeReference edge)
    {
        bool straight = IsStraightEdge(edge);
        if (!straight)
        {
            // 곡선 외곽은 구간 상계를 증명할 수 없다. 거리 자체는 유효하다.
            return new WitnessSegment(true, default, default, false);
        }

        ExitEdgeHit first = Bridge.EvaluateExitEdgeParameter(id, revision, edge, edge.FirstParameter);
        ExitEdgeHit last = Bridge.EvaluateExitEdgeParameter(id, revision, edge, edge.LastParameter);
        if (first?.Status != ExitEdgeQueryStatus.Hit || first.DatumPoint == null ||
            last?.Status != ExitEdgeQueryStatus.Hit || last.DatumPoint == null)
        {
            return new WitnessSegment(false, default, default, false);
        }

        return new WitnessSegment(true,
            new Vec2(first.DatumPoint[0], first.DatumPoint[1]),
            new Vec2(last.DatumPoint[0], last.DatumPoint[1]),
            true);
    }

    private static bool IsStraightEdge(ExitEdgeReference edge)
    {
        string signature = edge.EdgeSignature;
        if (string.IsNullOrEmpty(signature)) return false;
        const string prefix = "curve=";
        if (!signature.StartsWith(prefix, StringComparison.Ordinal)) return false;
        int end = signature.IndexOf(';', prefix.Length);
        if (end < 0) return false;
        return int.TryParse(signature.AsSpan(prefix.Length, end - prefix.Length), out int type) && type == 0;
    }

    /// <summary>
    /// 프로젝트 복원 시 저장된 2점 턴 연결선이 '평면 외곽 예외'로만 성립하는지 다시 검증한다.
    /// 표면 조회 실패·곡면·비공면·정상 직선 턴은 이 검사에서 통과시키고 기존 스테이징/매핑이
    /// 최종 판정을 내리게 둔다. 예외로만 성립하는 턴은 5mm 증명이 다시 통과해야 한다.
    /// </summary>
    internal static bool TryCheckRestoredTurn(
        WorkingArea area,
        long id,
        long revision,
        int[] faceIds,
        Vec2 start,
        Vec2 end,
        out string detail)
    {
        detail = "";
        if (area == null || faceIds == null || faceIds.Length == 0)
        {
            detail = "검증 입력이 없습니다";
            return false;
        }
        if (!double.IsFinite(start.X) || !double.IsFinite(start.Y) ||
            !double.IsFinite(end.X) || !double.IsFinite(end.Y))
        {
            detail = "턴 연결선 좌표가 유한하지 않습니다";
            return false;
        }
        if (start.DistanceTo(end) <= 0)
        {
            detail = "턴 연결선 길이가 0입니다";
            return false;
        }

        WorkingHit startHit = QuerySurface(id, revision, faceIds, start, out _);
        WorkingHit endHit = QuerySurface(id, revision, faceIds, end, out _);
        if (startHit == null || endHit == null)
        {
            // 표면 조회 실패는 이 검사의 판단 대상이 아니다. 기존 스테이징이 명시적으로 거부한다.
            return true;
        }

        if (!TryNormalize(startHit.Normal, out Vec3 normal) ||
            !TryNormalize(endHit.Normal, out Vec3 endNormal))
        {
            return true;
        }

        Vec3 start3D = new(startHit.Point[0], startHit.Point[1], startHit.Point[2]);
        Vec3 end3D = new(endHit.Point[0], endHit.Point[1], endHit.Point[2]);

        double normalDot = Math.Abs(normal.X * endNormal.X + normal.Y * endNormal.Y + normal.Z * endNormal.Z);
        if (!double.IsFinite(normalDot) || normalDot < 1.0 - NormalDotTolerance)
        {
            // 곡면 또는 다른 방향의 면 — 이 정책의 대상이 아니다.
            return true;
        }

        double planeOffset = Math.Abs(
            normal.X * (end3D.X - start3D.X) +
            normal.Y * (end3D.Y - start3D.Y) +
            normal.Z * (end3D.Z - start3D.Z));
        if (!double.IsFinite(planeOffset) || planeOffset > PlaneToleranceMm)
        {
            return true;
        }

        Vec2 middle = new((start.X + end.X) * 0.5, (start.Y + end.Y) * 0.5);
        WorkingHit middleHit = QuerySurface(id, revision, faceIds, middle, out _);
        if (middleHit != null && middleHit.Point != null)
        {
            double depth = Math.Abs(
                normal.X * (middleHit.Point[0] - start3D.X) +
                normal.Y * (middleHit.Point[1] - start3D.Y) +
                normal.Z * (middleHit.Point[2] - start3D.Z));
            if (double.IsFinite(depth) && depth <= PlaneToleranceMm)
            {
                // 연결선 중간에 같은 평면의 실제 표면이 있으면 정상 직선 턴이다.
                return true;
            }
        }

        var piece = new SerpentineRoutePiece(
            SerpentineRoutePieceKind.TurnConnector, 0, new[] { start, end });

        if (TryBuildCore(area, id, revision, faceIds, piece, out _, out PlanarExteriorTurnStatus status, out string coreDetail))
        {
            return true;
        }

        detail = status + " — " + coreDetail;
        return false;
    }

    private static bool TryNormalize(double[] values, out Vec3 normal)
    {
        normal = default;
        if (values == null || values.Length < 3)
        {
            return false;
        }

        var vector = new Vec3(values[0], values[1], values[2]);
        if (!double.IsFinite(vector.X) || !double.IsFinite(vector.Y) || !double.IsFinite(vector.Z))
        {
            return false;
        }

        double length = vector.Length;
        if (!double.IsFinite(length) || length <= 0)
        {
            return false;
        }

        normal = vector * (1.0 / length);
        return true;
    }

    /// <summary>적용영역 외곽선(제외 차감 전) 기준 even-odd 내부 판정.</summary>
    internal static bool PointInRing(IReadOnlyList<Vec2> ring, Vec2 point)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            Vec2 a = ring[j];
            Vec2 b = ring[i];
            if ((b.Y > point.Y) != (a.Y > point.Y))
            {
                double x = b.X + (point.Y - b.Y) * (a.X - b.X) / (a.Y - b.Y);
                if (point.X < x) inside = !inside;
            }
        }
        return inside;
    }
}
