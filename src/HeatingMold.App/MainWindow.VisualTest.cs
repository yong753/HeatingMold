using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using HeatingMold.App.Persistence;
using HeatingMold.App.VisualTesting;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Export;
using HeatingMold.OccBridge;
using netDxf;
using netDxf.Entities;

namespace HeatingMold.App
{
    public partial class MainWindow
    {
        private VisualTestRunner _visualTestRunner;
        private VisualTestPlan _visualTestPlan;
        private bool _visualTestStarted;
        private bool _visualTestInputLocked;
        private bool _visualTestSuppressDialogsBefore;
        private string _visualTestPendingError;
        private bool _visualTestPausedByMinimize;
        private readonly Dictionary<string, WorkingArea> _visualAreaAliases =
            new(StringComparer.Ordinal);
        private readonly Queue<(string Contains, bool Yes)> _visualTestConfirmResponses = new();
        private readonly Dictionary<string, string> _visualProjectCheckpoints =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, int>> _visualProjectAliasIds =
            new(StringComparer.OrdinalIgnoreCase);

        private void VisualTest_Click(object sender, RoutedEventArgs e)
        {
            if (_visualTestInputLocked || VisualTestSession.Current != null) return;

            var launcher = new VisualTestLauncherWindow { Owner = this };
            if (launcher.ShowDialog() == true)
                StatusText.Text = "시각 테스트를 새 창에서 시작했습니다 — " + launcher.LaunchedRunDirectory;
        }

        private void StartVisualTestIfConfigured()
        {
            if (_visualTestStarted) return;
            VisualTestSession session = VisualTestSession.Current;
            if (session == null) return;
            _visualTestStarted = true;

            _visualTestPlan = VisualScenarioCompiler.Compile(session.Scenario);
            _visualAreaAliases.Clear();

            _visualTestInputLocked = true;
            Viewport.InputLocked = true;
            VisualTestInputShield.Visibility = Visibility.Visible;
            VisualTestBar.Visibility = Visibility.Visible;
            _visualTestConfirmResponses.Clear();

            _visualTestSuppressDialogsBefore = ErrorReporter.SuppressDialogs;
            ErrorReporter.SuppressDialogs = true;
            ErrorReporter.Reported += OnVisualTestErrorReported;

            _visualTestRunner = new VisualTestRunner(
                _visualTestPlan.Steps.Select(step => step.Name).ToArray(),
                ExecuteVisualStepAsync,
                session.Report,
                session.Options.HoldSeconds,
                session.Options.StepMode,
                TakeVisualTestReportedError);
            _visualTestRunner.ProgressChanged += OnVisualTestProgress;
            _visualTestRunner.Finished += OnVisualTestFinished;
            StateChanged += OnVisualTestWindowStateChanged;
            _visualTestRunner.Start();
            if (WindowState == WindowState.Minimized)
            {
                _visualTestPausedByMinimize = true;
                _visualTestRunner.Pause();
            }
        }

        private void OnVisualTestWindowStateChanged(object sender, EventArgs e)
        {
            if (_visualTestRunner == null || _visualTestRunner.IsFinished) return;
            if (WindowState == WindowState.Minimized)
            {
                if (!_visualTestRunner.IsPaused)
                {
                    _visualTestPausedByMinimize = true;
                    _visualTestRunner.Pause();
                }
            }
            else if (_visualTestPausedByMinimize)
            {
                _visualTestPausedByMinimize = false;
                _visualTestRunner.Resume();
            }
        }

        private void OnVisualTestErrorReported(string context, ErrorLevel level)
        {
            // 단계 실행 중 오류가 접수되면 다음 단계로 넘어가지 않는다.
            // 실제 모달 표시는 SuppressDialogs로 막고, 실패 사유만 보관한다.
            if (_visualTestPendingError == null)
                _visualTestPendingError = "[" + level + "] " + context;
        }

        private string TakeVisualTestReportedError()
        {
            string error = _visualTestPendingError;
            _visualTestPendingError = null;
            return error;
        }

        private void OnVisualTestProgress(VisualTestRunnerProgress progress)
        {
            int total = Math.Max(1, progress.StepCount);
            int current = Math.Min(total, progress.StepIndex + 1);
            VisualTestStepText.Text = string.Format("시각 테스트 {0}/{1} · {2}", current, total, progress.StepName);
            VisualTestDetailText.Text = progress.Detail;
            VisualTestProgress.Minimum = 0;
            VisualTestProgress.Maximum = 100;
            VisualTestProgress.Value = 100.0 * progress.CompletedSteps / total;

            bool terminal = progress.State == VisualTestRunnerState.Completed
                || progress.State == VisualTestRunnerState.Failed
                || progress.State == VisualTestRunnerState.Stopped;
            VisualTestPauseButton.IsEnabled = !terminal;
            VisualTestPauseButton.Content = _visualTestRunner != null && _visualTestRunner.IsPaused
                ? "계속" : "일시정지";
            VisualTestNextButton.IsEnabled = !terminal && progress.CanNext
                && _visualTestRunner != null && !_visualTestRunner.IsPaused;
            VisualTestStopButton.IsEnabled = !terminal;
        }

        private void OnVisualTestFinished(VisualTestRunResult result)
        {
            _visualTestInputLocked = false;
            Viewport.InputLocked = false;
            VisualTestInputShield.Visibility = Visibility.Collapsed;
            // 실행이 끝나면 응답 큐는 비워지므로 이후 확인 요청은 제품 대화상자로 처리한다.
            // (VisualTestAutomationActive 참고)
            _visualTestConfirmResponses.Clear();
            StateChanged -= OnVisualTestWindowStateChanged;
            ErrorReporter.Reported -= OnVisualTestErrorReported;
            ErrorReporter.SuppressDialogs = _visualTestSuppressDialogsBefore;
            _visualAreaAliases.Clear();

            string status = result.Status == "Completed" ? "자동 절차 완료 — 화면을 직접 확인하세요"
                : result.Status == "Stopped" ? "사용자 중단 — 현재 화면을 유지합니다"
                : "자동 절차 실패 — " + result.Error;
            VisualTestStepText.Text = "시각 테스트 · " + result.Status;
            VisualTestDetailText.Text = status;
            VisualTestPauseButton.IsEnabled = false;
            VisualTestNextButton.IsEnabled = false;
            VisualTestStopButton.IsEnabled = false;
            if (result.Status == "Completed") VisualTestProgress.Value = 100;
        }

        private void VisualTestPause_Click(object sender, RoutedEventArgs e)
        {
            if (_visualTestRunner == null || _visualTestRunner.IsFinished) return;
            if (_visualTestRunner.IsPaused) _visualTestRunner.Resume();
            else _visualTestRunner.Pause();
        }

        private void VisualTestNext_Click(object sender, RoutedEventArgs e)
        {
            _visualTestRunner?.RequestNext();
        }

        private void VisualTestStop_Click(object sender, RoutedEventArgs e)
        {
            _visualTestRunner?.Stop();
        }

        private async Task<VisualStepOutcome> ExecuteVisualStepAsync(int index, CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (_visualTestPlan == null)
                    return VisualStepOutcome.Fail("시각테스트 계획이 없습니다.");

                Require(index >= 0 && index < _visualTestPlan.Steps.Count,
                    "알 수 없는 단계 번호입니다: " + index);
                return await ExecutePlanStepAsync(_visualTestPlan.Steps[index]);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return VisualStepOutcome.Fail(ex.Message);
            }
        }

        // ---- v2: 시나리오 행동 실행 ----

        private async Task<VisualStepOutcome> ExecutePlanStepAsync(VisualPlanStep step)
        {
            VisualScenario scenario = RequireScenario();
            switch (step.Action)
            {
                case "loadModel": return StepLoadModel();
                case "selectFaces": return VisualSelectFaces(step.Spec, replace: true);
                case "toggleFaces": return VisualSelectFaces(step.Spec, replace: false);
                case "expectSelection": return VisualExpectSelection(step.Spec);
                case "clearSelection": return VisualClearSelection(step.Spec);
                case "confirmArea": return VisualConfirmArea(step.Spec, scenario);
                case "activateArea": return VisualActivateArea(step.Spec);
                case "deleteArea": return VisualDeleteArea(step.Spec);
                case "setAreaInputs": return VisualSetAreaInputs(step.Spec, scenario);
                case "expectAreaInputs": return VisualExpectAreaInputs(step.Spec, scenario);
                case "pickExitPoint": return VisualPickExitPoint(step.Spec);
                case "generate": return VisualGenerate(step.Spec, scenario);
                case "checkArea": return VisualCheckArea(step.Spec, scenario);
                case "checkElectrical": return VisualCheckElectrical(step.Spec);
                case "setElectrical": return VisualSetElectrical(step.Spec);
                case "splitArea": return VisualSplitArea(step.Spec, scenario);
                case "checkAllAreas": return VisualCheckAllAreas(step.Spec, scenario);
                case "addExclusion": return VisualAddExclusion(step.Spec, scenario);
                case "removeExclusion": return VisualRemoveExclusion(step.Spec);
                case "expectNoPattern": return VisualExpectNoPattern(step.Spec);
                case "checkExclusion": return VisualCheckExclusion(step.Spec);
                case "moveVertex": return VisualMoveVertex(step.Spec);
                case "exportDxf": return await VisualExportDxfAsync(step.Spec);
                case "exportStep": return VisualExportStep(step.Spec);
                case "saveProject": return VisualSaveProject(step.Spec);
                case "loadProject": return VisualLoadProject(step.Spec);
                default:
                    return VisualStepOutcome.Fail("지원하지 않는 행동입니다: " + step.Action);
            }
        }

        private VisualStepOutcome VisualSelectFaces(VisualActionSpec spec, bool replace)
        {
            Require(spec.Faces != null && spec.Faces.Length > 0, "선택할 면 목록이 비어 있습니다.");
            SidebarTabs.SelectedIndex = AreaTabIndex;

            var requested = new HashSet<int>(spec.Faces);
            if (!replace)
            {
                requested = new HashSet<int>(_selectedFaceIds);
                foreach (int faceId in spec.Faces)
                {
                    if (!requested.Remove(faceId)) requested.Add(faceId);
                }
            }

            ClearFaceSelectionOnly();
            if (replace)
            {
                int[] applied = Bridge.SelectFaceIds(spec.Faces);
                Require(applied != null, "네이티브 면 선택을 적용하지 못했습니다.");
                foreach (int faceId in applied)
                    OnFacePicked(faceId, true);
                Require(_selectedFaceIds.SetEquals(requested),
                    "네이티브 선택과 관리 선택이 다릅니다.");
            }
            else
            {
                int[] applied = Bridge.SelectFaceIds(requested.ToArray());
                Require(applied != null, "네이티브 면 선택을 적용하지 못했습니다.");
                foreach (int faceId in applied)
                    OnFacePicked(faceId, true);
                Require(_selectedFaceIds.SetEquals(requested),
                    "토글 후 선택 집합이 기대와 다릅니다.");
            }

            int[] native = Bridge.GetSelectedFaceIds();
            Require(native != null && native.OrderBy(v => v).SequenceEqual(requested.OrderBy(v => v)),
                "네이티브 선택 집합이 요청과 다릅니다.");

            var data = new Dictionary<string, string>
            {
                ["selection"] = string.Join(",", requested.OrderBy(v => v))
            };
            return VisualStepOutcome.Ok(replace ? "면 선택 교체" : "면 선택 토글", data);
        }

        private VisualStepOutcome VisualExpectSelection(VisualActionSpec spec)
        {
            var expected = new HashSet<int>(spec.Faces ?? Array.Empty<int>());
            Require(_selectedFaceIds.SetEquals(expected),
                "관리 선택 집합이 기대와 다릅니다. 기대 [" + string.Join(",", expected.OrderBy(v => v)) +
                "] 실제 [" + string.Join(",", _selectedFaceIds.OrderBy(v => v)) + "]");
            int[] native = Bridge.GetSelectedFaceIds();
            Require(native != null && native.OrderBy(v => v).SequenceEqual(expected.OrderBy(v => v)),
                "네이티브 선택 집합이 기대와 다릅니다.");
            int confirmed = _areas.Count;
            var data = new Dictionary<string, string>
            {
                ["selection"] = string.Join(",", expected.OrderBy(v => v)),
                ["nativeSelection"] = string.Join(",", (native ?? Array.Empty<int>()).OrderBy(v => v)),
                ["areas"] = confirmed.ToString(CultureInfo.InvariantCulture)
            };
            return VisualStepOutcome.Ok("선택 집합 확인", data);
        }

        private VisualStepOutcome VisualClearSelection(VisualActionSpec spec)
        {
            SidebarTabs.SelectedIndex = AreaTabIndex;
            ClearFaceSelectionOnly();
            Require(_selectedFaceIds.Count == 0, "관리 선택이 비워지지 않았습니다.");
            int[] native = Bridge.GetSelectedFaceIds();
            Require(native != null && native.Length == 0, "네이티브 선택이 비워지지 않았습니다.");
            return VisualStepOutcome.Ok("선택 해제", new Dictionary<string, string> { ["selection"] = "" });
        }

        private VisualStepOutcome VisualConfirmArea(VisualActionSpec spec, VisualScenario scenario)
        {
            VisualAreaDefinition definition = RequireAreaDefinition(scenario, spec.Area);
            SidebarTabs.SelectedIndex = AreaTabIndex;
            var faces = new HashSet<int>(definition.FaceIds);
            Require(_selectedFaceIds.SetEquals(faces),
                "확정할 면 선택이 영역 정의와 다릅니다.");

            int areasBefore = _areas.Count;
            ConfirmWorkingArea();
            Require(_areas.Count == areasBefore + 1, "작업영역이 추가되지 않았습니다.");
            WorkingArea area = _activeArea;
            Require(area != null, "활성 작업영역이 없습니다.");
            Require(area.FaceIdSet.SetEquals(faces), "확정된 영역의 면 집합이 정의와 다릅니다.");
            Require(area.Region.Count == 1, "작업영역 경계 루프가 1개가 아닙니다.");

            _visualAreaAliases[definition.Alias] = area;
            var data = new Dictionary<string, string>
            {
                ["alias"] = definition.Alias,
                ["areaName"] = area.Name,
                ["faceIds"] = string.Join(",", area.FaceIdSet.OrderBy(v => v)),
                ["areaCount"] = _areas.Count.ToString(CultureInfo.InvariantCulture)
            };
            return VisualStepOutcome.Ok("영역 확정", data);
        }

        private VisualStepOutcome VisualActivateArea(VisualActionSpec spec)
        {
            WorkingArea area = ResolveVisualArea(spec.Area);
            Require(TryCaptureAllAreaRoutes(out var routesBefore, out string captureError), captureError);
            ActivateArea(area);
            RefreshAreaList();
            Require(ReferenceEquals(_activeArea, area), "영역 전환이 적용되지 않았습니다.");
            RequireAllRoutesPreserved(routesBefore, "영역 전환");
            var data = new Dictionary<string, string>
            {
                ["alias"] = spec.Area,
                ["areaName"] = area.Name,
                ["direction"] = ((int)area.Direction).ToString(CultureInfo.InvariantCulture)
            };
            return VisualStepOutcome.Ok("영역 전환", data);
        }

