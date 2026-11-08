using System;
using System.Collections.Generic;
using System.Linq;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

/// <summary>
/// 확정된 3D 경로를 다시 스테이징할 때 사용하는 불변 매핑 조건.
/// 다른 영역을 재생성하더라도 이 영역의 현재 초안 값이 아니라 이 스냅샷을 써야
/// 기존 경로가 조용히 다른 조건으로 다시 매핑되지 않는다.
/// </summary>
internal sealed class CommittedRouteContext
{
    public int[] FaceIds { get; init; } = Array.Empty<int>();
    public int ExitFaceId { get; init; } = -1;
    public List<Vec2> ExitChain { get; init; } = new();
    public List<Vec3> ExitChain3D { get; init; } = new();
    public List<int> ExitChainFaceIds { get; init; } = new();
    public Vec2? StartAnchor { get; init; }
    public Vec2? EndAnchor { get; init; }
    public List<ExitEdgeReference> ExitEdgeRefs { get; init; } = new();
    public IExitBoundary2D Boundary { get; init; }
    public IExitBoundary2D RegionBoundary { get; init; }
    public ExitBoundaryPoint? SelectedStartExit { get; init; }
    public ExitBoundaryPoint? SelectedEndExit { get; init; }
    public AnchoredCandidate SelectedAnchorCandidate { get; init; }
    public List<CircuitExitPoint> StartExits { get; init; } = new();
    public List<CircuitExitPoint> EndExits { get; init; } = new();
    /// <summary>분할된 회로별 인출점. 매핑 시 역할 기반 꼭지점 override에 쓴다.</summary>
    public List<Vec2> CircuitStarts { get; init; } = new();
    public List<Vec2> CircuitEnds { get; init; } = new();

    public static CommittedRouteContext Capture(WorkingArea area) =>
        Capture(area, area.HasSelectedExits ? area.RegionExitBoundary : area.ExitBoundary);

    /// <summary>적용영역 전체 외곽(선택 인출점 방식)을 기준으로 캡처한다.</summary>
    public static CommittedRouteContext CaptureRegion(WorkingArea area) => Capture(area, area.RegionExitBoundary);

    private static CommittedRouteContext Capture(WorkingArea area, IExitBoundary2D boundary) => new()
    {
        FaceIds = area.FaceIdSet.OrderBy(id => id).ToArray(),
        ExitFaceId = area.ExitFaceId,
        ExitChain = area.ExitChain.ToList(),
        ExitChain3D = area.ExitChain3D.ToList(),
        ExitChainFaceIds = area.ExitChainFaceIds.ToList(),
        StartAnchor = area.StartAnchor,
        EndAnchor = area.EndAnchor,
        ExitEdgeRefs = area.ExitEdgeRefs.Select(CadExitBoundary2D.CopyReference).ToList(),
        Boundary = boundary,
        RegionBoundary = area.RegionExitBoundary,
        SelectedStartExit = area.SelectedStartExit,
        SelectedEndExit = area.SelectedEndExit,
        SelectedAnchorCandidate = area.SelectedAnchorCandidate,
        StartExits = area.Circuits.Select(c => c.StartExit).ToList(),
        EndExits = area.Circuits.Select(c => c.EndExit).ToList(),
        CircuitStarts = area.Circuits.Select(c => c.StartExit.Point).ToList(),
        CircuitEnds = area.Circuits.Select(c => c.EndExit.Point).ToList()
    };
}
