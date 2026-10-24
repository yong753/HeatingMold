using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using HeatingMold.App.Geometry.Occt;
using HeatingMold.App.ViewModels;
using HeatingMold.Application.Sessions;
using HeatingMold.Application.Transactions;
using HeatingMold.Application.UseCases;
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
using HeatingMold.Design.Editing;
using HeatingMold.Design.HeatingPath;
using HeatingMold.Infrastructure.Geometry.Occt;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

public partial class MainWindow
{
    /// <summary>확정 프로젝트의 발열 영역을 경로 생성 선택 목록에 표시합니다.</summary>
    private void RefreshHeatingRegionChoices(DesignProject project)
    {
        HeatingPathRegionSelectionViewModel regionSelection = _mainWindowViewModel.PathRegionSelection;
        RegionId? selectedRegionId = regionSelection.GetPreferredSelection(GetSelectedHeatingRegion()?.Id);
        HeatingRegionChoice[] choices = project?.HeatingRegions
            .Select((region, index) => new HeatingRegionChoice(
                region,
                string.Format(
                    CultureInfo.CurrentCulture,
                    "영역 {0} · {1}",
                    index + 1,
                    region.Id.Value.ToString("N").Substring(0, 8))))
            .ToArray() ?? Array.Empty<HeatingRegionChoice>();

        regionSelection.BeginRefreshingChoices();
        try
        {
            HeatingRegionCombo.ItemsSource = choices;
            HeatingRegionChoice selectedChoice = selectedRegionId.HasValue
                ? choices.FirstOrDefault(choice => choice.Region.Id == selectedRegionId.Value)
                : null;
            HeatingRegionCombo.SelectedItem = selectedChoice;
            regionSelection.SelectRegion(selectedChoice?.Region.Id);
        }
        finally
        {
            regionSelection.EndRefreshingChoices();
        }

        ClearPathAnchors();
        RedrawCommittedExclusionOverlays(project);
        DrawRegionCanvas();
    }

    /// <summary>영역 선택이 바뀌면 이전 영역에서 지정한 표면 앵커를 비웁니다.</summary>
    private void HeatingRegion_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        HeatingPathRegionSelectionViewModel regionSelection = _mainWindowViewModel.PathRegionSelection;
        if (regionSelection.IsRefreshingChoices)
            return;

