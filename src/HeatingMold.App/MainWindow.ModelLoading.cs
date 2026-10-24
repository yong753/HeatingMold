using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using HeatingMold.Application.Ports.Geometry;
using HeatingMold.Application.Ports.Runtime;
using HeatingMold.Application.Sessions;
using HeatingMold.Application.Transactions;
using HeatingMold.App.ViewModels;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.App.Geometry.Occt;
using HeatingMold.Core.Validation;
using HeatingMold.Infrastructure.Geometry.Occt;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

public partial class MainWindow
{
    /// <summary>콤보 상자에 표시할 Native Face 번호와 설명입니다.</summary>
    private sealed class FaceItem
    {
        /// <summary>현재 Native 문서에서만 유효한 0 기반 Face 번호입니다.</summary>
        public int Index;

        /// <summary>현재 프로젝트에서 해당 면을 가리키는 안정 참조 ID입니다.</summary>
        public FaceRefId ReferenceId;

        /// <summary>사용자가 Face 종류와 크기를 구별하도록 표시하는 이름입니다.</summary>
        public string Label;

        /// <summary>콤보 상자에 표시할 이름을 반환합니다.</summary>
        public override string ToString() => Label;
    }

    /// <summary>투영된 면 크기와 목록 정렬값을 함께 보관합니다.</summary>
    private sealed record FaceCandidate(int NativeIndex, string Label, double ProjectedArea);

    /// <summary>Viewer를 초기화하고 개발 기본 모델이 있으면 엽니다.</summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!Viewport.InitViewer())
        {
            StatusText.Text = "뷰어 초기화 실패";
            return;
        }

        string modelPath = OcctEnv.FindMoldStp();
        if (modelPath is null)
        {
            StatusText.Text = "뷰어 준비 — '열기'로 STEP 파일 선택";
            return;
        }

