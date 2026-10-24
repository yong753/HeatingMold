#nullable enable annotations

using System;
using HeatingMold.App;
using HeatingMold.App.Geometry.Occt;
using HeatingMold.App.ViewModels;
using HeatingMold.Application.Sessions;
using HeatingMold.Application.Ports.Runtime;
using HeatingMold.Application.Transactions;
using HeatingMold.Application.UseCases;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;

namespace HeatingMold.App.Tests;

/// <summary>전기 화면 명령이 Application 초안 경계와 확정 상태를 보존하는지 검사합니다.</summary>
public sealed class ElectricalSpecificationViewModelTests
{
    /// <summary>초안 명령은 확정 프로젝트를 바꾸지 않고 편집 값을 별도 draft로 보관합니다.</summary>
    [Fact]
    public void CreateDraftCommand_StoresApplicationDraftWithoutChangingCommittedProject()
    {
        var (session, project, viewModel, wireSpecification) = CreateViewModel();
        wireSpecification.MaterialText = "NiCr-80";
        wireSpecification.ResistanceText = "350";

        viewModel.CreateDraftCommand.Execute(null);

        InputDraft draft = Assert.IsType<InputDraft>(session.Current.CurrentInputDraft);
        Assert.Equal(InputDraftKind.ElectricalSpecification, draft.Kind);
        Assert.Same(project, session.Current.CurrentProject);
        Assert.Equal("NiCr-80", draft.CandidateProject.WireSpecification!.MaterialName);
        Assert.Equal(new ResistanceOhmsPerMeter(350), draft.CandidateProject.WireSpecification.ResistanceOhmsPerMeter);
        Assert.Contains("초안을 만들었습니다", viewModel.StatusText);
    }

    /// <summary>기하 snapshot이 없는 초안은 적용되지 않고 입력과 session을 보존합니다.</summary>
    [Fact]
    public void ApplyDraftCommand_ReportsMissingGeometryAndKeepsDraft()
    {
        var (session, _, viewModel, wireSpecification) = CreateViewModel();
        wireSpecification.MaterialText = "NiCr-80";
        wireSpecification.ResistanceText = "350";
        viewModel.CreateDraftCommand.Execute(null);
        InputDraft draft = session.Current.CurrentInputDraft!;
        SessionSnapshot beforeApply = session.Current;
        SessionSnapshot? notified = null;
        viewModel.StateChanged += snapshot => notified = snapshot;

        viewModel.ApplyDraftCommand.Execute(null);

        Assert.Same(beforeApply, session.Current);
        Assert.Same(draft, session.Current.CurrentInputDraft);
        Assert.Same(session.Current, notified);
        Assert.Contains("전기 사양을 적용하지 못했습니다", viewModel.StatusText);
    }

    /// <summary>취소 명령은 확정 프로젝트를 유지하고 전기 초안만 지웁니다.</summary>
    [Fact]
    public void CancelDraftCommand_ClearsDraftAndNotifiesResultsPanel()
    {
        var (session, project, viewModel, wireSpecification) = CreateViewModel();
        wireSpecification.MaterialText = "NiCr-80";
        wireSpecification.ResistanceText = "350";
        viewModel.CreateDraftCommand.Execute(null);
        SessionSnapshot? notified = null;
        viewModel.StateChanged += snapshot => notified = snapshot;

        viewModel.CancelDraftCommand.Execute(null);

        Assert.Same(project, session.Current.CurrentProject);
        Assert.Null(session.Current.CurrentInputDraft);
        Assert.Same(session.Current, notified);
        Assert.Contains("초안을 취소했습니다", viewModel.StatusText);
    }

    private static (
        ProjectSession Session,
        DesignProject Project,
        ElectricalSpecificationViewModel ViewModel,
        WireSpecificationInputsViewModel WireSpecification) CreateViewModel()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var identity = new ModelIdentity(new string('a', 64), 1, "test-importer", "1", "face-v1");
        var project = new DesignProject(
            ProjectId.New(),
            1,
            new DesignRevision(3),
            "전기 ViewModel 회귀",
            now,
            now,
            identity,
            new WorkingCoordinateSystemDefinition(
                new Vec3(0, 0, 0),
                new Vec3(1, 0, 0),
                new Vec3(0, 1, 0),
                new Vec3(0, 0, 1)),
            [],
            [],
            new WireSpecification("NiCr-60", new LengthMm(0.3), new ResistanceOhmsPerMeter(250)),
            null,
            [],
            [],
            [],
            []);
        var initialSnapshot = new SessionSnapshot(
            project,
            runtimeDocument: null,
            geometry: null,
            electricalResults: [],
            areaAllocation: null,
            electricalAreaResult: null,
            currentInputDraft: null,
            dirtySinceSave: false,
            lastSavedRevision: null,
            isPresentationOutOfSync: false);
        var session = new ProjectSession(initialSnapshot);
        var geometryWorkspace = new OcctGeometryWorkspace();
        var coordinator = new CandidateCoordinator(
            session,
            geometryWorkspace,
            new SingleThreadOperationContext(),
            new ApplicationEventAdapter());
        var useCase = new UpdateElectricalSpecification(session, coordinator, geometryWorkspace);
        var wireSpecification = new WireSpecificationInputsViewModel();
        var viewModel = new ElectricalSpecificationViewModel(session, useCase, wireSpecification);
        return (session, project, viewModel, wireSpecification);
    }
}
