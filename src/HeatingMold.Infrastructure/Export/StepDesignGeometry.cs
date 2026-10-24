using System.Collections.ObjectModel;
using HeatingMold.Application.Ports.Geometry;
using HeatingMold.Infrastructure.Geometry.Occt;

namespace HeatingMold.Infrastructure.Export;

/// <summary>STEP 출력에서 하나의 연속 wire를 이루는 최종 곡선 묶음입니다.</summary>
public sealed class StepDesignWire
{
    /// <summary>출력 wire를 식별하는 읽기 쉬운 이름입니다.</summary>
    public string Name { get; }

    /// <summary>경로 순서와 진행 방향이 보존된 곡선 정의입니다.</summary>
    public IReadOnlyList<StepDesignCurve> Curves { get; }

    /// <summary>이름과 하나 이상의 연속 곡선을 방어 복사해 보관합니다.</summary>
    public StepDesignWire(string name, IEnumerable<StepDesignCurve> curves)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("STEP wire 이름이 필요합니다.", nameof(name));

        StepDesignCurve[] copiedCurves = curves?.ToArray()
            ?? throw new ArgumentNullException(nameof(curves));
        if (copiedCurves.Length == 0)
            throw new ArgumentException("STEP wire에는 곡선이 하나 이상 필요합니다.", nameof(curves));
        if (copiedCurves.Any(curve => curve is null))
            throw new ArgumentException("STEP wire는 null 곡선을 포함할 수 없습니다.", nameof(curves));

        Name = name.Trim();
        Curves = new ReadOnlyCollection<StepDesignCurve>(copiedCurves);
    }
}

/// <summary>하나의 회로 조각을 OCCT wire 생성기에 전달하는 곡선입니다.</summary>
public sealed record StepDesignCurve
{
    /// <summary>영속 발열 또는 연결 경로 구간 ID입니다.</summary>
    public Guid SegmentId { get; }

    /// <summary>OccBridge가 재구성할 형식별 곡선 정의입니다.</summary>
    public CurveDefinitionBridgeDto Definition { get; }

    /// <summary>회로 경로가 원 정의를 역방향으로 따라가는지 나타냅니다.</summary>
    public bool IsReversed { get; }

    /// <summary>유효 구간 ID와 정확한 곡선 정의, 진행 방향을 연결합니다.</summary>
    public StepDesignCurve(Guid segmentId, CurveDefinitionBridgeDto definition, bool isReversed)
    {
        if (segmentId == Guid.Empty)
            throw new ArgumentException("STEP 곡선에는 영속 구간 ID가 필요합니다.", nameof(segmentId));

        SegmentId = segmentId;
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        IsReversed = isReversed;
    }
}

/// <summary>회로별 발열 wire와 각 연결부 wire를 묶은 STEP 출력 그룹입니다.</summary>
public sealed class StepDesignGroup
{
    /// <summary>회로 그룹이면 회로 ID이며 미배정 경로 그룹은 null입니다.</summary>
    public Guid? CircuitId { get; }

    /// <summary>출력 계층에서 회로 또는 미배정 경로를 구분하는 이름입니다.</summary>
    public string Name { get; }

    /// <summary>발열 경로와 연결 경로 wire의 읽기 전용 목록입니다.</summary>
    public IReadOnlyList<StepDesignWire> Wires { get; }

    /// <summary>회로 소속과 포함 wire를 방어 복사해 출력 그룹을 만듭니다.</summary>
    public StepDesignGroup(Guid? circuitId, string name, IEnumerable<StepDesignWire> wires)
    {
        if (circuitId == Guid.Empty)
            throw new ArgumentException("회로 그룹 ID는 비어 있을 수 없습니다.", nameof(circuitId));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("STEP 그룹 이름이 필요합니다.", nameof(name));

        StepDesignWire[] copiedWires = wires?.ToArray()
            ?? throw new ArgumentNullException(nameof(wires));
        if (copiedWires.Any(wire => wire is null))
            throw new ArgumentException("STEP 그룹은 null wire를 포함할 수 없습니다.", nameof(wires));

        CircuitId = circuitId;
        Name = name.Trim();
        Wires = new ReadOnlyCollection<StepDesignWire>(copiedWires);
    }
}

/// <summary>원본 모델과 곡선 묶음을 받아 STEP 형상을 만드는 OCCT 경계입니다.</summary>
public interface IStepGeometryWriterBridge
{
    /// <summary>요청한 곡선을 임시 출력 경로에 기록하고 Native 상태를 반환합니다.</summary>
    StepGeometryWriteResult Write(
        string outputPath,
        ModelSource sourceModel,
        IReadOnlyList<StepDesignGroup> groups,
        bool includeSourceModel,
        CancellationToken cancellationToken = default);
}

/// <summary>OCCT STEP writer가 반환하는 작업 상태입니다.</summary>
public enum StepGeometryWriteStatus
{
    /// <summary>STEP 형상 기록이 완료되었습니다.</summary>
    Success,

    /// <summary>출력 경로나 곡선 묶음 입력이 유효하지 않습니다.</summary>
    InvalidInput,

    /// <summary>원본 STEP 모델을 읽지 못했습니다.</summary>
    SourceModelReadFailed,

    /// <summary>지원하지 않거나 잘못된 곡선 정의가 포함되었습니다.</summary>
    CurveDefinitionInvalid,

    /// <summary>곡선을 연결된 OCCT wire로 만들지 못했습니다.</summary>
    WireBuildFailed,

    /// <summary>OCCT STEP 전송 또는 파일 쓰기가 실패했습니다.</summary>
    WriteFailed,

    /// <summary>Native 경계에서 분류하지 못한 오류가 발생했습니다.</summary>
    GeometryFailure
}

/// <summary>Native STEP writer 상태와 진단을 보관합니다.</summary>
public sealed record StepGeometryWriteResult
{
    /// <summary>STEP 형상 구성·파일 쓰기 상태입니다.</summary>
    public StepGeometryWriteStatus Status { get; }

    /// <summary>실패 원인을 설명하는 진단이며 성공이면 null입니다.</summary>
    public string? Diagnostic { get; }

    /// <summary>성공 상태와 선택적 오류 진단을 모순 없이 보관합니다.</summary>
    public StepGeometryWriteResult(StepGeometryWriteStatus status, string? diagnostic = null)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (status == StepGeometryWriteStatus.Success && !string.IsNullOrWhiteSpace(diagnostic))
            throw new ArgumentException("성공 결과에는 실패 진단을 넣을 수 없습니다.", nameof(diagnostic));
        if (status != StepGeometryWriteStatus.Success && string.IsNullOrWhiteSpace(diagnostic))
            throw new ArgumentException("실패 결과에는 진단이 필요합니다.", nameof(diagnostic));

        Status = status;
        Diagnostic = string.IsNullOrWhiteSpace(diagnostic) ? null : diagnostic.Trim();
    }
}
