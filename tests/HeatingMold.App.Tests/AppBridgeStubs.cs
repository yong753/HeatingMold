using System;
using System.Collections.Generic;
using System.Linq;
using HeatingMold.App.ViewModels;
using HeatingMold.Core.Geometry;

namespace HeatingMold.OccBridge
{

/// <summary>Native STEP 모델 확정 결과 상태를 테스트에서 표현합니다.</summary>
public enum ModelLoadStatus
{
    /// <summary>후보 준비에 실패해 현재 모델을 유지했습니다.</summary>
    FailedBeforeCommit,

    /// <summary>모델과 표시를 확정했습니다.</summary>
    Committed,

    /// <summary>모델은 확정했지만 표시 동기화에 실패했습니다.</summary>
    CommittedPresentationOutOfSync
}

/// <summary>테스트용 STEP 후보 준비 상태입니다.</summary>
public enum ModelLoadCandidateStatus
{
    /// <summary>후보 준비에 실패했습니다.</summary>
    Failed,

    /// <summary>활성 문서를 바꾸지 않고 후보를 준비했습니다.</summary>
    Prepared
}

/// <summary>테스트용 STEP 후보 식별자와 기준 revision입니다.</summary>
public sealed class ModelLoadCandidateResult
{
    /// <summary>후보 처리 상태입니다.</summary>
    public ModelLoadCandidateStatus Status { get; set; }

    /// <summary>후보가 준비되기 전 활성 model revision입니다.</summary>
    public long BaseModelRevision { get; set; }

    /// <summary>후보 소유권을 식별하는 임시 ID입니다.</summary>
    public long CandidateId { get; set; }

    /// <summary>준비 실패 원인입니다.</summary>
    public string Diagnostic { get; set; }
}

/// <summary>테스트 모델 로드의 상태와 진단입니다.</summary>
public sealed class ModelLoadResult
{
    /// <summary>로드 후보 처리 결과입니다.</summary>
    public ModelLoadStatus Status { get; set; }

    /// <summary>로드 처리 후 활성 모델 revision입니다.</summary>
    public long ModelRevision { get; set; }

    /// <summary>실패 또는 후처리 진단입니다.</summary>
    public string Diagnostic { get; set; }
}

/// <summary>테스트가 제공하는 모델 형상 개수입니다.</summary>
public sealed class ShapeInfo
{
    /// <summary>솔리드 수입니다.</summary>
    public int Solids { get; set; }

    /// <summary>셸 수입니다.</summary>
    public int Shells { get; set; }

    /// <summary>Face 수입니다.</summary>
    public int Faces { get; set; }

    /// <summary>평면 Face 수입니다.</summary>
    public int Planes { get; set; }
}

/// <summary>테스트용 Native Face 특징 질의 상태입니다.</summary>
public enum FaceFeatureQueryStatus
{
    Success,
    InvalidModel,
    GeometryFailure,
    CandidateNotFound
}

/// <summary>테스트용 Native Face 특징 자료입니다.</summary>
public sealed class FaceGeometryFeatureData
{
    /// <summary>현재 문서의 0 기반 Face 번호입니다.</summary>
    public int FaceIndex { get; set; }
    /// <summary>Native 표면 종류 이름입니다.</summary>
    public string SurfaceType { get; set; }
    /// <summary>Native 면적(mm²)입니다.</summary>
    public double SurfaceAreaMm2 { get; set; }
    /// <summary>면 중심 XYZ(mm)입니다.</summary>
    public double[] Center { get; set; }
    /// <summary>공유 Edge 이웃 번호입니다.</summary>
    public int[] AdjacentFaceIndices { get; set; }
}

/// <summary>테스트용 Face 특징 질의 응답입니다.</summary>
public sealed class FaceGeometryFeaturesResult
{
    /// <summary>질의 상태입니다.</summary>
    public FaceFeatureQueryStatus Status { get; set; }
    /// <summary>Native Face 특징 목록입니다.</summary>
    public FaceGeometryFeatureData[] Features { get; set; }
}

/// <summary>테스트 Face의 UV 경계 표본입니다.</summary>
public sealed class UvWire
{
    /// <summary>외곽 경계인지 나타냅니다.</summary>
    public bool IsOuter { get; set; }
    /// <summary>닫힌 경계인지 나타냅니다.</summary>
    public bool Closed { get; set; }
    /// <summary>U/V가 번갈아 저장된 평면 배열입니다.</summary>
    public double[] Points { get; set; }
}

/// <summary>작업 좌표계 Native context의 테스트 결과입니다.</summary>
public sealed class WorkingSurfaceContext
{
    /// <summary>테스트에서 발급한 context ID입니다.</summary>
    public long Id { get; set; }

    /// <summary>context가 참조하는 모델 revision입니다.</summary>
    public long Revision { get; set; }

    /// <summary>원점/X/Y/N 순서의 frame 배열입니다.</summary>
    public double[] Frame { get; set; }
}

/// <summary>작업 좌표계 표시 저장점의 테스트 식별자입니다.</summary>
public sealed class WorkingCoordinateSystemPresentation
{
    /// <summary>저장점 ID입니다.</summary>
    public long Id { get; set; }
}

/// <summary>투영 경로의 Native 측정값을 테스트에서 표현합니다.</summary>
public sealed class HeatingPathSegmentResult
{
    /// <summary>측정된 곡선 길이(mm)입니다.</summary>
    public double Length { get; set; }

    /// <summary>측정된 곡선 시작점의 XYZ 배열입니다.</summary>
    public double[] Start { get; set; }

    /// <summary>측정된 곡선 끝점의 XYZ 배열입니다.</summary>
    public double[] End { get; set; }

    /// <summary>최종 곡선을 따라 순서대로 얻은 평탄화 XYZ 표본입니다.</summary>
    public double[] Samples { get; set; }

    /// <summary>표본 생성에 요청한 최대 편차(mm)입니다.</summary>
    public double RequestedDeflectionMm { get; set; }

    /// <summary>표본 생성에 요청한 접선 각도 한도(rad)입니다.</summary>
    public double RequestedAngularToleranceRad { get; set; }

    /// <summary>곡선 내부에서 측정한 최대 현 편차(mm)입니다.</summary>
    public double MaximumMeasuredDeviationMm { get; set; }

    /// <summary>연속 표본 사이에서 측정한 최대 거리(mm)입니다.</summary>
    public double MaximumSampleSpacingMm { get; set; }

    /// <summary>곡선 내부에서 수행한 근사 확인 횟수입니다.</summary>
    public int ProbeCount { get; set; }

    /// <summary>요청한 근사 한도를 만족했는지 나타냅니다.</summary>
    public bool ApproximationComplete { get; set; }

    /// <summary>Native 표본 근사 알고리즘의 버전입니다.</summary>
    public string ApproximationRuleVersion { get; set; }
}

/// <summary>OCCT 투영 입력을 기록하기 위한 Native 곡선 데이터 대역입니다.</summary>
public sealed class CurveDefinitionData
{
    /// <summary>곡선 종류입니다.</summary>
    public string Kind { get; set; }

    /// <summary>곡선 진행 방향입니다.</summary>
    public string Direction { get; set; }

    /// <summary>직선 시작점입니다.</summary>
    public double[] Start { get; set; }

    /// <summary>직선 끝점입니다.</summary>
    public double[] End { get; set; }

    /// <summary>다중선 점을 평탄화한 XYZ 배열입니다.</summary>
    public double[] Points { get; set; }

    /// <summary>원호 좌표 프레임입니다.</summary>
    public double[] Frame { get; set; }

    /// <summary>원호 중심입니다.</summary>
    public double[] Center { get; set; }

    /// <summary>원호 반지름(mm)입니다.</summary>
    public double RadiusMm { get; set; }

    /// <summary>원호 시작각(라디안)입니다.</summary>
    public double StartAngleRad { get; set; }

    /// <summary>원호 끝각(라디안)입니다.</summary>
    public double EndAngleRad { get; set; }

    /// <summary>B-spline 차수입니다.</summary>
    public int Degree { get; set; }

    /// <summary>B-spline 극점입니다.</summary>
    public double[] Poles { get; set; }

    /// <summary>B-spline 가중치입니다.</summary>
    public double[] Weights { get; set; }

    /// <summary>B-spline knot입니다.</summary>
    public double[] Knots { get; set; }

    /// <summary>B-spline knot 중복도입니다.</summary>
    public int[] Multiplicities { get; set; }

    /// <summary>B-spline 주기 여부입니다.</summary>
    public bool IsPeriodic { get; set; }

    /// <summary>B-spline 시작 trim 매개변수입니다.</summary>
    public double TrimStart { get; set; }

    /// <summary>B-spline 끝 trim 매개변수입니다.</summary>
    public double TrimEnd { get; set; }
}

/// <summary>정사영 DXF 테스트에서 사용할 Native 모서리 대역입니다.</summary>
public sealed class HlrPolyline
{
    /// <summary>정사영 평면에 평탄화한 점 목록입니다.</summary>
    public double[] Points { get; set; } = Array.Empty<double>();

