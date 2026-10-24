using HeatingMold.Core.Circuits;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;

namespace HeatingMold.Infrastructure.Persistence.SchemaV1;

/// <summary>프로젝트 스키마 1의 영속 설계 구조입니다.</summary>
internal sealed record ProjectV1Dto(
    Guid ProjectId,
    int SchemaVersion,
    long DesignRevision,
    string Name,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ModifiedUtc,
    ModelIdentityV1Dto SourceModel,
    WorkingCoordinateSystemV1Dto WorkingCoordinateSystem,
    FaceReferenceV1Dto[] FaceReferences,
    HeatingRegionV1Dto[] HeatingRegions,
    WireSpecificationV1Dto? WireSpecification,
    PlacementConstraintsV1Dto? PlacementConstraints,
    HeatingPathV1Dto[] HeatingPaths,
    CircuitV1Dto[] Circuits,
    ConnectionPointV1Dto[] ConnectionPoints,
    ConnectionPathV1Dto[] ConnectionPaths,
    DesignRuleSetV1Dto Rules);

/// <summary>원본 파일과 가져오기 규칙을 고정 형식으로 기록합니다.</summary>
internal sealed record ModelIdentityV1Dto(
    string SourceSha256,
    double SourceUnitToMillimeter,
    string ImporterName,
    string ImporterVersion,
    string FaceFingerprintVersion);

/// <summary>저장 모델 기준의 오른손 작업 좌표계를 기록합니다.</summary>
internal sealed record WorkingCoordinateSystemV1Dto(
    Vec3V1Dto Origin,
    Vec3V1Dto AxisX,
    Vec3V1Dto AxisY,
    Vec3V1Dto Normal);

/// <summary>밀리미터 작업 평면의 2차원 좌표를 기록합니다.</summary>
internal sealed record Vec2V1Dto(double X, double Y);

/// <summary>밀리미터 모델 좌표의 세 성분을 기록합니다.</summary>
internal sealed record Vec3V1Dto(double X, double Y, double Z);

/// <summary>재열기에서 다시 대응할 원본 Face 특징을 기록합니다.</summary>
internal sealed record FaceReferenceV1Dto(
    Guid FaceReferenceId,
    string SourceModelSha256,
    int ImportIndexHint,
    FaceGeometryKind GeometryKind,
    double SurfaceAreaMm2,
    Vec3V1Dto Center,
    string BoundaryFingerprint,
    string[] AdjacentFaceFingerprints);

/// <summary>원본·포함·제외·유효 경계와 출구 측면을 기록합니다.</summary>
internal sealed record HeatingRegionV1Dto(
    Guid RegionId,
    Guid[] FaceReferenceIds,
    BoundaryLoopV1Dto[] OriginalBoundaryLoops,
    BoundaryLoopV1Dto[] InclusionLoops,
    BoundaryLoopV1Dto[] ExclusionLoops,
    BoundaryLoopV1Dto[] EffectiveBoundaryLoops,
    ExitSideV1Dto ExitSide);

/// <summary>방향과 안정 구간 ID를 가진 닫힌 작업 평면 경계를 기록합니다.</summary>
internal sealed record BoundaryLoopV1Dto(Guid BoundaryLoopId, bool IsHole, BoundarySegmentV1Dto[] Segments);

/// <summary>안정 ID·양 끝점과 선택 Face 참조를 가진 경계 구간을 기록합니다.</summary>
internal sealed record BoundarySegmentV1Dto(
    Guid BoundarySegmentId,
    Vec2V1Dto Start,
    Vec2V1Dto End,
    Guid? SourceFaceReferenceId);

/// <summary>경계 구간 ID와 정규화된 시작·끝 매개변수를 기록합니다.</summary>
internal sealed record BoundarySegmentRangeV1Dto(Guid BoundarySegmentId, double StartParameter, double EndParameter);

/// <summary>경계 구간 일부로 지정한 공통 출구 측면을 기록합니다.</summary>
internal sealed record ExitSideV1Dto(BoundarySegmentRangeV1Dto[] Ranges);

/// <summary>도체 재료명, 지름과 단위 길이 저항을 기록합니다.</summary>
internal sealed record WireSpecificationV1Dto(
    string MaterialName,
    double DiameterMm,
    double ResistanceOhmsPerMeter);

/// <summary>간격·경계 여유·삽입 깊이·방향과 양 끝 앵커를 기록합니다.</summary>
internal sealed record PlacementConstraintsV1Dto(
    double MinimumClearGapMm,
    double BoundaryMarginMm,
    double InsertionDepthMm,
    InsertionDepthReference InsertionDepthReference,
    PlacementDirection Direction,
    SurfaceAnchorV1Dto StartAnchor,
    SurfaceAnchorV1Dto EndAnchor,
    ExitSideV1Dto ExitSide);

/// <summary>Face UV, 작업 UV와 모델 점을 함께 가진 표면 앵커를 기록합니다.</summary>
internal sealed record SurfaceAnchorV1Dto(
    Guid FaceReferenceId,
    Vec2V1Dto FaceUv,
    Vec2V1Dto WorkingUv,
    Vec3V1Dto ModelPoint);

/// <summary>생성 근거와 생성 원본·현재 편집 정의를 모두 기록합니다.</summary>
internal sealed record HeatingPathV1Dto(
    Guid HeatingPathId,
    HeatingPathGenerationSourceV1Dto GenerationSource,
    ControlPointV1Dto[] ControlPoints,
    HeatingPathSegmentV1Dto[] GeneratedSegments,
    HeatingPathSegmentV1Dto[] Segments);