        private VisualStepOutcome VisualDeleteArea(VisualActionSpec spec)
        {
            WorkingArea area = ResolveVisualArea(spec.Area);
            ActivateArea(area);
            RefreshAreaList();
            int areasBefore = _areas.Count;
            Require(TryCaptureAllAreaRoutes(out var routesBefore, out string captureError),
                "영역 삭제 전 확정 경로를 읽지 못했습니다: " + captureError);
            DeleteSelectedArea_Click(this, new RoutedEventArgs());
            Require(_areas.Count == areasBefore - 1, "작업영역이 삭제되지 않았습니다.");
            Require(!_areas.Contains(area), "삭제한 영역이 목록에 남아 있습니다.");
            Require(!_areas.Contains(_activeArea) == false || _activeArea == null || _areas.Contains(_activeArea),
                "활성 영역이 목록 밖을 가리킵니다.");
            _visualAreaAliases.Remove(spec.Area);
            RequireSiblingRoutesPreserved(routesBefore, area, "영역 삭제");

            int remainingPieces = _areas.Where(a => a.Pattern != null).Sum(a => a.Pattern.RoutePieces.Count);
            int expectedMarkers = 2 * _areas.Count(a => a.Pattern != null && a.Pattern.RoutePieces.Count > 0);
            Dictionary<string, int> diagnostic = ParseDisplayDiagnostic(Bridge.GetDisplayDiagnostic());
            Require(NativeCount(diagnostic, "routes") == remainingPieces,
                "삭제 후 네이티브 경로 조각 수가 남은 영역과 다릅니다.");
            Require(NativeCount(diagnostic, "markers") == expectedMarkers,
                "삭제 후 네이티브 끝점 마커 수가 남은 영역과 다릅니다.");

            var data = new Dictionary<string, string>
            {
                ["alias"] = spec.Area,
                ["areaCount"] = _areas.Count.ToString(CultureInfo.InvariantCulture),
                ["nativeRoutes"] = NativeCount(diagnostic, "routes").ToString(CultureInfo.InvariantCulture),
                ["nativeMarkers"] = NativeCount(diagnostic, "markers").ToString(CultureInfo.InvariantCulture)
            };
            return VisualStepOutcome.Ok("영역 삭제·네이티브 표시 정리 확인", data);
        }

        private VisualStepOutcome VisualSetAreaInputs(VisualActionSpec spec, VisualScenario scenario)
        {
            VisualAreaDefinition definition = RequireAreaDefinition(scenario, spec.Area);
            WorkingArea area = ResolveVisualArea(spec.Area);
            ActivateArea(area);
            RefreshAreaList();
            SidebarTabs.SelectedIndex = PatternTabIndex;

            double margin = spec.MarginMm ?? definition.MarginMm;
            double spacing = spec.SpacingMm ?? definition.SpacingMm;
            int direction = spec.Direction ?? definition.Direction;

            DirectionBox.SelectedIndex = IndexFromDirection((PatternDirection)direction);
            MarginBox.Text = margin.ToString("0.###", CultureInfo.CurrentCulture);
            SpacingBox.Text = spacing.ToString("0.###", CultureInfo.CurrentCulture);

            Require(TryGetParams(out double readMargin, out double readSpacing, out string error),
                "마진/간격 입력을 읽지 못했습니다: " + error);
            Require(Math.Abs(readMargin - margin) < 1e-9, "적용된 마진이 요청과 다릅니다.");
            Require(Math.Abs(readSpacing - spacing) < 1e-9, "적용된 간격이 요청과 다릅니다.");
            Require(area.Direction == EffectiveDirection((PatternDirection)direction), "영역 방향이 요청과 다릅니다.");
            Require(IsPatternInputStale(area) == (area.Pattern != null),
                "입력 변경 후 stale 상태가 예상과 다릅니다.");

            var data = new Dictionary<string, string>
            {
                ["alias"] = spec.Area,
                ["marginMm"] = readMargin.ToString("0.###", CultureInfo.InvariantCulture),
                ["spacingMm"] = readSpacing.ToString("0.###", CultureInfo.InvariantCulture),
                ["direction"] = direction.ToString(CultureInfo.InvariantCulture),
                ["stale"] = IsPatternInputStale(area).ToString()
            };
            return VisualStepOutcome.Ok("영역 조건 입력", data);
        }

        private VisualStepOutcome VisualExpectAreaInputs(VisualActionSpec spec, VisualScenario scenario)
        {
            VisualAreaDefinition definition = RequireAreaDefinition(scenario, spec.Area);
            WorkingArea area = ResolveVisualArea(spec.Area);
            Require(TryCaptureAllAreaRoutes(out var routesBefore, out string captureError), captureError);
            ActivateArea(area);
            RefreshAreaList();

            double expectedMargin = spec.MarginMm ?? definition.MarginMm;
            double expectedSpacing = spec.SpacingMm ?? definition.SpacingMm;
            int expectedDirection = spec.Direction ?? definition.Direction;

            Require(TryGetParams(out double margin, out double spacing, out string error),
                "마진/간격 초안을 읽지 못했습니다: " + error);
            Require(Math.Abs(margin - expectedMargin) < 1e-9,
                string.Format("영역 초안 마진이 기대와 다릅니다. 기대 {0}, 실제 {1}", expectedMargin, margin));
            Require(Math.Abs(spacing - expectedSpacing) < 1e-9,
                string.Format("영역 초안 간격이 기대와 다릅니다. 기대 {0}, 실제 {1}", expectedSpacing, spacing));
            Require(area.Direction == (PatternDirection)expectedDirection,
                "영역 방향이 기대와 다릅니다.");

            bool appliedDiffers = area.AppliedInputs != null &&
                (Math.Abs(margin - area.AppliedInputs.Margin) > 1e-9 ||
                 Math.Abs(spacing - area.AppliedInputs.Spacing) > 1e-9 ||
                 area.Direction != area.AppliedInputs.Direction);
            bool stale = IsPatternInputStale(area);
            Require(stale == appliedDiffers, "입력 초안과 생성 결과의 stale 상태가 일치하지 않습니다.");
            if (stale)
            {
                Require(area.Pattern != null, "패턴 없이 stale 상태일 수 없습니다.");
                Require(area.PatternDisplayed && area.Pattern.RoutePieces.Count > 0,
                    "조건이 바뀐 뒤에도 이전 경로가 화면에 유지되어야 합니다.");
            }

            if (spec.Expect?.MarginMm != null || spec.Expect?.SpacingMm != null)
            {
                Require(area.AppliedInputs != null, "적용된 생성 조건이 없습니다.");
                if (spec.Expect.MarginMm != null)
                    Require(Math.Abs(area.AppliedInputs.Margin - spec.Expect.MarginMm.Value) < 1e-9,
                        string.Format("마지막 생성에 적용된 마진이 다릅니다. 기대 {0}, 실제 {1}",
                            spec.Expect.MarginMm.Value, area.AppliedInputs.Margin));
                if (spec.Expect.SpacingMm != null)
                    Require(Math.Abs(area.AppliedInputs.Spacing - spec.Expect.SpacingMm.Value) < 1e-9,
                        string.Format("마지막 생성에 적용된 간격이 다릅니다. 기대 {0}, 실제 {1}",
                            spec.Expect.SpacingMm.Value, area.AppliedInputs.Spacing));
            }

            var data = new Dictionary<string, string>
            {
                ["alias"] = spec.Area,
                ["marginMm"] = margin.ToString("0.###", CultureInfo.InvariantCulture),
                ["spacingMm"] = spacing.ToString("0.###", CultureInfo.InvariantCulture),
                ["direction"] = ((int)area.Direction).ToString(CultureInfo.InvariantCulture),
                ["stale"] = stale.ToString(),
                ["displayed"] = area.PatternDisplayed.ToString(),
                ["appliedMarginMm"] = area.AppliedInputs?.Margin.ToString("0.###", CultureInfo.InvariantCulture) ?? "",
                ["appliedSpacingMm"] = area.AppliedInputs?.Spacing.ToString("0.###", CultureInfo.InvariantCulture) ?? ""
            };
            RequireAllRoutesPreserved(routesBefore, "영역 입력 초안 복원");
            return VisualStepOutcome.Ok("영역 입력 초안·stale 상태 확인", data);
        }

        private VisualStepOutcome VisualGenerate(VisualActionSpec spec, VisualScenario scenario)
        {
            // 자동분할로 새로 생긴 영역은 시나리오의 areas에 정의가 없다. 그 영역은
            // ActivateArea가 복원한 상속 입력을 그대로 쓰고, 시나리오가 값을 지정한 경우에만
            // 덮어쓴다. 제품의 생성 경로는 두 경우 모두 같다.
            VisualAreaDefinition definition = scenario.Areas.FirstOrDefault(a => a.Alias == spec.Area);
            WorkingArea area = ResolveVisualArea(spec.Area);
            ActivateArea(area);
            RefreshAreaList();
            SidebarTabs.SelectedIndex = PatternTabIndex;
            ConfigurePatternInputs(spec, definition);
            Require(TryGetParams(out _, out _, out string parameterError),
                "마진/간격 입력을 읽지 못했습니다: " + parameterError);

            AppliedGenerationInputs appliedBefore = area.AppliedInputs;
            string statusBefore = StatusText.Text;
            Require(TryCaptureAllAreaRoutes(out var routesBefore, out string captureError),
                "생성 전 확정 경로를 읽지 못했습니다: " + captureError);
            QueueVisualTestConfirm(spec);
            Generate_Click(this, new RoutedEventArgs());
            RequireNoPendingVisualTestConfirms();

            bool success = !ReferenceEquals(appliedBefore, area.AppliedInputs)
                && area.Pattern != null
                && area.PatternDisplayed
                && area.AppliedInputs != null
                && !IsPatternInputStale(area);

            if (string.Equals(spec.Outcome, "rejected", StringComparison.OrdinalIgnoreCase))
            {
                Require(!success, "거부를 기대했지만 생성이 성공했습니다.");
                Require(ReferenceEquals(appliedBefore, area.AppliedInputs),
                    "거부된 생성이 적용 조건을 바꿨습니다.");
                string rejectedPreservation = RequireAreaRoutesUnchanged(routesBefore, null, "거부된 생성");
                Require(rejectedPreservation == null, rejectedPreservation);
                if (!string.IsNullOrEmpty(spec.FailureContains))
                    Require((StatusText.Text ?? "").Contains(spec.FailureContains),
                        "거부 사유가 기대 문구를 포함하지 않습니다: " + StatusText.Text);
                var rejected = new Dictionary<string, string>
                {
                    ["alias"] = spec.Area,
                    ["outcome"] = "rejected",
                    ["status"] = StatusText.Text ?? "",
                    ["statusBefore"] = statusBefore ?? ""
                };
                return VisualStepOutcome.Ok("생성 거부 확인", rejected);
            }

            Require(success, "발열경로 생성이 완료되지 않았습니다: " + StatusText.Text);
            RequireSiblingRoutesPreserved(routesBefore, area, "발열경로 생성");
            VisualAreaExpect expect = spec.Expect ?? definition?.Expect;
            string error = CheckAreaState(area, expect, out Dictionary<string, string> data);
            if (error != null) return VisualStepOutcome.Fail(error);
            return VisualStepOutcome.Ok("발열경로 생성·검증", data);
        }

        private VisualStepOutcome VisualCheckArea(VisualActionSpec spec, VisualScenario scenario)
        {
            VisualAreaDefinition definition = scenario.Areas.FirstOrDefault(a => a.Alias == spec.Area);
            WorkingArea area = ResolveVisualArea(spec.Area);
            Require(TryCaptureAllAreaRoutes(out var routesBefore, out string captureError), captureError);
            string error = CheckAreaState(area, spec.Expect ?? definition?.Expect, out Dictionary<string, string> data);
            if (error != null) return VisualStepOutcome.Fail(error);
            RequireAllRoutesPreserved(routesBefore, "영역 상태 확인");
            data["alias"] = spec.Area;
            return VisualStepOutcome.Ok("영역 상태 확인", data);
        }

        private VisualStepOutcome VisualCheckAllAreas(VisualActionSpec spec, VisualScenario scenario)
        {
            SidebarTabs.SelectedIndex = ReviewTabIndex;
            SetVisualReviewScope(0);
            Require(TryCaptureAllAreaRoutes(out var routesBefore, out string captureError), captureError);
            Require(TryReadElectricalLimits(out TargetPowerElectricalSettings settings, out string settingsError),
                "전기 입력을 읽지 못했습니다: " + settingsError);
            HeatingWireSpec wire = settings.Wire;

            var pieces = 0;
            var turns = 0;
            var lengthTotal = 0.0;
            var checkedAliases = new List<string>();
            var seenAliases = new HashSet<string>(StringComparer.Ordinal);

            foreach (WorkingArea area in _areas.ToList())
            {
                string alias = _visualAreaAliases.FirstOrDefault(pair => ReferenceEquals(pair.Value, area)).Key;
                Require(!string.IsNullOrWhiteSpace(alias),
                    "현재 영역에 대응하는 시나리오 alias가 없습니다: " + area.Name);
                Require(seenAliases.Add(alias), "같은 alias가 여러 영역에 연결되었습니다: " + alias);
                string error = CheckAreaState(area, null, out Dictionary<string, string> areaData);
                if (error != null)
                    return VisualStepOutcome.Fail("영역 " + alias + ": " + error);
                checkedAliases.Add(alias);
                pieces += area.Pattern.RoutePieces.Count;
                turns += area.Pattern.RoutePieces.Count(p => p.Kind == SerpentineRoutePieceKind.TurnConnector);
                lengthTotal += area.RoutePieceLengths.Sum();
            }

            Require(_visualAreaAliases.Count == _areas.Count,
                "영역 식별자 매핑 수가 현재 영역 수와 다릅니다. 매핑 " + _visualAreaAliases.Count + ", 영역 " + _areas.Count);

            Dictionary<string, int> diagnostic = ParseDisplayDiagnostic(Bridge.GetDisplayDiagnostic());
            Require(NativeCount(diagnostic, "routes") == pieces, "네이티브 경로 조각 수가 관리 상태와 다릅니다.");
            Require(NativeCount(diagnostic, "displayed") == pieces,
                string.Format("화면에 표시된 네이티브 조각 수가 다릅니다. 표시 {0}, 기대 {1} (전체 {2})",
                    NativeCount(diagnostic, "displayed"), pieces, pieces));
            Require(NativeCount(diagnostic, "connectors") == turns, "네이티브 연결구간 수가 다릅니다.");
            Require(NativeCount(diagnostic, "enabled") == pieces, "네이티브 활성 조각 수가 다릅니다.");
            Require(NativeCount(diagnostic, "markers") == 2 * _areas.Where(a => a.Pattern != null && a.Pattern.RoutePieces.Count > 0).Sum(a => Math.Max(1, a.Circuits.Count)),
                "발열경로 끝점 마커 수가 영역 수와 다릅니다.");

            ReviewSnapshot snapshot = BuildReviewSnapshot();
            Require(snapshot.RequestedCount == _areas.Count, "전체 검토 범위에 모든 영역이 포함되지 않았습니다.");
            Require(snapshot.CanExport, "전체 영역 내보내기가 불가능합니다: " + snapshot.BlockReason);
            Require(snapshot.Entries.All(entry => entry.State == ReviewAreaState.Ready),
                "준비되지 않은 작업영역이 있습니다: " +
                string.Join(", ", snapshot.Entries.Where(e => e.State != ReviewAreaState.Ready).Select(e => e.Area.Name + "=" + e.State)));
            Require(snapshot.Table.Rows.Count == snapshot.Circuits.Count, "전기 사양표 행 수가 다릅니다.");
            Require(ElectricalSpecGrid.Items.Count == snapshot.Table.Rows.Count + 1, "화면 표 행 수가 다릅니다.");

            foreach (ElectricalSpecRow row in snapshot.Table.Rows)
            {
                CircuitLengthInfo circuit = snapshot.Circuits.FirstOrDefault(c => c.CircuitId == row.CircuitId);
                Require(circuit != null, "전기표 회로 ID에 대응하는 회로가 없습니다: " + row.CircuitId);
                Require(circuit.TargetPowerW.HasValue,
                    "회로에 배분된 발열전력이 없습니다: " + row.CircuitId);
                ElectricalResult expected = ElectricalCalculator.ComputeFromTargetPower(
                    circuit.HeatingLengthMm, wire, circuit.TargetPowerW.Value);
                Require(expected.IsValid, "회로 계산이 유효하지 않습니다: " + row.CircuitId);
                Require(Math.Abs(row.LengthM - expected.LengthM) < 1e-6, "회로 길이가 계산값과 다릅니다: " + row.CircuitId);
                Require(Math.Abs(row.CurrentA - expected.CurrentA) < 1e-6, "전류가 계산값과 다릅니다: " + row.CircuitId);
                Require(Math.Abs(row.PowerW - expected.PowerW) < 1e-3, "전력이 계산값과 다릅니다: " + row.CircuitId);
            }

            var data = new Dictionary<string, string>
            {
                ["areas"] = _areas.Count.ToString(CultureInfo.InvariantCulture),
                ["aliases"] = string.Join(",", checkedAliases),
                ["pieces"] = pieces.ToString(CultureInfo.InvariantCulture),
                ["turns"] = turns.ToString(CultureInfo.InvariantCulture),
                ["circuits"] = snapshot.Circuits.Count.ToString(CultureInfo.InvariantCulture),
                ["totalLengthMm"] = lengthTotal.ToString("0.###", CultureInfo.InvariantCulture),
                ["native"] = Bridge.GetDisplayDiagnostic() ?? ""
            };
            RequireAllRoutesPreserved(routesBefore, "전체 영역 검사");
            return VisualStepOutcome.Ok("전체 영역·전기 내보내기 가능 확인", data);
        }

