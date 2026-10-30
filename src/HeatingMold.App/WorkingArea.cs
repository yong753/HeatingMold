using System;
using System.Collections.Generic;
using System.Windows.Media;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

/// <summary>
/// 확정된 작업영역 하나. 확정 시점의 Face 집합/투영 프레임과 해당 영역에서
/// 생성한 발열 경로 상태를 함께 보관해 다른 영역과 독립적으로 유지한다.
/// </summary>
internal sealed class WorkingArea
{
    public string Name { get; init; } = "";
    public Color Color { get; init; }
    public HashSet<int> FaceIdSet { get; } = new();
    public List<List<Vec2>> Region { get; } = new();
    public ProjectionFrame Frame { get; init; }
    public List<(double MinX, double MinY, double MaxX, double MaxY)> Exclusions { get; } = new();

    /// <summary>사행 배치 방향(자동/프레임 X/프레임 Y).</summary>
    public PatternDirection Direction { get; set; } = PatternDirection.Auto;

    /// <summary>인출면 CAD Face ID. -1이면 미지정.</summary>
    public int ExitFaceId { get; set; } = -1;
    public long ExitFaceRevision { get; set; }

    /// <summary>인출면과 가열면의 공유 모서리를 작업영역 2D로 투영한 폴리라인.</summary>
    public List<Vec2> ExitChain { get; set; } = new();

    /// <summary>ExitChain과 1:1로 대응하는 실제 CAD 모서리 3D 폴리라인.</summary>
    public List<Vec3> ExitChain3D { get; set; } = new();

    /// <summary>ExitChain의 점 사이 구간별 실제 인출 Face ID (개수 = ExitChain.Count - 1).</summary>
    public List<int> ExitChainFaceIds { get; set; } = new();

    /// <summary>3D 체인이 2D 체인과 점 개수까지 일치할 때만 곡면 인출 리드 보정에 사용한다.</summary>
    public bool HasExitChain3D => ExitChain.Count >= 2 && ExitChain3D.Count == ExitChain.Count;

    /// <summary>인출면 공유 모서리 위의 시작점(=전선 입구)/종료점(=출구).</summary>
    public Vec2? StartAnchor { get; set; }
    public Vec2? EndAnchor { get; set; }

    /// <summary>현재 방향에서 같은 인출면으로 양 끝을 맞출 수 있는 후보 쌍.</summary>
    public List<AnchoredCandidate> AnchorCandidates { get; set; } = new();

    /// <summary>인출면 고정 생성 실패 사유(사용자 표시용).</summary>
    public string LastExitFailure { get; set; }

    public SerpentineResult Pattern { get; set; }
    public List<bool> TurnEnabled { get; set; } = new();
    public List<RoutePieceResult> ProjectedPieces { get; set; } = new();
    public int[] NativePieceIds { get; set; } = Array.Empty<int>();
    public double[] RoutePieceLengths { get; set; } = Array.Empty<double>();
    public double[] LeadLengths { get; set; } = Array.Empty<double>();
    public List<List<Vec2>> Paths { get; set; } = new();
    public bool PatternDisplayed { get; set; }

    /// <summary>생성 후 꼭지점 편집으로 경로가 수정되었는지. 재생성 시 삭제 경고에 사용한다.</summary>
    public bool RouteEdited { get; set; }

    public bool HasExitAnchors => ExitFaceId >= 0 && StartAnchor.HasValue && EndAnchor.HasValue;
    public bool HasExitFace => ExitFaceId >= 0 && ExitChain.Count >= 2;

    public string Label => string.Format("{0} · Face {1}", Name, FaceIdSet.Count);
    public Brush Swatch => new SolidColorBrush(Color);

    public override string ToString() => Label;
}
