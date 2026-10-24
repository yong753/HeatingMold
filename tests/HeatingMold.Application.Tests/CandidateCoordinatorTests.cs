using HeatingMold.Application.Ports.Geometry;
using HeatingMold.Application.Ports.Runtime;
using HeatingMold.Application.Sessions;
using HeatingMold.Application.Transactions;
using HeatingMold.Application.UseCases;
using HeatingMold.Core.Area;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;
using HeatingMold.Design.ConnectionPath;
using HeatingMold.Design.Circuits;
using HeatingMold.Design.HeatingPath;
using HeatingMold.Design.Editing;
using HeatingMold.Design.Regions;

namespace HeatingMold.Application.Tests;

/// <summary>변경 후보의 실패 보존과 publish 이후 처리 경계를 검증합니다.</summary>
public sealed partial class CandidateCoordinatorTests
{
    /// <summary>검증 뒤 Publish와 세션 확정을 수행하고 staging을 한 번 정리합니다.</summary>
    [Fact]
    public void Execute_CommitsPreparedCandidateAndNotifiesAfterCommit()
    {
        Fixture fixture = Fixture.Create();
        CandidateOutcome outcome = fixture.Execute(fixture.NextProject);

        Assert.Equal(CandidateStatus.Committed, outcome.Status);
        Assert.Same(fixture.NextProject, fixture.Session.Current.CurrentProject);
        Assert.True(fixture.Session.Current.DirtySinceSave);
        Assert.Equal(1, fixture.Workspace.Prepared!.PublishCount);
        Assert.Equal(1, fixture.Workspace.Prepared.DisposeCount);
        Assert.Equal(ApplicationEventKind.SessionChanged, Assert.Single(fixture.Events.Published).Kind);
        Assert.Equal(WorkflowState.ModelReady, fixture.Session.WorkflowState);
    }

    /// <summary>영역 제외 편집은 해당 영역의 경로만 무효화하고 새 영역을 함께 확정합니다.</summary>
    [Fact]
    public void UpdateRegionAdjustment_CommitsExclusionAndInvalidatesDependentPath()
    {
        (DesignProject project, RegionGraph target, RegionGraph other) = CreateProjectWithTwoRegionPaths();
        var fixture = Fixture.Create(
            project,
            new ProjectDerivedStateBuilder(designValidationOverride: (_, _) => new ValidationReport([])),
            includeInitialHeatingCurves: true);
        var updateRegion = new UpdateRegion(fixture.Session, fixture.Coordinator);
        IReadOnlyList<Vec2>[] exclusions =
        [
            new Vec2[] { new(5, 5), new(10, 5), new(10, 10), new(5, 10) }
        ];

        CandidateOutcome outcome = updateRegion.ApplyAdjustment(
            target.Region.Id,
            [],
            exclusions,
            project.Revision);

        Assert.Equal(CandidateStatus.Committed, outcome.Status);
        DesignProject committed = fixture.Session.Current.CurrentProject!;
        HeatingRegion adjusted = Assert.Single(committed.HeatingRegions.Where(region => region.Id == target.Region.Id));
        Assert.Single(adjusted.ExclusionLoops);
        Assert.Contains(adjusted.EffectiveBoundaryLoops, loop => loop.IsHole);
        Assert.DoesNotContain(committed.HeatingPaths, path => path.Id == target.Path.Id);
        Assert.Contains(committed.HeatingPaths, path => path.Id == other.Path.Id);
        Assert.DoesNotContain(committed.Circuits, circuit => circuit.Id == target.Circuit!.Id);
        Assert.Equal(1, fixture.Workspace.Prepared!.PublishCount);
        Assert.Single(fixture.Events.Published);
    }

    /// <summary>제외가 유일한 출구 측면을 지우면 기존 프로젝트와 Native 후보를 그대로 둡니다.</summary>
    [Fact]
    public void UpdateRegionAdjustment_RejectsRemovingExitSideBeforePreparingCandidate()
    {
        (DesignProject project, RegionGraph target, _) = CreateProjectWithTwoRegionPaths();
        var fixture = Fixture.Create(project, includeInitialHeatingCurves: true);
        var updateRegion = new UpdateRegion(fixture.Session, fixture.Coordinator);
        SessionSnapshot before = fixture.Session.Current;
        IReadOnlyList<Vec2>[] exclusions =
        [
            new Vec2[] { new(-1, -2), new(21, -2), new(21, 2), new(-1, 2) }
        ];

        CandidateOutcome outcome = updateRegion.ApplyAdjustment(
            target.Region.Id,
            [],
            exclusions,
            project.Revision);

        Assert.Equal(CandidateStatus.ValidationFailed, outcome.Status);
        Assert.Contains(outcome.Issues, issue => issue.Code == "ExitSideRemovedByAdjustment");
        Assert.Same(before, fixture.Session.Current);
        Assert.Null(fixture.Workspace.Prepared);
        Assert.Empty(fixture.Events.Published);
    }

    /// <summary>기하 준비 예외는 기존 프로젝트와 파생 기하를 그대로 둡니다.</summary>
    [Fact]
    public void Execute_PrepareFailurePreservesCurrentSnapshot()
    {
        Fixture fixture = Fixture.Create();
        SessionSnapshot before = fixture.Session.Current;
        fixture.Workspace.PrepareException = new InvalidOperationException("fixture failure");

        CandidateOutcome outcome = fixture.Execute(fixture.NextProject);

        Assert.Equal(CandidateStatus.PrepareFailed, outcome.Status);
        Assert.Same(before, fixture.Session.Current);
        Assert.Equal(0, fixture.Workspace.Prepared?.PublishCount ?? 0);
        Assert.Empty(fixture.Events.Published);
    }

    /// <summary>활성 경로나 연결 구간의 Native 곡선 측정값이 누락되면 후보를 확정하지 않습니다.</summary>
    [Fact]
    public void Execute_RejectsPreparedGeometryWithMissingCurveMeasurements()
    {
        DesignProject project = CreateProjectWithTwoRegionPaths().Project;
        Fixture fixture = Fixture.Create(project);
        SessionSnapshot before = fixture.Session.Current;
        fixture.Workspace.OmitAllCurves = true;

        CandidateOutcome outcome = fixture.Execute(fixture.NextProject);

        Assert.Equal(CandidateStatus.PrepareFailed, outcome.Status);
        ValidationIssue issue = Assert.Single(outcome.Issues);
        Assert.Equal("CandidatePreparationFailed", issue.Code);
        Assert.Contains("곡선 ID 집합", Assert.Single(issue.Evidence.Skip(1)));
        Assert.Same(before, fixture.Session.Current);
        Assert.Equal(0, fixture.Workspace.Prepared!.PublishCount);
        Assert.Equal(1, fixture.Workspace.Prepared.DisposeCount);
        Assert.Empty(fixture.Events.Published);
    }

    /// <summary>곡선 ID가 같아도 준비된 정의가 설계와 다르면 Native Publish를 차단합니다.</summary>
    [Fact]
    public void Execute_RejectsPreparedGeometryWithMismatchedCurveDefinition()
    {
        DesignProject project = CreateProjectWithTwoRegionPaths().Project;
        Fixture fixture = Fixture.Create(project);
        SessionSnapshot before = fixture.Session.Current;
        fixture.Workspace.ChangePreparedCurveDefinition = true;

        CandidateOutcome outcome = fixture.Execute(fixture.NextProject);

        Assert.Equal(CandidateStatus.PrepareFailed, outcome.Status);
        ValidationIssue issue = Assert.Single(outcome.Issues);
        Assert.Equal("CandidatePreparationFailed", issue.Code);
        Assert.Contains("곡선 정의", Assert.Single(issue.Evidence.Skip(1)));
        Assert.Same(before, fixture.Session.Current);
        Assert.Equal(0, fixture.Workspace.Prepared!.PublishCount);
        Assert.Equal(1, fixture.Workspace.Prepared.DisposeCount);
        Assert.Empty(fixture.Events.Published);
    }

    /// <summary>연결 구간도 저장한 곡선 정의와 다르면 Native Publish를 차단합니다.</summary>
    [Fact]
    public void Execute_RejectsPreparedGeometryWithMismatchedConnectionCurveDefinition()
    {
        DesignProject project = CreateProjectWithTwoRegionPaths().Project;
        Fixture fixture = Fixture.Create(project);
        SessionSnapshot before = fixture.Session.Current;
        fixture.Workspace.ChangePreparedConnectionCurveDefinition = true;

        CandidateOutcome outcome = fixture.Execute(fixture.NextProject);

        Assert.Equal(CandidateStatus.PrepareFailed, outcome.Status);
        ValidationIssue issue = Assert.Single(outcome.Issues);
        Assert.Equal("CandidatePreparationFailed", issue.Code);
        Assert.Contains("연결 곡선 정의", Assert.Single(issue.Evidence.Skip(1)));
        Assert.Same(before, fixture.Session.Current);
        Assert.Equal(0, fixture.Workspace.Prepared!.PublishCount);
        Assert.Equal(1, fixture.Workspace.Prepared.DisposeCount);
        Assert.Empty(fixture.Events.Published);
    }

    /// <summary>완료되지 않은 곡선 표본 근사가 있으면 파생 검증이나 Native Publish 전에 후보를 폐기합니다.</summary>
    [Fact]
    public void Execute_RejectsPreparedGeometryWithIncompleteCurveApproximation()
    {
        DesignProject project = CreateProjectWithTwoRegionPaths().Project;
        Fixture fixture = Fixture.Create(project);
        SessionSnapshot before = fixture.Session.Current;
        fixture.Workspace.MarkPreparedApproximationIncomplete = true;

        CandidateOutcome outcome = fixture.Execute(fixture.NextProject);

        Assert.Equal(CandidateStatus.PrepareFailed, outcome.Status);
        ValidationIssue issue = Assert.Single(outcome.Issues);
        Assert.Equal("CandidatePreparationFailed", issue.Code);
        Assert.Contains("완료되지 않은 곡선 표본 근사", Assert.Single(issue.Evidence.Skip(1)));
        Assert.Same(before, fixture.Session.Current);
        Assert.Equal(0, fixture.Workspace.Prepared!.PublishCount);
        Assert.Equal(1, fixture.Workspace.Prepared.DisposeCount);
        Assert.Empty(fixture.Events.Published);
    }

    /// <summary>파생 결과 검증 실패는 Native Publish 전에 후보를 폐기합니다.</summary>
    [Fact]
    public void Execute_DerivedValidationFailureDoesNotPublish()
    {
        Fixture fixture = Fixture.Create();
        fixture.Derive = _ => OperationResult<CandidateDerivedState>.Failure(
        [
            new ValidationIssue("MinimumClearanceViolation", ValidationSeverity.Error,
                "Validation.MinimumClearanceViolation", [], "clearance-v1")
        ]);

        CandidateOutcome outcome = fixture.Execute(fixture.NextProject);

        Assert.Equal(CandidateStatus.ValidationFailed, outcome.Status);
        Assert.Same(fixture.InitialSnapshot, fixture.Session.Current);
        Assert.Equal(0, fixture.Workspace.Prepared!.PublishCount);
        Assert.Equal(1, fixture.Workspace.Prepared.DisposeCount);
    }

    /// <summary>취소가 파생 계산 중 발생하면 Publish 없이 후보만 정리합니다.</summary>
    [Fact]
    public void Execute_CancellationAfterPrepareKeepsPreviousState()
    {
        Fixture fixture = Fixture.Create();
        using var cancellation = new CancellationTokenSource();
        fixture.Derive = _ =>
        {
            cancellation.Cancel();
            return OperationResult<CandidateDerivedState>.Success(new CandidateDerivedState());
        };

        CandidateOutcome outcome = fixture.Execute(fixture.NextProject, cancellation.Token);

        Assert.Equal(CandidateStatus.Cancelled, outcome.Status);
        Assert.Same(fixture.InitialSnapshot, fixture.Session.Current);
        Assert.Equal(0, fixture.Workspace.Prepared!.PublishCount);
        Assert.Equal(1, fixture.Workspace.Prepared.DisposeCount);
    }

    /// <summary>Native 확정 후 알림 예외가 나도 새 설계를 되돌리지 않고 복구 상태로 둡니다.</summary>
    [Fact]
    public void Execute_PostCommitNotificationFailureKeepsNewDesign()
    {
        Fixture fixture = Fixture.Create();
        fixture.Events.ThrowOnPublish = true;

        CandidateOutcome outcome = fixture.Execute(fixture.NextProject);

        Assert.Equal(CandidateStatus.PostCommitFailure, outcome.Status);
        Assert.Same(fixture.NextProject, fixture.Session.Current.CurrentProject);
        Assert.True(fixture.Session.Current.IsPresentationOutOfSync);
        Assert.Equal(WorkflowState.PresentationOutOfSync, fixture.Session.WorkflowState);
        Assert.Equal(1, fixture.Workspace.Prepared!.PublishCount);
    }

    /// <summary>Native 화면 재구성이 실패해도 commit된 새 설계를 보존하고 복구를 요청합니다.</summary>
    [Fact]
    public void Execute_NativePresentationFailureCommitsDesignAndRequestsRepair()
    {
        Fixture fixture = Fixture.Create();
        fixture.Workspace.PublishPresentationOutOfSync = true;

        CandidateOutcome outcome = fixture.Execute(fixture.NextProject);

        Assert.Equal(CandidateStatus.PostCommitFailure, outcome.Status);
        Assert.Equal("NativePresentationOutOfSync", Assert.Single(outcome.Issues).Code);
        Assert.Same(fixture.NextProject, fixture.Session.Current.CurrentProject);
        Assert.True(fixture.Session.Current.IsPresentationOutOfSync);
        Assert.Equal(WorkflowState.PresentationOutOfSync, fixture.Session.WorkflowState);
        Assert.Equal(ApplicationEventKind.PresentationRepairNeeded, Assert.Single(fixture.Events.Published).Kind);
        Assert.Equal(1, fixture.Workspace.Prepared!.PublishCount);
    }

    /// <summary>캡처한 BaseRevision이 오래되면 기하 후보도 만들지 않습니다.</summary>
    [Fact]
    public void Execute_StaleBaseRevisionRejectsCandidateBeforePrepare()
    {
        Fixture fixture = Fixture.Create();

        CandidateOutcome outcome = fixture.Coordinator.Execute(
            fixture.NextProject,
            new DesignRevision(12),
            _ => fixture.Workspace.Prepare(fixture.NextProject),
            fixture.Derive);

        Assert.Equal(CandidateStatus.Stale, outcome.Status);
        Assert.Same(fixture.InitialSnapshot, fixture.Session.Current);
        Assert.Null(fixture.Workspace.Prepared);
    }

    /// <summary>모델 축 변경과 전기 사양 변경은 서로 다른 재계산 범위를 가집니다.</summary>
    [Fact]
    public void ChangeImpactPolicy_SeparatesGeometryAndElectricalOnlyChanges()
    {
        ChangeImpact model = ChangeImpactPolicy.For(ChangeKind.ModelOrWorkingCoordinateSystem);
        ChangeImpact electrical = ChangeImpactPolicy.For(ChangeKind.ElectricalSpecification);

        Assert.True(model.HasFlag(ChangeImpact.Geometry));
        Assert.True(model.HasFlag(ChangeImpact.Area));
        Assert.True(electrical.HasFlag(ChangeImpact.Electrical));
        Assert.True(electrical.HasFlag(ChangeImpact.Export));
        Assert.False(electrical.HasFlag(ChangeImpact.Geometry));
        Assert.False(electrical.HasFlag(ChangeImpact.Area));
    }

    /// <summary>같은 작업축 재선택은 revision과 확정 설계를 건드리지 않습니다.</summary>
    [Fact]
    public void ChangeWorkingAxis_SameValueIsNoOp()
    {
        Fixture fixture = Fixture.Create();
        SessionSnapshot before = fixture.Session.Current;
        var useCase = new ChangeWorkingAxis(fixture.Session, fixture.Coordinator);

        CandidateOutcome outcome = useCase.Execute(before.CurrentProject!.WorkingCoordinateSystem);

        Assert.Equal(CandidateStatus.NoOp, outcome.Status);
        Assert.Same(before, fixture.Session.Current);
        Assert.Null(fixture.Workspace.Prepared);
    }

    /// <summary>새 작업축을 확정하면 이전 영역·경로·회로를 같은 revision에서 제거합니다.</summary>
    [Fact]
    public void ChangeWorkingAxis_ClearsGeometryDependentDesign()
    {
        Fixture fixture = Fixture.Create();
        var nextAxis = new WorkingCoordinateSystemDefinition(
            new Vec3(10, 20, 30),
            new Vec3(0, 1, 0),
            new Vec3(0, 0, 1),
            new Vec3(1, 0, 0));
        var useCase = new ChangeWorkingAxis(fixture.Session, fixture.Coordinator);

        CandidateOutcome outcome = useCase.Execute(nextAxis);

        Assert.Equal(CandidateStatus.Committed, outcome.Status);
        Assert.Equal(new DesignRevision(1), fixture.Session.Current.CurrentProject!.Revision);
        Assert.Equal(nextAxis, fixture.Session.Current.CurrentProject.WorkingCoordinateSystem);
        Assert.Empty(fixture.Session.Current.CurrentProject.HeatingRegions);
        Assert.Empty(fixture.Session.Current.CurrentProject.HeatingPaths);
        Assert.Empty(fixture.Session.Current.CurrentProject.Circuits);
    }