        private VisualStepOutcome VisualPickExitPoint(VisualActionSpec spec)
        {
            VisualExitPointSpec requested = spec.ExitPoint;
            Require(requested != null, "exitPoint 정의가 없습니다.");
            int role = requested.Role == "start" ? 0 : 1;
            WorkingArea area = ResolveVisualArea(spec.Area);
            ActivateArea(area);
            SidebarTabs.SelectedIndex = PatternTabIndex;
            Require(TryCaptureAllAreaRoutes(out var routesBefore, out string captureError),
                "인출점 선택 전 확정 경로를 읽지 못했습니다: " + captureError);

            BeginExitPointPick(role);
            Require(_exitPointPickRole == role,
                "인출점 선택 모드를 시작하지 못했습니다: " + StatusText.Text);

            var datum = new Vec2(requested.Uv[0], requested.Uv[1]);
            if (!TryResolveExitPoint(area, datum, out ExitBoundaryPoint point, out string resolveError))
            {
                CancelExitFacePick();
                return VisualStepOutcome.Fail("인출점을 영역 경계에 투영하지 못했습니다: " + resolveError);
            }
            double distance = ExitUvDistance(point.Point, datum);
            if (distance > requested.ToleranceMm)
            {
                CancelExitFacePick();
                return VisualStepOutcome.Fail(string.Format(CultureInfo.InvariantCulture,
                    "인출점이 요청 좌표에서 {0:0.###} mm 떨어져 있습니다(허용 {1:0.###} mm).",
                    distance, requested.ToleranceMm));
            }
            if (!TryEvaluateSelectedExit3D(area, point, out Vec3 position, out string surfaceError))
            {
                CancelExitFacePick();
                return VisualStepOutcome.Fail("인출점의 3D 표면 위치를 평가하지 못했습니다: " + surfaceError);
            }
            Require(point.Binding != null || point.ArtificialBinding != null || point.SplitBinding != null,
                "인출점에 경계 연결 정보가 없습니다.");
            string bindingKind = DescribeBindingKind(point);
            string expectedKind = string.IsNullOrWhiteSpace(requested.BindingKind)
                ? "any" : requested.BindingKind.Trim().ToLowerInvariant();
            Require(expectedKind == "any" || expectedKind == bindingKind,
                "인출점 경계 연결 종류가 기대와 다릅니다. 기대 " + expectedKind + ", 실제 " + bindingKind);
            ApplyPickedExitPoint(area, role, point);
            Require(_exitPointPickRole < 0 && !Viewport.ExitFacePickMode,
                "인출점 선택 모드가 종료되지 않았습니다.");

            ExitBoundaryPoint? applied = role == 0 ? area.SelectedStartExit : area.SelectedEndExit;
            Require(applied.HasValue, "선택 인출점이 영역에 저장되지 않았습니다.");
            Require(ExitUvDistance(applied.Value.Point, point.Point) <= 1e-6
                && applied.Value.SegmentIndex == point.SegmentIndex
                && Math.Abs(applied.Value.SegmentParameter - point.SegmentParameter) <= 1e-9,
                "저장된 인출점이 투영 결과와 다릅니다.");
            RequireSiblingRoutesPreserved(routesBefore, area, "인출점 선택");

            var data = new Dictionary<string, string>
            {
                ["role"] = requested.Role,
                ["u"] = point.Point.X.ToString("0.######", CultureInfo.InvariantCulture),
                ["v"] = point.Point.Y.ToString("0.######", CultureInfo.InvariantCulture),
                ["segment"] = point.SegmentIndex.ToString(CultureInfo.InvariantCulture),
                ["parameter"] = point.SegmentParameter.ToString("0.############", CultureInfo.InvariantCulture),
                ["surfaceX"] = position.X.ToString("0.###", CultureInfo.InvariantCulture),
                ["surfaceY"] = position.Y.ToString("0.###", CultureInfo.InvariantCulture),
                ["surfaceZ"] = position.Z.ToString("0.###", CultureInfo.InvariantCulture),
                ["binding"] = DescribeExitBinding(point),
                ["bindingKind"] = bindingKind
            };
            return VisualStepOutcome.Ok("직접 인출점 선택", data);
        }

        private VisualStepOutcome VisualSetElectrical(VisualActionSpec spec)
        {
            VisualElectricalSpec requested = spec.Electrical;
            Require(requested != null, "electrical 정의가 없습니다.");
            SidebarTabs.SelectedIndex = ReviewTabIndex;
            ApplyElectricalInputs(requested);

            Require(TryReadElectricalLimits(out TargetPowerElectricalSettings settings, out string settingsError),
                "전기 입력을 읽지 못했습니다: " + settingsError);
            HeatingWireSpec wire = settings.Wire;
            ElectricalLimits limits = settings.Limits;
            Require(limits != null, "전기 허용치를 읽지 못했습니다.");
            Require(Near(limits.PowerLimitW, requested.AllowedPowerKw * 1000.0, 1e-6),
                "제품에 적용된 전체 발열전력이 시나리오와 다릅니다.");
            Require(Near(limits.VoltageLimitV, requested.VoltageV, 1e-9),
                "제품에 적용된 허용 전압이 시나리오와 다릅니다.");
            Require(Near(limits.CurrentLimitA, requested.AllowedCurrentA, 1e-9),
                "제품에 적용된 허용 전류가 시나리오와 다릅니다.");

            var data = new Dictionary<string, string>
            {
                ["preset"] = requested.PresetIndex.ToString(CultureInfo.InvariantCulture),
                ["voltageLimitV"] = limits.VoltageLimitV.ToString("0.###", CultureInfo.InvariantCulture),
                ["currentLimitA"] = limits.CurrentLimitA.ToString("0.###", CultureInfo.InvariantCulture),
                ["powerLimitW"] = limits.PowerLimitW.ToString("0.###", CultureInfo.InvariantCulture),
                ["resistanceOhmPerM"] = wire.ResistanceOhmPerMeter.ToString("0.###", CultureInfo.InvariantCulture),
                ["reviewScope"] = requested.ReviewScope.ToString(CultureInfo.InvariantCulture)
            };
            return VisualStepOutcome.Ok("전기 입력 적용", data);
        }

        private void ApplyElectricalInputs(VisualElectricalSpec requested)
        {
            WirePresetBox.SelectedIndex = requested.PresetIndex;
            AllowedVoltageBox.Text = requested.VoltageV.ToString("0.###", CultureInfo.InvariantCulture);
            AllowedCurrentBox.Text = requested.AllowedCurrentA.ToString("0.###", CultureInfo.InvariantCulture);
            AllowedPowerBox.Text = requested.AllowedPowerKw.ToString("0.###", CultureInfo.InvariantCulture);
            ReviewScopeBox.SelectedIndex = 0;
            _reviewCurrentAreaOnly = false;
            RefreshElectricalTable();
        }

        private VisualStepOutcome VisualCheckElectrical(VisualActionSpec spec)
        {
            int entryScope = _reviewCurrentAreaOnly ? 1 : 0;
            Require(TryCaptureAllAreaRoutes(out var routesBefore, out string captureError), captureError);
            VisualStepOutcome outcome;
            try
            {
                outcome = CheckElectricalCore(spec, entryScope);
            }
            catch
            {
                // 실패해도 전기 배분은 되돌리지 않는다. 다만 검토 범위만 검사 전 상태로 돌려
                // 다음 단계가 예상하지 않은 범위를 물려받지 않게 한다.
                SetVisualReviewScope(entryScope);
                throw;
            }
            RequireAllRoutesPreserved(routesBefore, "전기 검사");
            return outcome;
        }

        /// <summary>제품의 검토 범위 전환 경로로 표시 범위를 바꾼다. 배분 자체는 바꾸지 않는다.</summary>
        private void SetVisualReviewScope(int scope)
        {
            ReviewScopeBox.SelectedIndex = scope == 1 ? 1 : 0;
            _reviewCurrentAreaOnly = ReviewScopeBox.SelectedIndex == 1;
            RefreshElectricalTable();
        }

