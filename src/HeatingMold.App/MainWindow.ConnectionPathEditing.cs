using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using HeatingMold.App.ViewModels;
using HeatingMold.Application.Sessions;
using HeatingMold.Application.Transactions;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Routing;
using HeatingMold.Design.ConnectionPath;
using HeatingMold.Infrastructure.Geometry.Occt;

namespace HeatingMold.App;

public partial class MainWindow
{
    private ConnectionPathEditorViewModel ConnectionPathEditor => _mainWindowViewModel.ConnectionPaths;

    /// <summary>연결 경로 표와 재료·공간 선택 목록을 초기화합니다.</summary>
    private void InitializeConnectionPathEditor()
    {
        ConnectionPathLegsGrid.ItemsSource = ConnectionPathEditor.Legs;
        DataGridComboBoxColumn[] enumColumns = ConnectionPathLegsGrid.Columns
            .OfType<DataGridComboBoxColumn>()
            .ToArray();
        if (enumColumns.Length >= 2)
        {
            enumColumns[0].ItemsSource = Enum.GetValues(typeof(GeometryDomain));
            enumColumns[1].ItemsSource = Enum.GetValues(typeof(ConductorRole));
        }
    }

    /// <summary>확정 프로젝트의 회로 끝 연결 경로를 안정 ID와 함께 선택 목록에 표시합니다.</summary>
    private void RefreshConnectionPathChoices(DesignProject project)
    {
        ConnectionPathEditor.RefreshChoices(project);
        LoadSelectedConnectionPath();
    }