    /// <summary>가시 모서리인지 나타냅니다.</summary>
    public bool Visible { get; set; }
}

/// <summary>테스트용 작업면 first-hit 상태입니다.</summary>
public enum WorkingSurfaceQueryStatus
{
    Hit,
    Miss,
    Tangent,
    InvalidInput,
    StaleContext,
    GeometryFailure,
    Unknown
}

/// <summary>테스트용 작업면 first-hit 자료입니다.</summary>
public sealed class WorkingSurfaceHit
{
    /// <summary>질의 결과 상태입니다.</summary>
    public WorkingSurfaceQueryStatus Status { get; set; }
    /// <summary>first-hit한 Native Face 번호입니다.</summary>
    public int FaceId { get; set; }
    /// <summary>교점 모델 좌표입니다.</summary>
    public double[] Point { get; set; }
    /// <summary>교점 표면 법선입니다.</summary>
    public double[] Normal { get; set; }
    /// <summary>교점 원본 Face의 UV 좌표입니다.</summary>
    public double[] FaceUv { get; set; }
}

/// <summary>테스트용 공유 Edge 인접 질의 상태입니다.</summary>
public enum WorkingFaceAdjacencyStatus
{
    Adjacent,
    NotAdjacent,
    InvalidFace,
    StaleContext,
    GeometryFailure,
    Unknown
}

/// <summary>테스트용 선택 Face topology 질의 상태입니다.</summary>
public enum WorkingTopologyQueryStatus
{
    Success,
    InvalidInput,
    InvalidFace,
    StaleContext,
    InvalidTopology,
    GeometryFailure,
    Unknown
}

/// <summary>테스트용 Native 경계 루프입니다.</summary>
public sealed class WorkingTopologyBoundaryLoop
{
    /// <summary>닫힌 루프의 평탄화 XYZ 좌표입니다.</summary>
    public double[] Points { get; set; } = Array.Empty<double>();
    /// <summary>각 구간의 원본 Native Face 번호입니다.</summary>
    public int[] SegmentFaceIndices { get; set; } = Array.Empty<int>();
    /// <summary>루프가 닫혔는지 나타냅니다.</summary>
    public bool IsClosed { get; set; }
}

/// <summary>테스트용 연결 topology 성분입니다.</summary>
public sealed class WorkingTopologyComponent
{
    /// <summary>성분의 Native Face 번호입니다.</summary>
    public int[] FaceIndices { get; set; } = Array.Empty<int>();
    /// <summary>성분의 경계 루프입니다.</summary>
    public WorkingTopologyBoundaryLoop[] BoundaryLoops { get; set; } = Array.Empty<WorkingTopologyBoundaryLoop>();
}

/// <summary>테스트용 Face 인접 쌍입니다.</summary>
public sealed class WorkingTopologyAdjacency
{
    /// <summary>첫 번째 Native Face 번호입니다.</summary>
    public int FirstFaceIndex { get; set; }
    /// <summary>두 번째 Native Face 번호입니다.</summary>
    public int SecondFaceIndex { get; set; }
}

/// <summary>테스트용 선택 Face topology 결과입니다.</summary>
public sealed class WorkingSelectedTopology
{
    /// <summary>topology 질의 상태입니다.</summary>
    public WorkingTopologyQueryStatus Status { get; set; }
    /// <summary>연결 성분 목록입니다.</summary>
    public WorkingTopologyComponent[] Components { get; set; } = Array.Empty<WorkingTopologyComponent>();
    /// <summary>공유 Edge 인접 관계입니다.</summary>
    public WorkingTopologyAdjacency[] Adjacencies { get; set; } = Array.Empty<WorkingTopologyAdjacency>();
}

/// <summary>테스트용 표면 요소 질의 상태입니다.</summary>
public enum WorkingSurfaceElementQueryStatus
{
    Success,
    InvalidInput,
    InvalidFace,
    StaleContext,
    InvalidRegion,
    ElementBudgetExceeded,
    GeometryFailure,
    Unknown
}

/// <summary>테스트용 표면 요소 영역 루프입니다.</summary>
public sealed class WorkingRegionLoopData
{
    /// <summary>유효 영역의 홀인지 나타냅니다.</summary>
    public bool IsHole { get; set; }
    /// <summary>평탄화 작업 평면 좌표입니다.</summary>
    public double[] Points { get; set; } = Array.Empty<double>();
}

/// <summary>테스트용 삼각 표면 요소입니다.</summary>
public sealed class WorkingSurfaceElementData
{
    /// <summary>Native Face 번호입니다.</summary>
    public int FaceIndex { get; set; }
    /// <summary>삼각 요소의 실면적입니다.</summary>
    public double SurfaceAreaMm2 { get; set; }
    /// <summary>모델 좌표 중심입니다.</summary>
    public double[] ModelCentroid { get; set; } = Array.Empty<double>();
    /// <summary>원본 Face UV 중심입니다.</summary>
    public double[] FaceUvCentroid { get; set; } = Array.Empty<double>();
    /// <summary>작업 평면 UV 중심입니다.</summary>
    public double[] WorkingUvCentroid { get; set; } = Array.Empty<double>();
    /// <summary>삼각 요소의 평탄화 XYZ 좌표입니다.</summary>
    public double[] ModelVertices { get; set; } = Array.Empty<double>();
    /// <summary>곡면 근사 편차입니다.</summary>
    public double MaximumMeasuredDeviationMm { get; set; }
}

/// <summary>테스트용 표면 요소 목록과 면적 오차입니다.</summary>
public sealed class WorkingSurfaceElementsResult
{
    /// <summary>표면 요소 질의 상태입니다.</summary>
    public WorkingSurfaceElementQueryStatus Status { get; set; }
    /// <summary>표면 삼각 요소입니다.</summary>
    public WorkingSurfaceElementData[] Elements { get; set; } = Array.Empty<WorkingSurfaceElementData>();
    /// <summary>면적 오차 상한입니다.</summary>
    public double AreaErrorBoundMm2 { get; set; }
}

/// <summary>테스트용 Native 곡선 간격 질의 상태입니다.</summary>
public enum WorkingCurveClearanceStatus
{
    Success,
    InvalidInput,
    StaleContext,
    CandidateNotFound,
    PairBudgetExceeded,
    GeometryFailure,
    Unresolved,
    Unknown
}

/// <summary>테스트용 곡선 최단거리, 위치와 오차 결과입니다.</summary>
public sealed class CurveClearanceResultData
{
    /// <summary>Native 곡선 간격 질의 상태입니다.</summary>
    public WorkingCurveClearanceStatus Status { get; set; }
    /// <summary>중심선 사이의 최소 거리(mm)입니다.</summary>
    public double MinimumCenterlineDistanceMm { get; set; }
    /// <summary>첫 번째 곡선의 최단 위치입니다.</summary>
    public double[] FirstLocation { get; set; } = Array.Empty<double>();
    /// <summary>두 번째 곡선의 최단 위치입니다.</summary>
    public double[] SecondLocation { get; set; } = Array.Empty<double>();
    /// <summary>최단거리의 최대 오차입니다.</summary>
    public double ErrorBoundMm { get; set; }
    /// <summary>미확정 또는 실패 원인입니다.</summary>
    public string Diagnostic { get; set; }
}

/// <summary>테스트용 Native 곡선 깊이 envelope 질의 상태입니다.</summary>
public enum WorkingCurveDepthEnvelopeStatus
{
    Success,
    InvalidInput,
    InvalidFace,
    CandidateNotFound,
    StaleContext,
    Miss,
    Tangent,
    ProbeBudgetExceeded,
    GeometryFailure,
    Unresolved,
    Unknown
}

/// <summary>테스트용 곡선 깊이 범위와 위치 결과입니다.</summary>
public sealed class CurveDepthEnvelopeResultData
{
    /// <summary>Native 깊이 envelope 질의 상태입니다.</summary>
    public WorkingCurveDepthEnvelopeStatus Status { get; set; }
    /// <summary>최소·최대 깊이의 중간값입니다.</summary>
    public double MeasuredInsertionDepthMm { get; set; }
    /// <summary>곡선 표본의 최소 깊이입니다.</summary>
    public double MinimumInsertionDepthMm { get; set; }
    /// <summary>곡선 표본의 최대 깊이입니다.</summary>
    public double MaximumInsertionDepthMm { get; set; }
    /// <summary>깊이 범위의 최대 오차입니다.</summary>
    public double ErrorBoundMm { get; set; }
    /// <summary>요구 깊이에서 가장 먼 표면 점입니다.</summary>
    public double[] SurfacePoint { get; set; } = Array.Empty<double>();
    /// <summary>최소 깊이 표본의 표면 점입니다.</summary>
    public double[] MinimumSurfacePoint { get; set; } = Array.Empty<double>();
    /// <summary>최대 깊이 표본의 표면 점입니다.</summary>
    public double[] MaximumSurfacePoint { get; set; } = Array.Empty<double>();
    /// <summary>최소 깊이 표본의 도체 바깥점입니다.</summary>
    public double[] MinimumWireOutsidePoint { get; set; } = Array.Empty<double>();
    /// <summary>최대 깊이 표본의 도체 바깥점입니다.</summary>
    public double[] MaximumWireOutsidePoint { get; set; } = Array.Empty<double>();
    /// <summary>앵커 확인을 포함한 깊이 표본 수입니다.</summary>
    public int ProbeCount { get; set; }
    /// <summary>실패 또는 미확정 사유입니다.</summary>
    public string Diagnostic { get; set; }
}

/// <summary>테스트용 Native 삽입 깊이 보정 결과입니다.</summary>
public sealed class CurveDepthAdjustmentResultData
{
    /// <summary>Native 깊이 보정 상태입니다.</summary>
    public WorkingCurveDepthEnvelopeStatus Status { get; set; }
    /// <summary>보정된 중심선의 모델 좌표 XYZ를 나열합니다.</summary>
    public double[] CenterlineSamples { get; set; } = Array.Empty<double>();
    /// <summary>최대 요구 깊이 잔차(mm)입니다.</summary>
    public double MaximumDepthResidualMm { get; set; }
    /// <summary>보정 결과의 최대 깊이 오차(mm)입니다.</summary>
    public double ErrorBoundMm { get; set; }
    /// <summary>앵커와 경로 표면을 확인한 표본 수입니다.</summary>
    public int ProbeCount { get; set; }
    /// <summary>Native 실패 또는 미확정 진단입니다.</summary>
    public string Diagnostic { get; set; }
}

/// <summary>테스트용 Native 연결 경로 충돌 질의 상태입니다.</summary>
public enum WorkingConnectionObstacleStatus
{
    Clear,
    Obstructed,
    InvalidInput,
    StaleContext,
    CandidateNotFound,
    GeometryFailure,
    Unresolved,
    Unknown
}

/// <summary>테스트용 연결 경로 충돌 위치와 경로 거리 결과입니다.</summary>
public sealed class ConnectionObstacleResultData
{
    /// <summary>Native 연결 경로 질의 상태입니다.</summary>
    public WorkingConnectionObstacleStatus Status { get; set; }
    /// <summary>첫 충돌 위치입니다.</summary>
    public double[] FirstObstruction { get; set; } = Array.Empty<double>();
    /// <summary>경로 시작점부터 충돌까지의 거리(mm)입니다.</summary>
    public double DistanceToObstructionMm { get; set; }
    /// <summary>Native 미확정 또는 실패 사유입니다.</summary>
    public string Diagnostic { get; set; }
}

/// <summary>App 상태 전이 테스트가 사용하는 결정적 Native 브리지 대역입니다.</summary>
public static class Bridge
{
    private static long _nextContextId;
    private static long _nextPresentationId;

    /// <summary>투영 경로 호출에 사용할 Native 결과입니다.</summary>
    public static HeatingPathSegmentResult NextProjectedPathResult { get; set; }

    /// <summary>투영 staging에서 Native null 결과를 반환할지 지정합니다.</summary>
    public static bool ProjectedPathStageShouldFail { get; set; }

    /// <summary>마지막 투영 호출의 context ID입니다.</summary>
    public static long LastProjectionContextId { get; private set; }

    /// <summary>마지막 투영 호출의 모델 revision입니다.</summary>
    public static long LastProjectionRevision { get; private set; }

    /// <summary>마지막 투영 호출의 staging ID입니다.</summary>
    public static int LastProjectionStagingId { get; private set; }

    /// <summary>마지막 투영 호출의 편집 경로 종류입니다.</summary>
    public static string LastProjectedPathKind { get; private set; }

    /// <summary>마지막 투영 호출의 곡선 정의입니다.</summary>
    public static CurveDefinitionData LastCurveDefinition { get; private set; }

    /// <summary>마지막 투영 호출의 평탄화 점 배열입니다.</summary>
    public static double[] LastProjectedPoints { get; private set; }

    /// <summary>마지막 투영 호출의 Native Face 번호입니다.</summary>
    public static int[] LastProjectedFaceIndices { get; private set; }

    /// <summary>마지막 투영 호출의 방향 벡터입니다.</summary>
    public static double[] LastProjectionDirection { get; private set; }

    /// <summary>마지막 확정 호출의 staging 경로 ID입니다.</summary>
    public static int[] LastStagedSegmentIds { get; private set; }

    /// <summary>마지막 확정 호출의 최종 경로 ID입니다.</summary>
    public static int[] LastFinalSegmentIds { get; private set; }

    /// <summary>마지막 확정 호출의 독립 다중선 ID입니다.</summary>
    public static int[] LastStagedPolylineIds { get; private set; }

    /// <summary>확정 호출의 성공 여부를 지정합니다.</summary>
    public static bool ProjectionCommitResult { get; set; } = true;

    /// <summary>투영 경로 확정 호출 횟수입니다.</summary>
    public static int ProjectionCommitCount { get; private set; }

    /// <summary>투영 후보 폐기 호출 횟수입니다.</summary>
    public static int ProjectionDiscardCount { get; private set; }

    /// <summary>후보 경로 staging에 전달된 Native 후보 ID입니다.</summary>
    public static long LastPreparedCandidatePathCandidateId { get; private set; }

    /// <summary>후보 곡선 표본에 전달된 근사 허용값입니다.</summary>
    public static double[] LastCandidateApproximationSettings { get; private set; }

    /// <summary>재투영하지 않는 후보 곡선 staging 호출 횟수입니다.</summary>
    public static int PreparedCandidateExactCurveStageCount { get; private set; }

    /// <summary>확정 뒤 회전 구간 가시성을 설정한 Native ID와 활성 상태입니다.</summary>
    public static List<(int SegmentId, bool IsActive)> TurnSegmentEnabledCalls { get; } = new();

    /// <summary>후보 경로 staging에 전달된 작업 context ID입니다.</summary>
    public static long LastPreparedCandidatePathContextId { get; private set; }

    /// <summary>후보 경로 확정에 사용한 staging ID입니다.</summary>
    public static int[] LastPreparedCandidatePathStagedIds { get; private set; }

    /// <summary>후보 경로 확정에 사용한 최종 ID입니다.</summary>
    public static int[] LastPreparedCandidatePathFinalIds { get; private set; }

    /// <summary>후보 경로 확정이 성공할지 지정합니다.</summary>
    public static bool PreparedCandidatePathCommitResult { get; set; } = true;

    /// <summary>후보 경로 확정 호출 횟수입니다.</summary>
    public static int PreparedCandidatePathCommitCount { get; private set; }

    /// <summary>후보 경로 폐기 호출 횟수입니다.</summary>
    public static int PreparedCandidatePathDiscardCount { get; private set; }

    /// <summary>다음 STEP 후보 준비 호출의 Native 결과입니다.</summary>
    public static ModelLoadCandidateResult NextPreparedStepCandidateResult { get; set; }

    /// <summary>다음 STEP 후보 publish 호출의 Native 결과입니다.</summary>
    public static ModelLoadResult NextPreparedStepPublishResult { get; set; }

    /// <summary>현재 테스트 Native 문서의 model revision입니다.</summary>
    public static long CurrentModelRevision { get; private set; } = 1;

    /// <summary>마지막 후보 준비 호출의 STEP 경로입니다.</summary>
    public static string LastPreparedStepCandidatePath { get; private set; }

    /// <summary>마지막 후보 publish 호출의 ID입니다.</summary>
    public static long LastPublishedStepCandidateId { get; private set; }