    /// <summary>존재하지 않는 영역 ID를 갱신하려는 요청은 Native 준비 전에 거부합니다.</summary>
    [Fact]
    public void UpdateRegion_RejectsRegionThatIsNotInTheCurrentProject()
    {
        Fixture fixture = Fixture.Create();
        HeatingRegion region = CreateRegion();
        var useCase = new UpdateRegion(fixture.Session, fixture.Coordinator);

        CandidateOutcome outcome = useCase.Execute(region, fixture.Session.Current.CurrentProject!.Revision);

        Assert.Equal(CandidateStatus.ValidationFailed, outcome.Status);
        Assert.Equal("HeatingRegionNotFound", Assert.Single(outcome.Issues).Code);
        Assert.Null(fixture.Workspace.Prepared);
    }

    /// <summary>영역을 갱신하면 그 영역의 경로만 제거하고 다른 영역의 경로는 보존합니다.</summary>
    [Fact]
    public void UpdateRegion_ReplacesRegionAndRemovesOnlyItsGeneratedPath()
    {
        (DesignProject project, RegionGraph target, RegionGraph other) = CreateProjectWithTwoRegionPaths();
        Fixture fixture = Fixture.Create(project);
        HeatingRegion replacement = CreateRegion(
            target.Region.Id,
            target.Region.FaceReferences[0],
            xOffset: 5);
        var useCase = new UpdateRegion(fixture.Session, fixture.Coordinator);

        CandidateOutcome outcome = useCase.Execute(replacement, project.Revision);

        Assert.True(
            outcome.Status == CandidateStatus.Committed,
            $"결과: {outcome.Status}, 진단: {string.Join(", ", outcome.Issues.Select(issue => issue.Code))}");
        DesignProject committedProject = fixture.Session.Current.CurrentProject!;
        Assert.Equal(project.Revision.Next(), committedProject.Revision);
        Assert.Same(replacement, Assert.Single(committedProject.HeatingRegions.Where(region => region.Id == replacement.Id)));
        Assert.Contains(committedProject.HeatingRegions, region => region.Id == other.Region.Id);
        Assert.DoesNotContain(committedProject.HeatingPaths, path => path.Id == target.Path.Id);
        Assert.Contains(committedProject.HeatingPaths, path => path.Id == other.Path.Id);
        Assert.DoesNotContain(committedProject.Circuits, circuit => circuit.Id == target.Circuit!.Id);
        Assert.All(target.ConnectionPoints, point => Assert.DoesNotContain(committedProject.ConnectionPoints, current => current.Id == point.Id));
        Assert.All(target.ConnectionPaths, path => Assert.DoesNotContain(committedProject.ConnectionPaths, current => current.Id == path.Id));
        Assert.True(fixture.Session.Current.DirtySinceSave);
        Assert.Equal(1, fixture.Workspace.Prepared!.PublishCount);
    }

    /// <summary>선택 topology에서 새 영역을 만들고 후보 확정기를 통해 다음 revision으로 반영합니다.</summary>
    [Fact]
    public void CreateHeatingRegion_BuildsAndCommitsSelectedTopology()
    {
        (DesignProject project, FaceReference faceReference) = CreateProjectWithFaceReference();
        Fixture fixture = Fixture.Create(project);
        BoundaryLoop sourceLoop = CreateLoop(faceReference.Id);
        var topology = new SelectedTopology(
            [new TopologyComponent([faceReference.Id], [sourceLoop])],
            []);
        var geometryQueries = new FakeGeometryQueries
        {
            SelectedTopologyResult = GeometryQueryResult<SelectedTopology>.Succeeded(topology)
        };
        var exitSide = new ExitSideReference(
            [new BoundarySegmentRange(sourceLoop.Segments[0].Id, 0, 1)]);
        RegionId regionId = RegionId.New();
        var useCase = new CreateHeatingRegion(fixture.Session, fixture.Coordinator);

        CandidateOutcome outcome = useCase.Execute(
            regionId,
            [faceReference.Id],
            exitSide,
            project.Revision,
            geometryQueries);

        Assert.Equal(CandidateStatus.Committed, outcome.Status);
        DesignProject committed = fixture.Session.Current.CurrentProject!;
        HeatingRegion region = Assert.Single(committed.HeatingRegions);
        Assert.Equal(project.Revision.Next(), committed.Revision);
        Assert.Equal(regionId, region.Id);
        Assert.Equal(sourceLoop.Segments.Select(segment => segment.Id),
            region.OriginalBoundaryLoops.Single().Segments.Select(segment => segment.Id));
        Assert.NotEqual(
            sourceLoop.Segments[0].Id,
            region.ExitSide.Ranges.Single().BoundarySegmentId);
        Assert.Equal(1, geometryQueries.SelectedTopologyQueryCount);
        Assert.Equal(1, fixture.Workspace.Prepared!.PublishCount);
    }

    /// <summary>WPF가 선택한 revision topology를 재질의하지 않고 영역 확정에 재사용합니다.</summary>
    [Fact]
    public void CreateHeatingRegion_FromTopology_ReusesStampedSelectionAndCommits()
    {
        (DesignProject project, FaceReference faceReference) = CreateProjectWithFaceReference();
        Fixture fixture = Fixture.Create(project);
        BoundaryLoop sourceLoop = CreateLoop(faceReference.Id);
        ModelRevision modelRevision = fixture.InitialSnapshot.Geometry!.ModelRevision;
        var topology = new SelectedTopology(
            [new TopologyComponent([faceReference.Id], [sourceLoop])],
            [],
            modelRevision);
        var topologyResult = GeometryQueryResult<SelectedTopology>.Succeeded(topology);
        var exitSide = new ExitSideReference(
            [new BoundarySegmentRange(sourceLoop.Segments[0].Id, 0, 1)]);
        var useCase = new CreateHeatingRegion(fixture.Session, fixture.Coordinator);

        CandidateOutcome outcome = useCase.ExecuteFromTopology(
            RegionId.New(),
            [faceReference.Id],
            exitSide,
            project.Revision,
            modelRevision,
            topologyResult);

        Assert.Equal(CandidateStatus.Committed, outcome.Status);
        Assert.Single(fixture.Session.Current.CurrentProject!.HeatingRegions);
        Assert.Equal(1, fixture.Workspace.Prepared!.PublishCount);
    }

    /// <summary>확정된 모델 revision과 다른 선택 topology는 Native 준비 전에 거부합니다.</summary>
    [Fact]
    public void CreateHeatingRegion_FromTopology_RejectsStaleModelRevision()
    {
        (DesignProject project, FaceReference faceReference) = CreateProjectWithFaceReference();
        Fixture fixture = Fixture.Create(project);
        BoundaryLoop sourceLoop = CreateLoop(faceReference.Id);
        var topology = new SelectedTopology(
            [new TopologyComponent([faceReference.Id], [sourceLoop])],
            [],
            new ModelRevision(fixture.InitialSnapshot.Geometry!.ModelRevision.Value + 1));
        var topologyResult = GeometryQueryResult<SelectedTopology>.Succeeded(topology);
        var exitSide = new ExitSideReference(
            [new BoundarySegmentRange(sourceLoop.Segments[0].Id, 0, 1)]);
        var useCase = new CreateHeatingRegion(fixture.Session, fixture.Coordinator);

        CandidateOutcome outcome = useCase.ExecuteFromTopology(
            RegionId.New(),
            [faceReference.Id],
            exitSide,
            project.Revision,
            fixture.InitialSnapshot.Geometry.ModelRevision,
            topologyResult);

        Assert.Equal(CandidateStatus.ValidationFailed, outcome.Status);
        Assert.Equal("StaleSelectedTopology", Assert.Single(outcome.Issues).Code);
        Assert.Same(fixture.InitialSnapshot, fixture.Session.Current);
        Assert.Null(fixture.Workspace.Prepared);
    }

    /// <summary>기준 revision이 오래되었으면 topology 질의와 기하 준비를 시작하지 않습니다.</summary>
    [Fact]
    public void CreateHeatingRegion_RejectsStaleRevisionBeforeQuery()
    {
        (DesignProject project, FaceReference faceReference) = CreateProjectWithFaceReference();
        Fixture fixture = Fixture.Create(project);
        var geometryQueries = new FakeGeometryQueries();
        var useCase = new CreateHeatingRegion(fixture.Session, fixture.Coordinator);

        CandidateOutcome outcome = useCase.Execute(
            RegionId.New(),
            [faceReference.Id],
            CreateExitSideForFace(faceReference.Id),
            project.Revision.Next(),
            geometryQueries);

        Assert.Equal(CandidateStatus.ValidationFailed, outcome.Status);
        Assert.Equal("StaleRegionCreation", Assert.Single(outcome.Issues).Code);
        Assert.Same(fixture.InitialSnapshot, fixture.Session.Current);
        Assert.Equal(0, geometryQueries.SelectedTopologyQueryCount);
        Assert.Null(fixture.Workspace.Prepared);
    }

    /// <summary>불일치하는 Native Face 집합은 확정하지 않고 기존 snapshot을 보존합니다.</summary>
    [Fact]
    public void CreateHeatingRegion_RejectsTopologyForDifferentFaceSet()
    {
        (DesignProject project, FaceReference faceReference) = CreateProjectWithFaceReference();
        Fixture fixture = Fixture.Create(project);
        var geometryQueries = new FakeGeometryQueries
        {
            SelectedTopologyResult = GeometryQueryResult<SelectedTopology>.Succeeded(
                new SelectedTopology([], []))
        };
        var useCase = new CreateHeatingRegion(fixture.Session, fixture.Coordinator);

        CandidateOutcome outcome = useCase.Execute(
            RegionId.New(),
            [faceReference.Id],
            CreateExitSideForFace(faceReference.Id),
            project.Revision,
            geometryQueries);

        Assert.Equal(CandidateStatus.ValidationFailed, outcome.Status);
        Assert.Equal("TopologyFaceSetMismatch", Assert.Single(outcome.Issues).Code);
        Assert.Same(fixture.InitialSnapshot, fixture.Session.Current);
        Assert.Equal(1, geometryQueries.SelectedTopologyQueryCount);
        Assert.Null(fixture.Workspace.Prepared);
    }

    /// <summary>분리된 영역 topology와 Native 경고를 확정 결과에 함께 보존합니다.</summary>
    [Fact]
    public void CreateHeatingRegion_PreservesTopologyAndMultipleComponentWarnings()
    {
        (DesignProject project, FaceReference[] faceReferences) = CreateProjectWithFaceReferences(2);
        Fixture fixture = Fixture.Create(project);
        BoundaryLoop firstLoop = CreateLoop(faceReferences[0].Id);
        BoundaryLoop secondLoop = CreateLoop(faceReferences[1].Id, xOffset: 30);
        ValidationIssue topologyWarning = new(
            "TopologyNotice",
            ValidationSeverity.Warning,
            "Geometry.TopologyNotice",
            faceReferences.Select(face => face.Id.Value),
            "geometry-query-v1",
            evidence: ["fixture topology warning"]);
        var topologyResult = new GeometryQueryResult<SelectedTopology>(
            GeometryQueryStatus.Success,
            new SelectedTopology(
                [
                    new TopologyComponent([faceReferences[0].Id], [firstLoop]),
                    new TopologyComponent([faceReferences[1].Id], [secondLoop])
                ],
                []),
            [topologyWarning]);
        var geometryQueries = new FakeGeometryQueries { SelectedTopologyResult = topologyResult };
        var exitSide = new ExitSideReference(
            [new BoundarySegmentRange(firstLoop.Segments[0].Id, 0, 1)]);
        var useCase = new CreateHeatingRegion(fixture.Session, fixture.Coordinator);

        CandidateOutcome outcome = useCase.Execute(
            RegionId.New(),
            faceReferences.Select(face => face.Id).ToArray(),
            exitSide,
            project.Revision,
            geometryQueries);

        Assert.Equal(CandidateStatus.Committed, outcome.Status);
        Assert.Contains(outcome.Issues, issue => issue.Code == "TopologyNotice");
        Assert.Contains(outcome.Issues, issue => issue.Code == "MultipleRegionComponents");
        Assert.Equal(2, Assert.Single(fixture.Session.Current.CurrentProject!.HeatingRegions).OriginalBoundaryLoops.Count);
    }

    /// <summary>취소된 요청은 topology 질의 전에 종료하고 기존 snapshot을 보존합니다.</summary>
    [Fact]
    public void CreateHeatingRegion_ReturnsCancelledBeforeTopologyQuery()
    {
        (DesignProject project, FaceReference faceReference) = CreateProjectWithFaceReference();
        Fixture fixture = Fixture.Create(project);
        var geometryQueries = new FakeGeometryQueries();
        var useCase = new CreateHeatingRegion(fixture.Session, fixture.Coordinator);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        CandidateOutcome outcome = useCase.Execute(
            RegionId.New(),
            [faceReference.Id],
            CreateExitSideForFace(faceReference.Id),
            project.Revision,
            geometryQueries,
            cancellation.Token);

        Assert.Equal(CandidateStatus.Cancelled, outcome.Status);
        Assert.Equal("OperationCancelled", Assert.Single(outcome.Issues).Code);
        Assert.Same(fixture.InitialSnapshot, fixture.Session.Current);
        Assert.Equal(0, geometryQueries.SelectedTopologyQueryCount);
        Assert.Null(fixture.Workspace.Prepared);
    }

    /// <summary>기준 revision이 바뀐 제어점 편집은 현재 경로를 읽기 전에 거부합니다.</summary>
    [Fact]
    public void MoveControlPoint_RejectsStaleRevision()
    {
        Fixture fixture = Fixture.Create();
        var request = new ControlPointEditRequest(
            HeatingPathId.New(),
            ControlPointId.New(),
            new DesignRevision(8),
            CreateAnchor(FaceRefId.New(), new Vec2(2, 3)),
            0.01);
        var useCase = new MoveControlPoint(fixture.Session, fixture.Coordinator);

        CandidateOutcome outcome = useCase.Execute(request);

        Assert.Equal(CandidateStatus.ValidationFailed, outcome.Status);
        Assert.Equal("StaleControlPointEdit", Assert.Single(outcome.Issues).Code);
        Assert.Null(fixture.Workspace.Prepared);
    }