/// <summary>경로 생성에 사용한 영역·제약·도체·규칙을 기록합니다.</summary>
internal sealed record HeatingPathGenerationSourceV1Dto(
    Guid RegionId,
    PlacementConstraintsV1Dto PlacementConstraints,
    WireSpecificationV1Dto WireSpecification,
    string RuleVersion);

/// <summary>공유 절점의 영속 ID와 최종 모델 좌표를 기록합니다.</summary>
internal sealed record ControlPointV1Dto(Guid ControlPointId, Vec3V1Dto Position);

/// <summary>안정 ID·역할·순서·활성 상태와 원형 곡선을 기록합니다.</summary>
internal sealed record HeatingPathSegmentV1Dto(
    Guid HeatingPathSegmentId,
    HeatingPathSegmentKind Kind,
    Guid StartControlPointId,
    Guid EndControlPointId,
    int Sequence,
    int? LaneIndex,
    bool IsActive,
    CurveDefinitionV1Dto Curve);

/// <summary>곡선 종류별 원 정의와 trim·가중치 정보를 기록합니다.</summary>
internal sealed record CurveDefinitionV1Dto
{
    public string CurveType { get; init; } = string.Empty;
    public CurveDirection Direction { get; init; }
    public Vec3V1Dto? Start { get; init; }
    public Vec3V1Dto? End { get; init; }
    public Vec3V1Dto[]? Points { get; init; }
    public ProjectionFrameV1Dto? Plane { get; init; }
    public Vec3V1Dto? Center { get; init; }
    public double? RadiusMm { get; init; }
    public double? StartAngleRad { get; init; }
    public double? EndAngleRad { get; init; }
    public int? Degree { get; init; }
    public Vec3V1Dto[]? Poles { get; init; }
    public double[]? Weights { get; init; }
    public double[]? Knots { get; init; }
    public int[]? Multiplicities { get; init; }
    public bool? IsPeriodic { get; init; }
    public double? TrimStart { get; init; }
    public double? TrimEnd { get; init; }
}

/// <summary>원호 기준 평면의 원점과 오른손 기저를 기록합니다.</summary>
internal sealed record ProjectionFrameV1Dto(
    Vec3V1Dto Origin,
    Vec3V1Dto AxisX,
    Vec3V1Dto Normal);

/// <summary>회로 순서·진행 방향·시작 및 종료 연결부·Supply를 기록합니다.</summary>
internal sealed record CircuitV1Dto(
    Guid CircuitId,
    int Sequence,
    CircuitSegmentReferenceV1Dto[] Segments,
    Guid StartConnectionPointId,
    Guid EndConnectionPointId,
    SupplyV1Dto Supply);

/// <summary>회로 내 경로 조각 ID·순서·역방향 여부를 기록합니다.</summary>
internal sealed record CircuitSegmentReferenceV1Dto(Guid SegmentId, int Sequence, bool IsReversed);

/// <summary>회로별 실제 전압과 선택 한도를 기록합니다.</summary>
internal sealed record SupplyV1Dto(
    double? ActualVoltageVrms,
    double? MaximumVoltageVrms,
    double? MaximumCurrentAmps,
    double? MaximumPowerWatts);

/// <summary>회로 끝의 표면 앵커와 외부 연결 좌표를 기록합니다.</summary>
internal sealed record ConnectionPointV1Dto(
    Guid ConnectionPointId,
    Guid CircuitId,
    ConnectionPointRole Role,
    Guid HeatingPathSegmentId,
    Guid ControlPointId,
    SurfaceAnchorV1Dto SurfaceAnchor,
    Vec3V1Dto ExternalConnectionPoint);

/// <summary>한 연결부까지 이어지는 순서 있는 연결 경로를 기록합니다.</summary>
internal sealed record ConnectionPathV1Dto(
    Guid ConnectionPathId,
    Guid ConnectionPointId,
    ConnectionPathSegmentV1Dto[] Segments);

/// <summary>연결 곡선·재료·공간 역할·여유·금형 이탈을 기록합니다.</summary>
internal sealed record ConnectionPathSegmentV1Dto(
    Guid ConnectionPathSegmentId,
    int Sequence,
    ConductorRole ConductorRole,
    GeometryDomain GeometryDomain,
    double AdditionalAllowanceMm,
    bool IsMoldExitTransition,
    CurveDefinitionV1Dto Curve);

/// <summary>설계 규칙 버전과 기하 허용오차 설정을 기록합니다.</summary>
internal sealed record DesignRuleSetV1Dto(
    string GenerationRuleVersion,
    string ValidationRuleVersion,
    string AreaRuleVersion,
    string ElectricalRuleVersion,
    GeometryToleranceProfileV1Dto GeometryTolerances);

/// <summary>곡선 매핑·메시·연속성 질의의 수치 설정을 기록합니다.</summary>
internal sealed record GeometryToleranceProfileV1Dto(
    double ConnectionToleranceMm,
    double MeshDeflectionMm,
    double MeshAngularToleranceRad,
    double MappingLeafLengthMm,
    double MappingChordDeviationMm,
    int MappingMaximumDepth,
    int MappingMaximumProbes,
    double MaximumContinuousSampleSpacingMm);