    /// <summary>후보 폐기 호출에서 전달한 ID 목록입니다.</summary>
    public static List<long> DiscardedStepCandidateIds { get; } = new();

    /// <summary>연결 경로 삭제 호출 횟수입니다.</summary>
    public static int ClearConnectionPathsCount { get; private set; }

    /// <summary>연결 경로 삭제 당시의 경로 확정 호출 횟수입니다.</summary>
    public static int ProjectionCommitCountWhenConnectionPathsCleared { get; private set; }

    /// <summary>연결 경로 미리보기 삭제 호출 횟수입니다.</summary>
    public static int ClearConnectionPathPreviewCount { get; private set; }

    /// <summary>제어점 편집 미리보기 삭제 호출 횟수입니다.</summary>
    public static int ClearControlPointEditPreviewCount { get; private set; }

    /// <summary>제어점 미리보기를 기록한 뒤 표시 실패를 반환할지 정합니다.</summary>
    public static bool FailControlPointEditPreviewAfterSetting { get; set; }

    /// <summary>품질 표본 위치 표시 호출에서 전달된 좌표입니다.</summary>
    public static double[] LastQualityIssueLocation { get; private set; }

    /// <summary>품질 표본 위치 표시 호출 횟수입니다.</summary>
    public static int ShowQualityIssueLocationCount { get; private set; }

    /// <summary>품질 표본 위치 삭제 호출 횟수입니다.</summary>
    public static int ClearQualityIssueLocationCount { get; private set; }

    /// <summary>마지막 제어점 편집 미리보기의 조각별 표본 좌표입니다.</summary>
    public static double[][] ControlPointEditPreviewPolylines { get; private set; } = Array.Empty<double[]>();

    /// <summary>다음 STEP 열기 호출에서 사용할 결과입니다.</summary>
    public static ModelLoadResult NextModelLoadResult { get; set; }

    /// <summary>현재 모델의 테스트 Face 수입니다.</summary>
    public static ShapeInfo ShapeInfo { get; set; }

    /// <summary>테스트에서 덮어쓸 Native Face 특징 결과입니다.</summary>
    public static FaceGeometryFeaturesResult FaceGeometryFeatures { get; set; }

    /// <summary>테스트에서 덮어쓸 Native 준비 후보 Face 특징 결과입니다.</summary>
    public static FaceGeometryFeaturesResult PreparedCandidateFaceGeometryFeatures { get; set; }

    /// <summary>테스트에서 덮어쓸 Native 준비 후보 first-hit 결과입니다.</summary>
    public static WorkingSurfaceHit PreparedCandidateWorkingSurfaceHit { get; set; }

    /// <summary>테스트에서 반환할 후보 작업 좌표계 context입니다.</summary>
    public static WorkingSurfaceContext PreparedCandidateWorkingSurfaceContext { get; set; }

    /// <summary>후보 작업 좌표계 생성에 전달된 후보 ID입니다.</summary>
    public static long LastPreparedCandidateContextCreationCandidateId { get; private set; }

    /// <summary>후보 작업 좌표계 생성에 전달된 법선과 X축 후보입니다.</summary>
    public static double[] LastPreparedCandidateContextCreationAxes { get; private set; }

    /// <summary>해제된 후보 작업 context와 소속 후보 ID입니다.</summary>
    public static List<(long CandidateId, long ContextId)> ReleasedPreparedCandidateContexts { get; } = new();

    /// <summary>후보 first-hit API에 전달된 후보 ID입니다.</summary>
    public static long LastPreparedCandidateWorkingSurfaceCandidateId { get; private set; }

    /// <summary>후보 first-hit API에 전달된 작업 context ID입니다.</summary>
    public static long LastPreparedCandidateWorkingSurfaceContextId { get; private set; }

    /// <summary>후보 first-hit API에 전달된 모델 revision입니다.</summary>
    public static long LastPreparedCandidateWorkingSurfaceRevision { get; private set; }

    /// <summary>후보 first-hit API에 전달된 작업 평면 좌표입니다.</summary>
    public static double[] LastPreparedCandidateWorkingSurfaceUv { get; private set; }

    /// <summary>활성 문서의 작업면·topology 질의를 호출한 횟수입니다.</summary>
    public static int ActiveWorkingGeometryQueryCount { get; private set; }

    /// <summary>후보 인접 질의에 전달된 후보 ID입니다.</summary>
    public static long LastPreparedCandidateAdjacencyQueryId { get; private set; }

    /// <summary>후보 topology 질의에 전달된 후보 ID입니다.</summary>
    public static long LastPreparedCandidateTopologyQueryId { get; private set; }

    /// <summary>후보 표면 요소 질의에 전달된 후보 ID입니다.</summary>
    public static long LastPreparedCandidateSurfaceElementsQueryId { get; private set; }

    /// <summary>테스트에서 반환할 후보 인접 상태입니다.</summary>
    public static WorkingFaceAdjacencyStatus PreparedCandidateAdjacencyStatus { get; set; }

    /// <summary>테스트에서 반환할 후보 topology 자료입니다.</summary>
    public static WorkingSelectedTopology PreparedCandidateSelectedTopology { get; set; }

    /// <summary>테스트에서 반환할 후보 표면 요소 자료입니다.</summary>
    public static WorkingSurfaceElementsResult PreparedCandidateSurfaceElements { get; set; }

    /// <summary>테스트에서 반환할 후보 곡선 간격 결과입니다.</summary>
    public static CurveClearanceResultData PreparedCandidateCurveClearanceResult { get; set; }

    /// <summary>테스트에서 반환할 활성 문서 곡선 간격 결과입니다.</summary>
    public static CurveClearanceResultData ActiveCurveClearanceResult { get; set; }

    /// <summary>테스트에서 반환할 활성 문서 깊이 envelope 결과입니다.</summary>
    public static CurveDepthEnvelopeResultData ActiveCurveDepthEnvelopeResult { get; set; }

    /// <summary>테스트에서 반환할 준비 후보 깊이 envelope 결과입니다.</summary>
    public static CurveDepthEnvelopeResultData PreparedCandidateCurveDepthEnvelopeResult { get; set; }

    /// <summary>마지막 Native 깊이 envelope 호출에 전달한 입력입니다.</summary>
    public static double[] LastDepthEnvelopeSamples { get; private set; }

    /// <summary>마지막 Native 깊이 envelope 호출의 선택 Face 번호입니다.</summary>
    public static int[] LastDepthEnvelopeFaceIndices { get; private set; }

    /// <summary>마지막 Native 깊이 envelope 호출의 좌표계 문맥입니다.</summary>
    public static long[] LastDepthEnvelopeContext { get; private set; }

    /// <summary>마지막 Native 깊이 envelope 호출의 앵커 좌표입니다.</summary>
    public static double[] LastDepthEnvelopeAnchor { get; private set; }

    /// <summary>마지막 Native 깊이 envelope 호출의 깊이 설정입니다.</summary>
    public static double[] LastDepthEnvelopeSettings { get; private set; }

    /// <summary>테스트에서 반환할 활성 깊이 보정 결과입니다.</summary>
    public static CurveDepthAdjustmentResultData ActiveCurveDepthAdjustmentResult { get; set; }

    /// <summary>테스트에서 반환할 후보 깊이 보정 결과입니다.</summary>
    public static CurveDepthAdjustmentResultData PreparedCandidateCurveDepthAdjustmentResult { get; set; }

    /// <summary>마지막 깊이 보정의 작업 평면 표본입니다.</summary>
    public static double[] LastDepthAdjustmentSamples { get; private set; }

    /// <summary>마지막 깊이 보정의 선택 Native Face 번호입니다.</summary>
    public static int[] LastDepthAdjustmentFaceIndices { get; private set; }

    /// <summary>마지막 깊이 보정의 좌표계 문맥입니다.</summary>
    public static long[] LastDepthAdjustmentContext { get; private set; }

    /// <summary>마지막 깊이 보정의 앵커 Face·UV·모델 좌표입니다.</summary>
    public static double[] LastDepthAdjustmentAnchor { get; private set; }

    /// <summary>마지막 깊이 보정의 치수·오차·작업 한도입니다.</summary>
    public static double[] LastDepthAdjustmentSettings { get; private set; }

    /// <summary>마지막 후보 깊이 보정의 후보 ID입니다.</summary>
    public static long LastPreparedCandidateDepthAdjustmentId { get; private set; }

    /// <summary>마지막 후보 깊이 envelope 호출의 후보 ID입니다.</summary>
    public static long LastPreparedCandidateDepthEnvelopeId { get; private set; }

    /// <summary>테스트에서 반환할 활성 연결 경로 충돌 결과입니다.</summary>
    public static ConnectionObstacleResultData ActiveConnectionObstacleResult { get; set; }

    /// <summary>테스트에서 반환할 후보 연결 경로 충돌 결과입니다.</summary>
    public static ConnectionObstacleResultData PreparedCandidateConnectionObstacleResult { get; set; }

    /// <summary>마지막 연결 충돌 질의에 전달한 exact 곡선 정의입니다.</summary>
    public static CurveDefinitionData[] LastConnectionObstacleCurves { get; private set; }

    /// <summary>마지막 연결 충돌 질의의 금형 이탈 구간 번호입니다.</summary>
    public static int LastConnectionObstacleTransitionIndex { get; private set; }

    /// <summary>마지막 연결 충돌 질의에 전달한 거리 공차(mm)입니다.</summary>
    public static double LastConnectionObstacleToleranceMm { get; private set; }

    /// <summary>마지막 연결 충돌 질의의 context와 revision입니다.</summary>
    public static long[] LastConnectionObstacleContext { get; private set; }

    /// <summary>마지막 후보 연결 충돌 질의의 후보 ID입니다.</summary>
    public static long LastPreparedCandidateConnectionObstacleId { get; private set; }

    /// <summary>마지막 곡선 간격 질의에 전달된 곡선 정의입니다.</summary>
    public static CurveDefinitionData[] LastCurveClearanceDefinitions { get; private set; }

    /// <summary>마지막 곡선 간격 질의에 전달된 연속 구간 곡선 인덱스 쌍입니다.</summary>
    public static int[] LastCurveClearanceLocalPairs { get; private set; }

    /// <summary>마지막 곡선 간격 질의의 수치 설정입니다.</summary>
    public static double[] LastCurveClearanceSettings { get; private set; }

    /// <summary>마지막 후보 곡선 간격 질의의 후보 ID입니다.</summary>
    public static long LastPreparedCandidateCurveClearanceId { get; private set; }

    /// <summary>마지막 곡선 간격 질의의 좌표계 ID와 revision입니다.</summary>
    public static long[] LastCurveClearanceContext { get; private set; }

    /// <summary>마지막으로 후보 범위 Face 조회에 사용한 ID입니다.</summary>
    public static long LastPreparedCandidateFaceQueryId { get; private set; }

    /// <summary>마지막 후보 범위 UV 경계 조회에 사용한 ID입니다.</summary>
    public static long LastPreparedCandidateBoundaryQueryId { get; private set; }

    /// <summary>마지막 후보 범위 UV 표본 조회에 사용한 ID입니다.</summary>
    public static long LastPreparedCandidateSampleQueryId { get; private set; }

    /// <summary>현재 문서 특징 API를 호출한 횟수입니다.</summary>
    public static int ActiveFaceFeatureQueryCount { get; private set; }

    /// <summary>해제된 작업 context ID입니다.</summary>
    public static List<long> ReleasedContextIds { get; } = new();

    /// <summary>표시 저장점을 복원한 횟수입니다.</summary>
    public static int RestoredPresentationCount { get; private set; }

    /// <summary>Face 강조를 숨긴 횟수입니다.</summary>
    public static int HideFaceHighlightsCount { get; private set; }

    /// <summary>확정 오버레이를 지운 횟수입니다.</summary>
    public static int ClearOverlaysCount { get; private set; }

    /// <summary>제외영역 오버레이를 지운 횟수입니다.</summary>
    public static int ClearExclusionOverlaysCount { get; private set; }

    /// <summary>Native 문서와 Viewer를 종료한 횟수입니다.</summary>
    public static int ShutdownCount { get; private set; }

