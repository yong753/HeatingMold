using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using HeatingMold.Application.Sessions;
using HeatingMold.Application.Transactions;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Validation;

namespace HeatingMold.App;

public partial class MainWindow
{
    /// <summary>작업 평면 경계 구간을 화면 선택 항목으로 표시합니다.</summary>
    private sealed record ExitSideSegmentChoice(BoundarySegmentId SegmentId, string DisplayName);

    /// <summary>현재 선택 topology의 각 경계 구간을 인출 측면 선택 목록에 표시합니다.</summary>
    private void RefreshExitSideChoices()
    {
        if (ExitSideSegmentsList is null)
            return;

        ExitSideSegmentsList.ItemsSource = null;
        if (RegionSelection.TopologyResult?.HasValue != true ||
            RegionSelection.TopologyResult.Value is not { } topology)
            return;

        var choices = new List<ExitSideSegmentChoice>();
        for (int componentIndex = 0; componentIndex < topology.Components.Count; componentIndex++)
        {
            IReadOnlyList<BoundaryLoop> loops = topology.Components[componentIndex].BoundaryLoops;
            for (int loopIndex = 0; loopIndex < loops.Count; loopIndex++)
            {
                BoundaryLoop loop = loops[loopIndex];
                string loopKind = loop.IsHole ? "홀" : "외곽";
                for (int segmentIndex = 0; segmentIndex < loop.Segments.Count; segmentIndex++)
                {
                    BoundarySegment segment = loop.Segments[segmentIndex];
                    string label = string.Format(
                        "성분 {0} · {1} {2} · 구간 {3} ({4:F1}, {5:F1}) → ({6:F1}, {7:F1})",
                        componentIndex + 1,
                        loopKind,
                        loopIndex + 1,
                        segmentIndex + 1,
                        segment.Start.X,
                        segment.Start.Y,
                        segment.End.X,
                        segment.End.Y);
                    choices.Add(new ExitSideSegmentChoice(segment.Id, label));
                }
            }
        }

        ExitSideSegmentsList.ItemsSource = choices;
    }

    /// <summary>같은 경계 루프에서 이어진 구간만 인출 측면으로 묶습니다.</summary>
    internal static OperationResult<ExitSideReference> BuildExitSideReference(
        IReadOnlyList<BoundaryLoop> boundaryLoops,
        IEnumerable<BoundarySegmentId> selectedSegmentIds)
    {
        ArgumentNullException.ThrowIfNull(boundaryLoops);
        ArgumentNullException.ThrowIfNull(selectedSegmentIds);

        BoundarySegmentId[] selectedIds = selectedSegmentIds.ToArray();
        if (selectedIds.Length == 0 || selectedIds.Any(id => id.IsEmpty) ||
            selectedIds.Distinct().Count() != selectedIds.Length)
        {
            return ExitSideFailure("인출 측면에는 중복 없이 하나 이상의 경계 구간을 선택해야 합니다.");
        }

        BoundaryLoop[] matchingLoops = boundaryLoops
            .Where(loop => selectedIds.All(id => loop.Segments.Any(segment => segment.Id == id)))
            .ToArray();
        if (matchingLoops.Length != 1)
            return ExitSideFailure("선택한 구간은 하나의 같은 경계 루프에 속해야 합니다.");

        BoundaryLoop selectedLoop = matchingLoops[0];
        var selectedIndices = selectedIds
            .Select(id => selectedLoop.Segments.ToList().FindIndex(segment => segment.Id == id))
            .ToHashSet();
        if (selectedIndices.Count == selectedLoop.Segments.Count)
            return ExitSideFailure("닫힌 경계 전체를 인출 측면으로 선택할 수 없습니다.");

        int selectedToUnselectedTransitions = Enumerable.Range(0, selectedLoop.Segments.Count)
            .Count(index =>
                selectedIndices.Contains(index) &&
                !selectedIndices.Contains((index + 1) % selectedLoop.Segments.Count));
        if (selectedToUnselectedTransitions != 1)
            return ExitSideFailure("인출 측면 구간은 경계를 따라 하나로 이어져야 합니다.");

        int firstIndex = Enumerable.Range(0, selectedLoop.Segments.Count)
            .Single(index =>
                selectedIndices.Contains(index) &&
                !selectedIndices.Contains((index - 1 + selectedLoop.Segments.Count) % selectedLoop.Segments.Count));
        var ranges = new List<BoundarySegmentRange>(selectedIndices.Count);
        int currentIndex = firstIndex;
        do
        {
            ranges.Add(new BoundarySegmentRange(selectedLoop.Segments[currentIndex].Id, 0, 1));
            currentIndex = (currentIndex + 1) % selectedLoop.Segments.Count;
        }
        while (selectedIndices.Contains(currentIndex));

        return OperationResult<ExitSideReference>.Success(new ExitSideReference(ranges));
    }

