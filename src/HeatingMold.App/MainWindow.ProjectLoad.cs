using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using HeatingMold.App.Persistence;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.OccBridge;
using Microsoft.Win32;

namespace HeatingMold.App;

public partial class MainWindow
{
    private sealed class RestoredAreaRoute
    {
        public WorkingArea Area;
        public int[] StageIds;
        public int[] FinalIds;
        public double[] Lengths;
        public RoutePieceResult[] ProjectedPieces;
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if (_visualTestInputLocked) return;
        var dialog = new OpenFileDialog
        {
            Title = "HeatingMold 프로젝트 열기",
            Filter = "HeatingMold 프로젝트 (*.json)|*.json|모든 파일 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        LoadProject(dialog.FileName);
    }

    private bool LoadProject(string projectPath)
    {
        if (!TryEnterBusy("프로젝트 열기")) return false;
        try
        {
            if (!ConfirmDiscardSession("프로젝트 열기")) return false;
            ExitVertexEdit();

            ProjectDocument document;
            try
            {
                document = ProjectJsonStore.Load(projectPath);
                ValidateProjectDocument(document);
            }
            catch (Exception ex)
            {
                ReportProjectLoadFailure("프로젝트 파일을 읽지 못했습니다", ex);
                return false;
            }

            string sourcePath = ResolveProjectSourcePath(document, projectPath);
            if (string.IsNullOrWhiteSpace(sourcePath)) return false;

            try
            {
                VerifyProjectSource(document.Source, sourcePath);
            }
            catch (Exception ex)
            {
                ReportProjectLoadFailure("원본 STEP 파일이 저장 당시와 다릅니다", ex);
                return false;
            }

            ProjectRestoreCandidateInfo candidate = null;
            bool modelCommitted = false;
            bool routesCommitted = false;
            WorkingContext context = null;
            var restored = new List<RestoredAreaRoute>();
            int nextStageId = _nextStageId;
            int nextNativeId = _nextNativePieceId;
            try
            {
                candidate = Bridge.BeginProjectRestoreCandidate(sourcePath);
                if (candidate == null)
                    throw new InvalidOperationException(
                        "STEP 격리 로드에 실패했습니다: " + (Bridge.GetProjectRestoreCandidateError() ?? "알 수 없는 오류"));

                if (!ProjectTopologyRemap.TryMapFaceIds(
                        document.Source.FaceTopologySignatures, candidate.FaceSignatures,
                        out int[] faceMap, out string faceMapError))
                    throw new InvalidOperationException(faceMapError);

                if (!Bridge.CommitProjectRestoreCandidate(candidate.CandidateId))
                    throw new InvalidOperationException("검증된 STEP 후보를 모델로 확정하지 못했습니다.");
                modelCommitted = true;

                _modelRevision = Bridge.GetModelRevision();
                _modelFileName = Path.GetFileNameWithoutExtension(sourcePath);
                _modelPath = sourcePath;
                if (ModelPathText != null)
                {
                    ModelPathText.Text = sourcePath;
                    ModelPathText.ToolTip = sourcePath;
                }

                _workingSide.Invalidate();
                ClearAllRouteEditStates();
                Bridge.SetShapeColor(0.55, 0.65, 0.85);
                ClearAllAreas(clearNative: true);
                Bridge.ClearFaceSelection();
                Bridge.ClearHover();
                _multiFaceRegionFailure = null;
                ExitExclusionSketch();
                Viewport.RectangleSelectionMode = false;

                double[] frameValues = ProjectFrameValues(document.WorkingSide.Frame);
                context = Bridge.CreateWorkingContextWithFrame(frameValues);
                if (context == null)
                    throw new InvalidOperationException("저장된 작업평면 프레임으로 작업 컨텍스트를 만들지 못했습니다.");
                VerifyRestoredFrame(context, frameValues);
                ProjectionFrame workingFrame = ToProjectionFrame(document.WorkingSide.Frame);
                _workingSide.RestoreConfirmed(new WorkingSideDefinition(
                    new WorkingSideId(context.Id),
                    new ModelRevision(context.Revision),
                    workingFrame));
                // 프로젝트를 열면 저장된 작업면을 화면 정면으로 맞춘다(모델 열기의
                // ApplyWorkingSide 와 같은 동작). 뷰 정렬은 표시 전용이므로 실패해도
                // 기하/경로 복원을 되돌리지 않고 완료 상태에 경고로만 남긴다.
                string viewWarning = null;
                try
                {
                    if (!_prepareWorkingFront(context.Id, context.Revision))
                        viewWarning = "작업 정면 뷰를 정렬하지 못했습니다";
                }
                catch (Exception viewException)
                {
                    viewWarning = viewException.Message;
                }
                Bridge.ShowWorkingBoundary(null);
                WorkingSideText.Text = string.Format(
                    "작업 방향 N={0} · 모델 r{1} · 저장 프레임 복원", workingFrame.Normal, context.Revision);

                foreach (ProjectAreaDocument areaDocument in document.Areas.OrderBy(a => a.AreaId))
                    restored.Add(RestoreArea(areaDocument, document, faceMap, context, workingFrame,
                        ref nextStageId, ref nextNativeId));

                CommitRestoredRoutes(context, restored);
                routesCommitted = true;
                _nextStageId = nextStageId;
                _nextNativePieceId = nextNativeId;

                _areas.Clear();
                foreach (RestoredAreaRoute route in restored)
                    _areas.Add(route.Area);
                _nextAreaId = Math.Max(_nextAreaId, _areas.Count == 0 ? 1 : _areas.Max(a => a.AreaId) + 1);

                ApplyAreaColors();
                // 인출점 마커는 커밋 경로의 표시 대상과 같은 방식으로 복원한다.
                // 마커 표시 실패는 복원된 기하를 되돌리는 이유가 아니므로 경고로만 남긴다.
                try
                {
                    var endpointValues = new List<double>();
                    foreach (WorkingArea area in _areas)
                        PrepareCommittedRoutePresentation(area, endpointValues, out _, out _);
                    if (!Bridge.SetRouteEndpointsNoRedraw(endpointValues.ToArray()))
                        _routePresentationWarning = "인출점 마커를 표시하지 못했습니다.";
                }
                catch (Exception markerException)
                {
                    _routePresentationWarning = "인출점 마커 표시 실패: " + markerException.Message;
                }
                // 길이 라벨은 복원된 확정 체인에서 다시 만든다. 라벨 표시 실패는
                // 이미 커밋된 경로 복원을 되돌리는 이유가 아니므로 경고로만 남긴다.
                try
                {
                    if (!TryReplaceRouteLengthLabels(_workingSide.Confirmed, out string labelFailure))
                    {
                        _routePresentationWarning = labelFailure;
                        try { Bridge.ClearRouteLengthLabels(); } catch (Exception) { }
                    }
                }
                catch (Exception labelException)
                {
                    _routePresentationWarning = labelException.Message;
                    try { Bridge.ClearRouteLengthLabels(); } catch (Exception) { }
                }
                // 복원된 확정 경로의 발열사 간격 표시. 표시 실패는 복원된 기하를 되돌리지 않는다.
                try
                {
                    if (!TryRefreshSpacingMarkers(_workingSide.Confirmed, _areas, out string spacingFailure))
                    {
                        _routePresentationWarning = spacingFailure;
                        try { Bridge.ClearSpacingMarkers(); } catch (Exception) { }
                    }
                }
                catch (Exception spacingException)
                {
                    _routePresentationWarning = spacingException.Message;
                    try { Bridge.ClearSpacingMarkers(); } catch (Exception) { }
                }
                WorkingArea active = _areas.FirstOrDefault(a => a.AreaId == document.ActiveAreaId) ?? _areas.FirstOrDefault();
                ActivateArea(active);
                RefreshAreaList();
                UpdateCircuitTable();
                ApplyProjectElectrical(document.Electrical, document.Review);

                ResetProjectBaselineAfterModelLoad(sourcePath, true);
                _projectPath = Path.GetFullPath(projectPath);
                _savedProjectFingerprint = ProjectJsonStore.ComputeFingerprint(document);
                _modelContentHash = document.Source.Sha256;
                UpdateProjectActionStates();

                StatusText.Text = string.Format(
                    "프로젝트 열기 완료 — {0} · 영역 {1}개 · 모델 r{2}",
                    Path.GetFileName(projectPath), _areas.Count, context.Revision);
                if (_routePresentationWarning != null)
                    StatusText.Text += " · 경고: " + _routePresentationWarning;
                if (viewWarning != null)
                    StatusText.Text += " · 경고: " + viewWarning;
                UpdateActionStates();
                ReturnToAreaTab();
                return true;
            }
            catch (Exception ex)
            {
                if (!modelCommitted && candidate != null)
                {
                    try { Bridge.DiscardProjectRestoreCandidate(candidate.CandidateId); } catch { }
                }
                if (modelCommitted && routesCommitted)
                {
                    if (_areas.Count == 0)
                    {
                        foreach (RestoredAreaRoute route in restored)
                            _areas.Add(route.Area);
                    }
                    ReportProjectLoadFailure("프로젝트는 복원되었지만 표시 갱신에 실패했습니다", ex);
                    return false;
                }
                if (modelCommitted)
                {
                    int[] staged = restored.SelectMany(r => r.StageIds ?? Array.Empty<int>()).ToArray();
                    if (context != null && staged.Length > 0)
                    {
                        try
                        {
                            Bridge.DiscardWorkingRouteGeometry(context.Id, context.Revision,
                                staged, Array.Empty<int>());
                        }
                        catch { }
                    }
                    try { ClearAllAreas(clearNative: true); } catch { }
                    _workingSide.Invalidate();
                    WorkingSideText.Text = "작업 방향 · 모델을 다시 불러오세요";
                }
                ReportProjectLoadFailure("프로젝트를 복원하지 못했습니다", ex);
                return false;
            }
            finally
            {
                if (!modelCommitted && candidate != null)
                {
                    try { Bridge.DiscardProjectRestoreCandidate(candidate.CandidateId); } catch { }
                }
            }
        }
        finally
        {
            ExitBusy();
        }
    }