    /// <summary>각 테스트에 사용할 기본 Native 상태로 되돌립니다.</summary>
    public static void Reset()
    {
        _nextContextId = 500;
        _nextPresentationId = 900;
        NextModelLoadResult = null;
        ShapeInfo = new ShapeInfo();
        FaceGeometryFeatures = null;
        PreparedCandidateFaceGeometryFeatures = null;
        PreparedCandidateWorkingSurfaceHit = null;
        PreparedCandidateWorkingSurfaceContext = null;
        LastPreparedCandidateContextCreationCandidateId = 0;
        LastPreparedCandidateContextCreationAxes = null;
        ReleasedPreparedCandidateContexts.Clear();
        LastPreparedCandidateWorkingSurfaceCandidateId = 0;
        LastPreparedCandidateWorkingSurfaceContextId = 0;
        LastPreparedCandidateWorkingSurfaceRevision = 0;
        LastPreparedCandidateWorkingSurfaceUv = null;
        ActiveWorkingGeometryQueryCount = 0;
        LastPreparedCandidateAdjacencyQueryId = 0;
        LastPreparedCandidateTopologyQueryId = 0;
        LastPreparedCandidateSurfaceElementsQueryId = 0;
        PreparedCandidateAdjacencyStatus = WorkingFaceAdjacencyStatus.Unknown;
        PreparedCandidateSelectedTopology = null;
        PreparedCandidateSurfaceElements = null;
        PreparedCandidateCurveClearanceResult = null;
        ActiveCurveClearanceResult = null;
        ActiveCurveDepthEnvelopeResult = null;
        PreparedCandidateCurveDepthEnvelopeResult = null;
        LastDepthEnvelopeSamples = null;
        LastDepthEnvelopeFaceIndices = null;
        LastDepthEnvelopeContext = null;
        LastDepthEnvelopeAnchor = null;
        LastDepthEnvelopeSettings = null;
        LastPreparedCandidateDepthEnvelopeId = 0;
        ActiveCurveDepthAdjustmentResult = null;
        PreparedCandidateCurveDepthAdjustmentResult = null;
        LastDepthAdjustmentSamples = null;
        LastDepthAdjustmentFaceIndices = null;
        LastDepthAdjustmentContext = null;
        LastDepthAdjustmentAnchor = null;
        LastDepthAdjustmentSettings = null;
        LastPreparedCandidateDepthAdjustmentId = 0;
        ActiveConnectionObstacleResult = null;
        PreparedCandidateConnectionObstacleResult = null;
        LastConnectionObstacleCurves = null;
        LastConnectionObstacleTransitionIndex = -1;
        LastConnectionObstacleToleranceMm = double.NaN;
        LastConnectionObstacleContext = null;
        LastPreparedCandidateConnectionObstacleId = 0;
        LastCurveClearanceDefinitions = null;
        LastCurveClearanceLocalPairs = null;
        LastCurveClearanceSettings = null;
        LastPreparedCandidateCurveClearanceId = 0;
        LastCurveClearanceContext = null;
        LastPreparedCandidateFaceQueryId = 0;
        LastPreparedCandidateBoundaryQueryId = 0;
        LastPreparedCandidateSampleQueryId = 0;
        ActiveFaceFeatureQueryCount = 0;
        ReleasedContextIds.Clear();
        RestoredPresentationCount = 0;
        HideFaceHighlightsCount = 0;
        ClearOverlaysCount = 0;
        ClearExclusionOverlaysCount = 0;
        ShutdownCount = 0;
        NextProjectedPathResult = null;
        ProjectedPathStageShouldFail = false;
        LastProjectionContextId = 0;
        LastProjectionRevision = 0;
        LastProjectionStagingId = 0;
        LastProjectedPathKind = null;
        LastCurveDefinition = null;
        LastProjectedPoints = null;
        LastProjectedFaceIndices = null;
        LastProjectionDirection = null;
        LastStagedSegmentIds = null;
        LastFinalSegmentIds = null;
        LastStagedPolylineIds = null;
        ProjectionCommitResult = true;
        ProjectionCommitCount = 0;
        ProjectionDiscardCount = 0;
        LastPreparedCandidatePathCandidateId = 0;
        LastCandidateApproximationSettings = null;
        PreparedCandidateExactCurveStageCount = 0;
        TurnSegmentEnabledCalls.Clear();
        LastPreparedCandidatePathContextId = 0;
        LastPreparedCandidatePathStagedIds = null;
        LastPreparedCandidatePathFinalIds = null;
        PreparedCandidatePathCommitResult = true;
        PreparedCandidatePathCommitCount = 0;
        PreparedCandidatePathDiscardCount = 0;
        NextPreparedStepCandidateResult = null;
        NextPreparedStepPublishResult = null;
        CurrentModelRevision = 1;
        LastPreparedStepCandidatePath = null;
        LastPublishedStepCandidateId = 0;
        DiscardedStepCandidateIds.Clear();
        ClearConnectionPathsCount = 0;
        ProjectionCommitCountWhenConnectionPathsCleared = -1;
        ClearConnectionPathPreviewCount = 0;
        ClearControlPointEditPreviewCount = 0;
        FailControlPointEditPreviewAfterSetting = false;
        LastQualityIssueLocation = null;
        ShowQualityIssueLocationCount = 0;
        ClearQualityIssueLocationCount = 0;
        ControlPointEditPreviewPolylines = Array.Empty<double[]>();
    }

    /// <summary>테스트에서 단순한 STEP 경로를 상태형 결과로 반환합니다.</summary>
    public static ModelLoadResult LoadStepDetailed(string path)
    {
        ModelLoadResult result = NextModelLoadResult ?? new ModelLoadResult
        {
            Status = ModelLoadStatus.Committed
        };
        NextModelLoadResult = null;
        return result;
    }

    /// <summary>STEP 후보 준비 호출을 기록하고 설정한 테스트 결과를 반환합니다.</summary>
    public static ModelLoadCandidateResult PrepareStepCandidate(string path)
    {
        LastPreparedStepCandidatePath = path;
        return NextPreparedStepCandidateResult;
    }

    /// <summary>STEP 후보 publish 호출을 기록하고 설정한 테스트 결과를 반환합니다.</summary>
    public static ModelLoadResult PublishStepCandidate(long candidateId)
    {
        LastPublishedStepCandidateId = candidateId;
        ModelLoadResult result = NextPreparedStepPublishResult ?? new ModelLoadResult
        {
            Status = ModelLoadStatus.Committed,
            ModelRevision = candidateId
        };
        if (result.Status != ModelLoadStatus.FailedBeforeCommit)
            CurrentModelRevision = result.ModelRevision;
        return result;
    }

    /// <summary>STEP 후보 폐기 호출의 ID를 테스트 상태에 기록합니다.</summary>
    public static bool DiscardStepCandidate(long candidateId)
    {
        DiscardedStepCandidateIds.Add(candidateId);
        return true;
    }

    /// <summary>테스트용 모델 표시색 호출을 받아들입니다.</summary>
    public static void SetShapeColor(double red, double green, double blue) { }

    /// <summary>뷰포트 초기화 호출을 받아들입니다.</summary>
    public static bool InitViewer(IntPtr hwnd, int width, int height) => true;

    /// <summary>테스트에서 Native 문서와 Viewer 종료 호출을 기록합니다.</summary>
    public static void Shutdown() => ShutdownCount++;

    /// <summary>뷰포트 크기 변경 호출을 받아들입니다.</summary>
    public static void ResizeView(int width, int height) { }

    /// <summary>뷰 이동 입력을 받아들입니다.</summary>
    public static void PanStart(int x, int y) { }

    /// <summary>뷰 이동 중 입력을 받아들입니다.</summary>
    public static void PanMove(int x, int y) { }

    /// <summary>뷰 이동 입력을 마칩니다.</summary>
    public static void PanEnd() { }

    /// <summary>면 강조 표시를 지웁니다.</summary>
    public static void ClearHover() { }

    /// <summary>뷰 회전 입력을 시작합니다.</summary>
    public static void RotateStart(int x, int y) { }

    /// <summary>뷰 회전 중 입력을 받아들입니다.</summary>
    public static void RotateMove(int x, int y) { }

    /// <summary>뷰 회전 입력을 마칩니다.</summary>
    public static void RotateEnd() { }

    /// <summary>Face 사각형 선택 표시를 갱신합니다.</summary>
    public static void SetSelectionRectangle(int x0, int y0, int x1, int y1) { }

    /// <summary>Face 사각형 선택 표시를 지웁니다.</summary>
    public static void ClearSelectionRectangle() { }

    /// <summary>Face 사각형 선택을 수행하지 않는 대역 결과를 반환합니다.</summary>
    public static int[] SelectFacesInRectangle(int x0, int y0, int x1, int y1, bool toggle) => Array.Empty<int>();

    /// <summary>회전 구간 피킹 결과가 없음을 반환합니다.</summary>
    public static int PickTurnSegment(int x, int y) => -1;

    /// <summary>발열 경로 구간 피킹 결과가 없음을 반환합니다.</summary>
    public static int PickHeatingPathSegment(int x, int y) => -1;

    /// <summary>Face hover 결과가 없음을 반환합니다.</summary>
    public static int HoverFace(int x, int y) => -1;

    /// <summary>Face 선택 결과가 없음을 반환합니다.</summary>
    public static int SelectFace(int x, int y, bool toggle) => -1;

    /// <summary>지정 위치에서 확대/축소 호출을 받아들입니다.</summary>
    public static void ZoomViewAtPoint(int x, int y, int amount) { }

    /// <summary>테스트용 제외영역 표시를 지웁니다.</summary>
    public static void ClearExclusionOverlays() => ClearExclusionOverlaysCount++;

    /// <summary>테스트용 확정 오버레이를 지웁니다.</summary>
    public static void ClearOverlays() => ClearOverlaysCount++;

    /// <summary>현재 테스트 형상 통계를 반환합니다.</summary>
    public static ShapeInfo GetShapeInfo() => ShapeInfo;

    /// <summary>테스트 모델의 전체 Face 면적·중심·인접 자료를 반환합니다.</summary>
    public static FaceGeometryFeaturesResult GetFaceGeometryFeatures()
    {
        ActiveFaceFeatureQueryCount++;
        return FaceGeometryFeatures ?? new FaceGeometryFeaturesResult
        {
            Status = FaceFeatureQueryStatus.Success,
            Features = Enumerable.Range(0, ShapeInfo.Faces)
                .Select(faceIndex => new FaceGeometryFeatureData
                {
                    FaceIndex = faceIndex,
                    SurfaceType = "Plane",
                    SurfaceAreaMm2 = 0.5,
                    Center = new[] { faceIndex * 3d + 0.5, 0.5, 0d },
                    AdjacentFaceIndices = Array.Empty<int>()
                })
                .ToArray()
        };
    }

    /// <summary>요청한 테스트 후보의 Face 특징 자료를 반환합니다.</summary>
    public static FaceGeometryFeaturesResult GetPreparedCandidateFaceGeometryFeatures(long candidateId)
    {
        LastPreparedCandidateFaceQueryId = candidateId;
        return PreparedCandidateFaceGeometryFeatures ?? new FaceGeometryFeaturesResult
        {
            Status = FaceFeatureQueryStatus.CandidateNotFound,
            Features = Array.Empty<FaceGeometryFeatureData>()
        };
    }

    /// <summary>요청된 후보와 작업 context를 기록하고 준비된 hit 결과를 반환합니다.</summary>
    public static WorkingSurfaceHit QueryPreparedCandidateWorkingSurface(
        long candidateId,
        long contextId,
        long revision,
        double u,
        double v)
    {
        LastPreparedCandidateWorkingSurfaceCandidateId = candidateId;
        LastPreparedCandidateWorkingSurfaceContextId = contextId;
        LastPreparedCandidateWorkingSurfaceRevision = revision;
        LastPreparedCandidateWorkingSurfaceUv = [u, v];
        return PreparedCandidateWorkingSurfaceHit ?? new WorkingSurfaceHit
        {
            Status = WorkingSurfaceQueryStatus.StaleContext,
            FaceId = -1
        };
    }

    /// <summary>요청한 후보에 작업 좌표계 context를 만들고 입력 축을 기록합니다.</summary>
    public static WorkingSurfaceContext CreatePreparedCandidateWorkingSurfaceContext(
        long candidateId,
        double normalX,
        double normalY,
        double normalZ,
        double axisXX,
        double axisXY,
        double axisXZ)
    {
        LastPreparedCandidateContextCreationCandidateId = candidateId;
        LastPreparedCandidateContextCreationAxes = [normalX, normalY, normalZ, axisXX, axisXY, axisXZ];
        if (PreparedCandidateWorkingSurfaceContext is not null)
            return PreparedCandidateWorkingSurfaceContext;

        var normal = new Vec3(normalX, normalY, normalZ).Normalized();
        var axisX = new Vec3(axisXX, axisXY, axisXZ).Normalized();
        Vec3 axisY = Vec3.Cross(normal, axisX).Normalized();
        return new WorkingSurfaceContext
        {
            Id = ++_nextContextId,
            Revision = GetModelRevision(),
            Frame =
            [
                0, 0, 0,
                axisX.X, axisX.Y, axisX.Z,
                axisY.X, axisY.Y, axisY.Z,
                normal.X, normal.Y, normal.Z
            ]
        };
    }

