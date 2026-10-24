namespace HeatingMold.Application.Transactions;

/// <summary>확정 변경에서 다시 계산해야 할 결과 종류입니다.</summary>
[Flags]
public enum ChangeImpact
{
    /// <summary>파생 계산 재사용이 가능한 변경입니다.</summary>
    None = 0,

    /// <summary>Native 경로·표면 기하를 다시 준비합니다.</summary>
    Geometry = 1 << 0,

    /// <summary>최종 길이 원장을 다시 측정합니다.</summary>
    Length = 1 << 1,

    /// <summary>실제 표면 요소와 회로별 면적을 다시 배정합니다.</summary>
    Area = 1 << 2,

    /// <summary>회로 전기값과 한도 판정을 다시 계산합니다.</summary>
    Electrical = 1 << 3,

    /// <summary>저장·출력 snapshot을 새 revision에 맞춥니다.</summary>
    Export = 1 << 4
}

/// <summary>확정 입력 종류와 파생 결과의 재계산 범위를 연결합니다.</summary>
public enum ChangeKind
{
    /// <summary>원본 모델 또는 작업 좌표계 변경입니다.</summary>
    ModelOrWorkingCoordinateSystem,

    /// <summary>영역, 간격, 깊이, 방향, 앵커 변경입니다.</summary>
    RegionOrPlacement,

    /// <summary>공유 절점 또는 본 경로 곡선 변경입니다.</summary>
    HeatingPathGeometry,

    /// <summary>회로 소속과 연결부 배치 변경입니다.</summary>
    CircuitTopology,

    /// <summary>연결 경로·재료 역할·여유 변경입니다.</summary>
    ConnectionPath,

    /// <summary>Supply·단위 저항·전기 한도 변경입니다.</summary>
    ElectricalSpecification
}

/// <summary>변경 영향표를 단일 정책으로 제공합니다.</summary>
public static class ChangeImpactPolicy
{
    /// <summary>입력 변경 종류에 필요한 파생 계산 조합을 반환합니다.</summary>
    public static ChangeImpact For(ChangeKind changeKind) => changeKind switch
    {
        ChangeKind.ModelOrWorkingCoordinateSystem =>
            ChangeImpact.Geometry | ChangeImpact.Length | ChangeImpact.Area |
            ChangeImpact.Electrical | ChangeImpact.Export,
        ChangeKind.RegionOrPlacement or ChangeKind.HeatingPathGeometry =>
            ChangeImpact.Geometry | ChangeImpact.Length | ChangeImpact.Area |
            ChangeImpact.Electrical | ChangeImpact.Export,
        ChangeKind.CircuitTopology =>
            ChangeImpact.Length | ChangeImpact.Area | ChangeImpact.Electrical | ChangeImpact.Export,
        ChangeKind.ConnectionPath =>
            ChangeImpact.Geometry | ChangeImpact.Length | ChangeImpact.Electrical | ChangeImpact.Export,
        ChangeKind.ElectricalSpecification => ChangeImpact.Electrical | ChangeImpact.Export,
        _ => throw new ArgumentOutOfRangeException(nameof(changeKind))
    };
}