        private VisualStepOutcome CheckElectricalCore(VisualActionSpec spec, int entryScope)
        {
            VisualElectricalSpec requested = spec.Electrical;
            SidebarTabs.SelectedIndex = ReviewTabIndex;
            if (requested != null && requested.Apply)
                ApplyElectricalInputs(requested);

            Require(TryReadElectricalLimits(out TargetPowerElectricalSettings settings, out string settingsError),
                "전기 입력을 읽지 못했습니다: " + settingsError);
            HeatingWireSpec wire = settings.Wire;
            ElectricalLimits limits = settings.Limits;
            Require(limits != null, "전기 허용치를 읽지 못했습니다.");
            if (requested != null)
            {
                Require(Near(limits.PowerLimitW, requested.AllowedPowerKw * 1000.0, 1e-6),
                    "제품에 적용된 전체 발열전력이 시나리오와 다릅니다.");
                Require(Near(limits.VoltageLimitV, requested.VoltageV, 1e-9),
                    "제품에 적용된 허용 전압이 시나리오와 다릅니다.");
                Require(Near(limits.CurrentLimitA, requested.AllowedCurrentA, 1e-9),
                    "제품에 적용된 허용 전류가 시나리오와 다릅니다.");
            }

            // 공유 전력 배분은 검토 범위와 무관하게 모든 확정 영역을 대상으로 한다.
            // 현재 영역 결과를 전체 결과로 오인하면 배분 합계를 전체 한도와 비교할 수 없다.
            SetVisualReviewScope(0);
            ReviewSnapshot global = BuildReviewSnapshot();
            Require(global.ElectricalError == null, "전기 입력 오류: " + global.ElectricalError);
            Require(global.Entries.Count > 0, "검토할 작업영역이 없습니다.");
            Require(global.Entries.All(entry => entry.State == ReviewAreaState.Ready),
                "준비되지 않은 작업영역이 있습니다: " +
                string.Join(", ", global.Entries.Where(e => e.State != ReviewAreaState.Ready)
                    .Select(e => e.Area.Name + "=" + e.State)));
            Require(global.CanExport, "전체 검토 결과를 내보낼 수 없습니다: " + global.BlockReason);
            Require(global.Circuits.Count > 0, "검토할 발열회로가 없습니다.");
            Require(global.Table != null, "전기 사양표가 없습니다.");
            Require(global.Table.Rows.Count == global.Circuits.Count,
                "전기 사양표 행 수가 회로 수와 다릅니다.");
            Require(Near(global.Table.TotalTargetPowerW, limits.PowerLimitW, 1e-6),
                "배분된 발열전력 합계가 전체 한도와 다릅니다.");
            Require(!global.Table.IsSharedPowerOverLimit,
                "배분된 발열전력 합계가 전체 한도를 넘었습니다.");

            double totalLengthM = global.Circuits.Sum(c => c.HeatingLengthMm) / 1000.0;
            Require(totalLengthM > 0, "회로 전체 길이가 0입니다.");
            double allocated = 0;
            var targetByCircuit = new Dictionary<string, double>(StringComparer.Ordinal);
            for (int i = 0; i < global.Circuits.Count; i++)
            {
                CircuitLengthInfo circuit = global.Circuits[i];
                double lengthM = circuit.HeatingLengthMm / 1000.0;
                double target = i == global.Circuits.Count - 1
                    ? limits.PowerLimitW - allocated
                    : limits.PowerLimitW * lengthM / totalLengthM;
                allocated += target;
                targetByCircuit[circuit.CircuitId] = target;

                ElectricalSpecRow row = global.Table.Rows.FirstOrDefault(r => r.CircuitId == circuit.CircuitId);
                Require(row != null, "전기 사양표에 회로 행이 없습니다: " + circuit.CircuitId);
                Require(row.IsValid, "회로 계산이 유효하지 않습니다: " + circuit.CircuitId + " (" + row.Note + ")");
                Require(row.TargetPowerW.HasValue, "회로에 배분된 발열전력이 없습니다: " + circuit.CircuitId);
                Require(Near(row.TargetPowerW.Value, target, 1e-6),
                    "길이비 배분 전력이 독립 계산과 다릅니다: " + circuit.CircuitId);
                Require(Near(row.LengthM, lengthM, 1e-9),
                    "회로 길이가 실제 3D 길이와 다릅니다: " + circuit.CircuitId);
                Require(Near(row.ResistanceOhm, wire.ResistanceOhmPerMeter * lengthM, 1e-9),
                    "회로 저항이 독립 계산과 다릅니다: " + circuit.CircuitId);

                double independentResistance = wire.ResistanceOhmPerMeter * lengthM;
                double independentVoltage = Math.Sqrt(target * independentResistance);
                double independentCurrent = Math.Sqrt(target / independentResistance);
                Require(Near(row.VoltageV, independentVoltage, 1e-6),
                    "회로 필요 전압이 독립 계산과 다릅니다: " + circuit.CircuitId);
                Require(Near(row.CurrentA, independentCurrent, 1e-9),
                    "회로 전류가 독립 계산과 다릅니다: " + circuit.CircuitId);
                Require(Near(row.PowerW, target, 1e-6),
                    "회로 전력이 배분 전력과 다릅니다: " + circuit.CircuitId);

                bool within = !(independentVoltage > limits.VoltageLimitV)
                    && !(independentCurrent > limits.CurrentLimitA);
                Require(row.Assessment != null, "회로 판정이 없습니다: " + circuit.CircuitId);
                Require(row.Assessment.IsWithinLimits == within,
                    "회로 판정이 독립 계산과 다릅니다: " + circuit.CircuitId);
                if (requested?.ExpectedWithinLimits != null)
                {
                    Require(within == requested!.ExpectedWithinLimits!.Value,
                        string.Format(CultureInfo.InvariantCulture,
                            "회로 {0} 판정이 기대와 다릅니다. 필요 전압 {1:0.###} V / 허용 {2:0.###} V, 전류 {3:0.####} A / 허용 {4:0.####} A",
                            circuit.CircuitId, independentVoltage, limits.VoltageLimitV,
                            independentCurrent, limits.CurrentLimitA));
                }
            }
            if (requested?.CircuitVerdicts != null)
            {
                foreach (VisualCircuitVerdict expectation in requested.CircuitVerdicts)
                {
                    WorkingArea target = ResolveVisualArea(expectation.Area);
                    string prefix = string.IsNullOrEmpty(target.Name) ? "A-" : target.Name + "-";
                    List<ElectricalSpecRow> areaRows = global.Table.Rows
                        .Where(row => row.CircuitId.StartsWith(prefix, StringComparison.Ordinal))
                        .ToList();
                    Require(areaRows.Count > 0,
                        "회로 판정을 확인할 발열회로가 없습니다: " + expectation.Area);
                    bool expectedOk = expectation.Verdict == "OK";
                    foreach (ElectricalSpecRow row in areaRows)
                    {
                        Require(row.Assessment != null,
                            "회로 판정이 없습니다: " + row.CircuitId);
                        Require(row.Assessment.IsWithinLimits == expectedOk,
                            string.Format(CultureInfo.InvariantCulture,
                                "회로 {0} 판정이 시나리오와 다릅니다: 기대 {1}, 실제 {2}",
                                row.CircuitId, expectation.Verdict,
                                row.Assessment.IsWithinLimits ? "OK" : "NG"));
                    }
                }
            }
            Require(Near(global.Table.TotalTargetPowerW, allocated, 1e-6),
                "배분 합계가 회로별 배분의 합과 다릅니다.");
            Require(Near(global.Table.TotalPowerW, allocated, 1e-6),
                "총 발열전력이 배분 합계와 다릅니다.");

            bool anyNg = global.Table.Rows.Any(row => row.IsOverLimit);
            if (requested?.ExpectedWithinLimits == false)
                Require(anyNg, "NG를 기대했지만 모든 회로가 한도 이내입니다.");

            int reviewScope = requested?.ReviewScope ?? entryScope;
            SetVisualReviewScope(reviewScope);
            ReviewSnapshot scoped = BuildReviewSnapshot();
            Require(scoped.ElectricalError == null, "범위 전환 후 전기 입력 오류: " + scoped.ElectricalError);
            if (reviewScope == 1)
            {
                Require(scoped.RequestedAreas.Count == 1 && ReferenceEquals(scoped.RequestedAreas[0], _activeArea),
                    "현재 영역 검토 범위가 활성 영역 하나로 제한되지 않았습니다.");
                Require(scoped.Entries.All(entry => ReferenceEquals(entry.Area, _activeArea)),
                    "현재 영역 검토 범위에 다른 영역이 포함되었습니다.");
            }
            foreach (CircuitLengthInfo circuit in scoped.Circuits)
            {
                Require(targetByCircuit.TryGetValue(circuit.CircuitId, out double globalTarget),
                    "범위 전환 후 알 수 없는 회로가 표시됩니다: " + circuit.CircuitId);
                Require(circuit.TargetPowerW.HasValue && Near(circuit.TargetPowerW.Value, globalTarget, 1e-9),
                    "검토 범위를 바꾸면서 발열전력이 다시 배분되었습니다: " + circuit.CircuitId);
            }
            Require(ElectricalSpecGrid.Items.Count == scoped.Table.Rows.Count + 1,
                "회로별 사양표 화면 행 수가 다릅니다.");
            Require(ExpandedElectricalGrid.Items.Count == scoped.Table.Rows.Count + 1,
                "확대 전기 사양표 화면 행 수가 다릅니다.");

            var data = new Dictionary<string, string>
            {
                ["circuits"] = global.Circuits.Count.ToString(CultureInfo.InvariantCulture),
                ["totalLengthM"] = totalLengthM.ToString("0.###", CultureInfo.InvariantCulture),
                ["allocatedW"] = allocated.ToString("0.###", CultureInfo.InvariantCulture),
                ["limitW"] = limits.PowerLimitW.ToString("0.###", CultureInfo.InvariantCulture),
                ["overLimitCircuits"] = global.Table.OverLimitCount.ToString(CultureInfo.InvariantCulture),
                ["reviewScope"] = reviewScope.ToString(CultureInfo.InvariantCulture),
                ["wireResistanceOhmPerM"] = wire.ResistanceOhmPerMeter.ToString("0.###", CultureInfo.InvariantCulture)
            };
            return VisualStepOutcome.Ok("전기 판정·공유 전력 배분 확인", data);
        }

        private static string DescribeCutLines(WorkingArea area)
        {
            if (area.SplitCutLines == null || area.SplitCutLines.Count == 0) return "cuts=none";
            return "cuts=" + string.Join(";", area.SplitCutLines.Select(line => string.Format(
                CultureInfo.InvariantCulture, "({0:F3},{1:F3})->({2:F3},{3:F3})",
                line.A.X, line.A.Y, line.B.X, line.B.Y)));
        }

        private static string DescribeRegionBounds(WorkingArea area)
        {
            if (area.Region == null || area.Region.Count == 0 || area.Region[0].Count == 0) return "ring=none";
            IReadOnlyList<Vec2> ring = area.Region[0];
            return string.Format(CultureInfo.InvariantCulture, "ring=[{0:F3},{1:F3}]x[{2:F3},{3:F3}]",
                ring.Min(p => p.X), ring.Max(p => p.X), ring.Min(p => p.Y), ring.Max(p => p.Y));
        }

        private VisualStepOutcome VisualSplitArea(VisualActionSpec spec, VisualScenario scenario)
        {
            WorkingArea parent = ResolveVisualArea(spec.Area);
            ActivateArea(parent);
            SidebarTabs.SelectedIndex = ReviewTabIndex;
            Require(parent.Pattern != null && parent.Pattern.RoutePieces.Count > 0,
                "분할할 영역에 확정된 발열경로가 없습니다: " + spec.Area);
            Require((parent.SelectedStartExit.HasValue && parent.SelectedEndExit.HasValue)
                || SplitExitPointPlanner.HasAssignablePair(parent),
                "분할에 사용할 시작/종료 인출점이 없습니다: " + spec.Area);

            List<WorkingArea> beforeAreas = _areas.ToList();
            Require(TryCaptureAllAreaRoutes(out Dictionary<WorkingArea, VisualRouteSnapshot> beforeRoutes,
                out string captureError), "분할 전 확정 경로를 읽지 못했습니다: " + captureError);

            QueueVisualTestConfirm(spec);
            SplitActiveArea();
            RequireNoPendingVisualTestConfirms();

            // 자동분할의 결과 문구는 StatusText가 아니라 분할 전용 상태 문구에만 기록된다.
            string splitStatus = SplitStatusText?.Text ?? string.Empty;
            bool parentRemoved = !_areas.Contains(parent);
            if (string.Equals(spec.Outcome, "rejected", StringComparison.OrdinalIgnoreCase))
            {
                Require(!parentRemoved, "거부를 기대했지만 영역이 분할되었습니다.");
                RequireSameAreasInOrder(beforeAreas, "거부된 분할");
                string rejectedPreservation = RequireAreaRoutesUnchanged(beforeRoutes, null, "거부된 분할");
                Require(rejectedPreservation == null, rejectedPreservation);
                if (!string.IsNullOrEmpty(spec.FailureContains))
                    Require(splitStatus.Contains(spec.FailureContains, StringComparison.Ordinal),
                        "분할 거부 사유가 기대와 다릅니다: " + splitStatus);
                var rejectedData = new Dictionary<string, string>
                {
                    ["reason"] = splitStatus,
                    ["areas"] = _areas.Count.ToString(CultureInfo.InvariantCulture)
                };
                return VisualStepOutcome.Ok("자동분할 거부·기존 상태 보존", rejectedData);
            }

            Require(parentRemoved, "자동분할이 완료되지 않았습니다: " + splitStatus);
            List<WorkingArea> children = _areas
                .Where(area => !beforeAreas.Contains(area))
                .OrderBy(area => area.AreaId)
                .ToList();
            Require(children.Count >= 2 && children.Count <= 4,
                "자동분할 결과 영역 수가 2..4가 아닙니다: " + children.Count);
            if (spec.PartCount != null)
                Require(children.Count == spec.PartCount.Value,
                    "자동분할 영역 수가 기대와 다릅니다. 기대 " + spec.PartCount.Value + ", 실제 " + children.Count);
            Require(_areas.Count == beforeAreas.Count - 1 + children.Count,
                "분할 결과 영역 목록에 예상하지 않은 영역이 있습니다.");

            string preservation = RequireAreaRoutesUnchanged(beforeRoutes, parent, "자동분할 후 다른 영역");
            Require(preservation == null, preservation);

            string[] childAliases = spec.ChildAliases;
            if (childAliases == null || childAliases.Length == 0)
                childAliases = Enumerable.Range(1, children.Count).Select(i => spec.Area + "-" + i).ToArray();
            Require(childAliases.Length == children.Count,
                "분할 결과 영역 수와 childAliases 수가 다릅니다.");

            int splitAxis = ResolveSplitAxis(parent);
            double parentSign = Math.Sign(AxisOf(parent.SelectedEndExit.Value.Point, splitAxis)
                - AxisOf(parent.SelectedStartExit.Value.Point, splitAxis));

            _visualAreaAliases.Remove(spec.Area);
            var childData = new List<string>();
            var childBindingData = new List<string>();
            var childBoundaryData = new List<string>();
            for (int i = 0; i < children.Count; i++)
            {
                WorkingArea child = children[i];
                Require(!_visualAreaAliases.ContainsKey(childAliases[i]),
                    "분할 결과 alias가 이미 사용 중입니다: " + childAliases[i]);
                string error = CheckAreaState(child, null, out Dictionary<string, string> areaData);
                Require(error == null, "분할 영역 " + childAliases[i] + ": " + error);
                Require(child.Circuits.Count == 1,
                    "분할 영역에 회로가 정확히 하나가 아닙니다: " + childAliases[i]);
                Require(child.FaceIdSet.SetEquals(parent.FaceIdSet),
                    "분할 영역이 부모의 선택 면을 유지하지 않았습니다: " + childAliases[i]);
                Require(child.SelectedStartExit.HasValue && child.SelectedEndExit.HasValue,
                    "분할 영역에 자동 배정된 시작/종료 인출점이 없습니다: " + childAliases[i]);

                ExitBoundaryPoint childStart = child.SelectedStartExit.Value;
                ExitBoundaryPoint childEnd = child.SelectedEndExit.Value;
                string startKind = DescribeBindingKind(childStart);
                string endKind = DescribeBindingKind(childEnd);
                Require(startKind != "none" && endKind != "none",
                    "분할 영역의 인출점에 경계 연결 정보가 없습니다: " + childAliases[i]);
                if (parentSign != 0)
                {
                    double sign = Math.Sign(AxisOf(childEnd.Point, splitAxis) - AxisOf(childStart.Point, splitAxis));
                    Require(sign == parentSign,
                        "분할 영역의 인출 방향이 부모와 다릅니다: " + childAliases[i]);
                }
                _visualAreaAliases[childAliases[i]] = child;
                childData.Add(childAliases[i] + ":" + areaData["pieces"] + "p/" + areaData["totalLengthMm"] + "mm");
                childBindingData.Add(childAliases[i] + ":" + startKind + "/" + endKind);
                childBoundaryData.Add(childAliases[i] + ":" + DescribeCutLines(child) + " " + DescribeRegionBounds(child));
            }

            double minLength = children.Min(c => c.RoutePieceLengths.Sum());
            double maxLength = children.Max(c => c.RoutePieceLengths.Sum());
            Require(minLength > 0, "분할 영역의 실제 3D 길이가 0입니다.");
            double ratio = maxLength / minLength;

            ActivateArea(children[0]);
            var data = new Dictionary<string, string>
            {
                ["areas"] = _areas.Count.ToString(CultureInfo.InvariantCulture),
                ["children"] = string.Join(",", childData),
                ["bindings"] = string.Join(",", childBindingData),
                ["boundaries"] = string.Join(",", childBoundaryData),
                ["minLengthMm"] = minLength.ToString("0.###", CultureInfo.InvariantCulture),
                ["maxLengthMm"] = maxLength.ToString("0.###", CultureInfo.InvariantCulture),
                ["lengthRatio"] = ratio.ToString("0.####", CultureInfo.InvariantCulture),
                ["splitStatus"] = splitStatus
            };
            return VisualStepOutcome.Ok("자동분할·자식 영역 확인", data);
        }

        private VisualStepOutcome VisualExportStep(VisualActionSpec spec)
        {
            SidebarTabs.SelectedIndex = ExportTabIndex;
            Require(TryCaptureAllAreaRoutes(out var routesBefore, out string captureError), captureError);
            // 전체/현재 영역 범위를 제품 경로로 전환한 뒤, 기대 대상 영역과 조각 수를
            // 확정 상태에서 독립적으로 계산한다. 실제 파일 검증은 제품의 XDE 재읽기가 맡는다.
            SetVisualReviewScope(0);
            ReviewSnapshot allSnapshot = BuildReviewSnapshot();
            int outputScope = spec.Scope ?? 0;
            SetVisualReviewScope(outputScope);
            ReviewSnapshot expectedSnapshot = BuildReviewSnapshot();
            string path = VisualArtifactPath(spec.FileName, "result.step");
            if (File.Exists(path))
                return VisualStepOutcome.Fail("STEP 결과 파일이 이미 있습니다: " + path);

            ExportStepResult result = ExportStepCore(path, out string error);
            if (spec.Outcome == "rejected")
            {
                Require(result == ExportStepResult.Rejected || result == ExportStepResult.Failed,
                    "STEP 출력 거부를 기대했지만 결과가 " + result + "입니다.");
                Require(!string.IsNullOrWhiteSpace(error), "STEP 거부 사유가 비어 있습니다.");
                if (!string.IsNullOrWhiteSpace(spec.FailureContains))
                    Require(error.Contains(spec.FailureContains, StringComparison.Ordinal),
                        "STEP 거부 사유가 기대와 다릅니다: " + error);
                Require(!File.Exists(path), "STEP 출력이 거부되었는데 파일이 생성되었습니다.");
                var rejectedData = new Dictionary<string, string> { ["reason"] = error };
                RequireAllRoutesPreserved(routesBefore, "STEP 출력 거부");
                return VisualStepOutcome.Ok("STEP 출력 거부 확인", rejectedData);
            }

            Require(result == ExportStepResult.Success, "STEP 출력이 완료되지 않았습니다: " + error);
            Require(File.Exists(path), "STEP 결과 파일이 없습니다: " + path);
            var info = new FileInfo(path);
            Require(info.Length > 0, "STEP 결과 파일이 비어 있습니다.");
            VisualTestSession.Current?.Report.RegisterArtifact("step", path);
            var data = new Dictionary<string, string>
            {
                ["path"] = path,
                ["bytes"] = info.Length.ToString(CultureInfo.InvariantCulture),
                ["scope"] = outputScope.ToString(CultureInfo.InvariantCulture)
            };
            foreach (KeyValuePair<string, string> metric in DescribeStepExportScope(allSnapshot, expectedSnapshot, outputScope))
                data[metric.Key] = metric.Value;
            RequireAllRoutesPreserved(routesBefore, "STEP 출력");
            return VisualStepOutcome.Ok("STEP 출력·네이티브 자기검증·범위 일치 확인", data);
        }