        LoadFile(modelPath);
    }

    /// <summary>STEP 모델을 후보로 열고 확정 성공 뒤에만 기존 선택 상태를 지웁니다.</summary>
    private void LoadFile(string path)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        SessionPresentation.RepairMessage = null;

        OperationResult<ModelSource> sourceResult = _modelSourceStore.Capture(path);
        if (!sourceResult.IsSuccess)
        {
            string diagnostic = FormatIssues(sourceResult.Issues);
            StatusText.Text = $"로드 실패: {diagnostic} · {path}";
            return;
        }

        Vec3 initialNormal = new(0, 0, 1);
        Vec3 initialAxisX = new(1, 0, 0);
        var initialFrame = new WorkingCoordinateSystemDefinition(
            new Vec3(0, 0, 0),
            initialAxisX,
            new Vec3(0, 1, 0),
            initialNormal);
        CandidateOutcome outcome = ExecuteWithoutSessionChangedRefresh(() => _openModel.Execute(
            ProjectId.New(),
            Path.GetFileNameWithoutExtension(path),
            sourceResult.GetValueOrThrow(),
            initialFrame));
        if (!outcome.IsCommitted)
        {
            StatusText.Text = $"로드 실패: {FormatIssues(outcome.Issues)} · {path}";
            return;
        }

        stopwatch.Stop();
        CompleteCommittedModelLoad(
            Path.GetFileName(path),
            outcome,
            initialNormal,
            stopwatch.ElapsedMilliseconds);
    }

    /// <summary>기존 모델 열기 흐름이 자체 화면 갱신을 하도록 세션 변경 알림을 잠시 보류합니다.</summary>
    private CandidateOutcome ExecuteWithoutSessionChangedRefresh(Func<CandidateOutcome> operation)
    {
        using (SessionPresentation.SuppressSessionChangedRefresh())
            return operation();
    }

    /// <summary>설계 후보가 Native 문서를 교체한 뒤 좌표계와 Face 대응을 새 세대에 연결합니다.</summary>
    private void SynchronizeCommittedProject(ApplicationEvent applicationEvent)
    {
        SessionSnapshot snapshot = _projectSession.Current;
        DesignProject project = snapshot.CurrentProject;
        if (project is null ||
            applicationEvent.DesignRevision is { } eventRevision && eventRevision != project.Revision)
        {
            return;
        }

        HashSet<FaceRefId> selectedFaceReferences = RegionSelection.SelectedFaceReferences.ToHashSet();
        SessionPresentation.RepairMessage = null;

        try
        {
            ShapeInfo shapeInfo = Bridge.GetShapeInfo();
            var outcome = new CandidateOutcome(
                CandidateStatus.Committed,
                snapshot,
                Array.Empty<ValidationIssue>());
            CompleteCommittedModelLoad(
                project.Name,
                outcome,
                project.WorkingCoordinateSystem.Normal,
                elapsedMilliseconds: 0,
                preserveCommittedGeometry: true);

            RestoreSelectedFaceReferences(selectedFaceReferences);
            if (RegionSelection.Count > 0)
                RefreshSelectedRegion(preserveCommittedGeometry: true);

            if (shapeInfo.Faces == 0)
                throw new InvalidOperationException("확정 뒤 Native 문서에서 Face를 찾지 못했습니다.");
        }
        catch (Exception exception)
        {
            SessionPresentation.RepairMessage = $"확정 설계 화면 동기화에 실패했습니다: {exception.Message}";
            throw;
        }
    }

    /// <summary>기존 영속 Face 선택을 새 Native 문서의 현재 면 번호로 복원합니다.</summary>
    private void RestoreSelectedFaceReferences(IReadOnlySet<FaceRefId> selectedFaceReferences)
    {
        RegionSelection.Clear();
        foreach (KeyValuePair<int, FaceRefId> mapping in _faceReferenceIndex.ReferencesByNativeIndex
                     .Where(mapping => selectedFaceReferences.Contains(mapping.Value))
                     .OrderBy(mapping => mapping.Key))
        {
            RegionSelection.Add(mapping.Value);
            SelectedFacesList.Items.Add(
                string.Format("#{0} {1}", mapping.Key, Bridge.GetFaceType(mapping.Key)));
        }

        FaceRefId selectedFaceReference = RegionSelection.SelectedFaceReferences.FirstOrDefault();
        if (!selectedFaceReference.IsEmpty)
        {
            FaceCombo.SelectedItem = FaceCombo.Items
                .OfType<FaceItem>()
                .FirstOrDefault(item => item.ReferenceId == selectedFaceReference);
        }
    }

    /// <summary>확정된 Native 문서와 프로젝트 세션을 기존 화면 상태에 반영합니다.</summary>
    private void CompleteCommittedModelLoad(
        string displayName,
        CandidateOutcome outcome,
        Vec3 workingNormal,
        long elapsedMilliseconds,
        bool preserveCommittedGeometry = false)
    {
        ResetWorkingCoordinateSystem();
        Bridge.SetShapeColor(0.55, 0.65, 0.85);
        ResetCircuitEditState(
            clearNative: true,
            preserveCommittedGeometry: preserveCommittedGeometry);

        ClearModelSelectionState();
        Viewport.RectangleSelectionMode = false;
        Viewport.ExclusionSketchMode = false;
        if (!preserveCommittedGeometry)
        {
            Bridge.ClearExclusionOverlays();
            Bridge.ClearOverlays();
        }
        DrawRegionCanvas();

        ShapeInfo shapeInfo = Bridge.GetShapeInfo();
        WorkingCoordinates.SynchronizeAxisSelection(
            WorkingCoordinateSystemViewModel.AxisIndexForNormal(workingNormal));

        bool coordinateSystemApplied = preserveCommittedGeometry
            ? ApplyWorkingCoordinateSystemPreservingCommittedGeometry(workingNormal)
            : ApplyWorkingCoordinateSystem(workingNormal);
        DesignProject committedProject = _projectSession.Current.CurrentProject
            ?? throw new InvalidOperationException("모델 확정 뒤 ProjectSession이 비어 있습니다.");
        string faceFeatureDiagnostic = BindFaceReferences(committedProject, shapeInfo.Faces);
        PopulateFaceCombo(shapeInfo.Faces);
        RefreshHeatingRegionChoices(committedProject);
        RefreshControlPointChoices(committedProject);
        RefreshTurnSegmentCurveChoices(committedProject);
        RefreshConnectionPathChoices(committedProject);
        RefreshCircuitSurvivorChoices(committedProject);
        RefreshCircuitTurnChoices(committedProject);
        RefreshCircuitLengthTable(_projectSession.Current);
        string turnVisibilityDiagnostic = ApplyCommittedTurnVisibility(committedProject);
        ShowLoadedModelSummary(displayName, shapeInfo, elapsedMilliseconds);
        if (!coordinateSystemApplied)
            StatusText.Text += " · 작업 좌표계를 적용하지 못했습니다.";
        if (faceFeatureDiagnostic is not null)
            StatusText.Text += string.Concat(" · ", faceFeatureDiagnostic);
        if (turnVisibilityDiagnostic is not null)
            StatusText.Text += string.Concat(" · ", turnVisibilityDiagnostic);
        if (SessionPresentation.RepairMessage is not null)
            StatusText.Text += string.Concat(" · ", SessionPresentation.RepairMessage);
        else if (outcome.Status == CandidateStatus.PostCommitFailure)
            StatusText.Text += " · 모델은 확정됐지만 화면 후처리 알림에 실패했습니다.";
    }

    /// <summary>새 모델을 확정한 뒤 이전 문서에 종속된 선택·제외 상태를 지웁니다.</summary>
    private void ClearModelSelectionState()
    {
        RegionSelection.Clear();
        SelectedFacesList.Items.Clear();
        RefreshExitSideChoices();
        FaceDragSelectModeBox.IsChecked = false;
        IncludeModeBox.IsChecked = false;
        ExcludeModeBox.IsChecked = false;
    }

    /// <summary>확정 프로젝트의 Face ID를 현재 Native 면 번호와 특징에 대응합니다.</summary>
    private string BindFaceReferences(DesignProject project, int faceCount)
    {
        _faceReferenceIndex.Clear();

        var adapter = new FaceFeatureBridgeAdapter();
        if (!adapter.TryReadCurrentModelFeatures(out IReadOnlyList<NativeFaceFeature> features, out string diagnostic))
            return string.Concat("확정된 Face 특징을 읽지 못했습니다: ", diagnostic);

        if (features.Count != faceCount || features.Any(feature =>
                feature.NativeFaceIndex < 0 || feature.NativeFaceIndex >= faceCount) ||
            features.Select(feature => feature.NativeFaceIndex).Distinct().Count() != faceCount)
        {
            return "확정된 Face 특징 목록이 현재 모델과 일치하지 않습니다.";
        }

        OperationResult<IReadOnlyDictionary<FaceRefId, int>> mapResult =
            new FaceReferenceMapBuilder().Build(project, project.SourceModel, features);
        if (!mapResult.IsSuccess)
            return string.Concat("확정 Face 참조 대응 실패: ", FormatIssues(mapResult.Issues));

        if (!_faceReferenceIndex.TryReplace(mapResult.GetValueOrThrow(), out diagnostic))
            return string.Concat("확정 Face 참조 대응 실패: ", diagnostic);

        return null;
    }

    /// <summary>검증 진단에서 가장 구체적인 설명을 모아 상태 표시용 문장으로 만듭니다.</summary>
    private static string FormatIssues(IReadOnlyList<ValidationIssue> issues)
        => Services.UserMessageFormatter.FormatIssues(issues, "모델 후보를 확정하지 못했습니다.");

    /// <summary>형상 개수와 STEP 로드 시간을 상태 표시줄에 기록합니다.</summary>
    private void ShowLoadedModelSummary(string displayName, ShapeInfo shapeInfo, long elapsedMilliseconds)
    {
        StatusText.Text = string.Format(
            "{0} — 솔리드 {1} · 셸 {2} · 면 {3} · 평면 {4} · 로드 {5}ms",
            displayName,
            shapeInfo.Solids,
            shapeInfo.Shells,
            shapeInfo.Faces,
            shapeInfo.Planes,
            elapsedMilliseconds);
    }

    /// <summary>투영 면적이 큰 Face를 우선해 선택 콤보 상자를 채웁니다.</summary>
    private void PopulateFaceCombo(int faceCount)
    {
        FaceCombo.Items.Clear();
        Vec3 axisX = ReadAutoAxis(0, new Vec3(1, 0, 0));
        Vec3 axisZ = ReadAutoAxis(2, new Vec3(0, 0, 1));
        var candidates = new List<FaceCandidate>();

        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            if (TryCreateFaceCandidate(faceIndex, axisX, axisZ, out FaceCandidate candidate))
                candidates.Add(candidate);
        }

        candidates.Sort((first, second) => second.ProjectedArea.CompareTo(first.ProjectedArea));
        foreach (FaceCandidate candidate in candidates)
        {
            FaceCombo.Items.Add(new FaceItem
            {
                Index = candidate.NativeIndex,
                ReferenceId = _faceReferenceIndex.TryGetReferenceId(candidate.NativeIndex, out FaceRefId referenceId)
                    ? referenceId
                    : default,
                Label = candidate.Label
            });
        }

        if (FaceCombo.Items.Count > 0)
            FaceCombo.SelectedIndex = 0;
    }

    /// <summary>Native 자동 축이 없으면 지정한 기본 축을 사용합니다.</summary>
    private static Vec3 ReadAutoAxis(int axisIndex, Vec3 fallback)
    {
        double[] nativeAxis = Bridge.GetAutoAxis(axisIndex);
        if (nativeAxis is null || nativeAxis.Length < 3)
            return fallback;

        return new Vec3(nativeAxis[0], nativeAxis[1], nativeAxis[2]).Normalized();
    }

    /// <summary>면 경계를 실제 크기 단위로 투영해 콤보 후보를 구성합니다.</summary>
    private static bool TryCreateFaceCandidate(
        int faceIndex,
        Vec3 axisX,
        Vec3 axisZ,
        out FaceCandidate candidate)
    {
        candidate = null;
        string surfaceType = Bridge.GetFaceType(faceIndex);
        if (surfaceType is null)
            return false;

        UvWire[] wires = Bridge.GetFaceUvBoundary(faceIndex);
        if (wires is null || wires.Length == 0)
            return false;

        List<Vec3> boundaryPoints = SampleFaceBoundary(faceIndex, wires);
        if (boundaryPoints.Count < 3)
            return false;

        Vec3 origin = new(
            boundaryPoints.Average(point => point.X),
            boundaryPoints.Average(point => point.Y),
            boundaryPoints.Average(point => point.Z));
        var frame = new ProjectionFrame(origin, axisZ, axisX);
        if (!TryMeasureProjectedBounds(boundaryPoints, frame, out double width, out double height))
            return false;
        if (width < 2 || height < 2)
            return false;

        string label = string.Format(
            "#{0} {1} {2:F0}×{3:F0}", faceIndex, surfaceType, width, height);
        candidate = new FaceCandidate(faceIndex, label, width * height);
        return true;
    }

    /// <summary>UV 표본을 Native에서 3차원 밀리미터 좌표로 변환합니다.</summary>
    private static List<Vec3> SampleFaceBoundary(int faceIndex, IReadOnlyList<UvWire> wires)
    {
        var points = new List<Vec3>(64);
        foreach (UvWire wire in wires)
        {
            double[] uvPoints = wire.Points;
            if (uvPoints is null)
                continue;

            for (int pointIndex = 0; pointIndex + 1 < uvPoints.Length; pointIndex += 2)
            {
                double[] sampledPoint = Bridge.SampleUvTo3D(
                    faceIndex,
                    uvPoints[pointIndex],
                    uvPoints[pointIndex + 1]);
                if (sampledPoint is not null && sampledPoint.Length >= 3)
                    points.Add(new Vec3(sampledPoint[0], sampledPoint[1], sampledPoint[2]));
            }
        }

        return points;
    }

    /// <summary>투영 표본으로 축 정렬 경계 상자의 폭과 높이를 계산합니다.</summary>
    private static bool TryMeasureProjectedBounds(
        IEnumerable<Vec3> points,
        ProjectionFrame frame,
        out double width,
        out double height)
    {
        double minX = double.MaxValue;
        double maxX = double.MinValue;
        double minY = double.MaxValue;
        double maxY = double.MinValue;

        foreach (Vec3 point in points)
        {
            Vec2 projected = frame.Project(point);
            minX = Math.Min(minX, projected.X);
            maxX = Math.Max(maxX, projected.X);
            minY = Math.Min(minY, projected.Y);
            maxY = Math.Max(maxY, projected.Y);
        }

        width = maxX - minX;
        height = maxY - minY;
        return double.IsFinite(width) && double.IsFinite(height);
    }
}