    private void ValidateProjectDocument(ProjectDocument document)
    {
        if (document?.Source == null)
            throw new InvalidDataException("원본 STEP 정보가 없습니다.");
        if (string.IsNullOrWhiteSpace(document.Source.Sha256) || document.Source.Sha256.Length != 64)
            throw new InvalidDataException("원본 STEP SHA-256이 올바르지 않습니다.");
        if (document.Source.ImportPolicyVersion != 2)
            throw new InvalidDataException("지원하지 않는 STEP 가져오기 정책 버전입니다: " + document.Source.ImportPolicyVersion);
        if (!string.IsNullOrWhiteSpace(document.Source.TopologyIdentityError))
            throw new InvalidDataException("저장된 topology identity가 완전하지 않습니다: " + document.Source.TopologyIdentityError);
        if (document.Source.FaceTopologySignatures == null || document.Source.FaceTopologySignatures.Count == 0)
            throw new InvalidDataException("저장된 Face topology signature가 없습니다.");
        if (document.WorkingSide?.Frame == null)
            throw new InvalidDataException("저장된 작업평면 프레임이 없습니다.");

        // 전기 제원은 네이티브 모델을 교체하기 전에 검증한다. 손상되었거나 프리셋과
        // 맞지 않는 값은 조용히 Case 1로 바꾸지 않고 프로젝트 열기를 거부한다.
        ProjectElectricalValidator.Validate(document.Electrical);

        var areaIds = new HashSet<int>();
        foreach (ProjectAreaDocument area in document.Areas ?? new List<ProjectAreaDocument>())
        {
            if (area == null) throw new InvalidDataException("작업영역 정보가 비어 있습니다.");
            if (area.AreaId <= 0 || !areaIds.Add(area.AreaId))
                throw new InvalidDataException("작업영역 ID가 중복되었거나 올바르지 않습니다: " + area.AreaId);
            if (string.IsNullOrWhiteSpace(area.Name))
                throw new InvalidDataException("작업영역 이름이 비어 있습니다.");
            if (area.FaceIds == null || area.FaceIds.Length == 0)
                throw new InvalidDataException("작업영역 " + area.Name + "에 Face가 없습니다.");
            if (area.Frame == null)
                throw new InvalidDataException("작업영역 " + area.Name + "에 프레임이 없습니다.");
            if (area.NativePieceIds == null || area.RoutePieceLengths == null)
                throw new InvalidDataException("작업영역 " + area.Name + "의 경로 ID/길이 정보가 없습니다.");
            if (area.NativePieceIds.Length > 0)
            {
                if (!area.ChainsAvailable)
                    throw new InvalidDataException(
                        "작업영역 " + area.Name + "의 확정 3D 경로가 저장되지 않아 복원할 수 없습니다: " +
                        (area.ChainError ?? "사유 없음"));
                if (area.CommittedChains == null || area.CommittedChains.Count != area.NativePieceIds.Length)
                    throw new InvalidDataException("작업영역 " + area.Name + "의 확정 3D 경로 조각 수가 일치하지 않습니다.");
            }
        }
    }