    /// <summary>해제 요청을 후보 ID와 함께 기록합니다.</summary>
    public static void ReleasePreparedCandidateWorkingSurfaceContext(long candidateId, long contextId) =>
        ReleasedPreparedCandidateContexts.Add((candidateId, contextId));

    /// <summary>활성 문서 first-hit 호출을 세어 기본 stale 결과를 반환합니다.</summary>
    public static WorkingSurfaceHit QueryWorkingSurface(long contextId, long revision, double u, double v)
    {
        ActiveWorkingGeometryQueryCount++;
        return new WorkingSurfaceHit { Status = WorkingSurfaceQueryStatus.StaleContext, FaceId = -1 };
    }

    /// <summary>활성 문서 인접 질의 호출을 기록합니다.</summary>
    public static WorkingFaceAdjacencyStatus QueryWorkingFaceAdjacency(
        long contextId, long revision, int firstFaceIndex, int secondFaceIndex)
    {
        ActiveWorkingGeometryQueryCount++;
        return WorkingFaceAdjacencyStatus.Unknown;
    }

    /// <summary>후보 전용 Native 인접 질의에 전달된 후보 ID와 context를 기록합니다.</summary>
    public static WorkingFaceAdjacencyStatus QueryPreparedCandidateFaceAdjacency(
        long candidateId, long contextId, long revision, int firstFaceIndex, int secondFaceIndex)
    {
        LastPreparedCandidateAdjacencyQueryId = candidateId;
        return PreparedCandidateAdjacencyStatus;
    }

    /// <summary>활성 문서 topology 질의 호출을 기록합니다.</summary>
    public static WorkingSelectedTopology QueryWorkingSelectedTopology(
        long contextId, long revision, int[] faceIds, double deflectionMm)
    {
        ActiveWorkingGeometryQueryCount++;
        return new WorkingSelectedTopology { Status = WorkingTopologyQueryStatus.StaleContext };
    }

    /// <summary>후보 전용 Native topology 질의에 전달된 후보 ID를 기록합니다.</summary>
    public static WorkingSelectedTopology QueryPreparedCandidateSelectedTopology(
        long candidateId, long contextId, long revision, int[] faceIds, double deflectionMm)
    {
        LastPreparedCandidateTopologyQueryId = candidateId;
        return PreparedCandidateSelectedTopology ?? new WorkingSelectedTopology
        {
            Status = WorkingTopologyQueryStatus.StaleContext
        };
    }

    /// <summary>활성 문서 표면 요소 질의 호출을 기록합니다.</summary>
    public static WorkingSurfaceElementsResult QueryWorkingSurfaceElements(
        long contextId,
        long revision,
        int[] faceIds,
        WorkingRegionLoopData[] boundary,
        double deflectionMm,
        double angularToleranceRad)
    {
        ActiveWorkingGeometryQueryCount++;
        return new WorkingSurfaceElementsResult { Status = WorkingSurfaceElementQueryStatus.StaleContext };
    }

    /// <summary>후보 전용 Native 표면 요소 질의에 전달된 후보 ID를 기록합니다.</summary>
    public static WorkingSurfaceElementsResult QueryPreparedCandidateSurfaceElements(
        long candidateId,
        long contextId,
        long revision,
        int[] faceIds,
        WorkingRegionLoopData[] boundary,
        double deflectionMm,
        double angularToleranceRad)
    {
        LastPreparedCandidateSurfaceElementsQueryId = candidateId;
        return PreparedCandidateSurfaceElements ?? new WorkingSurfaceElementsResult
        {
            Status = WorkingSurfaceElementQueryStatus.StaleContext
        };
    }

    /// <summary>활성 문서 곡선 간격 질의의 입력과 설정을 기록합니다.</summary>
    public static CurveClearanceResultData MeasureWorkingCurveClearance(
        long contextId,
        long revision,
        CurveDefinitionData[] curves,
        int[] localCurveIndexPairs,
        double wireDiameterMm,
        double maximumErrorMm,
        int maximumPairChecks)
    {
        ActiveWorkingGeometryQueryCount++;
        RecordCurveClearanceQuery(contextId, revision, curves, localCurveIndexPairs, wireDiameterMm, maximumErrorMm, maximumPairChecks);
        return ActiveCurveClearanceResult ?? new CurveClearanceResultData
        {
            Status = WorkingCurveClearanceStatus.StaleContext
        };
    }

    /// <summary>준비 후보의 곡선 간격 질의에 전달된 후보 ID와 입력을 기록합니다.</summary>
    public static CurveClearanceResultData MeasurePreparedCandidateWorkingCurveClearance(
        long candidateId,
        long contextId,
        long revision,
        CurveDefinitionData[] curves,
        int[] localCurveIndexPairs,
        double wireDiameterMm,
        double maximumErrorMm,
        int maximumPairChecks)
    {
        LastPreparedCandidateCurveClearanceId = candidateId;
        RecordCurveClearanceQuery(contextId, revision, curves, localCurveIndexPairs, wireDiameterMm, maximumErrorMm, maximumPairChecks);
        return PreparedCandidateCurveClearanceResult ?? new CurveClearanceResultData
        {
            Status = WorkingCurveClearanceStatus.StaleContext
        };
    }

    /// <summary>활성 문서 깊이 envelope 질의의 입력과 설정을 기록합니다.</summary>
    public static CurveDepthEnvelopeResultData MeasureWorkingCurveDepthEnvelope(
        long contextId,
        long revision,
        double[] centerlineSamples,
        int[] selectedFaceIds,
        int anchorFaceId,
        double anchorU,
        double anchorV,
        double[] anchorPoint,
        double wireDiameterMm,
        double requiredInsertionDepthMm,
        double maximumSampleSpacingMm,
        double requestedAngularToleranceRad,
        double maximumMeasuredDeviationMm,
        double connectionToleranceMm,
        int maximumProbeCount)
    {
        ActiveWorkingGeometryQueryCount++;
        RecordDepthEnvelopeQuery(
            contextId, revision, centerlineSamples, selectedFaceIds, anchorFaceId, anchorU, anchorV, anchorPoint,
            wireDiameterMm, requiredInsertionDepthMm, maximumSampleSpacingMm, requestedAngularToleranceRad,
            maximumMeasuredDeviationMm, connectionToleranceMm, maximumProbeCount);
        return ActiveCurveDepthEnvelopeResult ?? new CurveDepthEnvelopeResultData
        {
            Status = WorkingCurveDepthEnvelopeStatus.StaleContext
        };
    }

    /// <summary>후보 문서 깊이 envelope 질의에 후보 ID와 입력을 기록합니다.</summary>
    public static CurveDepthEnvelopeResultData MeasurePreparedCandidateWorkingCurveDepthEnvelope(
        long candidateId,
        long contextId,
        long revision,
        double[] centerlineSamples,
        int[] selectedFaceIds,
        int anchorFaceId,
        double anchorU,
        double anchorV,
        double[] anchorPoint,
        double wireDiameterMm,
        double requiredInsertionDepthMm,
        double maximumSampleSpacingMm,
        double requestedAngularToleranceRad,
        double maximumMeasuredDeviationMm,
        double connectionToleranceMm,
        int maximumProbeCount)
    {
        LastPreparedCandidateDepthEnvelopeId = candidateId;
        RecordDepthEnvelopeQuery(
            contextId, revision, centerlineSamples, selectedFaceIds, anchorFaceId, anchorU, anchorV, anchorPoint,
            wireDiameterMm, requiredInsertionDepthMm, maximumSampleSpacingMm, requestedAngularToleranceRad,
            maximumMeasuredDeviationMm, connectionToleranceMm, maximumProbeCount);
        return PreparedCandidateCurveDepthEnvelopeResult ?? new CurveDepthEnvelopeResultData
        {
            Status = WorkingCurveDepthEnvelopeStatus.StaleContext
        };
    }

    /// <summary>활성 문서 깊이 보정의 입력을 기록하고 테스트 결과를 반환합니다.</summary>
    public static CurveDepthAdjustmentResultData AdjustWorkingCurveInsertionDepth(
        long contextId,
        long revision,
        double[] surfacePathWorkingSamples,
        int[] selectedFaceIds,
        int anchorFaceId,
        double anchorU,
        double anchorV,
        double[] anchorPoint,
        double wireDiameterMm,
        double requiredInsertionDepthMm,
        double maximumSampleSpacingMm,
        double requestedAngularToleranceRad,
        double maximumSurfaceDeviationMm,
        double connectionToleranceMm,
        int maximumProbeCount)
    {
        ActiveWorkingGeometryQueryCount++;
        RecordDepthAdjustmentQuery(
            contextId, revision, surfacePathWorkingSamples, selectedFaceIds, anchorFaceId, anchorU, anchorV, anchorPoint,
            wireDiameterMm, requiredInsertionDepthMm, maximumSampleSpacingMm, requestedAngularToleranceRad,
            maximumSurfaceDeviationMm, connectionToleranceMm, maximumProbeCount);
        return ActiveCurveDepthAdjustmentResult ?? new CurveDepthAdjustmentResultData
        {
            Status = WorkingCurveDepthEnvelopeStatus.StaleContext
        };
    }

    /// <summary>후보 문서 깊이 보정의 후보 ID와 입력을 기록하고 테스트 결과를 반환합니다.</summary>
    public static CurveDepthAdjustmentResultData AdjustPreparedCandidateWorkingCurveInsertionDepth(
        long candidateId,
        long contextId,
        long revision,
        double[] surfacePathWorkingSamples,
        int[] selectedFaceIds,
        int anchorFaceId,
        double anchorU,
        double anchorV,
        double[] anchorPoint,
        double wireDiameterMm,
        double requiredInsertionDepthMm,
        double maximumSampleSpacingMm,
        double requestedAngularToleranceRad,
        double maximumSurfaceDeviationMm,
        double connectionToleranceMm,
        int maximumProbeCount)
    {
        LastPreparedCandidateDepthAdjustmentId = candidateId;
        RecordDepthAdjustmentQuery(
            contextId, revision, surfacePathWorkingSamples, selectedFaceIds, anchorFaceId, anchorU, anchorV, anchorPoint,
            wireDiameterMm, requiredInsertionDepthMm, maximumSampleSpacingMm, requestedAngularToleranceRad,
            maximumSurfaceDeviationMm, connectionToleranceMm, maximumProbeCount);
        return PreparedCandidateCurveDepthAdjustmentResult ?? new CurveDepthAdjustmentResultData
        {
            Status = WorkingCurveDepthEnvelopeStatus.StaleContext
        };
    }

    /// <summary>활성 문서 연결 경로 충돌 질의의 입력과 설정을 기록합니다.</summary>
    public static ConnectionObstacleResultData CheckConnectionPathObstacles(
        long contextId,
        long revision,
        CurveDefinitionData[] curves,
        int moldExitTransitionIndex,
        double connectionToleranceMm)
    {
        RecordConnectionObstacleQuery(contextId, revision, curves, moldExitTransitionIndex, connectionToleranceMm);
        return ActiveConnectionObstacleResult ?? new ConnectionObstacleResultData
        {
            Status = WorkingConnectionObstacleStatus.Unresolved
        };
    }

    /// <summary>준비 후보에서 후보 ID와 연결 경로 곡선 충돌 질의를 기록합니다.</summary>
    public static ConnectionObstacleResultData CheckPreparedCandidateConnectionPathObstacles(
        long candidateId,
        long contextId,
        long revision,
        CurveDefinitionData[] curves,
        int moldExitTransitionIndex,
        double connectionToleranceMm)
    {
        LastPreparedCandidateConnectionObstacleId = candidateId;
        RecordConnectionObstacleQuery(contextId, revision, curves, moldExitTransitionIndex, connectionToleranceMm);
        return PreparedCandidateConnectionObstacleResult ?? new ConnectionObstacleResultData
        {
            Status = WorkingConnectionObstacleStatus.StaleContext
        };
    }

    private static void RecordConnectionObstacleQuery(
        long contextId,
        long revision,
        CurveDefinitionData[] curves,
        int moldExitTransitionIndex,
        double connectionToleranceMm)
    {
        LastConnectionObstacleCurves = curves;
        LastConnectionObstacleTransitionIndex = moldExitTransitionIndex;
        LastConnectionObstacleToleranceMm = connectionToleranceMm;
        LastConnectionObstacleContext = [contextId, revision];
    }