    /// <summary>선택한 Face 경계와 인출 측면을 발열 영역 확정 유스케이스에 전달합니다.</summary>
    private void CommitSelectedRegion_Click(object sender, RoutedEventArgs e)
    {
        if (!WorkingGenerationReady())
            return;

        SessionSnapshot snapshot = _projectSession.Current;
        if (snapshot.CurrentProject is not { } project || snapshot.Geometry is not { } geometry)
        {
            StatusText.Text = "발열 영역을 확정할 프로젝트와 기하 snapshot이 없습니다.";
            return;
        }
        if (RegionSelection.TopologyResult?.HasValue != true ||
            RegionSelection.TopologyResult.Value is not { } topology)
        {
            StatusText.Text = "선택 Face에서 확정 가능한 경계 topology를 찾지 못했습니다.";
            return;
        }
        if (RegionSelection.Count == 0 ||
            RegionSelection.SelectedFaceReferences.Any(faceReferenceId =>
                !_faceReferenceIndex.TryGetNativeIndex(faceReferenceId, out _)))
        {
            StatusText.Text = "선택 Face를 영속 Face 참조로 연결하지 못했습니다.";
            return;
        }

        BoundarySegmentId[] selectedSegmentIds = ExitSideSegmentsList.SelectedItems
            .OfType<ExitSideSegmentChoice>()
            .Select(choice => choice.SegmentId)
            .ToArray();
        OperationResult<ExitSideReference> exitSideResult = BuildExitSideReference(
            topology.Components.SelectMany(component => component.BoundaryLoops).ToArray(),
            selectedSegmentIds);
        if (!exitSideResult.IsSuccess)
        {
            StatusText.Text = FormatIssues(exitSideResult.Issues);
            return;
        }

        RegionId regionId = RegionId.New();
        CandidateOutcome outcome = _createHeatingRegion.ExecuteFromTopology(
            regionId,
            RegionSelection.SelectedFaceReferences
                .OrderBy(faceReferenceId => _faceReferenceIndex.NativeIndicesByReference[faceReferenceId])
                .ToArray(),
            exitSideResult.GetValueOrThrow(),
            project.Revision,
            geometry.ModelRevision,
            RegionSelection.TopologyResult);
        if (!outcome.IsCommitted)
        {
            StatusText.Text = $"발열 영역 확정 실패: {FormatIssues(outcome.Issues)}";
            return;
        }

        _mainWindowViewModel.PathRegionSelection.SelectRegion(regionId);
        RefreshHeatingRegionChoices(outcome.Snapshot.CurrentProject);

        StatusText.Text = outcome.Status == CandidateStatus.PostCommitFailure
            ? $"발열 영역은 확정됐지만 화면 갱신에 실패했습니다: {FormatIssues(outcome.Issues)}"
            : $"발열 영역 확정 완료 · revision {project.Revision.Next().Value}";
    }

    /// <summary>인출 측면 선택 오류를 화면과 테스트에서 같은 검증 결과로 전달합니다.</summary>
    private static OperationResult<ExitSideReference> ExitSideFailure(string evidence) =>
        OperationResult<ExitSideReference>.Failure(
        [new ValidationIssue(
            "InvalidExitSideSelection",
            ValidationSeverity.Error,
            "App.InvalidExitSideSelection",
            [],
            "app-exit-side-selection-v1",
            evidence: [evidence])]);
}
