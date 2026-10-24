using System.Collections.ObjectModel;
using HeatingMold.Core.Area;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Electrical.Area;
using HeatingMold.Application.Ports.Geometry;
using HeatingMold.Design.Quality;

namespace HeatingMold.Application.Sessions;

/// <summary>화면·파일·Native가 공유하는 확정 설계와 최신 파생 결과입니다.</summary>
public sealed class SessionSnapshot
{
    /// <summary>현재 확정 설계가 없으면 null입니다.</summary>
    public DesignProject? CurrentProject { get; }

    /// <summary>현재 설계와 연결된 열린 Native 문서입니다.</summary>
    public RuntimeDocument? RuntimeDocument { get; }

    /// <summary>패키지 저장에 사용할 원본 STEP 바이트입니다.</summary>
    public ModelSource? SourceModel { get; }

    /// <summary>현재 확정 설계로 준비한 Native 기하 결과입니다.</summary>
    public GeometrySnapshot? Geometry { get; }

    /// <summary>회로별 최신 전기 계산 결과입니다.</summary>
    public IReadOnlyList<ElectricalResult> ElectricalResults { get; }

    /// <summary>Design가 분할한 실제 표면 면적과 회로 배정 결과입니다.</summary>
    public AppliedAreaAllocation? AreaAllocation { get; }

    /// <summary>회로 전기 상태로 나눈 면적 합계입니다.</summary>
    public ElectricallyCompliantAreaResult? ElectricalAreaResult { get; }

    /// <summary>확정 기하에서 계산한 영역별 간격·표면 커버리지 관측 결과입니다.</summary>
    public IReadOnlyList<CenterPitchQualityReport> CenterPitchQualityReports { get; }

    /// <summary>확정 전 사용자 입력이 있으면 해당 초안입니다.</summary>
    public InputDraft? CurrentInputDraft { get; }

    /// <summary>현재 설계가 마지막 성공 저장 뒤 변경되었는지 나타냅니다.</summary>
    public bool DirtySinceSave { get; }

    /// <summary>마지막으로 정상 저장한 확정 revision입니다.</summary>
    public DesignRevision? LastSavedRevision { get; }

    /// <summary>확정 설계와 Native 표시 상태가 일치하지 않는지 나타냅니다.</summary>
    public bool IsPresentationOutOfSync { get; }

    /// <summary>현재 확정 데이터를 기준으로 기본 작업 흐름을 계산합니다.</summary>
    public WorkflowState WorkflowState => GetWorkflowState(isBusy: false);

    /// <summary>완전히 비어 있는 프로젝트 세션 스냅샷입니다.</summary>
    public static SessionSnapshot Empty { get; } = new(
        null, null, null, Array.Empty<ElectricalResult>(), null, null,
        null, false, null, false);

    /// <summary>서로 같은 문서·revision에 속한 확정 상태와 파생 결과를 보관합니다.</summary>
    public SessionSnapshot(
        DesignProject? currentProject,
        RuntimeDocument? runtimeDocument,
        GeometrySnapshot? geometry,
        IEnumerable<ElectricalResult> electricalResults,
        AppliedAreaAllocation? areaAllocation,
        ElectricallyCompliantAreaResult? electricalAreaResult,
        InputDraft? currentInputDraft,
        bool dirtySinceSave,
        DesignRevision? lastSavedRevision,
        bool isPresentationOutOfSync,
        ModelSource? sourceModel = null,
        IEnumerable<CenterPitchQualityReport>? centerPitchQualityReports = null)
    {
        ElectricalResult[] copiedResults = electricalResults?.ToArray()
            ?? throw new ArgumentNullException(nameof(electricalResults));
        CenterPitchQualityReport[] copiedQualityReports = centerPitchQualityReports?.ToArray()
            ?? Array.Empty<CenterPitchQualityReport>();
        if (copiedResults.Any(result => result is null))
            throw new ArgumentException("세션에는 null 전기 결과를 보관할 수 없습니다.", nameof(electricalResults));
        if (copiedQualityReports.Any(report => report is null))
            throw new ArgumentException("세션에는 null 품질 결과를 보관할 수 없습니다.", nameof(centerPitchQualityReports));
        if (currentProject is null && (runtimeDocument is not null || sourceModel is not null || geometry is not null || copiedResults.Length > 0 || areaAllocation is not null || copiedQualityReports.Length > 0))
            throw new ArgumentException("프로젝트가 없는 세션은 모델과 파생 결과를 소유할 수 없습니다.");
        if (currentProject is not null && runtimeDocument is not null &&
            currentProject.SourceModel.SourceSha256 != runtimeDocument.SourceModelSha256)
        {
            throw new ArgumentException("열린 모델 원본 해시와 확정 프로젝트 원본 해시가 다릅니다.");
        }
        if (currentProject is not null && sourceModel is not null &&
            currentProject.SourceModel.SourceSha256 != sourceModel.Identity.SourceSha256)
        {
            throw new ArgumentException("보관한 원본 모델 바이트 해시와 확정 프로젝트 원본 해시가 다릅니다.");
        }
        if (geometry is not null &&
            (runtimeDocument is null || geometry.DocumentId != runtimeDocument.DocumentId || geometry.ModelRevision != runtimeDocument.ModelRevision))
        {
            throw new ArgumentException("기하 결과는 현재 런타임 문서와 모델 revision에 속해야 합니다.");
        }
        if (currentProject is not null && geometry is not null &&
            geometry.DependencyStamp.DesignRevision != currentProject.Revision)
        {
            throw new ArgumentException("기하 결과와 확정 설계 revision이 다릅니다.");
        }
        if (geometry is not null && copiedQualityReports.Any(report => report.DependencyStamp != geometry.DependencyStamp))
            throw new ArgumentException("품질 결과와 현재 기하 snapshot의 DependencyStamp가 다릅니다.");

        CurrentProject = currentProject;
        RuntimeDocument = runtimeDocument;
        SourceModel = sourceModel;
        Geometry = geometry;
        ElectricalResults = new ReadOnlyCollection<ElectricalResult>(copiedResults);
        AreaAllocation = areaAllocation;
        ElectricalAreaResult = electricalAreaResult;
        CenterPitchQualityReports = new ReadOnlyCollection<CenterPitchQualityReport>(copiedQualityReports);
        CurrentInputDraft = currentInputDraft;
        DirtySinceSave = dirtySinceSave;
        LastSavedRevision = lastSavedRevision;
        IsPresentationOutOfSync = isPresentationOutOfSync;
    }

