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
        private sealed class CircuitLengthRow { public string Circuit { get; set; } public double HeatingLength { get; set; } public double TotalLength => HeatingLength; }
        private sealed record PendingLead(int CircuitIndex, bool IsStart, Vec3 Start);
        private List<PendingLead> _pendingLeads = new();
        private double[] _leadLengths = Array.Empty<double>();
        private double _insertionDepth;

        // 제외 체크박스의 프로그램적 해제가 ExcludeMode_Changed를 다시 타지 않게 한다.
        private bool _suppressExcludeToggle;
        // 전원 연결 대기 중에는 제외영역 입력도 잠근다.
        private bool _powerEditingLocked;

        private void CancelPowerSetup()
        {
            _pendingLeads.Clear();
            if (Viewport != null) Viewport.PowerLocationMode = false;
            Bridge.ClearPowerLeads();
            _leadLengths = Array.Empty<double>();
            SetPowerEditingLock(false);
            WorkingArea area = _activeArea;
            if (area?.Pattern != null && area.RoutePieceLengths.Length == area.Pattern.RoutePieces.Count)
                UpdateCircuitTable();
        }

        private void SetPowerEditingLock(bool locked)
        {
            _powerEditingLocked = locked;
            UpdateActionStates();
        }

        private string PowerPrompt() => _pendingLeads.Count == 0 ? "전원 연결 완료" :
            $"회로 {_pendingLeads[0].CircuitIndex + 1} · {(_pendingLeads[0].IsStart ? "시작" : "끝")} 단자 (3D 끝점 자동) — 금형 밖 목적지만 클릭하세요";

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
            Viewport.PowerLocationPicked += OnPowerLocationPicked;
            Viewport.ExitFacePicked += OnExitFacePicked;
            Viewport.ExitFacePickMoved += OnExitFacePickMoved;
            Viewport.PowerLocationMoved += OnPowerLocationMoved;
            Viewport.RoutePieceHovered += OnRoutePieceHovered;
            Viewport.VertexHovered += OnVertexHovered;
            Viewport.VertexDragStarted += OnVertexDragStarted;
            Viewport.VertexDragMoved += OnVertexDragMoved;
            Viewport.VertexDragCompleted += OnVertexDragCompleted;
            Viewport.VertexDragCancelled += OnVertexDragCancelled;
            Viewport.VertexEscape += VertexEscape;
            PreviewKeyDown += OnWindowPreviewKeyDown;
            Loaded += OnLoaded;
            ApplyPowerMigrationState();
            UpdateActionStates();
            InitializeSidebar();
            InitializeElectricalUi();
        }

        // 전원 연결은 경로 마이그레이션 전까지 네이티브 게이트로만 막혀 있었다.
        // 버튼도 같은 상수로 비활성화해 UI가 사용 가능한 기능처럼 보이지 않게 한다.
        private void ApplyPowerMigrationState()
        {
            if (PowerSetupButton != null) PowerSetupButton.IsEnabled = WorkingRouteMigrationComplete;
            if (PowerCancelButton != null) PowerCancelButton.IsEnabled = WorkingRouteMigrationComplete;
            if (PowerMigrationHint != null)
                PowerMigrationHint.Visibility = WorkingRouteMigrationComplete
                    ? Visibility.Collapsed
                    : Visibility.Visible;
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
            if (ExportButton != null) ExportButton.IsEnabled = !_isBusy;
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

            // 제외 도구는 확정 작업영역이 있을 때만, 다른 편집/전원 대기 중에는 비활성화한다.
            bool exclusionEligible = ExclusionEligible() && !vertexActive && !_powerEditingLocked && !_isBusy;
            if (ExcludeModeBox != null) ExcludeModeBox.IsEnabled = exclusionEligible;
            if (ClearExclusionsButton != null)
                ClearExclusionsButton.IsEnabled = _activeArea != null
                    && _activeArea.Exclusions.Count > 0
                    && !_powerEditingLocked
                    && !_isBusy;

            if (ActiveAreaStateText != null)
                ActiveAreaStateText.Text = _activeArea == null
                    ? (_areas.Count == 0 ? "영역 없음" : "선택 없음")
                    : AreaResultState(_activeArea);
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

        private void StandardViewMenu_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || button.ContextMenu == null) return;
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            button.ContextMenu.IsOpen = true;
        }

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
            CancelPowerSetup();
            var sw = Stopwatch.StartNew();
            if (!Bridge.LoadStep(path))
            {
                ClearAllAreas(clearNative: true);
                _multiFaceRegionFailure = null;
                _modelFileName = "";
                _modelPath = "";
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
            ApplyWorkingSide(new Vec3(0,0,1));
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
              .Append(side?.Revision.Value ?? -1).Append('|')
              .Append(area.ExitFaceId).Append('|').Append(area.ExitFaceRevision).Append('|');

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
            sb.Append("|CF:");
            foreach (int faceId in area.ExitChainFaceIds) sb.Append(faceId).Append(',');
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
            sb.Append("|AC:");
            AppendCandidateIdentity(sb, area.SelectedAnchorCandidate?.CandidateId);
            sb.Append(',');
            if (area.SelectedAnchorCandidate != null)
            {
                sb.Append(area.SelectedAnchorCandidate.AngleDeg.ToString("R", inv)).Append(',')
                  .Append(area.SelectedAnchorCandidate.StartLeftToRight?.ToString() ?? "-").Append(';');
            }
            AppendExitBinding(sb, area.SelectedAnchorCandidate?.StartBinding, inv);
            sb.Append(';');
            AppendExitBinding(sb, area.SelectedAnchorCandidate?.EndBinding, inv);
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
                : area.HasExitFace && !area.HasExitAnchors && !string.IsNullOrEmpty(area.LastExitFailure)
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
            if (candidate.HasExitFace) RecomputeAnchorCandidates(candidate);
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

            CadExitBoundary2D cad = candidate.RegionCadBoundary
                ?? candidate.RegionExitBoundary as CadExitBoundary2D;
            if (cad == null)
            {
                error = "적용영역 원본 CAD 경계가 없어 제외영역 외곽선을 다시 만들 수 없습니다";
                return false;
            }
            candidate.RegionCadBoundary = cad;

            bool exitBoundaryWasRegion = candidate.ExitFaceId < 0
                || ReferenceEquals(candidate.ExitBoundary, candidate.RegionCadBoundary);
            IExitBoundary2D boundary;
            if (candidate.Exclusions.Count == 0)
            {
                boundary = cad;
            }
            else
            {
                if (candidate.Region.Count != 1)
                {
                    error = "제외영역은 단일 적용영역에서만 지원됩니다";
                    return false;
                }
                if (!EffectiveRegionBoundary2D.TryCreate(cad, candidate.Region[0],
                        candidate.Exclusions, out EffectiveRegionBoundary2D effective,
                        out string failure))
                {
                    error = failure;
                    return false;
                }
                boundary = effective;
            }

            bool hadStart = candidate.SelectedStartExit.HasValue;
            bool hadEnd = candidate.SelectedEndExit.HasValue;
            candidate.RegionExitBoundary = boundary;
            if (exitBoundaryWasRegion) candidate.ExitBoundary = boundary;
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

        /// <summary>조각 순서대로 점을 이어 하나의 폴리라인으로 만든다 — 조각 경계의 중복 시작점은 건너뛴다.</summary>
        private static List<Vec2> FlattenPieces(IReadOnlyList<SerpentineRoutePiece> pieces)
        {
            var points = new List<Vec2>();
            foreach (var piece in pieces)
            {
                int start = points.Count > 0 && piece.Points.Count > 0
                    && points[^1].DistanceTo(piece.Points[0]) <= 1e-9 ? 1 : 0;
                for (int i = start; i < piece.Points.Count; i++) points.Add(piece.Points[i]);
            }
            return points;
        }

        private void OnFacePicked(int faceIndex, bool toggle)
        {
            if (Viewport.PowerLocationMode) return;
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
            area.ExitChainFaceIds = new List<int>();
            area.ExitFaceId = -1;
            area.ExitFaceRevision = 0;
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
            CancelPowerSetup();
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
            _insertionDepth = insertionDepth;
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
            var paths = new List<IReadOnlyList<Vec2>>();
            SerpentineResult editablePattern = null;
            // 기존 인출면/후보 기반 영역은 회귀 검사와 기존 문서 호환을 위해 종전 경로를 유지한다.
            // 새 영역은 RegionExitBoundary + 두 선택점(HasSelectedExits)으로만 생성한다.
            bool legacyAnchored = !candidate.HasSelectedExits && candidate.HasExitAnchors;
            if (!candidate.HasSelectedExits && !legacyAnchored)
            {
                StatusText.Text = candidate.LastExitFailure
                    ?? "시작/종료 인출점이 지정되지 않았습니다 — 적용영역 외곽 Edge에서 두 점을 먼저 선택하세요";
                return;
            }
            // 배치 방향 콤보에서 자동(장축)을 제거했으므로 저장된 Auto는 가로로 해석한다.
            var parameters = new PatternParams { Margin = margin, Spacing = spacing, Direction = EffectiveDirection(candidate.Direction) };
            try
            {
                if (candidate.Exclusions.Count > 0)
                {
                    if (legacyAnchored)
                    {
                        var plan = TryPrepareExcludedPlan(candidate, out string planFailure);
                        if (plan == null)
                        {
                            SyncExitFaceUi(area);
                            StatusText.Text = planFailure;
                            return;
                        }
                        var anchored = Serpentine.GenerateAnchoredComponents(candidate.Region[0], candidate.Exclusions,
                            parameters, candidate.ExitChain, plan, candidate.Direction, boundary: candidate.ExitBoundary);
                        if (!anchored.Succeeded)
                        {
                            SyncExitFaceUi(area);
                            StatusText.Text = ExplainAnchoredFailure(anchored.Failure, anchored.BoundaryFailure);
                            return;
                        }
                        editablePattern = anchored.Pattern;
                        if (editablePattern.Path.Count >= 2) paths.Add(editablePattern.Path);
                    }
                    else
                    {
                        // 제외영역이 있어도 선택한 두 인출점을 고정하고, 모든 레인을 방문하는
                        // 하나의 완전 경로만 사용한다. 후보/계획 재계산으로 점을 바꾸지 않는다.
                        var anchored = Serpentine.GenerateComponentsWithSelectedExits(candidate.Region[0],
                            candidate.Exclusions, parameters, candidate.Direction,
                            candidate.SelectedStartExit.Value, candidate.SelectedEndExit.Value,
                            boundary: candidate.RegionExitBoundary);
                        if (!anchored.Succeeded)
                        {
                            SyncExitFaceUi(area);
                            StatusText.Text = ExplainAnchoredFailure(anchored.Failure, anchored.BoundaryFailure);
                            return;
                        }
                        editablePattern = anchored.Pattern;
                        if (editablePattern.Path.Count >= 2) paths.Add(editablePattern.Path);
                    }
                }
                else
                {
                    if (legacyAnchored)
                    {
                        // 기존 인출면이 지정된 영역은 종전 고정 경로를 그대로 사용한다.
                        var anchored = Serpentine.GenerateAnchored(candidate.Region[0], null, parameters,
                            candidate.ExitChain, candidate.StartAnchor.Value, candidate.EndAnchor.Value, candidate.Direction,
                            boundary: candidate.ExitBoundary, candidate: candidate.SelectedAnchorCandidate);
                        if (!anchored.Succeeded)
                        {
                            SyncExitFaceUi(area);
                            StatusText.Text = ExplainAnchoredFailure(anchored.Failure, anchored.BoundaryFailure);
                            return;
                        }
                        editablePattern = anchored.Pattern;
                        if (editablePattern.Path.Count >= 2) paths.Add(editablePattern.Path);
                    }
                    else
                    {
                        // 선택점은 첫/마지막 발열선 끝만 교체한다. 실패해도 무앵커 경로로 대체하지 않는다.
                        var anchored = Serpentine.GenerateWithSelectedExits(candidate.Region[0], null,
                            parameters, candidate.Direction,
                            candidate.SelectedStartExit.Value, candidate.SelectedEndExit.Value,
                            boundary: candidate.RegionExitBoundary);
                        if (!anchored.Succeeded)
                        {
                            SyncExitFaceUi(area);
                            StatusText.Text = ExplainAnchoredFailure(anchored.Failure, anchored.BoundaryFailure);
                            return;
                        }
                        editablePattern = anchored.Pattern;
                        if (editablePattern.Path.Count >= 2) paths.Add(editablePattern.Path);
                    }
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "경로 계산 실패: " + ex.Message;
                ErrorReporter.ShowError("경로 계산 실패", ex);
                return;
            }
            if (paths.Count == 0 && (editablePattern == null || editablePattern.RoutePieces.Count == 0))
            {
                StatusText.Text = "생성 가능한 경로가 없습니다. 마진/간격 또는 선택 영역을 확인하세요";
                return;
            }
            bool routePiecesMode = editablePattern != null && editablePattern.RoutePieces.Count > 0;

            // 새 생성은 이전 Pattern의 파생 상태를 상속하지 않는다. 이 모든 준비는
            // 후보 안에서만 수행하며, 실패 시 확정 영역은 수정조차 하지 않는다.
            candidate.Pattern = routePiecesMode ? editablePattern : null;
            candidate.Circuits = new List<CircuitRoute>();
            candidate.CircuitPieceIndices = new List<List<int>>();
            candidate.Paths = routePiecesMode ? new List<List<Vec2>>() : paths.Select(p => p.ToList()).ToList();
            candidate.ExclusionPieces = null;
            if (routePiecesMode)
            {
                int count = editablePattern.RoutePieces.Count;
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
            candidate.PatternDisplayed = routePiecesMode ? candidate.Pattern.RoutePieces.Count > 0 : candidate.Paths.Count > 0;

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
            if (CircuitLengthGrid == null) return;
            // 전기 사양표·내보내기와 같은 검토 스냅샷을 사용한다. 회로 ID는 영역명-회로번호로 구분한다.
            ReviewSnapshot snapshot = BuildReviewSnapshot();
            var rows = new List<CircuitLengthRow>();
            foreach (ReviewAreaEntry entry in snapshot.Entries)
            {
                bool reference = entry.State is ReviewAreaState.Stale or ReviewAreaState.Unverified;
                if (entry.State != ReviewAreaState.Ready && !reference) continue;
                if (reference && !HasUsableLengths(entry.Area, out _)) continue;
                string prefix = string.IsNullOrEmpty(entry.Area.Name) ? "A-" : entry.Area.Name + "-";
                rows.AddRange(GetCircuitLengths(entry.Area, prefix)
                    .Select(x => new CircuitLengthRow
                    {
                        Circuit = (reference ? "[참고] " : "") + x.CircuitId,
                        HeatingLength = x.HeatingLengthMm
                    }));
            }
            CircuitLengthGrid.ItemsSource = rows.Count == 0 ? null : rows;
            if (ExpandedCircuitGrid != null)
                ExpandedCircuitGrid.ItemsSource = rows.Count == 0 ? null : rows;
            RefreshElectricalTable();
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
        private void PowerSetup_Click(object sender, RoutedEventArgs e)
        {
            if (_workingSide.Confirmed == null) { WorkingGenerationReady(); return; }
            if (!WorkingRouteMigrationComplete) { StatusText.Text = "생성 보류: 전원 경로는 아직 작업 방향 첫 충돌 경로로 마이그레이션되지 않았습니다"; return; }
            if (!WorkingGenerationReady()) return;
            WorkingArea area = _activeArea;
            if (area?.Pattern == null || area.Frame == null) { StatusText.Text = "먼저 발열 패턴을 생성하세요"; return; }
            CancelPowerSetup();
            if (area.ProjectedPieces.Count != area.Pattern.RoutePieces.Count) { StatusText.Text = "3D 경로 끝점이 없습니다. 패턴을 다시 생성하세요"; return; }
            var groups = CircuitPieceGroups();
            _leadLengths = new double[groups.Count];
            for (int i = 0; i < groups.Count; i++)
            {
                int first = Array.IndexOf(area.NativePieceIds, groups[i][0]);
                int last = Array.IndexOf(area.NativePieceIds, groups[i][^1]);
                if (first < 0 || last < 0) continue;
                double[] a = area.ProjectedPieces[first].Start, b = area.ProjectedPieces[last].End;
                _pendingLeads.Add(new PendingLead(i, true, new Vec3(a[0], a[1], a[2])));
                _pendingLeads.Add(new PendingLead(i, false, new Vec3(b[0], b[1], b[2])));
            }
            ExitExclusionSketch();
            Viewport.RectangleSelectionMode = false;
            Bridge.ClearHover();
            HoverText.Text = "";
            Viewport.PowerLocationMode = _pendingLeads.Count > 0;
            Viewport.CircuitHoverMode = false;
            SetPowerEditingLock(Viewport.PowerLocationMode);
            UpdateCircuitTable();
            StatusText.Text = PowerPrompt();
        }
        private void PowerCancel_Click(object sender, RoutedEventArgs e)
        {
            CancelPowerSetup();
            StatusText.Text = "전원 설정 취소 — 접속 경로와 접속 길이를 초기화했습니다";
        }
        private void OnPowerLocationMoved(int x, int y) => UpdatePowerLocation(x, y, previewOnly: true);

        private void OnPowerLocationPicked(int x, int y)
        {
            Viewport.CancelPowerLeadPreview();
            UpdatePowerLocation(x, y, previewOnly: false);
        }

        private void UpdatePowerLocation(int x, int y, bool previewOnly)
        {
            if (!WorkingRouteMigrationComplete) { Bridge.ClearPowerLeadPreview(); return; }
            if (_pendingLeads.Count == 0 || !Viewport.PowerLocationMode) { Bridge.ClearPowerLeadPreview(); return; }
            int[] areaFaces = _activeArea == null ? Array.Empty<int>() : SortedFaceIds(_activeArea);
            double[] hit = Bridge.PickSelectedBoundaryPoint(areaFaces, x, y);
            if (hit == null || hit.Length < 4 || hit.Any(v => !double.IsFinite(v))) { Bridge.ClearPowerLeadPreview(); if (!previewOnly) StatusText.Text = "선택 가열면의 외곽 경계를 클릭하세요 · " + PowerPrompt(); return; }
            ProjectionFrame frame = _activeArea?.Frame;
            if (frame == null) { Bridge.ClearPowerLeadPreview(); return; }
            var pending = _pendingLeads[0];
            Vec3 end = new(hit[0], hit[1], hit[2]);
            Vec3 source = frame.Unproject(frame.Project(pending.Start));
            Vec3 datumEnd = end + frame.Normal * _insertionDepth;
            Vec3 direction = frame.Normal * -1.0;
            var result = Bridge.ProjectPowerLead(pending.CircuitIndex * 2 + (pending.IsStart ? 0 : 1),
                new[] { source.X, source.Y, source.Z, datumEnd.X, datumEnd.Y, datumEnd.Z },
                new[] { pending.Start.X, pending.Start.Y, pending.Start.Z }, areaFaces,
                direction.X, direction.Y, direction.Z, _insertionDepth, previewOnly);
            if (result == null || result.Length < 4 || result[0] != 1)
            {
                if (previewOnly) return; // Invalid hover: no misleading destination/lead.
                string reason = result == null ? "native 오류" : result[0] switch {
                    -2 => "금형 밖 목적지를 선택하세요", -3 => "표면 투영이 끊기거나 중복됩니다 (구멍/재진입/다층)",
                    -4 => "실제 3D 끝점에서 표면 경계까지 연속 연결을 검증하지 못했습니다",
                    -5 => "외부 접속 구간이 금형을 통과하거나 재진입합니다", _ => "OCCT 접속 경로 생성 실패" };
                StatusText.Text = reason + " · " + PowerPrompt(); return;
            }
            if (previewOnly) return; // Hover must never consume endpoints or update lengths.
            _pendingLeads.RemoveAt(0);
            _leadLengths[pending.CircuitIndex] += result[1];
            UpdateCircuitTable();
            if (_pendingLeads.Count == 0) { Viewport.PowerLocationMode = false; SetPowerEditingLock(false); }
            StatusText.Text = _pendingLeads.Count == 0 ? "전원 연결 경로 완료 — 실제 3D 접속 길이를 회로별 합계에 반영했습니다" : PowerPrompt();
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
            CancelPowerSetup();
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
                if (!DxfExporter.ExportPlan(temp, plan, specTable: snapshot.Table))
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

        private void StandardView_Click(object sender, RoutedEventArgs e)
        {
            if (Viewport != null && Viewport.VertexEditMode)
            {
                StatusText.Text = "발열경로 편집 중에는 뷰를 바꿀 수 없습니다 — 편집을 종료한 뒤 다시 시도하세요";
                return;
            }
            Viewport.CancelPowerLeadPreview();
            string view = (sender as FrameworkElement)?.Tag as string;
            int axis, sign, upAxis;
            switch (view)
            {
                case "Front": axis = 2; sign = -1; upAxis = 1; break;
                case "Back": axis = 2; sign = 1; upAxis = 1; break;
                case "Top": axis = 1; sign = -1; upAxis = 2; break;
                case "Bottom": axis = 1; sign = 1; upAxis = 2; break;
                case "Left": axis = 0; sign = -1; upAxis = 1; break;
                case "Right": axis = 0; sign = 1; upAxis = 1; break;
                default: return;
            }

            double[] direction = Bridge.GetAutoAxis(axis);
            double[] up = Bridge.GetAutoAxis(upAxis);
            if (direction == null || direction.Length < 3 || up == null || up.Length < 3)
            {
                StatusText.Text = "표준 뷰 축 계산 실패";
                return;
            }

            Bridge.SetViewFrame(sign * direction[0], sign * direction[1], sign * direction[2],
                up[0], up[1], up[2]);
            StatusText.Text = "표준 뷰 — " + view;
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            if (_visualTestInputLocked) return;
            var dlg = new OpenFileDialog { Filter = "STEP (*.stp;*.step)|*.stp;*.step|모든 파일 (*.*)|*.*" };
            if (dlg.ShowDialog(this) == true) LoadFile(dlg.FileName);
        }

    }
}