    private string ResolveProjectSourcePath(ProjectDocument document, string projectPath)
    {
        string baseDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(document.Source.RelativePath) && !string.IsNullOrWhiteSpace(baseDirectory))
            candidates.Add(Path.GetFullPath(Path.Combine(baseDirectory, document.Source.RelativePath)));
        if (!string.IsNullOrWhiteSpace(document.Source.OriginalPathHint))
            candidates.Add(document.Source.OriginalPathHint);

        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate)) continue;
            if (SourceMatches(document.Source, candidate)) return candidate;
        }

        var dialog = new OpenFileDialog
        {
            Title = "원본 STEP 파일 선택",
            Filter = "STEP 파일 (*.stp;*.step)|*.stp;*.step|모든 파일 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return null;
        if (!SourceMatches(document.Source, dialog.FileName))
        {
            ErrorReporter.ShowError("원본 STEP이 다릅니다",
                "선택한 STEP 파일의 SHA-256이 프로젝트에 저장된 값과 일치하지 않습니다." + Environment.NewLine +
                "저장 당시 사용한 원본 STEP 파일을 선택하세요.");
            return null;
        }
        return dialog.FileName;
    }

    private static bool SourceMatches(ProjectSourceDocument source, string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (source.FileLength > 0 && info.Length != source.FileLength) return false;
            return string.Equals(ProjectJsonStore.ComputeFileSha256(path), source.Sha256,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void VerifyProjectSource(ProjectSourceDocument source, string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("원본 STEP 파일을 찾을 수 없습니다.", path);
        if (source.FileLength > 0 && info.Length != source.FileLength)
            throw new InvalidDataException("원본 STEP 파일 크기가 저장 당시와 다릅니다.");
        string hash = ProjectJsonStore.ComputeFileSha256(path);
        if (!string.Equals(hash, source.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("원본 STEP 파일의 SHA-256이 저장 당시와 다릅니다.");
    }

    private RestoredAreaRoute RestoreArea(ProjectAreaDocument document, ProjectDocument project,
        int[] faceMap, WorkingContext context, ProjectionFrame workingFrame,
        ref int nextStageId, ref int nextNativeId)
    {
        ProjectionFrame frame = ToProjectionFrame(document.Frame);
        VerifySameFrame(frame, workingFrame, "작업영역 " + document.Name);

        int[] mappedFaces = MapFaceIds(document.FaceIds, faceMap, project, "작업영역 " + document.Name);

        RegionBoundaryLoop[] regionLoops = Bridge.GetSelectedRegionBoundary(
            mappedFaces.OrderBy(id => id).ToArray(), GeometryTolerancePolicy.DistanceTolerance,
            out string regionFailure);
        if (regionLoops == null || regionLoops.Length != 1 || regionLoops[0] == null || !regionLoops[0].Closed)
            throw new InvalidOperationException(
                "작업영역 " + document.Name + "의 원본 CAD 외곽 경계를 복원하지 못했습니다: " +
                (string.IsNullOrWhiteSpace(regionFailure) ? "닫힌 단일 루프가 아닙니다" : regionFailure));

        ExitEdgeReference[] regionReferences = regionLoops[0].EdgeReferences ?? Array.Empty<ExitEdgeReference>();
        var freshReferences = UniqueExitReferences(regionReferences);

        if (!ProjectTopologyRemap.TryMapExitEdgeReferences(
                document.ExitEdgeRefs, freshReferences, faceMap,
                out ExitEdgeReference[] mappedExitRefs, out string exitReferenceError))
            throw new InvalidOperationException("작업영역 " + document.Name + ": " + exitReferenceError);

        ExitBoundaryPoint? selectedStart = null;
        ExitBoundaryPoint? selectedEnd = null;
        if (document.SelectedStartExit != null)
        {
            if (!ProjectTopologyRemap.TryMapExitBoundaryPoint(
                    document.SelectedStartExit, document.ExitEdgeRefs, mappedExitRefs,
                    context.Revision, context.Id, context.Revision,
                    out ExitBoundaryPoint mapped, out string pointError))
                throw new InvalidOperationException("작업영역 " + document.Name + " 시작 인출점: " + pointError);
            selectedStart = mapped;
        }
        if (document.SelectedEndExit != null)
        {
            if (!ProjectTopologyRemap.TryMapExitBoundaryPoint(
                    document.SelectedEndExit, document.ExitEdgeRefs, mappedExitRefs,
                    context.Revision, context.Id, context.Revision,
                    out ExitBoundaryPoint mapped, out string pointError))
                throw new InvalidOperationException("작업영역 " + document.Name + " 종료 인출점: " + pointError);
            selectedEnd = mapped;
        }

        var area = new WorkingArea
        {
            Name = document.Name,
            AreaId = document.AreaId,
            Color = ToColor(document.Color),
            Frame = frame,
            Direction = ParseDirection(document.Direction),
            SpacingText = document.SpacingText ?? "5",
            ExitChain = MapVec2(document.ExitChain),
            ExitChain3D = MapVec3(document.ExitChain3D),
            StartAnchor = document.StartAnchor == null ? null : ToVec2(document.StartAnchor),
            EndAnchor = document.EndAnchor == null ? null : ToVec2(document.EndAnchor),
            ExitEdgeRefs = mappedExitRefs.ToList(),
            SelectedStartExit = selectedStart,
            SelectedEndExit = selectedEnd,
            Exclusions = (document.Exclusions ?? new List<ProjectRect>())
                .Select(r => (r.MinX, r.MinY, r.MaxX, r.MaxY)).ToList(),
            Pattern = RestorePattern(document.Pattern, document, mappedExitRefs, context,
                "작업영역 " + document.Name + " 패턴"),
            Paths = MapPaths(document.Paths),
            ExclusionPieces = MapExclusionPieces(document.ExclusionPieces, document, mappedExitRefs, context,
                "작업영역 " + document.Name + " 제외 경로"),
            PatternDisplayed = document.PatternDisplayed,
            RouteEdited = document.RouteEdited
        };
        foreach (int faceId in mappedFaces) area.FaceIdSet.Add(faceId);
        foreach (List<ProjectVec2> loop in document.Region ?? new List<List<ProjectVec2>>())
            area.Region.Add(MapVec2(loop));

        area.Circuits = RestoreCircuits(document.Circuits, document, mappedExitRefs, context,
            "작업영역 " + document.Name + " 회로");
        area.CircuitPieceIndices = (document.CircuitPieceIndices ?? new List<int[]>())
            .Select(indices => (indices ?? Array.Empty<int>()).ToList()).ToList();
        area.ProjectedPieces = RestoreProjectedPieces(document.ProjectedPieces);

        if (regionReferences.Length == 0)
            throw new InvalidOperationException(
                "작업영역 " + document.Name + "의 원본 CAD 외곽 Edge 참조를 복원하지 못했습니다.");
        area.RegionCadBoundary = new CadExitBoundary2D(context.Id, context.Revision, regionReferences);
        area.RegionBoundary = new ExactRegionContainment(
            new CadRegionBoundary2D(context.Id, context.Revision, regionReferences));

        // 저장된 제외영역에서 마진 적용 전 유효 외곽선을 다시 만든다. 저장된 구현 타입명은
        // 복원 근거로 쓰지 않는다. 허용되지 않는 제외는 원본 외곽으로 대체하지 않고 실패시킨다.
        IReadOnlyList<Vec2> regionLoop = area.Region.Count == 1 ? area.Region[0] : null;
        List<ProjectSplitCutLine> savedCutLines = document.SplitCutLines ?? new List<ProjectSplitCutLine>();
        if (savedCutLines.Count > 0)
        {
            // 분할 영역: CAD 외곽과 artificial 절단선이 섞인 경계를 저장된 절단선에서 재구성한다.
            if (regionLoop == null)
                throw new InvalidOperationException(string.Format(
                    "작업영역 '{0}'은 분할 영역이지만 단일 폐합 영역이 아닙니다.", document.Name));
            var cutLines = new List<(Vec2 A, Vec2 B)>();
            foreach (ProjectSplitCutLine cut in savedCutLines)
                cutLines.Add((new Vec2(cut.AX, cut.AY), new Vec2(cut.BX, cut.BY)));
            if (!SplitRegionBoundary2D.TryCreate(area.RegionCadBoundary, regionLoop, regionLoop,
                    area.Exclusions, cutLines, out SplitRegionBoundary2D splitBoundary,
                    out string splitFailure))
            {
                throw new InvalidOperationException(string.Format(
                    "작업영역 '{0}'의 분할 경계를 복원하지 못했습니다: {1}", document.Name, splitFailure));
            }
            area.SplitCutLines = cutLines;
            area.RegionExitBoundary = splitBoundary;
            area.RegionBoundary = new ExactRegionContainment(splitBoundary.CreateContainment());
        }
        else
        {
            if (!TryBuildRegionExitBoundary(area.RegionCadBoundary, regionLoop, area.Exclusions,
                    out IExitBoundary2D restoredRegionExitBoundary, out string boundaryFailure))
            {
                throw new InvalidOperationException(string.Format(
                    "작업영역 '{0}'의 제외영역 외곽선을 복원하지 못했습니다: {1}",
                    document.Name, boundaryFailure));
            }
            area.RegionExitBoundary = restoredRegionExitBoundary;
        }
        area.ExitBoundary = area.RegionExitBoundary;

        // 확정 경로 매핑을 캡처하기 전에 저장된 인출점 provenance를 복원된 경계에서
        // 그대로 검증한다. 실패한 선택점을 지우고 성공 처리하지 않는다.
        bool hasCommittedContext = document.CommittedMapping != null;
        ValidateRestoredExitPoints(area, document.Name, area.RegionExitBoundary);
        area.CommittedMapping = hasCommittedContext
            ? CommittedRouteContext.CaptureRegion(area)
            : CommittedRouteContext.Capture(area);
        area.AppliedInputs = RestoreAppliedInputs(document.AppliedInputs, area);

        var route = new RestoredAreaRoute { Area = area };
        if (document.NativePieceIds.Length > 0)
            StageRestoredChains(document, area, faceMap, context,
                ref nextStageId, ref nextNativeId, out route.StageIds,
                out route.FinalIds, out route.Lengths, out route.ProjectedPieces);
        else
        {
            route.StageIds = Array.Empty<int>();
            route.FinalIds = Array.Empty<int>();
            route.Lengths = Array.Empty<double>();
            route.ProjectedPieces = area.ProjectedPieces.ToArray();
        }
        return route;
    }

    /// <summary>
    /// 저장된 인출점 provenance를 복원된 경계에서 직접 평가한다. 가까운 다른 edge로
    /// 재탐색하거나 유효하지 않은 선택점을 조용히 지우지 않고, 로드 실패로 보고한다.
    /// </summary>
    private static void ValidateRestoredExitPoints(WorkingArea area, string areaName,
        IExitBoundary2D routeBoundary)
    {
        if (area.SelectedStartExit.HasValue)
            ValidateRestoredExitPoint(area.RegionExitBoundary, area.SelectedStartExit.Value,
                "작업영역 '" + areaName + "' 시작 인출점");
        if (area.SelectedEndExit.HasValue)
            ValidateRestoredExitPoint(area.RegionExitBoundary, area.SelectedEndExit.Value,
                "작업영역 '" + areaName + "' 종료 인출점");

        if (routeBoundary == null)
        {
            bool hasSavedExits = area.Pattern?.StartExit != null || area.Pattern?.EndExit != null ||
                (area.Circuits != null && area.Circuits.Any(
                    circuit => circuit.StartExit != null || circuit.EndExit != null));
            if (hasSavedExits)
                throw new InvalidOperationException("작업영역 '" + areaName +
                    "'의 인출 경계가 없어 저장된 회로 인출점을 검증할 수 없습니다");
            return;
        }

        if (area.Pattern?.StartExit != null)
            ValidateRestoredExitPoint(routeBoundary, ToBoundary(area.Pattern.StartExit),
                "작업영역 '" + areaName + "' 패턴 시작 인출점");
        if (area.Pattern?.EndExit != null)
            ValidateRestoredExitPoint(routeBoundary, ToBoundary(area.Pattern.EndExit),
                "작업영역 '" + areaName + "' 패턴 종료 인출점");

        if (area.Circuits == null) return;
        for (int i = 0; i < area.Circuits.Count; i++)
        {
            CircuitRoute circuit = area.Circuits[i];
            if (circuit?.StartExit != null)
                ValidateRestoredExitPoint(routeBoundary, ToBoundary(circuit.StartExit),
                    string.Format("작업영역 '{0}' 회로 {1} 시작 인출점", areaName, i + 1));
            if (circuit?.EndExit != null)
                ValidateRestoredExitPoint(routeBoundary, ToBoundary(circuit.EndExit),
                    string.Format("작업영역 '{0}' 회로 {1} 종료 인출점", areaName, i + 1));
        }
    }

    private static void ValidateRestoredExitPoint(IExitBoundary2D boundary,
        ExitBoundaryPoint point, string label)
    {
        ExitBoundaryResolution evaluated = ExitBoundaryConstraint.EvaluateSaved(boundary, point);
        if (evaluated.Succeeded) return;
        throw new InvalidOperationException(label + "이(가) 복원된 경계에서 유효하지 않습니다 (" +
            (evaluated.Failure?.ToString() ?? "Unknown") + ")");
    }

    private void StageRestoredChains(ProjectAreaDocument document, WorkingArea area, int[] faceMap,
        WorkingContext context, ref int nextStageId, ref int nextNativeId,
        out int[] stageIdsOut, out int[] finalIds, out double[] lengths, out RoutePieceResult[] projected)
    {
        var chainList = document.CommittedChains ?? new List<ProjectCommittedChain>();
        var stageIds = new List<int>();
        var finalList = new List<int>();
        var lengthList = new List<double>();
        var projectedList = new List<RoutePieceResult>();
        try
        {
            for (int i = 0; i < chainList.Count; i++)
            {
                ProjectCommittedChain chain = chainList[i]
                    ?? throw new InvalidOperationException("작업영역 " + area.Name + "의 3D 경로 조각이 비어 있습니다.");
                List<ProjectVec3> points = chain.Points ?? new List<ProjectVec3>();
                if (points.Count < 2)
                    throw new InvalidOperationException("작업영역 " + area.Name + "의 3D 경로 조각이 퇴화했습니다.");
                int[] supportFaces = chain.SupportFaceIds ?? Array.Empty<int>();
                if (supportFaces.Length == 0)
                    throw new InvalidOperationException(
                        "작업영역 " + area.Name + "의 3D 경로에 지지 Face 정보가 없어 정확히 복원할 수 없습니다.");
                if (supportFaces.Length != points.Count)
                    throw new InvalidOperationException(
                        "작업영역 " + area.Name + "의 3D 경로 지지 Face 수가 점 수와 일치하지 않습니다.");

                var xyz = new double[points.Count * 3];
                var mappedSupport = new int[points.Count];
                for (int p = 0; p < points.Count; p++)
                {
                    ProjectVec3 point = points[p] ?? throw new InvalidOperationException("3D 경로 점이 비어 있습니다.");
                    if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z))
                        throw new InvalidOperationException("3D 경로에 유한하지 않은 좌표가 있습니다.");
                    xyz[p * 3] = point.X;
                    xyz[p * 3 + 1] = point.Y;
                    xyz[p * 3 + 2] = point.Z;
                    mappedSupport[p] = MapFaceId(supportFaces[p], faceMap, null,
                        "작업영역 " + area.Name + " 지지 Face");
                }

                string kind = ResolveChainKind(chain, area, i);
                int stageId = nextStageId--;
                if (stageId >= 0) throw new InvalidOperationException("스테이징 ID 공간이 소진되었습니다.");
                RoutePieceResult staged = Bridge.StageSurfaceMappedRoutePieceWithFaces(
                    context.Id, context.Revision, stageId, kind, xyz, mappedSupport);
                if (staged == null)
                    throw new InvalidOperationException(
                        "작업영역 " + area.Name + "의 3D 경로를 스테이징하지 못했습니다: " +
                        Bridge.GetWorkingStageDiagnostic());

                double savedLength = i < document.RoutePieceLengths.Length
                    ? document.RoutePieceLengths[i]
                    : double.NaN;
                if (double.IsFinite(savedLength) && Math.Abs(staged.Length - savedLength) >
                    1e-5 * Math.Max(1.0, Math.Abs(savedLength)))
                    throw new InvalidOperationException(
                        "작업영역 " + area.Name + "의 3D 경로 길이가 저장 값과 다릅니다.");

                stageIds.Add(stageId);
                finalList.Add(nextNativeId++);
                lengthList.Add(staged.Length);
                projectedList.Add(new RoutePieceResult
                {
                    Length = staged.Length,
                    Start = (double[])staged.Start.Clone(),
                    End = (double[])staged.End.Clone()
                });
            }

            stageIdsOut = stageIds.ToArray();
            finalIds = finalList.ToArray();
            lengths = lengthList.ToArray();
            projected = projectedList.ToArray();
        }
        catch
        {
            if (stageIds.Count > 0)
            {
                try
                {
                    Bridge.DiscardWorkingRouteGeometry(context.Id, context.Revision,
                        stageIds.ToArray(), Array.Empty<int>());
                }
                catch { }
            }
            throw;
        }
    }

    private void CommitRestoredRoutes(WorkingContext context, IReadOnlyList<RestoredAreaRoute> routes)
    {
        int[] stageIds = routes.SelectMany(r => r.StageIds ?? Array.Empty<int>()).ToArray();
        int[] finalIds = routes.SelectMany(r => r.FinalIds ?? Array.Empty<int>()).ToArray();
        if (stageIds.Length != finalIds.Length)
            throw new InvalidOperationException("복원 경로의 스테이징/최종 ID 수가 일치하지 않습니다.");

        if (stageIds.Length > 0)
        {
            try
            {
                if (!Bridge.CommitWorkingRouteGeometryDelta(context.Id, context.Revision,
                        stageIds, finalIds, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()))
                    throw new InvalidOperationException("복원 경로의 네이티브 커밋에 실패했습니다.");
            }
            catch
            {
                try
                {
                    Bridge.DiscardWorkingRouteGeometry(context.Id, context.Revision,
                        stageIds, Array.Empty<int>());
                }
                catch { }
                throw;
            }
        }

        foreach (RestoredAreaRoute route in routes)
        {
            route.Area.NativePieceIds = route.FinalIds;
            route.Area.RoutePieceLengths = route.Lengths;
            route.Area.ProjectedPieces = route.ProjectedPieces.ToList();
            route.Area.PatternDisplayed = route.FinalIds.Length > 0;
            route.StageIds = Array.Empty<int>();
        }
    }

    private static string ResolveChainKind(ProjectCommittedChain chain, WorkingArea area, int index)
    {
        string kind = chain.Kind;
        if (string.IsNullOrWhiteSpace(kind) && area.Pattern?.RoutePieces != null &&
            index < area.Pattern.RoutePieces.Count)
            kind = area.Pattern.RoutePieces[index].Kind.ToString();
        if (!Enum.TryParse(kind, true, out SerpentineRoutePieceKind parsed))
            throw new InvalidOperationException("작업영역 " + area.Name + "의 3D 경로 조각 종류가 올바르지 않습니다.");
        return parsed.ToString();
    }

    private static int[] MapFaceIds(int[] saved, int[] faceMap, ProjectDocument project, string label)
    {
        if (saved == null) throw new InvalidOperationException(label + "의 Face ID가 없습니다.");
        return saved.Select(id => MapFaceId(id, faceMap, project, label)).ToArray();
    }

    private static int[] MapOptionalFaceIds(List<int> saved, int[] faceMap, ProjectDocument project, string label)
    {
        if (saved == null) return Array.Empty<int>();
        return saved.Select(id => MapFaceId(id, faceMap, project, label)).ToArray();
    }

    private static int MapFaceId(int saved, int[] faceMap, ProjectDocument project, string label)
    {
        if (faceMap == null || saved < 0 || saved >= faceMap.Length || faceMap[saved] < 0)
            throw new InvalidOperationException(label + "의 Face " + saved + "를 새 모델에서 찾지 못했습니다.");
        return faceMap[saved];
    }

    private static List<ExitEdgeReference> UniqueExitReferences(IEnumerable<ExitEdgeReference> references)
    {
        var result = new List<ExitEdgeReference>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ExitEdgeReference reference in references)
        {
            if (reference == null) continue;
            string key = string.Join("|",
                reference.EdgeSignature ?? "",
                reference.FaceId.ToString(),
                reference.FirstParameter.ToString("R"),
                reference.LastParameter.ToString("R"),
                reference.Reversed ? "1" : "0");
            if (seen.Add(key)) result.Add(CadExitBoundary2D.CopyReference(reference));
        }
        return result;
    }

    private static SerpentineResult RestorePattern(ProjectPatternDocument document, ProjectAreaDocument area,
        IReadOnlyList<ExitEdgeReference> exitReferences, WorkingContext context, string label)
    {
        if (document == null) return null;
        List<SerpentineRoutePiece> pieces = (document.RoutePieces ?? new List<ProjectRoutePiece>())
            .Select(p => ToRoutePiece(p, label)).ToList();
        List<SerpentineSegment> segments = (document.Segments ?? new List<ProjectSerpentineSegment>())
            .Select(s => new SerpentineSegment(s.LaneIndex, ToVec2(s.Start), ToVec2(s.End))).ToList();
        CircuitExitPoint? start = TryRestoreOptionalExitPoint(document.StartExit, area, exitReferences, context, label);
        CircuitExitPoint? end = TryRestoreOptionalExitPoint(document.EndExit, area, exitReferences, context, label);
        return new SerpentineResult
        {
            Path = MapVec2(document.Path),
            Segments = segments,
            LaneCount = document.LaneCount,
            AngleDeg = document.AngleDeg,
            TotalLength = document.TotalLength,
            NominalLaneCount = document.NominalLaneCount,
            RoutePieces = pieces,
            StartExit = start,
            EndExit = end,
            CandidateId = document.CandidateId
        };
    }

    private static List<CircuitRoute> RestoreCircuits(List<ProjectCircuitDocument> documents,
        ProjectAreaDocument area, IReadOnlyList<ExitEdgeReference> exitReferences, WorkingContext context,
        string label)
    {
        var result = new List<CircuitRoute>();
        foreach (ProjectCircuitDocument document in documents ?? new List<ProjectCircuitDocument>())
        {
            if (document == null) throw new InvalidOperationException(label + "에 빈 회로가 있습니다.");
            if (document.StartExit == null || document.EndExit == null)
                throw new InvalidOperationException(label + "의 시작/종료 인출점이 없습니다.");
            CircuitExitPoint start = RestoreExitPoint(document.StartExit, area, exitReferences, context, label);
            CircuitExitPoint end = RestoreExitPoint(document.EndExit, area, exitReferences, context, label);
            result.Add(new CircuitRoute(
                (document.Pieces ?? new List<ProjectRoutePiece>()).Select(p => ToRoutePiece(p, label)).ToList(),
                start, end, document.AngleDeg, document.LaneCount, document.NominalLaneCount)
            {
                CandidateId = document.CandidateId
            });
        }
        return result;
    }

    private static CircuitExitPoint RestoreExitPoint(ProjectExitPoint document, ProjectAreaDocument area,
        IReadOnlyList<ExitEdgeReference> exitReferences, WorkingContext context, string label)
    {
        if (!ProjectTopologyRemap.TryMapCircuitExitPoint(document, area.ExitEdgeRefs, exitReferences,
                context.Revision, context.Id, context.Revision, out CircuitExitPoint mapped, out string error))
            throw new InvalidOperationException(label + ": " + error);
        return mapped;
    }

    private static CircuitExitPoint? TryRestoreOptionalExitPoint(ProjectExitPoint document,
        ProjectAreaDocument area, IReadOnlyList<ExitEdgeReference> exitReferences, WorkingContext context, string label)
    {
        return document == null ? null : RestoreExitPoint(document, area, exitReferences, context, label);
    }

    private static SerpentineRoutePiece ToRoutePiece(ProjectRoutePiece document, string label)
    {
        if (document == null) throw new InvalidOperationException(label + "에 빈 경로 조각이 있습니다.");
        if (!Enum.TryParse(document.Kind, true, out SerpentineRoutePieceKind kind))
            throw new InvalidOperationException(label + "의 경로 조각 종류가 올바르지 않습니다.");
        List<Vec2> points = MapVec2(document.Points);
        if (points.Count < 2) throw new InvalidOperationException(label + "의 경로 조각이 퇴화했습니다.");
        return new SerpentineRoutePiece(kind, document.LaneIndex, points);
    }

    private AppliedGenerationInputs RestoreAppliedInputs(ProjectAppliedInputs document, WorkingArea area)
    {
        if (document == null) return null;
        if (!double.IsFinite(document.OuterGapMm) || !double.IsFinite(document.WireDiameterMm))
            throw new InvalidOperationException("작업영역 " + area.Name + "의 마지막 적용 외곽 간격/전선 직경이 올바르지 않습니다.");
        AppliedGenerationInputs captured = CaptureAppliedInputs(area, document.OuterGapMm, document.WireDiameterMm);
        return captured;
    }

    private static List<RoutePieceResult> RestoreProjectedPieces(List<ProjectRoutePieceResult> documents)
    {
        var result = new List<RoutePieceResult>();
        foreach (ProjectRoutePieceResult document in documents ?? new List<ProjectRoutePieceResult>())
        {
            if (document == null) { result.Add(null); continue; }
            result.Add(new RoutePieceResult
            {
                Length = document.Length,
                Start = document.Start,
                End = document.End
            });
        }
        return result;
    }

    private static List<List<SerpentineRoutePiece>> MapExclusionPieces(
        List<List<ProjectRoutePiece>> documents, ProjectAreaDocument area,
        IReadOnlyList<ExitEdgeReference> exitReferences, WorkingContext context, string label)
    {
        if (documents == null) return null;
        return documents.Select(component => (component ?? new List<ProjectRoutePiece>())
            .Select(piece => ToRoutePiece(piece, label)).ToList()).ToList();
    }

    private static List<List<Vec2>> MapPaths(List<List<ProjectVec2>> documents)
        => (documents ?? new List<List<ProjectVec2>>()).Select(MapVec2).ToList();

    private static List<Vec2> MapVec2(IEnumerable<ProjectVec2> points)
    {
        var result = new List<Vec2>();
        foreach (ProjectVec2 point in points ?? Array.Empty<ProjectVec2>())
        {
            if (point == null || !double.IsFinite(point.X) || !double.IsFinite(point.Y))
                throw new InvalidOperationException("2D 좌표에 유한하지 않은 값이 있습니다.");
            result.Add(new Vec2(point.X, point.Y));
        }
        return result;
    }

    private static List<Vec3> MapVec3(IEnumerable<ProjectVec3> points)
    {
        var result = new List<Vec3>();
        foreach (ProjectVec3 point in points ?? Array.Empty<ProjectVec3>())
        {
            if (point == null || !double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z))
                throw new InvalidOperationException("3D 좌표에 유한하지 않은 값이 있습니다.");
            result.Add(new Vec3(point.X, point.Y, point.Z));
        }
        return result;
    }

    private static Vec2 ToVec2(ProjectVec2 point)
    {
        if (point == null || !double.IsFinite(point.X) || !double.IsFinite(point.Y))
            throw new InvalidOperationException("2D 좌표가 올바르지 않습니다.");
        return new Vec2(point.X, point.Y);
    }

    private static Vec3 ToVec3(ProjectVec3 point)
    {
        if (point == null || !double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z))
            throw new InvalidOperationException("3D 좌표가 올바르지 않습니다.");
        return new Vec3(point.X, point.Y, point.Z);
    }

    private static ProjectionFrame ToProjectionFrame(ProjectFrame frame)
    {
        if (frame == null) throw new InvalidOperationException("프레임이 없습니다.");
        var result = new ProjectionFrame(ToVec3(frame.Origin), ToVec3(frame.Normal), ToVec3(frame.AxisX));
        if (Vec3.Dot(result.AxisY, ToVec3(frame.AxisY)) < 1.0 - 1e-6)
            throw new InvalidOperationException("저장된 프레임의 축이 오른손 직교 기저가 아닙니다.");
        return result;
    }

    private static double[] ProjectFrameValues(ProjectFrame frame)
    {
        if (frame == null) throw new InvalidOperationException("프레임이 없습니다.");
        Vec3 origin = ToVec3(frame.Origin), x = ToVec3(frame.AxisX), y = ToVec3(frame.AxisY), n = ToVec3(frame.Normal);
        return new[]
        {
            origin.X, origin.Y, origin.Z,
            x.X, x.Y, x.Z,
            y.X, y.Y, y.Z,
            n.X, n.Y, n.Z
        };
    }

    private static void VerifyRestoredFrame(WorkingContext context, double[] expected)
    {
        if (context?.Frame == null || context.Frame.Length != 12)
            throw new InvalidOperationException("작업 컨텍스트 프레임이 올바르지 않습니다.");
        for (int i = 0; i < 12; i++)
            if (!double.IsFinite(context.Frame[i]) ||
                Math.Abs(context.Frame[i] - expected[i]) > 1e-6)
                throw new InvalidOperationException("복원된 작업평면 프레임이 저장 값과 다릅니다.");
    }

    private static void VerifySameFrame(ProjectionFrame left, ProjectionFrame right, string label)
    {
        if (Vec3.Dot(left.Normal, right.Normal) < 1.0 - 1e-6 ||
            Vec3.Dot(left.AxisX, right.AxisX) < 1.0 - 1e-6 ||
            Vec3.Dot(left.AxisY, right.AxisY) < 1.0 - 1e-6 ||
            (left.Origin - right.Origin).Length > 1e-6)
            throw new InvalidOperationException(label + "의 프레임이 저장된 작업평면과 다릅니다.");
    }

    private static PatternDirection ParseDirection(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return PatternDirection.Auto;
        if (!Enum.TryParse(value, true, out PatternDirection direction))
            throw new InvalidOperationException("패턴 방향 값이 올바르지 않습니다: " + value);
        return direction;
    }

    private static Color ToColor(ProjectColor color)
    {
        if (color == null) return Color.FromRgb(0xD9, 0x53, 0x4F);
        return Color.FromArgb(color.A, color.R, color.G, color.B);
    }

    private void ApplyProjectElectrical(ProjectElectricalDocument electrical, ProjectReviewDocument review)
    {
        if (electrical != null)
        {
            _suppressElectricalInput = true;
            try
            {
                if (WirePresetBox != null)
                    WirePresetBox.SelectedIndex = electrical.WirePresetIndex;
                // 저장된 직경·단위 저항은 화면에 다시 채우지 않고 프리셋 이름으로만 표시한다.
                if (AllowedVoltageBox != null)
                    AllowedVoltageBox.Text = electrical.AllowedVoltageText ?? "";
                if (AllowedCurrentBox != null) AllowedCurrentBox.Text = electrical.AllowedCurrentText ?? "";
                if (AllowedPowerBox != null) AllowedPowerBox.Text = electrical.AllowedPowerText ?? "";
            }
            finally
            {
                _suppressElectricalInput = false;
            }
        }
        if (ReviewScopeBox != null)
            ReviewScopeBox.SelectedIndex = review?.CurrentAreaOnly == true ? 1 : 0;
        RefreshElectricalTable();
    }

    private void ReportProjectLoadFailure(string title, Exception ex)
    {
        StatusText.Text = title + ": " + ex.Message;
        ErrorReporter.ShowError(title, ex);
    }
}
