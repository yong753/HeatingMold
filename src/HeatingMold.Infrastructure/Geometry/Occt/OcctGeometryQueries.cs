using System.Collections.ObjectModel;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Validation;

namespace HeatingMold.Infrastructure.Geometry.Occt;

/// <summary>작업 좌표계에서 OCCT가 반환한 표면 질의 상태입니다.</summary>
public enum OcctSurfaceQueryStatus
{
    /// <summary>전체 모델에서 첫 표면 교점을 찾았습니다.</summary>
    Hit,

    /// <summary>작업 평면 광선과 교차하는 표면이 없습니다.</summary>
    Miss,

    /// <summary>광선이 표면에 접해 깊이 방향을 판정할 수 없습니다.</summary>
    Tangent,

    /// <summary>입력 좌표계 또는 Face 번호가 유효하지 않습니다.</summary>
    InvalidInput,

    /// <summary>작업 좌표계가 현재 모델 revision에 속하지 않습니다.</summary>
    StaleContext,

    /// <summary>OCCT 교차 연산에서 기하 오류가 발생했습니다.</summary>
    GeometryFailure,

    /// <summary>Native 브리지가 이 버전에서 알 수 없는 상태를 반환했습니다.</summary>
    Unknown
}

/// <summary>OCCT 공유 Edge 기반 Face 인접 질의의 결과 상태입니다.</summary>
public enum OcctFaceAdjacencyStatus
{
    /// <summary>두 Face가 같은 Face이거나 실제 공유 Edge를 가집니다.</summary>
    Adjacent,

    /// <summary>현재 문서에서 두 Face 사이의 공유 Edge를 찾지 못했습니다.</summary>
    NotAdjacent,

    /// <summary>요청에 포함된 Native Face 번호가 현재 문서에 없습니다.</summary>
    InvalidFace,

    /// <summary>질의 context가 현재 모델 revision에 속하지 않습니다.</summary>
    StaleContext,

    /// <summary>OCCT 위상 검사에서 기하 오류가 발생했습니다.</summary>
    GeometryFailure,

    /// <summary>Native 브리지가 이 버전에서 알 수 없는 상태를 반환했습니다.</summary>
    Unknown
}

/// <summary>네이티브 표면 질의를 읽기 전용으로 호출하는 최소 포트입니다.</summary>
public interface IOcctWorkingSurfaceBridge
{
    /// <summary>전체 모델에서 작업 평면 점의 가장 가까운 표면 교점을 반환합니다.</summary>
    OcctSurfaceQueryResult QueryFirstHit(
        WorkingCoordinateSystemId coordinateSystemId,
        ModelRevision revision,
        Vec2 workingUv);

    /// <summary>현재 모델에서 두 Native Face가 실제 공유 Edge를 갖는지 확인합니다.</summary>
    OcctFaceAdjacencyStatus QueryFaceAdjacency(
        WorkingCoordinateSystemId coordinateSystemId,
        ModelRevision revision,
        int firstNativeFaceIndex,
        int secondNativeFaceIndex);

    /// <summary>선택 Face의 실제 공유 Edge 성분과 닫힌 경계를 반환합니다.</summary>
    OcctSelectedTopologyResult QuerySelectedTopology(
        WorkingCoordinateSystemId coordinateSystemId,
        ModelRevision revision,
        IReadOnlyList<int> selectedNativeFaceIndices,
        double deflectionMm);

    /// <summary>선택 Face와 외곽·홀 경계로 실제 유효 표면의 삼각 요소를 구합니다.</summary>
    OcctSurfaceElementQueryResult QuerySurfaceElements(
        WorkingCoordinateSystemId coordinateSystemId,
        ModelRevision revision,
        IReadOnlyList<int> selectedNativeFaceIndices,
        IReadOnlyList<OcctRegionLoop> effectiveBoundary,
        double deflectionMm,
        double angularToleranceRad);
}

/// <summary>준비 후보의 Native session에 범위를 한정해 first-hit을 조회합니다.</summary>
public interface IOcctPreparedCandidateFirstHitBridge
{
    /// <summary>후보 ID와 후보에서 만든 작업 context로 실제 전체 모델 first-hit을 조회합니다.</summary>
    OcctSurfaceQueryResult QueryPreparedCandidateFirstHit(
        long candidateId,
        WorkingCoordinateSystemId coordinateSystemId,
        ModelRevision revision,
        Vec2 workingUv);
}

/// <summary>OCCT first-hit 결과를 Core의 좌표와 Native Face 번호로 보관합니다.</summary>
public sealed record OcctSurfaceQueryResult
{
    /// <summary>네이티브 광선-표면 교차 질의 상태입니다.</summary>
    public OcctSurfaceQueryStatus Status { get; }

    /// <summary>교차한 현재 모델의 zero-based Face 번호입니다.</summary>
    public int? NativeFaceIndex { get; }

    /// <summary>first-hit 모델 좌표이며 miss나 실패이면 null입니다.</summary>
    public Vec3? ModelPoint { get; }

    /// <summary>first-hit 단위 법선이며 miss나 실패이면 null입니다.</summary>
    public Vec3? SurfaceNormal { get; }

    /// <summary>first-hit 원본 Face의 매개변수 좌표이며 실패이면 null입니다.</summary>
    public Vec2? FaceUv { get; }

    /// <summary>상태와 교점 자료의 일관성을 확인해 네이티브 결과를 만듭니다.</summary>
    public OcctSurfaceQueryResult(
        OcctSurfaceQueryStatus status,
        int? nativeFaceIndex = null,
        Vec3? modelPoint = null,
        Vec3? surfaceNormal = null,
        Vec2? faceUv = null)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));

        bool hasHitData = nativeFaceIndex.HasValue || modelPoint.HasValue || surfaceNormal.HasValue || faceUv.HasValue;
        if (status == OcctSurfaceQueryStatus.Hit)
        {
            if (nativeFaceIndex is < 0 || !modelPoint.HasValue || !surfaceNormal.HasValue || !faceUv.HasValue)
                throw new ArgumentException("성공한 OCCT 표면 질의에는 Face 번호, 점, 법선과 Face UV가 필요합니다.");
            if (Math.Abs(surfaceNormal.Value.Length - 1) > 1e-6)
                throw new ArgumentException("OCCT 표면 질의의 법선은 단위 벡터여야 합니다.", nameof(surfaceNormal));
        }
        else if (hasHitData)
        {
            throw new ArgumentException("성공하지 않은 OCCT 표면 질의는 교점 자료를 포함할 수 없습니다.");
        }

        Status = status;
        NativeFaceIndex = nativeFaceIndex;
        ModelPoint = modelPoint;
        SurfaceNormal = surfaceNormal;
        FaceUv = faceUv;
    }
}

/// <summary>OCCT 결과를 안정 Face ID와 공통 기하 질의 상태로 변환합니다.</summary>
public sealed class OcctGeometryQueries : IGeometryQueries
{
    private const string RuleVersion = "occt-geometry-query-v1";
    private const int MaximumClearancePairChecks = 10_000_000;
    private const int MaximumConnectionCurveCount = 10_000;

    private readonly IOcctWorkingSurfaceBridge _bridge;
    private readonly CurveDefinitionMapper _curveDefinitionMapper = new();
    private readonly WorkingCoordinateSystem _adjacencyCoordinateSystem;
    private readonly GeometryToleranceProfile _tolerances;
    private readonly IReadOnlyDictionary<FaceRefId, int> _nativeFaceIndices;
    private readonly IReadOnlyDictionary<int, FaceRefId> _faceReferencesByNativeIndex;

    /// <summary>네이티브 브리지, 인접 질의 좌표계와 현재 문서의 Face 대응을 방어 복사합니다.</summary>
    public OcctGeometryQueries(
        IOcctWorkingSurfaceBridge bridge,
        WorkingCoordinateSystem adjacencyCoordinateSystem,
        IReadOnlyDictionary<FaceRefId, int> nativeFaceIndices,
        GeometryToleranceProfile? tolerances = null)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _adjacencyCoordinateSystem = adjacencyCoordinateSystem ?? throw new ArgumentNullException(nameof(adjacencyCoordinateSystem));
        _tolerances = tolerances ?? GeometryToleranceProfile.Initial;
        ArgumentNullException.ThrowIfNull(nativeFaceIndices);

