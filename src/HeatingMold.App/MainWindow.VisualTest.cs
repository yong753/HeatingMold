using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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

        private static readonly string[] VisualTestStepNames =
        {
            "모델 로드",
            "작업영역 선택·확정",
            "인출면·시작/종료점 선택",
            "발열경로 생성",
            "전기 사양 확인",
            "DXF 저장·재읽기"
        };

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

            _visualTestPlan = VisualScenarioCompiler.Compile(session.Scenario, VisualTestStepNames);
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
                if (_visualTestPlan == null || _visualTestPlan.IsLegacy)
                    return await ExecuteLegacyVisualStepAsync(index);

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

        private Task<VisualStepOutcome> ExecuteLegacyVisualStepAsync(int index)
        {
            switch (index)
            {
                case 0: return Task.FromResult(StepLoadModel());
                case 1: return Task.FromResult(StepSelectArea());
                case 2: return Task.FromResult(StepSelectExitAndAnchor());
                case 3: return Task.FromResult(StepGeneratePattern());
                case 4: return Task.FromResult(StepCheckElectrical());
                case 5: return StepExportDxfAsync();
                default: return Task.FromResult(VisualStepOutcome.Fail("알 수 없는 단계 번호입니다: " + index));
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
                case "pickExit": return VisualPickExit(step.Spec, scenario);
                case "cancelExitPick": return VisualCancelExitPick(step.Spec);
                case "generate": return VisualGenerate(step.Spec, scenario);
                case "checkArea": return VisualCheckArea(step.Spec, scenario);
                case "checkAllAreas": return VisualCheckAllAreas(step.Spec, scenario);
                case "addExclusion": return VisualAddExclusion(step.Spec, scenario);
                case "removeExclusion": return VisualRemoveExclusion(step.Spec);
                case "expectNoPattern": return VisualExpectNoPattern(step.Spec);
                case "checkExclusion": return VisualCheckExclusion(step.Spec);
                case "moveVertex": return VisualMoveVertex(step.Spec);
                case "exportDxf": return await VisualExportDxfAsync(step.Spec);
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
            ActivateArea(area);
            RefreshAreaList();
            Require(ReferenceEquals(_activeArea, area), "영역 전환이 적용되지 않았습니다.");
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
            DeleteSelectedArea_Click(this, new RoutedEventArgs());
            Require(_areas.Count == areasBefore - 1, "작업영역이 삭제되지 않았습니다.");
            Require(!_areas.Contains(area), "삭제한 영역이 목록에 남아 있습니다.");
            Require(!_areas.Contains(_activeArea) == false || _activeArea == null || _areas.Contains(_activeArea),
                "활성 영역이 목록 밖을 가리킵니다.");
            _visualAreaAliases.Remove(spec.Area);

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

        private VisualStepOutcome VisualPickExit(VisualActionSpec spec, VisualScenario scenario)
        {
            VisualAreaDefinition definition = RequireAreaDefinition(scenario, spec.Area);
            WorkingArea area = ResolveVisualArea(spec.Area);
            ActivateArea(area);
            RefreshAreaList();
            SidebarTabs.SelectedIndex = PatternTabIndex;
            // 사용자가 현재 화면에 입력해 둔 마진/간격으로 인출면을 고른다.
            // 시나리오 정의값이 아니라 초안을 쓰므로, 조건 변경 후 재선택도 같은 흐름을 검증한다.
            Require(TryGetParams(out double margin, out double spacing, out string error),
                "마진/간격 입력을 읽지 못했습니다: " + error);
            return ApplyExitAndAnchor(area, margin, spacing,
                (int)area.Direction, spec.Exit ?? definition.Exit, spec.Anchor ?? definition.Anchor, "인출면·시작/종료점 적용");
        }

        /// <summary>영역을 활성화한 뒤 화면 입력 초안이 기대값으로 복원됐는지 검사한다.
        /// 생성 결과와 입력이 다르면 stale이어야 한다.</summary>
        private VisualStepOutcome VisualExpectAreaInputs(VisualActionSpec spec, VisualScenario scenario)
        {
            VisualAreaDefinition definition = RequireAreaDefinition(scenario, spec.Area);
            WorkingArea area = ResolveVisualArea(spec.Area);
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
            return VisualStepOutcome.Ok("영역 입력 초안·stale 상태 확인", data);
        }

        private VisualStepOutcome VisualCancelExitPick(VisualActionSpec spec)
        {
            WorkingArea area = ResolveVisualArea(spec.Area);
            ActivateArea(area);
            RefreshAreaList();
            SidebarTabs.SelectedIndex = PatternTabIndex;

            int exitFace = area.ExitFaceId;
            int chainCount = area.ExitChain.Count;
            bool hadAnchors = area.HasExitAnchors;
            Vec2? start = area.StartAnchor;
            Vec2? end = area.EndAnchor;

            PickExitFace_Click(this, new RoutedEventArgs());
            Require(_exitCandidates.Count > 0, "취소할 인출면 후보가 생성되지 않았습니다.");
            CancelExitFacePick();

            Require(_exitCandidates.Count == 0, "인출면 후보 목록이 남아 있습니다.");
            Require(area.ExitFaceId == exitFace, "인출면 취소 후 Face가 바뀌었습니다.");
            Require(area.ExitChain.Count == chainCount, "인출면 취소 후 경계선이 바뀌었습니다.");
            Require(area.HasExitAnchors == hadAnchors, "인출면 취소 후 앵커 상태가 바뀌었습니다.");
            Require(Nullable.Equals(area.StartAnchor, start) && Nullable.Equals(area.EndAnchor, end),
                "인출면 취소 후 앵커 좌표가 바뀌었습니다.");

            var data = new Dictionary<string, string>
            {
                ["alias"] = spec.Area,
                ["exitFaceId"] = exitFace.ToString(CultureInfo.InvariantCulture),
                ["exitChainPoints"] = chainCount.ToString(CultureInfo.InvariantCulture),
                ["hasAnchors"] = hadAnchors.ToString()
            };
            return VisualStepOutcome.Ok("인출면 선택 취소·기존 상태 보존", data);
        }

        private VisualStepOutcome VisualGenerate(VisualActionSpec spec, VisualScenario scenario)
        {
            VisualAreaDefinition definition = RequireAreaDefinition(scenario, spec.Area);
            WorkingArea area = ResolveVisualArea(spec.Area);
            ActivateArea(area);
            RefreshAreaList();
            SidebarTabs.SelectedIndex = PatternTabIndex;
            ConfigurePatternInputs(definition, spec);
            Require(TryGetParams(out _, out _, out string parameterError),
                "마진/간격 입력을 읽지 못했습니다: " + parameterError);

            AppliedGenerationInputs appliedBefore = area.AppliedInputs;
            string statusBefore = StatusText.Text;
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
            VisualAreaExpect expect = spec.Expect ?? definition.Expect;
            string error = CheckAreaState(area, expect, out Dictionary<string, string> data);
            if (error != null) return VisualStepOutcome.Fail(error);
            return VisualStepOutcome.Ok("발열경로 생성·검증", data);
        }

        private VisualStepOutcome VisualCheckArea(VisualActionSpec spec, VisualScenario scenario)
        {
            VisualAreaDefinition definition = RequireAreaDefinition(scenario, spec.Area);
            WorkingArea area = ResolveVisualArea(spec.Area);
            string error = CheckAreaState(area, spec.Expect ?? definition.Expect, out Dictionary<string, string> data);
            if (error != null) return VisualStepOutcome.Fail(error);
            data["alias"] = spec.Area;
            return VisualStepOutcome.Ok("영역 상태 확인", data);
        }

        private VisualStepOutcome VisualCheckAllAreas(VisualActionSpec spec, VisualScenario scenario)
        {
            SidebarTabs.SelectedIndex = ReviewTabIndex;
            ReviewScopeBox.SelectedIndex = 0;
            Require(TryReadElectricalLimits(out TargetPowerElectricalSettings settings, out string settingsError),
                "전기 입력을 읽지 못했습니다: " + settingsError);
            HeatingWireSpec wire = settings.Wire;

            var pieces = 0;
            var turns = 0;
            var lengthTotal = 0.0;
            var checkedAliases = new List<string>();

            foreach (VisualAreaDefinition definition in scenario.Areas)
            {
                WorkingArea area = ResolveVisualArea(definition.Alias);
                string error = CheckAreaState(area, definition.Expect, out Dictionary<string, string> areaData);
                if (error != null)
                    return VisualStepOutcome.Fail("영역 " + definition.Alias + ": " + error);
                checkedAliases.Add(definition.Alias);
                pieces += area.Pattern.RoutePieces.Count;
                turns += area.Pattern.RoutePieces.Count(p => p.Kind == SerpentineRoutePieceKind.TurnConnector);
                lengthTotal += area.RoutePieceLengths.Sum();
            }

            Require(_areas.Count == scenario.Areas.Length,
                "영역 수가 시나리오 정의와 다릅니다. 기대 " + scenario.Areas.Length + ", 실제 " + _areas.Count);
            Require(_visualAreaAliases.Count == scenario.Areas.Length,
                "영역 식별자 매핑 수가 정의와 다릅니다.");

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
            return VisualStepOutcome.Ok("전체 영역·전기 내보내기 가능 확인", data);
        }

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
            QueueVisualTestConfirm(spec);
            bool added = AddActiveAreaExclusion(a, b, out string error);
            RequireNoPendingVisualTestConfirms();
            Require(added, "제외영역을 추가하지 못했습니다: " + error);
            Require(area.Exclusions.Count == before + 1, "제외영역 목록이 증가하지 않았습니다.");
            Require(area.Pattern == null, "제외영역 추가 후 이전 경로가 남아 있습니다.");
            Require(!area.PatternDisplayed, "제외영역 추가 후 이전 경로가 표시된 상태입니다.");
            Require(area.NativePieceIds.Length == 0,
                "제외영역 추가 후 조각 상태가 남아 있습니다.");

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

            QueueVisualTestConfirm(spec);
            bool removed = RemoveActiveAreaExclusions(out string error);
            RequireNoPendingVisualTestConfirms();
            Require(removed, "제외영역을 제거하지 못했습니다: " + error);
            Require(area.Exclusions.Count == 0, "제외영역 목록이 비워지지 않았습니다.");
            Require(area.Pattern == null, "제외영역 제거 후 이전 경로가 남아 있습니다.");

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
            // 경계 접촉은 정상이며, 원본 제외 사각형 대비 허용 거리(0.1mm)를 넘는
            // 침범만 위반으로 센다. 침범 깊이는 구간 전체에서 계산한다.
            var exclusion = (rect[0], rect[1], rect[2], rect[3]);

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
            Require(expected.DistanceTo(from) > 1e-6,
                "요청한 꼭지점 이동이 생성 방향과 수직이어서 실제로 적용되지 않습니다. "
                + string.Format(CultureInfo.InvariantCulture, "시작 ({0:0.###},{1:0.###}) → 요청 ({2:0.###},{3:0.###}), 생성 방향 {4:0.###}도",
                    from.X, from.Y, requested.X, requested.Y, area.Pattern.AngleDeg));

            // 그룹 목록은 확정 후 다시 만들어지므로 이동 대상 점을 미리 보관한다.
            IReadOnlyList<RouteVertexOccurrence> moving = _vertexGroups[index].Occurrences;
            _vertexDragGroup = index;
            CommitVertex(requested);
            _vertexDragGroup = -1;
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
            RefreshVertexHandles();
            ExitVertexEdit();
            Require(constraintKept,
                string.Format(CultureInfo.InvariantCulture,
                    "꼭지점 이동이 생성 방향 제약을 지키지 않았습니다: 기대 ({0:0.######},{1:0.######}), 실제 {2} — {3}",
                    expected.X, expected.Y, actualText, commitStatus));

            Require(area.RouteEdited, "꼭지점 이동이 적용되지 않았습니다: " + commitStatus);
            Require(area.Pattern != null && area.Pattern.RoutePieces.Count > 0, "편집 후 경로가 없습니다.");
            Require(!IsPatternInputStale(area), "편집 후 경로가 stale 상태입니다.");

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

        private static bool SegmentIntersectsRect(Vec2 a, Vec2 b, double[] rect)
        {
            double minX = Math.Min(rect[0], rect[2]);
            double maxX = Math.Max(rect[0], rect[2]);
            double minY = Math.Min(rect[1], rect[3]);
            double maxY = Math.Max(rect[1], rect[3]);
            if (PointInside(a) && PointInside(b)) return true;

            double t0 = 0, t1 = 1;
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            return Clip(-dx, a.X - minX) && Clip(dx, maxX - a.X)
                && Clip(-dy, a.Y - minY) && Clip(dy, maxY - a.Y);

            bool PointInside(Vec2 p) =>
                p.X > minX + 1e-9 && p.X < maxX - 1e-9 && p.Y > minY + 1e-9 && p.Y < maxY - 1e-9;

            bool Clip(double p, double q)
            {
                if (Math.Abs(p) < 1e-12) return q >= 0;
                double r = q / p;
                if (p < 0)
                {
                    if (r > t1) return false;
                    if (r > t0) t0 = r;
                }
                else
                {
                    if (r < t0) return false;
                    if (r < t1) t1 = r;
                }
                return true;
            }
        }

        // ---- 확인 대화상자 응답 큐 ----

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
            ReviewScopeBox.SelectedIndex = spec.Scope ?? 0;

            string fileName = string.IsNullOrWhiteSpace(spec.FileName) ? "result.dxf" : spec.FileName;
            string path = Path.Combine(session.Report.RunDirectory, fileName);
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
            Require(document.Layers.Contains("HEAT_WIRE"), "DXF에 발열경로 레이어가 없습니다.");
            Require(document.Entities.All.OfType<Polyline2D>()
                    .Any(p => string.Equals(p.Layer.Name, "HEAT_WIRE", StringComparison.OrdinalIgnoreCase)),
                "DXF에 발열경로 형상이 없습니다.");

            ReviewSnapshot snapshot = BuildReviewSnapshot();
            foreach (CircuitLengthInfo circuit in snapshot.Circuits)
                Require(texts.Any(t => t.Contains(circuit.CircuitId)), "DXF 전기표에 회로 ID가 없습니다: " + circuit.CircuitId);

            var data = new Dictionary<string, string>
            {
                ["dxf"] = path,
                ["bytes"] = new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture),
                ["circuitRows"] = snapshot.Circuits.Count.ToString(CultureInfo.InvariantCulture),
                ["textEntities"] = texts.Count.ToString(CultureInfo.InvariantCulture)
            };
            return VisualStepOutcome.Ok("DXF 저장·재읽기·전기표 확인", data);
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
            int circuits = GetCircuitLengths(area).Count;
            double length = area.RoutePieceLengths.Sum();
            data["areaName"] = area.Name;
            data["faceIds"] = string.Join(",", area.FaceIdSet.OrderBy(v => v));
            data["pieces"] = area.Pattern.RoutePieces.Count.ToString(CultureInfo.InvariantCulture);
            data["turns"] = turnCount.ToString(CultureInfo.InvariantCulture);
            data["routeEdited"] = area.RouteEdited.ToString();
            data["circuits"] = circuits.ToString(CultureInfo.InvariantCulture);
            data["totalLengthMm"] = length.ToString("0.###", CultureInfo.InvariantCulture);
            data["exitFaceId"] = area.ExitFaceId.ToString(CultureInfo.InvariantCulture);
            data["hasAnchors"] = area.HasExitAnchors.ToString();
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
            if (expect.ExitFaceId != null && area.ExitFaceId != expect.ExitFaceId.Value)
                return string.Format("인출면이 기대와 다릅니다: {0} 기대 {1} 실제 {2}",
                    area.Name, expect.ExitFaceId.Value, area.ExitFaceId);
            if (expect.LengthMm != null)
            {
                double tolerance = expect.LengthToleranceMm > 0 ? expect.LengthToleranceMm : 0.5;
                if (Math.Abs(length - expect.LengthMm.Value) > tolerance)
                    return string.Format("총 길이가 기대와 다릅니다: {0} 기대 {1:0.###} 실제 {2:0.###} (허용 {3})",
                        area.Name, expect.LengthMm.Value, length, tolerance);
            }
            return null;
        }

        private VisualStepOutcome ApplyExitAndAnchor(
            WorkingArea area, double marginMm, double spacingMm, int direction,
            VisualExitSpec exit, VisualAnchorSpec anchorSpec, string detail)
        {
            Require(exit != null, "인출면 정의가 없습니다.");
            SidebarTabs.SelectedIndex = PatternTabIndex;

            DirectionBox.SelectedIndex = IndexFromDirection((PatternDirection)direction);
            MarginBox.Text = marginMm.ToString("0.###", CultureInfo.CurrentCulture);
            SpacingBox.Text = spacingMm.ToString("0.###", CultureInfo.CurrentCulture);
            Require(TryGetParams(out double margin, out double spacing, out string parameterError),
                "마진/간격 입력을 읽지 못했습니다: " + parameterError);
            Require(Math.Abs(margin - marginMm) < 1e-9, "적용된 마진이 요청과 다릅니다.");
            Require(Math.Abs(spacing - spacingMm) < 1e-9, "적용된 간격이 요청과 다릅니다.");

            PickExitFace_Click(this, new RoutedEventArgs());
            int exitCandidateCount = _exitCandidates.Count;
            Require(exitCandidateCount > 0, "인출면 후보가 없습니다.");

            int exitIndex = FindExitCandidate(exit);
            Require(exitIndex >= 0, "시나리오의 인출면 후보를 찾지 못했습니다.");
            if (!ApplyExitCandidate(exitIndex, out string exitError))
                return VisualStepOutcome.Fail(exitError);

            Require(area.ExitFaceId == exit.FaceId,
                string.Format("적용된 인출면 Face가 다릅니다. 기대 {0}, 실제 {1}", exit.FaceId, area.ExitFaceId));
            Require(area.HasExitFace, "인출면 상태가 적용되지 않았습니다.");

            AnchoredCandidate anchor;
            string anchorSource;
            if (anchorSpec != null && anchorSpec.StartUv != null && anchorSpec.EndUv != null)
            {
                var matches = area.AnchorCandidates.Where(c => AnchorMatches(c, anchorSpec)).ToList();
                Require(matches.Count == 1,
                    string.Format("좌표 지문과 일치하는 앵커 후보가 정확히 1개가 아닙니다: {0}개 (전체 후보 {1}개, 사유 {2}, 후보 {3})",
                        matches.Count, area.AnchorCandidates.Count, area.LastExitFailure ?? "없음",
                        string.Join("; ", area.AnchorCandidates.Select(c => string.Format(CultureInfo.InvariantCulture,
                            "({0:0.###},{1:0.###})->({2:0.###},{3:0.###})",
                            c.Start.X, c.Start.Y, c.End.X, c.End.Y)))));
                anchor = matches[0];
                anchorSource = "uv";
            }
            else if (anchorSpec != null)
            {
                Require(area.AnchorCandidates.Count > 0,
                    "시작/종료점 후보가 없습니다. " + (area.LastExitFailure ?? "") +
                    " (인출 후보 " + exitCandidateCount + "개)");
                Require(anchorSpec.Index < area.AnchorCandidates.Count,
                    string.Format("앵커 후보 인덱스가 범위를 벗어났습니다. 후보 {0}개, 요청 {1}",
                        area.AnchorCandidates.Count, anchorSpec.Index));
                anchor = area.AnchorCandidates[anchorSpec.Index];
                anchorSource = "index";
            }
            else
            {
                Require(area.AnchorCandidates.Count == 1,
                    "앵커가 자동 선택되지 않았습니다. 시나리오에 anchor를 지정하세요.");
                anchor = area.AnchorCandidates[0];
                anchorSource = "auto";
            }

            if (!ApplyAnchorCandidate(anchor, out string anchorError))
                return VisualStepOutcome.Fail(anchorError);

            Require(area.HasExitAnchors, "시작/종료점이 적용되지 않았습니다.");
            Require(ReferenceEquals(area.SelectedAnchorCandidate, anchor) &&
                area.SelectedAnchorCandidate.StartBinding == anchor.StartBinding &&
                area.SelectedAnchorCandidate.EndBinding == anchor.EndBinding,
                "선택한 후보 identity/원본 edge binding이 보존되지 않았습니다.");
            Require(string.IsNullOrEmpty(area.LastExitFailure), "인출면 처리 실패 사유가 남아 있습니다: " + area.LastExitFailure);
            Require(area.ExitChain.Count >= 2, "인출 경계선이 유효하지 않습니다.");

            double exitLength = 0;
            for (int i = 1; i < area.ExitChain.Count; i++) exitLength += Dist(area.ExitChain[i - 1], area.ExitChain[i]);
            var data = new Dictionary<string, string>
            {
                ["alias"] = area.Name,
                ["exitFaceId"] = area.ExitFaceId.ToString(CultureInfo.InvariantCulture),
                ["exitCandidates"] = exitCandidateCount.ToString(CultureInfo.InvariantCulture),
                ["exitChainPoints"] = area.ExitChain.Count.ToString(CultureInfo.InvariantCulture),
                ["exitLengthMm"] = exitLength.ToString("0.###", CultureInfo.InvariantCulture),
                ["anchorSource"] = anchorSource,
                ["candidateId"] = anchor.CandidateId,
                ["startEdgeKey"] = anchor.StartBinding.Value.EdgeKey.ToString(CultureInfo.InvariantCulture),
                ["endEdgeKey"] = anchor.EndBinding.Value.EdgeKey.ToString(CultureInfo.InvariantCulture),
                ["anchor"] = string.Format(CultureInfo.InvariantCulture, "({0:0.###},{1:0.###})->({2:0.###},{3:0.###})",
                    anchor.Start.X, anchor.Start.Y, anchor.End.X, anchor.End.Y)
            };
            return VisualStepOutcome.Ok(detail, data);
        }

        private void ConfigurePatternInputs(VisualAreaDefinition definition, VisualActionSpec spec)
        {
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

        private static bool AnchorMatches(AnchoredCandidate candidate, VisualAnchorSpec spec)
        {
            double tolerance = spec.ToleranceMm > 0 ? spec.ToleranceMm : 0.5;
            return (Dist2(candidate.Start, spec.StartUv) <= tolerance && Dist2(candidate.End, spec.EndUv) <= tolerance)
                || (Dist2(candidate.Start, spec.EndUv) <= tolerance && Dist2(candidate.End, spec.StartUv) <= tolerance);
        }

        private static double Dist2(Vec2 point, double[] uv)
        {
            if (uv == null || uv.Length != 2) return double.MaxValue;
            double dx = point.X - uv[0];
            double dy = point.Y - uv[1];
            return Math.Sqrt(dx * dx + dy * dy);
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

        // ---- v1: 기존 고정 7단계 ----

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

        private VisualStepOutcome StepSelectArea()
        {
            VisualScenario scenario = RequireScenario();
            SidebarTabs.SelectedIndex = AreaTabIndex;
            ClearFaceSelectionOnly();
            foreach (int faceId in scenario.Area.FaceIds)
                OnFacePicked(faceId, true);

            var expected = new HashSet<int>(scenario.Area.FaceIds);
            Require(_selectedFaceIds.SetEquals(expected), "선택한 면 집합이 시나리오와 다릅니다.");

            ConfirmWorkingArea();
            Require(_areas.Count == 1, "작업영역이 정확히 1개 생성되지 않았습니다.");
            WorkingArea area = _activeArea;
            Require(area != null, "활성 작업영역이 없습니다.");
            Require(area.FaceIdSet.SetEquals(expected), "확정된 작업영역의 면 집합이 다릅니다.");
            Require(area.Region.Count == 1, "작업영역 경계 루프가 1개가 아닙니다.");
            Require(scenario.Expect == null || scenario.Expect.AreaCount == null
                || scenario.Expect.AreaCount.Value == _areas.Count,
                "작업영역 수가 시나리오 기대값과 다릅니다.");

            var data = new Dictionary<string, string>
            {
                ["areaName"] = area.Name,
                ["faceIds"] = string.Join(",", area.FaceIdSet.OrderBy(v => v)),
                ["regionLoops"] = area.Region.Count.ToString(CultureInfo.InvariantCulture)
            };
            return VisualStepOutcome.Ok("영역 1개 확정", data);
        }

        private VisualStepOutcome StepSelectExitAndAnchor()
        {
            VisualScenario scenario = RequireScenario();
            WorkingArea area = RequireActiveArea();
            return ApplyExitAndAnchor(area, scenario.Area.MarginMm, scenario.Area.SpacingMm,
                scenario.Area.Direction, scenario.Exit, scenario.Anchor, "인출면과 시작/종료점 적용");
        }

        private VisualStepOutcome StepGeneratePattern()
        {
            VisualScenario scenario = RequireScenario();
            WorkingArea area = RequireActiveArea();
            SidebarTabs.SelectedIndex = PatternTabIndex;

            Generate_Click(this, new RoutedEventArgs());
            Require(area.Pattern != null, "발열경로 패턴이 생성되지 않았습니다: " + StatusText.Text);
            Require(area.Pattern.RoutePieces.Count > 0, "패턴 조각이 없습니다.");
            Require(area.PatternDisplayed, "패턴이 화면에 표시된 상태가 아닙니다.");
            Require(area.AppliedInputs != null, "생성 입력 스냅샷이 없습니다.");
            Require(!IsPatternInputStale(area), "생성 직후 패턴이 stale 상태입니다.");
            Require(area.NativePieceIds.Length == area.Pattern.RoutePieces.Count,
                "네이티브 조각 ID 수가 패턴 조각 수와 다릅니다.");
            Require(area.RoutePieceLengths.Length == area.Pattern.RoutePieces.Count,
                "조각 길이 수가 패턴 조각 수와 다릅니다.");

            int turnCount = area.Pattern.RoutePieces.Count(p => p.Kind == SerpentineRoutePieceKind.TurnConnector);
            if (scenario.Expect?.PieceCount != null)
                Require(area.Pattern.RoutePieces.Count == scenario.Expect.PieceCount.Value,
                    string.Format("조각 수가 기대값과 다릅니다. 기대 {0}, 실제 {1}",
                        scenario.Expect.PieceCount.Value, area.Pattern.RoutePieces.Count));
            if (scenario.Expect?.TurnCount != null)
                Require(turnCount == scenario.Expect.TurnCount.Value,
                    string.Format("연결구간 수가 기대값과 다릅니다. 기대 {0}, 실제 {1}",
                        scenario.Expect.TurnCount.Value, turnCount));

            ReviewSnapshot snapshot = BuildReviewSnapshot();
            Require(snapshot.Circuits.Count == 1, "생성 직후 회로 수가 1이 아닙니다.");

            var data = new Dictionary<string, string>
            {
                ["pieces"] = area.Pattern.RoutePieces.Count.ToString(CultureInfo.InvariantCulture),
                ["turns"] = turnCount.ToString(CultureInfo.InvariantCulture),
                ["lanes"] = area.Pattern.LaneCount.ToString(CultureInfo.InvariantCulture),
                ["totalLengthMm"] = area.RoutePieceLengths.Sum().ToString("0.###", CultureInfo.InvariantCulture),
                ["circuits"] = snapshot.Circuits.Count.ToString(CultureInfo.InvariantCulture)
            };
            return VisualStepOutcome.Ok("발열경로 생성·화면 표시 확인", data);
        }

        private VisualStepOutcome StepCheckElectrical()
        {
            VisualScenario scenario = RequireScenario();
            WorkingArea area = RequireActiveArea();
            SidebarTabs.SelectedIndex = ReviewTabIndex;

            ReviewScopeBox.SelectedIndex = scenario.Electrical.ReviewScope;
            Require(scenario.Electrical.PresetIndex != 2,
                "직접 입력 발열사 프리셋은 더 이상 지원하지 않습니다. Case 1 또는 Case 2를 사용하세요.");
            WirePresetBox.SelectedIndex = scenario.Electrical.PresetIndex;
            AllowedVoltageBox.Text = scenario.Electrical.VoltageV.ToString("0.###", CultureInfo.CurrentCulture);
            AllowedCurrentBox.Text = scenario.Electrical.AllowedCurrentA.ToString("0.###", CultureInfo.CurrentCulture);
            AllowedPowerBox.Text = scenario.Electrical.AllowedPowerKw.ToString("0.###", CultureInfo.CurrentCulture);

            Require(TryReadElectricalLimits(out TargetPowerElectricalSettings settings, out string settingsError),
                "전기 입력을 읽지 못했습니다: " + settingsError);
            HeatingWireSpec wire = settings.Wire;

            ReviewSnapshot snapshot = BuildReviewSnapshot();
            Require(snapshot.CanExport, "전기 검토 결과가 내보내기 가능 상태가 아닙니다: " + snapshot.BlockReason);
            Require(snapshot.Entries.All(entry => entry.State == ReviewAreaState.Ready),
                "준비되지 않은 작업영역이 있습니다.");
            Require(snapshot.Table.Rows.Count == snapshot.Circuits.Count,
                "전기 사양표 행 수가 회로 수와 다릅니다.");
            Require(ElectricalSpecGrid.Items.Count == snapshot.Table.Rows.Count + 1,
                "화면 전기 사양표 행 수가 예상과 다릅니다.");

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
                Require(Math.Abs(row.ResistanceOhm - expected.ResistanceOhm) < 1e-6, "저항이 계산값과 다릅니다: " + row.CircuitId);
                Require(Math.Abs(row.VoltageV - expected.VoltageV) < 1e-6, "필요 전압이 계산값과 다릅니다: " + row.CircuitId);
                Require(Math.Abs(row.CurrentA - expected.CurrentA) < 1e-6, "전류가 계산값과 다릅니다: " + row.CircuitId);
                Require(Math.Abs(row.PowerW - expected.PowerW) < 1e-3, "전력이 계산값과 다릅니다: " + row.CircuitId);
                Require(row.Assessment != null, "회로 허용치 판정이 없습니다: " + row.CircuitId);
                if (scenario.Electrical.ExpectedWithinLimits is bool expectWithin)
                    Require(row.Assessment.IsWithinLimits == expectWithin,
                        string.Format(CultureInfo.InvariantCulture,
                            "회로 판정이 시나리오 기대와 다릅니다: {0} (기대 {1}, 사유 {2})",
                            row.CircuitId, expectWithin ? "OK" : "NG", row.Assessment.Note));
            }

            var data = new Dictionary<string, string>
            {
                ["circuits"] = snapshot.Circuits.Count.ToString(CultureInfo.InvariantCulture),
                ["totalCurrentA"] = snapshot.Table.TotalCurrentA.ToString("0.######", CultureInfo.InvariantCulture),
                ["totalPowerW"] = snapshot.Table.TotalPowerW.ToString("0.###", CultureInfo.InvariantCulture),
                ["wireDiameterMm"] = wire.DiameterMm.ToString("0.###", CultureInfo.InvariantCulture),
                ["wireResistanceOhmPerM"] = wire.ResistanceOhmPerMeter.ToString("0.###", CultureInfo.InvariantCulture),
                ["targetPowerW"] = snapshot.Table.TotalTargetPowerW.ToString("0.###", CultureInfo.InvariantCulture),
                ["allowedCurrentA"] = snapshot.Limits.CurrentLimitA.ToString("0.###", CultureInfo.InvariantCulture),
                ["allowedPowerW"] = snapshot.Limits.PowerLimitW.ToString("0.###", CultureInfo.InvariantCulture),
                ["okCircuits"] = snapshot.Table.WithinLimitCount.ToString(CultureInfo.InvariantCulture),
                ["ngCircuits"] = snapshot.Table.OverLimitCount.ToString(CultureInfo.InvariantCulture)
            };
            return VisualStepOutcome.Ok("전기 사양표·계산 일치 확인", data);
        }

        private async Task<VisualStepOutcome> StepExportDxfAsync()
        {
            VisualTestSession session = VisualTestSession.Current;
            Require(session != null, "시각 테스트 세션이 없습니다.");
            SidebarTabs.SelectedIndex = ExportTabIndex;

            ExportDxfResult result = ExportDxfCore(session.Report.DxfPath, out string exportError);
            Require(result == ExportDxfResult.Success, "DXF 저장이 성공하지 않았습니다: " + exportError);
            Require(File.Exists(session.Report.DxfPath), "DXF 파일이 생성되지 않았습니다.");
            Require(new FileInfo(session.Report.DxfPath).Length > 0, "DXF 파일이 비어 있습니다.");
            session.Report.RegisterDxf(session.Report.DxfPath);

            await Task.Yield();
            DxfDocument document = DxfDocument.Load(session.Report.DxfPath);
            Require(document != null, "DXF 파일을 다시 읽지 못했습니다.");
            var texts = document.Entities.All.OfType<Text>().Select(t => t.Value ?? "").ToList();
            Require(texts.Contains("전기 사양표"), "DXF에 전기 사양표 제목이 없습니다.");
            Require(document.Layers.Contains(DxfExporter.ElectricalSpecLayer), "DXF에 전기 사양 레이어가 없습니다.");
            Require(document.Layers.Contains("HEAT_WIRE"), "DXF에 발열경로 레이어가 없습니다.");
            Require(document.Entities.All.OfType<Polyline2D>()
                    .Any(p => string.Equals(p.Layer.Name, "HEAT_WIRE", StringComparison.OrdinalIgnoreCase)),
                "DXF에 발열경로 형상이 없습니다.");

            ReviewSnapshot snapshot = BuildReviewSnapshot();
            foreach (CircuitLengthInfo circuit in snapshot.Circuits)
                Require(texts.Any(t => t.Contains(circuit.CircuitId)), "DXF 전기표에 회로 ID가 없습니다: " + circuit.CircuitId);

            var data = new Dictionary<string, string>
            {
                ["dxf"] = session.Report.DxfPath,
                ["bytes"] = new FileInfo(session.Report.DxfPath).Length.ToString(CultureInfo.InvariantCulture),
                ["circuitRows"] = snapshot.Circuits.Count.ToString(CultureInfo.InvariantCulture),
                ["textEntities"] = texts.Count.ToString(CultureInfo.InvariantCulture)
            };
            return VisualStepOutcome.Ok("DXF 저장·재읽기·전기표 확인", data);
        }

        private int FindExitCandidate(VisualExitSpec exit)
        {
            var matches = new List<int>();
            for (int i = 0; i < _exitCandidates.Count; i++)
            {
                ExitSideCandidate candidate = _exitCandidates[i];
                if (candidate.FaceId != exit.FaceId) continue;
                if (exit.StartPoint3D == null)
                {
                    matches.Add(i);
                    continue;
                }

                int count = candidate.Points.Length / 3;
                if (count < 2) continue;
                bool forward = Point3Near(candidate.Points, 0, exit.StartPoint3D, exit.ToleranceMm)
                    && Point3Near(candidate.Points, count - 1, exit.EndPoint3D, exit.ToleranceMm);
                bool reverse = Point3Near(candidate.Points, 0, exit.EndPoint3D, exit.ToleranceMm)
                    && Point3Near(candidate.Points, count - 1, exit.StartPoint3D, exit.ToleranceMm);
                if (forward || reverse) matches.Add(i);
            }
            return matches.Count == 1 ? matches[0] : -1;
        }

        private static bool Point3Near(double[] flat, int index, double[] expected, double tolerance)
        {
            if (flat == null || expected == null || expected.Length != 3) return false;
            int offset = index * 3;
            if (offset + 2 >= flat.Length) return false;
            double dx = flat[offset] - expected[0];
            double dy = flat[offset + 1] - expected[1];
            double dz = flat[offset + 2] - expected[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz) <= tolerance;
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
