#pragma once

// 주의: 전역 using namespace System 은 Windows SDK 헤더(servprov.h 등)의
// COM 타입(IServiceProvider)과 충돌(C3699) → 헤더에서는 정규화 이름 사용.
namespace HeatingMold { namespace OccBridge {

/// <summary>셰이프 구성 요약 (S1 검증 항목 2)</summary>
public ref class ShapeInfo
{
public:
    property int Solids;
    property int Shells;
    property int Faces;
    property int Planes;
};

/// <summary>현재 문서의 Face 특징 질의 상태입니다.</summary>
public enum class FaceFeatureQueryStatus
{
    /// <summary>모든 Face 특징을 계산했습니다.</summary>
    Success = 0,
    /// <summary>현재 문서에 읽은 모델이 없습니다.</summary>
    InvalidModel = 1,
    /// <summary>OCCT가 한 개 이상의 Face 특징을 계산하지 못했습니다.</summary>
    GeometryFailure = 2,
    /// <summary>Face 질의를 요청한 준비 후보가 없거나 이미 폐기되었습니다.</summary>
    CandidateNotFound = 3
};

/// <summary>현재 Native Face의 종류·면적·중심·인접 번호입니다.</summary>
public ref class FaceGeometryFeatureData
{
public:
    /// <summary>질의 대상 문서에서 사용하는 0 기반 Native Face 번호입니다.</summary>
    property int FaceIndex;
    /// <summary>OCCT가 분류한 표면 종류 이름입니다.</summary>
    property System::String^ SurfaceType;
    /// <summary>OCCT가 계산한 면적(mm²)입니다.</summary>
    property double SurfaceAreaMm2;
    /// <summary>면의 무게 중심 XYZ(mm)입니다.</summary>
    property array<double>^ Center;
    /// <summary>공유 Edge를 가진 이웃 Face의 0 기반 번호입니다.</summary>
    property array<int>^ AdjacentFaceIndices;
};

/// <summary>Face 특징 일괄 질의의 상태와 전체 결과입니다.</summary>
public ref class FaceGeometryFeaturesResult
{
public:
    /// <summary>Native 특징 계산 상태입니다.</summary>
    property FaceFeatureQueryStatus Status;
    /// <summary>성공 시 질의 대상 모델의 모든 Face 특징입니다.</summary>
    property array<FaceGeometryFeatureData^>^ Features;
};

/// <summary>모델 로드의 거부·확정·화면 동기화 상태입니다.</summary>
public enum class ModelLoadStatus
{
    /// <summary>후보 준비가 실패해 활성 문서를 바꾸지 않았습니다.</summary>
    FailedBeforeCommit = 0,
    /// <summary>새 모델과 화면 표시를 확정했습니다.</summary>
    Committed = 1,
    /// <summary>새 모델은 확정했지만 화면 표시 동기화가 실패했습니다.</summary>
    CommittedPresentationOutOfSync = 2
};

/// <summary>STEP 모델 후보를 활성 문서에 반영하기 전 준비한 결과입니다.</summary>
public enum class ModelLoadCandidateStatus
{
    /// <summary>후보 기하 준비에 실패했습니다.</summary>
    Failed = 0,
    /// <summary>후보 기하를 준비했으며 아직 활성 문서를 바꾸지 않았습니다.</summary>
    Prepared = 1
};

/// <summary>준비된 STEP 모델 후보의 식별자와 기준 revision을 전달합니다.</summary>
public ref class ModelLoadCandidateResult
{
public:
    /// <summary>후보 준비 상태입니다.</summary>
    property ModelLoadCandidateStatus Status;
    /// <summary>준비 단계에서 활성 상태였던 모델 revision입니다.</summary>
    property long long BaseModelRevision;
    /// <summary>후보를 확정하거나 폐기할 때 사용하는 식별자입니다.</summary>
    property long long CandidateId;
    /// <summary>준비 실패 원인이며 성공 상태에서는 null입니다.</summary>
    property System::String^ Diagnostic;
};

/// <summary>모델 로드 상태와 확정된 문서 revision, 진단을 전달합니다.</summary>
public ref class ModelLoadResult
{
public:
    /// <summary>후보 로드와 화면 확정 결과입니다.</summary>
    property ModelLoadStatus Status;
    /// <summary>로드가 끝난 뒤 활성 문서의 모델 revision입니다.</summary>
    property long long ModelRevision;
    /// <summary>실패 원인 또는 후처리 진단이며 없으면 null입니다.</summary>
    property System::String^ Diagnostic;
};

/// <summary>UV 경계 와이어 1개 — 평면 2D 좌표 벌크(double flat: u0,v0,u1,v1,...)</summary>
public ref class UvWire
{
public:
    property bool IsOuter;            // true = 외곽, false = 홀
    property bool Closed;
    property array<double>^ Points;
};

/// <summary>원본 CAD 위상에서 추출한 선택 Face 통합 경계의 3D polyline.</summary>
public ref class RegionBoundaryLoop
{
public:
    property array<double>^ Points; // flat: x0,y0,z0,...
    property bool Closed;
};

public ref class HlrPolyline
{
public:
    property bool Visible;
    property array<double>^ Points;
};

/// <summary>투영된 발열 경로 구간의 길이와 양 끝점입니다.</summary>
public ref class HeatingPathSegmentResult
{
public:
    property double Length;
    property array<double>^ Start;
    property array<double>^ End;
    /// <summary>최종 곡선을 따라 순서대로 얻은 평탄화 XYZ 표본입니다.</summary>
    property array<double>^ Samples;
    /// <summary>표본 생성에 요청한 최대 편차(mm)입니다.</summary>
    property double RequestedDeflectionMm;
    /// <summary>표본 생성에 요청한 접선 각도 한도(rad)입니다.</summary>
    property double RequestedAngularToleranceRad;
    /// <summary>중간 표본에서 측정한 최대 현 편차(mm)입니다.</summary>
    property double MaximumMeasuredDeviationMm;
    /// <summary>연속 표본 사이에서 측정한 최대 거리(mm)입니다.</summary>
    property double MaximumSampleSpacingMm;
    /// <summary>곡선 내부에서 수행한 근사 확인 횟수입니다.</summary>
    property int ProbeCount;
    /// <summary>요청한 편차·각도·간격 조건을 모두 만족했는지 나타냅니다.</summary>
    property bool ApproximationComplete;
    /// <summary>표본 근사 알고리즘의 안정 버전 표식입니다.</summary>
    property System::String^ ApproximationRuleVersion;
};

/// <summary>
/// OCCT 최소 브리지 (S1 스파이크, /clr:netcore net8.0).
/// 상태는 프로세스 전역(단일 문서) — 본 개발에서 handle 기반으로 전환 예정.
/// </summary>
public enum class WorkingSurfaceQueryStatus
{
    /// <summary>실제 표면 first-hit 결과입니다.</summary>
    Hit = 0,
    /// <summary>작업 평면 광선과 교차한 표면이 없습니다.</summary>
    Miss = 1,
    /// <summary>광선이 표면에 접해 깊이를 판정할 수 없습니다.</summary>
    Tangent = 2,
    /// <summary>입력 좌표 또는 Face 번호가 유효하지 않습니다.</summary>
    InvalidInput = 3,
    /// <summary>작업 좌표계가 현재 모델 revision과 다릅니다.</summary>
    StaleContext = 4,
    /// <summary>OCCT 교차 계산에 실패했습니다.</summary>
    GeometryFailure = 5,
    /// <summary>현재 브리지가 알지 못하는 상태 값입니다.</summary>
    Unknown = 6
};
/// <summary>공유 Edge 기반 Face 인접 질의 상태입니다.</summary>
public enum class WorkingFaceAdjacencyStatus
{
    /// <summary>같은 Face이거나 실제 공유 Edge가 있습니다.</summary>
    Adjacent = 0,
    /// <summary>두 Face 사이에 공유 Edge가 없습니다.</summary>
    NotAdjacent = 1,
    /// <summary>Native Face 번호가 현재 문서 범위를 벗어났습니다.</summary>
    InvalidFace = 2,
    /// <summary>질의 작업 좌표계가 현재 모델 revision과 다릅니다.</summary>
    StaleContext = 3,
    /// <summary>OCCT 공유 Edge 검사에서 기하 오류가 발생했습니다.</summary>
    GeometryFailure = 4,
    /// <summary>현재 브리지가 알지 못하는 상태 값입니다.</summary>
    Unknown = 5
};

/// <summary>선택 Face topology 질의의 결과 상태입니다.</summary>
public enum class WorkingTopologyQueryStatus
{
    /// <summary>연결 성분과 닫힌 경계 루프를 얻었습니다.</summary>
    Success = 0,
    /// <summary>선택 목록이나 샘플 편차가 유효하지 않습니다.</summary>
    InvalidInput = 1,
    /// <summary>선택 목록에 현재 문서에 없는 Face 번호가 있습니다.</summary>
    InvalidFace = 2,
    /// <summary>요청 context가 현재 모델 revision과 다릅니다.</summary>
    StaleContext = 3,
    /// <summary>경계 루프가 열려 있거나 분기되어 연결 topology를 만들지 못했습니다.</summary>
    InvalidTopology = 4,
    /// <summary>OCCT 위상 처리에서 기하 오류가 발생했습니다.</summary>
    GeometryFailure = 5,
    /// <summary>현재 브리지가 알지 못하는 상태 값입니다.</summary>
    Unknown = 6
};

/// <summary>유효 작업 영역의 실제 표면 요소 질의 상태입니다.</summary>
public enum class WorkingSurfaceElementQueryStatus
{
    /// <summary>선택 Face를 유효 경계로 자르고 메시 요소를 반환했습니다.</summary>
    Success = 0,
    /// <summary>경계, 좌표계 또는 공차 입력이 유효하지 않습니다.</summary>
    InvalidInput = 1,
    /// <summary>요청한 Native Face 번호가 현재 문서에 없습니다.</summary>
    InvalidFace = 2,
    /// <summary>작업 context가 현재 모델 revision과 다릅니다.</summary>
    StaleContext = 3,
    /// <summary>영역과 선택 표면이 겹치지 않거나 유효한 face를 만들 수 없습니다.</summary>
    InvalidRegion = 4,
    /// <summary>요소 수가 질의 한도를 넘었습니다.</summary>
    ElementBudgetExceeded = 5,
    /// <summary>OCCT 절단 또는 삼각분할이 실패했습니다.</summary>
    GeometryFailure = 6,
    /// <summary>Native 구현이 알 수 없는 상태입니다.</summary>
    Unknown = 7
};

/// <summary>작업 평면의 외곽 또는 홀 경계 입력입니다.</summary>
public ref class WorkingRegionLoopData
{
public:
    /// <summary>중복 끝점이 없는 평탄화 작업 평면 좌표입니다.</summary>
    property array<double>^ Points;
    /// <summary>유효 영역에서 제외할 홀인지 나타냅니다.</summary>
    property bool IsHole;
};

/// <summary>표면 분할에서 얻은 삼각 요소의 측정값입니다.</summary>
public ref class WorkingSurfaceElementData
{
public:
    /// <summary>원본 현재 문서의 zero-based Native Face 번호입니다.</summary>
    property int FaceIndex;
    /// <summary>삼각 요소 표면 면적(mm²)입니다.</summary>
    property double SurfaceAreaMm2;
    /// <summary>요소 중심의 모델 좌표 XYZ입니다.</summary>
    property array<double>^ ModelCentroid;
    /// <summary>원본 Face의 매개변수 중심 UV입니다.</summary>
    property array<double>^ FaceUvCentroid;
    /// <summary>작업 평면의 요소 중심 UV입니다.</summary>
    property array<double>^ WorkingUvCentroid;
    /// <summary>삼각 요소 세 정점을 나타내는 모델 XYZ 좌표 아홉 개입니다.</summary>
    property array<double>^ ModelVertices;
    /// <summary>세 변 중점과 중심에서 곡면과 삼각형 사이에 측정한 최대 편차(mm)입니다.</summary>
    property double MaximumMeasuredDeviationMm;
};

/// <summary>유효 영역의 삼각 요소와 전체 메시 면적 오차입니다.</summary>
public ref class WorkingSurfaceElementsResult
{
public:
    /// <summary>표면 요소 질의 상태입니다.</summary>
    property WorkingSurfaceElementQueryStatus Status;
    /// <summary>성공 시 영역 안에 놓인 모든 삼각 요소입니다.</summary>
    property array<WorkingSurfaceElementData^>^ Elements;
    /// <summary>절단 표면의 정확 면적과 메시 합계 차이(mm²)입니다.</summary>
    property double AreaErrorBoundMm2;
};

/// <summary>원본 Face 추적을 보존하는 닫힌 선택 경계 루프입니다.</summary>
public ref class WorkingTopologyBoundaryLoop
{
public:
    /// <summary>중복 닫힘 점을 제외한 순서 있는 모델 좌표 정점입니다.</summary>
    property array<double>^ Points;
    /// <summary>각 정점에서 다음 정점으로 이어지는 구간의 원본 Native Face 번호입니다.</summary>
    property array<int>^ SegmentFaceIndices;
    /// <summary>OCCT가 연결된 닫힌 루프로 확인했는지 나타냅니다.</summary>
    property bool IsClosed;
};

/// <summary>Native Face와 해당 연결 성분의 외곽·홀 경계 목록입니다.</summary>
public ref class WorkingTopologyComponent
{
public:
    /// <summary>연결 성분에 포함된 원본 Native Face 번호입니다.</summary>
    property array<int>^ FaceIndices;
    /// <summary>공유 내부 Edge를 제외한 닫힌 경계 루프입니다.</summary>
    property array<WorkingTopologyBoundaryLoop^>^ BoundaryLoops;
};

/// <summary>공유 Edge로 확인한 두 Native Face의 인접 관계입니다.</summary>
public ref class WorkingTopologyAdjacency
{
public:
    /// <summary>인접 관계의 첫 번째 Native Face 번호입니다.</summary>
    property int FirstFaceIndex;
    /// <summary>인접 관계의 두 번째 Native Face 번호입니다.</summary>
    property int SecondFaceIndex;
};

/// <summary>선택 Face topology 질의의 상태와 연결 성분 payload입니다.</summary>
public ref class WorkingSelectedTopology
{
public:
    /// <summary>Native topology 질의 상태입니다.</summary>
    property WorkingTopologyQueryStatus Status;
    /// <summary>성공한 질의에서 반환된 연결 성분입니다.</summary>
    property array<WorkingTopologyComponent^>^ Components;
    /// <summary>실제 공유 Edge로 확인한 인접 관계입니다.</summary>
    property array<WorkingTopologyAdjacency^>^ Adjacencies;
};

public ref class WorkingSurfaceContext
{
public:
    property long long Id;
    property long long Revision;
    property array<double>^ Frame; // origin, X, Y, N (12 doubles)
    property array<double>^ Triangles; // world XYZ triples, 9 doubles per triangle
    property double MeshDeflection;
};
public ref class WorkingSurfaceHit
{
public:
    property long long ContextId;
    property long long Revision;
    property array<double>^ DatumPoint;
    /// <summary>실제 교차한 원본 Face의 매개변수 좌표입니다.</summary>
    property array<double>^ FaceUv;
    property WorkingSurfaceQueryStatus Status;
    property array<double>^ Point;
    property array<double>^ Normal;
    property int FaceId;
    property bool IsSelectedFace;
};
/// <summary>revision에 종속된 표시/카메라 저장 지점이며 복원 또는 확정 후 해제합니다.</summary>
public ref class WorkingCoordinateSystemPresentation
{
public:
    property long long Id;
    property long long Revision;
    property int BoundaryCount;
    property array<double>^ BoundaryBounds;
};

/// <summary>OCCT 경로 생성에 전달하는 형식별 수학 곡선 정의입니다.</summary>
public ref class CurveDefinitionData
{
public:
    /// <summary>지원하는 곡선 종류를 나타내는 이름입니다.</summary>
    property System::String^ Kind;
    /// <summary>곡선 진행 방향을 나타내는 이름입니다.</summary>
    property System::String^ Direction;
    /// <summary>직선 시작점 XYZ 배열입니다.</summary>
    property array<double>^ Start;
    /// <summary>직선 끝점 XYZ 배열입니다.</summary>
    property array<double>^ End;
    /// <summary>다중선 점들을 XYZ 순서로 평탄화한 배열입니다.</summary>
    property array<double>^ Points;
    /// <summary>원호 프레임의 원점, X축, Y축, 법선을 담은 12개 배열입니다.</summary>
    property array<double>^ Frame;
    /// <summary>원호 중심점 XYZ 배열입니다.</summary>
    property array<double>^ Center;
    /// <summary>원호 반지름(mm)입니다.</summary>
    property double RadiusMm;
    /// <summary>원호 시작각(라디안)입니다.</summary>
    property double StartAngleRad;
    /// <summary>원호 끝각(라디안)입니다.</summary>
    property double EndAngleRad;
    /// <summary>B-spline 차수입니다.</summary>
    property int Degree;
    /// <summary>B-spline 극점들을 XYZ 순서로 평탄화한 배열입니다.</summary>
    property array<double>^ Poles;
    /// <summary>B-spline 극점마다 대응하는 가중치 배열입니다.</summary>
    property array<double>^ Weights;
    /// <summary>B-spline 오름차순 knot 배열입니다.</summary>
    property array<double>^ Knots;
    /// <summary>B-spline knot별 정수 중복도 배열입니다.</summary>
    property array<int>^ Multiplicities;
    /// <summary>B-spline 주기 여부입니다.</summary>
    property bool IsPeriodic;
    /// <summary>B-spline 시작 trim 매개변수입니다.</summary>
    property double TrimStart;
    /// <summary>B-spline 끝 trim 매개변수입니다.</summary>
    property double TrimEnd;
};

/// <summary>Native 곡선 간격 질의의 상태입니다.</summary>
public enum class WorkingCurveClearanceStatus
{
    /// <summary>모든 비국소 곡선 쌍의 최단거리를 계산했습니다.</summary>
    Success = 0,
    /// <summary>곡선 정의나 거리 설정이 유효하지 않습니다.</summary>
    InvalidInput = 1,
    /// <summary>작업 좌표계가 현재 모델 revision에 속하지 않습니다.</summary>
    StaleContext = 2,
    /// <summary>준비 후보가 없거나 이미 폐기되었습니다.</summary>
    CandidateNotFound = 3,
    /// <summary>거리 계산 쌍의 작업 한도를 넘었습니다.</summary>
    PairBudgetExceeded = 4,
    /// <summary>OCCT가 거리 계산을 완료하지 못했습니다.</summary>
    GeometryFailure = 5,
    /// <summary>비국소 곡선 쌍이 없거나 수치 정밀도가 충분하지 않습니다.</summary>
    Unresolved = 6,
    /// <summary>현재 브리지가 알지 못하는 상태입니다.</summary>
    Unknown = 7
};

/// <summary>곡선 간격 질의의 최단점, 오차 범위와 진단입니다.</summary>
public ref class CurveClearanceResultData
{
public:
    /// <summary>Native 질의 종료 상태입니다.</summary>
    property WorkingCurveClearanceStatus Status;
    /// <summary>성공 시 도체 중심선 사이의 최소 거리(mm)입니다.</summary>
    property double MinimumCenterlineDistanceMm;
    /// <summary>첫 번째 곡선의 최단점 XYZ(mm)입니다.</summary>
    property array<double>^ FirstLocation;
    /// <summary>두 번째 곡선의 최단점 XYZ(mm)입니다.</summary>
    property array<double>^ SecondLocation;
    /// <summary>최단거리의 최대 오차 추정치(mm)입니다.</summary>
    property double ErrorBoundMm;
    /// <summary>실패 또는 미확정 사유입니다.</summary>
    property System::String^ Diagnostic;
};

/// <summary>Native 곡선 깊이 envelope 질의가 끝난 상태입니다.</summary>
public enum class WorkingCurveDepthEnvelopeStatus
{
    /// <summary>곡선 표본 전체의 깊이 범위를 계산했습니다.</summary>
    Success = 0,
    /// <summary>곡선·면·깊이 설정이 유효하지 않습니다.</summary>
    InvalidInput = 1,
    /// <summary>선택한 Native Face 번호가 현재 문서에 없습니다.</summary>
    InvalidFace = 2,
    /// <summary>준비 후보가 없거나 이미 폐기되었습니다.</summary>
    CandidateNotFound = 3,
    /// <summary>작업 좌표계가 현재 모델 revision에 속하지 않습니다.</summary>
    StaleContext = 4,
    /// <summary>곡선 표본의 작업 좌표에 표면 교점이 없습니다.</summary>
    Miss = 5,
    /// <summary>표면 광선이 접해 깊이를 정할 수 없습니다.</summary>
    Tangent = 6,
    /// <summary>Native 깊이 측정의 표본 한도를 넘었습니다.</summary>
    ProbeBudgetExceeded = 7,
    /// <summary>OCCT 교차 또는 깊이 계산에 실패했습니다.</summary>
    GeometryFailure = 8,
    /// <summary>표본 또는 오차 근거로 전체 범위를 확정하지 못했습니다.</summary>
    Unresolved = 9,
    /// <summary>현재 브리지가 알지 못하는 상태입니다.</summary>
    Unknown = 10
};

/// <summary>Native 곡선 깊이 envelope의 극값, 위치, 오차와 진단입니다.</summary>
public ref class CurveDepthEnvelopeResultData
{
public:
    /// <summary>Native 깊이 질의 종료 상태입니다.</summary>
    property WorkingCurveDepthEnvelopeStatus Status;
    /// <summary>최소와 최대 깊이의 중간 대표값(mm)입니다.</summary>
    property double MeasuredInsertionDepthMm;
    /// <summary>곡선에서 측정한 최소 삽입 깊이(mm)입니다.</summary>
    property double MinimumInsertionDepthMm;
    /// <summary>곡선에서 측정한 최대 삽입 깊이(mm)입니다.</summary>
    property double MaximumInsertionDepthMm;
    /// <summary>깊이 범위의 최대 오차 추정치(mm)입니다.</summary>
    property double ErrorBoundMm;
    /// <summary>요구 깊이에서 가장 멀리 벗어난 표면 점 XYZ(mm)입니다.</summary>
    property array<double>^ SurfacePoint;
    /// <summary>최소 깊이 표본의 표면 점 XYZ(mm)입니다.</summary>
    property array<double>^ MinimumSurfacePoint;
    /// <summary>최대 깊이 표본의 표면 점 XYZ(mm)입니다.</summary>
    property array<double>^ MaximumSurfacePoint;
    /// <summary>최소 깊이 표본의 도체 바깥점 XYZ(mm)입니다.</summary>
    property array<double>^ MinimumWireOutsidePoint;
    /// <summary>최대 깊이 표본의 도체 바깥점 XYZ(mm)입니다.</summary>
    property array<double>^ MaximumWireOutsidePoint;
    /// <summary>앵커 확인을 포함한 Native 깊이 표본 수입니다.</summary>
    property int ProbeCount;
    /// <summary>실패 또는 미확정 사유입니다.</summary>
    property System::String^ Diagnostic;
};

/// <summary>Native 깊이 root 보정의 최종 중심선과 잔차 근거입니다.</summary>
public ref class CurveDepthAdjustmentResultData
{
public:
    /// <summary>Native 보정 종료 상태입니다.</summary>
    property WorkingCurveDepthEnvelopeStatus Status;
    /// <summary>깊이 보정이 완료된 중심선 XYZ 표본입니다.</summary>
    property array<double>^ CenterlineSamples;
    /// <summary>요구 깊이 방정식의 최대 잔차(mm)입니다.</summary>
    property double MaximumDepthResidualMm;
    /// <summary>근사 편차와 root 잔차를 포함한 최대 오차 추정값(mm)입니다.</summary>
    property double ErrorBoundMm;
    /// <summary>앵커와 경로에서 수행한 표면 first-hit 질의 수입니다.</summary>
    property int ProbeCount;
    /// <summary>실패 또는 미확정 사유입니다.</summary>
    property System::String^ Diagnostic;
};

/// <summary>Native 연결 경로 충돌 질의의 상태입니다.</summary>
public enum class WorkingConnectionObstacleStatus
{
    /// <summary>명시적 금형 이탈 뒤 외부 경로가 금형과 만나지 않습니다.</summary>
    Clear = 0,
    /// <summary>외부 경로에서 금형과 다시 만나는 첫 위치를 찾았습니다.</summary>
    Obstructed = 1,
    /// <summary>곡선 정의나 이탈 구간 번호가 유효하지 않습니다.</summary>
    InvalidInput = 2,
    /// <summary>작업 좌표계가 현재 모델 revision과 다릅니다.</summary>
    StaleContext = 3,
    /// <summary>준비 후보가 존재하지 않거나 이미 폐기되었습니다.</summary>
    CandidateNotFound = 4,
    /// <summary>OCCT 충돌 연산에서 기하 실패가 발생했습니다.</summary>
    GeometryFailure = 5,
    /// <summary>이탈 접점 또는 전체 외부 경로를 확정하지 못했습니다.</summary>
    Unresolved = 6,
    /// <summary>현재 브리지가 알지 못하는 상태입니다.</summary>
    Unknown = 7
};

/// <summary>연결 경로 충돌 위치와 경로 누적 거리를 반환합니다.</summary>
public ref class ConnectionObstacleResultData
{
public:
    /// <summary>Native 연결 경로 질의 종료 상태입니다.</summary>
    property WorkingConnectionObstacleStatus Status;
    /// <summary>막힌 경우의 첫 충돌 좌표 XYZ(mm)입니다.</summary>
    property array<double>^ FirstObstruction;
    /// <summary>경로 시작점부터 충돌 위치까지의 누적 거리(mm)입니다.</summary>
    property double DistanceToObstructionMm;
    /// <summary>질의 실패 또는 미확정 사유입니다.</summary>
    property System::String^ Diagnostic;
};

/// <summary>STEP writer가 반환하는 형상 구성·파일 쓰기 상태입니다.</summary>
public enum class StepDesignWriteStatus
{
    /// <summary>모든 요청 형상을 STEP 파일에 기록했습니다.</summary>
    Success = 0,
    /// <summary>출력 경로나 그룹 입력이 유효하지 않습니다.</summary>
    InvalidInput = 1,
    /// <summary>원본 STEP 모델을 읽지 못했습니다.</summary>
    SourceModelReadFailed = 2,
    /// <summary>곡선 정의 payload가 OCCT 곡선을 만들지 못했습니다.</summary>
    CurveDefinitionInvalid = 3,
    /// <summary>순서 있는 곡선을 하나의 연속 wire로 연결하지 못했습니다.</summary>
    WireBuildFailed = 4,
    /// <summary>STEP entity 전송 또는 파일 쓰기가 실패했습니다.</summary>
    WriteFailed = 5,
    /// <summary>OCCT 경계에서 예상하지 못한 기하 오류가 발생했습니다.</summary>
    GeometryFailure = 6
};

/// <summary>STEP export에 포함할 곡선 하나와 회로 진행 방향입니다.</summary>
public ref class StepDesignCurveData
{
public:
    /// <summary>직선·다중선·원호·B-spline 수학 정의입니다.</summary>
    property CurveDefinitionData^ Definition;
    /// <summary>회로가 정의된 곡선의 역방향을 따라가는지 나타냅니다.</summary>
    property bool IsReversed;
};

/// <summary>순서 있는 곡선 정의를 결합할 하나의 STEP wire입니다.</summary>
public ref class StepDesignWireData
{
public:
    /// <summary>연결 wire 이름입니다.</summary>
    property System::String^ Name;
    /// <summary>연속성을 검사할 순서 있는 곡선 조각입니다.</summary>
    property array<StepDesignCurveData^>^ Curves;
};

/// <summary>회로 또는 미배정 경로를 포함하는 STEP geometry 그룹입니다.</summary>
public ref class StepGeometryGroupData
{
public:
    /// <summary>회로 또는 미배정 경로의 표시 이름입니다.</summary>
    property System::String^ Name;
    /// <summary>그룹에 포함할 발열 wire와 연결 wire입니다.</summary>
    property array<StepDesignWireData^>^ Wires;
};

/// <summary>STEP writer 결과 상태와 사람이 읽을 수 있는 진단입니다.</summary>
public ref class StepDesignWriteResult
{
public:
    /// <summary>Native STEP writer의 종료 상태입니다.</summary>
    property StepDesignWriteStatus Status;
    /// <summary>실패 이유이며 성공 상태에서는 null입니다.</summary>
    property System::String^ Diagnostic;
};

public ref class Bridge abstract sealed
{
public:
    static long long GetModelRevision();
    static WorkingSurfaceContext^ CreateWorkingSurfaceContext(double nx, double ny, double nz, double xx, double xy, double xz);
    static void ReleaseWorkingSurfaceContext(long long id);
    static WorkingSurfaceHit^ QueryWorkingSurface(long long id, long long revision, double u, double v);
    /// <summary>준비 후보 형상에 한정된 작업 좌표계 context를 만듭니다.</summary>
    static WorkingSurfaceContext^ CreatePreparedCandidateWorkingSurfaceContext(
        long long candidateId, double nx, double ny, double nz, double xx, double xy, double xz);
    /// <summary>준비 후보에서 만든 작업 좌표계 context를 해제합니다.</summary>
    static void ReleasePreparedCandidateWorkingSurfaceContext(long long candidateId, long long contextId);
    /// <summary>준비 후보와 그 후보의 작업 좌표계에 한정해 first-hit을 조회합니다.</summary>
    static WorkingSurfaceHit^ QueryPreparedCandidateWorkingSurface(
        long long candidateId, long long contextId, long long revision, double u, double v);
    /// <summary>안정 연결 인덱스를 반영해 활성 문서 곡선의 비국소 최소 간격을 계산합니다.</summary>
    /// <param name="localCurveIndexPairs">곡선 배열에서 이전·다음 인덱스를 번갈아 담은 방향 있는 연결 쌍입니다.</param>
    static CurveClearanceResultData^ MeasureWorkingCurveClearance(
        long long contextId,
        long long revision,
        array<CurveDefinitionData^>^ curves,
        array<int>^ localCurveIndexPairs,
        double wireDiameterMm,
        double maximumErrorMm,
        int maximumPairChecks);
    /// <summary>안정 연결 인덱스를 반영해 준비 후보 곡선의 비국소 최소 간격을 계산합니다.</summary>
    /// <param name="localCurveIndexPairs">곡선 배열에서 이전·다음 인덱스를 번갈아 담은 방향 있는 연결 쌍입니다.</param>
    static CurveClearanceResultData^ MeasurePreparedCandidateWorkingCurveClearance(
        long long candidateId,
        long long contextId,
        long long revision,
        array<CurveDefinitionData^>^ curves,
        array<int>^ localCurveIndexPairs,
        double wireDiameterMm,
        double maximumErrorMm,
        int maximumPairChecks);
    /// <summary>활성 문서에서 선택 Face 기준 최종 곡선 깊이 envelope를 측정합니다.</summary>
    static CurveDepthEnvelopeResultData^ MeasureWorkingCurveDepthEnvelope(
        long long contextId,
        long long revision,
        array<double>^ centerlineSamples,
        array<int>^ selectedFaceIds,
        int anchorFaceId,
        double anchorU,
        double anchorV,
        array<double>^ anchorPoint,
        double wireDiameterMm,
        double requiredInsertionDepthMm,
        double maximumSampleSpacingMm,
        double requestedAngularToleranceRad,
        double maximumMeasuredDeviationMm,
        double connectionToleranceMm,
        int maximumProbeCount);
    /// <summary>준비 후보에서 선택 Face 기준 최종 곡선 깊이 envelope를 측정합니다.</summary>
    static CurveDepthEnvelopeResultData^ MeasurePreparedCandidateWorkingCurveDepthEnvelope(
        long long candidateId,
        long long contextId,
        long long revision,
        array<double>^ centerlineSamples,
        array<int>^ selectedFaceIds,
        int anchorFaceId,
        double anchorU,
        double anchorV,
        array<double>^ anchorPoint,
        double wireDiameterMm,
        double requiredInsertionDepthMm,
        double maximumSampleSpacingMm,
        double requestedAngularToleranceRad,
        double maximumMeasuredDeviationMm,
        double connectionToleranceMm,
        int maximumProbeCount);
    /// <summary>활성 문서의 표면 경로에서 요구 깊이를 만족하는 Native 중심선을 계산합니다.</summary>
    static CurveDepthAdjustmentResultData^ AdjustWorkingCurveInsertionDepth(
        long long contextId,
        long long revision,
        array<double>^ surfacePathWorkingSamples,
        array<int>^ selectedFaceIds,
        int anchorFaceId,
        double anchorU,
        double anchorV,
        array<double>^ anchorPoint,
        double wireDiameterMm,
        double requiredInsertionDepthMm,
        double maximumSampleSpacingMm,
        double requestedAngularToleranceRad,
        double maximumSurfaceDeviationMm,
        double connectionToleranceMm,
        int maximumProbeCount);
    /// <summary>준비 후보에서 표면 경로를 보정하고 최종 Native 중심선을 반환합니다.</summary>
    static CurveDepthAdjustmentResultData^ AdjustPreparedCandidateWorkingCurveInsertionDepth(
        long long candidateId,
        long long contextId,
        long long revision,
        array<double>^ surfacePathWorkingSamples,
        array<int>^ selectedFaceIds,
        int anchorFaceId,
        double anchorU,
        double anchorV,
        array<double>^ anchorPoint,
        double wireDiameterMm,
        double requiredInsertionDepthMm,
        double maximumSampleSpacingMm,
        double requestedAngularToleranceRad,
        double maximumSurfaceDeviationMm,
        double connectionToleranceMm,
        int maximumProbeCount);
    /// <summary>활성 문서에서 명시 이탈 뒤 모든 연결 곡선을 지정 공차로 전체 금형과 검사합니다.</summary>
    static ConnectionObstacleResultData^ CheckConnectionPathObstacles(
        long long contextId,
        long long revision,
        array<CurveDefinitionData^>^ curves,
        int moldExitTransitionIndex,
        double connectionToleranceMm);
    /// <summary>준비 후보에서 후보 revision과 지정 공차로 연결 곡선의 금형 재관통을 검사합니다.</summary>
    static ConnectionObstacleResultData^ CheckPreparedCandidateConnectionPathObstacles(
        long long candidateId,
        long long contextId,
        long long revision,
        array<CurveDefinitionData^>^ curves,
        int moldExitTransitionIndex,
        double connectionToleranceMm);
    /// <summary>준비 후보 session에서 Face 공유 Edge 인접 여부를 조회합니다.</summary>
    static WorkingFaceAdjacencyStatus QueryPreparedCandidateFaceAdjacency(
        long long candidateId, long long contextId, long long revision, int faceA, int faceB);
    /// <summary>준비 후보 session에서 선택 Face의 연결 성분과 경계를 조회합니다.</summary>
    static WorkingSelectedTopology^ QueryPreparedCandidateSelectedTopology(
        long long candidateId, long long contextId, long long revision, array<int>^ faceIds, double deflectionMm);
    /// <summary>준비 후보 session에서 선택 영역에 해당하는 trimmed 표면 요소를 조회합니다.</summary>
    static WorkingSurfaceElementsResult^ QueryPreparedCandidateSurfaceElements(
        long long candidateId,
        long long contextId,
        long long revision,
        array<int>^ faceIds,
        array<WorkingRegionLoopData^>^ effectiveBoundary,
        double deflectionMm,
        double angularToleranceRad);
    /// <summary>전체 모델 first-hit이 선택 Face 목록 중 하나인지 확인합니다.</summary>
    static WorkingSurfaceHit^ QueryWorkingSurfaceForFaces(long long id, long long revision, array<int>^ faceIds, double u, double v);
    /// <summary>Face 인접·입력·stale·기하 실패 상태를 구분해 반환합니다.</summary>
    static WorkingFaceAdjacencyStatus QueryWorkingFaceAdjacency(long long id, long long revision, int faceA, int faceB);
    /// <summary>선택 Face의 공유 Edge 성분, 경계 루프와 구간별 원본 Face를 반환합니다.</summary>
    static WorkingSelectedTopology^ QueryWorkingSelectedTopology(
        long long id, long long revision, array<int>^ faceIds, double deflectionMm);
    /// <summary>선택 Face와 유효 영역의 교집합을 삼각 요소로 나누고 면적 오차를 반환합니다.</summary>
    static WorkingSurfaceElementsResult^ QueryWorkingSurfaceElements(
        long long id,
        long long revision,
        array<int>^ faceIds,
        array<WorkingRegionLoopData^>^ effectiveBoundary,
        double deflectionMm,
        double angularToleranceRad);
    static bool AreWorkingFacesAdjacent(long long id, long long revision, int faceA, int faceB);
    static HeatingPathSegmentResult^ DisplayWorkingPathSegment(long long id, long long revision, int pieceId,
        System::String^ kind, array<double>^ xyzFlat);
    static bool StageWorkingPolyline(long long id, long long revision, int stageId, array<double>^ xyzFlat);
    static HeatingPathSegmentResult^ StageProjectedWorkingPathSegment(long long id, long long revision, int stageId,
        System::String^ kind, array<double>^ xyzFlat, array<int>^ faceIds, double dx, double dy, double dz);
    /// <summary>원형 곡선 정의를 보존해 선택 면에 투영하고 후보 경로로 staging합니다.</summary>
    static HeatingPathSegmentResult^ StageProjectedWorkingCurveSegment(long long id, long long revision, int stageId,
        System::String^ kind, CurveDefinitionData^ definition, array<int>^ faceIds, double dx, double dy, double dz);
    static HeatingPathSegmentResult^ StageProjectedWorkingPolyline(long long id, long long revision, int stageId,
        array<double>^ xyzFlat, array<int>^ faceIds, double dx, double dy, double dz);
    static bool CommitWorkingPathGeometry(long long id, long long revision, array<int>^ stagedPathSegmentIds,
        array<int>^ finalPathSegmentIds, array<int>^ stagedPolylineIds);
    static void DiscardWorkingPathGeometry(long long id, long long revision,
        array<int>^ stagedPathSegmentIds, array<int>^ stagedPolylineIds);
    /// <summary>준비 후보 Face에 곡선을 투영하고 후보 문서 안에서 측정·staging합니다.</summary>
    static HeatingPathSegmentResult^ StagePreparedCandidateWorkingCurveSegment(
        long long candidateId,
        long long contextId,
        long long revision,
        int stageId,
        System::String^ kind,
        CurveDefinitionData^ definition,
        array<int>^ faceIds,
        double dx,
        double dy,
        double dz,
        double requestedDeflectionMm,
        double requestedAngularToleranceRad,
        double maximumSampleSpacingMm,
        int maximumSubdivisionDepth,
        int maximumProbeCount);
    /// <summary>이미 모델 좌표로 정의된 곡선을 다시 투영하지 않고 후보 안에서 측정·staging합니다.</summary>
    static HeatingPathSegmentResult^ StagePreparedCandidateExactCurveSegment(
        long long candidateId,
        long long contextId,
        long long revision,
        int stageId,
        System::String^ kind,
        CurveDefinitionData^ definition,
        double requestedDeflectionMm,
        double requestedAngularToleranceRad,
        double maximumSampleSpacingMm,
        int maximumSubdivisionDepth,
        int maximumProbeCount);
    /// <summary>검증을 통과한 후보 곡선 묶음을 최종 런타임 ID로 확정합니다.</summary>
    static bool CommitPreparedCandidateWorkingPathGeometry(
        long long candidateId,
        long long contextId,
        long long revision,
        array<int>^ stagedPathSegmentIds,
        array<int>^ finalPathSegmentIds);
    /// <summary>후보 작업 context가 만든 미확정 곡선 staging만 폐기합니다.</summary>
    static void DiscardPreparedCandidateWorkingPathGeometry(
        long long candidateId,
        long long contextId,
        long long revision,
        array<int>^ stagedPathSegmentIds);
    static bool IsWorkingPointVisible(long long id, long long revision, double x, double y, double z, double tolerance);
    static void ShowProjectedModelBoundary(array<array<double>^>^ loops);
    static void ShowProjectedModelBoundary(array<array<double>^>^ loops, array<bool>^ holes);
    static bool ReturnToWorkingFront(long long id, long long revision);
    static WorkingCoordinateSystemPresentation^ CaptureWorkingCoordinateSystemPresentation();
    static bool RestoreWorkingCoordinateSystemPresentation(long long id);
    static void ReleaseWorkingCoordinateSystemPresentation(long long id);
    // ---- 로드/기하 (S1 항목 2·3·4) ----
    /// <summary>STEP를 별도 후보 세션에 준비하며 활성 문서와 화면은 바꾸지 않습니다.</summary>
    static ModelLoadCandidateResult^ PrepareStepCandidate(System::String^ path);
    /// <summary>확정 전 후보 문서에서 Face 특징을 읽으며 활성 문서는 변경하지 않습니다.</summary>
    static FaceGeometryFeaturesResult^ GetPreparedCandidateFaceGeometryFeatures(long long candidateId);
    /// <summary>확정 전 후보의 Face UV 경계 와이어를 반환합니다.</summary>
    static array<UvWire^>^ GetPreparedCandidateFaceUvBoundary(long long candidateId, int faceIndex);
    /// <summary>확정 전 후보 Face의 UV 좌표를 모델 XYZ(mm)로 변환합니다.</summary>
    static array<double>^ SamplePreparedCandidateUvTo3D(long long candidateId, int faceIndex, double u, double v);
    /// <summary>기준 revision이 유지된 후보만 활성 문서로 확정하고 표시를 갱신합니다.</summary>
    static ModelLoadResult^ PublishStepCandidate(long long candidateId);
    /// <summary>확정하지 않을 STEP 후보를 폐기하고 임시 Native 자원을 해제합니다.</summary>
    static bool DiscardStepCandidate(long long candidateId);
    /// <summary>STEP를 후보로 읽은 뒤 상태와 진단을 포함한 교체 결과를 반환합니다.</summary>
    static ModelLoadResult^ LoadStepDetailed(System::String^ path);
    /// <summary>STEP 로드 호환 API이며 모델이 확정되면 화면 동기화 상태와 관계없이 true를 반환합니다.</summary>
    static bool LoadStep(System::String^ path);
    /// <summary>활성 모델의 음영 표시를 다시 구성해 화면 동기화를 복구합니다.</summary>
    static bool RebuildModelPresentation();

    /// <summary>확정 곡선을 회로별 wire로 기록하고 선택적으로 원본 STEP 형상을 함께 출력합니다.</summary>
    static StepDesignWriteResult^ WriteDesignStep(
        System::String^ outputPath,
        System::String^ sourceModelPath,
        bool includeSourceModel,
        array<StepGeometryGroupData^>^ groups);

    /// <summary>솔리드·셸·면·평면 수</summary>
    static ShapeInfo^ GetShapeInfo();

    /// <summary>면 타입 이름 (Plane/Cylinder/Cone/Sphere/Torus/BSplineSurface/...)</summary>
    static System::String^ GetFaceType(int faceIndex);

    /// <summary>모든 Face의 면적·중심·표면 종류·공유 Edge 이웃을 계산합니다.</summary>
    static FaceGeometryFeaturesResult^ GetFaceGeometryFeatures();

    /// <summary>면의 UV 경계 폴리곤 — 외곽 먼저, 이후 홀 (V3)</summary>
    static array<UvWire^>^ GetFaceUvBoundary(int faceIndex);
    /// <summary>임시 0.01mm Sewing 후 공유 내부 Edge를 제거하고 외곽 루프를 3D 적응 샘플링해 반환.</summary>
    static array<RegionBoundaryLoop^>^ GetSelectedRegionBoundary(array<int>^ faceIds, double deflection);
    /// <summary>UV → 3D 매핑 (V5)</summary>
    static array<double>^ SampleUvTo3D(int faceIndex, double u, double v);
    /// <summary>선택한 면의 trim 경계 위에 있는 가장 가까운 가시점을 반환합니다.</summary>
    static array<double>^ PickSelectedBoundaryPoint(array<int>^ faceIds, int x, int y);
    /// <summary>원본 wire를 고정 축으로 투영하고 삽입 깊이만큼 이동한 뒤 성공 여부·길이·Edge 수·Wire 수·상태 코드를 반환합니다.</summary>
    static array<double>^ ProjectPathWireOntoSelectedFaces(array<int>^ faceIds, array<double>^ xyzFlat,
                                                           double dx, double dy, double dz, double insertionDepth);
    /// <summary>발열 경로 구간 하나를 투영해 표시하고 같은 안정 ID의 기존 표시 구간을 교체합니다.</summary>
    static HeatingPathSegmentResult^ ProjectHeatingPathSegment(int pieceId, System::String^ kind, array<double>^ xyzFlat,
                                               array<int>^ faceIds, double dx, double dy, double dz, double insertionDepthMm);
    /// <summary>회전 구간 표시를 켜거나 끄며, 끈 구간은 점선 안내선으로 표시합니다.</summary>
    static bool SetTurnSegmentEnabled(int pieceId, bool enabled, double r, double g, double b);
    static void ClearPathSegments();
    /// <summary>기준점에서 목적점까지 발열 면을 따라 투영한 뒤 외부 연결 경로를 이어 만듭니다.
    /// 밀리미터 단위로 [상태(1=성공, 음수=실패), 전체, 표면, 외부] 길이를 반환합니다.</summary>
    static array<double>^ ProjectConnectionPath(int connectionPathId, array<double>^ endpointAndConnectionPoint,
        array<double>^ actualPathEndpoint, array<int>^ faceIds, double dx, double dy, double dz, double insertionDepthMm);
    /// <summary>미리보기 모드에서는 같은 검증을 수행하고 임시 청록색 기하만 교체합니다.</summary>
    static array<double>^ ProjectConnectionPath(int connectionPathId, array<double>^ endpointAndConnectionPoint,
        array<double>^ actualPathEndpoint, array<int>^ faceIds, double dx, double dy, double dz, double insertionDepthMm,
        bool previewOnly);
    /// <summary>임시 연결점·경로 미리보기만 제거하고 확정 경로는 유지합니다.</summary>
    static void ClearConnectionPathPreview();
    /// <summary>제어점 후보의 임시 곡선만 교체하고 확정 경로는 유지합니다.</summary>
    static bool SetControlPointEditPreview(array<array<double>^>^ xyzPolylines);
    /// <summary>제어점 편집 미리보기만 제거하고 확정 경로는 유지합니다.</summary>
    static void ClearControlPointEditPreview();
    static void ClearConnectionPaths();
    /// <summary>화면 좌표 아래의 회전 구간 ID를 반환하며, 해당 구간이 없으면 -1을 반환합니다.</summary>
    static int PickTurnSegment(int x, int y);
    static int PickHeatingPathSegment(int x, int y);
    static void HighlightCircuit(array<int>^ pieceIds);

    // ---- AIS 뷰어 (S1 항목 1·6) — WPF HwndHost 자식 HWND 임베드 ----
    /// <summary>자식 HWND에 OpenGl 뷰어 초기화 (1회). 이미 로드된 셰이프가 있으면 표시+Fit</summary>
    static bool InitViewer(System::IntPtr hwnd, int width, int height);

    /// <summary>Application 종료 시 Native 문서와 Viewer 자원을 순서대로 해제합니다.</summary>
    static void Shutdown();

    /// <summary>창 크기 변경 반영 (MustBeResized + Redraw)</summary>
    static void ResizeView(int width, int height);

    /// <summary>전체 형상이 보이도록 맞춘 뒤 화면을 다시 그립니다.</summary>
    static void FitView();

    /// <summary>회전 시작 (마우스 다운 좌표)</summary>
    static void RotateStart(int x, int y);

    /// <summary>회전 계속 (마우스 이동 좌표) — 내부적으로 Redraw</summary>
    static void RotateMove(int x, int y);

    /// <summary>회전 종료 (현재는 no-op — StartRotation/Rotation 쌍으로 충분)</summary>
    static void RotateEnd();
    static void PanStart(int x, int y);
    static void PanMove(int x, int y);
    static void PanEnd();

    /// <summary>상대 줌 (factor &gt; 1 확대, &lt; 1 축소) — 내부적으로 Redraw</summary>
    static void ZoomView(double factor);
    /// <summary>커서 위치를 기준으로 휠 줌 (deltaY는 화면 픽셀 단위)</summary>
    static void ZoomViewAtPoint(int x, int y, int deltaY);

    /// <summary>표시 셰이프 색상 변경 (RGB 0~1) — SetColor API 스모크</summary>
    static void SetShapeColor(double r, double g, double b);

    // ---- 면 하이라이트/선택 (W5 — 마우스 호버·클릭) ----
    /// <summary>호버: 커서 아래 면을 동적 하이라이트하고 faceId 반환 (변경 시만 Redraw, 없으면 -1)</summary>
    static int HoverFace(int x, int y);
    /// <summary>선택 정보는 보존하고 선택/호버 하이라이트 숨김. 다음 면 선택 시 복원.</summary>
    static void HideFaceHighlights();

    /// <summary>호버 하이라이트 해제 + Redraw (마우스가 창을 벗어날 때)</summary>
    static void ClearHover();

    /// <summary>클릭: 커서 아래 면을 선택(하이라이트 유지)하고 faceId 반환 (빈 곳이면 선택 해제 후 -1)</summary>
    static int SelectFace(int x, int y, bool toggle);

    // ---- 오버레이 (S3 통합 데모) ----
    static void RedrawView();
    static void ClearOverlays();

    /// <summary>방향과 화면 위쪽 벡터로 카메라를 설정</summary>
    static void SetViewFrame(double dx, double dy, double dz, double ux, double uy, double uz);
    /// <summary>로드된 형상에서 자동 추정한 주축(0=X, 1=Y, 2=Z)</summary>
    static array<double>^ GetAutoAxis(int axis);
    static array<int>^ SelectFacesInRectangle(int x0, int y0, int x1, int y1, bool toggle);
    static void SetSelectionRectangle(int x0, int y0, int x1, int y1);
    static void ClearSelectionRectangle();
    static array<double>^ ScreenPointOnPlane(int x, int y, double ox, double oy, double oz, double nx, double ny, double nz);
    static bool AddExclusionOverlay(array<double>^ xyzFlat);
    static void SetExclusionPreview(array<double>^ xyzFlat);
    static void ClearExclusionOverlays();

    /// <summary>현재 모델에 품질 표본 위치를 강조 표시합니다.</summary>
    static bool ShowQualityIssueLocation(double x, double y, double z);
    /// <summary>품질 표본 위치 강조 표시를 제거합니다.</summary>
    static void ClearQualityIssueLocation();


    // ---- HLR (S1 항목 5, S3 측면 뷰에서 본격 사용) ----
    /// <summary>현재 셰이프를 축측(1,1,1) 방향으로 HLR 투영 — 가시/은선 엣지 수 + 소요 ms</summary>
    /// <summary>전체 형상을 N 방향으로 HLR 투영하고 평면 X/Y 좌표의 폴리라인으로 반환합니다.</summary>
    static array<HlrPolyline^>^ HlrEdges(double nx, double ny, double nz,
                                         double xx, double xy, double xz, bool hiddenToo);
};

}}