    /// <summary>실행 중 상태를 반영해 현재 사용 가능한 작업 단계를 반환합니다.</summary>
    public WorkflowState GetWorkflowState(bool isBusy)
    {
        if (isBusy)
            return WorkflowState.Busy;
        if (IsPresentationOutOfSync)
            return WorkflowState.PresentationOutOfSync;
        if (CurrentInputDraft is not null)
            return WorkflowState.DraftPending;
        if (CurrentProject is null)
            return WorkflowState.Empty;
        if (CurrentProject.Circuits.Count > 0 || CurrentProject.HeatingPaths.Count > 0)
            return WorkflowState.DesignReady;
        if (CurrentProject.WireSpecification is not null && CurrentProject.PlacementConstraints is not null)
            return WorkflowState.ConstraintsReady;

        return WorkflowState.ModelReady;
    }

    /// <summary>확정 설계를 바꾸지 않고 새 입력 초안을 보관합니다.</summary>
    public SessionSnapshot WithDraft(InputDraft? draft) => new(
        CurrentProject, RuntimeDocument, Geometry, ElectricalResults,
        AreaAllocation, ElectricalAreaResult, draft,
        DirtySinceSave, LastSavedRevision, IsPresentationOutOfSync, SourceModel, CenterPitchQualityReports);

    /// <summary>확정 상태를 유지하면서 화면 표시가 어긋났음을 표시합니다.</summary>
    public SessionSnapshot WithPresentationOutOfSync() => new(
        CurrentProject, RuntimeDocument, Geometry, ElectricalResults,
        AreaAllocation, ElectricalAreaResult, CurrentInputDraft,
        DirtySinceSave, LastSavedRevision, isPresentationOutOfSync: true, sourceModel: SourceModel,
        centerPitchQualityReports: CenterPitchQualityReports);

    /// <summary>현재 revision만 저장한 경우 dirty 상태를 해제합니다.</summary>
    public SessionSnapshot WithSavedRevision(DesignRevision revision)
    {
        if (CurrentProject is null || CurrentProject.Revision != revision)
            throw new InvalidOperationException("현재 확정 프로젝트 revision만 저장 완료로 표시할 수 있습니다.");

        return new SessionSnapshot(
            CurrentProject, RuntimeDocument, Geometry, ElectricalResults,
            AreaAllocation, ElectricalAreaResult, CurrentInputDraft,
            dirtySinceSave: false,
            lastSavedRevision: revision,
            isPresentationOutOfSync: IsPresentationOutOfSync,
            sourceModel: SourceModel,
            centerPitchQualityReports: CenterPitchQualityReports);
    }
}

/// <summary>현재 확정 설계에서 가능한 사용자 작업 단계입니다.</summary>
public enum WorkflowState
{
    /// <summary>열린 프로젝트가 없습니다.</summary>
    Empty,

    /// <summary>모델과 좌표계가 준비되었습니다.</summary>
    ModelReady,

    /// <summary>경로 생성 제약과 도체 사양을 입력할 수 있습니다.</summary>
    ConstraintsReady,

    /// <summary>발열 경로와 회로가 확정되었습니다.</summary>
    DesignReady,

    /// <summary>현재 설계를 유지하며 변경 초안의 적용 또는 취소가 필요합니다.</summary>
    DraftPending,

    /// <summary>Native 기하 변경이 진행 중이므로 새 변경을 시작할 수 없습니다.</summary>
    Busy,

    /// <summary>설계는 확정되었지만 화면 장면 재구성이 필요합니다.</summary>
    PresentationOutOfSync
}
