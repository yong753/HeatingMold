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
/// 오래된/근거 미확인 영역은 참고 표시용으로만 남기고 합계와 내보내기에서는 제외한다.
/// </summary>
internal sealed class ReviewSnapshot
{
    public IReadOnlyList<WorkingArea> RequestedAreas { get; init; } = new List<WorkingArea>();
    public IReadOnlyList<ReviewAreaEntry> Entries { get; init; } = new List<ReviewAreaEntry>();
    public IReadOnlyList<WorkingArea> ReadyAreas { get; init; } = new List<WorkingArea>();
    public IReadOnlyList<CircuitLengthInfo> Circuits { get; init; } = new List<CircuitLengthInfo>();
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
