using System.Collections.Generic;
using System.Linq;
using HeatingMold.Core.Electrical;

namespace HeatingMold.App;

/// <summary>검토 범위 안의 영역 하나가 현재 결과·합계에 사용될 수 있는 상태.</summary>
internal enum ReviewAreaState
{
    Ready,
    Missing,
    Stale,
    Unverified,
    InvalidLengths
}

internal sealed record ReviewAreaEntry(WorkingArea Area, ReviewAreaState State, string Reason);

internal sealed record CircuitLengthInfo(string CircuitId, double HeatingLengthMm, double? TargetPowerW = null);

/// <summary>
/// 회로 표·전기 사양표·내보내기 안내가 같은 판단을 쓰도록 만든 한 번의 검토 결과.
/// 계산·합계·내보내기는 Ready 영역만 쓰고, 표시용 회로 목록(DisplayCircuits)은
/// 재생성 필요 등으로 계산에서 빠진 확정 회로도 행을 유지하기 위해 따로 담는다.
/// </summary>
internal sealed class ReviewSnapshot
{
    public IReadOnlyList<WorkingArea> RequestedAreas { get; init; } = new List<WorkingArea>();
    public IReadOnlyList<ReviewAreaEntry> Entries { get; init; } = new List<ReviewAreaEntry>();
    public IReadOnlyList<WorkingArea> ReadyAreas { get; init; } = new List<WorkingArea>();
    public IReadOnlyList<CircuitLengthInfo> Circuits { get; init; } = new List<CircuitLengthInfo>();
    /// <summary>
    /// 표시 전용 확정 회로 목록. 검토 범위의 모든 영역을 계산 자격과 무관하게 담는다.
    /// 행이 남는다는 이유로 분할·편집·내보내기 자격을 통과시키지 않는다.
    /// </summary>
    public IReadOnlyList<ReviewCircuitDisplayEntry> DisplayCircuits { get; init; } =
        new List<ReviewCircuitDisplayEntry>();
    /// <summary>표시용 회로 목록을 만들지 못한 이유. 비어 있으면 정상.</summary>
    public string DisplayReason { get; init; } = "";
    public ElectricalSpecTable Table { get; init; }
    /// <summary>회로별 판정에 쓴 허용치. 전기 입력 오류면 null.</summary>
    public ElectricalLimits Limits { get; init; }
    public string ElectricalError { get; init; }
    public bool CanExport { get; init; }
    public string BlockReason { get; init; }

    public int RequestedCount => RequestedAreas.Count;
    public int ReadyCount => ReadyAreas.Count;
    /// <summary>검토 표에 표시되지 않아 합계에서 빠진 영역 수.</summary>
    public int ExcludedCount => Entries.Count(e => e.State != ReviewAreaState.Ready);
}