        /// <summary>
        /// STEP 출력 대상 영역과 조각 수를 확정 상태에서 계산해 실제 제품 선택 결과와 대조한다.
        /// 범위 밖 영역이 조용히 섞이지 않았는지는 이 대상 집합으로 확인한다.
        /// </summary>
        private Dictionary<string, string> DescribeStepExportScope(
            ReviewSnapshot allSnapshot, ReviewSnapshot expectedSnapshot, int outputScope)
        {
            WorkingSideDefinition side = _workingSide?.Confirmed;
            Require(side != null, "STEP 범위를 확인할 수 없습니다 — 작업면이 확정되지 않았습니다.");
            var areas = new List<WorkingArea>(expectedSnapshot.ReadyAreas);
            Require(areas.Count > 0, "STEP 출력 대상 영역이 없습니다.");
            if (outputScope == 1)
            {
                Require(areas.Count == 1, string.Format(
                    "현재 영역 STEP 출력인데 대상 영역이 {0}개입니다.", areas.Count));
                Require(ReferenceEquals(areas[0], _activeArea),
                    "현재 영역 STEP 출력 대상이 활성 영역이 아닙니다.");
                Require(allSnapshot.ReadyAreas.Any(a => !ReferenceEquals(a, areas[0])),
                    "현재 영역 STEP 범위를 검사하려면 범위 밖에 확정 경로 영역이 하나 이상 있어야 합니다.");
            }
            else
            {
                Require(areas.Count == _areas.Count, string.Format(
                    "전체 STEP 출력 대상 영역 수({0})가 확정 영역 수({1})와 다릅니다.",
                    areas.Count, _areas.Count));
                if (_areas.Count > 1)
                    Require(allSnapshot.ReadyAreas.Count == _areas.Count,
                        "전체 STEP 출력인데 검토 상태가 Ready가 아닌 영역이 있습니다.");
            }

            List<RouteExportCircuit> circuits = CollectExportCircuits(areas, side, out string collectError);
            Require(circuits != null, "STEP 대상 확정 경로를 읽지 못했습니다 — " + collectError);
            Require(circuits.Count == areas.Count, string.Format(
                "STEP 대상 회로 수({0})가 영역 수({1})와 다릅니다.", circuits.Count, areas.Count));
            int pieces = circuits.Sum(c => c.Pieces.Count);
            int expectedPieces = areas.Sum(a => a.NativePieceIds.Length);
            Require(pieces == expectedPieces, string.Format(
                "STEP 대상 조각 수({0})가 확정 조각 수({1})와 다릅니다.", pieces, expectedPieces));
            foreach (WorkingArea area in areas)
            {
                Require(area.RoutePieceLengths.Length == area.NativePieceIds.Length,
                    area.Name + " 조각 길이와 확정 조각 수가 다릅니다.");
                Require(area.PatternDisplayed, area.Name + " 확정 경로가 화면에 표시되지 않았습니다.");
            }

            return new Dictionary<string, string>
            {
                ["areas"] = areas.Count.ToString(CultureInfo.InvariantCulture),
                ["pieces"] = pieces.ToString(CultureInfo.InvariantCulture),
                ["areaNames"] = string.Join(",", areas.Select(a => a.Name))
            };
        }

        private VisualStepOutcome VisualSaveProject(VisualActionSpec spec)
        {
            string path = VisualArtifactPath(spec.FileName, "project.json");
            if (File.Exists(path))
                return VisualStepOutcome.Fail("프로젝트 결과 파일이 이미 있습니다: " + path);

            Require(TryCaptureAllAreaRoutes(out var routesBefore, out string captureError), captureError);
            bool saved = SaveProjectTo(path);
            if (spec.Outcome == "rejected")
            {
                Require(!saved, "프로젝트 저장 거부를 기대했지만 저장되었습니다.");
                if (!string.IsNullOrWhiteSpace(spec.FailureContains))
                    Require(StatusText.Text.Contains(spec.FailureContains, StringComparison.Ordinal),
                        "프로젝트 저장 거부 사유가 기대와 다릅니다: " + StatusText.Text);
                Require(!File.Exists(path), "저장이 거부되었는데 프로젝트 파일이 생성되었습니다.");
                RequireAllRoutesPreserved(routesBefore, "프로젝트 저장 거부");
                return VisualStepOutcome.Ok("프로젝트 저장 거부 확인");
            }

            Require(saved, "프로젝트 저장이 완료되지 않았습니다: " + StatusText.Text);
            Require(File.Exists(path), "프로젝트 파일이 없습니다: " + path);
            var info = new FileInfo(path);
            Require(info.Length > 0, "프로젝트 파일이 비어 있습니다.");
            VisualTestSession.Current?.Report.RegisterArtifact("project", path);
            _visualProjectCheckpoints[path] = File.ReadAllText(path);
            _visualProjectAliasIds[path] = _visualAreaAliases.ToDictionary(
                pair => pair.Key, pair => pair.Value.AreaId, StringComparer.Ordinal);

            var data = new Dictionary<string, string>
            {
                ["path"] = path,
                ["bytes"] = info.Length.ToString(CultureInfo.InvariantCulture),
                ["areas"] = _areas.Count.ToString(CultureInfo.InvariantCulture),
                ["aliases"] = string.Join(",", _visualAreaAliases.Keys.OrderBy(k => k, StringComparer.Ordinal))
            };
            RequireAllRoutesPreserved(routesBefore, "프로젝트 저장");
            return VisualStepOutcome.Ok("프로젝트 저장", data);
        }

        private VisualStepOutcome VisualLoadProject(VisualActionSpec spec)
        {
            string path = VisualArtifactPath(spec.FileName, "project.json");
            Require(File.Exists(path), "불러올 프로젝트 파일이 없습니다: " + path);
            Require(_visualProjectAliasIds.ContainsKey(path),
                "이 실행에서 저장한 프로젝트가 아니어서 영역 alias를 복원할 수 없습니다: " + path);

            QueueVisualTestConfirm(spec);
            bool loaded = LoadProject(path);
            RequireNoPendingVisualTestConfirms();
            if (spec.Outcome == "rejected")
            {
                Require(!loaded, "프로젝트 열기 거부를 기대했지만 열렸습니다.");
                if (!string.IsNullOrWhiteSpace(spec.FailureContains))
                    Require(StatusText.Text.Contains(spec.FailureContains, StringComparison.Ordinal),
                        "프로젝트 열기 거부 사유가 기대와 다릅니다: " + StatusText.Text);
                return VisualStepOutcome.Ok("프로젝트 열기 거부 확인");
            }

            Require(loaded, "프로젝트 열기가 완료되지 않았습니다: " + StatusText.Text);
            Require(_areas.Count > 0, "프로젝트를 열었지만 작업영역이 없습니다.");

            Dictionary<string, int> aliasIds = _visualProjectAliasIds[path];
            var restoredAliases = new Dictionary<string, WorkingArea>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, int> pair in aliasIds)
            {
                WorkingArea area = _areas.FirstOrDefault(a => a.AreaId == pair.Value);
                Require(area != null, "복원된 영역을 alias에 연결하지 못했습니다: " + pair.Key + " (AreaId " + pair.Value + ")");
                Require(restoredAliases.Values.All(existing => !ReferenceEquals(existing, area)),
                    "여러 alias가 같은 복원 영역에 연결되었습니다: " + pair.Key);
                restoredAliases[pair.Key] = area;
            }
            _visualAreaAliases.Clear();
            foreach (KeyValuePair<string, WorkingArea> pair in restoredAliases)
                _visualAreaAliases[pair.Key] = pair.Value;
            Require(_visualAreaAliases.Count == _areas.Count,
                "복원된 영역 수와 alias 수가 다릅니다. 영역 " + _areas.Count + ", alias " + _visualAreaAliases.Count);

            string checkpointDiff = null;
            if (spec.CompareProject == true && _visualProjectCheckpoints.TryGetValue(path, out string expectedJson))
            {
                // 현재 상태를 메모리에서 직렬화한다. 저장 경로/변경 플래그를 바꾸지 않으므로
                // 복원 검증이 제품의 프로젝트 경로나 dirty 상태에 부작용을 남기지 않는다.
                ProjectDocument current = BuildProjectDocument(path);
                string actualJson = System.Text.Encoding.UTF8.GetString(ProjectJsonStore.Serialize(current));
                checkpointDiff = CompareProjectJson(expectedJson, actualJson);
            }
            if (checkpointDiff != null)
                return VisualStepOutcome.Fail("저장 시점과 복원 상태가 다릅니다: " + checkpointDiff);

