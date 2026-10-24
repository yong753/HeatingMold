using System;
using System.Collections.Generic;
using System.Threading;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;

namespace HeatingMold.App.Geometry.Occt;

/// <summary>전기 전용 후보처럼 Native 질의를 사용하지 않는 준비 상태를 명시합니다.</summary>
internal sealed class GeometryQueriesUnavailable : IGeometryQueries
{
    public static GeometryQueriesUnavailable Instance { get; } = new();

    private GeometryQueriesUnavailable()
    {
    }

    /// <summary>질의할 후보 context가 없어 요청을 수행하지 않습니다.</summary>
    public GeometryQueryResult<SurfaceHit> QueryFirstHit(
        WorkingCoordinateSystem coordinateSystem,
        Vec2 workingUv,
        FaceRefId selectedFaceReferenceId,
        CancellationToken cancellationToken = default) => ThrowUnavailable<SurfaceHit>();

    /// <summary>전기 전용 준비 상태에서는 선택 topology를 조회하지 않습니다.</summary>
    public GeometryQueryResult<SelectedTopology> GetSelectedTopology(
        IReadOnlyList<FaceRefId> selectedFaceReferences,
        CancellationToken cancellationToken = default) => ThrowUnavailable<SelectedTopology>();

    /// <summary>전기 전용 준비 상태에서는 Face 인접성을 조회하지 않습니다.</summary>
    public GeometryQueryResult<bool> AreAdjacent(
        FaceRefId first,
        FaceRefId second,
        CancellationToken cancellationToken = default) => ThrowUnavailable<bool>();

    /// <summary>전기 전용 준비 상태에서는 곡선 간격을 조회하지 않습니다.</summary>
    public GeometryQueryResult<ClearanceMeasurement> MeasureClearance(
        IReadOnlyList<CurveGeometrySnapshot> curves,
        LengthMm wireDiameterMm,
        LengthMm requiredClearGapMm,
        GeometryToleranceProfile tolerances,
        CancellationToken cancellationToken = default) => ThrowUnavailable<ClearanceMeasurement>();

    /// <summary>전기 전용 준비 상태에서는 삽입 깊이를 조회하지 않습니다.</summary>
    public GeometryQueryResult<DepthMeasurement> MeasureDepth(
        CurveGeometrySnapshot curve,
        LengthMm wireDiameterMm,
        SurfaceAnchor anchor,
        WorkingCoordinateSystem coordinateSystem,
        LengthMm requiredInsertionDepthMm,
        CancellationToken cancellationToken = default) => ThrowUnavailable<DepthMeasurement>();

    /// <summary>전기 전용 준비 상태에서는 표면 요소를 조회하지 않습니다.</summary>
    public GeometryQueryResult<SurfaceElementSet> GetSurfaceElements(
        HeatingRegion region,
        GeometryToleranceProfile tolerances,
        CancellationToken cancellationToken = default) => ThrowUnavailable<SurfaceElementSet>();

    /// <summary>전기 전용 준비 상태에서는 연결 경로 장애물을 조회하지 않습니다.</summary>
    public GeometryQueryResult<ConnectionObstacleCheck> CheckConnectionPathObstacles(
        ConnectionPath connectionPath,
        WorkingCoordinateSystem coordinateSystem,
        CancellationToken cancellationToken = default) => ThrowUnavailable<ConnectionObstacleCheck>();

    private static GeometryQueryResult<T> ThrowUnavailable<T>() =>
        throw new InvalidOperationException("전기 전용 후보에는 Native 기하 질의가 연결되어 있지 않습니다.");
}
