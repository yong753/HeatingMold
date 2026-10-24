using System.Collections.ObjectModel;
using HeatingMold.Core.Primitives;

namespace HeatingMold.Core.Circuits;

/// <summary>실제 3차원 길이와 추가 여유를 출처 구간별로 보관하는 원장입니다.</summary>
public sealed class LengthBreakdown
{
    /// <summary>본 발열 경로의 최종 기하 길이입니다.</summary>
    public LengthMm HeatingBodyMm { get; }

    /// <summary>같은 발열사로 된 연결 연장부의 최종 기하 길이입니다.</summary>
    public LengthMm HeatingExtensionMm { get; }

    /// <summary>저항 계산에서 제외하는 별도 연결선의 최종 기하 길이입니다.</summary>
    public LengthMm SeparateConnectionWireMm { get; }

    /// <summary>본 경로와 발열사 연장부에 추가로 반영할 여유 길이입니다.</summary>
    public LengthMm HeatingExtraAllowanceMm { get; }

    /// <summary>별도 연결선에 추가로 반영할 여유 길이입니다.</summary>
    public LengthMm SeparateConnectionWireExtraAllowanceMm { get; }

    /// <summary>저항·전류·전력 계산에 포함하는 총 발열사 길이입니다.</summary>
    public LengthMm ResistanceLengthMm { get; }

    /// <summary>중복 없는 원본 길이 항목의 읽기 전용 목록입니다.</summary>
    public IReadOnlyList<LengthLedgerEntry> Entries { get; }

    /// <summary>출처 ID 중복과 음수 길이를 검사하고 재료 역할별 합계를 계산합니다.</summary>
    public LengthBreakdown(IEnumerable<LengthLedgerEntry> entries)
    {
        LengthLedgerEntry[] copiedEntries = entries?.ToArray()
            ?? throw new ArgumentNullException(nameof(entries));

        HashSet<string> sourceKeys = new(StringComparer.Ordinal);
        foreach (LengthLedgerEntry entry in copiedEntries)
        {
            entry.Validate();
            if (!sourceKeys.Add(entry.SourceKey))
                throw new ArgumentException($"같은 경로 구간이 길이 원장에 중복되었습니다: {entry.SourceKey}", nameof(entries));
        }

        Entries = new ReadOnlyCollection<LengthLedgerEntry>(copiedEntries);
        HeatingBodyMm = Sum(LengthContributionKind.HeatingBody, copiedEntries, useAllowance: false);
        HeatingExtensionMm = Sum(LengthContributionKind.HeatingWireExtension, copiedEntries, useAllowance: false);
        SeparateConnectionWireMm = Sum(LengthContributionKind.SeparateConnectionWire, copiedEntries, useAllowance: false);
        HeatingExtraAllowanceMm = SumHeatingAllowances(copiedEntries);
        SeparateConnectionWireExtraAllowanceMm = Sum(
            LengthContributionKind.SeparateConnectionWire, copiedEntries, useAllowance: true);
        ResistanceLengthMm = HeatingBodyMm + HeatingExtensionMm + HeatingExtraAllowanceMm;
    }

    private static LengthMm Sum(
        LengthContributionKind kind,
        IEnumerable<LengthLedgerEntry> entries,
        bool useAllowance)
    {
        double total = 0;
        foreach (LengthLedgerEntry entry in entries.Where(entry => entry.Kind == kind))
        {
            total += useAllowance ? entry.AdditionalAllowanceMm.Value : entry.MeasuredLengthMm.Value;
            if (!double.IsFinite(total))
                throw new ArithmeticException("길이 원장 합계가 숫자 표현 범위를 벗어났습니다.");
        }

        return new LengthMm(total);
    }

    private static LengthMm SumHeatingAllowances(IEnumerable<LengthLedgerEntry> entries)
    {
        double total = entries
            .Where(entry => entry.Kind != LengthContributionKind.SeparateConnectionWire)
            .Sum(entry => entry.AdditionalAllowanceMm.Value);

        if (!double.IsFinite(total))
            throw new ArithmeticException("발열사 추가 여유 길이 합계가 숫자 표현 범위를 벗어났습니다.");

        return new LengthMm(total);
    }
}

