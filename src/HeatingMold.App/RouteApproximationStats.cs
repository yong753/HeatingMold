using System.Collections.Generic;
using HeatingMold.Core.Geometry;

namespace HeatingMold.App;

/// <summary>조각 표본을 만든 경로.</summary>
internal enum RouteSampleSource
{
    /// <summary>strict 적응 매핑이 성공한 표본(정상 fast path).</summary>
    Strict = 0,
    /// <summary>평면 턴 외곽 돌출 예외로 승인한 2점 표본.</summary>
    PlanarTurn = 1,
    /// <summary>표면 미검출 구간을 제한 근사로 연결한 표본.</summary>
    Approximation = 2,
}

/// <summary>
/// strict 검증 표본 사전에 저장할 수 있는 출처. 근사·턴 예외 표본은 strict와 같은
/// 보증이 아니므로 재사용 캐시에 넣지 않는다(최소 설계).
/// </summary>
internal static class RouteSampleCachePolicy
{
    internal static bool CanCache(RouteSampleSource source) => source == RouteSampleSource.Strict;
}

/// <summary>
/// 이번 경로 준비(스테이징)에서 <b>실제로 실행한</b> 제한 근사 연결의 집계.
/// 확정 경로 전체의 근사 개수가 아니다:
/// <list type="bullet">
/// <item>이전 커밋에서 retain된 조각과 strict 표본 캐시에 적중한 조각은 다시 매핑하지 않으므로
/// 여기에 포함되지 않는다.</item>
/// <item>프로젝트 schema 7에는 근사 메타데이터를 저장하지 않으므로, 로드 직후의 값 0을
/// "이 영역에 근사 연결이 없다"로 단정하지 않는다.</item>
/// </list>
/// 제품 판정에는 쓰지 않고 완료 상태 문구에만 쓰인다.
/// </summary>
internal sealed class RouteApproximationStats
{
    internal static RouteApproximationStats Empty { get; } = new();

    private readonly List<SurfaceApproximationBridge> _bridges = new();

    internal int BridgeCount => _bridges.Count;
    internal double TotalAdvanceMm { get; private set; }
    internal double MaxAdvanceMm { get; private set; }
    internal double MaxGap3DMm { get; private set; }

    internal void Add(SurfaceApproximationReport report)
    {
        if (!report.Used) return;
        foreach (SurfaceApproximationBridge bridge in report.Bridges)
        {
            _bridges.Add(bridge);
            TotalAdvanceMm += bridge.AdvanceMm;
            MaxAdvanceMm = System.Math.Max(MaxAdvanceMm, bridge.AdvanceMm);
            MaxGap3DMm = System.Math.Max(MaxGap3DMm, bridge.Gap3DMm);
        }
    }

    /// <summary>
    /// 완료 상태용 문구. 근사가 없으면 빈 문자열이며 "0곳"이라고 단정하지 않는다.
    /// 값은 이번 준비에서 실행한 근사만 센 것이다.
    /// </summary>
    internal string Describe() => BridgeCount == 0
        ? string.Empty
        : string.Format(" · 이번 준비 근사 연결 {0}곳 · 총 {1:F1}mm · 최대 {2:F1}mm",
            BridgeCount, TotalAdvanceMm, MaxAdvanceMm);
}

/// <summary>
/// 근사 집계의 준비/게시 상태. 네이티브 커밋이 성공한 뒤에만 게시하며, 실패하면
/// 이전에 게시된 값을 덮어쓰지 않는다. retain/캐시 적중으로 이번 준비에서 매핑하지 않은
/// 조각은 집계에 포함되지 않는다.
/// </summary>
internal sealed class RouteApproximationPublication
{
    private RouteApproximationStats _committed = RouteApproximationStats.Empty;

    /// <summary>이번 준비에서 실행한 근사. 커밋 성공 전에는 게시되지 않는다.</summary>
    internal RouteApproximationStats Staging { get; private set; }

    /// <summary>마지막으로 성공한 커밋의 집계(또는 비어 있음).</summary>
    internal RouteApproximationStats Committed => _committed;

    /// <summary>새 트랜잭션 준비 시작. 이전 준비 값은 버린다.</summary>
    internal void Begin() => Staging = new RouteApproximationStats();

    /// <summary>준비 실패/취소. 이전에 게시된 값은 그대로 둔다.</summary>
    internal void AbandonOnFailure() => Staging = null;

    /// <summary>네이티브 커밋 성공 직후 한 번만 호출한다.</summary>
    internal void PublishOnCommit()
    {
        _committed = Staging ?? RouteApproximationStats.Empty;
        Staging = null;
    }

    internal void Reset()
    {
        Staging = null;
        _committed = RouteApproximationStats.Empty;
    }

    internal string Note() => _committed.Describe();
}