    private static void RecordCurveClearanceQuery(
        long contextId,
        long revision,
        CurveDefinitionData[] curves,
        int[] localCurveIndexPairs,
        double wireDiameterMm,
        double maximumErrorMm,
        int maximumPairChecks)
    {
        LastCurveClearanceDefinitions = curves;
        LastCurveClearanceLocalPairs = localCurveIndexPairs;
        LastCurveClearanceSettings = [wireDiameterMm, maximumErrorMm, maximumPairChecks];
        LastCurveClearanceContext = [contextId, revision];
    }

    private static void RecordDepthEnvelopeQuery(
        long contextId,
        long revision,
        double[] centerlineSamples,
        int[] selectedFaceIds,
        int anchorFaceId,
        double anchorU,
        double anchorV,
        double[] anchorPoint,
        double wireDiameterMm,
        double requiredInsertionDepthMm,
        double maximumSampleSpacingMm,
        double requestedAngularToleranceRad,
        double maximumMeasuredDeviationMm,
        double connectionToleranceMm,
        int maximumProbeCount)
    {
        LastDepthEnvelopeSamples = centerlineSamples.ToArray();
        LastDepthEnvelopeFaceIndices = selectedFaceIds.ToArray();
        LastDepthEnvelopeContext = [contextId, revision];
        LastDepthEnvelopeAnchor = [anchorFaceId, anchorU, anchorV, .. anchorPoint];
        LastDepthEnvelopeSettings =
        [
            wireDiameterMm,
            requiredInsertionDepthMm,
            maximumSampleSpacingMm,
            requestedAngularToleranceRad,
            maximumMeasuredDeviationMm,
            connectionToleranceMm,
            maximumProbeCount
        ];
    }

    private static void RecordDepthAdjustmentQuery(
        long contextId,
        long revision,
        double[] surfacePathWorkingSamples,
        int[] selectedFaceIds,
        int anchorFaceId,
        double anchorU,
        double anchorV,
        double[] anchorPoint,
        double wireDiameterMm,
        double requiredInsertionDepthMm,
        double maximumSampleSpacingMm,
        double requestedAngularToleranceRad,
        double maximumSurfaceDeviationMm,
        double connectionToleranceMm,
        int maximumProbeCount)
    {
        LastDepthAdjustmentSamples = surfacePathWorkingSamples.ToArray();
        LastDepthAdjustmentFaceIndices = selectedFaceIds.ToArray();
        LastDepthAdjustmentContext = [contextId, revision];
        LastDepthAdjustmentAnchor = [anchorFaceId, anchorU, anchorV, .. anchorPoint];
        LastDepthAdjustmentSettings =
        [
            wireDiameterMm,
            requiredInsertionDepthMm,
            maximumSampleSpacingMm,
            requestedAngularToleranceRad,
            maximumSurfaceDeviationMm,
            connectionToleranceMm,
            maximumProbeCount
        ];
    }

    /// <summary>테스트에서는 모델 표시 재구성이 성공한 것으로 처리합니다.</summary>
    public static bool RebuildModelPresentation() => true;

    /// <summary>Face 목록 구성에 필요한 직교 테스트 축을 반환합니다.</summary>
    public static double[] GetAutoAxis(int axisIndex) => axisIndex == 0
        ? new[] { 1d, 0, 0 }
        : new[] { 0d, 0, 1 };

    /// <summary>테스트 Face에 표면 종류가 없도록 해 면적 후보를 생략합니다.</summary>
    public static string GetFaceType(int faceIndex) => null;

    /// <summary>테스트 Face의 닫힌 삼각 경계를 반환합니다.</summary>
    public static UvWire[] GetFaceUvBoundary(int faceIndex) =>
    [
        new UvWire
        {
            IsOuter = true,
            Closed = true,
            Points = [0, 0, 1, 0, 0, 1]
        }
    ];

    /// <summary>후보 범위에서 테스트 Face의 닫힌 삼각 경계를 반환합니다.</summary>
    public static UvWire[] GetPreparedCandidateFaceUvBoundary(long candidateId, int faceIndex)
    {
        LastPreparedCandidateBoundaryQueryId = candidateId;
        return GetFaceUvBoundary(faceIndex);
    }

    /// <summary>테스트 Face UV를 문서에 고유한 삼각 경계 표본으로 바꿉니다.</summary>
    public static double[] SampleUvTo3D(int faceIndex, double u, double v) =>
        new[] { faceIndex * 3d + u, v, 0d };

    /// <summary>후보 범위에서 테스트 Face UV를 모델 XYZ로 바꿉니다.</summary>
    public static double[] SamplePreparedCandidateUvTo3D(long candidateId, int faceIndex, double u, double v)
    {
        LastPreparedCandidateSampleQueryId = candidateId;
        return SampleUvTo3D(faceIndex, u, v);
    }

    /// <summary>작업 정면 카메라 준비를 성공으로 처리합니다.</summary>
    public static bool ReturnToWorkingFront(long contextId, long revision) => true;

    /// <summary>현재 테스트 모델 revision을 반환합니다.</summary>
    public static long GetModelRevision() => CurrentModelRevision;

    /// <summary>해제된 context를 테스트 기록에 보관합니다.</summary>
    public static void ReleaseWorkingSurfaceContext(long contextId) => ReleasedContextIds.Add(contextId);

    /// <summary>작업 좌표계 표시 저장점을 발급합니다.</summary>
    public static WorkingCoordinateSystemPresentation CaptureWorkingCoordinateSystemPresentation() => new()
    {
        Id = ++_nextPresentationId
    };

    /// <summary>테스트에서 투영 경계 표시를 비웁니다.</summary>
    public static void ShowProjectedModelBoundary(double[] loops) { }

    /// <summary>Normal과 수직인 축으로 유효한 frame을 가진 Native context를 만듭니다.</summary>
    public static WorkingSurfaceContext CreateWorkingSurfaceContext(
        double normalX,
        double normalY,
        double normalZ,
        double axisXX,
        double axisXY,
        double axisXZ)
    {
        var normal = new Vec3(normalX, normalY, normalZ).Normalized();
        var xAxis = new Vec3(axisXX, axisXY, axisXZ).Normalized();
        Vec3 yAxis = Vec3.Cross(normal, xAxis).Normalized();

        return new WorkingSurfaceContext
        {
            Id = ++_nextContextId,
            Revision = GetModelRevision(),
            Frame =
            [
                0, 0, 0,
                xAxis.X, xAxis.Y, xAxis.Z,
                yAxis.X, yAxis.Y, yAxis.Z,
                normal.X, normal.Y, normal.Z
            ]
        };
    }

    /// <summary>요청한 표시 저장점 복원을 성공으로 처리합니다.</summary>
    public static bool RestoreWorkingCoordinateSystemPresentation(long presentationId)
    {
        RestoredPresentationCount++;
        return true;
    }

    /// <summary>테스트용 Native 표시 저장점을 해제합니다.</summary>
    public static void ReleaseWorkingCoordinateSystemPresentation(long presentationId) { }

    /// <summary>Face 선택 강조 표시를 숨긴 횟수를 기록합니다.</summary>
    public static void HideFaceHighlights() => HideFaceHighlightsCount++;

    /// <summary>기존 발열 경로 표시를 지웁니다.</summary>
    public static void ClearPathSegments() { }

    /// <summary>기존 연결 경로를 지운 호출을 기록합니다.</summary>
    public static void ClearConnectionPaths()
    {
        ClearConnectionPathsCount++;
        ProjectionCommitCountWhenConnectionPathsCleared = ProjectionCommitCount;
    }

    /// <summary>연결 경로 미리보기 삭제를 기록합니다.</summary>
    public static void ClearConnectionPathPreview() => ClearConnectionPathPreviewCount++;

    /// <summary>제어점 편집 후보의 표시 좌표를 복사해 기록합니다.</summary>
    public static bool SetControlPointEditPreview(double[][] xyzPolylines)
    {
        ControlPointEditPreviewPolylines = xyzPolylines?.Select(points => points.ToArray()).ToArray()
            ?? Array.Empty<double[]>();
        return ControlPointEditPreviewPolylines.Length > 0 && !FailControlPointEditPreviewAfterSetting;
    }

    /// <summary>제어점 편집 임시 표시를 지운 호출을 기록합니다.</summary>
    public static void ClearControlPointEditPreview()
    {
        ClearControlPointEditPreviewCount++;
        ControlPointEditPreviewPolylines = Array.Empty<double[]>();
    }

    /// <summary>뷰어 카메라 맞춤 호출을 받습니다.</summary>
    public static void FitView() { }

    /// <summary>뷰어 확대·축소 호출을 받습니다.</summary>
    public static void ZoomView(double factor) { }

    /// <summary>표준 카메라 방향 설정 호출을 받습니다.</summary>
    public static void SetViewFrame(double nx, double ny, double nz, double ux, double uy, double uz) { }

    /// <summary>뷰 표시 갱신 호출을 받습니다.</summary>
    public static void RedrawView() { }

    /// <summary>품질 표본 위치 표시 호출의 좌표를 기록합니다.</summary>
    public static bool ShowQualityIssueLocation(double x, double y, double z)
    {
        LastQualityIssueLocation = [x, y, z];
        ShowQualityIssueLocationCount++;
        return true;
    }

    /// <summary>품질 표본 위치 삭제 호출을 기록합니다.</summary>
    public static void ClearQualityIssueLocation()
    {
        LastQualityIssueLocation = null;
        ClearQualityIssueLocationCount++;
    }

    /// <summary>회로 강조 표시 호출을 받습니다.</summary>
    public static void HighlightCircuit(int[] segmentIds) { }

    /// <summary>회전 구간 표시 호출을 기록합니다.</summary>
    public static bool SetTurnSegmentEnabled(int segmentId, bool enabled, double red, double green, double blue)
    {
        TurnSegmentEnabledCalls.Add((segmentId, enabled));
        return true;
    }

    /// <summary>화면 좌표를 작업 평면에 투영하지 않는 테스트 결과입니다.</summary>
    public static double[] ScreenPointOnPlane(
        int x, int y, double ox, double oy, double oz, double nx, double ny, double nz) => null;

    /// <summary>제외 사각형 미리보기 호출을 받습니다.</summary>
    public static void SetExclusionPreview(double[] points) { }

    /// <summary>확정 제외 사각형 표시 호출을 받습니다.</summary>
    public static void AddExclusionOverlay(double[] points) { }

    /// <summary>정사영 외곽 모서리가 없는 테스트 결과를 반환합니다.</summary>
    public static HlrPolyline[] HlrEdges(
        double nx, double ny, double nz, double xx, double xy, double xz, bool includeHidden) =>
        Array.Empty<HlrPolyline>();

    /// <summary>선택 영역 경계점을 반환하지 않는 테스트 결과입니다.</summary>
    public static double[] PickSelectedBoundaryPoint(int[] selectedFaceIndices, int x, int y) => null;

    /// <summary>Native 연결 경로 미리보기를 만들지 않는 테스트 결과입니다.</summary>
    public static double[] ProjectConnectionPath(
        int connectionId,
        double[] sourceAndDatumEndpoints,
        double[] sourcePathEndpoint,
        int[] selectedFaceIndices,
        double directionX,
        double directionY,
        double directionZ,
        double insertionDepthMm,
        bool previewOnly) => null;

    /// <summary>Native 곡선 투영 호출 인자와 결과를 테스트에 제공합니다.</summary>
    public static HeatingPathSegmentResult StageProjectedWorkingCurveSegment(
        long contextId,
        long revision,
        int stagingId,
        string kind,
        CurveDefinitionData definition,
        int[] faceIndices,
        double directionX,
        double directionY,
        double directionZ)
    {
        RecordProjection(contextId, revision, stagingId, kind, definition?.Points, faceIndices,
            directionX, directionY, directionZ);
        LastCurveDefinition = definition;
        return CreateProjectionResult();
    }

    /// <summary>Native 다중선 투영 호출 인자와 결과를 테스트에 제공합니다.</summary>
    public static HeatingPathSegmentResult StageProjectedWorkingPolyline(
        long contextId,
        long revision,
        int stagingId,
        double[] points,
        int[] faceIndices,
        double directionX,
        double directionY,
        double directionZ)
    {
        RecordProjection(contextId, revision, stagingId, null, points, faceIndices,
            directionX, directionY, directionZ);
        LastCurveDefinition = null;
        return CreateProjectionResult();
    }

    /// <summary>Native 경로 확정 호출의 ID와 결과를 테스트에 기록합니다.</summary>
    public static bool CommitWorkingPathGeometry(
        long contextId,
        long revision,
        int[] stagedSegmentIds,
        int[] finalSegmentIds,
        int[] stagedPolylineIds)
    {
        LastProjectionContextId = contextId;
        LastProjectionRevision = revision;
        LastStagedSegmentIds = (int[])stagedSegmentIds.Clone();
        LastFinalSegmentIds = (int[])finalSegmentIds.Clone();
        LastStagedPolylineIds = (int[])stagedPolylineIds.Clone();
        ProjectionCommitCount++;
        return ProjectionCommitResult;
    }