        Dictionary<FaceRefId, int> copiedMappings = new();
        foreach ((FaceRefId faceReferenceId, int nativeFaceIndex) in nativeFaceIndices)
        {
            if (faceReferenceId.IsEmpty || nativeFaceIndex < 0)
                throw new ArgumentException("Face 대응에는 안정 ID와 유효한 Native 번호가 필요합니다.", nameof(nativeFaceIndices));
            copiedMappings.Add(faceReferenceId, nativeFaceIndex);
        }
        if (copiedMappings.Values.Distinct().Count() != copiedMappings.Count)
            throw new ArgumentException("서로 다른 안정 Face가 같은 Native 번호를 가리킬 수 없습니다.", nameof(nativeFaceIndices));

        _nativeFaceIndices = new ReadOnlyDictionary<FaceRefId, int>(copiedMappings);
        _faceReferencesByNativeIndex = new ReadOnlyDictionary<int, FaceRefId>(
            copiedMappings.ToDictionary(mapping => mapping.Value, mapping => mapping.Key));
    }

    /// <summary>전체 모델의 first-hit을 확인하고 요청한 안정 Face 참조로 되돌립니다.</summary>
    public GeometryQueryResult<SurfaceHit> QueryFirstHit(
        WorkingCoordinateSystem coordinateSystem,
        Vec2 workingUv,
        FaceRefId selectedFaceReferenceId,
        CancellationToken cancellationToken = default) => QueryFirstHit(
            coordinateSystem,
            workingUv,
            [selectedFaceReferenceId],
            cancellationToken);

    /// <summary>선택 Face 집합의 실제 first-hit만 성공으로 반환하고 가려진 면을 거부합니다.</summary>
    public GeometryQueryResult<SurfaceHit> QueryFirstHit(
        WorkingCoordinateSystem coordinateSystem,
        Vec2 workingUv,
        IReadOnlyList<FaceRefId> selectedFaceReferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(coordinateSystem);
        ArgumentNullException.ThrowIfNull(selectedFaceReferences);

        FaceRefId[] selectedFaces = selectedFaceReferences.ToArray();
        if (selectedFaces.Length == 0 || selectedFaces.Any(face => face.IsEmpty) || selectedFaces.Distinct().Count() != selectedFaces.Length)
        {
            return Unresolved<SurfaceHit>(
                "InvalidSelectedFaceSet",
                "first-hit 질의에는 중복 없는 안정 Face 참조가 하나 이상 필요합니다.",
                workingUv);
        }

        FaceRefId[] missingFaces = selectedFaces
            .Where(face => !_nativeFaceIndices.ContainsKey(face))
            .ToArray();
        if (missingFaces.Length > 0)
        {
            return Unresolved<SurfaceHit>(
                "UnresolvedFaceReference",
                "요청한 안정 Face를 현재 OCCT 문서의 Native 번호로 대응하지 못했습니다.",
                workingUv,
                selectedFaces.Where(face => !_nativeFaceIndices.ContainsKey(face)).Select(face => face.Value));
        }

        if (cancellationToken.IsCancellationRequested)
            return Unresolved<SurfaceHit>("OperationCancelled", "first-hit 표면 질의가 취소되었습니다.", workingUv);

        OcctSurfaceQueryResult nativeResult;
        try
        {
            nativeResult = _bridge.QueryFirstHit(coordinateSystem.Id, coordinateSystem.Revision, workingUv);
        }
        catch (OperationCanceledException)
        {
            return Unresolved<SurfaceHit>("OperationCancelled", "first-hit 표면 질의가 취소되었습니다.", workingUv);
        }
        catch (Exception exception)
        {
            return Unresolved<SurfaceHit>(
                "OcctFirstHitFailed",
                $"OCCT first-hit 질의가 실패했습니다: {exception.Message}",
                workingUv);
        }

        if (cancellationToken.IsCancellationRequested)
            return Unresolved<SurfaceHit>("OperationCancelled", "first-hit 표면 질의가 취소되었습니다.", workingUv);
        if (nativeResult is null)
            return Unresolved<SurfaceHit>("OcctFirstHitFailed", "OCCT first-hit 질의가 결과를 반환하지 않았습니다.", workingUv);

        switch (nativeResult.Status)
        {
            case OcctSurfaceQueryStatus.Miss:
                return GeometryQueryResult<SurfaceHit>.Miss();
            case OcctSurfaceQueryStatus.Tangent:
                return new GeometryQueryResult<SurfaceHit>(
                    GeometryQueryStatus.Tangent,
                    null,
                    [CreateIssue("SurfaceMappingTangentHit", "광선이 표면에 접해 안정적인 first-hit 깊이를 정할 수 없습니다.", workingUv)]);
            case OcctSurfaceQueryStatus.StaleContext:
                return new GeometryQueryResult<SurfaceHit>(
                    GeometryQueryStatus.StaleGeometryContext,
                    null,
                    [CreateIssue("StaleGeometryContext", "작업 좌표계가 현재 모델 revision에 속하지 않습니다.", workingUv)]);
            case OcctSurfaceQueryStatus.InvalidInput:
                return Unresolved<SurfaceHit>("InvalidNativeSurfaceQuery", "OCCT가 좌표계 또는 표면 질의 입력을 거부했습니다.", workingUv);
            case OcctSurfaceQueryStatus.GeometryFailure:
                return new GeometryQueryResult<SurfaceHit>(
                    GeometryQueryStatus.Failure,
                    null,
                    [CreateIssue("OcctSurfaceIntersectionFailed", "OCCT 표면 교차 계산을 완료하지 못했습니다.", workingUv)]);
            case OcctSurfaceQueryStatus.Hit:
                return MapHit(nativeResult, selectedFaces, workingUv);
            default:
                return Unresolved<SurfaceHit>("UnknownNativeSurfaceQueryStatus", "OCCT가 알 수 없는 표면 질의 상태를 반환했습니다.", workingUv);
        }
    }

    /// <summary>현재 문서의 공유 Edge 검사 상태를 공통 인접 질의로 보존합니다.</summary>
    public GeometryQueryResult<bool> AreAdjacent(
        FaceRefId first,
        FaceRefId second,
        CancellationToken cancellationToken = default)
    {
        if (first.IsEmpty || second.IsEmpty)
            return Unresolved<bool>("InvalidFaceAdjacencyRequest", "인접 질의에는 유효한 안정 Face ID 두 개가 필요합니다.");
        if (first == second)
            return GeometryQueryResult<bool>.Succeeded(true);
        if (!_nativeFaceIndices.TryGetValue(first, out int firstNativeIndex) ||
            !_nativeFaceIndices.TryGetValue(second, out int secondNativeIndex))
        {
            return Unresolved<bool>("UnresolvedFaceReference", "인접 질의 대상 Face를 현재 OCCT 문서에 대응하지 못했습니다.", entityIds: [first.Value, second.Value]);
        }
        if (cancellationToken.IsCancellationRequested)
            return Unresolved<bool>("OperationCancelled", "Face 인접 질의가 취소되었습니다.");

        try
        {
            OcctFaceAdjacencyStatus nativeStatus = _bridge.QueryFaceAdjacency(
                _adjacencyCoordinateSystem.Id,
                _adjacencyCoordinateSystem.Revision,
                firstNativeIndex,
                secondNativeIndex);
            if (cancellationToken.IsCancellationRequested)
                return Unresolved<bool>("OperationCancelled", "Face 인접 질의가 취소되었습니다.");

            return nativeStatus switch
            {
                OcctFaceAdjacencyStatus.Adjacent => GeometryQueryResult<bool>.Succeeded(true),
                OcctFaceAdjacencyStatus.NotAdjacent => GeometryQueryResult<bool>.Succeeded(false),
                OcctFaceAdjacencyStatus.InvalidFace => Unresolved<bool>(
                    "InvalidNativeFaceAdjacencyRequest",
                    "OCCT가 현재 문서에 없는 Face 번호로 인접 질의를 요청받았습니다.",
                    entityIds: [first.Value, second.Value]),
                OcctFaceAdjacencyStatus.StaleContext => new GeometryQueryResult<bool>(
                    GeometryQueryStatus.StaleGeometryContext,
                    default,
                    [CreateIssue("StaleGeometryContext", "Face 인접 질의 context가 현재 모델 revision에 속하지 않습니다.", entityIds: [first.Value, second.Value])]),
                OcctFaceAdjacencyStatus.GeometryFailure => new GeometryQueryResult<bool>(
                    GeometryQueryStatus.Failure,
                    default,
                    [CreateIssue("OcctFaceAdjacencyFailed", "OCCT 공유 Edge 검사에서 기하 오류가 발생했습니다.", entityIds: [first.Value, second.Value])]),
                _ => Unresolved<bool>("UnknownFaceAdjacencyStatus", "OCCT가 알 수 없는 Face 인접 상태를 반환했습니다.", entityIds: [first.Value, second.Value])
            };
        }
        catch (OperationCanceledException)
        {
            return Unresolved<bool>("OperationCancelled", "Face 인접 질의가 취소되었습니다.");
        }
        catch (Exception exception)
        {
            return Unresolved<bool>("OcctFaceAdjacencyFailed", $"OCCT Face 인접 질의가 실패했습니다: {exception.Message}");
        }
    }

    /// <summary>선택 Face의 공유 Edge 연결 성분과 실제 경계 루프를 안정 Face 참조로 반환합니다.</summary>
    public GeometryQueryResult<SelectedTopology> GetSelectedTopology(
        IReadOnlyList<FaceRefId> selectedFaceReferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedFaceReferences);
        if (cancellationToken.IsCancellationRequested)
            return Unresolved<SelectedTopology>("OperationCancelled", "선택 topology 질의가 취소되었습니다.");

        FaceRefId[] selectedFaces = selectedFaceReferences.ToArray();
        if (selectedFaces.Length == 0 || selectedFaces.Any(face => face.IsEmpty) ||
            selectedFaces.Distinct().Count() != selectedFaces.Length)
        {
            return Unresolved<SelectedTopology>(
                "InvalidSelectedFaceSet",
                "topology 질의에는 중복 없는 안정 Face 참조가 하나 이상 필요합니다.");
        }

        FaceRefId[] missingFaces = selectedFaces
            .Where(face => !_nativeFaceIndices.ContainsKey(face))
            .ToArray();
        if (missingFaces.Length > 0)
        {
            return Unresolved<SelectedTopology>(
                "UnresolvedFaceReference",
                "선택 topology의 안정 Face를 현재 OCCT 문서의 Native 번호로 대응하지 못했습니다.",
                entityIds: missingFaces.Select(face => face.Value));
        }

        if (cancellationToken.IsCancellationRequested)
            return Unresolved<SelectedTopology>("OperationCancelled", "선택 topology 질의가 취소되었습니다.");

        OcctSelectedTopologyResult? nativeResult;
        try
        {
            int[] nativeFaceIndices = selectedFaces.Select(face => _nativeFaceIndices[face]).ToArray();
            nativeResult = _bridge.QuerySelectedTopology(
                _adjacencyCoordinateSystem.Id,
                _adjacencyCoordinateSystem.Revision,
                nativeFaceIndices,
                _tolerances.MeshDeflectionMm);
        }
        catch (OperationCanceledException)
        {
            return Unresolved<SelectedTopology>("OperationCancelled", "선택 topology 질의가 취소되었습니다.");
        }
        catch (Exception exception)
        {
            return Unresolved<SelectedTopology>(
                "OcctSelectedTopologyFailed",
                $"OCCT 선택 topology 질의가 실패했습니다: {exception.Message}",
                entityIds: selectedFaces.Select(face => face.Value));
        }

        if (cancellationToken.IsCancellationRequested)
            return Unresolved<SelectedTopology>("OperationCancelled", "선택 topology 질의가 취소되었습니다.");
        if (nativeResult is null)
        {
            return new GeometryQueryResult<SelectedTopology>(
                GeometryQueryStatus.Failure,
                default,
                [CreateIssue(
                    "OcctSelectedTopologyFailed",
                    "OCCT 선택 topology 질의가 결과를 반환하지 않았습니다.",
                    entityIds: selectedFaces.Select(face => face.Value))]);
        }

        return OcctSelectedTopologyMapper.Map(
            nativeResult,
            selectedFaces,
            _faceReferencesByNativeIndex,
            _adjacencyCoordinateSystem.Projection,
            _adjacencyCoordinateSystem.Revision);
    }

    /// <summary>Native 곡선 거리 질의를 우선 사용하고, 대역에는 정확한 선분 계산을 적용합니다.</summary>
    public GeometryQueryResult<ClearanceMeasurement> MeasureClearance(
        IReadOnlyList<CurveGeometrySnapshot> curves,
        LengthMm wireDiameterMm,
        LengthMm requiredClearGapMm,
        GeometryToleranceProfile tolerances,
        CancellationToken cancellationToken = default) => MeasureClearance(
            curves,
            wireDiameterMm,
            requiredClearGapMm,
            tolerances,
            Array.Empty<CurveSegmentAdjacency>(),
            cancellationToken);

    /// <summary>안정 구간 ID의 연결 관계를 Native 또는 정확 선분 계산기에 전달합니다.</summary>
    public GeometryQueryResult<ClearanceMeasurement> MeasureClearance(
        IReadOnlyList<CurveGeometrySnapshot> curves,
        LengthMm wireDiameterMm,
        LengthMm requiredClearGapMm,
        GeometryToleranceProfile tolerances,
        IReadOnlyCollection<CurveSegmentAdjacency> localAdjacencies,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(curves);
        ArgumentNullException.ThrowIfNull(tolerances);
        ArgumentNullException.ThrowIfNull(localAdjacencies);
        if (cancellationToken.IsCancellationRequested)
            return Unresolved<ClearanceMeasurement>("OperationCancelled", "곡선 간격 질의가 취소되었습니다.");
        if (wireDiameterMm.Value <= 0 || requiredClearGapMm.Value < 0)
            return Unresolved<ClearanceMeasurement>("InvalidClearanceInput", "도체 직경은 양수이고 요구 간격은 0 이상이어야 합니다.");
        if (curves.Count == 0)
            return Unresolved<ClearanceMeasurement>("NoClearanceCurves", "간격을 측정할 곡선이 없습니다.");
        if (curves.Any(curve => curve is null))
            return Unresolved<ClearanceMeasurement>("InvalidClearanceInput", "간격 질의에 null 곡선이 포함되어 있습니다.");

        try
        {
            if (!TryBuildLocalCurvePairs(curves, localAdjacencies, out var localCurvePairs, out string? adjacencyError))
                return Unresolved<ClearanceMeasurement>("InvalidClearanceAdjacency", adjacencyError!);

            foreach ((int firstCurveIndex, int secondCurveIndex) in localCurvePairs)
            {
                CurveGeometrySnapshot firstCurve = curves[firstCurveIndex];
                CurveGeometrySnapshot secondCurve = curves[secondCurveIndex];
                if ((firstCurve.End - secondCurve.Start).Length <= tolerances.ConnectionToleranceMm)
                    continue;

                return Unresolved<ClearanceMeasurement>(
                    "DeclaredCurveEndpointsDoNotMeet",
                    "선언된 연속 관계의 앞 구간 끝점과 뒤 구간 시작점이 연결 공차 안에서 만나지 않습니다.",
                    entityIds:
                    [
                        firstCurve.HeatingPathSegmentId?.Value ?? firstCurve.ConnectionPathSegmentId!.Value.Value,
                        secondCurve.HeatingPathSegmentId?.Value ?? secondCurve.ConnectionPathSegmentId!.Value.Value
                    ]);
            }

            if (_bridge is IOcctCurveClearanceBridge nativeClearanceBridge)
            {
                CurveDefinitionBridgeDto[] definitions = curves
                    .Select(curve => _curveDefinitionMapper.ToBridgeDto(curve.Definition))
                    .ToArray();
                OcctCurveClearanceQueryResult nativeResult = nativeClearanceBridge.MeasureCurveClearance(
                    _adjacencyCoordinateSystem.Id,
                    _adjacencyCoordinateSystem.Revision,
                    definitions,
                    localCurvePairs,
                    wireDiameterMm,
                    tolerances.ConnectionToleranceMm,
                    MaximumClearancePairChecks);

                if (cancellationToken.IsCancellationRequested)
                    return Unresolved<ClearanceMeasurement>("OperationCancelled", "곡선 간격 질의가 취소되었습니다.");

                if (nativeResult.Status == OcctCurveClearanceStatus.Unresolved &&
                    curves.Any(curve => curve.Definition is BSplineCurveDefinition))
                {
                    // Native 미확정 결과만 관리형 B-spline 지원 범위에서 재시도합니다.
                    GeometryQueryResult<ClearanceMeasurement> fallbackResult =
                        BSplineCurveClearanceCalculator.Measure(
                            curves,
                            wireDiameterMm,
                            requiredClearGapMm,
                            localCurvePairs,
                            tolerances,
                            cancellationToken);
                    if (fallbackResult.Status == GeometryQueryStatus.Success)
                        return fallbackResult;
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return Unresolved<ClearanceMeasurement>(
                            "OperationCancelled",
                            "곡선 간격 질의가 취소되었습니다.");
                    }
                }

                return MapNativeClearanceResult(nativeResult, curves);
            }

            return curves.Any(curve => curve.Definition is BSplineCurveDefinition)
                ? BSplineCurveClearanceCalculator.Measure(
                    curves,
                    wireDiameterMm,
                    requiredClearGapMm,
                    localCurvePairs,
                    tolerances,
                    cancellationToken)
                : curves.Any(curve => curve.Definition is ArcCurveDefinition)
                    ? ArcCurveClearanceCalculator.Measure(
                    curves,
                    wireDiameterMm,
                    requiredClearGapMm,
                    localCurvePairs,
                    tolerances,
                    cancellationToken)
                    : LinearCurveClearanceCalculator.Measure(
                    curves,
                    wireDiameterMm,
                    requiredClearGapMm,
                    localCurvePairs,
                    tolerances,
                    cancellationToken);
        }
        catch (Exception exception) when (
            exception is ArgumentException or ArithmeticException or InvalidOperationException)
        {
            return Unresolved<ClearanceMeasurement>("ClearanceNumericallyUnresolved", exception.Message);
        }
    }

    /// <summary>안정 구간 연결 관계를 현재 곡선 배열의 정렬된 인덱스 쌍으로 바꿉니다.</summary>
    private static bool TryBuildLocalCurvePairs(
        IReadOnlyList<CurveGeometrySnapshot> curves,
        IReadOnlyCollection<CurveSegmentAdjacency> localAdjacencies,
        out IReadOnlyList<(int FirstCurveIndex, int SecondCurveIndex)> localCurvePairs,
        out string? error)
    {
        var indexBySegmentId = new Dictionary<HeatingPathSegmentId, int>();
        for (int index = 0; index < curves.Count; index++)
        {
            if (curves[index].HeatingPathSegmentId is not { } segmentId)
                continue;
            if (!indexBySegmentId.TryAdd(segmentId, index))
            {
                localCurvePairs = Array.Empty<(int, int)>();
                error = "간격 곡선 목록에 발열 구간 ID가 중복되어 연결 관계를 매길 수 없습니다.";
                return false;
            }
        }

        var pairs = new HashSet<(int FirstCurveIndex, int SecondCurveIndex)>();
        foreach (CurveSegmentAdjacency adjacency in localAdjacencies)
        {
            if (adjacency is null ||
                !indexBySegmentId.TryGetValue(adjacency.FirstSegmentId, out int firstIndex) ||
                !indexBySegmentId.TryGetValue(adjacency.SecondSegmentId, out int secondIndex))
            {
                localCurvePairs = Array.Empty<(int, int)>();
                error = "연속 구간 관계가 현재 간격 곡선 목록의 발열 구간 ID에 대응하지 않습니다.";
                return false;
            }

            pairs.Add((firstIndex, secondIndex));
        }

        localCurvePairs = pairs.OrderBy(pair => pair.FirstCurveIndex)
            .ThenBy(pair => pair.SecondCurveIndex)
            .ToArray();
        error = null;
        return true;
    }

    /// <summary>Native 거리 결과의 상태와 진단을 공통 geometry query 계약으로 옮깁니다.</summary>
    private static GeometryQueryResult<ClearanceMeasurement> MapNativeClearanceResult(
        OcctCurveClearanceQueryResult nativeResult,
        IReadOnlyList<CurveGeometrySnapshot> curves)
    {
        string evidence = nativeResult.Diagnostic ?? "OCCT 곡선 간격 질의가 완전한 결과를 반환하지 않았습니다.";
        Guid[] entityIds = curves
            .Where(curve => curve.HeatingPathSegmentId.HasValue)
            .Select(curve => curve.HeatingPathSegmentId!.Value.Value)
            .ToArray();

        return nativeResult.Status switch
        {
            OcctCurveClearanceStatus.Success => GeometryQueryResult<ClearanceMeasurement>.Succeeded(
                nativeResult.Measurement!),
            OcctCurveClearanceStatus.StaleContext => new GeometryQueryResult<ClearanceMeasurement>(
                GeometryQueryStatus.StaleGeometryContext,
                default,
                [CreateIssue("StaleGeometryContext", evidence, entityIds: entityIds)]),
            OcctCurveClearanceStatus.InvalidInput or OcctCurveClearanceStatus.CandidateNotFound or
                OcctCurveClearanceStatus.GeometryFailure => new GeometryQueryResult<ClearanceMeasurement>(
                    GeometryQueryStatus.Failure,
                    default,
                    [CreateIssue("OcctClearanceFailed", evidence, entityIds: entityIds)]),
            OcctCurveClearanceStatus.PairBudgetExceeded => Unresolved<ClearanceMeasurement>(
                "ClearancePairBudgetExceeded",
                evidence,
                entityIds: entityIds),
            _ => Unresolved<ClearanceMeasurement>(
                "OcctClearanceUnresolved",
                evidence,
                entityIds: entityIds)
        };
    }

    /// <summary>first-hit 면과 선형 최종 곡선 끝의 도체 바깥면 사이 축방향 깊이를 측정합니다.</summary>
    public GeometryQueryResult<DepthMeasurement> MeasureDepth(
        CurveGeometrySnapshot curve,
        LengthMm wireDiameterMm,
        SurfaceAnchor anchor,
        WorkingCoordinateSystem coordinateSystem,
        LengthMm requiredInsertionDepthMm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(curve);
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(coordinateSystem);
        if (cancellationToken.IsCancellationRequested)
            return Unresolved<DepthMeasurement>("OperationCancelled", "삽입 깊이 질의가 취소되었습니다.");

        GeometryQueryResult<SurfaceHit> surfaceResult = QueryFirstHit(
            coordinateSystem,
            anchor.WorkingUv,
            anchor.FaceReferenceId,
            cancellationToken);
        if (!surfaceResult.HasValue)
        {
            return new GeometryQueryResult<DepthMeasurement>(
                surfaceResult.Status,
                default,
                surfaceResult.Issues);
        }
        if (cancellationToken.IsCancellationRequested)
            return Unresolved<DepthMeasurement>("OperationCancelled", "삽입 깊이 질의가 취소되었습니다.");

        try
        {
            return CurveEndpointDepthCalculator.Measure(
                curve,
                wireDiameterMm,
                anchor,
                coordinateSystem,
                requiredInsertionDepthMm,
                surfaceResult.Value!,
                _tolerances);
        }
        catch (Exception exception) when (
            exception is ArgumentException or ArithmeticException or InvalidOperationException)
        {
            return Unresolved<DepthMeasurement>("DepthNumericallyUnresolved", exception.Message);
        }
    }

    /// <summary>Native가 계산한 깊이 중심선을 공통 질의 결과로 변환합니다.</summary>
    public GeometryQueryResult<InsertionDepthAdjustedPath> AdjustInsertionDepth(
        IReadOnlyList<Vec2> surfacePathWorkingSamples,
        IReadOnlyList<FaceRefId> selectedSurfaceFaces,
        SurfaceAnchor anchor,
        LengthMm wireDiameterMm,
        LengthMm requiredInsertionDepthMm,
        WorkingCoordinateSystem coordinateSystem,
        GeometryToleranceProfile tolerances,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(surfacePathWorkingSamples);
        ArgumentNullException.ThrowIfNull(selectedSurfaceFaces);
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(coordinateSystem);
        ArgumentNullException.ThrowIfNull(tolerances);

        FaceRefId[] entityIds = selectedSurfaceFaces.ToArray();
        if (cancellationToken.IsCancellationRequested)
            return Unresolved<InsertionDepthAdjustedPath>("OperationCancelled", "Native 깊이 경로 보정이 취소되었습니다.");
        if (_bridge is not IOcctCurveDepthAdjustmentBridge nativeBridge)
        {
            return Unresolved<InsertionDepthAdjustedPath>(
                "InsertionDepthAdjustmentUnsupported",
                "현재 Native 브리지는 최종 삽입 깊이 경로 보정을 지원하지 않습니다.",
                entityIds: entityIds.Select(face => face.Value));
        }
        if (wireDiameterMm.Value <= 0 || requiredInsertionDepthMm.Value < 0 ||
            !double.IsFinite(wireDiameterMm.Value) || !double.IsFinite(requiredInsertionDepthMm.Value))
        {
            return Unresolved<InsertionDepthAdjustedPath>(
                "InvalidDepthAdjustmentInput",
                "도체 직경은 양수이고 요구 삽입 깊이는 0 이상이어야 합니다.",
                entityIds: entityIds.Select(face => face.Value));
        }
        if (selectedSurfaceFaces.Count == 0 || selectedSurfaceFaces.Any(face => face.IsEmpty) ||
            selectedSurfaceFaces.Distinct().Count() != selectedSurfaceFaces.Count ||
            !selectedSurfaceFaces.Contains(anchor.FaceReferenceId))
        {
            return Unresolved<InsertionDepthAdjustedPath>(
                "InvalidDepthSurfaceFaces",
                "깊이 보정 Face 집합은 중복 없이 유효해야 하며 앵커 Face를 포함해야 합니다.",
                entityIds: entityIds.Select(face => face.Value));
        }
        if (surfacePathWorkingSamples.Count < 2 || surfacePathWorkingSamples.Any(point =>
                !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
        {
            return Unresolved<InsertionDepthAdjustedPath>(
                "InvalidDepthAdjustmentPath",
                "깊이 보정 경로에는 유한한 작업 평면 표본 두 개 이상이 필요합니다.",
                entityIds: entityIds.Select(face => face.Value));
        }

        FaceRefId[] missingFaces = selectedSurfaceFaces
            .Where(face => !_nativeFaceIndices.ContainsKey(face))
            .ToArray();
        if (missingFaces.Length > 0)
        {
            return Unresolved<InsertionDepthAdjustedPath>(
                "UnresolvedFaceReference",
                "깊이 보정 Face를 현재 OCCT 문서 번호로 대응하지 못했습니다.",
                entityIds: missingFaces.Select(face => face.Value));
        }

        OcctCurveDepthAdjustmentQueryResult nativeResult;
        try
        {
            var request = new OcctCurveDepthAdjustmentRequest(
                coordinateSystem.Id,
                coordinateSystem.Revision,
                surfacePathWorkingSamples,
                selectedSurfaceFaces.Select(face => _nativeFaceIndices[face]),
                _nativeFaceIndices[anchor.FaceReferenceId],
                anchor.WorkingUv,
                anchor.ModelPoint,
                wireDiameterMm,
                requiredInsertionDepthMm,
                tolerances.MaximumContinuousSampleSpacingMm,
                tolerances.MeshAngularToleranceRad,
                tolerances.MappingChordDeviationMm,
                tolerances.ConnectionToleranceMm,
                tolerances.MappingMaximumProbes);
            nativeResult = nativeBridge.AdjustInsertionDepth(request);
        }
        catch (OperationCanceledException)
        {
            return Unresolved<InsertionDepthAdjustedPath>("OperationCancelled", "Native 깊이 경로 보정이 취소되었습니다.");
        }
        catch (Exception exception)
        {
            return Unresolved<InsertionDepthAdjustedPath>(
                "OcctDepthAdjustmentFailed",
                $"Native 깊이 경로 보정에 실패했습니다: {exception.Message}",
                entityIds: entityIds.Select(face => face.Value));
        }

        if (cancellationToken.IsCancellationRequested)
            return Unresolved<InsertionDepthAdjustedPath>("OperationCancelled", "Native 깊이 경로 보정이 취소되었습니다.");
        if (nativeResult is null)
        {
            return new GeometryQueryResult<InsertionDepthAdjustedPath>(
                GeometryQueryStatus.Failure,
                default,
                [CreateIssue(
                    "OcctDepthAdjustmentFailed",
                    "Native 깊이 경로 보정이 결과를 반환하지 않았습니다.",
                    entityIds: entityIds.Select(face => face.Value))]);
        }

        string evidence = nativeResult.Diagnostic ?? "Native 깊이 경로 보정이 완료되지 않았습니다.";
        if (nativeResult.Status != OcctCurveDepthEnvelopeStatus.Success)
        {
            return nativeResult.Status switch
            {
                OcctCurveDepthEnvelopeStatus.Miss => GeometryQueryResult<InsertionDepthAdjustedPath>.Miss(),
                OcctCurveDepthEnvelopeStatus.Tangent => new GeometryQueryResult<InsertionDepthAdjustedPath>(
                    GeometryQueryStatus.Tangent,
                    default,
                    [CreateIssue("DepthSurfaceTangent", evidence, entityIds: entityIds.Select(face => face.Value))]),
                OcctCurveDepthEnvelopeStatus.StaleContext => new GeometryQueryResult<InsertionDepthAdjustedPath>(
                    GeometryQueryStatus.StaleGeometryContext,
                    default,
                    [CreateIssue("StaleGeometryContext", evidence, entityIds: entityIds.Select(face => face.Value))]),
                OcctCurveDepthEnvelopeStatus.GeometryFailure or OcctCurveDepthEnvelopeStatus.CandidateNotFound =>
                    new GeometryQueryResult<InsertionDepthAdjustedPath>(
                        GeometryQueryStatus.Failure,
                        default,
                        [CreateIssue("OcctDepthAdjustmentFailed", evidence, entityIds: entityIds.Select(face => face.Value))]),
                _ => Unresolved<InsertionDepthAdjustedPath>(
                    "InsertionDepthAdjustmentUnresolved",
                    evidence,
                    entityIds: entityIds.Select(face => face.Value))
            };
        }

        InsertionDepthAdjustedPath? adjustment = nativeResult.Adjustment;
        if (adjustment is null || adjustment.Curve.Points.Count < surfacePathWorkingSamples.Count ||
            adjustment.ProbeCount > tolerances.MappingMaximumProbes ||
            adjustment.MaximumDepthResidualMm.Value > Math.Max(1e-8, tolerances.ConnectionToleranceMm * 0.1))
        {
            return Unresolved<InsertionDepthAdjustedPath>(
                "InvalidNativeDepthAdjustment",
                "Native 깊이 경로가 입력 표본·잔차 허용오차·질의 한도 조건을 만족하지 않습니다.",
                entityIds: entityIds.Select(face => face.Value));
        }

        CurveGeometrySnapshot verificationCurve;
        try
        {
            verificationCurve = CreateDepthAdjustmentSnapshot(adjustment, tolerances);
        }
        catch (Exception exception) when (exception is ArgumentException or ArithmeticException or InvalidOperationException)
        {
            return Unresolved<InsertionDepthAdjustedPath>(
                "InvalidDepthAdjustmentCurve",
                $"Native 깊이 보정 결과를 검증용 곡선으로 만들 수 없습니다: {exception.Message}",
                entityIds: entityIds.Select(face => face.Value));
        }

        GeometryQueryResult<DepthMeasurement> depthVerification = MeasureDepth(
            verificationCurve,
            wireDiameterMm,
            anchor,
            coordinateSystem,
            requiredInsertionDepthMm,
            selectedSurfaceFaces,
            cancellationToken);
        if (!depthVerification.HasValue)
        {
            return new GeometryQueryResult<InsertionDepthAdjustedPath>(
                depthVerification.Status,
                default,
                depthVerification.Issues);
        }

        DepthMeasurement measuredDepth = depthVerification.Value!;
        double maximumDepthDifference = Math.Max(
            Math.Abs(measuredDepth.MinimumMeasuredInsertionDepthMm.Value - requiredInsertionDepthMm.Value),
            Math.Abs(measuredDepth.MaximumMeasuredInsertionDepthMm.Value - requiredInsertionDepthMm.Value));
        double allowedDepthDifference = measuredDepth.ErrorBoundMm.Value + tolerances.ConnectionToleranceMm;
        if (!measuredDepth.IsResolved || !measuredDepth.IsComplete || !measuredDepth.IsCurveEnvelope ||
            Math.Abs(measuredDepth.RequiredInsertionDepthMm.Value - requiredInsertionDepthMm.Value) >
                tolerances.ConnectionToleranceMm ||
            !double.IsFinite(maximumDepthDifference) || !double.IsFinite(allowedDepthDifference) ||
            allowedDepthDifference < 0 || maximumDepthDifference > allowedDepthDifference)
        {
            return Unresolved<InsertionDepthAdjustedPath>(
                "DepthAdjustmentEnvelopeMismatch",
                "Native 보정 중심선의 독립 깊이 envelope가 요구 깊이를 허용오차 안에서 만족하지 않습니다.",
                entityIds: entityIds.Select(face => face.Value));
        }

        return GeometryQueryResult<InsertionDepthAdjustedPath>.Succeeded(adjustment);
    }

    /// <summary>Native 중심선 표본을 전체 깊이 envelope 질의에 사용할 스냅샷으로 만듭니다.</summary>
    private static CurveGeometrySnapshot CreateDepthAdjustmentSnapshot(
        InsertionDepthAdjustedPath adjustment,
        GeometryToleranceProfile tolerances)
    {
        Vec3[] points = adjustment.Curve.Points.ToArray();
        double[] sampleLengthsMm = points
            .Zip(points.Skip(1), (first, second) => (second - first).Length)
            .ToArray();
        double maximumSampleSpacingMm = sampleLengthsMm.DefaultIfEmpty(0).Max();
        double lengthMm = sampleLengthsMm.Sum();

        return new CurveGeometrySnapshot(
            HeatingPathSegmentId.New(),
            null,
            adjustment.Curve,
            new LengthMm(lengthMm),
            points[0],
            points[^1],
            points,
            new CurveApproximationInfo(
                tolerances.MeshDeflectionMm,
                tolerances.MeshAngularToleranceRad,
                tolerances.MappingChordDeviationMm,
                maximumSampleSpacingMm,
                adjustment.ProbeCount,
                isComplete: true,
                adjustment.AlgorithmVersion));
    }

    /// <summary>선택 Face 집합을 따라 최종 곡선의 최소·최대 삽입 깊이를 측정합니다.</summary>
    public GeometryQueryResult<DepthMeasurement> MeasureDepth(
        CurveGeometrySnapshot curve,
        LengthMm wireDiameterMm,
        SurfaceAnchor anchor,
        WorkingCoordinateSystem coordinateSystem,
        LengthMm requiredInsertionDepthMm,
        IReadOnlyList<FaceRefId> selectedSurfaceFaces,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(curve);
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(coordinateSystem);
        ArgumentNullException.ThrowIfNull(selectedSurfaceFaces);
        if (cancellationToken.IsCancellationRequested)
            return Unresolved<DepthMeasurement>("OperationCancelled", "삽입 깊이 envelope 질의가 취소되었습니다.");

        if (_bridge is IOcctCurveDepthBridge nativeDepthBridge)
        {
            return MeasureNativeDepthEnvelope(
                nativeDepthBridge,
                curve,
                wireDiameterMm,
                anchor,
                coordinateSystem,
                requiredInsertionDepthMm,
                selectedSurfaceFaces,
                cancellationToken);
        }

        try
        {
            return CurveDepthEnvelopeCalculator.Measure(
                curve,
                wireDiameterMm,
                anchor,
                coordinateSystem,
                requiredInsertionDepthMm,
                selectedSurfaceFaces,
                _tolerances,
                QueryFirstHit,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is ArgumentException or ArithmeticException or InvalidOperationException or OverflowException)
        {
            return Unresolved<DepthMeasurement>("DepthEnvelopeNumericallyUnresolved", exception.Message);
        }
    }

    /// <summary>Native 깊이 결과를 공통 query 상태로 바꾸고 안정 Face 참조를 검사합니다.</summary>
    private GeometryQueryResult<DepthMeasurement> MeasureNativeDepthEnvelope(
        IOcctCurveDepthBridge nativeBridge,
        CurveGeometrySnapshot curve,
        LengthMm wireDiameterMm,
        SurfaceAnchor anchor,
        WorkingCoordinateSystem coordinateSystem,
        LengthMm requiredInsertionDepthMm,
        IReadOnlyList<FaceRefId> selectedSurfaceFaces,
        CancellationToken cancellationToken)
    {
        Guid[] entityIds = curve.HeatingPathSegmentId is { } heatingSegment
            ? [heatingSegment.Value]
            : [curve.ConnectionPathSegmentId!.Value.Value];
        if (wireDiameterMm.Value <= 0 || requiredInsertionDepthMm.Value < 0)
            return Unresolved<DepthMeasurement>("InvalidDepthInput", "도체 직경은 양수이고 요구 삽입 깊이는 0 이상이어야 합니다.", entityIds: entityIds);
        if (selectedSurfaceFaces.Count == 0 || selectedSurfaceFaces.Any(face => face.IsEmpty) ||
            selectedSurfaceFaces.Distinct().Count() != selectedSurfaceFaces.Count ||
            !selectedSurfaceFaces.Contains(anchor.FaceReferenceId))
        {
            return Unresolved<DepthMeasurement>(
                "InvalidDepthSurfaceFaces",
                "깊이 측정 Face 집합은 중복 없이 유효해야 하며 앵커 Face를 포함해야 합니다.",
                entityIds: entityIds);
        }
        if (!curve.Approximation.IsComplete || curve.Samples.Count < 2 ||
            !double.IsFinite(curve.Approximation.MaximumSampleSpacingMm) ||
            curve.Approximation.MaximumSampleSpacingMm <= 0 ||
            !double.IsFinite(curve.Approximation.RequestedAngularToleranceRad) ||
            curve.Approximation.RequestedAngularToleranceRad <= 0 ||
            !double.IsFinite(curve.Approximation.MaximumMeasuredDeviationMm) ||
            curve.Approximation.MaximumMeasuredDeviationMm < 0)
        {
            return Unresolved<DepthMeasurement>(
                "MissingDepthApproximationEvidence",
                "Native 깊이 측정에 필요한 완료된 곡선 표본과 근사 근거가 없습니다.",
                entityIds: entityIds);
        }
        if (cancellationToken.IsCancellationRequested)
            return Unresolved<DepthMeasurement>("OperationCancelled", "삽입 깊이 envelope 질의가 취소되었습니다.", entityIds: entityIds);

        FaceRefId[] missingFaces = selectedSurfaceFaces
            .Where(face => !_nativeFaceIndices.ContainsKey(face))
            .ToArray();
        if (missingFaces.Length > 0)
        {
            return Unresolved<DepthMeasurement>(
                "UnresolvedFaceReference",
                "깊이 측정 대상 Face를 현재 OCCT 문서 번호로 대응하지 못했습니다.",
                entityIds: missingFaces.Select(face => face.Value).Concat(entityIds));
        }

        var request = new OcctCurveDepthEnvelopeRequest(
            coordinateSystem.Id,
            coordinateSystem.Revision,
            curve.Samples,
            selectedSurfaceFaces.Select(face => _nativeFaceIndices[face]),
            _nativeFaceIndices[anchor.FaceReferenceId],
            anchor.WorkingUv,
            anchor.ModelPoint,
            wireDiameterMm,
            requiredInsertionDepthMm,
            Math.Min(curve.Approximation.MaximumSampleSpacingMm, _tolerances.MaximumContinuousSampleSpacingMm),
            curve.Approximation.RequestedAngularToleranceRad,
            curve.Approximation.MaximumMeasuredDeviationMm,
            _tolerances.ConnectionToleranceMm,
            _tolerances.MappingMaximumProbes);

        OcctCurveDepthEnvelopeQueryResult nativeResult;
        try
        {
            nativeResult = nativeBridge.MeasureDepthEnvelope(request);
        }
        catch (OperationCanceledException)
        {
            return Unresolved<DepthMeasurement>("OperationCancelled", "삽입 깊이 envelope 질의가 취소되었습니다.", entityIds: entityIds);
        }
        catch (Exception exception)
        {
            return Unresolved<DepthMeasurement>(
                "OcctDepthEnvelopeFailed",
                $"Native 깊이 envelope 질의가 실패했습니다: {exception.Message}",
                entityIds: entityIds);
        }

        if (cancellationToken.IsCancellationRequested)
            return Unresolved<DepthMeasurement>("OperationCancelled", "삽입 깊이 envelope 질의가 취소되었습니다.", entityIds: entityIds);
        if (nativeResult is null)
        {
            return new GeometryQueryResult<DepthMeasurement>(
                GeometryQueryStatus.Failure,
                default,
                [CreateIssue(
                    "OcctDepthEnvelopeFailed",
                    "Native 깊이 envelope 질의가 결과를 반환하지 않았습니다.",
                    entityIds: entityIds)]);
        }

        string evidence = nativeResult.Diagnostic ?? "Native 깊이 envelope 측정이 완료되지 않았습니다.";
        return nativeResult.Status switch
        {
            OcctCurveDepthEnvelopeStatus.Success => GeometryQueryResult<DepthMeasurement>.Succeeded(nativeResult.Measurement!),
            OcctCurveDepthEnvelopeStatus.Miss => GeometryQueryResult<DepthMeasurement>.Miss(),
            OcctCurveDepthEnvelopeStatus.Tangent => new GeometryQueryResult<DepthMeasurement>(
                GeometryQueryStatus.Tangent,
                default,
                [CreateIssue("DepthSurfaceTangent", evidence, entityIds: entityIds)]),
            OcctCurveDepthEnvelopeStatus.StaleContext => new GeometryQueryResult<DepthMeasurement>(
                GeometryQueryStatus.StaleGeometryContext,
                default,
                [CreateIssue("StaleGeometryContext", evidence, entityIds: entityIds)]),
            OcctCurveDepthEnvelopeStatus.GeometryFailure => new GeometryQueryResult<DepthMeasurement>(
                GeometryQueryStatus.Failure,
                default,
                [CreateIssue("OcctDepthEnvelopeFailed", evidence, entityIds: entityIds)]),
            OcctCurveDepthEnvelopeStatus.CandidateNotFound => new GeometryQueryResult<DepthMeasurement>(
                GeometryQueryStatus.Failure,
                default,
                [CreateIssue("PreparedCandidateNotFound", evidence, entityIds: entityIds)]),
            OcctCurveDepthEnvelopeStatus.InvalidFace => Unresolved<DepthMeasurement>(
                "UnresolvedFaceReference", evidence, entityIds: entityIds),
            OcctCurveDepthEnvelopeStatus.InvalidInput => Unresolved<DepthMeasurement>(
                "InvalidNativeDepthInput", evidence, entityIds: entityIds),
            OcctCurveDepthEnvelopeStatus.ProbeBudgetExceeded => Unresolved<DepthMeasurement>(
                "DepthEnvelopeBudgetExceeded", evidence, entityIds: entityIds),
            _ => Unresolved<DepthMeasurement>("DepthEnvelopeUnresolved", evidence, entityIds: entityIds)
        };
    }

    /// <summary>Native가 선택 Face를 유효 경계로 절단한 실제 표면 요소를 안정 Face ID로 바꿉니다.</summary>
    public GeometryQueryResult<SurfaceElementSet> GetSurfaceElements(
        HeatingRegion region,
        GeometryToleranceProfile tolerances,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(tolerances);
        if (cancellationToken.IsCancellationRequested)
            return Unresolved<SurfaceElementSet>("OperationCancelled", "표면 요소 질의가 취소되었습니다.");

        FaceRefId[] selectedFaces = region.FaceReferences.ToArray();
        FaceRefId[] missingFaces = selectedFaces
            .Where(face => !_nativeFaceIndices.ContainsKey(face))
            .ToArray();
        if (missingFaces.Length > 0)
        {
            return Unresolved<SurfaceElementSet>(
                "UnresolvedFaceReference",
                "면적 질의 대상 Face를 현재 OCCT 문서 번호로 대응하지 못했습니다.",
                entityIds: missingFaces.Select(face => face.Value));
        }

        OcctRegionLoop[] boundary = region.EffectiveBoundaryLoops
            .Select(loop => new OcctRegionLoop(loop.Vertices, loop.IsHole))
            .ToArray();
        if (boundary.Length == 0 || boundary.All(loop => loop.IsHole))
        {
            return Unresolved<SurfaceElementSet>(
                "InvalidEffectiveRegionBoundary",
                "실제 표면 분할에는 외곽 경계가 하나 이상 필요합니다.",
                entityIds: [region.Id.Value]);
        }

        if (cancellationToken.IsCancellationRequested)
            return Unresolved<SurfaceElementSet>("OperationCancelled", "표면 요소 질의가 취소되었습니다.");

        OcctSurfaceElementQueryResult nativeResult;
        try
        {
            nativeResult = _bridge.QuerySurfaceElements(
                _adjacencyCoordinateSystem.Id,
                _adjacencyCoordinateSystem.Revision,
                selectedFaces.Select(face => _nativeFaceIndices[face]).ToArray(),
                boundary,
                tolerances.MeshDeflectionMm,
                tolerances.MeshAngularToleranceRad);
        }
        catch (OperationCanceledException)
        {
            return Unresolved<SurfaceElementSet>("OperationCancelled", "표면 요소 질의가 취소되었습니다.");
        }
        catch (Exception exception)
        {
            return Unresolved<SurfaceElementSet>(
                "OcctSurfaceElementQueryFailed",
                exception.Message,
                entityIds: selectedFaces.Select(face => face.Value));
        }

        if (cancellationToken.IsCancellationRequested)
            return Unresolved<SurfaceElementSet>("OperationCancelled", "표면 요소 질의가 취소되었습니다.");
        if (nativeResult is null)
        {
            return new GeometryQueryResult<SurfaceElementSet>(
                GeometryQueryStatus.Failure,
                default,
                [CreateIssue(
                    "OcctSurfaceElementQueryFailed",
                    "OCCT 표면 요소 질의가 결과를 반환하지 않았습니다.",
                    entityIds: selectedFaces.Select(face => face.Value))]);
        }

        switch (nativeResult.Status)
        {
            case OcctSurfaceElementQueryStatus.InvalidInput:
                return Unresolved<SurfaceElementSet>(
                    "InvalidSurfaceElementQuery",
                    "OCCT가 표면 요소 입력을 거부했습니다.");
            case OcctSurfaceElementQueryStatus.InvalidFace:
                return Unresolved<SurfaceElementSet>(
                    "UnresolvedFaceReference",
                    "OCCT가 선택 Face 번호를 찾지 못했습니다.",
                    entityIds: selectedFaces.Select(face => face.Value));
            case OcctSurfaceElementQueryStatus.StaleContext:
                return new GeometryQueryResult<SurfaceElementSet>(
                    GeometryQueryStatus.StaleGeometryContext,
                    null,
                    [CreateIssue(
                        "StaleGeometryContext",
                        "표면 요소 context가 현재 모델 revision과 다릅니다.",
                        entityIds: selectedFaces.Select(face => face.Value))]);
            case OcctSurfaceElementQueryStatus.InvalidRegion:
                return Unresolved<SurfaceElementSet>(
                    "InvalidEffectiveRegionBoundary",
                    "유효 경계와 선택 표면이 교차하지 않거나 경계를 만들 수 없습니다.",
                    entityIds: [region.Id.Value]);
            case OcctSurfaceElementQueryStatus.ElementBudgetExceeded:
                return Unresolved<SurfaceElementSet>(
                    "SurfaceElementBudgetExceeded",
                    "OCCT 표면 메시가 요소 수 한도를 넘었습니다.",
                    entityIds: [region.Id.Value]);
            case OcctSurfaceElementQueryStatus.GeometryFailure:
                return new GeometryQueryResult<SurfaceElementSet>(
                    GeometryQueryStatus.Failure,
                    null,
                    [CreateIssue(
                        "OcctSurfaceElementQueryFailed",
                        "OCCT 절단 또는 메시 계산이 실패했습니다.",
                        entityIds: selectedFaces.Select(face => face.Value))]);
            case OcctSurfaceElementQueryStatus.Unknown:
                return Unresolved<SurfaceElementSet>(
                    "UnknownSurfaceElementQueryStatus",
                    "OCCT가 알 수 없는 표면 요소 상태를 반환했습니다.");
        }

        try
        {
            SurfaceElement[] elements = nativeResult.Elements
                .Select((element, index) =>
                {
                    if (!_faceReferencesByNativeIndex.TryGetValue(element.NativeFaceIndex, out FaceRefId faceId) ||
                        !selectedFaces.Contains(faceId))
                    {
                        throw new InvalidOperationException(
                            "OCCT 표면 요소가 선택되지 않은 Native Face를 가리킵니다.");
                    }

                    var approximation = new CurveApproximationInfo(
                        tolerances.MeshDeflectionMm,
                        tolerances.MeshAngularToleranceRad,
                        element.MaximumMeasuredDeviationMm,
                        GetMaximumTriangleEdgeLength(element.ModelVertices),
                        probeCount: 4,
                        isComplete: true,
                        ruleVersion: RuleVersion);
                    return new SurfaceElement(
                        index,
                        faceId,
                        new AreaMm2(element.SurfaceAreaMm2),
                        element.ModelCentroid,
                        element.FaceUvCentroid,
                        element.WorkingUvCentroid,
                        element.ModelVertices,
                        approximation);
                })
                .ToArray();
            var surface = new SurfaceElementSet(elements, new AreaMm2(nativeResult.AreaErrorBoundMm2));
            return GeometryQueryResult<SurfaceElementSet>.Succeeded(surface);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or ArithmeticException)
        {
            return Unresolved<SurfaceElementSet>(
                "InvalidNativeSurfaceElements",
                exception.Message,
                entityIds: selectedFaces.Select(face => face.Value));
        }
    }

    /// <summary>경로 곡선을 Native에 전달해 금형 이탈 뒤 재관통 충돌을 확인합니다.</summary>
    public GeometryQueryResult<ConnectionObstacleCheck> CheckConnectionPathObstacles(
        ConnectionPath connectionPath,
        WorkingCoordinateSystem coordinateSystem,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionPath);
        ArgumentNullException.ThrowIfNull(coordinateSystem);
        if (cancellationToken.IsCancellationRequested)
            return Unresolved<ConnectionObstacleCheck>("OperationCancelled", "연결 경로 충돌 질의가 취소되었습니다.");

        if (_bridge is not IOcctConnectionObstacleBridge obstacleBridge)
        {
            return Unsupported<ConnectionObstacleCheck>(
                "ConnectionObstacleQueryUnavailable",
                "현재 OCCT 브리지는 연결 경로 충돌과 금형 이탈 전이 질의를 제공하지 않습니다.");
        }

        try
        {
            ConnectionPathSegment[] segments = connectionPath.Segments.ToArray();
            if (segments.Length > MaximumConnectionCurveCount)
            {
                return Unresolved<ConnectionObstacleCheck>(
                    "ConnectionPathTooComplex",
                    "연결 경로 곡선 수가 Native 질의 한도를 넘었습니다.",
                    entityIds: [connectionPath.Id.Value]);
            }

            int transitionIndex = Array.FindIndex(segments, segment => segment.IsMoldExitTransition);
            if (transitionIndex < 1)
            {
                return Unresolved<ConnectionObstacleCheck>(
                    "MoldExitTransitionInvalid",
                    "금형 이탈 전이가 첫 구간이거나 경로에서 누락되었습니다.",
                    entityIds: [connectionPath.Id.Value]);
            }

            CurveDefinitionBridgeDto[] curves = segments
                .Select(segment => _curveDefinitionMapper.ToBridgeDto(segment.Curve))
                .ToArray();
            OcctConnectionObstacleQueryResult nativeResult = obstacleBridge.CheckConnectionPathObstacles(
                coordinateSystem.Id,
                coordinateSystem.Revision,
                curves,
                transitionIndex,
                _tolerances.ConnectionToleranceMm);

            return nativeResult.Status switch
            {
                OcctConnectionObstacleStatus.Clear => GeometryQueryResult<ConnectionObstacleCheck>.Succeeded(
                    new ConnectionObstacleCheck(true, null, null, moldExitTransitionFound: true)),
                OcctConnectionObstacleStatus.Obstructed => MapObstructedConnectionPath(nativeResult, connectionPath),
                OcctConnectionObstacleStatus.StaleContext => new GeometryQueryResult<ConnectionObstacleCheck>(
                    GeometryQueryStatus.StaleGeometryContext,
                    null,
                    [CreateIssue(
                        "StaleGeometryContext",
                        nativeResult.Diagnostic ?? "연결 경로 질의가 만료된 작업 좌표계를 사용했습니다.",
                        entityIds: [connectionPath.Id.Value])]),
                OcctConnectionObstacleStatus.GeometryFailure => new GeometryQueryResult<ConnectionObstacleCheck>(
                    GeometryQueryStatus.Failure,
                    null,
                    [CreateIssue(
                        "OcctConnectionObstacleFailed",
                        nativeResult.Diagnostic ?? "OCCT 연결 경로 충돌 질의에 실패했습니다.",
                        entityIds: [connectionPath.Id.Value])]),
                OcctConnectionObstacleStatus.InvalidInput or OcctConnectionObstacleStatus.CandidateNotFound =>
                    Unresolved<ConnectionObstacleCheck>(
                        "InvalidConnectionObstacleRequest",
                        nativeResult.Diagnostic ?? "Native 연결 경로 충돌 질의 입력이나 후보가 유효하지 않습니다.",
                        entityIds: [connectionPath.Id.Value]),
                _ => Unresolved<ConnectionObstacleCheck>(
                    "ConnectionObstacleUnresolved",
                    nativeResult.Diagnostic ?? "Native가 연결 경로의 금형 충돌 여부를 확정하지 못했습니다.",
                    entityIds: [connectionPath.Id.Value])
            };
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or ArithmeticException or NotSupportedException)
        {
            return Unresolved<ConnectionObstacleCheck>(
                "InvalidConnectionPathGeometry",
                exception.Message,
                entityIds: [connectionPath.Id.Value]);
        }
    }

    private static GeometryQueryResult<ConnectionObstacleCheck> MapObstructedConnectionPath(
        OcctConnectionObstacleQueryResult nativeResult,
        ConnectionPath connectionPath)
    {
        if (nativeResult.FirstObstruction is not Vec3 obstruction ||
            nativeResult.DistanceToObstruction is not LengthMm distance)
        {
            return Unresolved<ConnectionObstacleCheck>(
                "InvalidNativeConnectionObstruction",
                "Native 충돌 결과에 위치 또는 경로 거리가 없습니다.",
                entityIds: [connectionPath.Id.Value]);
        }

        try
        {
            return GeometryQueryResult<ConnectionObstacleCheck>.Succeeded(
                new ConnectionObstacleCheck(false, obstruction, distance, moldExitTransitionFound: true));
        }
        catch (ArgumentException exception)
        {
            return Unresolved<ConnectionObstacleCheck>(
                "InvalidNativeConnectionObstruction",
                exception.Message,
                entityIds: [connectionPath.Id.Value]);
        }
    }

    private GeometryQueryResult<SurfaceHit> MapHit(
        OcctSurfaceQueryResult nativeResult,
        IReadOnlyCollection<FaceRefId> selectedFaces,
        Vec2 workingUv)
    {
        int nativeFaceIndex = nativeResult.NativeFaceIndex!.Value;
        if (!_faceReferencesByNativeIndex.TryGetValue(nativeFaceIndex, out FaceRefId faceReferenceId))
        {
            return Unresolved<SurfaceHit>(
                "UnknownFirstHitFace",
                "OCCT first-hit Face가 현재 프로젝트의 안정 Face 참조에 연결되지 않았습니다.",
                workingUv);
        }
        if (!selectedFaces.Contains(faceReferenceId))
        {
            return Unresolved<SurfaceHit>(
                "FirstHitOutsideSelectedFaces",
                "전체 모델 first-hit이 선택된 발열 영역 Face보다 앞에 있습니다.",
                workingUv,
                [faceReferenceId.Value]);
        }

        try
        {
            var hit = new SurfaceHit(
                faceReferenceId,
                nativeResult.ModelPoint!.Value,
                nativeResult.SurfaceNormal!.Value,
                nativeResult.FaceUv!.Value);
            return GeometryQueryResult<SurfaceHit>.Succeeded(hit);
        }
        catch (Exception exception) when (exception is ArgumentException or ArithmeticException)
        {
            return Unresolved<SurfaceHit>("InvalidNativeSurfaceHit", exception.Message, workingUv, [faceReferenceId.Value]);
        }
    }

    private static double GetMaximumTriangleEdgeLength(IReadOnlyList<Vec3> vertices)
    {
        double firstEdge = (vertices[1] - vertices[0]).Length;
        double secondEdge = (vertices[2] - vertices[1]).Length;
        double thirdEdge = (vertices[0] - vertices[2]).Length;
        return Math.Max(firstEdge, Math.Max(secondEdge, thirdEdge));
    }

    private static GeometryQueryResult<T> Unsupported<T>(string code, string evidence) => Unresolved<T>(code, evidence);

    private static GeometryQueryResult<T> Unresolved<T>(
        string code,
        string evidence,
        Vec2? workingUv = null,
        IEnumerable<Guid>? entityIds = null) => GeometryQueryResult<T>.Unresolved(
        [CreateIssue(code, evidence, workingUv, entityIds)]);

    private static ValidationIssue CreateIssue(
        string code,
        string evidence,
        Vec2? workingUv = null,
        IEnumerable<Guid>? entityIds = null) => new(
        code,
        ValidationSeverity.Error,
        $"Geometry.{code}",
        entityIds,
        RuleVersion,
        datumUv: workingUv,
        evidence: [evidence]);
}
