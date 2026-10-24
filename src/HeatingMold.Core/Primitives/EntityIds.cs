namespace HeatingMold.Core.Primitives;

/// <summary>JSON에 저장하는 Guid 기반 식별자입니다.</summary>
public readonly record struct ProjectId(Guid Value)
{
    /// <summary>새 프로젝트 식별자를 만듭니다.</summary>
    public static ProjectId New() => new(Guid.NewGuid());

    /// <summary>빈 Guid인지 나타냅니다.</summary>
    public bool IsEmpty => Value == Guid.Empty;
}

/// <summary>가져온 면의 영속 참조 식별자입니다.</summary>
public readonly record struct FaceRefId(Guid Value)
{
    /// <summary>새 면 참조 식별자를 만듭니다.</summary>
    public static FaceRefId New() => new(Guid.NewGuid());

    /// <summary>빈 Guid인지 나타냅니다.</summary>
    public bool IsEmpty => Value == Guid.Empty;
}

/// <summary>발열 영역의 영속 식별자입니다.</summary>
public readonly record struct RegionId(Guid Value)
{
    /// <summary>새 영역 식별자를 만듭니다.</summary>
    public static RegionId New() => new(Guid.NewGuid());

    /// <summary>빈 Guid인지 나타냅니다.</summary>
    public bool IsEmpty => Value == Guid.Empty;
}

/// <summary>발열 영역의 경계 구간 영속 식별자입니다.</summary>
public readonly record struct BoundarySegmentId(Guid Value)
{
    /// <summary>새 경계 구간 식별자를 만듭니다.</summary>
    public static BoundarySegmentId New() => new(Guid.NewGuid());

    /// <summary>빈 Guid인지 나타냅니다.</summary>
    public bool IsEmpty => Value == Guid.Empty;
}

/// <summary>발열 영역 경계 루프의 영속 식별자입니다.</summary>
public readonly record struct BoundaryLoopId(Guid Value)
{
    /// <summary>새 경계 루프 식별자를 만듭니다.</summary>
    public static BoundaryLoopId New() => new(Guid.NewGuid());

    /// <summary>빈 Guid인지 나타냅니다.</summary>
    public bool IsEmpty => Value == Guid.Empty;
}

/// <summary>발열사 경로의 영속 식별자입니다.</summary>
public readonly record struct HeatingPathId(Guid Value)
{
    /// <summary>새 발열사 경로 식별자를 만듭니다.</summary>
    public static HeatingPathId New() => new(Guid.NewGuid());

    /// <summary>빈 Guid인지 나타냅니다.</summary>
    public bool IsEmpty => Value == Guid.Empty;
}

/// <summary>발열사 경로 구간의 영속 식별자입니다.</summary>
public readonly record struct HeatingPathSegmentId(Guid Value)
{
    /// <summary>새 경로 구간 식별자를 만듭니다.</summary>
    public static HeatingPathSegmentId New() => new(Guid.NewGuid());

    /// <summary>빈 Guid인지 나타냅니다.</summary>
    public bool IsEmpty => Value == Guid.Empty;
}

/// <summary>발열사 경로를 편집하는 제어점의 영속 식별자입니다.</summary>
public readonly record struct ControlPointId(Guid Value)
{
    /// <summary>새 제어점 식별자를 만듭니다.</summary>
    public static ControlPointId New() => new(Guid.NewGuid());

    /// <summary>빈 Guid인지 나타냅니다.</summary>
    public bool IsEmpty => Value == Guid.Empty;
}

/// <summary>회로의 영속 식별자입니다.</summary>
public readonly record struct CircuitId(Guid Value)
{
    /// <summary>새 회로 식별자를 만듭니다.</summary>
    public static CircuitId New() => new(Guid.NewGuid());

    /// <summary>빈 Guid인지 나타냅니다.</summary>
    public bool IsEmpty => Value == Guid.Empty;
}

/// <summary>회로 끝의 연결부 위치를 나타내는 영속 식별자입니다.</summary>
public readonly record struct ConnectionPointId(Guid Value)
{
    /// <summary>새 연결부 위치 식별자를 만듭니다.</summary>
    public static ConnectionPointId New() => new(Guid.NewGuid());

    /// <summary>빈 Guid인지 나타냅니다.</summary>
    public bool IsEmpty => Value == Guid.Empty;
}

/// <summary>연결부 위치까지의 연결 경로를 나타내는 영속 식별자입니다.</summary>
public readonly record struct ConnectionPathId(Guid Value)
{
    /// <summary>새 연결 경로 식별자를 만듭니다.</summary>
    public static ConnectionPathId New() => new(Guid.NewGuid());

    /// <summary>빈 Guid인지 나타냅니다.</summary>
    public bool IsEmpty => Value == Guid.Empty;
}

/// <summary>연결 경로의 안정 구간 ID입니다.</summary>
public readonly record struct ConnectionPathSegmentId(Guid Value)
{
    /// <summary>새 연결 경로 구간 ID를 만듭니다.</summary>
    public static ConnectionPathSegmentId New() => new(Guid.NewGuid());

    /// <summary>비어 있는 ID인지 나타냅니다.</summary>
    public bool IsEmpty => Value == Guid.Empty;
}

/// <summary>열린 네이티브 기하 문서에만 사용하는 세션 식별자입니다.</summary>
public readonly record struct GeometryDocumentId(Guid Value)
{
    /// <summary>새 기하 문서 세션 식별자를 만듭니다.</summary>
    public static GeometryDocumentId New() => new(Guid.NewGuid());

    /// <summary>빈 Guid인지 나타냅니다.</summary>
    public bool IsEmpty => Value == Guid.Empty;
}

/// <summary>확정 전 변경 후보에만 사용하는 런타임 식별자입니다.</summary>
public readonly record struct CandidateId(Guid Value)
{
    /// <summary>새 변경 후보 식별자를 만듭니다.</summary>
    public static CandidateId New() => new(Guid.NewGuid());

    /// <summary>빈 Guid인지 나타냅니다.</summary>
    public bool IsEmpty => Value == Guid.Empty;
}