    /// <summary>후보 전용 곡선 투영 입력을 기록하고 측정 결과를 반환합니다.</summary>
    public static HeatingPathSegmentResult StagePreparedCandidateWorkingCurveSegment(
        long candidateId,
        long contextId,
        long revision,
        int stagingId,
        string kind,
        CurveDefinitionData definition,
        int[] faceIndices,
        double directionX,
        double directionY,
        double directionZ,
        double requestedDeflectionMm,
        double requestedAngularToleranceRad,
        double maximumSampleSpacingMm,
        int maximumSubdivisionDepth,
        int maximumProbeCount)
    {
        LastPreparedCandidatePathCandidateId = candidateId;
        LastPreparedCandidatePathContextId = contextId;
        RecordProjection(contextId, revision, stagingId, kind, definition?.Points, faceIndices,
            directionX, directionY, directionZ);
        LastCurveDefinition = definition;
        LastCandidateApproximationSettings = [
            requestedDeflectionMm,
            requestedAngularToleranceRad,
            maximumSampleSpacingMm,
            maximumSubdivisionDepth,
            maximumProbeCount];
        return CreateProjectionResult();
    }

    /// <summary>정확한 후보 곡선 입력과 근사 설정을 기록하고 측정 대역 결과를 반환합니다.</summary>
    public static HeatingPathSegmentResult StagePreparedCandidateExactCurveSegment(
        long candidateId,
        long contextId,
        long revision,
        int stagingId,
        string kind,
        CurveDefinitionData definition,
        double requestedDeflectionMm,
        double requestedAngularToleranceRad,
        double maximumSampleSpacingMm,
        int maximumSubdivisionDepth,
        int maximumProbeCount)
    {
        LastPreparedCandidatePathCandidateId = candidateId;
        LastPreparedCandidatePathContextId = contextId;
        LastProjectionContextId = contextId;
        LastProjectionRevision = revision;
        LastProjectionStagingId = stagingId;
        LastProjectedPathKind = kind;
        LastCurveDefinition = definition;
        LastProjectedPoints = null;
        LastProjectedFaceIndices = Array.Empty<int>();
        LastProjectionDirection = Array.Empty<double>();
        LastCandidateApproximationSettings = [
            requestedDeflectionMm,
            requestedAngularToleranceRad,
            maximumSampleSpacingMm,
            maximumSubdivisionDepth,
            maximumProbeCount];
        PreparedCandidateExactCurveStageCount++;
        return CreateProjectionResult();
    }

    /// <summary>후보 곡선 staging과 최종 ID를 기록하고 확정 결과를 반환합니다.</summary>
    public static bool CommitPreparedCandidateWorkingPathGeometry(
        long candidateId,
        long contextId,
        long revision,
        int[] stagedSegmentIds,
        int[] finalSegmentIds)
    {
        LastPreparedCandidatePathCandidateId = candidateId;
        LastPreparedCandidatePathContextId = contextId;
        LastProjectionRevision = revision;
        LastPreparedCandidatePathStagedIds = (int[])stagedSegmentIds.Clone();
        LastPreparedCandidatePathFinalIds = (int[])finalSegmentIds.Clone();
        PreparedCandidatePathCommitCount++;
        return PreparedCandidatePathCommitResult;
    }

    /// <summary>후보 context에 속한 곡선 폐기 요청을 기록합니다.</summary>
    public static void DiscardPreparedCandidateWorkingPathGeometry(
        long candidateId,
        long contextId,
        long revision,
        int[] stagedSegmentIds)
    {
        LastPreparedCandidatePathCandidateId = candidateId;
        LastPreparedCandidatePathContextId = contextId;
        LastProjectionRevision = revision;
        LastPreparedCandidatePathStagedIds = (int[])stagedSegmentIds.Clone();
        PreparedCandidatePathDiscardCount++;
    }

    /// <summary>Native 후보 폐기 호출의 ID를 테스트에 기록합니다.</summary>
    public static void DiscardWorkingPathGeometry(
        long contextId,
        long revision,
        int[] stagedSegmentIds,
        int[] stagedPolylineIds)
    {
        LastProjectionContextId = contextId;
        LastProjectionRevision = revision;
        LastStagedSegmentIds = (int[])stagedSegmentIds.Clone();
        LastStagedPolylineIds = (int[])stagedPolylineIds.Clone();
        ProjectionDiscardCount++;
    }

    /// <summary>투영 경로 호출 인자를 복사해 테스트 관찰 상태로 저장합니다.</summary>
    private static void RecordProjection(
        long contextId,
        long revision,
        int stagingId,
        string kind,
        double[] points,
        int[] faceIndices,
        double directionX,
        double directionY,
        double directionZ)
    {
        LastProjectionContextId = contextId;
        LastProjectionRevision = revision;
        LastProjectionStagingId = stagingId;
        LastProjectedPathKind = kind;
        LastProjectedPoints = points == null ? null : (double[])points.Clone();
        LastProjectedFaceIndices = faceIndices == null ? null : (int[])faceIndices.Clone();
        LastProjectionDirection = new[] { directionX, directionY, directionZ };
    }

    /// <summary>설정된 결과가 없으면 유효한 기본 경로 측정값을 만듭니다.</summary>
    private static HeatingPathSegmentResult CreateProjectionResult()
    {
        if (ProjectedPathStageShouldFail)
            return null;

        if (NextProjectedPathResult is not null)
            return NextProjectedPathResult;

        if (LastProjectedPoints is { Length: >= 6 } points)
        {
            Vec3[] modelPoints = Enumerable.Range(0, points.Length / 3)
                .Select(index => new Vec3(points[index * 3], points[index * 3 + 1], points[index * 3 + 2]))
                .ToArray();
            double length = 0;
            double maximumSpacing = 0;
            for (int index = 1; index < modelPoints.Length; index++)
            {
                double spacing = (modelPoints[index] - modelPoints[index - 1]).Length;
                length += spacing;
                maximumSpacing = Math.Max(maximumSpacing, spacing);
            }

            if (double.IsFinite(length) && length > 0)
            {
                return new HeatingPathSegmentResult
                {
                    Length = length,
                    Start = [modelPoints[0].X, modelPoints[0].Y, modelPoints[0].Z],
                    End = [modelPoints[^1].X, modelPoints[^1].Y, modelPoints[^1].Z],
                    Samples = (double[])points.Clone(),
                    RequestedDeflectionMm = 0.05,
                    RequestedAngularToleranceRad = 0.1,
                    MaximumMeasuredDeviationMm = 0,
                    MaximumSampleSpacingMm = maximumSpacing,
                    ProbeCount = modelPoints.Length - 1,
                    ApproximationComplete = maximumSpacing <= 2.0,
                    ApproximationRuleVersion = "occt-wire-sampling-v1"
                };
            }
        }

        return new HeatingPathSegmentResult
        {
            Length = 1,
            Start = [0, 0, 0],
            End = [1, 0, 0],
            Samples = [0, 0, 0, 1, 0, 0],
            RequestedDeflectionMm = 0.05,
            RequestedAngularToleranceRad = 0.1,
            MaximumMeasuredDeviationMm = 0,
            MaximumSampleSpacingMm = 1,
            ProbeCount = 1,
            ApproximationComplete = true,
            ApproximationRuleVersion = "occt-wire-sampling-v1"
        };
    }
}
}

namespace HeatingMold.App
{
/// <summary>Viewer의 WPF 모드 속성을 제공하는 테스트 대역입니다.</summary>
internal sealed class StubViewport
{
    private readonly ViewerInputModeState _inputModeState = new();

    /// <summary>현재 활성화된 상호 배타 입력 모드입니다.</summary>
    internal ViewerInputMode InputMode => _inputModeState.Mode;

    /// <summary>테스트에서 실제 Viewer와 같은 규칙으로 입력 모드를 전환합니다.</summary>
    internal void SetInputMode(ViewerInputMode mode)
    {
        _inputModeState.Activate(mode);
        ViewerInputModeConfiguration configuration = _inputModeState.Configuration;
        if (configuration.EditsControlPoint)
            CircuitHoverMode = false;
    }

    /// <summary>호환 입력 속성 변경을 하나의 모드 전이로 처리합니다.</summary>
    private void SetInputMode(ViewerInputMode mode, bool enabled)
    {
        if (enabled)
            SetInputMode(mode);
        else if (_inputModeState.Mode == mode)
            SetInputMode(ViewerInputMode.Navigate);
    }

    /// <summary>단일 Face 선택 이벤트입니다.</summary>
    internal event Action<int, bool> FacePicked;

    /// <summary>Face 선택 대역 이벤트를 발생시킵니다.</summary>
    internal void RaiseFacePicked(int faceIndex, bool toggle) => FacePicked?.Invoke(faceIndex, toggle);

    /// <summary>Face 호버 이벤트입니다.</summary>
    internal event Action<int> FaceHovered;

    /// <summary>사각 영역 Face 선택 이벤트입니다.</summary>
    internal event Action<int[], bool> FacesRectanglePicked;

    /// <summary>제외 사각형 이동 이벤트입니다.</summary>
    internal event Action<int, int, int, int> ExclusionSketchMoved;

    /// <summary>제외 사각형 확정 이벤트입니다.</summary>
    internal event Action<int, int, int, int> ExclusionSketchCompleted;

    /// <summary>제외 사각형 취소 이벤트입니다.</summary>
    internal event Action ExclusionSketchCancelled;

    /// <summary>회전 구간 호버 이벤트입니다.</summary>
    internal event Action<int> TurnSegmentHovered;

    /// <summary>발열 경로 조각 호버 이벤트입니다.</summary>
    internal event Action<int> HeatingPathSegmentHovered;

    /// <summary>회전 구간 선택 이벤트입니다.</summary>
    internal event Action<int> TurnSegmentPicked;

    /// <summary>연결부 목적지 선택 이벤트입니다.</summary>

    /// <summary>연결부 목적지 이동 이벤트입니다.</summary>

    /// <summary>표면 앵커 선택 이벤트입니다.</summary>
    internal event Action<int, int> SurfaceAnchorPicked;

    /// <summary>제어점 미리보기 위치 이벤트입니다.</summary>
    internal event Action<int, int, long> ControlPointEditPreviewMoved;

    /// <summary>제어점 드래그 확정 위치 이벤트입니다.</summary>
    internal event Action<int, int> ControlPointEditCompleted;

    /// <summary>제어점 드래그 취소 이벤트입니다.</summary>
    internal event Action ControlPointEditCancelled;

    /// <summary>Face 사각 선택 입력 모드입니다.</summary>
    internal bool RectangleSelectionMode
    {
        get => _inputModeState.Configuration.UsesFaceRectangle;
        set => SetInputMode(ViewerInputMode.FaceRectangleSelection, value);
    }

    /// <summary>제외 사각형 입력 모드입니다.</summary>
    internal bool ExclusionSketchMode
    {
        get => _inputModeState.Configuration.UsesRegionSketch;
        set => SetInputMode(ViewerInputMode.RegionExclusionSketch, value);
    }

    /// <summary>회전 구간 분할 입력 모드입니다.</summary>
    internal bool CircuitSplitMode
    {
        get => _inputModeState.Configuration.EditsCircuitTopology;
        set => SetInputMode(ViewerInputMode.CircuitTopologyEditing, value);
    }

    /// <summary>연결부 선택 입력 모드입니다.</summary>

    /// <summary>표면 앵커 선택 입력 모드입니다.</summary>
    internal bool SurfaceAnchorSelectionMode
    {
        get => _inputModeState.Configuration.PicksSurfaceAnchor;
        set => SetInputMode(ViewerInputMode.SurfaceAnchorPicking, value);
    }

    /// <summary>회로 구간 호버 입력 모드입니다.</summary>
    internal bool CircuitHoverMode { get; set; }

    /// <summary>절점 드래그 입력 모드입니다.</summary>
    internal bool ControlPointEditMode
    {
        get => _inputModeState.Configuration.EditsControlPoint;
        set => SetInputMode(ViewerInputMode.ControlPointEditing, value);
    }

    /// <summary>테스트 뷰어에 품질 표본 좌표를 전달합니다.</summary>
    internal bool ShowQualityIssueLocation(Vec3 location) =>
        HeatingMold.OccBridge.Bridge.ShowQualityIssueLocation(location.X, location.Y, location.Z);

