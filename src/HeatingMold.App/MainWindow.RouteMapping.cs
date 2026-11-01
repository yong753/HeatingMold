using System;
using System.Collections.Generic;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

public partial class MainWindow
{
    /// <summary>
    /// 경로·핸들·미리보기·확정이 모두 사용하는 단일 표면 매핑 규칙.
    /// 1) 인출 앵커 데이텀은 실측 공유 모서리 3D 점을 그대로 쓴다.
    /// 2) 그 외 점은 선택 면 직접 히트를 우선한다(영역 안은 기존과 동일).
    /// 3) 선택 면에 없으면 전역 모델 표면 레이캐스트 히트를 선택 면 여부와 무관하게 쓴다.
    /// 4) 모델 표면에도 없으면 null — 확정 단계가 fail-closed로 거부한다.
    /// 0.5mm 선택면 스냅과 프레임 평면 대체 표시는 쓰지 않는다.
    /// </summary>
    private Func<Vec2, SurfaceMappedSample?> BuildSurfaceMapDelegate(
        WorkingArea area, WorkingSideDefinition side, Vec2? movedAnchorDatum = null)
    {
        int[] faces = SortedFaceIds(area);
        var anchors = new List<(Vec2 Datum, Vec3 Point, int FaceId)>();

        void AddAnchor(Vec2 datum)
        {
            if (!area.HasExitChain3D || area.ExitChain.Count < 2) return;
            if (Serpentine.DistanceToChain(area.ExitChain, datum) > 0.05) return;
            Vec3 point = PointOnExitChain3D(area, datum, out int segment);
            int faceId = segment >= 1 && segment - 1 < area.ExitChainFaceIds.Count
                ? area.ExitChainFaceIds[segment - 1]
                : area.ExitFaceId;
            anchors.Add((datum, point, faceId));
        }

        if (movedAnchorDatum.HasValue) AddAnchor(movedAnchorDatum.Value);
        if (area.Pattern != null && area.Pattern.RoutePieces.Count > 0)
        {
            var pieces = area.Pattern.RoutePieces;
            if (pieces[0].Points.Count > 0) AddAnchor(pieces[0].Points[0]);
            if (pieces[^1].Points.Count > 0) AddAnchor(pieces[^1].Points[^1]);
        }

        long id = side.Id.Value, revision = side.Revision.Value;
        return uv =>
        {
            // 인출 리드의 끝점은 실측 CAD 공유 모서리 위 점을 그대로 쓴다(실루엣 붕괴 예외).
            foreach ((Vec2 datum, Vec3 point, int faceId) in anchors)
                if (uv == datum) return new SurfaceMappedSample(uv, point, faceId);

            var direct = Bridge.MapWorkingPointOntoFaces(id, revision, faces, uv.X, uv.Y);
            if (direct != null && direct.Status == WorkingQueryStatus.Hit && direct.Point != null)
                return new SurfaceMappedSample(uv,
                    new Vec3(direct.Point[0], direct.Point[1], direct.Point[2]), direct.FaceId);

            var global = Bridge.QueryWorkingSurface(id, revision, uv.X, uv.Y);
            if (global != null &&
                (global.Status == WorkingQueryStatus.Hit || global.Status == WorkingQueryStatus.Tangent) &&
                global.Point != null)
                return new SurfaceMappedSample(uv,
                    new Vec3(global.Point[0], global.Point[1], global.Point[2]), global.FaceId);
            return null;
        };
    }
}
