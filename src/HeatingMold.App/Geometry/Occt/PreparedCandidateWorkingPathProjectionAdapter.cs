using System;
using System.Collections.Generic;
using System.Linq;
using HeatingMold.App.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Infrastructure.Geometry.Occt;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Geometry.Occt;

/// <summary>확정 전 후보 문서에서 경로 곡선을 staging하고 측정하는 브리지 어댑터입니다.</summary>
internal sealed class PreparedCandidateWorkingPathProjectionAdapter : IWorkingPathProjectionBridge
{
    private readonly PreparedCandidateWorkingSurfaceSession _session;
    private readonly GeometryToleranceProfile _tolerances;
    private readonly CurveDefinitionMapper _curveMapper = new();

    /// <summary>한 후보 작업 좌표계 session에 고정된 경로 어댑터를 만듭니다.</summary>
    public PreparedCandidateWorkingPathProjectionAdapter(
        PreparedCandidateWorkingSurfaceSession session,
        GeometryToleranceProfile tolerances = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _tolerances = tolerances ?? GeometryToleranceProfile.Initial;
    }

    /// <summary>후보 형상으로 투영한 경로 조각을 후보 session 안에서 측정합니다.</summary>
    public WorkingPathSegmentMeasurement StageProjectedPathSegment(
        WorkingCoordinateSystem coordinateSystem,
        int stagingId,
        string kind,
        IReadOnlyList<Vec3> modelPoints,
        IReadOnlyList<int> selectedFaceIndices,
        Vec3 projectionDirection)
    {
        ValidateCoordinateSystem(coordinateSystem);
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("후보 경로 조각의 역할이 필요합니다.", nameof(kind));
        ArgumentNullException.ThrowIfNull(modelPoints);
        ArgumentNullException.ThrowIfNull(selectedFaceIndices);

        double[] points = BridgeWorkingPathProjectionAdapter.Flatten(modelPoints);
        var definition = new CurveDefinitionBridgeDto("Polyline", "Forward", points: points);
        HeatingPathSegmentResult result = Bridge.StagePreparedCandidateWorkingCurveSegment(
            _session.CandidateId,
            coordinateSystem.Id.Value,
            coordinateSystem.Revision.Value,
            stagingId,
            kind,
            CurveDefinitionBridgeAdapter.ToNativeData(definition),
            selectedFaceIndices.ToArray(),
            projectionDirection.X,
            projectionDirection.Y,
            projectionDirection.Z,
            _tolerances.MappingChordDeviationMm,
            _tolerances.MeshAngularToleranceRad,
            _tolerances.MaximumContinuousSampleSpacingMm,
            _tolerances.MappingMaximumDepth,
            _tolerances.MappingMaximumProbes);

        return BridgeWorkingPathProjectionAdapter.ConvertResult(result, requireApproximation: true);
    }

    /// <summary>이미 모델 좌표로 정의된 최종 곡선을 재투영하지 않고 후보에서 측정합니다.</summary>
    public OcctCurveMeasurement StageExactCurveSegment(
        WorkingCoordinateSystem coordinateSystem,
        int stagingId,
        string kind,
        CurveDefinition definition)
    {
        ValidateCoordinateSystem(coordinateSystem);
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("후보 경로 조각의 역할이 필요합니다.", nameof(kind));
        ArgumentNullException.ThrowIfNull(definition);

        CurveDefinitionBridgeDto bridgeDefinition = _curveMapper.ToBridgeDto(definition);
        HeatingPathSegmentResult result = Bridge.StagePreparedCandidateExactCurveSegment(
            _session.CandidateId,
            coordinateSystem.Id.Value,
            coordinateSystem.Revision.Value,
            stagingId,
            kind,
            CurveDefinitionBridgeAdapter.ToNativeData(bridgeDefinition),
            _tolerances.MappingChordDeviationMm,
            _tolerances.MeshAngularToleranceRad,
            _tolerances.MaximumContinuousSampleSpacingMm,
            _tolerances.MappingMaximumDepth,
            _tolerances.MappingMaximumProbes);

        return BridgeWorkingPathProjectionAdapter.ConvertExactCurveMeasurement(result);
    }

    /// <summary>후보 설계의 확정 곡선이 아닌 독립 다중선 요청은 거부합니다.</summary>
    public WorkingPathSegmentMeasurement StageProjectedPolyline(
        WorkingCoordinateSystem coordinateSystem,
        int stagingId,
        IReadOnlyList<Vec3> modelPoints,
        IReadOnlyList<int> selectedFaceIndices,
        Vec3 projectionDirection)
    {
        ValidateCoordinateSystem(coordinateSystem);
        throw new NotSupportedException("후보 기하 session에는 확정 경로 조각만 staging할 수 있습니다.");
    }

    /// <summary>후보 안에서 검증된 곡선 묶음을 최종 런타임 ID로 확정합니다.</summary>
    public bool CommitWorkingPathGeometry(
        WorkingCoordinateSystem coordinateSystem,
        IReadOnlyList<int> stagedSegmentIds,
        IReadOnlyList<int> finalSegmentIds,
        IReadOnlyList<int> stagedPolylineIds)
    {
        ValidateCoordinateSystem(coordinateSystem);
        ArgumentNullException.ThrowIfNull(stagedSegmentIds);
        ArgumentNullException.ThrowIfNull(finalSegmentIds);
        ArgumentNullException.ThrowIfNull(stagedPolylineIds);
        if (stagedPolylineIds.Count != 0)
            return false;

        return Bridge.CommitPreparedCandidateWorkingPathGeometry(
            _session.CandidateId,
            coordinateSystem.Id.Value,
            coordinateSystem.Revision.Value,
            stagedSegmentIds.ToArray(),
            finalSegmentIds.ToArray());
    }

    /// <summary>후보 문서에서 확정하지 못한 경로 staging만 폐기합니다.</summary>
    public void DiscardWorkingPathGeometry(
        WorkingCoordinateSystem coordinateSystem,
        IReadOnlyList<int> stagedSegmentIds,
        IReadOnlyList<int> stagedPolylineIds)
    {
        ValidateCoordinateSystem(coordinateSystem);
        ArgumentNullException.ThrowIfNull(stagedSegmentIds);
        ArgumentNullException.ThrowIfNull(stagedPolylineIds);

        int[] stagedIds = stagedSegmentIds.Concat(stagedPolylineIds).Distinct().ToArray();
        Bridge.DiscardPreparedCandidateWorkingPathGeometry(
            _session.CandidateId,
            coordinateSystem.Id.Value,
            coordinateSystem.Revision.Value,
            stagedIds);
    }

    /// <summary>요청한 좌표계가 이 어댑터가 소유한 후보 context인지 확인합니다.</summary>
    private void ValidateCoordinateSystem(WorkingCoordinateSystem coordinateSystem)
    {
        _session.ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(coordinateSystem);
        WorkingCoordinateSystem sessionCoordinateSystem = _session.CoordinateSystem;
        if (coordinateSystem.Id != sessionCoordinateSystem.Id ||
            coordinateSystem.Revision != sessionCoordinateSystem.Revision)
        {
            throw new ArgumentException("요청 좌표계가 후보 session의 context와 다릅니다.", nameof(coordinateSystem));
        }
    }
}