    /// <summary>테스트 뷰어의 품질 표본 표시를 제거합니다.</summary>
    internal void ClearQualityIssueLocation() => HeatingMold.OccBridge.Bridge.ClearQualityIssueLocation();

    /// <summary>Native viewer 초기화를 성공으로 처리합니다.</summary>
    internal bool InitViewer() => true;

    /// <summary>연결 경로 미리보기를 종료합니다.</summary>
    internal void CancelConnectionPathPreview() => HeatingMold.OccBridge.Bridge.ClearConnectionPathPreview();

    /// <summary>창 비활성화 때 임시 포인터 입력과 연결 경로 표시를 정리합니다.</summary>
    internal void CancelTransientPointerInput() => CancelConnectionPathPreview();

    /// <summary>절점 미리보기를 정리합니다.</summary>
    internal void CancelControlPointEditPreview() => HeatingMold.OccBridge.Bridge.ClearControlPointEditPreview();

    /// <summary>요청한 절점 편집 응답이 현재 드래그 세대인지 확인합니다.</summary>
    internal bool IsCurrentControlPointEditGeneration(long generation) => ControlPointEditMode;

    /// <summary>회귀 검사에서 Native 회전 구간 선택 이벤트를 발생시킵니다.</summary>
    internal void RaiseTurnSegmentPicked(int runtimeId) => TurnSegmentPicked?.Invoke(runtimeId);

    /// <summary>제어점 드래그 회귀 검사에서 미리보기 응답을 보냅니다.</summary>
    internal void RaiseControlPointEditPreviewMoved(int x, int y, long generation) =>
        ControlPointEditPreviewMoved?.Invoke(x, y, generation);

    /// <summary>제어점 드래그 회귀 검사에서 확정 위치를 보냅니다.</summary>
    internal void RaiseControlPointEditCompleted(int x, int y) =>
        ControlPointEditCompleted?.Invoke(x, y);

    /// <summary>제어점 드래그 회귀 검사에서 취소를 보냅니다.</summary>
    internal void RaiseControlPointEditCancelled() => ControlPointEditCancelled?.Invoke();
}

/// <summary>MainWindow 부분 코드의 UI 의존성을 충족하는 테스트 창입니다.</summary>
public partial class MainWindow : System.Windows.Window
{
    /// <summary>작업축 선택 UI 대역입니다.</summary>
    public System.Windows.Controls.ComboBox WorkingAxisBox { get; } = new();

    /// <summary>작업 결과 상태 표시 대역입니다.</summary>
    public System.Windows.Controls.TextBlock StatusText { get; } = new();

    /// <summary>확정 작업 좌표계 표시 대역입니다.</summary>
    public System.Windows.Controls.TextBlock WorkingCoordinateSystemText { get; } = new();

    /// <summary>경로 생성을 위한 발열 영역 선택 입력입니다.</summary>
    public System.Windows.Controls.ComboBox HeatingRegionCombo { get; } = new();

    /// <summary>발열 경로 시작 앵커 상태 표시입니다.</summary>
    public System.Windows.Controls.TextBlock HeatingStartAnchorStatusText { get; } = new();

    /// <summary>발열 경로 끝 앵커 상태 표시입니다.</summary>
    public System.Windows.Controls.TextBlock HeatingEndAnchorStatusText { get; } = new();

    /// <summary>시작부 금형 인출 앵커 상태 표시입니다.</summary>
    public System.Windows.Controls.TextBlock StartMoldExitAnchorStatusText { get; } = new();

    /// <summary>끝부 금형 인출 앵커 상태 표시입니다.</summary>
    public System.Windows.Controls.TextBlock EndMoldExitAnchorStatusText { get; } = new();

    /// <summary>선택 Face 목록 대역입니다.</summary>
    public System.Windows.Controls.ListBox SelectedFacesList { get; } = new();
    public System.Windows.Controls.ListBox ExitSideSegmentsList { get; } = new();
    /// <summary>안정 ID로 편집 대상을 고르는 절점 콤보 상자 대역입니다.</summary>
    public System.Windows.Controls.ComboBox ControlPointCombo { get; } = new();

    /// <summary>현재 제어점 편집 위치를 표시하는 대역입니다.</summary>
    public System.Windows.Controls.TextBlock ControlPointAnchorStatusText { get; } = new();

    /// <summary>원호 편집의 끝점 허용 오차 입력 대역입니다.</summary>
    public System.Windows.Controls.TextBox ControlPointToleranceBox { get; } = new() { Text = "0.05" };

    /// <summary>안정 ID로 회전 곡선을 편집할 구간 선택 대역입니다.</summary>
    public System.Windows.Controls.ComboBox TurnSegmentCurveCombo { get; } = new();

    /// <summary>회전 곡선 내부 좌표를 편집하는 표 대역입니다.</summary>
    public System.Windows.Controls.DataGrid TurnSegmentInteriorPointsGrid { get; } = new();

    /// <summary>회전 곡선 편집 상태를 표시하는 텍스트 대역입니다.</summary>
    public System.Windows.Controls.TextBlock TurnSegmentCurveEditorStatusText { get; } = new();
    /// <summary>회로 통합 시 ID를 보존할 회로 선택 대역입니다.</summary>
    public System.Windows.Controls.ComboBox CircuitSurvivorCombo { get; } = new();

    /// <summary>신규 회로 연결부 입력 대상을 고르는 콤보 상자 대역입니다.</summary>
    public System.Windows.Controls.ComboBox CircuitTopologyEndpointCombo { get; } = new();

    /// <summary>신규 회로 연결부의 앵커와 외부 좌표 상태 표시 대역입니다.</summary>
    public System.Windows.Controls.TextBlock CircuitTopologyEndpointStatusText { get; } = new();

    /// <summary>편집할 안정 연결부를 고르는 경로 콤보 상자 대역입니다.</summary>
    public System.Windows.Controls.ComboBox ConnectionPathEndpointCombo { get; } = new();

    /// <summary>연결 경로의 좌표와 검증 속성을 편집하는 표 대역입니다.</summary>
    public System.Windows.Controls.DataGrid ConnectionPathLegsGrid { get; } = new();

    /// <summary>연결 경로 편집 상태를 표시하는 텍스트 대역입니다.</summary>
    public System.Windows.Controls.TextBlock ConnectionPathEditorStatusText { get; } = new();

    /// <summary>신규 연결부 외부 좌표의 X 입력 대역입니다.</summary>
    public System.Windows.Controls.TextBox TopologyEndpointXBox { get; } = new();

    /// <summary>신규 연결부 외부 좌표의 Y 입력 대역입니다.</summary>
    public System.Windows.Controls.TextBox TopologyEndpointYBox { get; } = new();

    /// <summary>신규 연결부 외부 좌표의 Z 입력 대역입니다.</summary>
    public System.Windows.Controls.TextBox TopologyEndpointZBox { get; } = new();

    /// <summary>분할 회로의 금형 위 연장 여유 입력 대역입니다.</summary>
    public System.Windows.Controls.TextBox TopologyMoldExitAllowanceBox { get; } = new() { Text = "0" };

    /// <summary>분할 회로의 외부 연결선 여유 입력 대역입니다.</summary>
    public System.Windows.Controls.TextBox TopologyExternalAllowanceBox { get; } = new() { Text = "0" };

    /// <summary>회로 topology 편집 대상 회전 구간 목록 대역입니다.</summary>
    public System.Windows.Controls.ListBox CircuitTurnSegmentsList { get; } = new();

    /// <summary>회로 topology 임시 분석 결과를 표시하는 대역입니다.</summary>
    public System.Windows.Controls.TextBlock CircuitTopologyPreviewText { get; } = new();

    /// <summary>Face 후보 선택 대역입니다.</summary>
    public System.Windows.Controls.ComboBox FaceCombo { get; } = new();

    /// <summary>호버 결과 표시 대역입니다.</summary>
    public System.Windows.Controls.TextBlock HoverText { get; } = new();

    /// <summary>발열선 배치 방향 선택입니다.</summary>
    public System.Windows.Controls.ComboBox PlacementDirectionBox { get; } = new();

    /// <summary>기존 수동 편집 경로 처리 방식 선택입니다.</summary>
    public System.Windows.Controls.ComboBox ManualEditPolicyBox { get; } = new();

    /// <summary>시작부 외부 연결점의 X 좌표 입력입니다.</summary>
    public System.Windows.Controls.TextBox StartExternalXBox { get; } = new();

    /// <summary>시작부 외부 연결점의 Y 좌표 입력입니다.</summary>
    public System.Windows.Controls.TextBox StartExternalYBox { get; } = new();

    /// <summary>시작부 외부 연결점의 Z 좌표 입력입니다.</summary>
    public System.Windows.Controls.TextBox StartExternalZBox { get; } = new();

    /// <summary>끝부 외부 연결점의 X 좌표 입력입니다.</summary>
    public System.Windows.Controls.TextBox EndExternalXBox { get; } = new();

    /// <summary>끝부 외부 연결점의 Y 좌표 입력입니다.</summary>
    public System.Windows.Controls.TextBox EndExternalYBox { get; } = new();

    /// <summary>끝부 외부 연결점의 Z 좌표 입력입니다.</summary>
    public System.Windows.Controls.TextBox EndExternalZBox { get; } = new();

    /// <summary>금형 위 연장 구간의 추가 여유 입력입니다.</summary>
    public System.Windows.Controls.TextBox MoldExitAllowanceBox { get; } = new();

    /// <summary>외부 연결선 구간의 추가 여유 입력입니다.</summary>
    public System.Windows.Controls.TextBox ExternalConnectionAllowanceBox { get; } = new();

    /// <summary>회로의 실제 전압 입력입니다.</summary>
    public System.Windows.Controls.TextBox ActualVoltageBox { get; } = new();

    /// <summary>회로의 최대 전압 입력입니다.</summary>
    public System.Windows.Controls.TextBox MaximumVoltageBox { get; } = new();

    /// <summary>회로의 최대 전류 입력입니다.</summary>
    public System.Windows.Controls.TextBox MaximumCurrentBox { get; } = new();

    /// <summary>회로의 최대 전력 입력입니다.</summary>
    public System.Windows.Controls.TextBox MaximumPowerBox { get; } = new();

    /// <summary>회로 구간 편집 선택 대역입니다.</summary>
    public System.Windows.Controls.CheckBox CircuitSplitModeBox { get; } = new();

    /// <summary>STEP 출력에 원본 모델 형상을 포함할지 정하는 선택 상자입니다.</summary>
    public System.Windows.Controls.CheckBox IncludeSourceModelInStepBox { get; } = new();

    /// <summary>회로 길이 표 대역입니다.</summary>
    public System.Windows.Controls.DataGrid CircuitLengthGrid { get; } = new();

    /// <summary>회로별 전기 계산의 현재 상태를 표시하는 대역입니다.</summary>
    public System.Windows.Controls.TextBlock ElectricalResultsStatusText { get; } = new();

    /// <summary>회로별 전기 계산 표시 목록 대역입니다.</summary>
    public System.Windows.Controls.ItemsControl ElectricalResultsList { get; } = new();

    /// <summary>면적 분할의 현재성과 수렴 상태를 표시하는 대역입니다.</summary>
    public System.Windows.Controls.TextBlock AppliedAreaStatusText { get; } = new();

    /// <summary>전체 및 전기 상태별 면적 요약을 표시하는 대역입니다.</summary>
    public System.Windows.Controls.TextBlock AppliedAreaSummaryText { get; } = new();

    /// <summary>회로별 적용면적 행을 표시하는 대역입니다.</summary>
    public System.Windows.Controls.ItemsControl CircuitAppliedAreasList { get; } = new();

    public System.Windows.Controls.TextBlock CenterPitchQualityStatusText { get; } = new();

    public System.Windows.Controls.ItemsControl CenterPitchQualityList { get; } = new();

    /// <summary>작업 영역 preview canvas 대역입니다.</summary>
    public System.Windows.Controls.Canvas RegionCanvas { get; } = new();

    /// <summary>Face 드래그 선택 모드 대역입니다.</summary>
    public System.Windows.Controls.CheckBox FaceDragSelectModeBox { get; } = new();

    /// <summary>포함 영역 스케치 모드 대역입니다.</summary>
    public System.Windows.Controls.CheckBox IncludeModeBox { get; } = new();

    /// <summary>제외 영역 스케치 모드 대역입니다.</summary>
    public System.Windows.Controls.CheckBox ExcludeModeBox { get; } = new();

    /// <summary>Native viewer가 없는 테스트 구현입니다.</summary>
    private readonly StubViewport Viewport = new();

    /// <summary>실제 XAML 대신 테스트용 WPF 컨트롤 대역을 사용합니다.</summary>
    private void InitializeComponent() { }
}
}