    /// <summary>현재 연결 경로 선택에 맞춰 편집 행과 지원 상태를 갱신합니다.</summary>
    private void ConnectionPathEndpoint_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ConnectionPathEditor.IsRefreshingChoices)
            LoadSelectedConnectionPath();
    }

    /// <summary>선택한 연결 경로의 측정 끝점과 저장된 구간 속성을 편집 표에 불러옵니다.</summary>
    private void LoadSelectedConnectionPath()
    {
        ConnectionPathEditor.ClearRows();
        ConnectionPathLegsGrid.IsEnabled = false;
        ConnectionPathEditorStatusText.Text = "수정할 연결 경로를 선택하세요.";

        ConnectionPathChoice choice = GetSelectedConnectionPathChoice();
        SessionSnapshot snapshot = _projectSession.Current;
        if (choice is null || snapshot.CurrentProject is not { } project || snapshot.Geometry is not { } geometry)
            return;
        if (choice.DesignRevision != project.Revision || geometry.DependencyStamp.DesignRevision != project.Revision)
        {
            ConnectionPathEditorStatusText.Text = "설계 revision이 바뀌었습니다. 경로를 다시 선택하세요.";
            return;
        }

        ConnectionPath path = project.ConnectionPaths.SingleOrDefault(candidate =>
            candidate.ConnectionPointId == choice.ConnectionPointId);
        if (path is null)
        {
            ConnectionPathEditorStatusText.Text = "선택한 연결부의 확정 경로를 찾지 못했습니다.";
            return;
        }

        try
        {
            ConnectionPathEditor.LoadRows(path, geometry);
            ConnectionPathLegsGrid.IsEnabled = true;
            ConnectionPathEditorStatusText.Text =
                "경로 구간을 편집한 뒤 적용하세요. 시작 표면점과 마지막 외부점은 확정 연결부에 맞아야 합니다.";
        }
        catch (InvalidOperationException exception)
        {
            ConnectionPathEditorStatusText.Text = exception.Message;
        }
    }

    /// <summary>금형 이탈 뒤 기존 종점 앞에 외부 경유점을 추가합니다.</summary>
    private void AddConnectionPathWaypoint_Click(object sender, RoutedEventArgs e)
    {
        CommitConnectionPathGridEdits();
        if (!ConnectionPathEditor.TryAddWaypoint())
        {
            ConnectionPathEditorStatusText.Text = "유효한 금형 이탈 뒤에 외부 경유점을 추가할 수 없습니다.";
            return;
        }

        ConnectionPathEditorStatusText.Text = "외부 경유점을 추가했습니다. 장애물 검사 후 적용됩니다.";
    }

    /// <summary>선택한 내부 경유점을 제거하고 필요한 금형 이탈 표식을 유지합니다.</summary>
    private void RemoveConnectionPathWaypoint_Click(object sender, RoutedEventArgs e)
    {
        CommitConnectionPathGridEdits();
        ConnectionPathWaypointRemovalResult removalResult =
            ConnectionPathEditor.RemoveWaypoint(ConnectionPathLegsGrid.SelectedItem as ConnectionPathLegRow);
        if (removalResult == ConnectionPathWaypointRemovalResult.NoSelection)
        {
            ConnectionPathEditorStatusText.Text = "제거할 내부 경유점을 표에서 선택하세요.";
            return;
        }
        if (removalResult == ConnectionPathWaypointRemovalResult.EndpointProtected)
        {
            ConnectionPathEditorStatusText.Text = "시작 표면점과 마지막 외부점은 제거할 수 없습니다.";
            return;
        }
        ConnectionPathEditorStatusText.Text = "선택한 내부 경유점을 제거했습니다.";
    }

    /// <summary>편집한 연결 구간을 Application에서 장애물 검사하고 후보로 확정합니다.</summary>
    private void ApplyConnectionPathEdit_Click(object sender, RoutedEventArgs e)
    {
        if (!WorkingGenerationReady())
            return;

        CommitConnectionPathGridEdits();
        ConnectionPathChoice choice = GetSelectedConnectionPathChoice();
        SessionSnapshot snapshot = _projectSession.Current;
        if (choice is null || snapshot.CurrentProject is not { } project || snapshot.Geometry is not { } geometry)
        {
            ConnectionPathEditorStatusText.Text = "확정 프로젝트와 기하 snapshot에서 편집할 연결부를 선택하세요.";
            return;
        }
        if (choice.DesignRevision != project.Revision || geometry.DependencyStamp.DesignRevision != project.Revision)
        {
            ConnectionPathEditorStatusText.Text = "편집 중 설계가 바뀌었습니다. 최신 연결 경로를 다시 불러오세요.";
            return;
        }

        if (!TryCreateConnectionPathLegRequests(out ConnectionPathLegRequest[] legs))
            return;

        ConnectionPoint connectionPoint = project.ConnectionPoints.SingleOrDefault(point =>
            point.Id == choice.ConnectionPointId);
        Circuit circuit = project.Circuits.SingleOrDefault(candidate =>
            connectionPoint is not null && candidate.Id == connectionPoint.CircuitId);
        HeatingPath endpointPath = project.HeatingPaths.SingleOrDefault(path =>
            connectionPoint is not null && path.Segments.Any(segment => segment.Id == connectionPoint.HeatingPathSegmentId));
        HeatingPathSegment endpointSegment = endpointPath?.Segments.SingleOrDefault(segment =>
            segment.Id == connectionPoint.HeatingPathSegmentId);
        CurveGeometrySnapshot measuredEndpoint = geometry.Curves.SingleOrDefault(curve =>
            curve.HeatingPathSegmentId == connectionPoint?.HeatingPathSegmentId);
        if (connectionPoint is null || circuit is null || endpointSegment is null ||
            measuredEndpoint is null || !measuredEndpoint.Approximation.IsComplete)
        {
            ConnectionPathEditorStatusText.Text = "연결부의 회로 또는 완전한 발열 곡선 측정값을 찾지 못했습니다.";
            return;
        }

        Vec3 heatingEndpoint;
        bool curveRunsForward = endpointSegment.Curve.Direction == CurveDirection.Forward;
        if (connectionPoint.ControlPointId == endpointSegment.StartControlPointId)
            heatingEndpoint = curveRunsForward ? measuredEndpoint.Start : measuredEndpoint.End;
        else if (connectionPoint.ControlPointId == endpointSegment.EndControlPointId)
            heatingEndpoint = curveRunsForward ? measuredEndpoint.End : measuredEndpoint.Start;
        else
        {
            ConnectionPathEditorStatusText.Text = "연결부 절점이 발열 곡선의 실제 끝점과 일치하지 않습니다.";
            return;
        }

        WorkingCoordinateSystem coordinateSystem = WorkingCoordinates.Session.Confirmed;
        if (coordinateSystem is null || coordinateSystem.Revision != geometry.ModelRevision)
        {
            ConnectionPathEditorStatusText.Text = "작업 좌표계와 측정 기하 revision이 다릅니다.";
            return;
        }

        IReadOnlyDictionary<FaceRefId, int> nativeFaceIndices = _faceReferenceIndex.NativeIndicesByReference;
        try
        {
            var request = new ConnectionPathPlanningRequest(
                circuit,
                connectionPoint,
                endpointSegment,
                heatingEndpoint,
                legs,
                coordinateSystem,
                geometry.ModelRevision);
            IGeometryQueries geometryQueries = AppCompositionRoot.CreateOcctGeometryQueries(
                coordinateSystem,
                nativeFaceIndices);
            CandidateOutcome outcome = _updateConnectionPath.Execute(
                request,
                geometryQueries,
                project.Rules.GeometryTolerances.ConnectionToleranceMm,
                project.Revision);
            ConnectionPathEditorStatusText.Text = outcome.IsCommitted
                ? outcome.Status == CandidateStatus.PostCommitFailure
                    ? string.Concat("경로는 확정됐지만 화면 복구가 필요합니다: ", FormatIssues(outcome.Issues))
                    : "연결 경로를 장애물 검사 후 확정했습니다."
                : string.Concat("연결 경로를 적용하지 않았습니다: ", FormatIssues(outcome.Issues));
        }
        catch (ArgumentException exception)
        {
            ConnectionPathEditorStatusText.Text = string.Concat("연결 경로 입력이 유효하지 않습니다: ", exception.Message);
        }
    }

    /// <summary>현재 선택한 표 행의 편집 중인 셀과 행을 값으로 확정합니다.</summary>
    private void CommitConnectionPathGridEdits()
    {
        ConnectionPathLegsGrid.CommitEdit(DataGridEditingUnit.Cell, exitEditingMode: true);
        ConnectionPathLegsGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
    }

    /// <summary>표의 모든 좌표·역할·추가 여유를 계획 입력으로 검증해 복사합니다.</summary>
    private bool TryCreateConnectionPathLegRequests(out ConnectionPathLegRequest[] legs)
    {
        if (ConnectionPathEditor.TryCreateRequests(out legs, out int invalidRowNumber))
            return true;

        ConnectionPathEditorStatusText.Text = string.Format(
            CultureInfo.CurrentCulture,
            "{0}번째 구간의 좌표·역할·추가 여유를 확인하세요.",
            invalidRowNumber);
        return false;
    }

    /// <summary>선택한 연결 경로를 안정 ID 기준으로 가져옵니다.</summary>
    private ConnectionPathChoice GetSelectedConnectionPathChoice() =>
        ConnectionPathEditor.SelectedChoice;
}