/// <summary>길이 원장 구간의 도체 재료 역할입니다.</summary>
public enum LengthContributionKind
{
    /// <summary>회로의 본 발열 경로입니다.</summary>
    HeatingBody,

    /// <summary>같은 발열사 재료로 연장한 연결 경로입니다.</summary>
    HeatingWireExtension,

    /// <summary>발열사와 분리된 연결선입니다.</summary>
    SeparateConnectionWire
}

/// <summary>하나의 실제 경로 구간에서 측정한 길이와 추가 여유입니다.</summary>
public readonly record struct LengthLedgerEntry
{
    /// <summary>본 발열 경로 구간 ID입니다.</summary>
    public HeatingPathSegmentId? HeatingPathSegmentId { get; }

    /// <summary>연결 경로 구간 ID입니다.</summary>
    public ConnectionPathSegmentId? ConnectionPathSegmentId { get; }

    /// <summary>구간의 도체 재료 역할입니다.</summary>
    public LengthContributionKind Kind { get; }

    /// <summary>금형 위 또는 외부라는 공간 구분입니다.</summary>
    public GeometryDomain Domain { get; }

    /// <summary>네이티브 기하에서 측정한 최종 3차원 길이입니다.</summary>
    public LengthMm MeasuredLengthMm { get; }

    /// <summary>실제 형상에 아직 포함하지 않은 추가 길이입니다.</summary>
    public LengthMm AdditionalAllowanceMm { get; }

    /// <summary>정확히 하나의 안정 ID와 유한한 비음수 길이로 원장 구간을 만듭니다.</summary>
    public LengthLedgerEntry(
        HeatingPathSegmentId? heatingPathSegmentId,
        ConnectionPathSegmentId? connectionPathSegmentId,
        LengthContributionKind kind,
        GeometryDomain domain,
        LengthMm measuredLengthMm,
        LengthMm additionalAllowanceMm)
    {
        HeatingPathSegmentId = heatingPathSegmentId;
        ConnectionPathSegmentId = connectionPathSegmentId;
        Kind = kind;
        Domain = domain;
        MeasuredLengthMm = measuredLengthMm;
        AdditionalAllowanceMm = additionalAllowanceMm;
        Validate();
    }

    /// <summary>원장 항목의 ID 종류, 재료 역할, 단위와 부호를 검증합니다.</summary>
    public void Validate()
    {
        bool hasHeatingPathId = HeatingPathSegmentId.HasValue;
        bool hasConnectionPathId = ConnectionPathSegmentId.HasValue;
        if (hasHeatingPathId == hasConnectionPathId)
            throw new ArgumentException("길이 항목에는 발열 경로 또는 연결 경로 ID 중 하나만 지정해야 합니다.");
        if (HeatingPathSegmentId is { IsEmpty: true } || ConnectionPathSegmentId is { IsEmpty: true })
            throw new ArgumentException("길이 항목의 출처 ID는 비어 있을 수 없습니다.");
        if (!Enum.IsDefined(Kind) || !Enum.IsDefined(Domain))
            throw new ArgumentOutOfRangeException(nameof(Kind), "길이 항목의 역할이나 공간 구분이 올바르지 않습니다.");
        if (Kind == LengthContributionKind.HeatingBody && !hasHeatingPathId ||
            Kind != LengthContributionKind.HeatingBody && !hasConnectionPathId)
        {
            throw new ArgumentException("본 발열 경로와 연결 경로의 ID 종류가 재료 역할과 일치하지 않습니다.");
        }
        if (MeasuredLengthMm.Value < 0 || AdditionalAllowanceMm.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(MeasuredLengthMm), "측정 길이와 추가 여유는 음수일 수 없습니다.");
    }

    /// <summary>중복 검사를 위해 출처 ID에 종류 접두사를 붙입니다.</summary>
    internal string SourceKey => HeatingPathSegmentId is { } heatingId
        ? $"heating:{heatingId.Value:N}"
        : $"connection:{ConnectionPathSegmentId!.Value.Value:N}";
}
