using HeatingMold.Core.Primitives;

namespace HeatingMold.Infrastructure.Geometry.Occt;

/// <summary>네이티브 정수 ID가 가리키는 설계 요소의 종류입니다.</summary>
public enum RuntimeGeometryKind
{
    /// <summary>발열 경로 구간입니다.</summary>
    HeatingPathSegment,

    /// <summary>연결 경로 구간입니다.</summary>
    ConnectionPathSegment,

    /// <summary>회로 끝까지 이어지는 연결 경로입니다.</summary>
    ConnectionPath
}

/// <summary>네이티브 표시 ID가 속한 확정·후보·미리보기 수명입니다.</summary>
public enum RuntimeGeometryLifetime
{
    /// <summary>현재 확정된 기하입니다.</summary>
    Committed,

    /// <summary>Publish 전 검증 중인 후보 기하입니다.</summary>
    Staging,

    /// <summary>취소할 수 있는 화면 미리보기 기하입니다.</summary>
    Preview
}

/// <summary>관리형 영속 ID와 네이티브 런타임 ID의 역방향 대응값입니다.</summary>
public readonly record struct RuntimeGeometryReference(
    Guid EntityId,
    RuntimeGeometryKind Kind,
    RuntimeGeometryLifetime Lifetime);

/// <summary>문서 하나에서만 유효한 네이티브 정수 ID 대응을 관리합니다.</summary>
public sealed class RuntimeIdMap
{
    private readonly Dictionary<RuntimeGeometryReference, int> _runtimeIds = [];
    private readonly Dictionary<int, RuntimeGeometryReference> _references = [];
    private long _nextRuntimeId = 1;

    /// <summary>발열 구간과 지정 수명의 네이티브 ID를 반환하거나 새로 만듭니다.</summary>
    public int GetOrAdd(HeatingPathSegmentId id, RuntimeGeometryLifetime lifetime) =>
        GetOrAdd(id.Value, RuntimeGeometryKind.HeatingPathSegment, lifetime);

    /// <summary>연결 구간과 지정 수명의 네이티브 ID를 반환하거나 새로 만듭니다.</summary>
    public int GetOrAdd(ConnectionPathSegmentId id, RuntimeGeometryLifetime lifetime) =>
        GetOrAdd(id.Value, RuntimeGeometryKind.ConnectionPathSegment, lifetime);

    /// <summary>연결 경로와 지정 수명의 네이티브 ID를 반환하거나 새로 만듭니다.</summary>
    public int GetOrAdd(ConnectionPathId id, RuntimeGeometryLifetime lifetime) =>
        GetOrAdd(id.Value, RuntimeGeometryKind.ConnectionPath, lifetime);

    /// <summary>발열 구간과 수명이 이미 대응되면 네이티브 ID를 반환합니다.</summary>
    public bool TryGetRuntimeId(
        HeatingPathSegmentId id,
        RuntimeGeometryLifetime lifetime,
        out int runtimeId) =>
        TryGetRuntimeId(id.Value, RuntimeGeometryKind.HeatingPathSegment, lifetime, out runtimeId);

    /// <summary>연결 구간과 수명이 이미 대응되면 네이티브 ID를 반환합니다.</summary>
    public bool TryGetRuntimeId(
        ConnectionPathSegmentId id,
        RuntimeGeometryLifetime lifetime,
        out int runtimeId) =>
        TryGetRuntimeId(id.Value, RuntimeGeometryKind.ConnectionPathSegment, lifetime, out runtimeId);

    /// <summary>연결 경로와 수명이 이미 대응되면 네이티브 ID를 반환합니다.</summary>
    public bool TryGetRuntimeId(
        ConnectionPathId id,
        RuntimeGeometryLifetime lifetime,
        out int runtimeId) =>
        TryGetRuntimeId(id.Value, RuntimeGeometryKind.ConnectionPath, lifetime, out runtimeId);

    /// <summary>네이티브 ID가 이 문서 맵에 있으면 안정 ID·종류·수명을 돌려줍니다.</summary>
    public bool TryResolve(int runtimeId, out RuntimeGeometryReference reference)
    {
        if (runtimeId <= 0)
        {
            reference = default;
            return false;
        }

        return _references.TryGetValue(runtimeId, out reference);
    }

    /// <summary>취소할 후보나 미리보기의 대응만 해제하고 확정 ID는 유지합니다.</summary>
    public int RemoveLifetime(RuntimeGeometryLifetime lifetime)
    {
        ValidateLifetime(lifetime);
        RuntimeGeometryReference[] removed = _runtimeIds.Keys
            .Where(reference => reference.Lifetime == lifetime)
            .ToArray();

        foreach (RuntimeGeometryReference reference in removed)
        {
            int runtimeId = _runtimeIds[reference];
            _runtimeIds.Remove(reference);
            _references.Remove(runtimeId);
        }

        return removed.Length;
    }

    /// <summary>문서를 닫을 때 해당 문서의 모든 런타임 ID 대응을 해제합니다.</summary>
    public void Clear()
    {
        _runtimeIds.Clear();
        _references.Clear();
        _nextRuntimeId = 1;
    }

    private int GetOrAdd(Guid entityId, RuntimeGeometryKind kind, RuntimeGeometryLifetime lifetime)
    {
        ValidateEntityId(entityId);
        ValidateKind(kind);
        ValidateLifetime(lifetime);

        var reference = new RuntimeGeometryReference(entityId, kind, lifetime);
        if (_runtimeIds.TryGetValue(reference, out int existingId))
            return existingId;

        if (_nextRuntimeId > int.MaxValue)
            throw new InvalidOperationException("네이티브 런타임 ID 공간을 모두 사용했습니다.");

        int runtimeId = (int)_nextRuntimeId++;
        _runtimeIds.Add(reference, runtimeId);
        _references.Add(runtimeId, reference);
        return runtimeId;
    }

    private bool TryGetRuntimeId(
        Guid entityId,
        RuntimeGeometryKind kind,
        RuntimeGeometryLifetime lifetime,
        out int runtimeId)
    {
        if (entityId == Guid.Empty || !Enum.IsDefined(kind) || !Enum.IsDefined(lifetime))
        {
            runtimeId = default;
            return false;
        }

        return _runtimeIds.TryGetValue(new RuntimeGeometryReference(entityId, kind, lifetime), out runtimeId);
    }

    private static void ValidateEntityId(Guid entityId)
    {
        if (entityId == Guid.Empty)
            throw new ArgumentException("런타임 ID 대응에는 안정 요소 ID가 필요합니다.", nameof(entityId));
    }

    private static void ValidateKind(RuntimeGeometryKind kind)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
    }

    private static void ValidateLifetime(RuntimeGeometryLifetime lifetime)
    {
        if (!Enum.IsDefined(lifetime))
            throw new ArgumentOutOfRangeException(nameof(lifetime));
    }
}