            var data = new Dictionary<string, string>
            {
                ["path"] = path,
                ["areas"] = _areas.Count.ToString(CultureInfo.InvariantCulture),
                ["aliases"] = string.Join(",", _visualAreaAliases.Keys.OrderBy(k => k, StringComparer.Ordinal)),
                ["activeAreaId"] = (_activeArea?.AreaId ?? -1).ToString(CultureInfo.InvariantCulture),
                ["electrical"] = !string.IsNullOrWhiteSpace(AllowedPowerBox.Text) ? AllowedPowerBox.Text : "",
                ["checkpoint"] = checkpointDiff == null && spec.CompareProject == true ? "equal" : "skipped"
            };
            return VisualStepOutcome.Ok("프로젝트 열기·상태 복원", data);
        }

        /// <summary>거부된 동작이 영역 목록을 바꾸지 않았는지 참조·순서까지 검사한다.</summary>
        private void RequireSameAreasInOrder(List<WorkingArea> expected, string label)
        {
            Require(_areas.Count == expected.Count, label + " — 영역 수가 달라졌습니다.");
            for (int i = 0; i < expected.Count; i++)
                Require(ReferenceEquals(_areas[i], expected[i]),
                    label + " — 영역 목록의 순서나 참조가 달라졌습니다: " + expected[i].Name);
        }

        /// <summary>자동분할은 레인에 수직인 축으로 영역을 나눈다. 0=X, 1=Y.</summary>
        private static int ResolveSplitAxis(WorkingArea area)
        {
            PatternDirection direction = area.AppliedInputs?.Direction ?? area.Direction;
            // FrameY(2)는 세로 레인이라 X로 나누고, FrameX(1)와 Auto는 가로 레인이라 Y로 나눈다.
            return direction == PatternDirection.FrameY ? 0 : 1;
        }

        private static double AxisOf(Vec2 point, int axis) => axis == 0 ? point.X : point.Y;

        private static string DescribeBindingKind(ExitBoundaryPoint point)
        {
            if (point.Binding.HasValue) return "cad";
            if (point.ArtificialBinding.HasValue) return "artificial";
            if (point.SplitBinding.HasValue) return "split";
            return "none";
        }

        private static double ExitUvDistance(Vec2 a, Vec2 b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static bool Near(double actual, double expected, double tolerance) =>
            double.IsFinite(actual) && double.IsFinite(expected) && Math.Abs(actual - expected) <= tolerance;

        private static string DescribeExitBinding(ExitBoundaryPoint point)
        {
            if (point.Binding.HasValue)
            {
                ExitEdgeBinding binding = point.Binding.Value;
                return string.Format(CultureInfo.InvariantCulture,
                    "CAD edge={0} parameter={1:0.############} range=[{2:0.############},{3:0.############}] reversed={4}",
                    binding.EdgeKey, binding.Parameter, binding.FirstParameter, binding.LastParameter, binding.Reversed);
            }
            if (point.ArtificialBinding.HasValue) return "artificial=" + point.ArtificialBinding.Value;
            if (point.SplitBinding.HasValue) return "split=" + point.SplitBinding.Value;
            return "none";
        }

        private static string VisualArtifactPath(string fileName, string fallback)
        {
            string name = string.IsNullOrWhiteSpace(fileName) ? fallback : fileName;
            if (name == "." || name == ".." || name.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)
                throw new InvalidOperationException("아티팩트 파일명은 실행 폴더 안의 순수 파일명이어야 합니다: " + name);
            return Path.Combine(VisualTestSession.Current.Report.RunDirectory, name);
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static string CompareProjectJson(string expected, string actual)
        {
            JsonNode left, right;
            try
            {
                left = JsonNode.Parse(expected);
                right = JsonNode.Parse(actual);
            }
            catch (JsonException ex)
            {
                return "프로젝트 JSON 해석 실패: " + ex.Message;
            }
            return CompareJsonNode(left, right, "$");
        }

        private static string CompareJsonNode(JsonNode left, JsonNode right, string path)
        {
            if (left == null && right == null) return null;
            if (left == null || right == null) return path + " 한쪽이 null입니다.";
            if (left is JsonObject leftObject && right is JsonObject rightObject)
            {
                List<string> leftNames = leftObject
                    .Where(pair => !IsVolatileProjectProperty(pair.Key))
                    .Select(pair => pair.Key)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToList();
                List<string> rightNames = rightObject
                    .Where(pair => !IsVolatileProjectProperty(pair.Key))
                    .Select(pair => pair.Key)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToList();
                if (!leftNames.SequenceEqual(rightNames, StringComparer.Ordinal))
                    return path + " 속성 집합이 다릅니다.";
                foreach (string name in leftNames)
                {
                    string diff = CompareJsonNode(leftObject[name], rightObject[name], path + "." + name);
                    if (diff != null) return diff;
                }
                return null;
            }
            if (left is JsonArray leftArray && right is JsonArray rightArray)
            {
                if (leftArray.Count != rightArray.Count)
                    return path + " 배열 길이가 다릅니다. " + leftArray.Count + " != " + rightArray.Count;
                for (int i = 0; i < leftArray.Count; i++)
                {
                    string diff = CompareJsonNode(leftArray[i], rightArray[i], path + "[" + i + "]");
                    if (diff != null) return diff;
                }
                return null;
            }
            return JsonNode.DeepEquals(left, right)
                ? null
                : path + " 값이 다릅니다: " + left.ToJsonString() + " != " + right.ToJsonString();
        }

        private static bool IsVolatileProjectProperty(string name) =>
            name.EndsWith("Utc", StringComparison.Ordinal)
            || name == "routeContextKey"
            || name == "edgeKey"
            || name == "revision"
            || name == "modelRevision"
            || name == "workingContextId"
            || name == "workingContextRevision"
            || name == "nativePieceId"
            || name == "nativePieceIds";

        // ---- 13·14: 제외영역·수동 편집 ----

        private VisualStepOutcome VisualAddExclusion(VisualActionSpec spec, VisualScenario scenario)
        {
            RequireAreaDefinition(scenario, spec.Area);
            WorkingArea area = ResolveVisualArea(spec.Area);
            ActivateArea(area);
            RefreshAreaList();
            SidebarTabs.SelectedIndex = PatternTabIndex;
            Require(spec.Rect != null && spec.Rect.Length == 4, "제외 사각형 좌표가 없습니다.");

            int before = area.Exclusions.Count;
            var a = new Vec2(spec.Rect[0], spec.Rect[1]);
            var b = new Vec2(spec.Rect[2], spec.Rect[3]);
            Require(TryCaptureAllAreaRoutes(out var routesBefore, out string captureError),
                "제외영역 추가 전 확정 경로를 읽지 못했습니다: " + captureError);
            QueueVisualTestConfirm(spec);
            bool added = AddActiveAreaExclusion(a, b, out string error);
            RequireNoPendingVisualTestConfirms();
            Require(added, "제외영역을 추가하지 못했습니다: " + error);
            Require(area.Exclusions.Count == before + 1, "제외영역 목록이 증가하지 않았습니다.");
            Require(area.Pattern == null, "제외영역 추가 후 이전 경로가 남아 있습니다.");
            Require(!area.PatternDisplayed, "제외영역 추가 후 이전 경로가 표시된 상태입니다.");
            Require(area.NativePieceIds.Length == 0,
                "제외영역 추가 후 조각 상태가 남아 있습니다.");
            RequireSiblingRoutesPreserved(routesBefore, area, "제외영역 추가");

            string rawDiagnostic = Bridge.GetDisplayDiagnostic() ?? "";
            Dictionary<string, int> diagnostic = ParseDisplayDiagnostic(rawDiagnostic);
            int expectedRoutes = _areas.Where(x => !ReferenceEquals(x, area) && x.Pattern != null)
                .Sum(x => x.Pattern.RoutePieces.Count);
            int expectedMarkers = 2 * _areas.Count(x => !ReferenceEquals(x, area) && x.Pattern != null);
            Require(NativeCount(diagnostic, "routes") == expectedRoutes,
                "제외영역 추가 후 다른 영역의 네이티브 경로 수가 다릅니다.");
            Require(NativeCount(diagnostic, "markers") == expectedMarkers,
                "제외영역 추가 후 다른 영역의 끝점 마커 수가 다릅니다.");

            var data = new Dictionary<string, string>
            {
                ["area"] = area.Name,
                ["rect"] = string.Format(CultureInfo.InvariantCulture, "({0:0.###},{1:0.###})-({2:0.###},{3:0.###})",
                    spec.Rect[0], spec.Rect[1], spec.Rect[2], spec.Rect[3]),
                ["exclusions"] = area.Exclusions.Count.ToString(CultureInfo.InvariantCulture),
                ["native"] = rawDiagnostic
            };
            return VisualStepOutcome.Ok("제외영역 추가 — 기존 경로 무효화 확인", data);
        }

        private VisualStepOutcome VisualRemoveExclusion(VisualActionSpec spec)
        {
            WorkingArea area = ResolveVisualArea(spec.Area);
            ActivateArea(area);
            RefreshAreaList();
            Require(area.Exclusions.Count > 0, "제거할 제외영역이 없습니다.");

            Require(TryCaptureAllAreaRoutes(out var routesBefore, out string captureError),
                "제외영역 제거 전 확정 경로를 읽지 못했습니다: " + captureError);
            QueueVisualTestConfirm(spec);
            bool removed = RemoveActiveAreaExclusions(out string error);
            RequireNoPendingVisualTestConfirms();
            Require(removed, "제외영역을 제거하지 못했습니다: " + error);
            Require(area.Exclusions.Count == 0, "제외영역 목록이 비워지지 않았습니다.");
            Require(area.Pattern == null, "제외영역 제거 후 이전 경로가 남아 있습니다.");
            RequireSiblingRoutesPreserved(routesBefore, area, "제외영역 제거");

            string rawDiagnostic = Bridge.GetDisplayDiagnostic() ?? "";
            Dictionary<string, int> diagnostic = ParseDisplayDiagnostic(rawDiagnostic);
            int expectedRoutes = _areas.Where(x => !ReferenceEquals(x, area) && x.Pattern != null)
                .Sum(x => x.Pattern.RoutePieces.Count);
            Require(NativeCount(diagnostic, "routes") == expectedRoutes,
                "제외영역 제거 후 다른 영역의 네이티브 경로 수가 다릅니다.");

            var data = new Dictionary<string, string>
            {
                ["area"] = area.Name,
                ["exclusions"] = "0",
                ["native"] = rawDiagnostic
            };
            return VisualStepOutcome.Ok("제외영역 제거 — 재생성 전 상태 확인", data);
        }

        private VisualStepOutcome VisualExpectNoPattern(VisualActionSpec spec)
        {
            WorkingArea area = ResolveVisualArea(spec.Area);
            ActivateArea(area);
            RefreshAreaList();
            Require(area.Pattern == null, "경로가 없는 상태여야 합니다.");
            Require(!area.PatternDisplayed, "경로 표시가 꺼져 있어야 합니다.");
            Require(area.NativePieceIds.Length == 0, "네이티브 조각 ID가 남아 있습니다.");
            Require(area.ExclusionPieces == null, "제외영역 조각 상태가 남아 있습니다.");

            string rawDiagnostic = Bridge.GetDisplayDiagnostic() ?? "";
            Dictionary<string, int> diagnostic = ParseDisplayDiagnostic(rawDiagnostic);
            int expectedRoutes = _areas.Where(x => !ReferenceEquals(x, area) && x.Pattern != null)
                .Sum(x => x.Pattern.RoutePieces.Count);
            int expectedMarkers = 2 * _areas.Count(x => !ReferenceEquals(x, area) && x.Pattern != null);
            Require(NativeCount(diagnostic, "routes") == expectedRoutes,
                "다른 영역의 네이티브 경로 수가 다릅니다.");
            Require(NativeCount(diagnostic, "markers") == expectedMarkers,
                "다른 영역의 끝점 마커 수가 다릅니다.");

            var data = new Dictionary<string, string>
            {
                ["area"] = area.Name,
                ["pattern"] = "none",
                ["native"] = rawDiagnostic
            };
            return VisualStepOutcome.Ok("현재 영역 경로 없음·다른 영역 보존 확인", data);
        }

        private VisualStepOutcome VisualCheckExclusion(VisualActionSpec spec)
        {
            WorkingArea area = ResolveVisualArea(spec.Area);
            Require(area.Pattern != null, "제외영역 적용 결과를 검사할 경로가 없습니다.");
            double[] rect = spec.Rect;
            Require(rect != null && rect.Length == 4, "검사할 제외 사각형이 없습니다.");
            // 발열선은 마진이 적용된 제외영역 밖에 있어야 한다. 원본 사각형만 보면
            // 마진 경계에 붙은 발열 구간을 통과시킬 수 있다. 커넥터의 통과는 허용한다.
            double margin = area.AppliedInputs?.Margin ?? 0;
            var exclusion = (
                rect[0] - margin, rect[1] - margin,
                rect[2] + margin, rect[3] + margin);
            Require(area.SelectedStartExit == null || area.SelectedStartExit.Value.Binding != null
                || area.SelectedStartExit.Value.ArtificialBinding != null
                || area.SelectedStartExit.Value.SplitBinding != null,
                "시작 인출점의 경계 연결 정보가 사라졌습니다: " + area.Name);
            Require(area.SelectedEndExit == null || area.SelectedEndExit.Value.Binding != null
                || area.SelectedEndExit.Value.ArtificialBinding != null
                || area.SelectedEndExit.Value.SplitBinding != null,
                "종료 인출점의 경계 연결 정보가 사라졌습니다: " + area.Name);

            // 인출면이 있는 제외영역 생성은 editablePattern 경로를 쓰므로 ExclusionPieces가
            // null이고, 최종 조각은 Pattern.RoutePieces에 들어 있다. 둘 다 지원한다.
            IEnumerable<SerpentineRoutePiece> pieces;
            string source;
            if (area.ExclusionPieces != null && area.ExclusionPieces.Count > 0)
            {
                pieces = area.ExclusionPieces.SelectMany(component => component);
                source = "exclusionPieces";
            }
            else
            {
                pieces = area.Pattern.RoutePieces;
                source = "pattern";
            }

            int checkedPieces = 0;
            int heatingViolations = 0;
            int connectorViolations = 0;
            var violatingKinds = new Dictionary<SerpentineRoutePieceKind, int>();
            foreach (SerpentineRoutePiece piece in pieces)
            {
                if (piece.Kind != SerpentineRoutePieceKind.HeatingRun
                    && piece.Kind != SerpentineRoutePieceKind.FixedConnector) continue;
                checkedPieces++;
                bool violates = false;
                for (int i = 1; i < piece.Points.Count; i++)
                {
                    if (ExactRegionContainment.MaxExclusionPenetration(
                            piece.Points[i - 1], piece.Points[i], exclusion)
                        <= GeometryTolerancePolicy.DistanceTolerance) continue;
                    violates = true;
                    break;
                }
                if (!violates) continue;
                violatingKinds.TryGetValue(piece.Kind, out int kindCount);
                violatingKinds[piece.Kind] = kindCount + 1;
                if (piece.Kind == SerpentineRoutePieceKind.HeatingRun) heatingViolations++;
                else connectorViolations++;
            }

            Require(checkedPieces > 0, "제외영역 적용 결과에 검사할 발열/고정 구간이 없습니다.");
            Require(heatingViolations == 0,
                string.Format("제외영역 내부를 지나는 발열 구간이 {0}개 있습니다. 위반 종류: {1}",
                    heatingViolations, string.Join(",", violatingKinds.Select(pair => pair.Key + "=" + pair.Value))));
            var data = new Dictionary<string, string>
            {
                ["area"] = area.Name,
                ["source"] = source,
                ["heatingPieces"] = checkedPieces.ToString(CultureInfo.InvariantCulture),
                ["heatingViolations"] = heatingViolations.ToString(CultureInfo.InvariantCulture),
                ["connectorViolations"] = connectorViolations.ToString(CultureInfo.InvariantCulture),
                ["marginMm"] = margin.ToString("0.###", CultureInfo.InvariantCulture),
                ["expandedRect"] = string.Format(CultureInfo.InvariantCulture, "({0:0.###},{1:0.###})-({2:0.###},{3:0.###})",
                    exclusion.Item1, exclusion.Item2, exclusion.Item3, exclusion.Item4),
                ["rect"] = string.Format(CultureInfo.InvariantCulture, "({0:0.###},{1:0.###})-({2:0.###},{3:0.###})",
                    rect[0], rect[1], rect[2], rect[3])
            };
            return VisualStepOutcome.Ok("제외영역 내부에 발열 구간 없음 확인", data);
        }

        private VisualStepOutcome VisualMoveVertex(VisualActionSpec spec)
        {
            WorkingArea area = ResolveVisualArea(spec.Area);
            ActivateArea(area);
            RefreshAreaList();
            SidebarTabs.SelectedIndex = PatternTabIndex;
            Require(area.Pattern != null, "꼭지점을 이동할 경로가 없습니다.");
            Require(!IsPatternInputStale(area), "입력 조건이 변경되어 꼭지점을 편집할 수 없습니다.");
            Require(spec.VertexIndex != null, "이동할 꼭지점 인덱스가 없습니다.");

            double[] beforeLengths = area.RoutePieceLengths.ToArray();
            bool beforeEdited = area.RouteEdited;
            int beforePieces = area.Pattern.RoutePieces.Count;
            Require(TryCaptureAllAreaRoutes(out var routesBefore, out string captureError),
                "꼭지점 편집 전 확정 경로를 읽지 못했습니다: " + captureError);

            EnterVertexEdit();
            Require(Viewport.VertexEditMode, "꼭지점 편집 모드가 켜지지 않았습니다.");
            int index = spec.VertexIndex.Value;
            Require(index >= 0 && index < _vertexGroups.Count,
                string.Format("꼭지점 그룹 인덱스가 범위를 벗어났습니다. 그룹 {0}개, 요청 {1}", _vertexGroups.Count, index));
            Vec2 from = _vertexGroups[index].Datum;
            var requested = new Vec2(from.X + spec.OffsetMm[0], from.Y + spec.OffsetMm[1]);
            // 생성 방향 성분만 적용되므로 기대 좌표를 같은 제약으로 먼저 계산한다.
            Require(RouteVertexMoveConstraint.TryConstrain(from, requested, area.Pattern!.AngleDeg,
                    out Vec2 expected, out string constraintFailure),
                "이동 제약을 계산할 수 없습니다: " + constraintFailure);
            IReadOnlyList<RouteVertexOccurrence> moving = _vertexGroups[index].Occurrences;

            bool begin = BeginVertexDrag(index, from, 0, 0);
            Require(begin, "꼭지점 드래그를 시작하지 못했습니다: " + StatusText.Text);

            if (spec.Outcome == "rejected")
            {
                Require(expected.DistanceTo(from) <= 1e-6,
                    string.Format(CultureInfo.InvariantCulture,
                        "이동 거부를 기대했지만 요청 변위가 생성 방향 성분을 가집니다: 시작 ({0:0.###},{1:0.###}) → 기대 ({2:0.###},{3:0.###})",
                        from.X, from.Y, expected.X, expected.Y));
                UpdateVertexPreview(requested);
                RequireStatusContains(spec.FailureContains);
                OnVertexDragCancelled();
                ExitVertexEdit();
                RequireVertexEditExited();
                RequirePatternUnchanged(area, beforePieces, beforeLengths, beforeEdited, "이동 거부 후");
                string rejectedPreservation = RequireAreaRoutesUnchanged(routesBefore, null, "이동 거부 후");
                Require(rejectedPreservation == null, rejectedPreservation);
                var rejected = new Dictionary<string, string>
                {
                    ["area"] = area.Name,
                    ["from"] = string.Format(CultureInfo.InvariantCulture, "({0:0.###},{1:0.###})", from.X, from.Y),
                    ["requested"] = string.Format(CultureInfo.InvariantCulture, "({0:0.###},{1:0.###})", requested.X, requested.Y),
                    ["reason"] = StatusText.Text
                };
                return VisualStepOutcome.Ok("꼭지점 이동 제한 확인", rejected);
            }

            Require(expected.DistanceTo(from) > 1e-6,
                "요청한 꼭지점 이동이 생성 방향과 수직이어서 실제로 적용되지 않습니다. "
                + string.Format(CultureInfo.InvariantCulture, "시작 ({0:0.###},{1:0.###}) → 요청 ({2:0.###},{3:0.###}), 생성 방향 {4:0.###}도",
                    from.X, from.Y, requested.X, requested.Y, area.Pattern.AngleDeg));

            UpdateVertexPreview(expected);
            if (spec.Cancel)
            {
                // 미리보기 자체가 실패하면 취소 후 보존 검사만으로 통과할 수 있으므로
                // 표시용 2D 미리보기가 실제로 그려진 상태를 먼저 확인한다.
                Require((StatusText.Text ?? "").Contains(VertexPreviewShownFragment, StringComparison.Ordinal),
                    "미리보기가 표시되지 않았습니다: " + StatusText.Text);
                OnVertexDragCancelled();
                ExitVertexEdit();
                RequireVertexEditExited();
                RequirePatternUnchanged(area, beforePieces, beforeLengths, beforeEdited, "미리보기 취소 후");
                string cancelPreservation = RequireAreaRoutesUnchanged(routesBefore, null, "미리보기 취소 후");
                Require(cancelPreservation == null, cancelPreservation);
                var cancelled = new Dictionary<string, string>
                {
                    ["area"] = area.Name,
                    ["vertexIndex"] = index.ToString(CultureInfo.InvariantCulture),
                    ["expected"] = string.Format(CultureInfo.InvariantCulture, "({0:0.###},{1:0.###})", expected.X, expected.Y),
                    ["pieces"] = area.Pattern.RoutePieces.Count.ToString(CultureInfo.InvariantCulture),
                    ["totalLengthMm"] = area.RoutePieceLengths.Sum().ToString("0.###", CultureInfo.InvariantCulture)
                };
                return VisualStepOutcome.Ok("꼭지점 미리보기 취소 — 확정 경로 보존", cancelled);
            }

            bool committed = CompleteVertexDrag(expected);
            Require(committed, "꼭지점 이동 확정이 완료되지 않았습니다: " + StatusText.Text);
            string commitStatus = StatusText.Text;

            bool constraintKept = true;
            string actualText = string.Empty;
            foreach (RouteVertexOccurrence occurrence in moving)
            {
                Vec2 actual = area.Pattern != null && occurrence.Piece >= 0
                    && occurrence.Piece < area.Pattern.RoutePieces.Count
                    && occurrence.Point >= 0
                    && occurrence.Point < area.Pattern.RoutePieces[occurrence.Piece].Points.Count
                    ? area.Pattern.RoutePieces[occurrence.Piece].Points[occurrence.Point]
                    : from;
                if (actual.DistanceTo(expected) <= 1e-6) continue;
                constraintKept = false;
                actualText = string.Format(CultureInfo.InvariantCulture, "({0:0.######},{1:0.######})",
                    actual.X, actual.Y);
                break;
            }
            ExitVertexEdit();
            Require(constraintKept,
                string.Format(CultureInfo.InvariantCulture,
                    "꼭지점 이동이 생성 방향 제약을 지키지 않았습니다: 기대 ({0:0.######},{1:0.######}), 실제 {2} — {3}",
                    expected.X, expected.Y, actualText, commitStatus));

            Require(area.RouteEdited, "꼭지점 이동이 적용되지 않았습니다: " + commitStatus);
            Require(area.Pattern != null && area.Pattern.RoutePieces.Count > 0, "편집 후 경로가 없습니다.");
            Require(!IsPatternInputStale(area), "편집 후 경로가 stale 상태입니다.");
            RequireSiblingRoutesPreserved(routesBefore, area, "꼭지점 이동 확정");

            var data = new Dictionary<string, string>
            {
                ["area"] = area.Name,
                ["vertexIndex"] = index.ToString(CultureInfo.InvariantCulture),
                ["from"] = string.Format(CultureInfo.InvariantCulture, "({0:0.###},{1:0.###})", from.X, from.Y),
                ["requested"] = string.Format(CultureInfo.InvariantCulture, "({0:0.###},{1:0.###})", requested.X, requested.Y),
                ["to"] = string.Format(CultureInfo.InvariantCulture, "({0:0.###},{1:0.###})", expected.X, expected.Y),
                ["angleDeg"] = area.Pattern.AngleDeg.ToString(CultureInfo.InvariantCulture),
                ["routeEdited"] = area.RouteEdited.ToString()
            };
            return VisualStepOutcome.Ok("꼭지점 이동·수동 편집 적용 확인", data);
        }

        private void RequireStatusContains(string fragment)
        {
            if (string.IsNullOrWhiteSpace(fragment)) return;
            Require((StatusText.Text ?? "").Contains(fragment, StringComparison.Ordinal),
                "상태 문구가 기대를 포함하지 않습니다: " + StatusText.Text);
        }

        private void RequireVertexEditExited()
        {
            Require(!_vertexDragging && _vertexDragGroup < 0 && !Viewport.VertexEditMode,
                "꼭지점 편집 상태가 종료되지 않았습니다.");
        }

        private static void RequirePatternUnchanged(
            WorkingArea area, int pieces, double[] lengths, bool edited, string label)
        {
            Require(area.Pattern != null && area.Pattern.RoutePieces.Count == pieces,
                label + " 패턴 조각 수가 달라졌습니다.");
            Require(area.RoutePieceLengths.Length == lengths.Length,
                label + " 조각 길이 배열 수가 달라졌습니다.");
            for (int i = 0; i < lengths.Length; i++)
                Require(Near(area.RoutePieceLengths[i], lengths[i], 1e-9),
                    label + " 조각 길이가 달라졌습니다: index " + i);
            Require(area.RouteEdited == edited,
                label + " 편집 상태가 달라졌습니다.");
        }

        // ---- 확인 대화상자 응답 큐 ----

        /// <summary>자동 실행 중에만 확인 응답 큐를 쓴다. 완료·실패·중단 뒤에는 제품 대화상자를 쓴다.</summary>
        private bool VisualTestAutomationActive =>
            VisualTestConfirmationPolicy.UsesQueuedResponses(
                VisualTestSession.Current != null, _visualTestRunner);

        internal bool ResolveVisualTestConfirm(string action)
        {
            if (_visualTestConfirmResponses.Count == 0)
                throw new InvalidOperationException("예상하지 않은 확인 대화상자 요청입니다: " + action);
            (string contains, bool yes) = _visualTestConfirmResponses.Dequeue();
            if (string.IsNullOrEmpty(contains) || action.IndexOf(contains, StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("확인 대화상자 동작이 시나리오와 다릅니다: " + action);
            return yes;
        }

        private void QueueVisualTestConfirm(VisualActionSpec spec)
        {
            if (string.IsNullOrEmpty(spec.Confirm)) return;
            _visualTestConfirmResponses.Enqueue((spec.ConfirmAction, spec.Confirm == "yes"));
        }

        private void RequireNoPendingVisualTestConfirms()
        {
            if (_visualTestConfirmResponses.Count == 0) return;
            throw new InvalidOperationException("사용되지 않은 확인 응답이 남아 있습니다: "
                + _visualTestConfirmResponses.Count);
        }

        private async Task<VisualStepOutcome> VisualExportDxfAsync(VisualActionSpec spec)
        {
            VisualTestSession session = VisualTestSession.Current;
            Require(session != null, "시각 테스트 세션이 없습니다.");
            SidebarTabs.SelectedIndex = ExportTabIndex;
            Require(TryCaptureAllAreaRoutes(out var routesBefore, out string captureError), captureError);
            SetVisualReviewScope(0);
            ReviewSnapshot allSnapshot = BuildReviewSnapshot();
            int outputScope = spec.Scope ?? 0;
            SetVisualReviewScope(outputScope);
            ReviewSnapshot expectedSnapshot = BuildReviewSnapshot();

            string fileName = string.IsNullOrWhiteSpace(spec.FileName) ? "result.dxf" : spec.FileName;
            string path = VisualArtifactPath(fileName, "result.dxf");
            if (File.Exists(path) && !string.Equals(spec.Outcome, "rejected", StringComparison.OrdinalIgnoreCase))
                return VisualStepOutcome.Fail("이미 존재하는 DXF 경로입니다: " + path);

            ExportDxfResult result = ExportDxfCore(path, out string exportError);
            if (string.Equals(spec.Outcome, "rejected", StringComparison.OrdinalIgnoreCase))
            {
                Require(result == ExportDxfResult.Rejected, "DXF 저장 거부를 기대했지만 결과가 다릅니다: " + result + " / " + exportError);
                Require(!File.Exists(path), "거부된 DXF 경로에 파일이 생성되었습니다: " + path);
                if (!string.IsNullOrEmpty(spec.FailureContains))
                    Require((StatusText.Text ?? "").Contains(spec.FailureContains),
                        "거부 사유가 기대 문구를 포함하지 않습니다: " + StatusText.Text);
                var rejected = new Dictionary<string, string>
                {
                    ["dxf"] = path,
                    ["result"] = result.ToString(),
                    ["reason"] = StatusText.Text ?? ""
                };
                RequireAllRoutesPreserved(routesBefore, "DXF 저장 거부");
                return VisualStepOutcome.Ok("DXF 저장 거부 확인", rejected);
            }

            Require(result == ExportDxfResult.Success, "DXF 저장이 성공하지 않았습니다: " + exportError);
            Require(File.Exists(path), "DXF 파일이 생성되지 않았습니다.");
            Require(new FileInfo(path).Length > 0, "DXF 파일이 비어 있습니다.");
            session.Report.RegisterDxf(path);

            await Task.Yield();
            DxfDocument document = DxfDocument.Load(path);
            Require(document != null, "DXF 파일을 다시 읽지 못했습니다.");
            var texts = document.Entities.All.OfType<Text>().Select(t => t.Value ?? "").ToList();
            Require(texts.Contains("전기 사양표"), "DXF에 전기 사양표 제목이 없습니다.");
            Require(document.Layers.Contains(DxfExporter.ElectricalSpecLayer), "DXF에 전기 사양 레이어가 없습니다.");
            Require(document.Layers.Contains(DxfExporter.HeatWireLayer), "DXF에 발열경로 레이어가 없습니다.");

            var expectedIds = new HashSet<string>(expectedSnapshot.Circuits.Select(c => c.CircuitId), StringComparer.Ordinal);
            var outOfScope = allSnapshot.Circuits
                .Where(c => !expectedIds.Contains(c.CircuitId))
                .ToList();
            RequireDxfExportContent(document, expectedSnapshot, outOfScope, out Dictionary<string, string> content);

            var data = new Dictionary<string, string>
            {
                ["dxf"] = path,
                ["bytes"] = new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture),
                ["scope"] = outputScope.ToString(CultureInfo.InvariantCulture),
                ["textEntities"] = texts.Count.ToString(CultureInfo.InvariantCulture)
            };
            foreach (KeyValuePair<string, string> entry in content) data[entry.Key] = entry.Value;
            RequireAllRoutesPreserved(routesBefore, "DXF 저장");
            return VisualStepOutcome.Ok("DXF 저장·재읽기·범위 일치 확인", data);
        }

        /// <summary>
        /// DXF 재읽기 내용이 요청 범위와 일치하는지 확인한다. 전기 사양표는 Y 좌표로 묶어
        /// 회로 한 행의 셀들을 비교하고, 범위 밖 회로 ID·발열경로 폴리라인 수·영역 색도
        /// 기대 집합과 대조한다. 파일 순서에 의존하지 않는다.
        /// </summary>
        private void RequireDxfExportContent(
            DxfDocument document, ReviewSnapshot expected,
            IReadOnlyList<CircuitLengthInfo> outOfScopeCircuits,
            out Dictionary<string, string> metrics)
        {
            metrics = new Dictionary<string, string>(StringComparer.Ordinal);
            IReadOnlyList<WorkingArea> areas = expected.ReadyAreas;
            Require(areas.Count > 0, "DXF 요청 범위에 확정된 발열영역이 없습니다.");
            Require(expected.Table != null && expected.Table.Rows.Count > 0, "DXF 요청 범위에 전기 사양 행이 없습니다.");

            WorkingSideDefinition side = _workingSide?.Confirmed;
            List<RouteExportCircuit> circuits = CollectExportCircuits(areas, side, out string collectError);
            Require(circuits != null, "DXF 범위 검사용 확정 회로를 읽지 못했습니다 — " + collectError);
            int expectedPieces = circuits.Sum(c => c.Pieces.Count);

            List<Polyline2D> wires = document.Entities.All.OfType<Polyline2D>()
                .Where(p => string.Equals(p.Layer?.Name, DxfExporter.HeatWireLayer, StringComparison.OrdinalIgnoreCase))
                .ToList();
            List<Polyline2D> rings = document.Entities.All.OfType<Polyline2D>()
                .Where(p => string.Equals(p.Layer?.Name, DxfExporter.HeatAreaLayer, StringComparison.OrdinalIgnoreCase))
                .ToList();
            Require(wires.Count == expectedPieces, string.Format(CultureInfo.InvariantCulture,
                "DXF 발열경로 폴리라인 수가 확정 조각 수와 다릅니다: {0} != {1}", wires.Count, expectedPieces));
            Require(rings.Count == areas.Count, string.Format(CultureInfo.InvariantCulture,
                "DXF 발열영역 외곽 수가 요청 영역 수와 다릅니다: {0} != {1}", rings.Count, areas.Count));

            var allowed = new HashSet<(byte R, byte G, byte B)>();
            foreach (WorkingArea area in areas) allowed.Add((area.Color.R, area.Color.G, area.Color.B));
            var used = new HashSet<(byte R, byte G, byte B)>();
            foreach (Polyline2D wire in wires)
            {
                Require(wire.Color != null, "DXF 발열경로 폴리라인에 색이 없습니다.");
                var key = (wire.Color.R, wire.Color.G, wire.Color.B);
                Require(allowed.Contains(key), string.Format(CultureInfo.InvariantCulture,
                    "DXF 발열경로 색 {0},{1},{2}이 요청 영역 색이 아닙니다.", key.R, key.G, key.B));
                used.Add(key);
            }
            foreach (Polyline2D ring in rings)
            {
                Require(ring.Color != null, "DXF 발열영역 외곽에 색이 없습니다.");
                var key = (ring.Color.R, ring.Color.G, ring.Color.B);
                Require(allowed.Contains(key), string.Format(CultureInfo.InvariantCulture,
                    "DXF 발열영역 색 {0},{1},{2}이 요청 영역 색이 아닙니다.", key.R, key.G, key.B));
                used.Add(key);
            }
            Require(used.SetEquals(allowed), "DXF에 요청 영역 색이 모두 나타나지 않았습니다: "
                + used.Count + " / " + allowed.Count);

            List<Text> specTexts = document.Entities.All.OfType<Text>()
                .Where(t => string.Equals(t.Layer?.Name, DxfExporter.ElectricalSpecLayer, StringComparison.OrdinalIgnoreCase))
                .ToList();
            Require(specTexts.Count > 0, "DXF 전기 사양표 텍스트가 없습니다.");
            var inv = CultureInfo.InvariantCulture;
            var byRow = specTexts.GroupBy(t => Math.Round(t.Position.Y, 6)).ToList();
            foreach (ElectricalSpecRow row in expected.Table.Rows)
            {
                List<Text> group = byRow.FirstOrDefault(g => g.Any(t =>
                    string.Equals((t.Value ?? "").Trim(), row.CircuitId, StringComparison.Ordinal)))?.ToList();
                Require(group != null, "DXF 전기표에 회로 행이 없습니다: " + row.CircuitId);
                List<string> cells = group.Select(t => (t.Value ?? "").Trim()).ToList();
                string joined = string.Join(" | ", cells);
                Require(cells.Contains(row.LengthM.ToString("F2", inv)),
                    "DXF 회로 " + row.CircuitId + " 길이 셀이 기대와 다릅니다: " + joined);
                Require(cells.Contains(row.ResistanceOhm.ToString("F2", inv)),
                    "DXF 회로 " + row.CircuitId + " 저항 셀이 기대와 다릅니다: " + joined);
                Require(cells.Contains(row.VoltageV.ToString("F1", inv)),
                    "DXF 회로 " + row.CircuitId + " 전압 셀이 기대와 다릅니다: " + joined);
                Require(cells.Contains(row.CurrentA.ToString("F2", inv)),
                    "DXF 회로 " + row.CircuitId + " 전류 셀이 기대와 다릅니다: " + joined);
                Require(cells.Contains(row.PowerW.ToString("F1", inv)),
                    "DXF 회로 " + row.CircuitId + " 전력 셀이 기대와 다릅니다: " + joined);
            }
            List<Text> totalRow = byRow.FirstOrDefault(g => g.Any(t =>
                string.Equals((t.Value ?? "").Trim(), "합계", StringComparison.Ordinal)))?.ToList();
            Require(totalRow != null, "DXF 전기표에 합계 행이 없습니다.");
            List<string> totalCells = totalRow.Select(t => (t.Value ?? "").Trim()).ToList();
            Require(totalCells.Contains(expected.Table.TotalCurrentA.ToString("F2", inv)),
                "DXF 합계 행의 전류가 기대와 다릅니다: " + string.Join(" | ", totalCells));
            Require(totalCells.Contains(expected.Table.TotalPowerW.ToString("F1", inv)),
                "DXF 합계 행의 전력이 기대와 다릅니다: " + string.Join(" | ", totalCells));

            if (outOfScopeCircuits != null)
            {
                foreach (CircuitLengthInfo other in outOfScopeCircuits)
                {
                    Require(!specTexts.Any(t => string.Equals((t.Value ?? "").Trim(), other.CircuitId, StringComparison.Ordinal)),
                        "DXF 전기표에 요청 범위 밖 회로가 섞였습니다: " + other.CircuitId);
                }
            }

            metrics["areas"] = areas.Count.ToString(inv);
            metrics["circuitRows"] = expected.Table.Rows.Count.ToString(inv);
            metrics["heatWires"] = wires.Count.ToString(inv);
            metrics["heatAreaRings"] = rings.Count.ToString(inv);
            metrics["areaColors"] = used.Count.ToString(inv);
        }

        // ---- v2 보조 ----

        private string CheckAreaState(WorkingArea area, VisualAreaExpect expect, out Dictionary<string, string> data)
        {
            data = new Dictionary<string, string>();
            if (area == null) return "작업영역이 없습니다.";
            if (area.Pattern == null) return "발열경로 패턴이 없습니다: " + area.Name;
            if (area.Pattern.RoutePieces.Count == 0) return "패턴 조각이 없습니다: " + area.Name;
            if (!area.PatternDisplayed) return "패턴이 화면에 표시된 상태가 아닙니다: " + area.Name;
            if (area.AppliedInputs == null) return "생성 입력 스냅샷이 없습니다: " + area.Name;
            if (IsPatternInputStale(area)) return "패턴이 stale 상태입니다: " + area.Name;
            if (area.NativePieceIds.Length != area.Pattern.RoutePieces.Count)
                return "네이티브 조각 ID 수가 패턴 조각 수와 다릅니다: " + area.Name;
            if (area.RoutePieceLengths.Length != area.Pattern.RoutePieces.Count)
                return "조각 길이 수가 패턴 조각 수와 다릅니다: " + area.Name;

            int turnCount = area.Pattern.RoutePieces.Count(p => p.Kind == SerpentineRoutePieceKind.TurnConnector);
            int heatingPieces = 0;
            double heatingLength = 0;
            for (int i = 0; i < area.Pattern.RoutePieces.Count; i++)
            {
                if (area.Pattern.RoutePieces[i].Kind != SerpentineRoutePieceKind.HeatingRun) continue;
                heatingPieces++;
                heatingLength += area.RoutePieceLengths[i];
            }
            int circuits = GetCircuitLengths(area).Count;
            double length = area.RoutePieceLengths.Sum();
            data["areaName"] = area.Name;
            data["faceIds"] = string.Join(",", area.FaceIdSet.OrderBy(v => v));
            data["pieces"] = area.Pattern.RoutePieces.Count.ToString(CultureInfo.InvariantCulture);
            data["turns"] = turnCount.ToString(CultureInfo.InvariantCulture);
            data["heatingPieces"] = heatingPieces.ToString(CultureInfo.InvariantCulture);
            data["heatingLengthMm"] = heatingLength.ToString("0.###", CultureInfo.InvariantCulture);
            data["routeEdited"] = area.RouteEdited.ToString();
            data["circuits"] = circuits.ToString(CultureInfo.InvariantCulture);
            data["totalLengthMm"] = length.ToString("0.###", CultureInfo.InvariantCulture);
            data["appliedMarginMm"] = area.AppliedInputs.Margin.ToString("0.###", CultureInfo.InvariantCulture);
            data["appliedSpacingMm"] = area.AppliedInputs.Spacing.ToString("0.###", CultureInfo.InvariantCulture);
            if (area.StartAnchor.HasValue && area.EndAnchor.HasValue)
                data["anchor"] = string.Format(CultureInfo.InvariantCulture, "({0:0.###},{1:0.###})->({2:0.###},{3:0.###})",
                    area.StartAnchor.Value.X, area.StartAnchor.Value.Y, area.EndAnchor.Value.X, area.EndAnchor.Value.Y);

            if (expect == null) return null;

            if (expect.Pieces != null && area.Pattern.RoutePieces.Count != expect.Pieces.Value)
                return string.Format("조각 수가 기대와 다릅니다: {0} 기대 {1} 실제 {2}",
                    area.Name, expect.Pieces.Value, area.Pattern.RoutePieces.Count);
            if (expect.Turns != null && turnCount != expect.Turns.Value)
                return string.Format("연결구간 수가 기대와 다릅니다: {0} 기대 {1} 실제 {2}",
                    area.Name, expect.Turns.Value, turnCount);
            if (expect.Circuits != null && circuits != expect.Circuits.Value)
                return string.Format("회로 수가 기대와 다릅니다: {0} 기대 {1} 실제 {2}",
                    area.Name, expect.Circuits.Value, circuits);
            if (expect.RouteEdited != null && area.RouteEdited != expect.RouteEdited.Value)
                return string.Format("편집 상태가 기대와 다릅니다: {0} 기대 {1} 실제 {2}",
                    area.Name, expect.RouteEdited.Value, area.RouteEdited);
            if (expect.MarginMm != null && Math.Abs(area.AppliedInputs.Margin - expect.MarginMm.Value) > 1e-9)
                return string.Format("적용 마진이 기대와 다릅니다: {0} 기대 {1} 실제 {2}",
                    area.Name, expect.MarginMm.Value, area.AppliedInputs.Margin);
            if (expect.SpacingMm != null && Math.Abs(area.AppliedInputs.Spacing - expect.SpacingMm.Value) > 1e-9)
                return string.Format("적용 간격이 기대와 다릅니다: {0} 기대 {1} 실제 {2}",
                    area.Name, expect.SpacingMm.Value, area.AppliedInputs.Spacing);
            if (expect.LengthMm != null)
            {
                double tolerance = expect.LengthToleranceMm > 0 ? expect.LengthToleranceMm : 0.5;
                if (Math.Abs(length - expect.LengthMm.Value) > tolerance)
                    return string.Format("총 길이가 기대와 다릅니다: {0} 기대 {1:0.###} 실제 {2:0.###} (허용 {3})",
                        area.Name, expect.LengthMm.Value, length, tolerance);
            }
            if (expect.HeatingPieces != null && heatingPieces != expect.HeatingPieces.Value)
                return string.Format("발열선 조각 수가 기대와 다릅니다: {0} 기대 {1} 실제 {2}",
                    area.Name, expect.HeatingPieces.Value, heatingPieces);
            if (expect.HeatingLengthMm != null)
            {
                double tolerance = expect.LengthToleranceMm > 0 ? expect.LengthToleranceMm : 0.5;
                if (Math.Abs(heatingLength - expect.HeatingLengthMm.Value) > tolerance)
                    return string.Format("발열선 길이가 기대와 다릅니다: {0} 기대 {1:0.###} 실제 {2:0.###} (허용 {3})",
                        area.Name, expect.HeatingLengthMm.Value, heatingLength, tolerance);
            }
            return null;
        }

        /// <summary>
        /// 모든 확정 영역의 경로 상태를 실행 전에 캡처한다. 아직 경로가 없는 영역은
        /// null로 기록해, 실행 후 경로가 생기지 않았는지까지 비교할 수 있게 한다.
        /// </summary>
        private bool TryCaptureAllAreaRoutes(out Dictionary<WorkingArea, VisualRouteSnapshot> snapshots, out string error)
        {
            snapshots = new Dictionary<WorkingArea, VisualRouteSnapshot>();
            error = null;
            WorkingSideDefinition side = _workingSide?.Confirmed;
            foreach (WorkingArea area in _areas)
            {
                if (area.Pattern == null)
                {
                    snapshots[area] = null;
                    continue;
                }
                VisualRouteSnapshot snapshot = VisualRouteSnapshotComparer.Capture(area, side, out string captureError);
                if (snapshot == null)
                {
                    error = area.Name + " 확정 경로를 읽지 못했습니다 — " + captureError;
                    return false;
                }
                snapshots[area] = snapshot;
            }
            return true;
        }

        /// <summary>
        /// 실행 전에 캡처한 경로 상태와 현재 상태를 비교한다. <paramref name="skip"/>은
        /// 이번 동작으로 경로가 바뀌는 것이 정상인 영역이며, null이면 모든 영역을 비교한다.
        /// </summary>
        private string RequireAreaRoutesUnchanged(Dictionary<WorkingArea, VisualRouteSnapshot> snapshots,
            WorkingArea skip, string context)
        {
            if (snapshots == null) return null;
            foreach (KeyValuePair<WorkingArea, VisualRouteSnapshot> entry in snapshots)
            {
                WorkingArea area = entry.Key;
                if (ReferenceEquals(area, skip)) continue;
                if (!_areas.Contains(area))
                    return context + " — 영역이 목록에서 사라졌습니다: " + area.Name;
                if (entry.Value == null)
                {
                    if (area.Pattern != null) return context + " — 없던 발열경로가 생겼습니다: " + area.Name;
                    continue;
                }
                if (area.Pattern == null)
                    return context + " — 발열경로가 사라졌습니다: " + area.Name;
                VisualRouteSnapshot current = VisualRouteSnapshotComparer.Capture(
                    area, _workingSide?.Confirmed, out string captureError);
                if (current == null)
                    return context + " — 확정 경로를 읽지 못했습니다: " + area.Name + " — " + captureError;
                if (!VisualRouteSnapshotComparer.IsUnchanged(entry.Value, current, out string difference))
                    return context + " — " + difference;
            }
            return null;
        }

        /// <summary>대상 영역을 제외한 나머지 영역의 보존을 검사한다.</summary>
        private void RequireSiblingRoutesPreserved(Dictionary<WorkingArea, VisualRouteSnapshot> before,
            WorkingArea target, string context)
        {
            string difference = RequireAreaRoutesUnchanged(before, target, context);
            Require(difference == null, difference);
        }

        /// <summary>모든 확정 영역의 경로가 그대로인지 검사한다(읽기·표시 전용 동작용).</summary>
        private void RequireAllRoutesPreserved(Dictionary<WorkingArea, VisualRouteSnapshot> before, string context)
        {
            string difference = RequireAreaRoutesUnchanged(before, null, context);
            Require(difference == null, difference);
        }

        private void ConfigurePatternInputs(VisualActionSpec spec, VisualAreaDefinition definition)
        {
            _ = definition;
            // 시나리오가 값을 명시한 경우에만 덮어쓴다. 그 외에는 화면에 입력된 초안을
            // 그대로 사용해, 사용자가 조건을 바꾼 뒤 생성 버튼을 누르는 흐름과 같게 한다.
            if (spec.MarginMm != null)
                MarginBox.Text = spec.MarginMm.Value.ToString("0.###", CultureInfo.CurrentCulture);
            if (spec.SpacingMm != null)
                SpacingBox.Text = spec.SpacingMm.Value.ToString("0.###", CultureInfo.CurrentCulture);
            if (spec.Direction != null)
                DirectionBox.SelectedIndex = IndexFromDirection((PatternDirection)spec.Direction.Value);
        }

        private WorkingArea ResolveVisualArea(string alias)
        {
            Require(!string.IsNullOrWhiteSpace(alias), "행동에 area 식별자가 없습니다.");
            Require(_visualAreaAliases.TryGetValue(alias, out WorkingArea area) && area != null,
                "영역 식별자를 찾지 못했습니다: " + alias);
            Require(_areas.Contains(area), "식별자가 가리키는 영역이 목록에 없습니다: " + alias);
            return area;
        }

        private static VisualAreaDefinition RequireAreaDefinition(VisualScenario scenario, string alias)
        {
            Require(!string.IsNullOrWhiteSpace(alias), "행동에 area 식별자가 없습니다.");
            VisualAreaDefinition definition = scenario.Areas.FirstOrDefault(a => a.Alias == alias);
            Require(definition != null, "시나리오에 영역 정의가 없습니다: " + alias);
            return definition;
        }

        private static Dictionary<string, int> ParseDisplayDiagnostic(string text)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string part in (text ?? "").Split(' '))
            {
                int equals = part.IndexOf('=');
                if (equals <= 0) continue;
                if (int.TryParse(part.Substring(equals + 1), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int value))
                    result[part.Substring(0, equals)] = value;
            }
            return result;
        }

        private static int NativeCount(Dictionary<string, int> diagnostic, string key)
        {
            Require(diagnostic.TryGetValue(key, out int value), "네이티브 표시 진단에 " + key + " 값이 없습니다.");
            return value;
        }

        private VisualStepOutcome StepLoadModel()
        {
            VisualScenario scenario = RequireScenario();
            LoadFile(scenario.ResolvedModelPath);

            Require(Viewport.IsReady, "뷰어가 준비되지 않았습니다.");
            Require(_workingSide.Confirmed != null, "작업방향이 확정되지 않았습니다.");
            Require(_workingSide.Confirmed.Revision.Value == _modelRevision,
                "작업방향 revision과 모델 revision이 다릅니다.");
            Require(!string.IsNullOrEmpty(_modelPath)
                && string.Equals(Path.GetFullPath(_modelPath), Path.GetFullPath(scenario.ResolvedModelPath),
                    StringComparison.OrdinalIgnoreCase),
                "요청한 모델과 실제 로드된 모델 경로가 다릅니다.");

            ShapeInfo info = Bridge.GetShapeInfo();
            Require(info.Faces == scenario.Model.FaceCount,
                string.Format("면 수가 다릅니다. 기대 {0}, 실제 {1}", scenario.Model.FaceCount, info.Faces));
            Require(info.Solids == scenario.Model.SolidCount,
                string.Format("솔리드 수가 다릅니다. 기대 {0}, 실제 {1}", scenario.Model.SolidCount, info.Solids));

            var data = new Dictionary<string, string>
            {
                ["model"] = _modelPath,
                ["sha256"] = scenario.ActualModelSha256,
                ["faces"] = info.Faces.ToString(CultureInfo.InvariantCulture),
                ["solids"] = info.Solids.ToString(CultureInfo.InvariantCulture),
                ["revision"] = _modelRevision.ToString(CultureInfo.InvariantCulture),
                ["workingSideId"] = _workingSide.Confirmed.Id.Value.ToString(CultureInfo.InvariantCulture)
            };
            return VisualStepOutcome.Ok("모델 로드·작업방향 확인", data);
        }

        private VisualScenario RequireScenario()
        {
            VisualTestSession session = VisualTestSession.Current;
            Require(session != null && session.Scenario != null, "시각 테스트 시나리오가 없습니다.");
            return session.Scenario;
        }

        private WorkingArea RequireActiveArea()
        {
            Require(_activeArea != null, "활성 작업영역이 없습니다.");
            return _activeArea;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
