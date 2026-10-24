using HeatingMold.Core.Primitives;
using HeatingMold.Infrastructure.Geometry.Occt;

namespace HeatingMold.Infrastructure.Tests.Geometry.Occt;

/// <summary>문서 런타임 ID의 역방향 대응과 후보 수명 격리를 검증합니다.</summary>
public sealed class RuntimeIdMapTests
{
    /// <summary>같은 요소와 수명은 같은 ID를 얻고 종류별 ID 공간은 서로 분리합니다.</summary>
    [Fact]
    public void GetOrAdd_ReusesOnlyTheSameTypedEntityAndLifetime()
    {
        Guid stableId = Guid.NewGuid();
        var heatingSegmentId = new HeatingPathSegmentId(stableId);
        var connectionSegmentId = new ConnectionPathSegmentId(stableId);
        var map = new RuntimeIdMap();

        int committedId = map.GetOrAdd(heatingSegmentId, RuntimeGeometryLifetime.Committed);
        int sameCommittedId = map.GetOrAdd(heatingSegmentId, RuntimeGeometryLifetime.Committed);
        int stagingId = map.GetOrAdd(heatingSegmentId, RuntimeGeometryLifetime.Staging);
        int connectionId = map.GetOrAdd(connectionSegmentId, RuntimeGeometryLifetime.Committed);

        Assert.Equal(committedId, sameCommittedId);
        Assert.NotEqual(committedId, stagingId);
        Assert.NotEqual(committedId, connectionId);
        Assert.NotEqual(stagingId, connectionId);
    }

    /// <summary>네이티브 ID를 안정 요소 ID와 기하 종류·수명으로 되돌립니다.</summary>
    [Fact]
    public void TryResolve_ReturnsStableReference()
    {
        var connectionPathId = ConnectionPathId.New();
        var map = new RuntimeIdMap();
        int runtimeId = map.GetOrAdd(connectionPathId, RuntimeGeometryLifetime.Preview);

        bool found = map.TryResolve(runtimeId, out RuntimeGeometryReference reference);

        Assert.True(found);
        Assert.Equal(connectionPathId.Value, reference.EntityId);
        Assert.Equal(RuntimeGeometryKind.ConnectionPath, reference.Kind);
        Assert.Equal(RuntimeGeometryLifetime.Preview, reference.Lifetime);
        Assert.False(map.TryResolve(-1, out _));
    }

    /// <summary>미리보기와 staging만 해제해 확정 매핑을 유지하고 정수 ID를 재사용하지 않습니다.</summary>
    [Fact]
    public void RemoveLifetime_PreservesCommittedIdsAndDoesNotReuseReleasedIds()
    {
        var map = new RuntimeIdMap();
        HeatingPathSegmentId committedSegment = HeatingPathSegmentId.New();
        int committedId = map.GetOrAdd(committedSegment, RuntimeGeometryLifetime.Committed);
        int previewId = map.GetOrAdd(HeatingPathSegmentId.New(), RuntimeGeometryLifetime.Preview);
        int stagingId = map.GetOrAdd(ConnectionPathSegmentId.New(), RuntimeGeometryLifetime.Staging);

        Assert.Equal(1, map.RemoveLifetime(RuntimeGeometryLifetime.Preview));
        Assert.Equal(1, map.RemoveLifetime(RuntimeGeometryLifetime.Staging));

        Assert.True(map.TryGetRuntimeId(committedSegment, RuntimeGeometryLifetime.Committed, out int retainedId));
        Assert.Equal(committedId, retainedId);
        Assert.False(map.TryResolve(previewId, out _));
        Assert.False(map.TryResolve(stagingId, out _));
        int nextId = map.GetOrAdd(ConnectionPathId.New(), RuntimeGeometryLifetime.Preview);
        Assert.True(nextId > Math.Max(previewId, stagingId));
    }

    /// <summary>빈 안정 ID는 네이티브 요소로 등록하지 않습니다.</summary>
    [Fact]
    public void GetOrAdd_RejectsEmptyStableId()
    {
        var map = new RuntimeIdMap();

        Assert.Throws<ArgumentException>(() =>
            map.GetOrAdd(default(HeatingPathSegmentId), RuntimeGeometryLifetime.Staging));
    }
}
