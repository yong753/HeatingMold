using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HeatingMold.App.VisualTesting;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Export;
using HeatingMold.OccBridge;
using Microsoft.Win32;

namespace HeatingMold.App
{
    public partial class MainWindow : Window
    {
        private readonly HashSet<int> _selectedFaceIds = new();
        private string _multiFaceRegionFailure;
        private string _modelFileName = "";
        private string _modelPath = "";
        private long _modelRevision;
        // 영역 전환 시 프로그램이 입력 상자를 복원하는 동안 TextChanged가 초안을 덮어쓰지 않게 한다.
        private bool _suppressPatternInputWriteBack;
        // 네이티브 호출은 UI 스레드에서 동기 실행된다. 중복 클릭/중첩 이벤트가
        // 같은 네이티브 스테이징/커밋을 다시 시작하지 않도록 단일 작업 게이트를 둔다.
        private bool _isBusy;
        private string _busyOperation = "";

        // 확정된 작업영역 목록. 항목을 추가/삭제해도 기존 영역과 패턴은 유지된다.
        private readonly List<WorkingArea> _areas = new();
        private WorkingArea _activeArea;
        private bool _syncingAreaSelector;
        // 제외영역 채움 메시(AreaHighlight) 키 — 영역 인스턴스마다 1씩 증가.
        private int _nextAreaId;
        private int _nextNativePieceId;
        private int _nextStageId = int.MinValue + 1_000_000;
        private bool _suppressAreaSelection;
        private static readonly Color[] AreaColors =
        {
            Color.FromRgb(0xFF, 0x00, 0x00), // A 빨강
            Color.FromRgb(0x00, 0x00, 0xFF), // B 파랑
            Color.FromRgb(0x00, 0xFF, 0x00), // C 초록
            Color.FromRgb(0xFF, 0x80, 0x00), // D 주황
            Color.FromRgb(0x80, 0x00, 0xFF), // E 보라
            Color.FromRgb(0x00, 0xFF, 0xFF), // F 청록
            Color.FromRgb(0xFF, 0x00, 0xFF), // G 자홍
            Color.FromRgb(0xFF, 0xFF, 0x00), // H 노랑
        };
        // 제외 체크박스의 프로그램적 해제가 ExcludeMode_Changed를 다시 타지 않게 한다.
        private bool _suppressExcludeToggle;

        public MainWindow()
        {
            InitializeComponent();
            Viewport.FacePicked += OnFacePicked;
            Viewport.EnterPressed += ConfirmWorkingArea;
            Viewport.FaceHovered += OnFaceHovered;
            Viewport.FacesRectanglePicked += OnFacesRectanglePicked;
            Viewport.ExclusionSketchMoved += OnExclusionSketchMoved;
            Viewport.ExclusionSketchCompleted += OnExclusionSketchCompleted;
            Viewport.ExclusionSketchCancelled += () => Bridge.SetExclusionPreview(null);
            Viewport.ExitFacePicked += OnExitFacePicked;
            Viewport.ExitFacePickMoved += OnExitFacePickMoved;
            Viewport.RoutePieceHovered += OnRoutePieceHovered;
            Viewport.VertexHovered += OnVertexHovered;
            Viewport.VertexDragStarted += OnVertexDragStarted;
            Viewport.VertexDragMoved += OnVertexDragMoved;
            Viewport.VertexDragCompleted += OnVertexDragCompleted;
            Viewport.VertexDragCancelled += OnVertexDragCancelled;
            Viewport.VertexEscape += VertexEscape;
            PreviewKeyDown += OnWindowPreviewKeyDown;
            Loaded += OnLoaded;
            UpdateActionStates();
            InitializeSidebar();
            InitializeElectricalUi();
        }

        private bool TryEnterBusy(string operation)
        {
            if (_isBusy)
            {
                StatusText.Text = string.IsNullOrEmpty(_busyOperation)
                    ? "다른 작업이 진행 중입니다 — 완료된 뒤 다시 시도하세요"
                    : _busyOperation + " 작업이 진행 중입니다 — 완료된 뒤 다시 시도하세요";
                return false;
            }
            _isBusy = true;
            _busyOperation = operation;
            UpdateActionStates();
            return true;
        }

        private void ExitBusy()
        {
            _isBusy = false;
            _busyOperation = "";
            UpdateActionStates();
        }

        // 사이드바 요약/버튼은 현재 선택·확정 상태와 항상 일치해야 한다.
        private void UpdateActionStates()
        {
            int selected = _selectedFaceIds.Count;
            bool vertexActive = Viewport != null && Viewport.VertexEditMode;
            bool patternInputValid = TryGetParams(out _, out _, out string patternInputError);
            bool activeStale = _activeArea != null && IsPatternInputStale(_activeArea);
            // 편집 중에는 영역 구성/생성/삭제로 경로와 핸들이 어긋나지 않게 막는다.
            if (ConfirmAreaButton != null) ConfirmAreaButton.IsEnabled = selected > 0 && !vertexActive && !_isBusy;
            if (ClearConfirmedAreaButton != null) ClearConfirmedAreaButton.IsEnabled = _activeArea != null && !vertexActive && !_isBusy;
            if (GenerateButton != null)
                GenerateButton.IsEnabled = _activeArea != null && !vertexActive && patternInputValid && !_isBusy;
            if (SplitAreaButton != null)
                SplitAreaButton.IsEnabled = _activeArea != null && !vertexActive && !_isBusy
                    && patternInputValid && !activeStale && _activeArea.TargetPowerW is > 0;
            if (ExportButton != null) ExportButton.IsEnabled = !_isBusy;
            if (StepExportButton != null) StepExportButton.IsEnabled = !_isBusy;
            if (OpenButton != null) OpenButton.IsEnabled = !_isBusy;
            if (OpenExampleButton != null) OpenExampleButton.IsEnabled = !_isBusy;
            if (PatternInputErrorText != null)
            {
                PatternInputErrorText.Text = patternInputValid ? "" : patternInputError;
                PatternInputErrorText.Visibility = patternInputValid ? Visibility.Collapsed : Visibility.Visible;
            }
            if (SelectionSummaryText != null)
                SelectionSummaryText.Text = selected > 0
                    ? string.Format("선택 {0}개 · 새 작업영역 확정 대기", selected)
                    : _activeArea != null
                        ? string.Format("{0} 영역 활성 · Face {1}개", _activeArea.Name, _activeArea.FaceIdSet.Count)
                        : "Face를 선택하세요";
            if (GenerateHintText != null)
                GenerateHintText.Text = _activeArea == null
                    ? "작업 영역을 확정해야 생성할 수 있습니다."
                    : !patternInputValid
                        ? "입력 오류를 수정한 뒤 생성할 수 있습니다."
                        : activeStale
                            ? (_activeArea.RouteEdited
                                ? "입력 조건이 변경되었습니다 — 수동 편집된 이전 결과가 표시 중이며 재생성해야 반영됩니다."
                                : "입력 조건이 변경되었습니다 — 이전 결과가 표시 중이며 재생성해야 반영됩니다.")
                            : string.Format("{0} 영역에 발열 경로를 생성합니다.", _activeArea.Name);

            bool vertexEligible = _activeArea?.Pattern != null
                && _activeArea.Pattern.RoutePieces.Count > 0
                && _activeArea.Paths.Count == 0
                && !activeStale;
            if (VertexEditModeBox != null) VertexEditModeBox.IsEnabled = (vertexEligible || vertexActive) && !_isBusy;

            // 제외 도구는 확정 작업영역이 있을 때만, 다른 편집 중에는 비활성화한다.
            bool exclusionEligible = ExclusionEligible() && !vertexActive && !_isBusy;
            if (ExcludeModeBox != null) ExcludeModeBox.IsEnabled = exclusionEligible;
            if (ClearExclusionsButton != null)
                ClearExclusionsButton.IsEnabled = _activeArea != null
                    && _activeArea.Exclusions.Count > 0
                    && !_isBusy;

            if (ActiveAreaStateText != null)
                ActiveAreaStateText.Text = _activeArea == null
                    ? (_areas.Count == 0 ? "영역 없음" : "선택 없음")
                    : AreaResultState(_activeArea);

            UpdateProjectActionStates();
        }

        /// <summary>영역 목록/선택기에 표시할 결과 상태. 입력 초안과 적용 조건을 구분한다.</summary>
        private string AreaResultState(WorkingArea area)
        {
            if (area == null) return "영역 없음";
            if (area.Pattern == null)
                return area.PatternDisplayed ? "경로 표시 중" : "미생성";
            bool stale = IsPatternInputStale(area);
            if (stale && area.RouteEdited) return "재생성 필요 · 수동 편집됨";
            if (stale) return "재생성 필요";
            if (area.RouteEdited) return "수동 편집됨";
            return "생성 완료";
        }

        private void ConfirmArea_Click(object sender, RoutedEventArgs e) => ConfirmWorkingArea();

