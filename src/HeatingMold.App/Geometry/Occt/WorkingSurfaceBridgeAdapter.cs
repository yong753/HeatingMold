using System;
using System.Collections.Generic;
using System.Linq;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Infrastructure.Geometry.Occt;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Geometry.Occt;

/// <summary>Infrastructure 기하 질의를 OccBridge 작업면 API에 연결합니다.</summary>
public sealed class WorkingSurfaceBridgeAdapter :
    IOcctWorkingSurfaceBridge,
    IOcctPreparedCandidateFirstHitBridge,
    IOcctCurveClearanceBridge,
    IOcctCurveDepthBridge,
    IOcctCurveDepthAdjustmentBridge,
    IOcctConnectionObstacleBridge
{
    private const int MaximumConnectionCurveCount = 10_000;

    private readonly long? _preparedCandidateId;
    private readonly PreparedCandidateFirstHitBridgeAdapter _candidateFirstHitAdapter = new();

    /// <summary>활성 Native 문서에 연결된 표면 질의 어댑터를 만듭니다.</summary>
    public WorkingSurfaceBridgeAdapter()
    {
    }

    /// <summary>준비 Native 후보 session만 조회하는 표면 질의 어댑터를 만듭니다.</summary>
    public WorkingSurfaceBridgeAdapter(long preparedCandidateId)
    {
        if (preparedCandidateId <= 0)
            throw new ArgumentOutOfRangeException(nameof(preparedCandidateId));

        _preparedCandidateId = preparedCandidateId;
    }

    /// <summary>OCCT 전체 모델에서 작업 좌표의 첫 교차점을 조회합니다.</summary>
    public OcctSurfaceQueryResult QueryFirstHit(
        WorkingCoordinateSystemId coordinateSystemId,
        ModelRevision revision,
        Vec2 workingUv)
    {
        if (_preparedCandidateId is long candidateId)
            return QueryPreparedCandidateFirstHit(candidateId, coordinateSystemId, revision, workingUv);

        WorkingSurfaceHit nativeHit = Bridge.QueryWorkingSurface(
            coordinateSystemId.Value,
            revision.Value,
            workingUv.X,
            workingUv.Y);

        return MapSurfaceHit(nativeHit);
    }

    /// <summary>후보 ID를 지정해 활성 문서와 분리된 first-hit을 조회합니다.</summary>
    public OcctSurfaceQueryResult QueryPreparedCandidateFirstHit(
        long candidateId,
        WorkingCoordinateSystemId coordinateSystemId,
        ModelRevision revision,
        Vec2 workingUv) => _candidateFirstHitAdapter.QueryPreparedCandidateFirstHit(
            candidateId,
            coordinateSystemId,
            revision,
            workingUv);

    /// <summary>Native exact 곡선 정의를 활성 또는 준비 후보 문서에서 비교합니다.</summary>
    public OcctCurveClearanceQueryResult MeasureCurveClearance(
        WorkingCoordinateSystemId coordinateSystemId,
        ModelRevision revision,
        IReadOnlyList<CurveDefinitionBridgeDto> curves,
        IReadOnlyList<(int FirstCurveIndex, int SecondCurveIndex)> localCurvePairs,
        HeatingMold.Core.Primitives.LengthMm wireDiameterMm,
        double maximumErrorMm,
        int maximumPairChecks)
    {
        ArgumentNullException.ThrowIfNull(curves);
        ArgumentNullException.ThrowIfNull(localCurvePairs);
        if (localCurvePairs.Any(pair => pair.FirstCurveIndex < 0 || pair.FirstCurveIndex >= curves.Count ||
                pair.SecondCurveIndex < 0 || pair.SecondCurveIndex >= curves.Count ||
                pair.SecondCurveIndex == pair.FirstCurveIndex) ||
            localCurvePairs.Distinct().Count() != localCurvePairs.Count)
        {
            return new OcctCurveClearanceQueryResult(
                OcctCurveClearanceStatus.InvalidInput,
                diagnostic: "곡선 연결 관계 인덱스가 입력 배열과 일치하지 않습니다.");
        }

        CurveDefinitionData[] nativeCurves = curves
            .Select(CurveDefinitionBridgeAdapter.ToNativeData)
            .ToArray();
        int[] nativeLocalCurvePairs = localCurvePairs
            .SelectMany(pair => new[] { pair.FirstCurveIndex, pair.SecondCurveIndex })
            .ToArray();
        CurveClearanceResultData nativeResult = _preparedCandidateId is long candidateId
            ? Bridge.MeasurePreparedCandidateWorkingCurveClearance(
                candidateId,
                coordinateSystemId.Value,
                revision.Value,
                nativeCurves,
                nativeLocalCurvePairs,
                wireDiameterMm.Value,
                maximumErrorMm,
                maximumPairChecks)
            : Bridge.MeasureWorkingCurveClearance(
                coordinateSystemId.Value,
                revision.Value,
                nativeCurves,
                nativeLocalCurvePairs,
                wireDiameterMm.Value,
                maximumErrorMm,
                maximumPairChecks);

        if (nativeResult is null)
            return new OcctCurveClearanceQueryResult(
                OcctCurveClearanceStatus.GeometryFailure,
                diagnostic: "Native 곡선 간격 질의가 결과를 반환하지 않았습니다.");

        OcctCurveClearanceStatus status = nativeResult.Status switch
        {
            WorkingCurveClearanceStatus.Success => OcctCurveClearanceStatus.Success,
            WorkingCurveClearanceStatus.InvalidInput => OcctCurveClearanceStatus.InvalidInput,
            WorkingCurveClearanceStatus.StaleContext => OcctCurveClearanceStatus.StaleContext,
            WorkingCurveClearanceStatus.CandidateNotFound => OcctCurveClearanceStatus.CandidateNotFound,
            WorkingCurveClearanceStatus.PairBudgetExceeded => OcctCurveClearanceStatus.PairBudgetExceeded,
            WorkingCurveClearanceStatus.GeometryFailure => OcctCurveClearanceStatus.GeometryFailure,
            WorkingCurveClearanceStatus.Unresolved => OcctCurveClearanceStatus.Unresolved,
            _ => OcctCurveClearanceStatus.Unknown
        };
        if (status != OcctCurveClearanceStatus.Success)
            return new OcctCurveClearanceQueryResult(status, diagnostic: nativeResult.Diagnostic);

        try
        {
            Vec3 firstLocation = ToPoint(nativeResult.FirstLocation, nameof(nativeResult.FirstLocation));
            Vec3 secondLocation = ToPoint(nativeResult.SecondLocation, nameof(nativeResult.SecondLocation));
            double centerlineDistanceMm = nativeResult.MinimumCenterlineDistanceMm;
            double errorBoundMm = nativeResult.ErrorBoundMm;
            if (!double.IsFinite(centerlineDistanceMm) || centerlineDistanceMm < 0 ||
                !double.IsFinite(errorBoundMm) || errorBoundMm < 0)
            {
                throw new ArgumentException("Native 최소 간격이나 오차 범위가 유효하지 않습니다.");
            }

            var measurement = new HeatingMold.Core.Contracts.Geometry.ClearanceMeasurement(
                new HeatingMold.Core.Primitives.LengthMm(centerlineDistanceMm),
                new HeatingMold.Core.Primitives.LengthMm(centerlineDistanceMm - wireDiameterMm.Value),
                firstLocation,
                secondLocation,
                new HeatingMold.Core.Primitives.LengthMm(errorBoundMm),
                isComplete: true);
            return new OcctCurveClearanceQueryResult(status, measurement);
        }
        catch (Exception exception) when (exception is ArgumentException or ArithmeticException)
        {
            return new OcctCurveClearanceQueryResult(
                OcctCurveClearanceStatus.GeometryFailure,
                diagnostic: exception.Message);
        }
    }

    /// <summary>깊이 표본과 선택 Face를 활성 또는 후보 Native session에서 측정합니다.</summary>
    public OcctCurveDepthEnvelopeQueryResult MeasureDepthEnvelope(OcctCurveDepthEnvelopeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        double[] centerlineSamples = request.CenterlineSamples
            .SelectMany(point => new[] { point.X, point.Y, point.Z })
            .ToArray();
        int[] selectedFaceIndices = request.SelectedNativeFaceIndices.ToArray();
        double[] anchorPoint =
        [
            request.AnchorModelPoint.X,
            request.AnchorModelPoint.Y,
            request.AnchorModelPoint.Z
        ];
        CurveDepthEnvelopeResultData nativeResult = _preparedCandidateId is long candidateId
            ? Bridge.MeasurePreparedCandidateWorkingCurveDepthEnvelope(
                candidateId,
                request.CoordinateSystemId.Value,
                request.Revision.Value,
                centerlineSamples,
                selectedFaceIndices,
                request.AnchorNativeFaceIndex,
                request.AnchorWorkingUv.X,
                request.AnchorWorkingUv.Y,
                anchorPoint,
                request.WireDiameterMm.Value,
                request.RequiredInsertionDepthMm.Value,
                request.MaximumSampleSpacingMm,
                request.RequestedAngularToleranceRad,
                request.MaximumMeasuredDeviationMm,
                request.ConnectionToleranceMm,
                request.MaximumProbeCount)
            : Bridge.MeasureWorkingCurveDepthEnvelope(
                request.CoordinateSystemId.Value,
                request.Revision.Value,
                centerlineSamples,
                selectedFaceIndices,
                request.AnchorNativeFaceIndex,
                request.AnchorWorkingUv.X,
                request.AnchorWorkingUv.Y,
                anchorPoint,
                request.WireDiameterMm.Value,
                request.RequiredInsertionDepthMm.Value,
                request.MaximumSampleSpacingMm,
                request.RequestedAngularToleranceRad,
                request.MaximumMeasuredDeviationMm,
                request.ConnectionToleranceMm,
                request.MaximumProbeCount);

        if (nativeResult is null)
        {
            return new OcctCurveDepthEnvelopeQueryResult(
                OcctCurveDepthEnvelopeStatus.GeometryFailure,
                diagnostic: "Native 깊이 envelope 질의가 결과를 반환하지 않았습니다.");
        }

        OcctCurveDepthEnvelopeStatus status = nativeResult.Status switch
        {
            WorkingCurveDepthEnvelopeStatus.Success => OcctCurveDepthEnvelopeStatus.Success,
            WorkingCurveDepthEnvelopeStatus.InvalidInput => OcctCurveDepthEnvelopeStatus.InvalidInput,
            WorkingCurveDepthEnvelopeStatus.InvalidFace => OcctCurveDepthEnvelopeStatus.InvalidFace,
            WorkingCurveDepthEnvelopeStatus.CandidateNotFound => OcctCurveDepthEnvelopeStatus.CandidateNotFound,
            WorkingCurveDepthEnvelopeStatus.StaleContext => OcctCurveDepthEnvelopeStatus.StaleContext,
            WorkingCurveDepthEnvelopeStatus.Miss => OcctCurveDepthEnvelopeStatus.Miss,
            WorkingCurveDepthEnvelopeStatus.Tangent => OcctCurveDepthEnvelopeStatus.Tangent,
            WorkingCurveDepthEnvelopeStatus.ProbeBudgetExceeded => OcctCurveDepthEnvelopeStatus.ProbeBudgetExceeded,
            WorkingCurveDepthEnvelopeStatus.GeometryFailure => OcctCurveDepthEnvelopeStatus.GeometryFailure,
            WorkingCurveDepthEnvelopeStatus.Unresolved => OcctCurveDepthEnvelopeStatus.Unresolved,
            _ => OcctCurveDepthEnvelopeStatus.Unknown
        };
        if (status != OcctCurveDepthEnvelopeStatus.Success)
            return new OcctCurveDepthEnvelopeQueryResult(status, diagnostic: nativeResult.Diagnostic);

        try
        {
            double minimumDepthMm = nativeResult.MinimumInsertionDepthMm;
            double maximumDepthMm = nativeResult.MaximumInsertionDepthMm;
            double measuredDepthMm = nativeResult.MeasuredInsertionDepthMm;
            double errorBoundMm = nativeResult.ErrorBoundMm;
            if (!double.IsFinite(minimumDepthMm) || minimumDepthMm < 0 ||
                !double.IsFinite(maximumDepthMm) || maximumDepthMm < minimumDepthMm ||
                !double.IsFinite(measuredDepthMm) || measuredDepthMm < minimumDepthMm || measuredDepthMm > maximumDepthMm ||
                !double.IsFinite(errorBoundMm) || errorBoundMm < 0 || nativeResult.ProbeCount < 2)
            {
                throw new ArgumentException("Native 깊이 envelope의 범위·오차·표본 수가 유효하지 않습니다.");
            }

            var measurement = new HeatingMold.Core.Contracts.Geometry.DepthMeasurement(
                request.RequiredInsertionDepthMm,
                new HeatingMold.Core.Primitives.LengthMm(measuredDepthMm),
                new HeatingMold.Core.Primitives.LengthMm(minimumDepthMm),
                new HeatingMold.Core.Primitives.LengthMm(maximumDepthMm),
                new HeatingMold.Core.Primitives.LengthMm(errorBoundMm),
                ToPoint(nativeResult.SurfacePoint, nameof(nativeResult.SurfacePoint)),
                ToPoint(nativeResult.MinimumSurfacePoint, nameof(nativeResult.MinimumSurfacePoint)),
                ToPoint(nativeResult.MaximumSurfacePoint, nameof(nativeResult.MaximumSurfacePoint)),
                ToPoint(nativeResult.MinimumWireOutsidePoint, nameof(nativeResult.MinimumWireOutsidePoint)),
                ToPoint(nativeResult.MaximumWireOutsidePoint, nameof(nativeResult.MaximumWireOutsidePoint)),
                nativeResult.ProbeCount,
                isResolved: true,
                isComplete: true,
                measurementRuleVersion: "occt-native-sampled-depth-envelope-v2");
            return new OcctCurveDepthEnvelopeQueryResult(status, measurement);
        }
        catch (Exception exception) when (exception is ArgumentException or ArithmeticException or NullReferenceException)
        {
            return new OcctCurveDepthEnvelopeQueryResult(
                OcctCurveDepthEnvelopeStatus.GeometryFailure,
                diagnostic: exception.Message);
        }
    }

    /// <summary>선택 Face first-hit을 따라 Native root solver로 깊이 중심선을 계산합니다.</summary>
    public OcctCurveDepthAdjustmentQueryResult AdjustInsertionDepth(OcctCurveDepthAdjustmentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        double[] workingSamples = request.SurfacePathWorkingSamples
            .SelectMany(point => new[] { point.X, point.Y })
            .ToArray();
        int[] selectedFaceIndices = request.SelectedNativeFaceIndices.ToArray();
        double[] anchorPoint =
        [
            request.AnchorModelPoint.X,
            request.AnchorModelPoint.Y,
            request.AnchorModelPoint.Z
        ];
        CurveDepthAdjustmentResultData nativeResult = _preparedCandidateId is long candidateId
            ? Bridge.AdjustPreparedCandidateWorkingCurveInsertionDepth(
                candidateId,
                request.CoordinateSystemId.Value,
                request.Revision.Value,
                workingSamples,
                selectedFaceIndices,
                request.AnchorNativeFaceIndex,
                request.AnchorWorkingUv.X,
                request.AnchorWorkingUv.Y,
                anchorPoint,
                request.WireDiameterMm.Value,
                request.RequiredInsertionDepthMm.Value,
                request.MaximumSampleSpacingMm,
                request.RequestedAngularToleranceRad,
                request.MaximumSurfaceDeviationMm,
                request.ConnectionToleranceMm,
                request.MaximumProbeCount)
            : Bridge.AdjustWorkingCurveInsertionDepth(
                request.CoordinateSystemId.Value,
                request.Revision.Value,
                workingSamples,
                selectedFaceIndices,
                request.AnchorNativeFaceIndex,
                request.AnchorWorkingUv.X,
                request.AnchorWorkingUv.Y,
                anchorPoint,
                request.WireDiameterMm.Value,
                request.RequiredInsertionDepthMm.Value,
                request.MaximumSampleSpacingMm,
                request.RequestedAngularToleranceRad,
                request.MaximumSurfaceDeviationMm,
                request.ConnectionToleranceMm,
                request.MaximumProbeCount);

        if (nativeResult is null)
        {
            return new OcctCurveDepthAdjustmentQueryResult(
                OcctCurveDepthEnvelopeStatus.GeometryFailure,
                diagnostic: "Native 깊이 root 보정이 결과를 반환하지 않았습니다.");
        }

        OcctCurveDepthEnvelopeStatus status = nativeResult.Status switch
        {
            WorkingCurveDepthEnvelopeStatus.Success => OcctCurveDepthEnvelopeStatus.Success,
            WorkingCurveDepthEnvelopeStatus.InvalidInput => OcctCurveDepthEnvelopeStatus.InvalidInput,
            WorkingCurveDepthEnvelopeStatus.InvalidFace => OcctCurveDepthEnvelopeStatus.InvalidFace,
            WorkingCurveDepthEnvelopeStatus.CandidateNotFound => OcctCurveDepthEnvelopeStatus.CandidateNotFound,
            WorkingCurveDepthEnvelopeStatus.StaleContext => OcctCurveDepthEnvelopeStatus.StaleContext,
            WorkingCurveDepthEnvelopeStatus.Miss => OcctCurveDepthEnvelopeStatus.Miss,
            WorkingCurveDepthEnvelopeStatus.Tangent => OcctCurveDepthEnvelopeStatus.Tangent,
            WorkingCurveDepthEnvelopeStatus.ProbeBudgetExceeded => OcctCurveDepthEnvelopeStatus.ProbeBudgetExceeded,
            WorkingCurveDepthEnvelopeStatus.GeometryFailure => OcctCurveDepthEnvelopeStatus.GeometryFailure,
            WorkingCurveDepthEnvelopeStatus.Unresolved => OcctCurveDepthEnvelopeStatus.Unresolved,
            _ => OcctCurveDepthEnvelopeStatus.Unknown
        };
        if (status != OcctCurveDepthEnvelopeStatus.Success)
            return new OcctCurveDepthAdjustmentQueryResult(status, diagnostic: nativeResult.Diagnostic);

        try
        {
            double[] coordinates = nativeResult.CenterlineSamples ?? [];
            if (coordinates.Length < 6 || coordinates.Length % 3 != 0 ||
                coordinates.Any(value => !double.IsFinite(value)) ||
                !double.IsFinite(nativeResult.MaximumDepthResidualMm) || nativeResult.MaximumDepthResidualMm < 0 ||
                !double.IsFinite(nativeResult.ErrorBoundMm) || nativeResult.ErrorBoundMm < nativeResult.MaximumDepthResidualMm ||
                nativeResult.ProbeCount < coordinates.Length / 3)
            {
                throw new ArgumentException("Native 깊이 root 결과의 중심선·오차·표본 근거가 유효하지 않습니다.");
            }

            Vec3[] centerlinePoints = new Vec3[coordinates.Length / 3];
            for (int index = 0; index < centerlinePoints.Length; index++)
            {
                int offset = index * 3;
                centerlinePoints[index] = new Vec3(coordinates[offset], coordinates[offset + 1], coordinates[offset + 2]);
            }
            var curve = new PolylineCurveDefinition(centerlinePoints);
            var adjustment = new InsertionDepthAdjustedPath(
                curve,
                new LengthMm(nativeResult.MaximumDepthResidualMm),
                new LengthMm(nativeResult.ErrorBoundMm),
                nativeResult.ProbeCount,
                "native-axis-ray-bracket-v2");
            return new OcctCurveDepthAdjustmentQueryResult(status, adjustment);
        }
        catch (Exception exception) when (exception is ArgumentException or ArithmeticException or NullReferenceException)
        {
            return new OcctCurveDepthAdjustmentQueryResult(
                OcctCurveDepthEnvelopeStatus.GeometryFailure,
                diagnostic: exception.Message);
        }
    }

    /// <summary>정확한 연결 곡선, 이탈 전이와 연결 공차를 Native 금형 충돌 질의에 전달합니다.</summary>
    public OcctConnectionObstacleQueryResult CheckConnectionPathObstacles(
        WorkingCoordinateSystemId coordinateSystemId,
        ModelRevision revision,
        IReadOnlyList<CurveDefinitionBridgeDto> curves,
        int moldExitTransitionIndex,
        double connectionToleranceMm)
    {
        ArgumentNullException.ThrowIfNull(curves);
        if (!double.IsFinite(connectionToleranceMm) || connectionToleranceMm <= 0)
        {
            return new OcctConnectionObstacleQueryResult(
                OcctConnectionObstacleStatus.InvalidInput,
                diagnostic: "연결 경로 충돌 질의 공차는 양의 유한값이어야 합니다.");
        }
        if (curves.Count < 2 || curves.Count > MaximumConnectionCurveCount ||
            moldExitTransitionIndex <= 0 || moldExitTransitionIndex >= curves.Count)
        {
            return new OcctConnectionObstacleQueryResult(
                OcctConnectionObstacleStatus.InvalidInput,
                diagnostic: "연결 경로에는 첫 구간 뒤에 유효한 이탈 전이가 있어야 합니다.");
        }

        CurveDefinitionData[] nativeCurves = curves
            .Select(CurveDefinitionBridgeAdapter.ToNativeData)
            .ToArray();
        ConnectionObstacleResultData nativeResult = _preparedCandidateId is long candidateId
            ? Bridge.CheckPreparedCandidateConnectionPathObstacles(
                candidateId,
                coordinateSystemId.Value,
                revision.Value,
                nativeCurves,
                moldExitTransitionIndex,
                connectionToleranceMm)
            : Bridge.CheckConnectionPathObstacles(
                coordinateSystemId.Value,
                revision.Value,
                nativeCurves,
                moldExitTransitionIndex,
                connectionToleranceMm);

        if (nativeResult is null)
        {
            return new OcctConnectionObstacleQueryResult(
                OcctConnectionObstacleStatus.GeometryFailure,
                diagnostic: "Native 연결 경로 충돌 질의가 결과를 반환하지 않았습니다.");
        }

        OcctConnectionObstacleStatus status = nativeResult.Status switch
        {
            WorkingConnectionObstacleStatus.Clear => OcctConnectionObstacleStatus.Clear,
            WorkingConnectionObstacleStatus.Obstructed => OcctConnectionObstacleStatus.Obstructed,
            WorkingConnectionObstacleStatus.InvalidInput => OcctConnectionObstacleStatus.InvalidInput,
            WorkingConnectionObstacleStatus.StaleContext => OcctConnectionObstacleStatus.StaleContext,
            WorkingConnectionObstacleStatus.CandidateNotFound => OcctConnectionObstacleStatus.CandidateNotFound,
            WorkingConnectionObstacleStatus.GeometryFailure => OcctConnectionObstacleStatus.GeometryFailure,
            WorkingConnectionObstacleStatus.Unresolved => OcctConnectionObstacleStatus.Unresolved,
            _ => OcctConnectionObstacleStatus.Unknown
        };

        if (status is not (OcctConnectionObstacleStatus.Clear or OcctConnectionObstacleStatus.Obstructed))
            return new OcctConnectionObstacleQueryResult(status, diagnostic: nativeResult.Diagnostic);

        try
        {
            Vec3? obstruction = status == OcctConnectionObstacleStatus.Obstructed
                ? ToPoint(nativeResult.FirstObstruction, nameof(nativeResult.FirstObstruction))
                : null;
            LengthMm? distance = status == OcctConnectionObstacleStatus.Obstructed
                ? new LengthMm(nativeResult.DistanceToObstructionMm)
                : null;
            return new OcctConnectionObstacleQueryResult(
                status,
                obstruction,
                distance,
                nativeResult.Diagnostic);
        }
        catch (Exception exception) when (exception is ArgumentException or ArithmeticException)
        {
            return new OcctConnectionObstacleQueryResult(
                OcctConnectionObstacleStatus.GeometryFailure,
                diagnostic: exception.Message);
        }
    }

    /// <summary>OCCT 공유 Edge 인접 질의의 상태를 관리형 결과로 변환합니다.</summary>
    public OcctFaceAdjacencyStatus QueryFaceAdjacency(
        WorkingCoordinateSystemId coordinateSystemId,
        ModelRevision revision,
        int firstNativeFaceIndex,
        int secondNativeFaceIndex)
    {
        WorkingFaceAdjacencyStatus nativeStatus = _preparedCandidateId is long candidateId
            ? Bridge.QueryPreparedCandidateFaceAdjacency(
                candidateId,
                coordinateSystemId.Value,
                revision.Value,
                firstNativeFaceIndex,
                secondNativeFaceIndex)
            : Bridge.QueryWorkingFaceAdjacency(
                coordinateSystemId.Value,
                revision.Value,
                firstNativeFaceIndex,
                secondNativeFaceIndex);

        return nativeStatus switch
        {
            WorkingFaceAdjacencyStatus.Adjacent => OcctFaceAdjacencyStatus.Adjacent,
            WorkingFaceAdjacencyStatus.NotAdjacent => OcctFaceAdjacencyStatus.NotAdjacent,
            WorkingFaceAdjacencyStatus.InvalidFace => OcctFaceAdjacencyStatus.InvalidFace,
            WorkingFaceAdjacencyStatus.StaleContext => OcctFaceAdjacencyStatus.StaleContext,
            WorkingFaceAdjacencyStatus.GeometryFailure => OcctFaceAdjacencyStatus.GeometryFailure,
            _ => OcctFaceAdjacencyStatus.Unknown
        };
    }

    /// <summary>Native topology의 좌표, 연결 성분, 인접 관계를 Infrastructure DTO로 변환합니다.</summary>
    public OcctSelectedTopologyResult QuerySelectedTopology(
        WorkingCoordinateSystemId coordinateSystemId,
        ModelRevision revision,
        IReadOnlyList<int> selectedNativeFaceIndices,
        double deflectionMm)
    {
        WorkingSelectedTopology nativeResult = _preparedCandidateId is long candidateId
            ? Bridge.QueryPreparedCandidateSelectedTopology(
                candidateId,
                coordinateSystemId.Value,
                revision.Value,
                selectedNativeFaceIndices.ToArray(),
                deflectionMm)
            : Bridge.QueryWorkingSelectedTopology(
                coordinateSystemId.Value,
                revision.Value,
                selectedNativeFaceIndices.ToArray(),
                deflectionMm);
        if (nativeResult is null)
            return new OcctSelectedTopologyResult(OcctSelectedTopologyStatus.GeometryFailure);

        OcctSelectedTopologyStatus status = nativeResult.Status switch
        {
            WorkingTopologyQueryStatus.Success => OcctSelectedTopologyStatus.Success,
            WorkingTopologyQueryStatus.InvalidInput => OcctSelectedTopologyStatus.InvalidInput,
            WorkingTopologyQueryStatus.InvalidFace => OcctSelectedTopologyStatus.InvalidFace,
            WorkingTopologyQueryStatus.StaleContext => OcctSelectedTopologyStatus.StaleContext,
            WorkingTopologyQueryStatus.InvalidTopology => OcctSelectedTopologyStatus.InvalidTopology,
            WorkingTopologyQueryStatus.GeometryFailure => OcctSelectedTopologyStatus.GeometryFailure,
            _ => OcctSelectedTopologyStatus.Unknown
        };
        if (status != OcctSelectedTopologyStatus.Success)
            return new OcctSelectedTopologyResult(status);

        try
        {
            OcctTopologyComponent[] components = nativeResult.Components
                .Select(component => new OcctTopologyComponent(
                    component.FaceIndices,
                    component.BoundaryLoops.Select(loop => new OcctTopologyBoundaryLoop(
                        ToPoints(loop.Points),
                        loop.SegmentFaceIndices,
                        loop.IsClosed))))
                .ToArray();
            OcctTopologyAdjacency[] adjacencies = nativeResult.Adjacencies
                .Select(adjacency => new OcctTopologyAdjacency(
                    adjacency.FirstFaceIndex,
                    adjacency.SecondFaceIndex))
                .ToArray();
            return new OcctSelectedTopologyResult(status, components, adjacencies);
        }
        catch (Exception exception) when (
            exception is ArgumentException or ArithmeticException or NullReferenceException)
        {
            return new OcctSelectedTopologyResult(OcctSelectedTopologyStatus.GeometryFailure);
        }
    }

    /// <summary>선택 면과 유효 경계로 잘라낸 표면 삼각 요소를 Infrastructure 결과로 변환합니다.</summary>
    public OcctSurfaceElementQueryResult QuerySurfaceElements(
        WorkingCoordinateSystemId coordinateSystemId,
        ModelRevision revision,
        IReadOnlyList<int> selectedNativeFaceIndices,
        IReadOnlyList<OcctRegionLoop> effectiveBoundary,
        double deflectionMm,
        double angularToleranceRad)
    {
        WorkingRegionLoopData[] nativeBoundary = effectiveBoundary
            .Select(loop => new WorkingRegionLoopData
            {
                IsHole = loop.IsHole,
                Points = loop.Vertices.SelectMany(vertex => new[] { vertex.X, vertex.Y }).ToArray()
            })
            .ToArray();
        WorkingSurfaceElementsResult nativeResult = _preparedCandidateId is long candidateId
            ? Bridge.QueryPreparedCandidateSurfaceElements(
                candidateId,
                coordinateSystemId.Value,
                revision.Value,
                selectedNativeFaceIndices.ToArray(),
                nativeBoundary,
                deflectionMm,
                angularToleranceRad)
            : Bridge.QueryWorkingSurfaceElements(
                coordinateSystemId.Value,
                revision.Value,
                selectedNativeFaceIndices.ToArray(),
                nativeBoundary,
                deflectionMm,
                angularToleranceRad);
        if (nativeResult is null)
            return new OcctSurfaceElementQueryResult(OcctSurfaceElementQueryStatus.GeometryFailure);

        OcctSurfaceElementQueryStatus status = nativeResult.Status switch
        {
            WorkingSurfaceElementQueryStatus.Success => OcctSurfaceElementQueryStatus.Success,
            WorkingSurfaceElementQueryStatus.InvalidInput => OcctSurfaceElementQueryStatus.InvalidInput,
            WorkingSurfaceElementQueryStatus.InvalidFace => OcctSurfaceElementQueryStatus.InvalidFace,
            WorkingSurfaceElementQueryStatus.StaleContext => OcctSurfaceElementQueryStatus.StaleContext,
            WorkingSurfaceElementQueryStatus.InvalidRegion => OcctSurfaceElementQueryStatus.InvalidRegion,
            WorkingSurfaceElementQueryStatus.ElementBudgetExceeded => OcctSurfaceElementQueryStatus.ElementBudgetExceeded,
            WorkingSurfaceElementQueryStatus.GeometryFailure => OcctSurfaceElementQueryStatus.GeometryFailure,
            _ => OcctSurfaceElementQueryStatus.Unknown
        };
        if (status != OcctSurfaceElementQueryStatus.Success)
            return new OcctSurfaceElementQueryResult(status);

        try
        {
            OcctSurfaceElementData[] elements = nativeResult.Elements
                .Select(element => new OcctSurfaceElementData(
                    element.FaceIndex,
                    element.SurfaceAreaMm2,
                    ToPoint(element.ModelCentroid, nameof(element.ModelCentroid)),
                    ToVector(element.FaceUvCentroid, nameof(element.FaceUvCentroid)),
                    ToVector(element.WorkingUvCentroid, nameof(element.WorkingUvCentroid)),
                    ToTrianglePoints(element.ModelVertices),
                    element.MaximumMeasuredDeviationMm))
                .ToArray();
            return new OcctSurfaceElementQueryResult(
                status,
                elements,
                nativeResult.AreaErrorBoundMm2);
        }
        catch (Exception exception) when (
            exception is ArgumentException or ArithmeticException or NullReferenceException)
        {
            return new OcctSurfaceElementQueryResult(OcctSurfaceElementQueryStatus.GeometryFailure);
        }
    }

    private static OcctSurfaceQueryResult CreateHit(WorkingSurfaceHit hit)
    {
        try
        {
            double[] point = RequireLength(hit.Point, 3, nameof(hit.Point));
            double[] normal = RequireLength(hit.Normal, 3, nameof(hit.Normal));
            double[] faceUv = RequireLength(hit.FaceUv, 2, nameof(hit.FaceUv));
            return new OcctSurfaceQueryResult(
                OcctSurfaceQueryStatus.Hit,
                hit.FaceId,
                new Vec3(point[0], point[1], point[2]),
                new Vec3(normal[0], normal[1], normal[2]),
                new Vec2(faceUv[0], faceUv[1]));
        }
        catch (Exception exception) when (exception is ArgumentException or ArithmeticException)
        {
            return new OcctSurfaceQueryResult(OcctSurfaceQueryStatus.GeometryFailure);
        }
    }

    private static OcctSurfaceQueryResult MapSurfaceHit(WorkingSurfaceHit nativeHit)
    {
        if (nativeHit is null)
            return new OcctSurfaceQueryResult(OcctSurfaceQueryStatus.GeometryFailure);

        return nativeHit.Status switch
        {
            WorkingSurfaceQueryStatus.Hit => CreateHit(nativeHit),
            WorkingSurfaceQueryStatus.Miss => new OcctSurfaceQueryResult(OcctSurfaceQueryStatus.Miss),
            WorkingSurfaceQueryStatus.Tangent => new OcctSurfaceQueryResult(OcctSurfaceQueryStatus.Tangent),
            WorkingSurfaceQueryStatus.InvalidInput => new OcctSurfaceQueryResult(OcctSurfaceQueryStatus.InvalidInput),
            WorkingSurfaceQueryStatus.StaleContext => new OcctSurfaceQueryResult(OcctSurfaceQueryStatus.StaleContext),
            WorkingSurfaceQueryStatus.GeometryFailure => new OcctSurfaceQueryResult(OcctSurfaceQueryStatus.GeometryFailure),
            _ => new OcctSurfaceQueryResult(OcctSurfaceQueryStatus.Unknown)
        };
    }

    private static double[] RequireLength(double[] values, int expectedLength, string parameterName)
    {
        if (values is null || values.Length != expectedLength)
        {
            throw new ArgumentException(
                $"OCCT {parameterName} 결과는 길이가 {expectedLength}이어야 합니다.",
                parameterName);
        }

        return values;
    }

    private static Vec3[] ToPoints(double[] values)
    {
        if (values is null || values.Length < 9 || values.Length % 3 != 0)
        {
            throw new ArgumentException(
                "Native topology 점은 세 개 이상의 XYZ 좌표로 이루어져야 합니다.",
                nameof(values));
        }

        var points = new Vec3[values.Length / 3];
        for (int index = 0; index < points.Length; index++)
        {
            int offset = index * 3;
            points[index] = new Vec3(values[offset], values[offset + 1], values[offset + 2]);
        }

        return points;
    }

    private static Vec3 ToPoint(double[] values, string parameterName)
    {
        double[] coordinates = RequireLength(values, 3, parameterName);
        return new Vec3(coordinates[0], coordinates[1], coordinates[2]);
    }

    private static Vec2 ToVector(double[] values, string parameterName)
    {
        double[] coordinates = RequireLength(values, 2, parameterName);
        return new Vec2(coordinates[0], coordinates[1]);
    }

    private static Vec3[] ToTrianglePoints(double[] values)
    {
        double[] coordinates = RequireLength(values, 9, nameof(values));
        return
        [
            new Vec3(coordinates[0], coordinates[1], coordinates[2]),
            new Vec3(coordinates[3], coordinates[4], coordinates[5]),
            new Vec3(coordinates[6], coordinates[7], coordinates[8])
        ];
    }
}
