using System.Collections.Generic;
using HeatingMold.Application.Ports.Files;
using HeatingMold.Application.Ports.Export;
using HeatingMold.Application.Ports.Geometry;
using HeatingMold.Application.Ports.Runtime;
using HeatingMold.Application.Sessions;
using HeatingMold.Application.Transactions;
using HeatingMold.Application.UseCases;
using HeatingMold.App.Geometry.Occt;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Infrastructure.Export;
using HeatingMold.Infrastructure.Geometry.Occt;
using HeatingMold.Infrastructure.Persistence;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

/// <summary>화면이 사용할 애플리케이션 유스케이스와 Infrastructure 구현을 조립합니다.</summary>
internal static class AppCompositionRoot
{
    private static ProjectSession? _activeSession;

    /// <summary>실행 창과 현재 연결된 유스케이스를 만듭니다.</summary>
    internal static MainWindow CreateMainWindow() => new(CreateMainWindowServices());

    /// <summary>Application 종료 순서에 따라 Native 문서와 Viewer를 정리합니다.</summary>
    internal static void Shutdown()
    {
        try
        {
            Bridge.Shutdown();
        }
        finally
        {
            _activeSession?.Dispose();
            _activeSession = null;
        }
    }

    /// <summary>새 화면이 공유할 세션, 모델 열기 흐름, 원본 저장소와 알림 전달기를 조립합니다.</summary>
    internal static MainWindowServices CreateMainWindowServices(
        ExportOrthographicViews exportOrthographicViews = null,
        IDesignExporter designExporter = null,
        System.Func<string, bool> confirmHeatingPathRegeneration = null)
    {
        _activeSession?.Dispose();
        var session = new ProjectSession();
        _activeSession = session;
        var events = new ApplicationEventAdapter();
        var geometryWorkspace = new OcctGeometryWorkspace();
        var coordinator = new CandidateCoordinator(
            session,
            geometryWorkspace,
            new SingleThreadOperationContext(),
            events);
        var assessHeatingPathRegeneration = new AssessHeatingPathRegeneration();
        var openModel = new OpenModel(session, coordinator);
        var createHeatingRegion = new CreateHeatingRegion(session, coordinator);
        var updateRegion = new UpdateRegion(session, coordinator);
        var generateHeatingPath = new GenerateHeatingPath(
            session,
            coordinator,
            regenerationAssessment: assessHeatingPathRegeneration);
        var moveControlPoint = new MoveControlPoint(session, coordinator);
        var editTurnSegmentCurve = new EditTurnSegmentCurve(session, coordinator);
        var changeCircuitTopology = new ChangeCircuitTopology(session, coordinator);
        var updateConnectionPath = new UpdateConnectionPath(session, coordinator);
        var updateElectricalSpecification = new UpdateElectricalSpecification(
            session,
            coordinator,
            geometryWorkspace);
        IProjectPackageStore packageStore = new ProjectPackageStore();
        var openProject = new OpenProject(session, packageStore, coordinator);
        var saveProject = new SaveProject(session, packageStore);
        var exportDesign = new ExportDesign(session, designExporter ?? CreateDesignExporter());
        IModelSourceStore modelSourceStore = new StepModelSourceStore(
            sourceUnitToMillimeter: 1,
            importerVersion: "OCCT-7.9.3",
            faceFingerprintVersion: FaceReferenceFeatureBuilder.FingerprintVersion);

        return new MainWindowServices(
            exportOrthographicViews ?? CreateOrthographicExportUseCase(),
            exportDesign,
            openModel,
            createHeatingRegion,
            updateRegion,
            assessHeatingPathRegeneration,
            generateHeatingPath,
            moveControlPoint,
            editTurnSegmentCurve,
            changeCircuitTopology,
            updateConnectionPath,
            updateElectricalSpecification,
            openProject,
            saveProject,
            modelSourceStore,
            geometryWorkspace,
            session,
            events,
            confirmHeatingPathRegeneration);
    }

    /// <summary>기존 HLR 정사영 DXF 출력을 포트와 파일 작성기로 연결합니다.</summary>
    internal static ExportOrthographicViews CreateOrthographicExportUseCase() =>
        new(new OrthographicDxfExporter());

    /// <summary>설계 DXF와 OCCT STEP writer를 형식별 출력 포트에 연결합니다.</summary>
    internal static IDesignExporter CreateDesignExporter() =>
        new CompositeDesignExporter(
            stepWriter: new StepDesignWriter(new OcctStepGeometryWriterBridgeAdapter()));

    /// <summary>OCCT 후보 문서와 Infrastructure 기하 adapter를 작업 workspace로 연결합니다.</summary>
    internal static IGeometryWorkspace CreateGeometryWorkspace() => new OcctGeometryWorkspace();

    /// <summary>현재 Native 작업 좌표계, Face 대응과 기하 공차 설정을 질의 어댑터로 조립합니다.</summary>
    internal static OcctGeometryQueries CreateOcctGeometryQueries(
        WorkingCoordinateSystem coordinateSystem,
        IReadOnlyDictionary<FaceRefId, int> nativeFaceIndices,
        GeometryToleranceProfile tolerances = null) =>
        new(new WorkingSurfaceBridgeAdapter(), coordinateSystem, nativeFaceIndices, tolerances);

    /// <summary>준비 Native 후보에 고정된 기하 질의 어댑터를 조립합니다.</summary>
    internal static OcctGeometryQueries CreatePreparedCandidateOcctGeometryQueries(
        long candidateId,
        WorkingCoordinateSystem coordinateSystem,
        IReadOnlyDictionary<FaceRefId, int> nativeFaceIndices,
        GeometryToleranceProfile tolerances = null) =>
        new(new WorkingSurfaceBridgeAdapter(candidateId), coordinateSystem, nativeFaceIndices, tolerances);

    /// <summary>후보 context session의 후보 ID와 좌표계로 Native 기하 질의 어댑터를 조립합니다.</summary>
    internal static OcctGeometryQueries CreatePreparedCandidateOcctGeometryQueries(
        PreparedCandidateWorkingSurfaceSession session,
        IReadOnlyDictionary<FaceRefId, int> nativeFaceIndices,
        GeometryToleranceProfile tolerances = null)
    {
        if (session is null)
            throw new System.ArgumentNullException(nameof(session));

        return CreatePreparedCandidateOcctGeometryQueries(
            session.CandidateId,
            session.CoordinateSystem,
            nativeFaceIndices,
            tolerances);
    }

    /// <summary>화면 생성에 필요한 Application 의존성을 한 묶음으로 보관합니다.</summary>
    internal sealed record MainWindowServices(
        ExportOrthographicViews ExportOrthographicViews,
        ExportDesign ExportDesign,
        OpenModel OpenModel,
        CreateHeatingRegion CreateHeatingRegion,
        UpdateRegion UpdateRegion,
        AssessHeatingPathRegeneration AssessHeatingPathRegeneration,
        GenerateHeatingPath GenerateHeatingPath,
        MoveControlPoint MoveControlPoint,
        EditTurnSegmentCurve EditTurnSegmentCurve,
        ChangeCircuitTopology ChangeCircuitTopology,
        UpdateConnectionPath UpdateConnectionPath,
        UpdateElectricalSpecification UpdateElectricalSpecification,
        OpenProject OpenProject,
        SaveProject SaveProject,
        IModelSourceStore ModelSourceStore,
        OcctGeometryWorkspace GeometryWorkspace,
        ProjectSession ProjectSession,
        ApplicationEventAdapter Events,
        System.Func<string, bool> ConfirmHeatingPathRegeneration);
}