    /// <summary>미리보기 후보는 확정 세션과 Native staging을 바꾸지 않습니다.</summary>
    [Fact]
    public void MoveControlPoint_CreatePreviewDraftDoesNotCommitOrPrepareGeometry()
    {
        DesignProject project = CreateProjectWithInteriorControlPoint(out ControlPointId interiorPointId);
        Fixture fixture = Fixture.Create(project);
        HeatingPath path = project.HeatingPaths.Single(candidate =>
            candidate.ControlPoints.Any(point => point.Id == interiorPointId));
        HeatingRegion region = project.HeatingRegions.Single(candidate =>
            candidate.Id == path.GenerationSource.RegionId);
        var request = new ControlPointEditRequest(
            path.Id,
            interiorPointId,
            project.Revision,
            new SurfaceAnchor(
                region.FaceReferences[0],
                new Vec2(5, 2),
                new Vec2(5, 2),
                new Vec3(5, 2, 0)),
            0.01);
        var useCase = new MoveControlPoint(fixture.Session, fixture.Coordinator);
        SessionSnapshot previousSnapshot = fixture.Session.Current;

        OperationResult<HeatingPathEditDraft> result = useCase.CreatePreviewDraft(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(new Vec3(5, 2, 0), result.GetValueOrThrow().CandidatePath.ControlPoints
            .Single(point => point.Id == interiorPointId).Position);
        Assert.Same(previousSnapshot, fixture.Session.Current);
        Assert.Null(fixture.Workspace.Prepared);
    }

    /// <summary>내부 절점 편집은 회로·끝점·연결 경로를 유지하며 새 경로를 원자 확정합니다.</summary>
    [Fact]
    public void MoveControlPoint_PreservesCircuitAndConnectionPaths()
    {
        DesignProject project = CreateProjectWithInteriorControlPoint(out ControlPointId interiorPointId);
        var derivedStateBuilder = new ProjectDerivedStateBuilder(
            designValidationOverride: (_, _) => new ValidationReport([]));
        Fixture fixture = Fixture.Create(project, derivedStateBuilder);
        CircuitId circuitId = Assert.Single(project.Circuits).Id;
        ConnectionPathId[] connectionPathIds = project.ConnectionPaths
            .Select(connectionPath => connectionPath.Id)
            .OrderBy(id => id.Value)
            .ToArray();
        HeatingPath path = project.HeatingPaths.Single(candidate => candidate.Segments.Any(segment => segment.IsActive));
        HeatingRegion region = project.HeatingRegions.Single(candidate => candidate.Id == path.GenerationSource.RegionId);
        Vec3 movedPoint = new(5, 2, 0);
        var targetAnchor = new SurfaceAnchor(
            region.FaceReferences[0],
            new Vec2(5, 2),
            new Vec2(5, 2),
            movedPoint);
        var request = new ControlPointEditRequest(
            path.Id,
            interiorPointId,
            project.Revision,
            targetAnchor,
            0.01);
        var useCase = new MoveControlPoint(fixture.Session, fixture.Coordinator);

        CandidateOutcome outcome = useCase.Execute(request);

        Assert.Equal(CandidateStatus.Committed, outcome.Status);
        Assert.Equal(circuitId, Assert.Single(fixture.Session.Current.CurrentProject!.Circuits).Id);
        Assert.Equal(2, fixture.Session.Current.CurrentProject.ConnectionPoints.Count);
        Assert.Equal(2, fixture.Session.Current.CurrentProject.ConnectionPaths.Count);
        ConnectionPathId[] committedConnectionPathIds = fixture.Session.Current.CurrentProject.ConnectionPaths
            .Select(connectionPath => connectionPath.Id)
            .OrderBy(id => id.Value)
            .ToArray();
        Assert.Equal(connectionPathIds, committedConnectionPathIds);
        Vec3 committedPosition = fixture.Session.Current.CurrentProject.HeatingPaths
            .SelectMany(candidate => candidate.ControlPoints)
            .Single(point => point.Id == interiorPointId)
            .Position;
        Assert.Equal(movedPoint, committedPosition);
    }

    /// <summary>회전 곡선 미리보기는 확정 세션과 Native staging을 건드리지 않습니다.</summary>
    [Fact]
    public void EditTurnSegmentCurve_CreatePreviewDraftDoesNotCommitOrPrepareGeometry()
    {
        DesignProject project = CreateProjectWithEditableTurn(
            out HeatingPathId pathId,
            out HeatingPathSegmentId segmentId);
        Fixture fixture = Fixture.Create(project);
        HeatingPathSegment turn = project.HeatingPaths.Single(path => path.Id == pathId)
            .Segments.Single(segment => segment.Id == segmentId);
        PolylineCurveDefinition previousCurve = Assert.IsType<PolylineCurveDefinition>(turn.Curve);
        var curve = new PolylineCurveDefinition(
            [previousCurve.Points[0], new Vec3(5, 2, 0), previousCurve.Points[^1]]);
        var request = new TurnSegmentCurveEditRequest(pathId, segmentId, project.Revision, curve);
        var useCase = new EditTurnSegmentCurve(fixture.Session, fixture.Coordinator);
        SessionSnapshot previousSnapshot = fixture.Session.Current;

        OperationResult<TurnSegmentCurveEditDraft> result = useCase.CreatePreviewDraft(request);

        Assert.True(result.IsSuccess);
        Assert.Equal(curve.Points, ((PolylineCurveDefinition)result.GetValueOrThrow().CandidatePath
            .Segments.Single(segment => segment.Id == segmentId).Curve).Points);
        Assert.Same(previousSnapshot, fixture.Session.Current);
        Assert.Null(fixture.Workspace.Prepared);
    }

    /// <summary>회전 곡선 확정은 안정 ID와 회로·연결부를 보존하며 새 revision을 원자 적용합니다.</summary>
    [Fact]
    public void EditTurnSegmentCurve_CommitsAndPreservesCircuitAndConnections()
    {
        DesignProject project = CreateProjectWithEditableTurn(
            out HeatingPathId pathId,
            out HeatingPathSegmentId segmentId);
        var derivedStateBuilder = new ProjectDerivedStateBuilder(
            designValidationOverride: (_, _) => new ValidationReport([]));
        Fixture fixture = Fixture.Create(project, derivedStateBuilder);
        HeatingPathSegment turn = project.HeatingPaths.Single(path => path.Id == pathId)
            .Segments.Single(segment => segment.Id == segmentId);
        PolylineCurveDefinition previousCurve = (PolylineCurveDefinition)turn.Curve;
        var candidateCurve = new PolylineCurveDefinition(
            [previousCurve.Points[0], new Vec3(5, 2, 0), previousCurve.Points[^1]]);
        var request = new TurnSegmentCurveEditRequest(pathId, segmentId, project.Revision, candidateCurve);
        var useCase = new EditTurnSegmentCurve(fixture.Session, fixture.Coordinator);
        CircuitId[] circuitIds = project.Circuits.Select(circuit => circuit.Id).ToArray();
        ConnectionPointId[] connectionPointIds = project.ConnectionPoints.Select(point => point.Id).ToArray();
        ConnectionPathId[] connectionPathIds = project.ConnectionPaths.Select(path => path.Id).ToArray();

        CandidateOutcome outcome = useCase.Execute(request);

        Assert.Equal(CandidateStatus.Committed, outcome.Status);
        DesignProject committed = fixture.Session.Current.CurrentProject!;
        Assert.Equal(project.Revision.Next(), committed.Revision);
        HeatingPath committedPath = committed.HeatingPaths.Single(path => path.Id == pathId);
        HeatingPathSegment committedTurn = committedPath.Segments.Single(segment => segment.Id == segmentId);
        Assert.Equal(candidateCurve.Points, ((PolylineCurveDefinition)committedTurn.Curve).Points);
        Assert.Equal(project.HeatingPaths.Single(path => path.Id == pathId).ControlPoints, committedPath.ControlPoints);
        Assert.Equal(circuitIds, committed.Circuits.Select(circuit => circuit.Id));
        Assert.Equal(connectionPointIds, committed.ConnectionPoints.Select(point => point.Id));
        Assert.Equal(connectionPathIds, committed.ConnectionPaths.Select(path => path.Id));
    }

    /// <summary>취소된 회전 곡선 편집은 기존 확정 snapshot을 그대로 유지합니다.</summary>
    [Fact]
    public void EditTurnSegmentCurve_CancellationPreservesCommittedSnapshot()
    {
        DesignProject project = CreateProjectWithEditableTurn(
            out HeatingPathId pathId,
            out HeatingPathSegmentId segmentId);
        Fixture fixture = Fixture.Create(project);
        HeatingPathSegment turn = project.HeatingPaths.Single(path => path.Id == pathId)
            .Segments.Single(segment => segment.Id == segmentId);
        PolylineCurveDefinition previousCurve = (PolylineCurveDefinition)turn.Curve;
        var request = new TurnSegmentCurveEditRequest(
            pathId,
            segmentId,
            project.Revision,
            new PolylineCurveDefinition([previousCurve.Points[0], new Vec3(5, 2, 0), previousCurve.Points[^1]]));
        var useCase = new EditTurnSegmentCurve(fixture.Session, fixture.Coordinator);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        SessionSnapshot previousSnapshot = fixture.Session.Current;

        CandidateOutcome outcome = useCase.Execute(request, cancellation.Token);

        Assert.Equal(CandidateStatus.Cancelled, outcome.Status);
        Assert.Same(previousSnapshot, fixture.Session.Current);
        Assert.Null(fixture.Workspace.Prepared);
    }

    /// <summary>현재 프로젝트보다 오래된 경로 생성 요청은 표면 조회를 시작하지 않습니다.</summary>
    [Fact]
    public void GenerateHeatingPath_RejectsStaleRevisionBeforePlanning()
    {
        Fixture fixture = Fixture.Create();
        HeatingRegion region = CreateRegion();
        PlacementConstraints constraints = CreateConstraints(region);
        var coordinateSystem = CreateCoordinateSystem(new ModelRevision(3));
        var request = new HeatingPathGenerationRequest(
            fixture.Session.Current.CurrentProject!.Id,
            new DesignRevision(8),
            region,
            new WireSpecification("Fixture alloy", new LengthMm(0.3), new ResistanceOhmsPerMeter(100)),
            constraints,
            coordinateSystem,
            coordinateSystem.Revision,
            ManualEditPolicy.PreserveIfCompatible);
        var useCase = new GenerateHeatingPath(fixture.Session, fixture.Coordinator);

        CandidateOutcome outcome = useCase.Execute(request, new FakeGeometryQueries());

        Assert.Equal(CandidateStatus.ValidationFailed, outcome.Status);
        Assert.Equal("StaleHeatingPathRequest", Assert.Single(outcome.Issues).Code);
        Assert.Null(fixture.Workspace.Prepared);
    }

    /// <summary>회로와 연결 경로 입력이 없으면 표면 질의 전에 생성을 차단합니다.</summary>
    [Fact]
    public void GenerateHeatingPath_RequiresCircuitSetupBeforeMapping()
    {
        var scenario = CreateHeatingPathGenerationCase();
        Fixture fixture = Fixture.Create(scenario.Project);
        var useCase = new GenerateHeatingPath(fixture.Session, fixture.Coordinator);

        CandidateOutcome outcome = useCase.Execute(scenario.Request, scenario.GeometryQueries);

        Assert.Equal(CandidateStatus.ValidationFailed, outcome.Status);
        Assert.Equal("CircuitSetupRequired", Assert.Single(outcome.Issues).Code);
        Assert.Equal(0, scenario.GeometryQueries.FirstHitQueryCount);
        Assert.Null(fixture.Workspace.Prepared);
    }

    /// <summary>새 영역 경로의 생성 전 진단은 수정하지 않고 초안과 편집 없음 상태를 반환합니다.</summary>
    [Fact]
    public void AssessHeatingPathRegeneration_ReturnsDraftWithoutExistingPath()
    {
        var scenario = CreateHeatingPathGenerationCase();
        var assessmentService = new AssessHeatingPathRegeneration();

        OperationResult<HeatingPathRegenerationAssessment> result = assessmentService.Execute(
            scenario.Project,
            scenario.Request);

        Assert.True(result.IsSuccess, string.Join("; ", result.Issues.Select(issue => issue.Code)));
        HeatingPathRegenerationAssessment assessment = result.GetValueOrThrow();
        Assert.Equal(scenario.Project.Id, assessment.ProjectId);
        Assert.Equal(scenario.Project.Revision, assessment.BaseRevision);
        Assert.Equal(scenario.Request.Region.Id, assessment.Draft.RegionId);
        Assert.Null(assessment.ExistingPath);
        Assert.Equal(RegenerationCompatibilityStatus.NoOverrides, assessment.Compatibility.Status);
        Assert.True(assessment.CanPreserveCurrentEdits);
        Assert.False(assessment.RequiresReplacementChoice);
        Assert.Empty(assessment.Impact.HeatingPathIds);
    }

    /// <summary>프로젝트 공통 입력을 바꾸면 다른 영역 경로까지 영향 범위에 포함합니다.</summary>
    [Fact]
    public void AssessHeatingPathRegeneration_ReportsPathsOutsideRequestedRegion()
    {
        var scenario = CreateHeatingPathGenerationCase();
        DesignProject project = AddOtherRegionPath(scenario.Project);

        OperationResult<HeatingPathRegenerationAssessment> result = new AssessHeatingPathRegeneration().Execute(
            project,
            scenario.Request);

        Assert.True(result.IsSuccess, string.Join("; ", result.Issues.Select(issue => issue.Code)));
        HeatingPathRegenerationAssessment assessment = result.GetValueOrThrow();
        Assert.Equal([project.HeatingPaths.Single().Id], assessment.Impact.HeatingPathIds);
        Assert.True(assessment.RequiresAdditionalPathReplacement);
    }

    /// <summary>단일 영역 재생성은 영향받는 다른 영역 경로를 삭제하지 않고 중단합니다.</summary>
    [Fact]
    public void GenerateHeatingPath_RejectsProjectWideInputChangeWithoutRemovingOtherPaths()
    {
        var scenario = CreateHeatingPathGenerationCase();
        DesignProject project = AddOtherRegionPath(scenario.Project);
        Fixture fixture = Fixture.Create(project);
        var useCase = new GenerateHeatingPath(fixture.Session, fixture.Coordinator);

        CandidateOutcome outcome = useCase.Execute(
            scenario.Request,
            scenario.GeometryQueries,
            scenario.CircuitSetup);

        Assert.Equal(CandidateStatus.ValidationFailed, outcome.Status);
        Assert.Equal("AdditionalHeatingPathsRequireRegeneration", Assert.Single(outcome.Issues).Code);
        Assert.Same(project, fixture.Session.Current.CurrentProject);
        Assert.Equal(project.HeatingPaths.Select(path => path.Id),
            fixture.Session.Current.CurrentProject!.HeatingPaths.Select(path => path.Id));
        Assert.Null(fixture.Workspace.Prepared);
    }

    /// <summary>경로 topology 편집은 초안을 유지하면서 교체 선택이 필요하다고 진단합니다.</summary>
    [Fact]
    public void AssessHeatingPathRegeneration_RequiresChoiceForChangedTopology()
    {
        var scenario = CreateChangedTopologyRegenerationCase(ManualEditPolicy.PreserveIfCompatible);
        var assessmentService = new AssessHeatingPathRegeneration();

        OperationResult<HeatingPathRegenerationAssessment> result = assessmentService.Execute(
            scenario.Project,
            scenario.Request);

        Assert.True(result.IsSuccess, string.Join("; ", result.Issues.Select(issue => issue.Code)));
        HeatingPathRegenerationAssessment assessment = result.GetValueOrThrow();
        Assert.Equal(scenario.ExistingPath.Id, assessment.ExistingPath!.Id);
        Assert.Equal(RegenerationCompatibilityStatus.ReplaceRequired, assessment.Compatibility.Status);
        Assert.False(assessment.CanPreserveCurrentEdits);
        Assert.True(assessment.RequiresReplacementChoice);
        Assert.Contains(assessment.Compatibility.Issues, issue => issue.Code == "EditedTopologyChanged");
        Assert.Equal([scenario.ExistingPath.Id], assessment.Impact.HeatingPathIds);
        Assert.Equal([scenario.CircuitSetup.CircuitId], assessment.Impact.CircuitIds);
        Assert.Equal(2, assessment.Impact.ConnectionPointIds.Count);
        Assert.Equal(2, assessment.Impact.ConnectionPathIds.Count);
    }

    /// <summary>교체 정책은 topology 편집을 버리고 새 경로 후보를 확정합니다.</summary>
    [Fact]
    public void GenerateHeatingPath_ReplacePolicyCommitsAfterChangedTopology()
    {
        var scenario = CreateChangedTopologyRegenerationCase(ManualEditPolicy.ReplaceEditedHeatingPath);
        var derivedStateBuilder = new ProjectDerivedStateBuilder(
            designValidationOverride: (_, _) => new ValidationReport([]));
        Fixture fixture = Fixture.Create(scenario.Project, derivedStateBuilder);
        var useCase = new GenerateHeatingPath(fixture.Session, fixture.Coordinator);

        CandidateOutcome outcome = useCase.Execute(
            scenario.Request,
            scenario.GeometryQueries,
            scenario.CircuitSetup);

        Assert.True(
            outcome.Status == CandidateStatus.Committed,
            $"상태: {outcome.Status}; 진단: {string.Join("; ", outcome.Issues.Select(issue => string.Join(", ", issue.Evidence)))}");
        HeatingPath committedPath = Assert.Single(fixture.Session.Current.CurrentProject!.HeatingPaths);
        Assert.NotEqual(scenario.ExistingPath.Id, committedPath.Id);
        Assert.NotEqual(
            scenario.ExistingPath.Segments.Select(segment => segment.Id),
            committedPath.Segments.Select(segment => segment.Id));
    }

    /// <summary>생성 경로와 회로 양 끝의 연결 경로를 하나의 후보로 확정합니다.</summary>
    [Fact]
    public void GenerateHeatingPath_CommitsPathCircuitAndConnectionPathsTogether()
    {
        var scenario = CreateHeatingPathGenerationCase();
        var derivedStateBuilder = new ProjectDerivedStateBuilder(
            designValidationOverride: (_, _) => new ValidationReport([]));
        Fixture fixture = Fixture.Create(scenario.Project, derivedStateBuilder);
        var useCase = new GenerateHeatingPath(fixture.Session, fixture.Coordinator);

        CandidateOutcome outcome = useCase.Execute(scenario.Request, scenario.GeometryQueries, scenario.CircuitSetup);

        Assert.Equal(CandidateStatus.Committed, outcome.Status);
        DesignProject committed = fixture.Session.Current.CurrentProject!;
        HeatingPath path = Assert.Single(committed.HeatingPaths);
        Circuit circuit = Assert.Single(committed.Circuits);
        Assert.Equal(scenario.CircuitSetup.CircuitId, circuit.Id);
        Assert.Equal(scenario.CircuitSetup.Supply, circuit.Supply);
        Assert.All(path.Segments.Where(segment => segment.IsActive), segment =>
            Assert.Contains(circuit.Segments, reference => reference.SegmentId == segment.Id));
        Assert.Equal(2, committed.ConnectionPoints.Count);
        Assert.Equal(2, committed.ConnectionPaths.Count);
        Assert.True(committed.ValidateReferences().IsValid);
        Assert.Single(fixture.Session.Current.ElectricalResults);
        Assert.Equal(1, fixture.Workspace.Prepared!.PublishCount);
    }

    /// <summary>새 발열 절점 뒤에 사용자 연결 구간을 계획해 같은 후보로 확정합니다.</summary>
    [Fact]
    public void GenerateHeatingPath_PlansConnectionLegsFromMaterializedEndpoints()
    {
        var scenario = CreateHeatingPathGenerationCase();
        FaceRefId faceId = scenario.Request.Region.FaceReferences[0];
        var startExit = new SurfaceAnchor(
            faceId,
            default,
            new Vec2(4, 0),
            new Vec3(4, 0, 0));
        var endExit = new SurfaceAnchor(
            faceId,
            default,
            new Vec2(16, 0),
            new Vec3(16, 0, 0));
        var startEndpoint = new CircuitEndpointDefinition(
            ConnectionPointId.New(),
            startExit,
            new Vec3(6, 0, 3));
        var endEndpoint = new CircuitEndpointDefinition(
            ConnectionPointId.New(),
            endExit,
            new Vec3(14, 0, 3));
        var circuitSetup = new HeatingPathCircuitSetup(
            CircuitId.New(),
            new CircuitEndpointPair(startEndpoint, endEndpoint),
            new Supply(new VoltageVrms(220), new VoltageVrms(240), new CurrentAmps(25)),
            new Dictionary<ConnectionPointId, IReadOnlyList<ConnectionPathLegRequest>>
            {
                [startEndpoint.Id] =
                [
                    new ConnectionPathLegRequest(
                        startExit.ModelPoint,
                        GeometryDomain.OnMold,
                        ConductorRole.HeatingWireExtension,
                        new LengthMm(0)),
                    new ConnectionPathLegRequest(
                        startEndpoint.ExternalConnectionPoint,
                        GeometryDomain.External,
                        ConductorRole.SeparateConnectionWire,
                        new LengthMm(0),
                        isMoldExitTransition: true)
                ],
                [endEndpoint.Id] =
                [
                    new ConnectionPathLegRequest(
                        endExit.ModelPoint,
                        GeometryDomain.OnMold,
                        ConductorRole.HeatingWireExtension,
                        new LengthMm(0)),
                    new ConnectionPathLegRequest(
                        endEndpoint.ExternalConnectionPoint,
                        GeometryDomain.External,
                        ConductorRole.SeparateConnectionWire,
                        new LengthMm(0),
                        isMoldExitTransition: true)
                ]
            });
        scenario.GeometryQueries.ConnectionPathObstacleResult =
            GeometryQueryResult<ConnectionObstacleCheck>.Succeeded(
                new ConnectionObstacleCheck(true, null, null, moldExitTransitionFound: true));
        var derivedStateBuilder = new ProjectDerivedStateBuilder(
            designValidationOverride: (_, _) => new ValidationReport([]));
        Fixture fixture = Fixture.Create(scenario.Project, derivedStateBuilder);
        var useCase = new GenerateHeatingPath(fixture.Session, fixture.Coordinator);

        CandidateOutcome outcome = useCase.Execute(
            scenario.Request,
            scenario.GeometryQueries,
            circuitSetup);

        Assert.Equal(CandidateStatus.Committed, outcome.Status);
        DesignProject committed = fixture.Session.Current.CurrentProject!;
        HeatingPath committedHeatingPath = Assert.Single(committed.HeatingPaths);
        Assert.Equal(2, committed.ConnectionPaths.Count);
        Assert.Equal(2, scenario.GeometryQueries.ConnectionPathObstacleQueryCount);
        Assert.All(committed.ConnectionPaths, path =>
        {
            Assert.Equal(2, path.Segments.Count);
            Assert.Equal(GeometryDomain.OnMold, path.Segments[0].GeometryDomain);
            Assert.True(path.Segments[1].IsMoldExitTransition);
            Assert.Equal(GeometryDomain.External, path.Segments[1].GeometryDomain);

            ConnectionPoint endpoint = committed.ConnectionPoints.Single(point => point.Id == path.ConnectionPointId);
            Vec3 expectedStart = committedHeatingPath.ControlPoints
                .Single(point => point.Id == endpoint.ControlPointId)
                .Position;
            LineCurveDefinition firstLeg = Assert.IsType<LineCurveDefinition>(path.Segments[0].Curve);
            Assert.Equal(expectedStart, firstLeg.Start);
            Assert.Equal(-0.15, expectedStart.Z, precision: 10);
        });
        Assert.Equal(1, fixture.Workspace.Prepared!.PublishCount);
    }

    /// <summary>외부 연결 좌표가 다르면 표면 매핑을 시작하기 전에 연결 요청을 거부합니다.</summary>
    [Fact]
    public void GenerateHeatingPath_RejectsMismatchedExternalRouteBeforeSurfaceQueries()
    {
        var scenario = CreateHeatingPathGenerationCase();
        FaceRefId faceId = scenario.Request.Region.FaceReferences[0];
        var startExit = new SurfaceAnchor(faceId, default, new Vec2(4, 0), new Vec3(4, 0, 0));
        var endExit = new SurfaceAnchor(faceId, default, new Vec2(16, 0), new Vec3(16, 0, 0));
        var startEndpoint = new CircuitEndpointDefinition(
            ConnectionPointId.New(), startExit, new Vec3(6, 0, 3));
        var endEndpoint = new CircuitEndpointDefinition(
            ConnectionPointId.New(), endExit, new Vec3(14, 0, 3));
        var circuitSetup = new HeatingPathCircuitSetup(
            CircuitId.New(),
            new CircuitEndpointPair(startEndpoint, endEndpoint),
            new Supply(new VoltageVrms(220), new VoltageVrms(240), new CurrentAmps(25)),
            new Dictionary<ConnectionPointId, IReadOnlyList<ConnectionPathLegRequest>>
            {
                [startEndpoint.Id] =
                [
                    new ConnectionPathLegRequest(startExit.ModelPoint, GeometryDomain.OnMold,
                        ConductorRole.HeatingWireExtension, new LengthMm(0)),
                    new ConnectionPathLegRequest(new Vec3(7, 0, 3), GeometryDomain.External,
                        ConductorRole.SeparateConnectionWire, new LengthMm(0), isMoldExitTransition: true)
                ],
                [endEndpoint.Id] =
                [
                    new ConnectionPathLegRequest(endExit.ModelPoint, GeometryDomain.OnMold,
                        ConductorRole.HeatingWireExtension, new LengthMm(0)),
                    new ConnectionPathLegRequest(endEndpoint.ExternalConnectionPoint, GeometryDomain.External,
                        ConductorRole.SeparateConnectionWire, new LengthMm(0), isMoldExitTransition: true)
                ]
            });
        Fixture fixture = Fixture.Create(
            scenario.Project,
            new ProjectDerivedStateBuilder(designValidationOverride: (_, _) => new ValidationReport([])));
        var useCase = new GenerateHeatingPath(fixture.Session, fixture.Coordinator);

        CandidateOutcome outcome = useCase.Execute(
            scenario.Request,
            scenario.GeometryQueries,
            circuitSetup);

        Assert.Equal(CandidateStatus.ValidationFailed, outcome.Status);
        Assert.Equal("ExternalConnectionPointMismatch", Assert.Single(outcome.Issues).Code);
        Assert.Equal(0, scenario.GeometryQueries.FirstHitQueryCount);
        Assert.Null(fixture.Workspace.Prepared);
    }

    /// <summary>존재하지 않는 연결부의 경로 요청은 Native 장애물 검사 전에 거부합니다.</summary>
    [Fact]
    public void UpdateConnectionPath_RejectsConnectionOutsideCurrentProject()
    {
        Fixture fixture = Fixture.Create();
        FaceRefId faceId = FaceRefId.New();
        ControlPointId startId = ControlPointId.New();
        ControlPointId endId = ControlPointId.New();
        HeatingPathSegment segment = new(
            HeatingPathSegmentId.New(), HeatingPathSegmentKind.HeatingRun,
            startId, endId, 0, true,
            new LineCurveDefinition(default, new Vec3(10, 0, 0)));
        CircuitId circuitId = CircuitId.New();
        ConnectionPointId startPointId = ConnectionPointId.New();
        ConnectionPointId endPointId = ConnectionPointId.New();
        var circuit = new Circuit(
            circuitId, 0, [new CircuitSegmentReference(segment.Id, 0, false)],
            startPointId, endPointId, new Supply(new VoltageVrms(220), new VoltageVrms(240), new CurrentAmps(20)));
        var point = new ConnectionPoint(
            startPointId, circuitId, ConnectionPointRole.Start, segment.Id, startId,
            CreateAnchor(faceId, new Vec2(0, 0)), new Vec3(-10, 0, 0));
        var request = new ConnectionPathPlanningRequest(
            circuit,
            point,
            segment,
            default,
            [
                new ConnectionPathLegRequest(new Vec3(0, 0, 0), GeometryDomain.OnMold, ConductorRole.HeatingWireExtension, new LengthMm(0)),
                new ConnectionPathLegRequest(new Vec3(2, 0, 0), GeometryDomain.External, ConductorRole.SeparateConnectionWire, new LengthMm(0), true)
            ],
            CreateCoordinateSystem(new ModelRevision(2)),
            new ModelRevision(2));
        var useCase = new UpdateConnectionPath(fixture.Session, fixture.Coordinator);

        CandidateOutcome outcome = useCase.Execute(
            request, new FakeGeometryQueries(), 0.01,
            fixture.Session.Current.CurrentProject!.Revision);

        Assert.Equal(CandidateStatus.ValidationFailed, outcome.Status);
        Assert.Equal("ConnectionOwnerNotFound", Assert.Single(outcome.Issues).Code);
        Assert.Null(fixture.Workspace.Prepared);
    }

    /// <summary>연결부 위치가 달라진 요청은 장애물 질의와 후보 준비 전에 거부합니다.</summary>
    [Fact]
    public void UpdateConnectionPath_RejectsChangedEndpointBeforeGeometryQueries()
    {
        DesignProject project = CreateProjectWithTwoRegionPaths().Project;
        var derivedStateBuilder = new ProjectDerivedStateBuilder(
            designValidationOverride: (_, _) => new ValidationReport([]));
        Fixture fixture = Fixture.Create(project, derivedStateBuilder);
        ConnectionPoint currentPoint = project.ConnectionPoints[0];
        ConnectionPoint changedPoint = new(
            currentPoint.Id,
            currentPoint.CircuitId,
            currentPoint.Role,
            currentPoint.HeatingPathSegmentId,
            currentPoint.ControlPointId,
            currentPoint.SurfaceAnchor,
            currentPoint.ExternalConnectionPoint + new Vec3(1, 0, 0));
        Circuit circuit = project.Circuits.Single(candidate => candidate.Id == currentPoint.CircuitId);
        HeatingPathSegment segment = project.HeatingPaths
            .SelectMany(path => path.Segments)
            .Single(candidate => candidate.Id == currentPoint.HeatingPathSegmentId);
        var request = new ConnectionPathPlanningRequest(
            circuit,
            changedPoint,
            segment,
            default,
            [
                new ConnectionPathLegRequest(
                    currentPoint.SurfaceAnchor.ModelPoint,
                    GeometryDomain.OnMold,
                    ConductorRole.HeatingWireExtension,
                    new LengthMm(0)),
                new ConnectionPathLegRequest(
                    changedPoint.ExternalConnectionPoint,
                    GeometryDomain.External,
                    ConductorRole.SeparateConnectionWire,
                    new LengthMm(0),
                    isMoldExitTransition: true)
            ],
            CreateCoordinateSystem(new ModelRevision(2)),
            new ModelRevision(2));
        var geometryQueries = new FakeGeometryQueries();
        var useCase = new UpdateConnectionPath(fixture.Session, fixture.Coordinator);

        CandidateOutcome outcome = useCase.Execute(
            request,
            geometryQueries,
            connectionToleranceMm: 0.01,
            expectedBaseRevision: project.Revision);

        Assert.Equal(CandidateStatus.ValidationFailed, outcome.Status);
        Assert.Equal("ConnectionEndpointChanged", Assert.Single(outcome.Issues).Code);
        Assert.Equal(0, geometryQueries.ConnectionPathObstacleQueryCount);
        Assert.Null(fixture.Workspace.Prepared);
    }

    /// <summary>장애물 검사를 통과한 연결 경로 수정만 새 후보 revision으로 확정합니다.</summary>
    [Fact]
    public void UpdateConnectionPath_ReplansAndCommitsValidatedPath()
    {
        DesignProject project = AddProjectDesignInputs(CreateProjectWithTwoRegionPaths().Project);
        var derivedStateBuilder = new ProjectDerivedStateBuilder(
            designValidationOverride: (_, _) => new ValidationReport([]));
        Fixture fixture = Fixture.Create(
            project,
            derivedStateBuilder,
            includeInitialHeatingCurves: true);
        ConnectionPoint connectionPoint = project.ConnectionPoints[0];
        Circuit circuit = project.Circuits.Single(candidate => candidate.Id == connectionPoint.CircuitId);
        HeatingPathSegment endpointSegment = project.HeatingPaths
            .SelectMany(path => path.Segments)
            .Single(segment => segment.Id == connectionPoint.HeatingPathSegmentId);
        CurveGeometrySnapshot measuredEndpoint = fixture.Session.Current.Geometry!.Curves
            .Single(curve => curve.HeatingPathSegmentId == endpointSegment.Id);
        bool curveRunsForward = endpointSegment.Curve.Direction == CurveDirection.Forward;
        Vec3 heatingEndpoint;
        if (connectionPoint.ControlPointId == endpointSegment.StartControlPointId)
            heatingEndpoint = curveRunsForward ? measuredEndpoint.Start : measuredEndpoint.End;
        else
            heatingEndpoint = curveRunsForward ? measuredEndpoint.End : measuredEndpoint.Start;
        var request = new ConnectionPathPlanningRequest(
            circuit,
            connectionPoint,
            endpointSegment,
            heatingEndpoint,
            [
                new ConnectionPathLegRequest(
                    connectionPoint.SurfaceAnchor.ModelPoint,
                    GeometryDomain.OnMold,
                    ConductorRole.HeatingWireExtension,
                    new LengthMm(1)),
                new ConnectionPathLegRequest(
                    connectionPoint.ExternalConnectionPoint,
                    GeometryDomain.External,
                    ConductorRole.SeparateConnectionWire,
                    new LengthMm(2),
                    isMoldExitTransition: true)
            ],
            CreateCoordinateSystem(new ModelRevision(1)),
            new ModelRevision(1));
        var geometryQueries = new FakeGeometryQueries
        {
            ConnectionPathObstacleResult = GeometryQueryResult<ConnectionObstacleCheck>.Succeeded(
                new ConnectionObstacleCheck(true, null, null, moldExitTransitionFound: true))
        };
        ConnectionPath previousPath = project.ConnectionPaths.Single(path =>
            path.ConnectionPointId == connectionPoint.Id);
        var useCase = new UpdateConnectionPath(fixture.Session, fixture.Coordinator);

        CandidateOutcome outcome = useCase.Execute(
            request,
            geometryQueries,
            project.Rules.GeometryTolerances.ConnectionToleranceMm,
            project.Revision);

        Assert.Equal(CandidateStatus.Committed, outcome.Status);
        ConnectionPath updatedPath = fixture.Session.Current.CurrentProject!.ConnectionPaths
            .Single(path => path.ConnectionPointId == connectionPoint.Id);
        Assert.NotEqual(previousPath.Id, updatedPath.Id);
        Assert.Equal(new LengthMm(1), updatedPath.Segments[0].AdditionalAllowanceMm);
        Assert.Equal(new LengthMm(2), updatedPath.Segments[1].AdditionalAllowanceMm);
        Assert.Equal(1, geometryQueries.ConnectionPathObstacleQueryCount);
        Assert.NotNull(fixture.Workspace.Prepared);
    }

    /// <summary>연결 성분이 없는 설계는 빈 회로 topology로 확정할 수 있습니다.</summary>
    [Fact]
    public void ChangeCircuitTopology_AnalyzesAndCommitsEmptyTopology()
    {
        Fixture fixture = Fixture.Create();
        var useCase = new ChangeCircuitTopology(fixture.Session, fixture.Coordinator);
        DesignRevision revision = fixture.Session.Current.CurrentProject!.Revision;

        OperationResult<CircuitTopologyPlan> analysis = useCase.Analyze(revision);

        Assert.True(analysis.IsSuccess);
        Assert.Empty(analysis.GetValueOrThrow().Mapping.Assignments);
        CandidateOutcome outcome = useCase.Execute(analysis.GetValueOrThrow());

        Assert.Equal(CandidateStatus.Committed, outcome.Status);
        Assert.Empty(fixture.Session.Current.CurrentProject!.Circuits);
        Assert.Empty(fixture.Session.Current.CurrentProject.ConnectionPoints);
        Assert.Empty(fixture.Session.Current.CurrentProject.ConnectionPaths);
    }

    /// <summary>연결부 위치가 유지되는 topology 적용은 기존 ConnectionPath를 후보에 보존합니다.</summary>
    [Fact]
    public void ChangeCircuitTopology_PreservesConnectionPathsForUnchangedEndpoints()
    {
        DesignProject project = AddProjectDesignInputs(CreateProjectWithTwoRegionPaths().Project);
        var derivedStateBuilder = new ProjectDerivedStateBuilder(
            designValidationOverride: (_, _) => new ValidationReport([]));
        Fixture fixture = Fixture.Create(project, derivedStateBuilder);
        ConnectionPathId[] previousPathIds = project.ConnectionPaths
            .Select(path => path.Id)
            .OrderBy(id => id.Value)
            .ToArray();
        var useCase = new ChangeCircuitTopology(fixture.Session, fixture.Coordinator);

        OperationResult<CircuitTopologyPlan> analysis = useCase.Analyze(project.Revision);
        Assert.True(analysis.IsSuccess);
        CandidateOutcome outcome = useCase.Execute(analysis.GetValueOrThrow());

        Assert.Equal(CandidateStatus.Committed, outcome.Status);
        ConnectionPathId[] resultingPathIds = fixture.Session.Current.CurrentProject!.ConnectionPaths
            .Select(path => path.Id)
            .OrderBy(id => id.Value)
            .ToArray();
        Assert.Equal(previousPathIds, resultingPathIds);
    }

    /// <summary>회전 구간 비활성화와 회로 분할을 같은 후보에서 확정합니다.</summary>
    [Fact]
    public void ChangeCircuitTopology_DisablesTurnAndCommitsSplitCircuits()
    {
        DesignProject project = CreateProjectWithToggleableTurn(out HeatingPathSegmentId turnSegmentId);
        var derivedStateBuilder = new ProjectDerivedStateBuilder(
            designValidationOverride: (_, _) => new ValidationReport([]));
        Fixture fixture = Fixture.Create(project, derivedStateBuilder);
        Circuit previousCircuit = Assert.Single(project.Circuits);
        var useCase = new ChangeCircuitTopology(fixture.Session, fixture.Coordinator);
        var requestedTurnStates = new Dictionary<HeatingPathSegmentId, bool>
        {
            [turnSegmentId] = false
        };

        OperationResult<CircuitTopologyPlan> analysis = useCase.Analyze(
            project.Revision,
            turnSegmentStates: requestedTurnStates);

        Assert.True(analysis.IsSuccess);
        Assert.Equal(2, analysis.GetValueOrThrow().Topology.Components.Count);
        CandidateOutcome preview = useCase.Execute(analysis.GetValueOrThrow());

        Assert.Equal(CandidateStatus.ValidationFailed, preview.Status);
        Assert.Equal("CircuitEndpointResolutionRequired", Assert.Single(preview.Issues).Code);
        Assert.Null(fixture.Workspace.Prepared);

        CircuitTopologyPlan plan = analysis.GetValueOrThrow();
        var endpoints = new Dictionary<CircuitId, CircuitEndpointPair>();
        foreach (CircuitIdentityAssignment assignment in plan.Mapping.Assignments)
        {
            CircuitEndpointDefinition start = CreateEndpointDefinition(
                project,
                assignment.Component.StartControlPointId);
            CircuitEndpointDefinition end = CreateEndpointDefinition(
                project,
                assignment.Component.EndControlPointId);
            endpoints.Add(assignment.CircuitId, new CircuitEndpointPair(start, end));
        }

        HashSet<ConnectionPointId> previousPointIds = project.ConnectionPoints
            .Select(point => point.Id)
            .ToHashSet();
        var requestedPaths = endpoints.Values
            .SelectMany(pair => new[] { pair.Start, pair.End })
            .Where(endpoint => !previousPointIds.Contains(endpoint.Id))
            .DistinctBy(endpoint => endpoint.Id)
            .ToDictionary(
                endpoint => endpoint.Id,
                endpoint => CreateConnectionPath(endpoint.Id));

        CandidateOutcome outcome = useCase.Execute(
            plan,
            endpoints,
            newConnectionPaths: requestedPaths);

        Assert.Equal(CandidateStatus.Committed, outcome.Status);
        DesignProject committed = fixture.Session.Current.CurrentProject!;
        HeatingPath committedPath = committed.HeatingPaths.Single(path => path.Id ==
            project.HeatingPaths.Single(candidate => candidate.Segments.Any(segment => segment.Id == turnSegmentId)).Id);
        Assert.False(committedPath.Segments.Single(segment => segment.Id == turnSegmentId).IsActive);
        Assert.Equal(2, committed.Circuits.Count);
        Assert.Contains(committed.Circuits, circuit => circuit.Id == previousCircuit.Id);
        Assert.All(committed.Circuits, circuit => Assert.Equal(previousCircuit.Supply, circuit.Supply));
        Assert.Equal(4, committed.ConnectionPoints.Count);
        Assert.Equal(4, committed.ConnectionPaths.Count);
        Assert.True(committed.ValidateReferences().IsValid);
    }

    /// <summary>회전 상태 입력이 일반 주행 구간을 가리키면 분석을 거부합니다.</summary>
    [Fact]
    public void ChangeCircuitTopology_RejectsTurnStateForHeatingRun()
    {
        DesignProject project = AddProjectDesignInputs(CreateProjectWithTwoRegionPaths().Project);
        Fixture fixture = Fixture.Create(project);
        HeatingPathSegment heatingRun = project.HeatingPaths
            .SelectMany(path => path.Segments)
            .Single(segment => segment.IsActive && segment.Kind == HeatingPathSegmentKind.HeatingRun);
        var useCase = new ChangeCircuitTopology(fixture.Session, fixture.Coordinator);

        OperationResult<CircuitTopologyPlan> analysis = useCase.Analyze(
            project.Revision,
            turnSegmentStates: new Dictionary<HeatingPathSegmentId, bool>
            {
                [heatingRun.Id] = false
            });

        Assert.False(analysis.IsSuccess);
        Assert.Equal("TurnSegmentStateTargetInvalid", Assert.Single(analysis.Issues).Code);
        Assert.Null(fixture.Workspace.Prepared);
    }

    /// <summary>새 회로 끝점의 경로 누락은 차단하고 경로를 함께 주면 원자 확정합니다.</summary>
    [Fact]
    public void ChangeCircuitTopology_RequiresAndCommitsPathsForNewEndpoints()
    {
        DesignProject project = AddProjectDesignInputs(CreateProjectWithTwoRegionPaths().Project);
        var derivedStateBuilder = new ProjectDerivedStateBuilder(
            designValidationOverride: (_, _) => new ValidationReport([]));
        Fixture fixture = Fixture.Create(project, derivedStateBuilder);
        CircuitPathTopologyAnalysis topology = new CircuitPathTopologyService().Analyze(
            project.HeatingPaths.SelectMany(path => path.Segments),
            project.Rules.ValidationRuleVersion);
        CircuitPathComponent component = Assert.Single(topology.Components);
        CircuitId newCircuitId = CircuitId.New();
        var assignment = new CircuitIdentityAssignment(
            newCircuitId,
            parentCircuitId: null,
            preservesExistingIdentity: false,
            component: component,
            startConnectionPointId: null,
            endConnectionPointId: null,
            preservedSupply: null,
            mergedCircuitIds: []);
        var plan = new CircuitTopologyPlan(
            project.Id,
            project.Revision,
            topology,
            new CircuitIdentityMapping([assignment]));
        ConnectionPoint previousStart = project.ConnectionPoints.Single(point => point.Role == ConnectionPointRole.Start);
        ConnectionPoint previousEnd = project.ConnectionPoints.Single(point => point.Role == ConnectionPointRole.End);
        var endpoints = new Dictionary<CircuitId, CircuitEndpointPair>
        {
            [newCircuitId] = new CircuitEndpointPair(
                new CircuitEndpointDefinition(
                    ConnectionPointId.New(), previousStart.SurfaceAnchor, previousStart.ExternalConnectionPoint),
                new CircuitEndpointDefinition(
                    ConnectionPointId.New(), previousEnd.SurfaceAnchor, previousEnd.ExternalConnectionPoint))
        };
        var supplies = new Dictionary<CircuitId, Supply>
        {
            [newCircuitId] = project.Circuits[0].Supply
        };
        var useCase = new ChangeCircuitTopology(fixture.Session, fixture.Coordinator);

        CandidateOutcome outcome = useCase.Execute(plan, endpoints, supplies);

        Assert.Equal(CandidateStatus.ValidationFailed, outcome.Status);
        Assert.Equal("CircuitConnectionPathRequired", Assert.Single(outcome.Issues).Code);
        Assert.Null(fixture.Workspace.Prepared);

        var requestedPaths = new Dictionary<ConnectionPointId, ConnectionPath>
        {
            [endpoints[newCircuitId].Start.Id] = CreateConnectionPath(endpoints[newCircuitId].Start.Id),
            [endpoints[newCircuitId].End.Id] = CreateConnectionPath(endpoints[newCircuitId].End.Id)
        };
        CandidateOutcome committed = useCase.Execute(
            plan,
            endpoints,
            supplies,
            requestedPaths,
            CancellationToken.None);

        Assert.Equal(CandidateStatus.Committed, committed.Status);
        Assert.Equal(2, fixture.Session.Current.CurrentProject!.ConnectionPaths.Count);
        Assert.All(
            fixture.Session.Current.CurrentProject.ConnectionPaths,
            path => Assert.Contains(path.ConnectionPointId, requestedPaths.Keys));
    }

    /// <summary>새 끝점의 ConnectionPath를 Native 측정 곡선에서 시작해 검증 후 확정합니다.</summary>
    [Fact]
    public void ChangeCircuitTopology_PlansAndChecksConnectionPathsForNewEndpoints()
    {
        DesignProject project = AddProjectDesignInputs(CreateProjectWithTwoRegionPaths().Project);
        var derivedStateBuilder = new ProjectDerivedStateBuilder(
            designValidationOverride: (_, _) => new ValidationReport([]));
        Fixture fixture = Fixture.Create(project, derivedStateBuilder, includeInitialHeatingCurves: true);
        CircuitPathTopologyAnalysis topology = new CircuitPathTopologyService().Analyze(
            project.HeatingPaths.SelectMany(path => path.Segments),
            project.Rules.ValidationRuleVersion);
        CircuitPathComponent component = Assert.Single(topology.Components);
        CircuitId newCircuitId = CircuitId.New();
        var assignment = new CircuitIdentityAssignment(
            newCircuitId,
            parentCircuitId: null,
            preservesExistingIdentity: false,
            component,
            startConnectionPointId: null,
            endConnectionPointId: null,
            preservedSupply: null,
            mergedCircuitIds: []);
        var plan = new CircuitTopologyPlan(
            project.Id,
            project.Revision,
            topology,
            new CircuitIdentityMapping([assignment]));
        ConnectionPoint previousStart = project.ConnectionPoints.Single(point => point.Role == ConnectionPointRole.Start);
        ConnectionPoint previousEnd = project.ConnectionPoints.Single(point => point.Role == ConnectionPointRole.End);
        var startEndpoint = new CircuitEndpointDefinition(
            ConnectionPointId.New(), previousStart.SurfaceAnchor, previousStart.ExternalConnectionPoint);
        var endEndpoint = new CircuitEndpointDefinition(
            ConnectionPointId.New(), previousEnd.SurfaceAnchor, previousEnd.ExternalConnectionPoint);
        var endpoints = new Dictionary<CircuitId, CircuitEndpointPair>
        {
            [newCircuitId] = new CircuitEndpointPair(startEndpoint, endEndpoint)
        };
        var supplies = new Dictionary<CircuitId, Supply>
        {
            [newCircuitId] = project.Circuits[0].Supply
        };
        var queries = new FakeGeometryQueries
        {
            ConnectionPathObstacleResult = GeometryQueryResult<ConnectionObstacleCheck>.Succeeded(
                new ConnectionObstacleCheck(true, null, null, moldExitTransitionFound: true))
        };
        var pathInputs = new Dictionary<ConnectionPointId, CircuitTopologyConnectionPathInput>
        {
            [startEndpoint.Id] = CreateTopologyConnectionPathInput(startEndpoint, new ModelRevision(1)),
            [endEndpoint.Id] = CreateTopologyConnectionPathInput(endEndpoint, new ModelRevision(1))
        };
        var useCase = new ChangeCircuitTopology(fixture.Session, fixture.Coordinator);

        CandidateOutcome outcome = useCase.Execute(
            plan,
            endpoints,
            supplies,
            newConnectionPathInputs: pathInputs,
            geometryQueries: queries);

        Assert.Equal(CandidateStatus.Committed, outcome.Status);
        Assert.Equal(2, queries.ConnectionPathObstacleQueryCount);
        DesignProject committed = fixture.Session.Current.CurrentProject!;
        Assert.Equal(2, committed.ConnectionPaths.Count);
        Assert.All(committed.ConnectionPaths, path =>
            Assert.Contains(path.ConnectionPointId, pathInputs.Keys));
        Assert.Equal(2, queries.CheckedConnectionPaths.Count);
        HeatingPathSegment endpointSegment = project.HeatingPaths
            .SelectMany(path => path.Segments)
            .Single(segment => segment.Id == component.Segments[0].SegmentId);
        CurveGeometrySnapshot measuredCurve = fixture.InitialSnapshot.Geometry!.Curves
            .Single(curve => curve.HeatingPathSegmentId == endpointSegment.Id);
        foreach (ConnectionPath checkedPath in queries.CheckedConnectionPaths)
        {
            ConnectionPoint point = committed.ConnectionPoints.Single(candidate =>
                candidate.Id == checkedPath.ConnectionPointId);
            Vec3 expectedEndpoint = point.ControlPointId == endpointSegment.StartControlPointId
                ? (endpointSegment.Curve.Direction == CurveDirection.Forward ? measuredCurve.Start : measuredCurve.End)
                : (endpointSegment.Curve.Direction == CurveDirection.Forward ? measuredCurve.End : measuredCurve.Start);
            LineCurveDefinition plannedStart = Assert.IsType<LineCurveDefinition>(checkedPath.Segments[0].Curve);
            Assert.Equal(expectedEndpoint, plannedStart.Start);
        }
        Assert.Contains(
            fixture.InitialSnapshot.Geometry.Curves,
            curve => curve.HeatingPathSegmentId == endpointSegment.Id);
    }

    /// <summary>분석 결과에 없는 성분을 끼워 넣은 회로 배정은 확정하지 않습니다.</summary>
    [Fact]
    public void ChangeCircuitTopology_RejectsMappingOutsideAnalyzedTopology()
    {
        Fixture fixture = Fixture.Create();
        var useCase = new ChangeCircuitTopology(fixture.Session, fixture.Coordinator);
        DesignProject project = fixture.Session.Current.CurrentProject!;
        var topology = new CircuitPathTopologyAnalysis([], []);
        var component = new CircuitPathComponent(
            [new CircuitSegmentReference(HeatingPathSegmentId.New(), 0, isReversed: false)],
            ControlPointId.New(),
            ControlPointId.New());
        var assignment = new CircuitIdentityAssignment(
            CircuitId.New(), null, preservesExistingIdentity: false, component,
            null, null, null, []);
        var plan = new CircuitTopologyPlan(
            project.Id,
            project.Revision,
            topology,
            new CircuitIdentityMapping([assignment]));

        CandidateOutcome outcome = useCase.Execute(plan);

        Assert.Equal(CandidateStatus.ValidationFailed, outcome.Status);
        Assert.Equal("CircuitTopologyMappingMismatch", Assert.Single(outcome.Issues).Code);
        Assert.Null(fixture.Workspace.Prepared);
    }

    /// <summary>원본 모델 후보 준비가 성공한 뒤 기존 문서를 새 빈 프로젝트로 교체합니다.</summary>
    [Fact]
    public void OpenModel_CommitsOnlyThePreparedModelCandidate()
    {
        Fixture fixture = Fixture.Create();
        byte[] sourceBytes = [9, 8, 7, 6];
        string sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sourceBytes)).ToLowerInvariant();
        var identity = new ModelIdentity(sourceHash, 1, "FixtureImporter", "1", "face-v1");
        var source = new ModelSource(identity, sourceBytes);
        var useCase = new OpenModel(fixture.Session, fixture.Coordinator);