        // 뷰포트 HWND에 포커스가 없는 경우(WPF 쪽 포커스)를 위한 Enter 확정 경로.
        private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_visualTestInputLocked)
            {
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape && Viewport != null && Viewport.ExitFacePickMode)
            {
                CancelExitFacePick();
                StatusText.Text = "인출면 선택을 취소했습니다";
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape && Viewport != null && Viewport.VertexEditMode)
            {
                if (!_vertexDragging) ExitVertexEdit();
                e.Handled = true;
                return;
            }
            if (e.Key != Key.Enter) return;
            if (Viewport != null && Viewport.VertexEditMode) { e.Handled = true; return; }
            if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is ComboBox) return;
            // 탭 머리글 포커스 중 Enter는 키보드 탭 전환 기본 동작이므로 가로채지 않는다.
            if (Keyboard.FocusedElement is TabItem) return;
            ConfirmWorkingArea();
            e.Handled = true;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!Viewport.InitViewer())
            {
                ErrorReporter.ShowError("뷰어 초기화 실패",
                    "3D 뷰어(OCCT)를 초기화하지 못했습니다." + Environment.NewLine +
                    "third_party DLL 경로와 그래픽 드라이버를 확인하세요.");
                StatusText.Text = "뷰어 초기화 실패";
                return;
            }

            string mold = OcctEnv.FindMoldStp();
            if (mold != null) StatusText.Text = "뷰어 준비 — STEP 열기 또는 예제 열기로 모델을 선택하세요";
            else StatusText.Text = "뷰어 준비 — 'STEP 열기'로 모델 파일을 선택하세요";

            if (VisualTestSession.Current != null)
                Dispatcher.BeginInvoke(new Action(StartVisualTestIfConfigured));
        }

        private void OpenExample_Click(object sender, RoutedEventArgs e)
        {
            if (_visualTestInputLocked) return;
            string mold = OcctEnv.FindMoldStp();
            if (string.IsNullOrEmpty(mold))
            {
                StatusText.Text = "예제 STEP을 찾을 수 없습니다 — STEP 열기로 파일을 선택하세요";
                return;
            }
            LoadFile(mold);
        }

        private void LoadFile(string path)
        {
            if (!ConfirmDiscardSession("모델 열기")) return;
            ExitVertexEdit();
            ResetWorkingSide();
            var sw = Stopwatch.StartNew();
            if (!Bridge.LoadStep(path))
            {
                ClearAllAreas(clearNative: true);
                _multiFaceRegionFailure = null;
                _modelFileName = "";
                _modelPath = "";
                ResetProjectBaselineAfterModelLoad(path, false);
                if (ModelPathText != null) ModelPathText.Text = "모델 없음";
                ExitExclusionSketch();
                Viewport.RectangleSelectionMode = false;
                StatusText.Text = "로드 실패: " + path;
                UpdateActionStates();
                ReturnToAreaTab();
                ErrorReporter.ShowError("STEP 로드 실패",
                    "파일: " + path + Environment.NewLine + Environment.NewLine +
                    "파일 형식/경로와 OCCT 로그(occ_viewer_log.txt)를 확인하세요.");
                return;
            }
            sw.Stop();
            _modelFileName = Path.GetFileNameWithoutExtension(path);
            _modelPath = path;
            if (ModelPathText != null)
            {
                ModelPathText.Text = path;
                ModelPathText.ToolTip = path;
            }
            _modelRevision = Bridge.GetModelRevision();
            ResetProjectBaselineAfterModelLoad(path, true);
            // 새 모델이 로드된 뒤에만 편집 표시를 지운다(로드 실패 분기는 아래에서 별도 처리).
            ClearAllRouteEditStates();

            Bridge.SetShapeColor(0.55, 0.65, 0.85);   // 고정 표시색 (스틸 블루)
            ClearAllAreas(clearNative: true);
            _multiFaceRegionFailure = null;
            ExitExclusionSketch();
            Viewport.RectangleSelectionMode = false;

            var info = Bridge.GetShapeInfo();
            StatusText.Text = string.Format("{0} — 솔리드 {1} · 셸 {2} · 면 {3} · 평면 {4} · 로드 {5}ms",
                Path.GetFileName(path), info.Solids, info.Shells, info.Faces, info.Planes, sw.ElapsedMilliseconds);
            // 기본은 +Z. 예제 프리셋이 있는 모델은 그 작업 법선을 쓴다
            // (Mold_Cover_Trim.step → +X 가열면). 파일 해시로 판별하므로
            // '예제 열기'와 'STEP 열기'가 같은 좌표를 쓴다.
            var workingNormal = new Vec3(0, 0, 1);
            if (ExampleModelPresets.TryGetWorkingNormal(path, out var presetNormal))
                workingNormal = presetNormal;
            ApplyWorkingSide(workingNormal);
            UpdateActionStates();
            ReturnToAreaTab();
        }

        private bool TryGetParams(out double margin, out double spacing, out string error)
        {
            margin = 0;
            spacing = 0;
            string marginError = null, spacingError = null;
            bool marginOk = NumericInputValidator.TryMargin(MarginBox?.Text ?? "", out margin, out marginError);
            bool spacingOk = NumericInputValidator.TrySpacing(SpacingBox?.Text ?? "", out spacing, out spacingError);
            error = !marginOk ? marginError + " (mm)" : !spacingOk ? spacingError + " (mm)" : null;
            return marginOk && spacingOk;
        }

        /// <summary>영역에 보관된 입력 초안을 같은 규칙으로 해석한다(비활성 영역 포함).</summary>
        private static bool TryParseAreaParams(string marginText, string spacingText,
            out double margin, out double spacing)
        {
            margin = 0;
            spacing = 0;
            if (!NumericInputValidator.TryMargin(marginText ?? "", out margin, out _)) return false;
            return NumericInputValidator.TrySpacing(spacingText ?? "", out spacing, out _);
        }

        /// <summary>마지막 성공 생성/편집 시점의 원본 조건 지문을 만든다.</summary>
        private string BuildRouteContextKey(WorkingArea area)
        {
            if (area == null) return "";
            var inv = CultureInfo.InvariantCulture;
            var side = _workingSide?.Confirmed;
            var sb = new StringBuilder();
            sb.Append(_modelRevision).Append('|')
              .Append(side?.Id.Value ?? -1).Append('|')
              .Append(side?.Revision.Value ?? -1).Append('|');

            sb.Append("F:");
            foreach (int faceId in area.FaceIdSet.OrderBy(id => id)) sb.Append(faceId).Append(',');
            sb.Append("|R:");
            foreach (var ring in area.Region)
                foreach (Vec2 p in ring)
                    sb.Append(p.X.ToString("R", inv)).Append(',').Append(p.Y.ToString("R", inv)).Append(';');
            sb.Append("|C:");
            foreach (Vec2 p in area.ExitChain)
                sb.Append(p.X.ToString("R", inv)).Append(',').Append(p.Y.ToString("R", inv)).Append(';');
            sb.Append("|C3:");
            foreach (Vec3 p in area.ExitChain3D)
                sb.Append(p.X.ToString("R", inv)).Append(',').Append(p.Y.ToString("R", inv)).Append(',')
                  .Append(p.Z.ToString("R", inv)).Append(';');
            sb.Append("|ER:");
            AppendExitReferences(sb, area.ExitEdgeRefs, inv);
            sb.Append("|A:");
            AppendAnchor(sb, area.StartAnchor, inv);
            sb.Append(';');
            AppendAnchor(sb, area.EndAnchor, inv);
            sb.Append("|SX:");
            AppendSelectedExit(sb, area.SelectedStartExit, inv);
            sb.Append(';');
            AppendSelectedExit(sb, area.SelectedEndExit, inv);
            sb.Append("|CE:");
            foreach (CircuitRoute circuit in area.Circuits)
            {
                sb.Append('[');
                AppendCandidateIdentity(sb, circuit.CandidateId);
                sb.Append(';');
                AppendAnchor(sb, circuit.StartExit?.Point, inv);
                sb.Append(';');
                AppendExitBinding(sb, circuit.StartExit?.Binding, inv);
                sb.Append(';');
                AppendAnchor(sb, circuit.EndExit?.Point, inv);
                sb.Append(';');
                AppendExitBinding(sb, circuit.EndExit?.Binding, inv);
                sb.Append(']');
            }
            sb.Append("|X:");
            foreach (var e in area.Exclusions)
                sb.Append(e.MinX.ToString("R", inv)).Append(',')
                  .Append(e.MinY.ToString("R", inv)).Append(',')
                  .Append(e.MaxX.ToString("R", inv)).Append(',')
                  .Append(e.MaxY.ToString("R", inv)).Append(';');
            sb.Append("|EBP:cad-original-edge-v2,").Append(ExitBoundaryConstraint.Tolerance.ToString("R", inv))
              .Append(',').Append(ExitBoundaryConstraint.AcceptanceTolerance.ToString("R", inv))
              .Append(',').Append(GeometryTolerancePolicy.Version);
            return sb.ToString();
        }

        private static void AppendCandidateIdentity(StringBuilder sb, string candidateId)
        {
            if (candidateId == null) { sb.Append('-'); return; }
            sb.Append(candidateId.Length).Append(':').Append(candidateId);
        }

        private static void AppendSelectedExit(StringBuilder sb, ExitBoundaryPoint? point, CultureInfo inv)
        {
            if (!point.HasValue) { sb.Append('-'); return; }
            ExitBoundaryPoint value = point.Value;
            AppendAnchor(sb, value.Point, inv);
            sb.Append('/').Append(value.SegmentIndex).Append('/')
              .Append(value.SegmentParameter.ToString("R", inv)).Append('/');
            AppendExitBinding(sb, value.Binding, inv);
        }

        private static void AppendExitReferences(StringBuilder sb, IEnumerable<ExitEdgeReference> references,
            CultureInfo inv)
        {
            foreach (ExitEdgeReference reference in references)
            {
                if (reference == null) { sb.Append("missing;"); continue; }
                sb.Append(reference.Revision).Append(',').Append(reference.EdgeKey).Append(',')
                  .Append(reference.FaceId).Append(',').Append(reference.FirstParameter.ToString("R", inv)).Append(',')
                  .Append(reference.LastParameter.ToString("R", inv)).Append(',')
                  .Append(reference.Reversed ? '1' : '0').Append(';');
            }
        }

        private static void AppendExitBinding(StringBuilder sb, ExitEdgeBinding? binding, CultureInfo inv)
        {
            if (!binding.HasValue) { sb.Append('-'); return; }
            ExitEdgeBinding value = binding.Value;
            sb.Append(value.ModelRevision).Append(',').Append(value.WorkingContextId).Append(',')
              .Append(value.WorkingContextRevision).Append(',').Append(value.EdgeKey).Append(',')
              .Append(value.FirstParameter.ToString("R", inv)).Append(',')
              .Append(value.LastParameter.ToString("R", inv)).Append(',')
              .Append(value.Parameter.ToString("R", inv)).Append(',').Append(value.Reversed ? '1' : '0');
        }

        private static void AppendAnchor(StringBuilder sb, Vec2? anchor, CultureInfo inv)
        {
            if (!anchor.HasValue) { sb.Append('-'); return; }
            sb.Append(anchor.Value.X.ToString("R", inv)).Append(',')
              .Append(anchor.Value.Y.ToString("R", inv));
        }

        private AppliedGenerationInputs CaptureAppliedInputs(WorkingArea area, double margin, double spacing) =>
            new()
            {
                Margin = margin,
                Spacing = spacing,
                Direction = area.Direction,
                RouteContextKey = BuildRouteContextKey(area)
            };

        /// <summary>표시 중인 경로가 현재 입력 초안이나 원본 조건보다 오래되었는지 판단한다.</summary>
        internal bool IsPatternInputStale(WorkingArea area)
        {
            if (area?.Pattern == null) return false;
            // 생성 출처를 알 수 없는 결과는 최신으로 간주하지 않는다.
            if (area.AppliedInputs == null) return true;
            if (!TryParseAreaParams(area.MarginText, area.SpacingText, out double margin, out double spacing))
                return true;
            AppliedGenerationInputs applied = area.AppliedInputs;
            return Math.Abs(margin - applied.Margin) > 1e-9 ||
                   Math.Abs(spacing - applied.Spacing) > 1e-9 ||
                   area.Direction != applied.Direction ||
                   !string.Equals(BuildRouteContextKey(area), applied.RouteContextKey, StringComparison.Ordinal);
        }

        // 입력 문자열은 영역별 초안으로 보존한다. 프로그램이 영역 전환 중 상자를 복원할 때는
        // 아직 화면에 남아 있는 다른 필드 값을 되쓰지 않도록 변경된 필드만 기록한다.
        private void PatternInput_Changed(object sender, TextChangedEventArgs e)
        {
            if (_suppressPatternInputWriteBack) return;
            if (_activeArea != null)
            {
                if (ReferenceEquals(sender, MarginBox)) _activeArea.MarginText = MarginBox.Text;
                else if (ReferenceEquals(sender, SpacingBox)) _activeArea.SpacingText = SpacingBox.Text;
                // 버튼만 비활성화하고 진행 중인 편집 제스처를 남겨두면 확정 시점에
                // 이전 결과를 새 조건으로 덮어쓸 수 있으므로 즉시 종료한다.
                if (IsPatternInputStale(_activeArea))
                {
                    if (Viewport != null && Viewport.VertexEditMode) ExitVertexEdit();
                }
            }
            // 마진/간격이 바뀌면 기존 경로가 stale이 되어 배분 대상에서 빠진다. 분할 버튼이
            // 오래된 배분값으로 분할 수를 계산하지 않도록 파생 배분값을 즉시 다시 계산한다.
            RefreshElectricalTable();
            UpdateActionStates();
        }

        private void Generate_Click(object sender, RoutedEventArgs e)
        {
            if (_activeArea == null) { StatusText.Text = "Face 선택 후 Enter로 작업영역을 확정하세요"; return; }
            if (!WorkingGenerationReady()) return;
            if (!TryEnterBusy("패턴 생성")) return;
            try { GenerateMultiFace(); }
            finally { ExitBusy(); }
        }

        private void OnFaceHovered(int faceIndex)
        {
            if (faceIndex < 0)
            {
                HoverText.Text = "";
                return;
            }
            string type = Bridge.GetFaceType(faceIndex);
            HoverText.Text = string.Format("호버 #{0} ({1})", faceIndex, type);
        }

        private bool ExclusionEligible() => _activeArea?.Frame != null;

        private void ExcludeMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressExcludeToggle) return;
            bool enabled = ExcludeModeBox.IsChecked == true;
            if (enabled && !ExclusionEligible())
            {
                _suppressExcludeToggle = true;
                try { ExcludeModeBox.IsChecked = false; }
                finally { _suppressExcludeToggle = false; }
                StatusText.Text = "제외영역을 그리려면 먼저 작업영역을 확정하세요";
                return;
            }
            Viewport.ExclusionSketchMode = enabled;
            if (!enabled)
            {
                Viewport.CancelExclusionSketch();
                Bridge.SetExclusionPreview(null);
            }
            if (enabled) StatusText.Text = "모델 뷰포트에서 사각형 제외영역을 드래그하세요";
        }

        private void OnFacesRectanglePicked(int[] faceIds)
        {
            if (faceIds == null) return;
            if (ActiveSidebarTab != AreaTabIndex) return;
            _selectedFaceIds.Clear();
            SelectedFacesList.Items.Clear();
            foreach (int faceIndex in faceIds)
            {
                string type = Bridge.GetFaceType(faceIndex);
                _selectedFaceIds.Add(faceIndex);
                SelectedFacesList.Items.Add(string.Format("#{0} {1}", faceIndex, type));
            }
            RefreshSelectedRegion();
        }

        private bool TryViewportPointOnFrame(int x, int y, out Vec2 point, out Vec3 world)
        {
            point = default;
            world = default;
            ProjectionFrame frame = _activeArea?.Frame;
            if (frame == null) return false;
            Vec3 o = frame.Origin, n = frame.Normal;
            double[] hit = Bridge.ScreenPointOnPlane(x, y, o.X, o.Y, o.Z, n.X, n.Y, n.Z);
            if (hit == null || hit.Length < 3 || hit.Any(v => !double.IsFinite(v))) return false;
            world = new Vec3(hit[0], hit[1], hit[2]);
            point = frame.Project(world);
            return true;
        }

        private double[] ExclusionRectangleWire(Vec2 a, Vec2 b)
        {
            double minX = Math.Min(a.X, b.X), maxX = Math.Max(a.X, b.X);
            double minY = Math.Min(a.Y, b.Y), maxY = Math.Max(a.Y, b.Y);
            Vec2[] corners = { new(minX, minY), new(maxX, minY), new(maxX, maxY), new(minX, maxY), new(minX, minY) };
            var xyz = new double[corners.Length * 3];
            ProjectionFrame frame = _activeArea.Frame;
            for (int i = 0; i < corners.Length; i++)
            {
                Vec3 p = frame.Unproject(corners[i]);
                xyz[i * 3] = p.X; xyz[i * 3 + 1] = p.Y; xyz[i * 3 + 2] = p.Z;
            }
            return xyz;
        }

        private void OnExclusionSketchMoved(int x0, int y0, int x1, int y1)
        {
            if (!TryViewportPointOnFrame(x0, y0, out Vec2 a, out _) ||
                !TryViewportPointOnFrame(x1, y1, out Vec2 b, out _) ||
                Math.Abs(a.X - b.X) < 1e-6 || Math.Abs(a.Y - b.Y) < 1e-6)
            {
                Bridge.SetExclusionPreview(null);
                return;
            }
            Bridge.SetExclusionPreview(ExclusionRectangleWire(a, b));
        }

        private void OnExclusionSketchCompleted(int x0, int y0, int x1, int y1)
        {
            Bridge.SetExclusionPreview(null);
            if (!TryViewportPointOnFrame(x0, y0, out Vec2 a, out _) ||
                !TryViewportPointOnFrame(x1, y1, out Vec2 b, out _))
            {
                StatusText.Text = "제외 사각형을 작업 평면에 투영할 수 없습니다";
                return;
            }
            AddActiveAreaExclusion(a, b, out _);
        }

        /// <summary>
        /// 작업 평면 사각형을 활성 영역의 제외영역으로 추가한다. 화면 좌표 핸들러와
        /// 시각 테스트가 같은 본문을 통과하도록 분리했다. 추가는 기존 경로를 무효화하며
        /// 새 경로를 자동 생성하지 않는다.
        /// </summary>
        internal bool AddActiveAreaExclusion(Vec2 a, Vec2 b, out string error)
        {
            error = null;
            double minX = Math.Min(a.X, b.X), maxX = Math.Max(a.X, b.X);
            double minY = Math.Min(a.Y, b.Y), maxY = Math.Max(a.Y, b.Y);
            if (maxX - minX < 0.5 || maxY - minY < 0.5)
            {
                error = "제외 사각형이 너무 작습니다";
                StatusText.Text = error;
                return false;
            }
            if (_activeArea == null)
            {
                error = "제외영역을 추가하려면 먼저 작업영역을 확정하세요";
                StatusText.Text = error;
                return false;
            }
            if (!ConfirmDiscardRouteEdits(_activeArea, "제외영역 추가")) return false;
            ExitVertexEdit();
            var area = _activeArea;
            var rect = (minX, minY, maxX, maxY);
            var candidate = ForkAreaRoute(area);
            candidate.Exclusions.Add(rect);
            if (!TryRefreshEffectiveBoundary(candidate, out error,
                    out bool clearedStart, out bool clearedEnd))
            {
                StatusText.Text = error;
                return false;
            }
            string selectionNote = ExitSelectionResetNote(clearedStart, clearedEnd);
            PrepareExclusionRouteCandidate(candidate);

            // 1) 표시 메시 스테이징 — 실패하면 아무 상태도 바꾸지 않는다.
            if (!StageAreaHighlightMesh(candidate))
            {
                Bridge.DiscardAreaHighlight(candidate.AreaId);
                error = "제외영역 표시를 생성하지 못했습니다 — 이전 제외영역/경로를 유지했습니다";
                StatusText.Text = error;
                return false;
            }

            // 2) 빈 최종 경로도 retain/replace/delete 트랜잭션을 통과한다.
            if (!RebuildNativePatternGeometry(area, candidate, out _, out _))
            {
                Bridge.DiscardAreaHighlight(candidate.AreaId);
                error = "제외영역 추가 후 네이티브 경로 상태를 갱신하지 못했습니다 — 이전 제외영역/경로를 유지했습니다";
                StatusText.Text = error;
                return false;
            }

            // 확정 데이터는 이미 게시되었다. 이후 표시 오류를 경로 롤백으로 취급하지 않는다.
            bool highlightApplied = CommitAreaHighlight(area);
            if (!highlightApplied)
                Bridge.DiscardAreaHighlight(area.AreaId);
            SyncExitFaceUi(area);
            // 경로가 무효화된 영역은 회로표/전기표에서도 즉시 빠져야 한다.
            UpdateCircuitTable();
            UpdateActionStates();
            StatusText.Text = (!highlightApplied
                ? "제외영역을 추가했지만 표시 갱신에 실패했습니다 — 확정된 제외영역/경로 상태는 유지합니다"
                : !area.HasSelectedExits && !string.IsNullOrEmpty(area.LastExitFailure)
                    ? area.LastExitFailure
                    : string.Format("모델 뷰포트 제외영역 추가 — {0}개", area.Exclusions.Count))
                + selectionNote;
            return true;
        }

        /// <summary>제외 변경의 빈 최종 경로/후보를 확정 영역이 아닌 fork에서 준비한다.</summary>
        private void PrepareExclusionRouteCandidate(WorkingArea candidate)
        {
            candidate.Pattern = null;
            candidate.Circuits = new List<CircuitRoute>();
            candidate.CircuitPieceIndices = new List<List<int>>();
            candidate.Paths = new List<List<Vec2>>();
            candidate.ExclusionPieces = null;
            candidate.NativePieceIds = Array.Empty<int>();
            candidate.RoutePieceLengths = Array.Empty<double>();
            candidate.ProjectedPieces = new List<RoutePieceResult>();
            candidate.PatternDisplayed = false;
            candidate.RouteEdited = false;
            candidate.AppliedInputs = null;
            candidate.CommittedMapping = null;
        }

        /// <summary>
        /// 제외영역을 반영한 마진 적용 전 유효 외곽선을 계산만 한다. 선택점/앵커/회로/경로는
        /// 건드리지 않으므로 편집과 로드가 같은 계산을 공유하되 후처리는 각자 수행한다.
        /// 제외가 없으면 원본 CAD 경계를 그대로 돌려준다.
        /// </summary>
        internal static bool TryBuildRegionExitBoundary(CadExitBoundary2D cad,
            IReadOnlyList<Vec2> region,
            IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY)> exclusions,
            out IExitBoundary2D boundary, out string error)
        {
            boundary = null;
            error = null;
            if (cad == null)
            {
                error = "적용영역 원본 CAD 경계가 없어 제외영역 외곽선을 다시 만들 수 없습니다";
                return false;
            }
            if (exclusions == null || exclusions.Count == 0)
            {
                boundary = cad;
                return true;
            }
            if (region == null || region.Count < 3)
            {
                error = "제외영역은 단일 적용영역에서만 지원됩니다";
                return false;
            }
            if (!EffectiveRegionBoundary2D.TryCreate(cad, region, exclusions,
                    out EffectiveRegionBoundary2D effective, out string failure))
            {
                error = failure;
                return false;
            }
            boundary = effective;
            return true;
        }

        /// <summary>
        /// 제외 변경 후 마진 적용 전 유효 외곽선을 다시 만든다. 구멍/분리/무효 제외는
        /// 명시적으로 거부하고, 유효한 기존 인출점만 유지한다.
        /// </summary>
        private bool TryRefreshEffectiveBoundary(WorkingArea candidate, out string error,
            out bool clearedStart, out bool clearedEnd)
        {
            error = null;
            clearedStart = false;
            clearedEnd = false;

            // 분할 영역의 경계는 CAD 외곽 + artificial 절단선의 혼합이다. 제외영역을 바꾸면
            // 절단선 provenance를 재구성할 수 없으므로 기존 경계를 조용히 대체하지 않고 거부한다.
            if (candidate.HasSplitBoundary)
            {
                error = "분할로 만들어진 영역은 절단선 경계를 유지해야 하므로 제외영역을 변경할 수 없습니다. 필요하면 영역을 다시 확정하세요";
                return false;
            }

            CadExitBoundary2D cad = candidate.RegionCadBoundary
                ?? candidate.RegionExitBoundary as CadExitBoundary2D;
            if (cad == null)
            {
                error = "적용영역 원본 CAD 경계가 없어 제외영역 외곽선을 다시 만들 수 없습니다";
                return false;
            }
            candidate.RegionCadBoundary = cad;

            if (!TryBuildRegionExitBoundary(cad,
                    candidate.Region.Count == 1 ? candidate.Region[0] : null,
                    candidate.Exclusions, out IExitBoundary2D boundary, out error))
                return false;

            bool hadStart = candidate.SelectedStartExit.HasValue;
            bool hadEnd = candidate.SelectedEndExit.HasValue;
            candidate.RegionExitBoundary = boundary;
            candidate.ExitBoundary = boundary;
            candidate.SelectedStartExit = RefreshExitSelection(candidate.SelectedStartExit, boundary,
                out clearedStart);
            candidate.SelectedEndExit = RefreshExitSelection(candidate.SelectedEndExit, boundary,
                out clearedEnd);
            if (hadStart) candidate.StartAnchor = candidate.SelectedStartExit?.Point;
            if (hadEnd) candidate.EndAnchor = candidate.SelectedEndExit?.Point;
            return true;
        }

        private static ExitBoundaryPoint? RefreshExitSelection(ExitBoundaryPoint? point,
            IExitBoundary2D boundary, out bool cleared)
        {
            cleared = false;
            if (!point.HasValue) return null;
            ExitBoundaryResolution evaluated = ExitBoundaryConstraint.EvaluateSaved(boundary, point.Value);
            if (evaluated.Succeeded) return evaluated.Point;
            cleared = true;
            return null;
        }

        private static string ExitSelectionResetNote(bool clearedStart, bool clearedEnd) =>
            clearedStart || clearedEnd
                ? string.Format(" — 변경된 외곽선에서 유효하지 않게 된 인출점({0})을 다시 선택하세요",
                    clearedStart && clearedEnd ? "시작·종료" : clearedStart ? "시작" : "종료")
                : string.Empty;

        private void OnFacePicked(int faceIndex, bool toggle)
        {
            // 뷰포트에서 이미 차단되지만, 다른 경로로 올라온 선택도 후보 목록을 바꾸지 않게 방어한다.
            if (ActiveSidebarTab != AreaTabIndex) return;
            if (faceIndex < 0)
            {
                if (!toggle)
                {
                    ClearFaceSelectionOnly();
                    StatusText.Text = _activeArea == null
                    ? "Face를 선택하세요"
                    : string.Format("{0} 영역 활성 — Face를 선택해 새 영역을 추가하세요", _activeArea.Name);
                }
                UpdateActionStates();
                return;
            }
            string type = Bridge.GetFaceType(faceIndex);
            if (!toggle)
            {
                _selectedFaceIds.Clear();
                SelectedFacesList.Items.Clear();
            }
            if (toggle && _selectedFaceIds.Contains(faceIndex))
            {
                _selectedFaceIds.Remove(faceIndex);
                for (int i = SelectedFacesList.Items.Count - 1; i >= 0; i--)
                    if (SelectedFacesList.Items[i].ToString().StartsWith("#" + faceIndex + " "))
                        SelectedFacesList.Items.RemoveAt(i);
                StatusText.Text = string.Format("적용면에서 제외 — #{0} ({1})", faceIndex, type);
            }
            else
            {
                _selectedFaceIds.Add(faceIndex);
                SelectedFacesList.Items.Add(string.Format("#{0} {1}", faceIndex, type));
                string assigned = AreaNameForFace(faceIndex);
                StatusText.Text = assigned == null
                    ? string.Format("적용면에 추가 — #{0} ({1}) · 총 {2}개", faceIndex, type, _selectedFaceIds.Count)
                    : string.Format("적용면에 추가 — #{0} ({1}) · {2} 영역과 같은 Face", faceIndex, type, assigned);
            }
            RefreshSelectedRegion();
        }

        // 선택은 다음 작업영역의 후보일 뿐, 기존 확정 영역/경로를 절대 변경하지 않는다.
        private void RefreshSelectedRegion()
        {
            Bridge.ClearHover();
            HoverText.Text = "";
            if (_selectedFaceIds.Count == 0)
                StatusText.Text = _activeArea == null
                    ? "Face를 선택하세요"
                    : string.Format("{0} 영역 활성 — Face를 선택해 새 영역을 추가하세요", _activeArea.Name);
            else
                StatusText.Text = string.Format("Face 선택 {0}개 — Enter로 새 작업영역 확정", _selectedFaceIds.Count);
            UpdateActionStates();
        }

        private void ConfirmWorkingArea()
        {
            // WPF 키 경로와 네이티브 뷰포트 Enter가 모두 이 지점을 지나므로 탭 게이트를 한 곳에 둔다.
            if (ActiveSidebarTab != AreaTabIndex) return;
            if (Viewport != null && Viewport.ExclusionSketchMode)
            {
                StatusText.Text = "제외영역 그리기 중에는 영역을 확정할 수 없습니다";
                return;
            }
            if (_selectedFaceIds.Count == 0)
            {
                StatusText.Text = _activeArea == null
                    ? "Face를 선택한 뒤 Enter로 작업영역을 확정하세요"
                    : string.Format("{0} 영역 활성 — 새 영역으로 확정할 Face를 선택하세요", _activeArea.Name);
                return;
            }
            if (_workingSide.Confirmed == null)
            {
                ErrorReporter.ShowWarning("작업영역 확정 실패", "작업 방향을 먼저 확정하세요.");
                return;
            }

            // 같은 Face를 여러 작업영역이 참조할 수 있다. 실제 발열영역이 겹치지 않는지는
            // 사용자가 제외영역/인출점으로 구성하는 입력 전제이며, 여기서 자동 배분하지 않는다.
            if (!TryBuildSelectedRegion(out List<List<Vec2>> region, out ProjectionFrame frame,
                    out ExactRegionContainment regionBoundary, out CadExitBoundary2D exitBoundary,
                    out List<Vec2> sourceChain2D, out List<Vec3> sourceChain3D,
                    out ExitEdgeReference[] sourceRefs))
            {
                // 후보 구성 실패 시 기존 확정 영역·경로·하이라이트를 그대로 둔다.
                ErrorReporter.ShowError("작업영역 확정 실패",
                    _multiFaceRegionFailure ?? "통합 적용영역을 구성하지 못했습니다.");
                StatusText.Text = "작업영역 확정 실패 — 기존 확정 영역과 경로를 유지했습니다";
                return;
            }

            var area = new WorkingArea
            {
                Name = NextAreaName(),
                Color = NextAreaColor(),
                Frame = frame,
                AreaId = ++_nextAreaId,
                // 새 영역은 현재 화면에 입력된 배치 조건을 초안으로 이어받는다.
                Direction = DirectionFromIndex(DirectionBox?.SelectedIndex ?? 0),
                MarginText = MarginBox?.Text ?? "5",
                SpacingText = SpacingBox?.Text ?? "50"
            };
            area.FaceIdSet.UnionWith(_selectedFaceIds);
            area.Region.Add(region[0]);
            area.RegionBoundary = regionBoundary;
            // 인출점 직접 선택은 인출면이 아니라 적용영역 전체 외곽 Edge를 쓴다.
            // source-order 표시 체인과 원본 edge 참조를 함께 보관해야 2D/3D/parameter가 일치한다.
            area.ExitBoundary = exitBoundary;
            area.RegionExitBoundary = exitBoundary;
            area.RegionCadBoundary = exitBoundary;
            area.ExitChain = sourceChain2D;
            area.ExitChain3D = sourceChain3D;
            area.ExitEdgeRefs = sourceRefs.ToList();
            _areas.Add(area);
            _multiFaceRegionFailure = null;

            // 기존 영역의 하이라이트/경로는 건드리지 않고 새 영역만 추가한다.
            ApplyAreaColors();
            ClearFaceSelectionOnly();
            ActivateArea(area);
            RefreshAreaList();
            StatusText.Text = string.Format("작업영역 {0} 추가 — {1}개 Face · 총 {2}개 영역",
                area.Name, area.FaceIdSet.Count, _areas.Count);
            UpdateActionStates();
        }

        // 현재 선택으로 확정 후보 영역을 계산만 한다. 성공 시에만 호출자가 게시하고,
        // 실패해도 기존 확정 표시(하이라이트/경로/2D 영역)는 건드리지 않는다.
        private bool TryBuildSelectedRegion(out List<List<Vec2>> region, out ProjectionFrame frame,
            out ExactRegionContainment boundary, out CadExitBoundary2D exitBoundary,
            out List<Vec2> sourceChain2D, out List<Vec3> sourceChain3D,
            out ExitEdgeReference[] sourceRefs)
        {
            region = new List<List<Vec2>>();
            frame = null;
            boundary = null;
            exitBoundary = null;
            sourceChain2D = new List<Vec2>();
            sourceChain3D = new List<Vec3>();
            sourceRefs = Array.Empty<ExitEdgeReference>();
            _multiFaceRegionFailure = null;
            if (_selectedFaceIds.Count == 0)
            {
                _multiFaceRegionFailure = "작업영역으로 확정할 Face를 먼저 선택하세요";
                return false;
            }

            // Legacy region code is retained behind the migration gate, but must use the confirmed frame.
            if (_workingSide.Confirmed == null) { _multiFaceRegionFailure = "작업 방향을 먼저 확정하세요"; return false; }
            var fixedFrame = _workingSide.Confirmed.Frame;

            // Build the union boundary from shared source CAD edges first;
            // only then approximate those surviving 3D edges for projection.
            RegionBoundaryLoop[] sourceLoops = Bridge.GetSelectedRegionBoundary(
                _selectedFaceIds.OrderBy(id => id).ToArray(), GeometryTolerancePolicy.DistanceTolerance, out string regionFailure);
            if (sourceLoops == null || sourceLoops.Length == 0 || sourceLoops.Any(loop => !loop.Closed))
            {
                _multiFaceRegionFailure = _selectedFaceIds.Any(id => Bridge.FaceRegionIsClosed(id))
                    ? "경계가 없는 닫힌 곡면은 Phase 1에서 미지원입니다 — 경계가 있는 열린 곡면 패치를 선택하세요"
                    : string.IsNullOrWhiteSpace(regionFailure)
                        ? "원본 CAD 위상에서 닫힌 통합 경계를 구성하지 못했습니다. 선택 Face의 공유 Edge 연결을 확인하세요"
                        : "원본 CAD 위상에서 닫힌 통합 경계를 구성하지 못했습니다 — " + regionFailure;
                return false;
            }
            if (sourceLoops.Length != 1)
            {
                _multiFaceRegionFailure = sourceLoops.Length == 2 && SelectionAxisParallelToWorking()
                    ? "이 방향에서는 영역을 구성할 수 없습니다 — 원통/원뿔 축이 작업 방향과 평행합니다. 작업 방향을 변경하세요"
                    : string.Format(
                        "원본 통합 경계가 {0}개 루프로 나뉩니다. 실제 구멍 또는 분리된 적용영역은 현재 경로 모드에서 지원하지 않습니다",
                        sourceLoops.Length);
                return false;
            }
            List<Vec3> firstPts = new();
            for (int k = 0; k + 2 < sourceLoops[0].Points.Length; k += 3)
                firstPts.Add(new Vec3(sourceLoops[0].Points[k], sourceLoops[0].Points[k + 1], sourceLoops[0].Points[k + 2]));
            if (firstPts.Count < 3)
            {
                _multiFaceRegionFailure = "원본 통합 경계의 적응 샘플 점이 부족합니다";
                return false;
            }

            // 표시/생성용 폴리곤과 별개로, 판정은 원본 CAD 경계 참조로만 한다.
            // 참조가 불완전하면 근사 검증으로 대체하지 않고 영역 확정을 실패시킨다.
            ExitEdgeReference[] boundaryReferences = sourceLoops[0].EdgeReferences ?? Array.Empty<ExitEdgeReference>();
            if (!TryValidateRegionBoundaryReferences(boundaryReferences, firstPts.Count, out string referenceFailure))
            {
                _multiFaceRegionFailure = referenceFailure;
                return false;
            }
            var confirmedSide = _workingSide.Confirmed;
            boundary = new ExactRegionContainment(new CadRegionBoundary2D(
                confirmedSide.Id.Value, confirmedSide.Revision.Value, boundaryReferences));
            exitBoundary = new CadExitBoundary2D(
                confirmedSide.Id.Value, confirmedSide.Revision.Value, boundaryReferences);

            List<Vec2> projectedSource = firstPts.Select(fixedFrame.Project).ToList();
            List<Vec2> projected = new(projectedSource);
            if (projected.Count < 3 || Math.Abs(RegionOps.SignedArea(projected)) < 1e-8)
            {
                _multiFaceRegionFailure = "원본 통합 경계를 작업 평면에 유효하게 투영하지 못했습니다";
                return false;
            }
            if (RegionOps.SignedArea(projected) < 0) projected.Reverse();
            region.Add(projected);
            // source-order 체인은 원본 edge 참조의 StartIndex/EndIndex와 1:1로 대응해야 한다.
            // Region 폴리곤은 CCW로 뒤집지만, 인출점 표시/하이라이트용 체인은 뒤집지 않는다.
            sourceChain2D = projectedSource;
            sourceChain3D = firstPts;
            sourceRefs = boundaryReferences;
            frame = fixedFrame;
            return true;
        }

        /// <summary>경계 참조가 루프 점 전체를 빠짐없이 덮는지 확인한다.</summary>
        private static bool TryValidateRegionBoundaryReferences(ExitEdgeReference[] references, int pointCount,
            out string failure)
        {
            failure = null;
            if (references == null || references.Length == 0)
            {
                failure = "영역 경계의 원본 CAD 참조를 구성하지 못했습니다 — 영역을 확정할 수 없습니다";
                return false;
            }
            if (pointCount < 4)
            {
                failure = "원본 통합 경계의 점이 부족합니다";
                return false;
            }
            if (references[0].StartIndex != 0 || references[^1].EndIndex != pointCount - 1)
            {
                failure = "영역 경계의 원본 CAD 참조가 루프 전체를 덮지 않습니다";
                return false;
            }
            for (int i = 0; i < references.Length; i++)
            {
                if (references[i].EndIndex <= references[i].StartIndex ||
                    references[i].Revision <= 0 || references[i].EdgeKey <= 0 ||
                    references[i].FaceId < 0 ||
                    references[i].LastParameter <= references[i].FirstParameter)
                {
                    failure = "영역 경계의 원본 CAD 참조가 유효하지 않습니다";
                    return false;
                }
                if (i + 1 < references.Length && references[i].EndIndex != references[i + 1].StartIndex)
                {
                    failure = string.Format(
                        "영역 경계의 원본 CAD 참조 구간이 연속되지 않습니다 — 참조 {0} 끝 인덱스 {1}, 다음 참조 시작 인덱스 {2}",
                        i, references[i].EndIndex, references[i + 1].StartIndex);
                    return false;
                }
            }
            return true;
        }

        // 선택한 면이 모두 원통/원뿔이고 축이 작업 방향과 평행하면, 그 면은
        // 전 둘레에 걸쳐 위/아래 두 루프로만 나뉘어 영역을 만들 수 없다.
        private bool SelectionAxisParallelToWorking()
        {
            if (_selectedFaceIds.Count == 0 || _workingSide.Confirmed == null) return false;
            var n = _workingSide.Confirmed.Frame.Normal;
            foreach (int id in _selectedFaceIds)
            {
                double[] axis = Bridge.GetFaceAxis(id);
                if (axis == null) return false;
                if (Math.Abs(Vec3.Dot(new Vec3(axis[0], axis[1], axis[2]), n)) < 0.999) return false;
            }
            return true;
        }

        private void GenerateMultiFace()
        {
            if (_activeArea != null && !ConfirmDiscardRouteEdits(_activeArea, "패턴 재생성")) return;
            ExitVertexEdit();
            CancelExitFacePick();
            if (!WorkingGenerationReady()) return;
            var area = _activeArea;
            if (area == null || area.Frame == null || area.Region.Count == 0)
            {
                StatusText.Text = _multiFaceRegionFailure ?? "작업영역을 먼저 확정하세요";
                return;
            }
            if (!TryGetParams(out double margin, out double spacing, out string parameterError))
            {
                StatusText.Text = parameterError;
                return;
            }
            if (!double.TryParse(InsertionDepthBox.Text, out double insertionDepth) ||
                !double.IsFinite(insertionDepth) || insertionDepth < 0)
            {
                StatusText.Text = "삽입 깊이는 0 이상의 유한한 mm 값이어야 합니다";
                return;
            }
            if (insertionDepth != 0)
            {
                StatusText.Text = "작업면 첫 충돌 경로는 현재 깊이 0만 지원합니다. 삽입 깊이 적용은 아직 지원되지 않습니다";
                return;
            }
            if (area.Region.Count != 1)
            {
                StatusText.Text = "선택영역이 서로 떨어져 있습니다. 하나로 연결된 적용영역을 선택하세요";
                return;
            }
            var candidate = ForkAreaRoute(area);
            SerpentineResult? editablePattern;
            List<IReadOnlyList<Vec2>> paths;
            try
            {
                if (!TryGenerateAreaPattern(candidate, margin, spacing,
                        out editablePattern, out paths, out string generationFailure))
                {
                    SyncExitFaceUi(area);
                    StatusText.Text = generationFailure;
                    return;
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "경로 계산 실패: " + ex.Message;
                ErrorReporter.ShowError("경로 계산 실패", ex);
                return;
            }
            bool routePiecesMode = editablePattern != null && editablePattern.RoutePieces.Count > 0;

            // 새 생성은 이전 Pattern의 파생 상태를 상속하지 않는다. 이 모든 준비는
            // 후보 안에서만 수행하며, 실패 시 확정 영역은 수정조차 하지 않는다.
            ApplyPreparedPattern(candidate, editablePattern, paths);

            if (!RebuildNativePatternGeometry(area, candidate, out double total, out int projectedCount))
            {
                SyncExitFaceUi(area);
                return;
            }

            // Rebuild가 확정 영역에 게시한 준비 상태만 UI에 사용한다.
            projectedCount = routePiecesMode ? area.Pattern.RoutePieces.Count : area.Paths.Count;
            Viewport.CircuitHoverMode = routePiecesMode;
            SyncExitFaceUi(area);
            Bridge.ClearHover();
            HoverText.Text = "";
            if (routePiecesMode)
            {
                HoverText.Text = "";
            }
            UpdateCircuitTable();
            int connectors = area.ExclusionPieces?.Sum(c => c.Count(p => p.Kind == SerpentineRoutePieceKind.TurnConnector)) ?? 0;
            int turnCount = routePiecesMode
                ? area.Pattern.RoutePieces.Count(p => p.Kind == SerpentineRoutePieceKind.TurnConnector) : 0;
            if (projectedCount == 0)
            {
                StatusText.Text = "생성 가능한 경로가 없습니다. 마진/간격 또는 선택 영역을 확인하세요";
            }
            else if (routePiecesMode)
            {
                int circuits = area.Circuits.Count;
                string counts = area.Exclusions.Count > 0
                    ? string.Format(" · 연결선 {0}개", turnCount)
                    : string.Empty;
                StatusText.Text = string.Format(
                    "{0} 영역 — {1}개 경로 · 길이 {2:F1}mm · 회로 {3}개",
                    area.Name, projectedCount, total, circuits) + counts + LaneNote(area.Pattern);
            }
            else
            {
                StatusText.Text = string.Format(
                    "{0} 영역 제외경로 — {1}개 경로 · 길이 {2:F1}mm · 연결선 {3}개 · 깊이 0",
                    area.Name, projectedCount, total, connectors) + LaneNote(area.Pattern);
            }
            UpdateActionStates();
        }

        /// <summary>
        /// 한 영역의 발열경로 후보를 계산한다. 확정 영역/UI에는 아무것도 쓰지 않고 성공한
        /// 패턴과 2D 경로만 돌려준다. 일반 생성과 분할 자동 생성이 같은 4분기 규칙을 쓰도록
        /// GenerateMultiFace의 생성부를 그대로 옮겼다.
        /// </summary>
        private bool TryGenerateAreaPattern(WorkingArea candidate, double margin, double spacing,
            out SerpentineResult? pattern, out List<IReadOnlyList<Vec2>> paths, out string failure)
        {
            pattern = null;
            paths = new List<IReadOnlyList<Vec2>>();
            failure = string.Empty;
            if (candidate == null || candidate.Region.Count == 0)
            {
                failure = "경로를 생성할 영역이 없습니다";
                return false;
            }
            if (candidate.Region.Count != 1)
            {
                failure = "선택영역이 서로 떨어져 있습니다. 하나로 연결된 적용영역을 선택하세요";
                return false;
            }
            if (!candidate.HasSelectedExits)
            {
                failure = candidate.LastExitFailure
                    ?? "시작/종료 인출점이 지정되지 않았습니다 — 적용영역 외곽 Edge에서 두 점을 먼저 선택하세요";
                return false;
            }

            // 선택한 두 인출점을 고정한 실제 생성 경로. 분할 후보 탐색과 최종 생성이 같은
            // 헬퍼를 쓰므로, 탐색에서 잰 2D 길이와 확정 경로의 길이가 달라지지 않는다.
            if (!TryGenerateSelectedExitPattern(candidate, margin, spacing,
                    out SerpentineResult? editablePattern, out failure))
                return false;
            paths.Add(editablePattern!.Path);
            if (paths.Count == 0 && (editablePattern == null || editablePattern.RoutePieces.Count == 0))
            {
                failure = "생성 가능한 경로가 없습니다. 마진/간격 또는 선택 영역을 확인하세요";
                return false;
            }
            pattern = editablePattern;
            return true;
        }

        /// <summary>
        /// 선택한 시작/종료 인출점을 고정한 실제 2D 생성 경로. 일반 생성과 분할 후보 평가가
        /// 같은 코드를 쓰도록 한 곳에 모은다. Auto 방향은 UI·인출점 배정과 같은 가로(FrameX)로
        /// 해석하고, 실패하면 무앵커 경로로 대체하지 않는다.
        /// </summary>
        private bool TryGenerateSelectedExitPattern(WorkingArea candidate, double margin, double spacing,
            out SerpentineResult pattern, out string failure)
        {
            pattern = null;
            failure = string.Empty;
            if (candidate == null || candidate.Region.Count != 1 || candidate.Region[0].Count < 3)
            {
                failure = "경로를 생성할 영역이 단일 폐합 영역이 아닙니다.";
                return false;
            }
            if (!candidate.HasSelectedExits)
            {
                failure = candidate.LastExitFailure
                    ?? "시작/종료 인출점이 지정되지 않았습니다 — 적용영역 외곽 Edge에서 두 점을 먼저 선택하세요";
                return false;
            }

            PatternDirection effectiveDirection = EffectiveDirection(candidate.Direction);
            var parameters = new PatternParams
            {
                Margin = margin,
                Spacing = spacing,
                Direction = effectiveDirection,
            };
            AnchoredResult anchored = candidate.Exclusions.Count > 0
                ? Serpentine.GenerateComponentsWithSelectedExits(candidate.Region[0], candidate.Exclusions,
                    parameters, effectiveDirection,
                    candidate.SelectedStartExit.Value, candidate.SelectedEndExit.Value,
                    boundary: candidate.RegionExitBoundary)
                : Serpentine.GenerateWithSelectedExits(candidate.Region[0], null,
                    parameters, effectiveDirection,
                    candidate.SelectedStartExit.Value, candidate.SelectedEndExit.Value,
                    boundary: candidate.RegionExitBoundary);
            if (!anchored.Succeeded)
            {
                failure = ExplainAnchoredFailure(anchored.Failure, anchored.BoundaryFailure);
                return false;
            }
            SerpentineResult generated = anchored.Pattern;
            if (generated == null || generated.RoutePieces.Count == 0 || generated.Path.Count < 2)
            {
                failure = "생성 가능한 경로가 없습니다. 마진/간격 또는 선택 영역을 확인하세요";
                return false;
            }
            pattern = generated;
            return true;
        }

        /// <summary>
        /// 생성된 패턴을 후보 영역에 게시한다. 이전 Pattern의 파생 상태를 상속하지 않으며,
        /// 호출자는 성공한 뒤에만 이 후보를 확정 영역으로 커밋한다.
        /// </summary>
        private static void ApplyPreparedPattern(WorkingArea candidate,
            SerpentineResult? editablePattern, List<IReadOnlyList<Vec2>> paths)
        {
            bool routePiecesMode = editablePattern != null && editablePattern.RoutePieces.Count > 0;
            candidate.Pattern = routePiecesMode ? editablePattern : null;
            candidate.Circuits = new List<CircuitRoute>();
            candidate.CircuitPieceIndices = new List<List<int>>();
            candidate.Paths = routePiecesMode ? new List<List<Vec2>>() : paths.Select(p => p.ToList()).ToList();
            candidate.ExclusionPieces = null;
            if (routePiecesMode)
            {
                int count = editablePattern!.RoutePieces.Count;
                candidate.RoutePieceLengths = new double[count];
                candidate.ProjectedPieces = new List<RoutePieceResult>();
            }
            else
            {
                candidate.NativePieceIds = Array.Empty<int>();
                candidate.RoutePieceLengths = Array.Empty<double>();
                candidate.ProjectedPieces = new List<RoutePieceResult>();
            }
            candidate.RouteEdited = false;
            candidate.PatternDisplayed = routePiecesMode
                ? candidate.Pattern!.RoutePieces.Count > 0
                : candidate.Paths.Count > 0;
        }

        private void OnRoutePieceHovered(int id)
        {
            var area = _areas.FirstOrDefault(a => a.NativePieceIds.Contains(id));
            if (area == null) { Bridge.HighlightCircuit(Array.Empty<int>()); HoverText.Text = ""; return; }
            var groups = CircuitPieceGroups(area);
            int circuit = groups.FindIndex(group => group.Contains(id));
            Bridge.HighlightCircuit(circuit < 0 ? Array.Empty<int>() : groups[circuit].ToArray());
            HoverText.Text = circuit < 0 ? "" : string.Format("회로 #{0} ({1})", circuit + 1, area.Name);
        }
        private static double Distance3D(double[] a, double[] b) =>
            Math.Sqrt((a[0]-b[0])*(a[0]-b[0]) + (a[1]-b[1])*(a[1]-b[1]) + (a[2]-b[2])*(a[2]-b[2]));
        private void UpdateCircuitTable()
        {
            // 검토 탭의 전기 사양표와 뷰포트의 확대 보기가 같은 검토 스냅샷을 사용한다.
            // 회로 ID는 영역명-회로번호이며, 길이는 사양표의 길이 열이 보여 준다.
            // 공통 발열전력 배분도 이 계산 안에서 갱신된다.
            RefreshElectricalTableCore(BuildReviewSnapshot());
        }

        // Group stable route-piece IDs by the canonical split circuits. Disabled
        // turns are separators and contribute neither geometry nor length.
        private List<List<int>> CircuitPieceGroups() => CircuitPieceGroups(_activeArea);

        private List<List<int>> CircuitPieceGroups(WorkingArea area)
        {
            var groups = new List<List<int>>();
            if (area?.Pattern == null) return groups;
            // 조회 중 현재 초안으로 회로를 재생성하지 않는다. 확정 그룹이 없으면 표시하지 않는다.
            if (area.CircuitPieceIndices == null) return groups;
            foreach (List<int> indices in area.CircuitPieceIndices)
            {
                var group = new List<int>();
                foreach (int index in indices)
                {
                    if (index < 0 || index >= area.Pattern.RoutePieces.Count) continue;
                    if (area.Pattern.RoutePieces[index].Points.Count == 0) continue;
                    if (index >= area.NativePieceIds.Length) continue;
                    group.Add(area.NativePieceIds[index]);
                }
                if (group.Count > 0) groups.Add(group);
            }
            return groups;
        }
        private void ClearExclusions_Click(object sender, RoutedEventArgs e)
        {
            RemoveActiveAreaExclusions(out _);
        }

        /// <summary>활성 영역의 모든 제외 사각형을 지운다. 제거도 새 경로를 자동 생성하지 않는다.</summary>
        internal bool RemoveActiveAreaExclusions(out string error)
        {
            error = null;
            var area = _activeArea;
            if (area == null) { error = "작업영역이 없습니다"; StatusText.Text = error; return false; }
            if (!ConfirmDiscardRouteEdits(area, "제외영역 지우기")) return false;
            ExitVertexEdit();
            var candidate = ForkAreaRoute(area);
            candidate.Exclusions.Clear();
            if (!TryRefreshEffectiveBoundary(candidate, out error,
                    out bool clearedStart, out bool clearedEnd))
            {
                StatusText.Text = error;
                return false;
            }
            string selectionNote = ExitSelectionResetNote(clearedStart, clearedEnd);
            PrepareExclusionRouteCandidate(candidate);
            // 제외가 0이면 스테이징할 클리핑 메시가 없다. 기존 채움 표시는 경로
            // 트랜잭션이 성공한 뒤에만 지우며, 실패 시에는 그대로 둔다.
            if (!RebuildNativePatternGeometry(area, candidate, out _, out _))
            {
                error = "제외영역 제거 후 네이티브 경로 상태를 갱신하지 못했습니다 — 이전 제외영역/경로를 유지했습니다";
                StatusText.Text = error;
                return false;
            }
            // 경로 재생성 성공 후에만 채움 메시를 걷어내고 Face 색칠로 되돌린다.
            UpdateActiveAreaHighlight();   // 제외 0: ClearAreaHighlight + ApplyAreaColors
            SyncExitFaceUi(area);
            UpdateCircuitTable();
            UpdateActionStates();
            StatusText.Text = string.Format("{0} 영역의 제외 사각형을 모두 지웠습니다", area.Name)
                + selectionNote;
            return true;
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            if (!TryEnterBusy("DXF 내보내기")) return;
            try { ExportDxfCore(null, out _); }
            finally { ExitBusy(); }
        }

        private enum ExportDxfResult
        {
            Success,
            Canceled,
            Rejected,
            Failed
        }

        /// <summary>
        /// DXF 저장의 공통 경로. explicitPath가 있으면 저장 대화상자만 생략하고
        /// 검토/재검사/작업평면 평면도/임시파일/원자적 교체 보호 로직은 그대로 공유한다.
        /// </summary>
        private ExportDxfResult ExportDxfCore(string explicitPath, out string error)
        {
            error = null;
            // 대화상자를 열기 전에 같은 검토 스냅샷으로 출력 가능 여부를 확정한다.
            ReviewSnapshot preflight = BuildReviewSnapshot();
            if (!preflight.CanExport)
            {
                error = preflight.BlockReason;
                StatusText.Text = "DXF 내보내기 불가 — " + preflight.BlockReason;
                return ExportDxfResult.Rejected;
            }
            long modelRevisionBeforeDialog = _modelRevision;

            string file;
            if (explicitPath != null)
            {
                file = explicitPath;
            }
            else
            {
                string suffix = _reviewCurrentAreaOnly && _activeArea != null
                    ? "_" + _activeArea.Name
                    : "";
                string defaultName = SanitizeFileName(
                    (string.IsNullOrEmpty(_modelFileName) ? "heat_pattern" : _modelFileName) + suffix + "_검토") + ".dxf";
                var dialog = new SaveFileDialog
                {
                    Title = "DXF 내보내기",
                    Filter = "DXF 파일 (*.dxf)|*.dxf|모든 파일 (*.*)|*.*",
                    DefaultExt = ".dxf",
                    AddExtension = true,
                    OverwritePrompt = true,
                    FileName = defaultName
                };
                if (dialog.ShowDialog(this) != true)
                {
                    error = "사용자가 저장을 취소했습니다";
                    StatusText.Text = "DXF 내보내기를 취소했습니다";
                    return ExportDxfResult.Canceled;
                }
                file = dialog.FileName;
            }

            string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                if (string.IsNullOrWhiteSpace(file))
                    throw new IOException("저장 경로가 비어 있습니다. 다시 선택하세요.");
                if (!string.Equals(Path.GetExtension(file), ".dxf", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("DXF 확장자(.dxf)로 저장하세요.");
                if (!string.IsNullOrEmpty(_modelPath) &&
                    string.Equals(Path.GetFullPath(file), Path.GetFullPath(_modelPath),
                        StringComparison.OrdinalIgnoreCase))
                    throw new IOException("원본 STEP 파일과 같은 경로에는 저장할 수 없습니다. 다른 이름을 선택하세요.");
                if (_modelRevision != modelRevisionBeforeDialog)
                    throw new IOException("모델이 변경되어 내보내기를 중단했습니다. 다시 검토한 뒤 저장하세요.");

                // 대화상자 동안 초안/영역/전기 입력이 바뀌었을 수 있으므로 다시 계산한다.
                ReviewSnapshot snapshot = BuildReviewSnapshot();
                if (!snapshot.CanExport)
                    throw new IOException("내보내기 조건이 바뀌었습니다 — " + snapshot.BlockReason);

                DxfView plan = BuildPlanExportView(snapshot, out string planError);
                if (plan == null)
                    throw new IOException(planError);

                // 대화상자에서 동의한 시점의 대상 상태를 기록한다. 쓰기 직후 이 상태가
                // 달라졌다면 사용자가 동의한 파일이 아니므로 덮어쓰지 않는다.
                bool destinationExisted = File.Exists(file);
                DateTime destinationWriteUtc = default;
                long destinationLength = 0;
                if (destinationExisted)
                {
                    var before = new FileInfo(file);
                    destinationWriteUtc = before.LastWriteTimeUtc;
                    destinationLength = before.Length;
                }

                string directory = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                // 최종 파일에 바로 쓰지 않는다. 기존 파일은 교체가 성공할 때까지 그대로 둔다.
                if (!DxfExporter.ExportPlan(temp, plan, specTable: snapshot.Table,
                        lengthNote: RouteLengthLabels.UnitNote))
                    throw new IOException("DXF 작성기가 저장을 완료하지 못했습니다.");
                if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
                    throw new IOException("DXF 임시 파일이 생성되지 않았습니다.");

                bool existsNow = File.Exists(file);
                if (destinationExisted)
                {
                    if (!existsNow)
                        throw new IOException("선택한 대상 파일이 삭제되었습니다. 다시 내보내세요.");
                    var now = new FileInfo(file);
                    if (now.LastWriteTimeUtc != destinationWriteUtc || now.Length != destinationLength)
                        throw new IOException("선택한 대상 파일이 그 사이 변경되었습니다. 다시 내보내세요.");
                    try
                    {
                        File.Replace(temp, file, null);
                    }
                    catch (Exception replaceError) when (replaceError is IOException ||
                        replaceError is UnauthorizedAccessException ||
                        replaceError is PlatformNotSupportedException)
                    {
                        throw new IOException(
                            "기존 파일을 안전하게 교체하지 못했습니다. 다른 저장 위치를 선택하세요.", replaceError);
                    }
                }
                else
                {
                    if (existsNow)
                        throw new IOException("선택한 경로에 다른 파일이 생성되었습니다. 다시 내보내세요.");
                    File.Move(temp, file);
                }
                StatusText.Text = "DXF 저장 완료 — " + file;
                return ExportDxfResult.Success;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                StatusText.Text = "DXF 저장 실패: " + ex.Message;
                ErrorReporter.ShowError("DXF 저장 실패 — " + file, ex);
                return ExportDxfResult.Failed;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch { /* 임시 파일 정리 실패는 저장 결과를 덮지 않는다. */ }
            }
        }

        private static string SanitizeFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            var builder = new System.Text.StringBuilder(name.Length);
            foreach (char c in name) builder.Append(invalid.Contains(c) ? '_' : c);
            string cleaned = builder.ToString().Trim();
            return string.IsNullOrEmpty(cleaned) ? "heat_pattern" : cleaned;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // 오프스크린 회귀 하네스는 Show 없이 창을 만들고 닫으므로 확인 대화상자를 띄우지 않는다.
            if (IsVisible && !ConfirmDiscardSession("앱 종료"))
            {
                e.Cancel = true;
                return;
            }
            base.OnClosing(e);
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            if (_visualTestInputLocked) return;
            var dlg = new OpenFileDialog { Filter = "STEP (*.stp;*.step)|*.stp;*.step|모든 파일 (*.*)|*.*" };
            if (dlg.ShowDialog(this) == true) LoadFile(dlg.FileName);
        }

    }
}
