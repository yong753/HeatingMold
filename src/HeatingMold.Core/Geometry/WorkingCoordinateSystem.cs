namespace HeatingMold.Core.Geometry;

/// <summary>열린 네이티브 문서의 기하 revision을 나타냅니다.</summary>
public readonly record struct ModelRevision
{
    /// <summary>모델 문서 안에서 증가하는 기하 세대입니다.</summary>
    public long Value { get; }

    /// <summary>음수가 아닌 모델 기하 세대를 만듭니다.</summary>
    public ModelRevision(long value)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), "모델 revision은 음수일 수 없습니다.");

        Value = value;
    }
}
/// <summary>런타임 작업 좌표계 context의 식별자입니다.</summary>
public readonly record struct WorkingCoordinateSystemId(long Value);

/// <summary>카메라 상태와 독립적으로 모델에 고정되는 작업 좌표계입니다.</summary>
public sealed record WorkingCoordinateSystem
{
    /// <summary>네이티브 작업 좌표계 context의 런타임 식별자입니다.</summary>
    public WorkingCoordinateSystemId Id { get; }

    /// <summary>작업 좌표계가 참조하는 열린 모델의 기하 revision입니다.</summary>
    public ModelRevision Revision { get; }

    /// <summary>원점과 오른손 X/Y/N축을 정의하는 정사영 좌표계입니다.</summary>
    public ProjectionFrame Projection { get; }

    /// <summary>식별자, 모델 revision, 좌표계 값으로 세션용 값을 만듭니다.</summary>
    public WorkingCoordinateSystem(WorkingCoordinateSystemId id, ModelRevision revision, ProjectionFrame projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        Id = id;
        Revision = revision;
        Projection = projection;
    }

    /// <summary>모델 바깥 datum에서 표면을 질의하는 작업축 방향입니다.</summary>
    public Vec3 RayDirection => Projection.Normal * -1;

    /// <summary>좌표계 식별자, 모델 revision, 투영 좌표계를 분리해 반환합니다.</summary>
    public void Deconstruct(out WorkingCoordinateSystemId id, out ModelRevision revision, out ProjectionFrame projection)
    {
        id = Id;
        revision = Revision;
        projection = Projection;
    }
}