        CandidateOutcome outcome = useCase.Execute(
            ProjectId.New(),
            "Opened Model",
            source,
            new WorkingCoordinateSystemDefinition(default,
                new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1)));

        Assert.Equal(CandidateStatus.Committed, outcome.Status);
        Assert.Equal("Opened Model", fixture.Session.Current.CurrentProject!.Name);
        Assert.Equal(sourceHash, fixture.Session.Current.RuntimeDocument!.SourceModelSha256);
        Assert.Empty(fixture.Session.Current.CurrentProject.HeatingPaths);
        Assert.Equal(1, fixture.Workspace.Prepared!.PublishCount);
    }

    /// <summary>Native 후보 준비에 실패하면 새 모델의 임시 저장소를 정리하고 기존 세션을 보존합니다.</summary>
    [Fact]
    public void OpenModel_PrepareFailureDisposesUncommittedTemporarySource()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string storageDirectory = Path.Combine(temporaryDirectory.Path, "model-source");
        byte[] sourceBytes = [4, 8, 15, 16, 23, 42];
        using var sourceStream = new MemoryStream(sourceBytes);
        ModelSource source = ModelSource.CaptureFromStream(
            sourceStream,
            sourceBytes.Length,
            1.0,
            "FixtureImporter",
            "1",
            "face-v1",
            inMemoryThresholdBytes: 0,
            temporaryDirectory: storageDirectory);
        Fixture fixture = Fixture.Create();
        fixture.Workspace.PrepareException = new InvalidOperationException("fixture prepare failure");
        SessionSnapshot originalSnapshot = fixture.Session.Current;
        var useCase = new OpenModel(fixture.Session, fixture.Coordinator);

        CandidateOutcome outcome = useCase.Execute(
            ProjectId.New(),
            "Failed Model",
            source,
            new WorkingCoordinateSystemDefinition(default,
                new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1)));

        Assert.Equal(CandidateStatus.PrepareFailed, outcome.Status);
        Assert.Same(originalSnapshot, fixture.Session.Current);
        Assert.Empty(Directory.GetFiles(storageDirectory, "*.step"));
        Assert.Throws<ObjectDisposedException>(() => source.OpenRead());
    }

    private sealed class Fixture
    {
        public ProjectSession Session { get; }
        public DesignProject NextProject { get; }
        public SessionSnapshot InitialSnapshot { get; }
        public FakeGeometryWorkspace Workspace { get; }
        public FakeApplicationEvents Events { get; }
        public CandidateCoordinator Coordinator { get; }
        public Func<IPreparedGeometry, OperationResult<CandidateDerivedState>> Derive { get; set; }

        private Fixture(
            DesignProject? initialProject = null,
            ProjectDerivedStateBuilder? derivedStateBuilder = null,
            bool includeInitialHeatingCurves = false)
        {
            (DesignProject project, RuntimeDocument runtimeDocument, GeometrySnapshot geometry) =
                initialProject is null
                    ? CreateInitialDesign()
                    : CreateSnapshotForProject(initialProject, includeInitialHeatingCurves);
            InitialSnapshot = new SessionSnapshot(
                project, runtimeDocument, geometry, [], null, null, null,
                dirtySinceSave: false, lastSavedRevision: project.Revision, isPresentationOutOfSync: false);
            Session = new ProjectSession(InitialSnapshot);
            NextProject = CopyWithRevision(project, project.Revision.Next());
            Workspace = new FakeGeometryWorkspace();
            Events = new FakeApplicationEvents();
            Coordinator = new CandidateCoordinator(
                Session,
                Workspace,
                new FakeOperationContext(),
                Events,
                derivedStateBuilder);
            Derive = _ => OperationResult<CandidateDerivedState>.Success(new CandidateDerivedState());
        }

        public static Fixture Create(
            DesignProject? initialProject = null,
            ProjectDerivedStateBuilder? derivedStateBuilder = null,
            bool includeInitialHeatingCurves = false) =>
            new(initialProject, derivedStateBuilder, includeInitialHeatingCurves);

        public CandidateOutcome Execute(DesignProject candidate, CancellationToken cancellationToken = default)
        {
            return Coordinator.Execute(
                candidate,
                InitialSnapshot.CurrentProject!.Revision,
                token => Workspace.Prepare(candidate, token),
                Derive,
                cancellationToken);
        }

        private static (DesignProject Project, RuntimeDocument Runtime, GeometrySnapshot Geometry) CreateInitialDesign()
        {
            byte[] sourceBytes = [1, 2, 3, 4, 5];
            string sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sourceBytes)).ToLowerInvariant();
            var model = new ModelIdentity(sourceHash, 1, "Fixture", "1", "face-v1");
            var project = DesignProject.CreateEmpty(
                ProjectId.New(), model,
                new WorkingCoordinateSystemDefinition(default,
                    new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1)),
                "Application Fixture",
                new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero));
            var runtime = new RuntimeDocument(GeometryDocumentId.New(), new ModelRevision(1), sourceHash);
            var stamp = CreateStamp(project.Revision, sourceHash);
            var geometry = new GeometrySnapshot(runtime.DocumentId, runtime.ModelRevision, stamp, []);
            return (project, runtime, geometry);
        }

        private static (DesignProject Project, RuntimeDocument Runtime, GeometrySnapshot Geometry)
            CreateSnapshotForProject(DesignProject project, bool includeHeatingCurves)
        {
            string sourceHash = project.SourceModel.SourceSha256;
            var runtime = new RuntimeDocument(GeometryDocumentId.New(), new ModelRevision(1), sourceHash);
            CurveGeometrySnapshot[] curves = includeHeatingCurves
                ? FakeGeometryWorkspace.CreateCurveSnapshots(project)
                    .Where(curve => curve.HeatingPathSegmentId.HasValue)
                    .ToArray()
                : [];
            var geometry = new GeometrySnapshot(
                runtime.DocumentId,
                runtime.ModelRevision,
                CreateStamp(project.Revision, sourceHash),
                curves);
            return (project, runtime, geometry);
        }

        private static DesignProject CopyWithRevision(DesignProject project, DesignRevision revision) => new(
            project.Id, project.SchemaVersion, revision, project.Name,
            project.CreatedUtc, project.ModifiedUtc, project.SourceModel,
            project.WorkingCoordinateSystem, project.FaceReferences, project.HeatingRegions,
            project.WireSpecification, project.PlacementConstraints, project.HeatingPaths,
            project.Circuits, project.ConnectionPoints, project.ConnectionPaths, project.Rules);

        private static DependencyStamp CreateStamp(DesignRevision revision, string sourceHash) => new(
            revision, sourceHash, new string('b', 64), new string('c', 64), "test-v1");
    }

    private static HeatingRegion CreateRegion(
        RegionId? id = null,
        FaceRefId? faceId = null,
        double xOffset = 0)
    {
        return CreateRegion(id ?? RegionId.New(), faceId ?? FaceRefId.New(), xOffset);
    }

    private static HeatingRegion CreateRegion(
        RegionId id,
        FaceRefId faceId,
        double xOffset = 0)
    {
        BoundaryLoop original = CreateLoop(faceId, xOffset);
        BoundaryLoop effective = CreateLoop(faceId, xOffset);
        var exitSide = new ExitSideReference([new BoundarySegmentRange(effective.Segments[0].Id, 0, 1)]);
        return new HeatingRegion(id, [faceId], [original], [], [], [effective], exitSide);
    }

    private static BoundaryLoop CreateLoop(FaceRefId faceId, double xOffset = 0)
    {
        Vec2[] vertices =
        [
            new(xOffset, 0),
            new(xOffset + 20, 0),
            new(xOffset + 20, 20),
            new(xOffset, 20)
        ];
        BoundarySegment[] segments = vertices.Select((start, index) => new BoundarySegment(
            BoundarySegmentId.New(), start, vertices[(index + 1) % vertices.Length], faceId)).ToArray();
        return new BoundaryLoop(BoundaryLoopId.New(), segments, isHole: false);
    }

    private static (DesignProject Project, FaceReference FaceReference) CreateProjectWithFaceReference()
    {
        (DesignProject project, FaceReference[] faceReferences) = CreateProjectWithFaceReferences(1);
        return (project, faceReferences[0]);
    }

    private static (DesignProject Project, FaceReference[] FaceReferences) CreateProjectWithFaceReferences(int count)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        Fixture fixture = Fixture.Create();
        DesignProject source = fixture.InitialSnapshot.CurrentProject!;
        FaceReference[] faceReferences = Enumerable.Range(0, count)
            .Select(_ => CreateFaceReference(FaceRefId.New(), source.SourceModel.SourceSha256))
            .ToArray();
        var project = new DesignProject(
            source.Id,
            source.SchemaVersion,
            source.Revision,
            source.Name,
            source.CreatedUtc,
            source.ModifiedUtc,
            source.SourceModel,
            source.WorkingCoordinateSystem,
            faceReferences,
            source.HeatingRegions,
            source.WireSpecification,
            source.PlacementConstraints,
            source.HeatingPaths,
            source.Circuits,
            source.ConnectionPoints,
            source.ConnectionPaths,
            source.Rules);
        return (project, faceReferences);
    }

    private static ExitSideReference CreateExitSideForFace(FaceRefId faceId)
    {
        BoundaryLoop loop = CreateLoop(faceId);
        return new ExitSideReference(
            [new BoundarySegmentRange(loop.Segments[0].Id, 0, 1)]);
    }

    private sealed record RegionGraph(
        HeatingRegion Region,
        FaceReference FaceReference,
        HeatingPath Path,
        Circuit? Circuit,
        ConnectionPoint[] ConnectionPoints,
        ConnectionPath[] ConnectionPaths);

    private static (DesignProject Project, RegionGraph Target, RegionGraph Other) CreateProjectWithTwoRegionPaths()
    {
        byte[] sourceBytes = [3, 1, 4, 1, 5];
        string sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sourceBytes)).ToLowerInvariant();
        var model = new ModelIdentity(sourceHash, 1, "Fixture", "1", "face-v1");
        RegionGraph target = CreateRegionGraph(CreateRegion(), sourceHash);
        RegionGraph other = CreateRegionGraph(CreateRegion(xOffset: 30), sourceHash, hasActiveCircuit: false);
        DateTimeOffset timestamp = new(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
        var project = new DesignProject(
            ProjectId.New(), 1, new DesignRevision(4), "Region update fixture", timestamp, timestamp,
            model,
            new WorkingCoordinateSystemDefinition(default,
                new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1)),
            [target.FaceReference, other.FaceReference],
            [target.Region, other.Region],
            wireSpecification: null,
            placementConstraints: null,
            heatingPaths: [target.Path, other.Path],
            circuits: new[] { target.Circuit, other.Circuit }.OfType<Circuit>(),
            connectionPoints: target.ConnectionPoints.Concat(other.ConnectionPoints),
            connectionPaths: target.ConnectionPaths.Concat(other.ConnectionPaths));

        ValidationReport report = project.ValidateReferences();
        Assert.True(report.IsValid, string.Join(", ", report.Issues.Select(issue => issue.Code)));
        return (project, target, other);
    }

    private static DesignProject AddProjectDesignInputs(DesignProject project)
    {
        HeatingPath path = project.HeatingPaths.Single(candidate => candidate.Segments.Any(segment => segment.IsActive));
        return new DesignProject(
            project.Id,
            project.SchemaVersion,
            project.Revision,
            project.Name,
            project.CreatedUtc,
            project.ModifiedUtc,
            project.SourceModel,
            project.WorkingCoordinateSystem,
            project.FaceReferences,
            project.HeatingRegions,
            path.GenerationSource.WireSpecification,
            path.GenerationSource.PlacementConstraints,
            project.HeatingPaths,
            project.Circuits,
            project.ConnectionPoints,
            project.ConnectionPaths,
            project.Rules);
    }

    /// <summary>단일 영역 생성 요청에 영향을 받는 두 번째 영역의 기존 경로를 추가합니다.</summary>
    private static DesignProject AddOtherRegionPath(DesignProject project)
    {
        RegionGraph other = CreateRegionGraph(
            CreateRegion(xOffset: 30),
            project.SourceModel.SourceSha256,
            hasActiveCircuit: false);
        var expandedProject = new DesignProject(
            project.Id,
            project.SchemaVersion,
            project.Revision,
            project.Name,
            project.CreatedUtc,
            project.ModifiedUtc,
            project.SourceModel,
            project.WorkingCoordinateSystem,
            project.FaceReferences.Append(other.FaceReference),
            project.HeatingRegions.Append(other.Region),
            project.WireSpecification,
            project.PlacementConstraints,
            project.HeatingPaths.Append(other.Path),
            project.Circuits,
            project.ConnectionPoints,
            project.ConnectionPaths,
            project.Rules);

        ValidationReport report = expandedProject.ValidateReferences();
        Assert.True(report.IsValid, string.Join(", ", report.Issues.Select(issue => issue.Code)));
        return expandedProject;
    }

    private static DesignProject CreateProjectWithInteriorControlPoint(out ControlPointId interiorPointId)
    {
        DesignProject source = AddProjectDesignInputs(CreateProjectWithTwoRegionPaths().Project);
        HeatingPath path = source.HeatingPaths.Single(candidate => candidate.Segments.Any(segment => segment.IsActive));
        HeatingPathSegment originalSegment = path.Segments.Single(segment => segment.IsActive);
        ControlPoint startPoint = path.ControlPoints.Single(point => point.Id == originalSegment.StartControlPointId);
        ControlPoint endPoint = path.ControlPoints.Single(point => point.Id == originalSegment.EndControlPointId);
        var middlePoint = new ControlPoint(ControlPointId.New(), new Vec3(5, 0, 0));
        interiorPointId = middlePoint.Id;
        var firstSegment = new HeatingPathSegment(
            HeatingPathSegmentId.New(),
            HeatingPathSegmentKind.HeatingRun,
            startPoint.Id,
            middlePoint.Id,
            0,
            true,
            new LineCurveDefinition(startPoint.Position, middlePoint.Position),
            laneIndex: 0);
        var secondSegment = new HeatingPathSegment(
            HeatingPathSegmentId.New(),
            HeatingPathSegmentKind.HeatingRun,
            middlePoint.Id,
            endPoint.Id,
            1,
            true,
            new LineCurveDefinition(middlePoint.Position, endPoint.Position),
            laneIndex: 1);
        var replacementPath = new HeatingPath(
            path.Id,
            path.GenerationSource,
            path.ControlPoints.Append(middlePoint),
            path.GeneratedSegments,
            [firstSegment, secondSegment]);

        Circuit previousCircuit = source.Circuits.Single();
        var replacementCircuit = new Circuit(
            previousCircuit.Id,
            previousCircuit.Sequence,
            [
                new CircuitSegmentReference(firstSegment.Id, 0, false),
                new CircuitSegmentReference(secondSegment.Id, 1, false)
            ],
            previousCircuit.StartConnectionPointId,
            previousCircuit.EndConnectionPointId,
            previousCircuit.Supply);
        ConnectionPoint[] connectionPoints = source.ConnectionPoints
            .Select(point => point.ControlPointId == startPoint.Id
                ? new ConnectionPoint(
                    point.Id,
                    point.CircuitId,
                    point.Role,
                    firstSegment.Id,
                    point.ControlPointId,
                    point.SurfaceAnchor,
                    point.ExternalConnectionPoint)
                : new ConnectionPoint(
                    point.Id,
                    point.CircuitId,
                    point.Role,
                    secondSegment.Id,
                    point.ControlPointId,
                    point.SurfaceAnchor,
                    point.ExternalConnectionPoint))
            .ToArray();

        return new DesignProject(
            source.Id,
            source.SchemaVersion,
            source.Revision,
            source.Name,
            source.CreatedUtc,
            source.ModifiedUtc,
            source.SourceModel,
            source.WorkingCoordinateSystem,
            source.FaceReferences,
            source.HeatingRegions,
            source.WireSpecification,
            source.PlacementConstraints,
            source.HeatingPaths.Select(candidate => candidate.Id == path.Id ? replacementPath : candidate),
            [replacementCircuit],
            connectionPoints,
            source.ConnectionPaths,
            source.Rules);
    }

    private static (
        DesignProject Project,
        HeatingPathGenerationRequest Request,
        HeatingPathCircuitSetup CircuitSetup,
        FakeGeometryQueries GeometryQueries) CreateHeatingPathGenerationCase()
    {
        HeatingRegion region = CreateRegion();
        Fixture baseFixture = Fixture.Create();
        DesignProject baseProject = baseFixture.InitialSnapshot.CurrentProject!;
        DesignProject project = new(
            baseProject.Id,
            baseProject.SchemaVersion,
            baseProject.Revision,
            baseProject.Name,
            baseProject.CreatedUtc,
            baseProject.ModifiedUtc,
            baseProject.SourceModel,
            baseProject.WorkingCoordinateSystem,
            [CreateFaceReference(region.FaceReferences[0], baseProject.SourceModel.SourceSha256)],
            [region],
            wireSpecification: null,
            placementConstraints: null,
            heatingPaths: [],
            circuits: [],
            connectionPoints: [],
            connectionPaths: []);

        FaceRefId faceId = region.FaceReferences[0];
        Vec2 startUv = new(2, 0);
        Vec2 endUv = new(18, 0);
        var startAnchor = new SurfaceAnchor(faceId, startUv, startUv, new Vec3(2, 0, 0));
        var endAnchor = new SurfaceAnchor(faceId, endUv, endUv, new Vec3(18, 0, 0));
        var wire = new WireSpecification("Fixture alloy", new LengthMm(0.3), new ResistanceOhmsPerMeter(100));
        var constraints = new PlacementConstraints(
            new LengthMm(1),
            new LengthMm(0),
            new LengthMm(0),
            PlacementDirection.AlongX,
            startAnchor,
            endAnchor,
            region.ExitSide);
        WorkingCoordinateSystem coordinateSystem = CreateCoordinateSystem(new ModelRevision(3));
        var request = new HeatingPathGenerationRequest(
            project.Id,
            project.Revision,
            region,
            wire,
            constraints,
            coordinateSystem,
            coordinateSystem.Revision,
            ManualEditPolicy.ReplaceEditedHeatingPath);

        var startEndpoint = new CircuitEndpointDefinition(
            ConnectionPointId.New(),
            startAnchor,
            startAnchor.ModelPoint + new Vec3(-3, 0, 2));
        var endEndpoint = new CircuitEndpointDefinition(
            ConnectionPointId.New(),
            endAnchor,
            endAnchor.ModelPoint + new Vec3(3, 0, 2));
        var supply = new Supply(new VoltageVrms(220), new VoltageVrms(240), new CurrentAmps(25));
        var circuitSetup = new HeatingPathCircuitSetup(
            CircuitId.New(),
            new CircuitEndpointPair(startEndpoint, endEndpoint),
            supply,
            new Dictionary<ConnectionPointId, ConnectionPath>
            {
                [startEndpoint.Id] = CreateConnectionPath(
                    startEndpoint.Id,
                    startEndpoint.SurfaceAnchor.ModelPoint,
                    startEndpoint.ExternalConnectionPoint),
                [endEndpoint.Id] = CreateConnectionPath(
                    endEndpoint.Id,
                    endEndpoint.SurfaceAnchor.ModelPoint,
                    endEndpoint.ExternalConnectionPoint)
            });
        var geometryQueries = new FakeGeometryQueries
        {
            FirstHitQuery = (_, workingUv, selectedFace) => GeometryQueryResult<SurfaceHit>.Succeeded(
                new SurfaceHit(
                    selectedFace,
                    new Vec3(workingUv.X, workingUv.Y, 0),
                    new Vec3(0, 0, 1),
                    workingUv))
        };

        return (project, request, circuitSetup, geometryQueries);
    }

    /// <summary>같은 영역에 topology 편집 경로를 둔 재생성 정책 검사 입력을 만듭니다.</summary>
    private static (
        DesignProject Project,
        HeatingPath ExistingPath,
        HeatingPathGenerationRequest Request,
        HeatingPathCircuitSetup CircuitSetup,
        FakeGeometryQueries GeometryQueries) CreateChangedTopologyRegenerationCase(
            ManualEditPolicy manualEditPolicy)
    {
        var source = CreateHeatingPathGenerationCase();
        HeatingPathGenerationRequest sourceRequest = source.Request;
        ControlPointId startId = ControlPointId.New();
        ControlPointId endId = ControlPointId.New();
        ControlPointId insertedId = ControlPointId.New();
        var start = new ControlPoint(startId, sourceRequest.PlacementConstraints.StartAnchor.ModelPoint);
        var end = new ControlPoint(endId, sourceRequest.PlacementConstraints.EndAnchor.ModelPoint);
        var inserted = new ControlPoint(insertedId, new Vec3(10, 1, 0));
        var generatedSegment = new HeatingPathSegment(
            HeatingPathSegmentId.New(),
            HeatingPathSegmentKind.HeatingRun,
            startId,
            endId,
            0,
            true,
            new LineCurveDefinition(start.Position, end.Position),
            laneIndex: 0);
        HeatingPathSegment[] editedSegments =
        [
            new(HeatingPathSegmentId.New(), HeatingPathSegmentKind.HeatingRun,
                startId, insertedId, 0, true,
                new LineCurveDefinition(start.Position, inserted.Position), laneIndex: 0),
            new(HeatingPathSegmentId.New(), HeatingPathSegmentKind.HeatingRun,
                insertedId, endId, 1, true,
                new LineCurveDefinition(inserted.Position, end.Position), laneIndex: 0)
        ];
        HeatingPathCircuitSetup circuitSetup = source.CircuitSetup;
        var existingPath = new HeatingPath(
            HeatingPathId.New(),
            new HeatingPathGenerationSource(
                sourceRequest.Region.Id,
                sourceRequest.PlacementConstraints,
                sourceRequest.WireSpecification,
                "regeneration-assessment-test-v1"),
            [start, inserted, end],
            [generatedSegment],
            editedSegments);
        var existingCircuit = new Circuit(
            circuitSetup.CircuitId,
            0,
            editedSegments.Select((segment, index) =>
                new CircuitSegmentReference(segment.Id, index, isReversed: false)),
            circuitSetup.Endpoints.Start.Id,
            circuitSetup.Endpoints.End.Id,
            circuitSetup.Supply);
        ConnectionPoint[] existingConnectionPoints =
        [
            new ConnectionPoint(
                circuitSetup.Endpoints.Start.Id,
                circuitSetup.CircuitId,
                ConnectionPointRole.Start,
                editedSegments[0].Id,
                startId,
                circuitSetup.Endpoints.Start.SurfaceAnchor,
                circuitSetup.Endpoints.Start.ExternalConnectionPoint),
            new ConnectionPoint(
                circuitSetup.Endpoints.End.Id,
                circuitSetup.CircuitId,
                ConnectionPointRole.End,
                editedSegments[^1].Id,
                endId,
                circuitSetup.Endpoints.End.SurfaceAnchor,
                circuitSetup.Endpoints.End.ExternalConnectionPoint)
        ];

        DesignProject baseProject = source.Project;
        var project = new DesignProject(
            baseProject.Id,
            baseProject.SchemaVersion,
            baseProject.Revision,
            baseProject.Name,
            baseProject.CreatedUtc,
            baseProject.ModifiedUtc,
            baseProject.SourceModel,
            baseProject.WorkingCoordinateSystem,
            baseProject.FaceReferences,
            baseProject.HeatingRegions,
            baseProject.WireSpecification,
            baseProject.PlacementConstraints,
            [existingPath],
            [existingCircuit],
            existingConnectionPoints,
            circuitSetup.ConnectionPaths.Values,
            baseProject.Rules);
        var request = new HeatingPathGenerationRequest(
            project.Id,
            project.Revision,
            sourceRequest.Region,
            sourceRequest.WireSpecification,
            sourceRequest.PlacementConstraints,
            sourceRequest.CoordinateSystem,
            sourceRequest.ExpectedModelRevision,
            manualEditPolicy);

        return (
            project,
            existingPath,
            request,
            circuitSetup,
            source.GeometryQueries);
    }

    private static DesignProject CreateProjectWithToggleableTurn(out HeatingPathSegmentId turnSegmentId)
    {
        DesignProject source = AddProjectDesignInputs(CreateProjectWithTwoRegionPaths().Project);
        HeatingPath path = source.HeatingPaths.Single(candidate => candidate.Segments.Any(segment => segment.IsActive));
        HeatingPathSegment originalSegment = path.Segments.Single(segment => segment.IsActive);
        ControlPoint startPoint = path.ControlPoints.Single(point => point.Id == originalSegment.StartControlPointId);
        ControlPoint endPoint = path.ControlPoints.Single(point => point.Id == originalSegment.EndControlPointId);
        var turnStart = new ControlPoint(ControlPointId.New(), new Vec3(3, 0, 0));
        var turnEnd = new ControlPoint(ControlPointId.New(), new Vec3(7, 0, 0));
        var firstRun = new HeatingPathSegment(
            HeatingPathSegmentId.New(), HeatingPathSegmentKind.HeatingRun,
            startPoint.Id, turnStart.Id, 0, true,
            new LineCurveDefinition(startPoint.Position, turnStart.Position), laneIndex: 0);
        var turn = new HeatingPathSegment(
            HeatingPathSegmentId.New(), HeatingPathSegmentKind.Turn,
            turnStart.Id, turnEnd.Id, 1, true,
            new LineCurveDefinition(turnStart.Position, turnEnd.Position));
        var secondRun = new HeatingPathSegment(
            HeatingPathSegmentId.New(), HeatingPathSegmentKind.HeatingRun,
            turnEnd.Id, endPoint.Id, 2, true,
            new LineCurveDefinition(turnEnd.Position, endPoint.Position), laneIndex: 1);
        turnSegmentId = turn.Id;
        var replacementPath = new HeatingPath(
            path.Id,
            path.GenerationSource,
            path.ControlPoints.Append(turnStart).Append(turnEnd),
            path.GeneratedSegments,
            [firstRun, turn, secondRun]);

        Circuit previousCircuit = source.Circuits.Single();
        var replacementCircuit = new Circuit(
            previousCircuit.Id,
            previousCircuit.Sequence,
            [
                new CircuitSegmentReference(firstRun.Id, 0, false),
                new CircuitSegmentReference(turn.Id, 1, false),
                new CircuitSegmentReference(secondRun.Id, 2, false)
            ],
            previousCircuit.StartConnectionPointId,
            previousCircuit.EndConnectionPointId,
            previousCircuit.Supply);
        ConnectionPoint[] connectionPoints = source.ConnectionPoints
            .Select(point => point.Role == ConnectionPointRole.Start
                ? new ConnectionPoint(
                    point.Id,
                    point.CircuitId,
                    point.Role,
                    firstRun.Id,
                    point.ControlPointId,
                    point.SurfaceAnchor,
                    point.ExternalConnectionPoint)
                : new ConnectionPoint(
                    point.Id,
                    point.CircuitId,
                    point.Role,
                    secondRun.Id,
                    point.ControlPointId,
                    point.SurfaceAnchor,
                    point.ExternalConnectionPoint))
            .ToArray();

        return new DesignProject(
            source.Id,
            source.SchemaVersion,
            source.Revision,
            source.Name,
            source.CreatedUtc,
            source.ModifiedUtc,
            source.SourceModel,
            source.WorkingCoordinateSystem,
            source.FaceReferences,
            source.HeatingRegions,
            source.WireSpecification,
            source.PlacementConstraints,
            source.HeatingPaths.Select(candidate => candidate.Id == path.Id ? replacementPath : candidate),
            [replacementCircuit],
            connectionPoints,
            source.ConnectionPaths,
            source.Rules);
    }

    private static DesignProject CreateProjectWithEditableTurn(
        out HeatingPathId pathId,
        out HeatingPathSegmentId turnSegmentId)
    {
        DesignProject source = CreateProjectWithToggleableTurn(out turnSegmentId);
        HeatingPathSegmentId targetSegmentId = turnSegmentId;
        HeatingPath path = source.HeatingPaths.Single(candidate =>
            candidate.Segments.Any(segment => segment.Id == targetSegmentId));
        HeatingPathSegment turn = path.Segments.Single(segment => segment.Id == targetSegmentId);
        ControlPoint turnStart = path.ControlPoints.Single(point => point.Id == turn.StartControlPointId);
        ControlPoint turnEnd = path.ControlPoints.Single(point => point.Id == turn.EndControlPointId);
        var curve = new PolylineCurveDefinition(
            [turnStart.Position, new Vec3(5, 1, 0), turnEnd.Position]);
        var replacementTurn = new HeatingPathSegment(
            turn.Id,
            turn.Kind,
            turn.StartControlPointId,
            turn.EndControlPointId,
            turn.Sequence,
            turn.IsActive,
            curve,
            turn.LaneIndex);
        HeatingPathSegment[] segments = path.Segments
            .Select(segment => segment.Id == turn.Id ? replacementTurn : segment)
            .ToArray();
        var replacementPath = new HeatingPath(
            path.Id,
            path.GenerationSource,
            path.ControlPoints,
            path.GeneratedSegments,
            segments);
        pathId = path.Id;

        return new DesignProject(
            source.Id,
            source.SchemaVersion,
            source.Revision,
            source.Name,
            source.CreatedUtc,
            source.ModifiedUtc,
            source.SourceModel,
            source.WorkingCoordinateSystem,
            source.FaceReferences,
            source.HeatingRegions,
            source.WireSpecification,
            source.PlacementConstraints,
            source.HeatingPaths.Select(candidate => candidate.Id == path.Id ? replacementPath : candidate),
            source.Circuits,
            source.ConnectionPoints,
            source.ConnectionPaths,
            source.Rules);
    }

    private static CircuitEndpointDefinition CreateEndpointDefinition(
        DesignProject project,
        ControlPointId controlPointId)
    {
        ConnectionPoint? existingPoint = project.ConnectionPoints
            .SingleOrDefault(point => point.ControlPointId == controlPointId);
        if (existingPoint is not null)
        {
            return new CircuitEndpointDefinition(
                existingPoint.Id,
                existingPoint.SurfaceAnchor,
                existingPoint.ExternalConnectionPoint);
        }

        ControlPoint controlPoint = project.HeatingPaths
            .SelectMany(path => path.ControlPoints)
            .Single(point => point.Id == controlPointId);
        HeatingPath pathForPoint = project.HeatingPaths
            .Single(path => path.ControlPoints.Any(point => point.Id == controlPointId));
        HeatingRegion region = project.HeatingRegions
            .Single(candidate => candidate.Id == pathForPoint.GenerationSource.RegionId);
        FaceRefId faceId = region.FaceReferences[0];
        var uv = new Vec2(controlPoint.Position.X, controlPoint.Position.Y);
        var anchor = new SurfaceAnchor(faceId, uv, uv, controlPoint.Position);
        var externalPoint = controlPoint.Position + new Vec3(0, -5, 2);
        return new CircuitEndpointDefinition(ConnectionPointId.New(), anchor, externalPoint);
    }

    private static RegionGraph CreateRegionGraph(
        HeatingRegion region,
        string sourceHash,
        bool hasActiveCircuit = true)
    {
        FaceRefId faceId = region.FaceReferences[0];
        var startAnchor = CreateAnchor(faceId, new Vec2(2, 10));
        var endAnchor = CreateAnchor(faceId, new Vec2(18, 10));
        var constraints = new PlacementConstraints(
            new LengthMm(1), new LengthMm(1), new LengthMm(0), PlacementDirection.AlongX,
            startAnchor, endAnchor, region.ExitSide);
        var wire = new WireSpecification("Fixture alloy", new LengthMm(0.3), new ResistanceOhmsPerMeter(100));
        var generationSource = new HeatingPathGenerationSource(region.Id, constraints, wire, "path-v1");
        ControlPointId startId = ControlPointId.New();
        ControlPointId endId = ControlPointId.New();
        var segment = new HeatingPathSegment(
            HeatingPathSegmentId.New(), HeatingPathSegmentKind.HeatingRun,
            startId, endId, 0, hasActiveCircuit,
            new LineCurveDefinition(default, new Vec3(10, 0, 0)));

        var path = new HeatingPath(
            HeatingPathId.New(), generationSource,
            [new ControlPoint(startId, default), new ControlPoint(endId, new Vec3(10, 0, 0))],
            [segment], [segment]);
        FaceReference face = CreateFaceReference(faceId, sourceHash);
        if (!hasActiveCircuit)
            return new RegionGraph(region, face, path, null, [], []);

        CircuitId circuitId = CircuitId.New();
        ConnectionPointId startPointId = ConnectionPointId.New();
        ConnectionPointId endPointId = ConnectionPointId.New();
        var circuit = new Circuit(
            circuitId,
            0,
            [new CircuitSegmentReference(segment.Id, 0, isReversed: false)],
            startPointId,
            endPointId,
            new Supply(new VoltageVrms(220), new VoltageVrms(240), new CurrentAmps(25)));
        ConnectionPoint[] points =
        [
            new ConnectionPoint(
                startPointId, circuitId, ConnectionPointRole.Start, segment.Id, startId,
                startAnchor, new Vec3(-10, 0, 0)),
            new ConnectionPoint(
                endPointId, circuitId, ConnectionPointRole.End, segment.Id, endId,
                endAnchor, new Vec3(20, 0, 0))
        ];
        ConnectionPath[] connectionPaths =
        [
            CreateConnectionPath(startPointId),
            CreateConnectionPath(endPointId)
        ];
        return new RegionGraph(region, face, path, circuit, points, connectionPaths);
    }

    private static ConnectionPath CreateConnectionPath(ConnectionPointId pointId)
    {
        var onMold = new ConnectionPathSegment(
            ConnectionPathSegmentId.New(), 0,
            ConductorRole.HeatingWireExtension, GeometryDomain.OnMold,
            new LengthMm(0), new LineCurveDefinition(default, new Vec3(1, 0, 0)));
        var external = new ConnectionPathSegment(
            ConnectionPathSegmentId.New(), 1,
            ConductorRole.SeparateConnectionWire, GeometryDomain.External,
            new LengthMm(0), new LineCurveDefinition(new Vec3(1, 0, 0), new Vec3(2, 0, 0)),
            isMoldExitTransition: true);
        return new ConnectionPath(ConnectionPathId.New(), pointId, [onMold, external]);
    }

    /// <summary>끝점의 표면 앵커와 외부 좌표를 잇는 두 구간 계획 입력을 만듭니다.</summary>
    private static CircuitTopologyConnectionPathInput CreateTopologyConnectionPathInput(
        CircuitEndpointDefinition endpoint,
        ModelRevision modelRevision) =>
        new(
            CreateCoordinateSystem(modelRevision),
            modelRevision,
            [
                new ConnectionPathLegRequest(
                    endpoint.SurfaceAnchor.ModelPoint,
                    GeometryDomain.OnMold,
                    ConductorRole.HeatingWireExtension,
                    new LengthMm(0)),
                new ConnectionPathLegRequest(
                    endpoint.ExternalConnectionPoint,
                    GeometryDomain.External,
                    ConductorRole.SeparateConnectionWire,
                    new LengthMm(0),
                    isMoldExitTransition: true)
            ]);

    private static ConnectionPath CreateConnectionPath(
        ConnectionPointId pointId,
        Vec3 surfacePoint,
        Vec3 externalPoint)
    {
        Vec3 transitionPoint = surfacePoint + new Vec3(0, 0, 0.5);
        var onMold = new ConnectionPathSegment(
            ConnectionPathSegmentId.New(),
            0,
            ConductorRole.HeatingWireExtension,
            GeometryDomain.OnMold,
            new LengthMm(0),
            new LineCurveDefinition(surfacePoint, transitionPoint));
        var external = new ConnectionPathSegment(
            ConnectionPathSegmentId.New(),
            1,
            ConductorRole.SeparateConnectionWire,
            GeometryDomain.External,
            new LengthMm(0),
            new LineCurveDefinition(transitionPoint, externalPoint),
            isMoldExitTransition: true);
        return new ConnectionPath(ConnectionPathId.New(), pointId, [onMold, external]);
    }

    private static FaceReference CreateFaceReference(FaceRefId faceId, string sourceHash) => new(
        faceId,
        sourceHash,
        0,
        FaceGeometryKind.Plane,
        new AreaMm2(400),
        default,
        "fixture-face-boundary",
        []);

    private static SurfaceAnchor CreateAnchor(FaceRefId faceId, Vec2 uv) =>
        new(faceId, default, uv, new Vec3(0, 0, 1));

    private static PlacementConstraints CreateConstraints(HeatingRegion region) => new(
        new LengthMm(1),
        new LengthMm(1),
        new LengthMm(0),
        PlacementDirection.AlongX,
        CreateAnchor(region.FaceReferences[0], new Vec2(2, 10)),
        CreateAnchor(region.FaceReferences[0], new Vec2(18, 10)),
        region.ExitSide);

    private static WorkingCoordinateSystem CreateCoordinateSystem(ModelRevision revision) => new(
        new WorkingCoordinateSystemId(1),
        revision,
        new ProjectionFrame(default, new Vec3(0, 0, 1), new Vec3(1, 0, 0)));

    private sealed class FakeGeometryWorkspace : IGeometryWorkspace
    {
        public FakePreparedGeometry? Prepared { get; private set; }
        public int PrepareCount { get; private set; }
        public Exception? PrepareException { get; set; }
        public bool OmitAllCurves { get; set; }
        public bool ChangePreparedCurveDefinition { get; set; }
        public bool ChangePreparedConnectionCurveDefinition { get; set; }
        public bool MarkPreparedApproximationIncomplete { get; set; }
        public bool PublishPresentationOutOfSync { get; set; }

        public IPreparedGeometry Prepare(DesignProject candidate, CancellationToken cancellationToken = default)
        {
            PrepareCount++;
            if (PrepareException is not null)
                throw PrepareException;

            string sourceHash = candidate.SourceModel.SourceSha256;
            var runtime = new RuntimeDocument(GeometryDocumentId.New(), new ModelRevision(2), sourceHash);
            var stamp = new DependencyStamp(
                candidate.Revision, sourceHash, new string('b', 64), new string('c', 64), "test-v1");
            CurveGeometrySnapshot[] curves = OmitAllCurves
                ? []
                : CreateCurveSnapshots(candidate);
            if (ChangePreparedCurveDefinition && curves.Length > 0)
                curves[0] = ChangeDefinition(curves[0]);
            if (ChangePreparedConnectionCurveDefinition)
                ChangeFirstConnectionDefinition(curves);
            if (MarkPreparedApproximationIncomplete && curves.Length > 0)
                curves[0] = WithIncompleteApproximation(curves[0]);
            var snapshot = new GeometrySnapshot(runtime.DocumentId, runtime.ModelRevision, stamp, curves);
            Prepared = new FakePreparedGeometry(runtime, snapshot)
            {
                PresentationOutOfSync = PublishPresentationOutOfSync
            };
            return Prepared;
        }

        public IPreparedGeometry PrepareModel(DesignProject candidate, ModelSource modelSource, CancellationToken cancellationToken = default)
        {
            if (PrepareException is not null)
                throw PrepareException;

            string sourceHash = modelSource.Identity.SourceSha256;
            var runtime = new RuntimeDocument(GeometryDocumentId.New(), new ModelRevision(2), sourceHash);
            var stamp = new DependencyStamp(
                candidate.Revision, sourceHash, new string('b', 64), new string('c', 64), "test-v1");
            CurveGeometrySnapshot[] curves = OmitAllCurves
                ? []
                : CreateCurveSnapshots(candidate);
            if (ChangePreparedCurveDefinition && curves.Length > 0)
                curves[0] = ChangeDefinition(curves[0]);
            if (ChangePreparedConnectionCurveDefinition)
                ChangeFirstConnectionDefinition(curves);
            if (MarkPreparedApproximationIncomplete && curves.Length > 0)
                curves[0] = WithIncompleteApproximation(curves[0]);
            Prepared = new FakePreparedGeometry(
                runtime,
                new GeometrySnapshot(runtime.DocumentId, runtime.ModelRevision, stamp, curves))
            {
                PresentationOutOfSync = PublishPresentationOutOfSync
            };
            return Prepared;
        }

        public IPreparedGeometry Restore(DesignProject project, ModelSource modelSource, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        /// <summary>확정 발열 경로와 연결 경로에서 테스트 측정 snapshot을 만듭니다.</summary>
        internal static CurveGeometrySnapshot[] CreateCurveSnapshots(DesignProject project)
        {
            CurveGeometrySnapshot[] heatingCurves = project.HeatingPaths
                .SelectMany(path => path.Segments)
                .Where(segment => segment.IsActive)
                .Select(segment => CreateCurveSnapshot(segment.Id, segment.Curve))
                .ToArray();
            CurveGeometrySnapshot[] connectionCurves = project.ConnectionPaths
                .SelectMany(path => path.Segments)
                .Select(segment => CreateCurveSnapshot(segment.Id, segment.Curve))
                .ToArray();

            return heatingCurves.Concat(connectionCurves).ToArray();
        }

        private static CurveGeometrySnapshot ChangeDefinition(CurveGeometrySnapshot curve)
        {
            CurveDefinition changedDefinition = curve.Definition switch
            {
                LineCurveDefinition line => new LineCurveDefinition(
                    line.Start,
                    line.End + new Vec3(0, 0, 1)),
                PolylineCurveDefinition polyline => new PolylineCurveDefinition(
                    polyline.Points.Take(polyline.Points.Count - 1)
                        .Append(polyline.Points[^1] + new Vec3(0, 0, 1))),
                _ => throw new NotSupportedException("테스트 fixture의 준비 곡선 형식은 선형이어야 합니다.")
            };

            return curve.HeatingPathSegmentId is { } heatingId
                ? new CurveGeometrySnapshot(
                    heatingId,
                    null,
                    changedDefinition,
                    curve.LengthMm,
                    curve.Start,
                    curve.End,
                    curve.Samples,
                    curve.Approximation)
                : new CurveGeometrySnapshot(
                    null,
                    curve.ConnectionPathSegmentId,
                    changedDefinition,
                    curve.LengthMm,
                    curve.Start,
                    curve.End,
                    curve.Samples,
                    curve.Approximation);
        }

        private static void ChangeFirstConnectionDefinition(CurveGeometrySnapshot[] curves)
        {
            int index = Array.FindIndex(curves, curve => curve.ConnectionPathSegmentId.HasValue);
            if (index < 0)
                throw new InvalidOperationException("테스트 설계에 연결 곡선이 없습니다.");

            curves[index] = ChangeDefinition(curves[index]);
        }

        private static CurveGeometrySnapshot WithIncompleteApproximation(CurveGeometrySnapshot curve)
        {
            CurveApproximationInfo approximation = curve.Approximation;
            var incompleteApproximation = new CurveApproximationInfo(
                approximation.RequestedDeflectionMm,
                approximation.RequestedAngularToleranceRad,
                approximation.MaximumMeasuredDeviationMm,
                approximation.MaximumSampleSpacingMm,
                approximation.ProbeCount,
                isComplete: false,
                approximation.RuleVersion);

            return curve.HeatingPathSegmentId is { } heatingId
                ? new CurveGeometrySnapshot(
                    heatingId, null, curve.Definition, curve.LengthMm, curve.Start, curve.End,
                    curve.Samples, incompleteApproximation)
                : new CurveGeometrySnapshot(
                    null, curve.ConnectionPathSegmentId, curve.Definition, curve.LengthMm,
                    curve.Start, curve.End, curve.Samples, incompleteApproximation);
        }

        private static CurveGeometrySnapshot CreateCurveSnapshot(
            HeatingPathSegmentId segmentId,
            CurveDefinition definition)
        {
            Vec3[] samples = Sample(definition);
            return new CurveGeometrySnapshot(
                segmentId,
                null,
                definition,
                new LengthMm(MeasurePolylineLength(samples)),
                samples[0],
                samples[^1],
                samples,
                CreateApproximation(samples));
        }

        private static CurveGeometrySnapshot CreateCurveSnapshot(
            ConnectionPathSegmentId segmentId,
            CurveDefinition definition)
        {
            Vec3[] samples = Sample(definition);
            return new CurveGeometrySnapshot(
                null,
                segmentId,
                definition,
                new LengthMm(MeasurePolylineLength(samples)),
                samples[0],
                samples[^1],
                samples,
                CreateApproximation(samples));
        }

        private static Vec3[] Sample(CurveDefinition definition)
        {
            IEnumerable<Vec3> forwardSamples = definition switch
            {
                LineCurveDefinition line => [line.Start, line.End],
                PolylineCurveDefinition polyline => polyline.Points,
                _ => throw new NotSupportedException(
                    $"테스트 geometry workspace는 {definition.Kind} 곡선을 표본화하지 않습니다.")
            };
            Vec3[] samples = forwardSamples.ToArray();
            if (definition.Direction == CurveDirection.Reversed)
                Array.Reverse(samples);

            return samples;
        }

        private static double MeasurePolylineLength(IReadOnlyList<Vec3> samples) =>
            samples.Zip(samples.Skip(1), (first, second) => (second - first).Length).Sum();

        private static CurveApproximationInfo CreateApproximation(IReadOnlyList<Vec3> samples)
        {
            double maximumSpacing = samples.Zip(samples.Skip(1), (first, second) =>
                (second - first).Length).DefaultIfEmpty().Max();
            return new CurveApproximationInfo(
                requestedDeflectionMm: 0.05,
                requestedAngularToleranceRad: 0.1,
                maximumMeasuredDeviationMm: 0,
                maximumSampleSpacingMm: maximumSpacing,
                probeCount: samples.Count,
                isComplete: true,
                ruleVersion: "application-test-curve-v1");
        }
    }

    private sealed class FakePreparedGeometry : IPreparedGeometry
    {
        public GeometrySnapshot Snapshot { get; }
        public IGeometryQueries Queries { get; } = new FakeGeometryQueries();
        public RuntimeDocument RuntimeDocument { get; }
        public int PublishCount { get; private set; }
        public int DisposeCount { get; private set; }
        public bool RejectPublish { get; set; }
        public bool PresentationOutOfSync { get; set; }

        public FakePreparedGeometry(RuntimeDocument runtimeDocument, GeometrySnapshot snapshot)
        {
            RuntimeDocument = runtimeDocument;
            Snapshot = snapshot;
        }

        public GeometryCommitReceipt Publish()
        {
            PublishCount++;
            return RejectPublish
                ? new GeometryCommitReceipt(false, null, "FixturePublishRejected")
                : new GeometryCommitReceipt(
                    true,
                    RuntimeDocument,
                    presentationOutOfSync: PresentationOutOfSync);
        }

        public void Dispose() => DisposeCount++;
    }

    private sealed class FakeGeometryQueries : IGeometryQueries
    {
        /// <summary>장애물 검사를 요청받은 연결 경로를 순서대로 기록합니다.</summary>
        public List<ConnectionPath> CheckedConnectionPaths { get; } = [];
        public GeometryQueryResult<SelectedTopology> SelectedTopologyResult { get; set; } =
            GeometryQueryResult<SelectedTopology>.Succeeded(new SelectedTopology([], []));
        public int SelectedTopologyQueryCount { get; private set; }
        public int FirstHitQueryCount { get; private set; }
        public int ConnectionPathObstacleQueryCount { get; private set; }
        public GeometryQueryResult<ConnectionObstacleCheck> ConnectionPathObstacleResult { get; set; } =
            GeometryQueryResult<ConnectionObstacleCheck>.Succeeded(
                new ConnectionObstacleCheck(false, null, null, moldExitTransitionFound: false));
        public Func<WorkingCoordinateSystem, Vec2, FaceRefId, GeometryQueryResult<SurfaceHit>>? FirstHitQuery { get; set; }

        public GeometryQueryResult<SurfaceHit> QueryFirstHit(
            WorkingCoordinateSystem coordinateSystem,
            Vec2 workingUv,
            FaceRefId selectedFaceReferenceId,
            CancellationToken cancellationToken = default)
        {
            FirstHitQueryCount++;
            return FirstHitQuery?.Invoke(coordinateSystem, workingUv, selectedFaceReferenceId)
                ?? GeometryQueryResult<SurfaceHit>.Miss();
        }

        public GeometryQueryResult<SelectedTopology> GetSelectedTopology(IReadOnlyList<FaceRefId> selectedFaceReferences, CancellationToken cancellationToken = default)
        {
            SelectedTopologyQueryCount++;
            return SelectedTopologyResult;
        }

        public GeometryQueryResult<bool> AreAdjacent(FaceRefId first, FaceRefId second, CancellationToken cancellationToken = default) =>
            GeometryQueryResult<bool>.Succeeded(false);

        public GeometryQueryResult<ClearanceMeasurement> MeasureClearance(IReadOnlyList<CurveGeometrySnapshot> curves, LengthMm wireDiameterMm, LengthMm requiredClearGapMm, GeometryToleranceProfile tolerances, CancellationToken cancellationToken = default) =>
            GeometryQueryResult<ClearanceMeasurement>.Unresolved([]);

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
            if (cancellationToken.IsCancellationRequested || selectedSurfaceFaces.Count == 0)
                return GeometryQueryResult<InsertionDepthAdjustedPath>.Unresolved([]);

            FaceRefId surfaceFace = selectedSurfaceFaces[0];
            var surfacePoints = new List<Vec3>(surfacePathWorkingSamples.Count);
            foreach (Vec2 workingUv in surfacePathWorkingSamples)
            {
                GeometryQueryResult<SurfaceHit> hit = QueryFirstHit(
                    coordinateSystem,
                    workingUv,
                    surfaceFace,
                    cancellationToken);
                if (!hit.HasValue)
                    return GeometryQueryResult<InsertionDepthAdjustedPath>.Unresolved(hit.Issues);
                surfacePoints.Add(hit.Value!.ModelPoint);
            }

            double offsetMm = requiredInsertionDepthMm.Value + wireDiameterMm.Value / 2.0;
            Vec3[] centerlinePoints = surfacePoints
                .Select(point => point - coordinateSystem.Projection.Normal * offsetMm)
                .ToArray();
            var adjustedCurve = new PolylineCurveDefinition(centerlinePoints);
            return GeometryQueryResult<InsertionDepthAdjustedPath>.Succeeded(new InsertionDepthAdjustedPath(
                adjustedCurve,
                new LengthMm(0.0001),
                new LengthMm(0.0002),
                centerlinePoints.Length + 1,
                "application-native-depth-fake-v1"));
        }

        public GeometryQueryResult<DepthMeasurement> MeasureDepth(CurveGeometrySnapshot curve, LengthMm wireDiameterMm, SurfaceAnchor anchor, WorkingCoordinateSystem coordinateSystem, LengthMm requiredInsertionDepthMm, CancellationToken cancellationToken = default) =>
            GeometryQueryResult<DepthMeasurement>.Unresolved([]);

        public GeometryQueryResult<SurfaceElementSet> GetSurfaceElements(HeatingRegion region, GeometryToleranceProfile tolerances, CancellationToken cancellationToken = default) =>
            GeometryQueryResult<SurfaceElementSet>.Succeeded(new SurfaceElementSet([], new HeatingMold.Core.Primitives.AreaMm2(0)));

        public GeometryQueryResult<ConnectionObstacleCheck> CheckConnectionPathObstacles(ConnectionPath connectionPath, WorkingCoordinateSystem coordinateSystem, CancellationToken cancellationToken = default)
        {
            ConnectionPathObstacleQueryCount++;
            CheckedConnectionPaths.Add(connectionPath);
            return ConnectionPathObstacleResult;
        }
    }

    private sealed class FakeOperationContext : IOperationContext
    {
        public int VerifyCount { get; private set; }
        public void VerifyGeometryThread() => VerifyCount++;
    }

    private sealed class FakeApplicationEvents : IApplicationEvents
    {
        public List<ApplicationEvent> Published { get; } = [];
        public bool ThrowOnPublish { get; set; }

        public void Publish(ApplicationEvent applicationEvent)
        {
            if (ThrowOnPublish)
                throw new InvalidOperationException("fixture event failure");

            Published.Add(applicationEvent);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"HeatingMold-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