        RegionId? selectedRegionId = GetSelectedHeatingRegion()?.Id;
        if (regionSelection.SelectRegion(selectedRegionId))
            ClearPathAnchors();
        RedrawCommittedExclusionOverlays(_projectSession.Current.CurrentProject);
        DrawRegionCanvas();
    }

    /// <summary>발열 경로 시작 앵커를 지정하도록 화면 선택 모드를 켭니다.</summary>
    private void PickHeatingStartAnchor_Click(object sender, RoutedEventArgs e) =>
        BeginSurfaceAnchorPick(SurfaceAnchorTarget.HeatingStart);

    /// <summary>발열 경로 끝 앵커를 지정하도록 화면 선택 모드를 켭니다.</summary>
    private void PickHeatingEndAnchor_Click(object sender, RoutedEventArgs e) =>
        BeginSurfaceAnchorPick(SurfaceAnchorTarget.HeatingEnd);

    /// <summary>시작부 금형 인출 앵커를 지정하도록 화면 선택 모드를 켭니다.</summary>
    private void PickStartMoldExitAnchor_Click(object sender, RoutedEventArgs e) =>
        BeginSurfaceAnchorPick(SurfaceAnchorTarget.StartMoldExit);

    /// <summary>끝부 금형 인출 앵커를 지정하도록 화면 선택 모드를 켭니다.</summary>
    private void PickEndMoldExitAnchor_Click(object sender, RoutedEventArgs e) =>
        BeginSurfaceAnchorPick(SurfaceAnchorTarget.EndMoldExit);

    /// <summary>진행 중인 표면 앵커 지정을 취소합니다.</summary>
    private void CancelSurfaceAnchorPick_Click(object sender, RoutedEventArgs e)
    {
        ClearPendingSurfaceAnchorPick();
        if (CircuitSplitModeBox.IsChecked == true)
            Viewport.CircuitSplitMode = true;
        StatusText.Text = "표면 앵커 지정을 취소했습니다.";
    }

    /// <summary>표면 앵커 대상을 비우고 앵커 선택 입력 모드를 종료합니다.</summary>
    private void ClearPendingSurfaceAnchorPick()
    {
        _mainWindowViewModel.PathAnchors.CancelPicking();
        CircuitTopologyEditor.PendingSurfaceAnchorEndpointKey = null;
        if (Viewport is not null)
            Viewport.SurfaceAnchorSelectionMode = false;
    }

    /// <summary>선택한 영역과 좌표계가 현재 상태인지 확인한 뒤 클릭을 기다립니다.</summary>
    private void BeginSurfaceAnchorPick(SurfaceAnchorTarget target)
    {
        if (!WorkingGenerationReady())
            return;

        CircuitTopologyEditor.PendingSurfaceAnchorEndpointKey = target == SurfaceAnchorTarget.CircuitEndpoint
            ? GetSelectedCircuitTopologyEndpoint()?.Key
            : null;
        HeatingRegion region = GetSurfaceAnchorRegion(
            target,
            _projectSession.Current.CurrentProject,
            CircuitTopologyEditor.PendingSurfaceAnchorEndpointKey);
        if (region is null)
        {
            CircuitTopologyEditor.PendingSurfaceAnchorEndpointKey = null;
            StatusText.Text = target switch
            {
                SurfaceAnchorTarget.ControlPointTarget => "이동할 제어점을 먼저 선택하세요.",
                SurfaceAnchorTarget.CircuitEndpoint => "표면 위치를 입력할 신규 회로 끝점을 선택하세요.",
                _ => "먼저 경로를 생성할 발열 영역을 선택하세요."
            };
            return;
        }

        WorkingCoordinateSystem coordinateSystem = WorkingCoordinates.Session.Confirmed;
        if (coordinateSystem is null ||
            !Bridge.ReturnToWorkingFront(coordinateSystem.Id.Value, coordinateSystem.Revision.Value))
        {
            CircuitTopologyEditor.PendingSurfaceAnchorEndpointKey = null;
            StatusText.Text = "표면 앵커를 지정할 작업 화면을 준비하지 못했습니다.";
            return;
        }

        _mainWindowViewModel.PathAnchors.BeginPicking(target);
        IncludeModeBox.IsChecked = false;
        ExcludeModeBox.IsChecked = false;
        FaceDragSelectModeBox.IsChecked = false;
        if (target != SurfaceAnchorTarget.CircuitEndpoint)
            CircuitSplitModeBox.IsChecked = false;
        Viewport.CircuitHoverMode = false;
        Viewport.SetInputMode(ViewerInputMode.SurfaceAnchorPicking);
        StatusText.Text = string.Concat(GetAnchorLabel(target), " 위치의 금형 표면을 클릭하세요.");
    }

    /// <summary>작업 화면 클릭을 전체 모델 first-hit 질의로 확인해 선택한 앵커에 저장합니다.</summary>
    private void OnSurfaceAnchorPicked(int x, int y)
    {
        if (!_mainWindowViewModel.PathAnchors.TryCompletePicking(out SurfaceAnchorTarget target))
            return;

        CircuitTopologyEndpointKey? endpointKey = CircuitTopologyEditor.PendingSurfaceAnchorEndpointKey;
        CircuitTopologyEditor.PendingSurfaceAnchorEndpointKey = null;
        Viewport.SurfaceAnchorSelectionMode = false;
        try
        {
            CompleteSurfaceAnchorPick(target, endpointKey, x, y);
        }
        finally
        {
            if (target == SurfaceAnchorTarget.CircuitEndpoint && CircuitSplitModeBox.IsChecked == true)
                Viewport.CircuitSplitMode = true;
        }
    }

    /// <summary>뷰포트 선택을 검증해 표면 앵커 입력 대상에 보관합니다.</summary>
    private void CompleteSurfaceAnchorPick(
        SurfaceAnchorTarget target,
        CircuitTopologyEndpointKey? endpointKey,
        int x,
        int y)
    {
        if (!WorkingGenerationReady())
            return;

        WorkingCoordinateSystem coordinateSystem = WorkingCoordinates.Session.Confirmed;
        SessionSnapshot snapshot = _projectSession.Current;
        HeatingRegion region = GetSurfaceAnchorRegion(target, snapshot.CurrentProject, endpointKey);
        if (region is null || coordinateSystem is null || snapshot.Geometry is not { } geometry ||
            coordinateSystem.Revision != geometry.ModelRevision)
        {
            StatusText.Text = "영역 또는 작업 좌표계가 바뀌었습니다. 앵커를 다시 지정하세요.";
            return;
        }

        ProjectionFrame projection = coordinateSystem.Projection;
        double[] projectedPoint = Bridge.ScreenPointOnPlane(
            x,
            y,
            projection.Origin.X,
            projection.Origin.Y,
            projection.Origin.Z,
            projection.Normal.X,
            projection.Normal.Y,
            projection.Normal.Z);
        if (projectedPoint is null ||
            projectedPoint.Length < 3 ||
            projectedPoint.Take(3).Any(value => !double.IsFinite(value)))
        {
            StatusText.Text = "화면 위치를 작업 평면에 투영하지 못했습니다.";
            return;
        }

        Vec3 modelPointOnPlane = new(projectedPoint[0], projectedPoint[1], projectedPoint[2]);
        Vec2 workingUv = projection.Project(modelPointOnPlane);
        IReadOnlyDictionary<FaceRefId, int> nativeFaceIndices = _faceReferenceIndex.NativeIndicesByReference;
        if (region.FaceReferences.Any(faceReferenceId => !nativeFaceIndices.ContainsKey(faceReferenceId)))
        {
            StatusText.Text = "선택 영역의 Face 참조를 현재 모델의 면 번호에 연결하지 못했습니다.";
            return;
        }

        try
        {
            OcctGeometryQueries geometryQueries = AppCompositionRoot.CreateOcctGeometryQueries(
                coordinateSystem,
                nativeFaceIndices);
            GeometryQueryResult<SurfaceHit> query = geometryQueries.QueryFirstHit(
                coordinateSystem,
                workingUv,
                region.FaceReferences);
            if (!query.HasValue || query.Value is not { } hit)
            {
                string diagnostic = query.Issues.Count > 0
                    ? FormatIssues(query.Issues)
                    : "선택한 발열 영역의 표면이 아닙니다.";
                StatusText.Text = string.Format(
                    CultureInfo.CurrentCulture,
                    "{0} 표면을 확인하지 못했습니다: {1}",
                    GetAnchorLabel(target),
                    diagnostic);
                return;
            }

            var anchor = new SurfaceAnchor(
                hit.FaceReferenceId,
                hit.FaceUv,
                workingUv,
                hit.ModelPoint);
            SetSurfaceAnchor(target, anchor, endpointKey);
            StatusText.Text = string.Concat(GetAnchorLabel(target), " 앵커를 저장했습니다.");
        }
        catch (Exception exception)
        {
            StatusText.Text = string.Concat("표면 앵커 질의에 실패했습니다: ", exception.Message);
        }
    }

    /// <summary>영역, 네 표면 앵커와 필수 입력을 모아 Application 경로 생성 유스케이스에 전달합니다.</summary>
    private void GenerateSessionHeatingPath()
    {
        if (!WorkingGenerationReady())
            return;

        Viewport.ClearQualityIssueLocation();

        SessionSnapshot snapshot = _projectSession.Current;
        if (snapshot.CurrentProject is not { } project || snapshot.Geometry is not { } geometry)
        {
            StatusText.Text = "확정 프로젝트와 기하 snapshot이 있어야 경로를 생성할 수 있습니다.";
            return;
        }

        HeatingRegion region = GetSelectedHeatingRegion();
        if (region is null || region.Id != _mainWindowViewModel.PathRegionSelection.SelectedRegionId)
        {
            StatusText.Text = "경로를 생성할 확정 발열 영역을 선택하세요.";
            return;
        }
        HeatingPathAnchorViewModel anchors = _mainWindowViewModel.PathAnchors;
        if (anchors.HeatingStartAnchor is not { } heatingStartAnchor ||
            anchors.HeatingEndAnchor is not { } heatingEndAnchor ||
            anchors.StartMoldExitAnchor is not { } startMoldExitAnchor ||
            anchors.EndMoldExitAnchor is not { } endMoldExitAnchor)
        {
            StatusText.Text = "발열 경로 시작·끝과 시작부·끝부 금형 인출 앵커를 모두 지정하세요.";
            return;
        }

        if (!_mainWindowViewModel.PlacementParameters.TryCreateParameters(
                out HeatingPathPlacementParameters placement) ||
            placement is null)
        {
            StatusText.Text = "최소 간격 g, 선경 d, 경계 여유와 삽입 깊이를 유효한 mm 값으로 입력하세요.";
            return;
        }

        double boundaryMarginMm = placement.BoundaryMarginMm;
        double minimumClearGapMm = placement.MinimumClearGapMm;
        double insertionDepthMm = placement.InsertionDepthMm;

        WireSpecificationInputsViewModel wireSpecificationInputs = _mainWindowViewModel.WireSpecification;
        if (!wireSpecificationInputs.TryCreateSpecification(
                placement.WireDiameterMm,
                out WireSpecification wireSpecification,
                out string wireSpecificationError))
        {
            StatusText.Text = wireSpecificationError;
            return;
        }

        if (!_mainWindowViewModel.HeatingPathCircuitInputs.TryCreateInputs(
                out HeatingPathCircuitInputs circuitInputs,
                out string circuitInputError) ||
            circuitInputs is null)
        {
            StatusText.Text = circuitInputError;
            return;
        }

        if (!TryReadEnum(PlacementDirectionBox.SelectedItem, out PlacementDirection direction) ||
            !TryReadEnum(ManualEditPolicyBox.SelectedItem, out ManualEditPolicy manualEditPolicy))
        {
            StatusText.Text = "배치 방향과 기존 편집 경로 처리 방식을 선택하세요.";
            return;
        }

        WorkingCoordinateSystem coordinateSystem = WorkingCoordinates.Session.Confirmed;
        if (coordinateSystem.Revision != geometry.ModelRevision)
        {
            StatusText.Text = "작업 좌표계가 현재 기하 snapshot과 다릅니다. 다시 확정하세요.";
            return;
        }

        try
        {
            var placementConstraints = new PlacementConstraints(
                new LengthMm(minimumClearGapMm),
                new LengthMm(boundaryMarginMm),
                new LengthMm(insertionDepthMm),
                direction,
                heatingStartAnchor,
                heatingEndAnchor,
                region.ExitSide);
            var request = new HeatingPathGenerationRequest(
                project.Id,
                project.Revision,
                region,
                wireSpecification,
                placementConstraints,
                coordinateSystem,
                geometry.ModelRevision,
                manualEditPolicy);

            var startEndpoint = new CircuitEndpointDefinition(
                ConnectionPointId.New(),
                startMoldExitAnchor,
                circuitInputs.StartExternalPoint);
            var endEndpoint = new CircuitEndpointDefinition(
                ConnectionPointId.New(),
                endMoldExitAnchor,
                circuitInputs.EndExternalPoint);
            var endpoints = new CircuitEndpointPair(startEndpoint, endEndpoint);
            var circuitSetup = new HeatingPathCircuitSetup(
                CircuitId.New(),
                endpoints,
                circuitInputs.Supply,
                new Dictionary<ConnectionPointId, IReadOnlyList<ConnectionPathLegRequest>>
                {
                    [startEndpoint.Id] = CreateConnectionLegs(
                        startMoldExitAnchor,
                        circuitInputs.StartExternalPoint,
                        circuitInputs.MoldExitAllowance,
                        circuitInputs.ExternalConnectionAllowance),
                    [endEndpoint.Id] = CreateConnectionLegs(
                        endMoldExitAnchor,
                        circuitInputs.EndExternalPoint,
                        circuitInputs.MoldExitAllowance,
                        circuitInputs.ExternalConnectionAllowance)
                });

            OperationResult<HeatingPathRegenerationAssessment> assessmentResult =
                _assessHeatingPathRegeneration.Execute(project, request);
            if (!assessmentResult.IsSuccess)
            {
                StatusText.Text = string.Concat(
                    "경로 재생성 검토 실패: ",
                    FormatIssues(assessmentResult.Issues));
                return;
            }

            HeatingPathRegenerationAssessment assessment = assessmentResult.GetValueOrThrow();
            HeatingPathRegenerationPrompt prompt = HeatingPathRegenerationPresentation.Create(
                assessment,
                manualEditPolicy);
            if (!prompt.CanContinue)
            {
                StatusText.Text = prompt.Message;
                MessageBox.Show(
                    this,
                    prompt.Message,
                    "경로 재생성 검토",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }
            if (prompt.RequiresConfirmation && !_confirmHeatingPathRegeneration(prompt.Message))
            {
                StatusText.Text = "경로 재생성을 취소했습니다.";
                return;
            }

            IReadOnlyDictionary<FaceRefId, int> nativeFaceIndices = _faceReferenceIndex.NativeIndicesByReference;
            if (region.FaceReferences.Any(faceReferenceId => !nativeFaceIndices.ContainsKey(faceReferenceId)))
            {
                StatusText.Text = "선택 영역의 Face 참조를 현재 모델에 연결하지 못했습니다.";
                return;
            }

            OcctGeometryQueries geometryQueries = AppCompositionRoot.CreateOcctGeometryQueries(
                coordinateSystem,
                nativeFaceIndices);
            CandidateOutcome outcome = _generateHeatingPath.Execute(
                request,
                geometryQueries,
                circuitSetup);
            if (outcome.IsCommitted)
            {
                StatusText.Text = outcome.Status == CandidateStatus.PostCommitFailure
                    ? string.Concat("경로와 회로는 확정됐지만 화면 동기화가 필요합니다: ", FormatIssues(outcome.Issues))
                    : "발열 경로와 시작·끝 연결 경로를 프로젝트에 확정했습니다.";
                return;
            }

            string failureMessage = string.Concat("경로 생성 실패: ", FormatIssues(outcome.Issues));
            StatusText.Text = TryHighlightClearanceIssueLocation(outcome.Issues)
                ? string.Concat(failureMessage, " 간격 문제 위치를 뷰포트에 표시했습니다.")
                : failureMessage;
        }
        catch (ArgumentException exception)
        {
            StatusText.Text = string.Concat("경로 생성 입력이 유효하지 않습니다: ", exception.Message);
        }
    }

    /// <summary>실패한 간격 검증의 최단점만 현재 뷰포트에서 강조합니다.</summary>
    private bool TryHighlightClearanceIssueLocation(IReadOnlyList<ValidationIssue> issues)
    {
        Viewport.ClearQualityIssueLocation();
        return ClearanceIssueLocationPresentation.TryGetLocation(issues, out Vec3 location) &&
            Viewport.ShowQualityIssueLocation(location);
    }

    /// <summary>표면 앵커와 외부 좌표 사이의 금형 위 연장 및 외부 연결 구간을 구성합니다.</summary>
    private static IReadOnlyList<ConnectionPathLegRequest> CreateConnectionLegs(
        SurfaceAnchor moldExitAnchor,
        Vec3 externalPoint,
        LengthMm moldExitAllowance,
        LengthMm externalAllowance) =>
        new ConnectionPathLegRequest[]
        {
            new(
                moldExitAnchor.ModelPoint,
                GeometryDomain.OnMold,
                ConductorRole.HeatingWireExtension,
                moldExitAllowance),
            new(
                externalPoint,
                GeometryDomain.External,
                ConductorRole.SeparateConnectionWire,
                externalAllowance,
                isMoldExitTransition: true)
        };

    /// <summary>선택 영역에서 지정한 앵커 상태만 초기화합니다.</summary>
    private void ClearPathAnchors()
    {
        if (Viewport != null)
            Viewport.SurfaceAnchorSelectionMode = false;
        _mainWindowViewModel.PathAnchors.Clear();
    }

    /// <summary>선택 대상에 표면 앵커와 화면 상태를 함께 기록합니다.</summary>
    private void SetSurfaceAnchor(
        SurfaceAnchorTarget target,
        SurfaceAnchor anchor,
        CircuitTopologyEndpointKey? endpointKey = null)
    {
        string faceLabel = anchor.FaceReferenceId.Value.ToString("N").Substring(0, 8);
        string status = string.Format(
            CultureInfo.CurrentCulture,
            "{0}: Face {1}, U {2:F2}, V {3:F2}",
            GetAnchorLabel(target),
            faceLabel,
            anchor.WorkingUv.X,
            anchor.WorkingUv.Y);

        switch (target)
        {
            case SurfaceAnchorTarget.HeatingStart:
                _mainWindowViewModel.PathAnchors.SetHeatingStartAnchor(anchor);
                break;
            case SurfaceAnchorTarget.HeatingEnd:
                _mainWindowViewModel.PathAnchors.SetHeatingEndAnchor(anchor);
                break;
            case SurfaceAnchorTarget.StartMoldExit:
                _mainWindowViewModel.PathAnchors.SetStartMoldExitAnchor(anchor);
                break;
            case SurfaceAnchorTarget.EndMoldExit:
                _mainWindowViewModel.PathAnchors.SetEndMoldExitAnchor(anchor);
                break;
            case SurfaceAnchorTarget.ControlPointTarget:
                StoreControlPointTargetAnchor(anchor, status);
                break;
            case SurfaceAnchorTarget.CircuitEndpoint:
                if (endpointKey is not { } key ||
                    !CircuitTopologyEditor.TryGetEndpointDraft(key, out CircuitTopologyEndpointDraft draft))
                    throw new InvalidOperationException("표면 위치를 저장할 회로 끝점 초안을 찾을 수 없습니다.");
                draft.SurfaceAnchor = anchor;
                RefreshCircuitEndpointStatus();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target));
        }
    }

    /// <summary>선택 대상에 대응하는 짧은 화면 안내를 반환합니다.</summary>
    private static string GetAnchorLabel(SurfaceAnchorTarget target) => target switch
    {
        SurfaceAnchorTarget.HeatingStart => "발열 경로 시작",
        SurfaceAnchorTarget.HeatingEnd => "발열 경로 끝",
        SurfaceAnchorTarget.StartMoldExit => "시작부 금형 인출",
        SurfaceAnchorTarget.EndMoldExit => "끝부 금형 인출",
        SurfaceAnchorTarget.ControlPointTarget => "제어점 이동",
        SurfaceAnchorTarget.CircuitEndpoint => "새 회로 연결부",
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };

    /// <summary>현재 콤보 상자에서 확정 발열 영역을 가져옵니다.</summary>
    private HeatingRegion GetSelectedHeatingRegion() =>
        (HeatingRegionCombo.SelectedItem as HeatingRegionChoice)?.Region;

    /// <summary>입력 문자열이 양의 유한 수인지 확인합니다.</summary>
    private static bool TryReadPositive(string text, out double value) =>
        TryReadFinite(text, out value) && value > 0;

    /// <summary>입력 문자열이 0 이상의 유한 수인지 확인합니다.</summary>
    private static bool TryReadNonNegative(string text, out double value) =>
        TryReadFinite(text, out value) && value >= 0;

    /// <summary>현재 문화권의 입력 문자열이 유한 수인지 확인합니다.</summary>
    private static bool TryReadFinite(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) &&
        double.IsFinite(value);

    /// <summary>콤보 상자 태그를 지정한 열거형 값으로 안전하게 읽습니다.</summary>
    private static bool TryReadEnum<TEnum>(object selectedItem, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        string tag = (selectedItem as ComboBoxItem)?.Tag?.ToString();
        return Enum.TryParse(tag, ignoreCase: false, out value) && Enum.IsDefined(value);
    }

    private sealed record HeatingRegionChoice(HeatingRegion Region, string DisplayName);

}
