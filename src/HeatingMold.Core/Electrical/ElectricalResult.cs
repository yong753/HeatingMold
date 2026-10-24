using System.Collections.ObjectModel;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Primitives;

namespace HeatingMold.Core.Electrical;

/// <summary>한 회로의 계산 수치와 각 한도 판정을 보관합니다.</summary>
public sealed class ElectricalResult
{
    /// <summary>계산 대상 회로의 안정 ID입니다.</summary>
    public CircuitId CircuitId { get; }

    /// <summary>계산에 사용한 설계·모델·입력·규칙 지문입니다.</summary>
    public DependencyStamp DependencyStamp { get; }

    /// <summary>저항 길이의 출처와 재료 역할별 길이를 보존한 원장입니다.</summary>
    public LengthBreakdown LengthBreakdown { get; }

    /// <summary>결과 계산에 적용한 전기 규칙의 버전입니다.</summary>
    public string RuleVersion { get; }

    /// <summary>저항 계산에 포함한 발열사 길이(mm)입니다.</summary>
    public LengthMm ResistanceLengthMm { get; }

    /// <summary>저항 계산에 사용한 발열사 길이(m)입니다.</summary>
    public double? ResistanceLengthMeters { get; }

    /// <summary>입력된 실제 전압(V RMS)입니다.</summary>
    public VoltageVrms? ActualVoltageVrms { get; }

    /// <summary>도체의 입력 단위 저항(Ω/m)입니다.</summary>
    public ResistanceOhmsPerMeter? ResistanceOhmsPerMeter { get; }

    /// <summary>계산한 전체 저항(Ω)입니다.</summary>
    public ResistanceOhms? ResistanceOhms { get; }

    /// <summary>계산한 회로 전류(A)입니다.</summary>
    public CurrentAmps? CurrentAmps { get; }

    /// <summary>계산한 회로 전력(W)입니다.</summary>
    public PowerWatts? PowerWatts { get; }

    /// <summary>회로 계산의 최종 상태입니다.</summary>
    public ElectricalStatus Status { get; }

    /// <summary>전압·전류·전력 한도별 판정 목록입니다.</summary>
    public IReadOnlyList<ElectricalLimitResult> Limits { get; }

    /// <summary>계산을 차단한 입력 오류가 있으면 표준 코드로 나타냅니다.</summary>
    public string? FailureCode { get; }

    /// <summary>누락 또는 무효 입력을 설명하는 기계 판독 코드입니다.</summary>
    public IReadOnlyList<string> InputIssues { get; }

    /// <summary>수치, 한도 판정, 의존 지문을 복사해 결과 스냅샷을 만듭니다.</summary>
    public ElectricalResult(
        CircuitId circuitId,
        DependencyStamp dependencyStamp,
        LengthBreakdown lengthBreakdown,
        string ruleVersion,
        LengthMm resistanceLengthMm,
        double? resistanceLengthMeters,
        VoltageVrms? actualVoltageVrms,
        ResistanceOhmsPerMeter? resistanceOhmsPerMeter,
        ResistanceOhms? resistanceOhms,
        CurrentAmps? currentAmps,
        PowerWatts? powerWatts,
        ElectricalStatus status,
        IEnumerable<ElectricalLimitResult> limits,
        string? failureCode = null,
        IEnumerable<string>? inputIssues = null)
    {
        ArgumentNullException.ThrowIfNull(dependencyStamp);
        ArgumentNullException.ThrowIfNull(lengthBreakdown);
        ArgumentNullException.ThrowIfNull(limits);
        if (string.IsNullOrWhiteSpace(ruleVersion))
            throw new ArgumentException("전기 계산 규칙 버전을 입력해야 합니다.", nameof(ruleVersion));
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (resistanceLengthMeters.HasValue && !double.IsFinite(resistanceLengthMeters.Value))
            throw new ArgumentOutOfRangeException(nameof(resistanceLengthMeters));

        CircuitId = circuitId;
        DependencyStamp = dependencyStamp;
        LengthBreakdown = lengthBreakdown;
        RuleVersion = ruleVersion.Trim();
        ResistanceLengthMm = resistanceLengthMm;
        ResistanceLengthMeters = resistanceLengthMeters;
        ActualVoltageVrms = actualVoltageVrms;
        ResistanceOhmsPerMeter = resistanceOhmsPerMeter;
        ResistanceOhms = resistanceOhms;
        CurrentAmps = currentAmps;
        PowerWatts = powerWatts;
        Status = status;
        Limits = new ReadOnlyCollection<ElectricalLimitResult>(limits.ToArray());
        FailureCode = string.IsNullOrWhiteSpace(failureCode) ? null : failureCode.Trim();
        InputIssues = new ReadOnlyCollection<string>((inputIssues ?? Array.Empty<string>()).ToArray());
    }
}

/// <summary>회로 전기 계산의 전체 상태입니다.</summary>
public enum ElectricalStatus
{
    /// <summary>필수 입력이 있고 모든 한도를 만족합니다.</summary>
    Pass,

    /// <summary>전기값은 계산했으나 하나 이상의 한도를 초과했습니다.</summary>
    LimitExceeded,

    /// <summary>한도 또는 필수 입력이 누락되어 판정이 완료되지 않았습니다.</summary>
    Incomplete,

    /// <summary>숫자 입력이 무효이거나 계산 범위를 벗어났습니다.</summary>
    InvalidInput
}

/// <summary>전압·전류·전력 중 판정할 회로 한도 종류입니다.</summary>
public enum ElectricalLimitKind
{
    /// <summary>회로 최대 전압입니다.</summary>
    MaximumVoltage,

    /// <summary>회로 최대 전류입니다.</summary>
    MaximumCurrent,

    /// <summary>선택 입력인 회로 최대 전력입니다.</summary>
    MaximumPower
}

/// <summary>한도별 실제값, 허용값과 판정 상태입니다.</summary>
public sealed record ElectricalLimitResult(
    ElectricalLimitKind Kind,
    LimitCheckStatus Status,
    double? MeasuredValue,
    double? AllowedValue,
    double? ExceedanceRatio,
    string UnitSymbol);

/// <summary>개별 전기 한도의 판정 상태입니다.</summary>
public enum LimitCheckStatus
{
    /// <summary>측정값이 한도 이내이거나 한도와 같습니다.</summary>
    WithinLimit,

    /// <summary>측정값이 허용 한도를 넘었습니다.</summary>
    Exceeded,

    /// <summary>선택 또는 필수 한도 입력이 없습니다.</summary>
    Missing,

    /// <summary>측정 결과나 한도값이 유효하지 않습니다.</summary>
    Invalid
}
