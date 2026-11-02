using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Rectangle = System.Windows.Shapes.Rectangle;
using Polyline = System.Windows.Shapes.Polyline;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Export;
using HeatingMold.OccBridge;
using Microsoft.Win32;

namespace HeatingMold.App
{
    public partial class MainWindow : Window
    {
        private SerpentineResult _pattern;
        private readonly HashSet<int> _selectedFaceIds = new();
        // 활성 작업영역의 Face 집합. 작업영역을 전환하면 해당 영역의 집합을 가리킨다.
        private HashSet<int> _confirmedFaceIds = new();
        private List<List<Vec2>> _selectedRegion = new();
        private ProjectionFrame _multiFaceFrame;
        private string _multiFaceRegionFailure;
        private List<(double MinX, double MinY, double MaxX, double MaxY)> _exclusions = new();
        private bool _multiFacePatternDisplayed;
        private List<bool> _turnEnabled = new();
        private readonly Stack<bool[]> _connectorUndo = new(), _connectorRedo = new();
        private double[] _routePieceLengths = Array.Empty<double>();
        private List<RoutePieceResult> _projectedPieces = new();

        // 확정된 작업영역 목록. 항목을 추가/삭제해도 기존 영역과 패턴은 유지된다.
        private readonly List<WorkingArea> _areas = new();
        private WorkingArea _activeArea;
        // 제외영역 채움 메시(AreaHighlight) 키 — 영역 인스턴스마다 1씩 증가.
        private int _nextAreaId;
        private int _nextNativePieceId;
        private int _nextStageId = int.MinValue + 1_000_000;
        private bool _suppressAreaSelection;
        private static readonly Color[] AreaColors =
        {
            Color.FromRgb(0xE6, 0x28, 0x28), // A 빨강
            Color.FromRgb(0x25, 0x63, 0xEB), // B 파랑
            Color.FromRgb(0x16, 0xA3, 0x4A), // C 초록
            Color.FromRgb(0xEA, 0x58, 0x0C), // D 주황
            Color.FromRgb(0x7C, 0x3A, 0xED), // E 보라
            Color.FromRgb(0x0D, 0x94, 0x88), // F 청록
            Color.FromRgb(0xDB, 0x27, 0x77), // G 자홍
            Color.FromRgb(0x92, 0x40, 0x0E), // H 갈색
        };
        private int _hoverConnector = -1;
        private sealed class CircuitLengthRow { public string Circuit { get; set; } public double HeatingLength { get; set; } public double LeadLength { get; set; } public double TotalLength => HeatingLength + LeadLength; }
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
            if (_pattern != null && _routePieceLengths.Length == _pattern.RoutePieces.Count)
                UpdateCircuitTable(SerpentineTopology.Compute(_pattern.RoutePieces, _turnEnabled));
        }

        private void SetPowerEditingLock(bool locked)
        {
            _powerEditingLocked = locked;
            if (ConnectorEditModeBox != null) ConnectorEditModeBox.IsEnabled = !locked;
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
            Viewport.ConnectorPicked += OnConnectorPicked;
            Viewport.PowerLocationPicked += OnPowerLocationPicked;
            Viewport.ExitFacePicked += OnExitFacePicked;
            Viewport.ExitFacePickMoved += OnExitFacePickMoved;
            Viewport.PowerLocationMoved += OnPowerLocationMoved;
            Viewport.ConnectorHovered += OnConnectorHovered;
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

        // 사이드바 요약/버튼은 현재 선택·확정 상태와 항상 일치해야 한다.
        private void UpdateActionStates()
        {
            int selected = _selectedFaceIds.Count;
            bool vertexActive = Viewport != null && Viewport.VertexEditMode;
            // 편집 중에는 영역 구성/생성/삭제로 경로와 핸들이 어긋나지 않게 막는다.
            if (ConfirmAreaButton != null) ConfirmAreaButton.IsEnabled = selected > 0 && !vertexActive;
            if (ClearConfirmedAreaButton != null) ClearConfirmedAreaButton.IsEnabled = _activeArea != null && !vertexActive;
            if (GenerateButton != null) GenerateButton.IsEnabled = _activeArea != null && !vertexActive;
            if (SelectionSummaryText != null)
                SelectionSummaryText.Text = selected > 0
                    ? string.Format("선택 {0}개 · 새 작업영역 확정 대기", selected)
                    : _activeArea != null
                        ? string.Format("{0} 영역 활성 · Face {1}개", _activeArea.Name, _activeArea.FaceIdSet.Count)
                        : "Face를 선택하세요";
            if (GenerateHintText != null)
                GenerateHintText.Text = _activeArea == null
                    ? "작업 영역을 확정해야 생성할 수 있습니다."
                    : string.Format("{0} 영역에 발열 경로를 생성합니다.", _activeArea.Name);

            bool vertexEligible = _activeArea?.Pattern != null
                && _activeArea.Pattern.RoutePieces.Count > 0
                && _activeArea.Paths.Count == 0;
            if (VertexEditModeBox != null) VertexEditModeBox.IsEnabled = vertexEligible || vertexActive;
            if (VertexUndoButton != null) VertexUndoButton.IsEnabled = vertexActive && VertexUndoCount(_activeArea) > 0;
            if (VertexRedoButton != null) VertexRedoButton.IsEnabled = vertexActive && VertexRedoCount(_activeArea) > 0;

            // 제외 도구는 확정 작업영역이 있을 때만, 다른 편집/전원 대기 중에는 비활성화한다.
            bool exclusionEligible = ExclusionEligible() && !vertexActive && !_powerEditingLocked;
            if (ExcludeModeBox != null) ExcludeModeBox.IsEnabled = exclusionEligible;
            if (ClearExclusionsButton != null)
                ClearExclusionsButton.IsEnabled = _activeArea != null
                    && _activeArea.Exclusions.Count > 0
                    && !_powerEditingLocked;
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
            if (mold != null) LoadFile(mold);
            else StatusText.Text = "뷰어 준비 — '열기'로 STEP 파일 선택";
        }

        private void LoadFile(string path)
        {
            if (!ConfirmDiscardAllRouteEdits("모델 열기")) return;
            ExitVertexEdit();
            ResetWorkingSide();
            CancelPowerSetup();
            var sw = Stopwatch.StartNew();
            if (!Bridge.LoadStep(path))
            {
                ClearAllAreas(clearNative: true);
                _multiFaceRegionFailure = null;
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

        private void TryGetParams(out double margin, out double spacing)
        {
            margin = 5; spacing = 50;
            if (!double.TryParse(MarginBox.Text, out margin) || margin < 0) margin = 5;
            if (!double.TryParse(SpacingBox.Text, out spacing) || spacing <= 0) spacing = 50;
        }

        private void Generate_Click(object sender, RoutedEventArgs e)
        {
            if (_activeArea == null) { StatusText.Text = "Face 선택 후 Enter로 작업영역을 확정하세요"; return; }
            if (!WorkingGenerationReady()) return;
            GenerateMultiFace();
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

        private bool ExclusionEligible() => _activeArea != null && _multiFaceFrame != null;

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

        private void OnFacesRectanglePicked(int[] faceIds, bool toggle)
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
            if (_multiFaceFrame == null) return false;
            Vec3 o = _multiFaceFrame.Origin, n = _multiFaceFrame.Normal;
            double[] hit = Bridge.ScreenPointOnPlane(x, y, o.X, o.Y, o.Z, n.X, n.Y, n.Z);
            if (hit == null || hit.Length < 3 || hit.Any(v => !double.IsFinite(v))) return false;
            world = new Vec3(hit[0], hit[1], hit[2]);
            point = _multiFaceFrame.Project(world);
            return true;
        }

        private double[] ExclusionRectangleWire(Vec2 a, Vec2 b)
        {
            double minX = Math.Min(a.X, b.X), maxX = Math.Max(a.X, b.X);
            double minY = Math.Min(a.Y, b.Y), maxY = Math.Max(a.Y, b.Y);
            Vec2[] corners = { new(minX, minY), new(maxX, minY), new(maxX, maxY), new(minX, maxY), new(minX, minY) };
            var xyz = new double[corners.Length * 3];
            for (int i = 0; i < corners.Length; i++)
            {
                Vec3 p = _multiFaceFrame.Unproject(corners[i]);
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
            double minX = Math.Min(a.X, b.X), maxX = Math.Max(a.X, b.X);
            double minY = Math.Min(a.Y, b.Y), maxY = Math.Max(a.Y, b.Y);
            if (maxX - minX < 0.5 || maxY - minY < 0.5)
            {
                StatusText.Text = "제외 사각형이 너무 작습니다";
                return;
            }
            if (_activeArea == null)
            {
                StatusText.Text = "제외영역을 추가하려면 먼저 작업영역을 확정하세요";
                return;
            }
            if (!ConfirmDiscardRouteEdits(_activeArea, "제외영역 추가")) return;
            ExitVertexEdit();
            var area = _activeArea;
            var rect = (minX, minY, maxX, maxY);
            // 롤백 스냅샷 — 어느 단계에서 실패해도 제외 목록과 경로 상태를 이전 그대로 되돌린다.
            var previousExclusions = area.Exclusions.ToList();
            var previousPattern = area.Pattern;
            var previousPaths = area.Paths;
            var previousExclusionPieces = area.ExclusionPieces;
            var previousPieceIds = area.NativePieceIds;
            var previousLengths = area.RoutePieceLengths;
            var previousProjected = area.ProjectedPieces;
            var previousTurns = area.TurnEnabled;
            var previousDisplayed = area.PatternDisplayed;
            var previousLeadLengths = area.LeadLengths;
            var previousFailure = area.LastExitFailure;
            var previousStartAnchor = area.StartAnchor;
            var previousEndAnchor = area.EndAnchor;
            var previousCandidates = area.AnchorCandidates;
            var previousExcludedCandidates = area.ExcludedCandidates;
            var previousExcludedPlan = area.ExcludedPlan;
            var previousExcludedFailure = area.ExcludedCandidateFailure;
            var previousExcludedKey = area.ExcludedPlanKey;

            area.Exclusions.Add(rect);
            // 1) 표시 메시 스테이징 — 실패하면 아무 상태도 바꾸지 않는다.
            if (!StageAreaHighlightMesh(area))
            {
                RestoreAreaExclusions(area, previousExclusions);
                StatusText.Text = "제외영역 표시를 생성하지 못했습니다 — 이전 제외영역/경로를 유지했습니다";
                return;
            }

            // 2) 스테이징이 성공한 뒤에만 이 영역의 기존 경로를 무효화한다.
            area.Pattern = null;
            area.Paths = new List<List<Vec2>>();
            area.ExclusionPieces = null;
            area.NativePieceIds = Array.Empty<int>();
            area.RoutePieceLengths = Array.Empty<double>();
            area.ProjectedPieces = new List<RoutePieceResult>();
            area.TurnEnabled = new List<bool>();
            area.PatternDisplayed = false;
            ActivateAreaStateFields(area);
            Bridge.ClearExclusionOverlays();
            if (!CommitAreaHighlight(area))
            {
                Bridge.DiscardAreaHighlight(area.AreaId);
                RestoreAreaExclusions(area, previousExclusions);
                RestoreAreaPatternState(area, previousPattern, previousPaths, previousExclusionPieces, previousPieceIds,
                    previousLengths, previousProjected, previousTurns, previousDisplayed,
                    previousStartAnchor, previousEndAnchor, previousCandidates,
                    previousExcludedCandidates, previousExcludedPlan, previousExcludedFailure,
                    previousExcludedKey, previousLeadLengths, previousFailure);
                UpdateActiveAreaHighlight();
                StatusText.Text = "제외영역 표시를 적용하지 못했습니다 — 이전 제외영역/경로를 유지했습니다";
                return;
            }

            // 3) 경로 재생성이 실패하면 표시까지 이전 상태로 되돌린다.
            if (!RebuildNativePatternGeometry(out _, out _))
            {
                RestoreAreaExclusions(area, previousExclusions);
                RestoreAreaPatternState(area, previousPattern, previousPaths, previousExclusionPieces, previousPieceIds,
                    previousLengths, previousProjected, previousTurns, previousDisplayed,
                    previousStartAnchor, previousEndAnchor, previousCandidates,
                    previousExcludedCandidates, previousExcludedPlan, previousExcludedFailure,
                    previousExcludedKey, previousLeadLengths, previousFailure);
                Bridge.DiscardAreaHighlight(area.AreaId);
                UpdateActiveAreaHighlight();
                return;
            }
            ClearRouteEditState(area);
            if (area.HasExitFace)
            {
                RecomputeAnchorCandidates(area);
                SyncExitFaceUi(area);
            }
            UpdateActiveAreaHighlight();
            StatusText.Text = area.HasExitFace && !area.HasExitAnchors && !string.IsNullOrEmpty(area.LastExitFailure)
                ? area.LastExitFailure
                : string.Format("모델 뷰포트 제외영역 추가 — {0}개", area.Exclusions.Count);
        }

        /// <summary>제외 목록을 스냅샷으로 되돌린다.</summary>
        private static void RestoreAreaExclusions(WorkingArea area,
            List<(double MinX, double MinY, double MaxX, double MaxY)> snapshot)
        {
            area.Exclusions.Clear();
            area.Exclusions.AddRange(snapshot);
        }

        /// <summary>경로 상태를 스냅샷으로 되돌리고 창 필드도 다시 연결한다.</summary>
        private void RestoreAreaPatternState(WorkingArea area, SerpentineResult pattern,
            List<List<Vec2>> paths, List<List<SerpentineRoutePiece>> exclusionPieces, int[] pieceIds,
            double[] lengths, List<RoutePieceResult> projected, List<bool> turns, bool displayed,
            Vec2? startAnchor, Vec2? endAnchor, List<AnchoredCandidate> anchorCandidates,
            List<ExcludedAnchorCandidate> excludedCandidates, ExcludedAnchorCandidate excludedPlan,
            SerpentineComponentsFailureReason? excludedFailure, string excludedKey,
            double[] leadLengths, string lastExitFailure)
        {
            area.Pattern = pattern;
            area.Paths = paths;
            area.ExclusionPieces = exclusionPieces;
            area.NativePieceIds = pieceIds;
            area.RoutePieceLengths = lengths;
            area.ProjectedPieces = projected;
            area.TurnEnabled = turns;
            area.PatternDisplayed = displayed;
            area.StartAnchor = startAnchor;
            area.EndAnchor = endAnchor;
            area.AnchorCandidates = anchorCandidates;
            area.ExcludedCandidates = excludedCandidates;
            area.ExcludedPlan = excludedPlan;
            area.ExcludedCandidateFailure = excludedFailure;
            area.ExcludedPlanKey = excludedKey;
            area.LeadLengths = leadLengths;
            area.LastExitFailure = lastExitFailure;
            if (ReferenceEquals(area, _activeArea)) ActivateAreaStateFields(area);
        }

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
                    : string.Format("적용면에 추가 — #{0} ({1}) · 이미 {2} 영역에 속함", faceIndex, type, assigned);
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

            int[] overlap = _areas.SelectMany(area => area.FaceIdSet)
                                  .Where(_selectedFaceIds.Contains)
                                  .Distinct()
                                  .OrderBy(id => id)
                                  .ToArray();
            if (overlap.Length > 0)
            {
                string owner = string.Join(", ", overlap.Select(id => AreaNameForFace(id) ?? "?").Distinct());
                ErrorReporter.ShowWarning("작업영역 확정 실패",
                    string.Format("선택한 Face 중 {0}개가 이미 다른 작업영역({1})에 속해 있습니다.\n각 Face는 하나의 작업영역에만 포함될 수 있습니다.",
                        overlap.Length, owner));
                StatusText.Text = "작업영역 확정 실패 — 다른 영역과 겹치는 Face가 있습니다";
                return;
            }

            if (!TryBuildSelectedRegion(out List<List<Vec2>> region, out ProjectionFrame frame))
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
                AreaId = ++_nextAreaId
            };
            area.FaceIdSet.UnionWith(_selectedFaceIds);
            area.Region.Add(region[0]);
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
        private bool TryBuildSelectedRegion(out List<List<Vec2>> region, out ProjectionFrame frame)
        {
            region = new List<List<Vec2>>();
            frame = null;
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
                _selectedFaceIds.OrderBy(id => id).ToArray(), 0.05);
            if (sourceLoops == null || sourceLoops.Length == 0 || sourceLoops.Any(loop => !loop.Closed))
            {
                _multiFaceRegionFailure = _selectedFaceIds.Any(id => Bridge.FaceRegionIsClosed(id))
                    ? "경계가 없는 닫힌 곡면은 Phase 1에서 미지원입니다 — 경계가 있는 열린 곡면 패치를 선택하세요"
                    : "원본 CAD 위상에서 닫힌 통합 경계를 구성하지 못했습니다. 선택 Face의 공유 Edge 연결을 확인하세요";
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

            List<Vec2> projected = firstPts.Select(fixedFrame.Project).ToList();
            if (projected.Count < 3 || Math.Abs(RegionOps.SignedArea(projected)) < 1e-8)
            {
                _multiFaceRegionFailure = "원본 통합 경계를 작업 평면에 유효하게 투영하지 못했습니다";
                return false;
            }
            if (RegionOps.SignedArea(projected) < 0) projected.Reverse();
            region.Add(projected);
            frame = fixedFrame;
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
            CancelPowerSetup();
            if (!WorkingGenerationReady()) return;
            var area = _activeArea;
            if (area == null || area.Frame == null || area.Region.Count == 0)
            {
                StatusText.Text = _multiFaceRegionFailure ?? "작업영역을 먼저 확정하세요";
                return;
            }
            TryGetParams(out double margin, out double spacing);
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
            var paths = new List<IReadOnlyList<Vec2>>();
            List<List<SerpentineRoutePiece>> exclusionPieces = null;
            SerpentineResult editablePattern = null;
            if (area.HasExitFace && !area.HasExitAnchors && area.Exclusions.Count == 0)
            {
                StatusText.Text = area.LastExitFailure
                    ?? "인출면의 시작/종료점이 지정되지 않았습니다 — 후보 목록에서 시작/종료점을 선택하세요";
                return;
            }
            var parameters = new PatternParams { Margin = margin, Spacing = spacing, Direction = area.Direction };
            try
            {
                if (area.HasExitFace && area.Exclusions.Count > 0)
                {
                    // 제외영역이 있어도 인출면 앵커를 고정한 단일 사행 경로를 생성한다.
                    var plan = TryPrepareExcludedPlan(area, out string planFailure);
                    if (plan == null)
                    {
                        area.LastExitFailure = planFailure;
                        SyncExitFaceUi(area);
                        StatusText.Text = planFailure;
                        return;
                    }
                    var anchored = Serpentine.GenerateAnchoredComponents(area.Region[0], area.Exclusions,
                        parameters, area.ExitChain, plan, area.Direction);
                    if (!anchored.Succeeded)
                    {
                        area.LastExitFailure = ExplainAnchoredFailure(anchored.Failure);
                        SyncExitFaceUi(area);
                        StatusText.Text = area.LastExitFailure;
                        return;
                    }
                    editablePattern = anchored.Pattern;
                    if (editablePattern.Path.Count >= 2) paths.Add(editablePattern.Path);
                }
                else if (area.HasExitAnchors)
                {
                    // 인출면이 지정되면 앵커 경로만 사용한다. 실패해도 무앵커 경로로 조용히 대체하지 않는다.
                    var anchored = Serpentine.GenerateAnchored(area.Region[0], null, parameters,
                        area.ExitChain, area.StartAnchor.Value, area.EndAnchor.Value, area.Direction);
                    if (!anchored.Succeeded)
                    {
                        area.LastExitFailure = ExplainAnchoredFailure(anchored.Failure);
                        SyncExitFaceUi(area);
                        StatusText.Text = area.LastExitFailure;
                        return;
                    }
                    editablePattern = anchored.Pattern;
                    if (editablePattern.Path.Count >= 2) paths.Add(editablePattern.Path);
                }
                else
                {
                    foreach (List<Vec2> ring in area.Region)
                    {
                        if (area.Exclusions.Count == 0)
                        {
                            var result = Serpentine.Generate(ring, null, parameters, constrainConnectors: true);
                            editablePattern = result;
                            if (result.Path.Count >= 2) paths.Add(result.Path);
                        }
                        else
                        {
                            // 제외영역 모드: 발열선은 발열영역(외곽−제외) 안에서만 생성되고, 턴커넥터는
                            // 이전 발열선 끝점에서 다음 발열선 시작점까지의 최단 직선(2점)이다.
                            // 제외영역으로 발열영역이 분리되면 두 독립 경로를 만들지 않고 실패로 알린다.
                            var result = Serpentine.GenerateComponents(ring, area.Exclusions, parameters);
                            if (result.Failure != null)
                            {
                                StatusText.Text = ExplainComponentsFailure(result.Failure);
                                return;
                            }
                            exclusionPieces = result.Components.Select(c => c.ToList()).ToList();
                            paths.AddRange(result.Components.Select(FlattenPieces));
                        }
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

            // 실패 시 이 영역의 기존 패턴 상태를 복원한다. 다른 영역은 건드리지 않는다.
            var oldPattern = area.Pattern;
            var oldTurns = area.TurnEnabled;
            var oldIds = area.NativePieceIds;
            var oldLengths = area.RoutePieceLengths;
            var oldProjected = area.ProjectedPieces;
            var oldPaths = area.Paths;
            var oldExclusionPieces = area.ExclusionPieces;
            var oldShown = area.PatternDisplayed;
            var oldLeadLengths = area.LeadLengths;
            var oldFailure = area.LastExitFailure;
            var oldStartAnchor = area.StartAnchor;
            var oldEndAnchor = area.EndAnchor;
            var oldCandidates = area.AnchorCandidates;
            var oldExcludedCandidates = area.ExcludedCandidates;
            var oldExcludedPlan = area.ExcludedPlan;
            var oldExcludedFailure = area.ExcludedCandidateFailure;
            var oldExcludedKey = area.ExcludedPlanKey;

            area.Pattern = routePiecesMode ? editablePattern : null;
            area.TurnEnabled = routePiecesMode
                ? editablePattern.RoutePieces.Where(p => p.Kind == SerpentineRoutePieceKind.TurnConnector).Select(_ => true).ToList()
                : new List<bool>();
            area.Paths = routePiecesMode ? new List<List<Vec2>>() : paths.Select(p => p.ToList()).ToList();
            area.ExclusionPieces = routePiecesMode ? null : exclusionPieces;
            if (routePiecesMode)
            {
                int count = editablePattern.RoutePieces.Count;
                if (area.NativePieceIds.Length != count) area.NativePieceIds = AllocateNativePieceIds(count);
                area.RoutePieceLengths = new double[count];
                area.ProjectedPieces = new List<RoutePieceResult>();
            }
            else
            {
                area.NativePieceIds = Array.Empty<int>();
                area.RoutePieceLengths = Array.Empty<double>();
                area.ProjectedPieces = new List<RoutePieceResult>();
            }
            area.PatternDisplayed = false;
            ActivateAreaStateFields(area);
            _connectorUndo.Clear();
            _connectorRedo.Clear();

            if (!RebuildNativePatternGeometry(out double total, out int projectedCount))
            {
                area.Pattern = oldPattern;
                area.TurnEnabled = oldTurns;
                area.NativePieceIds = oldIds;
                area.RoutePieceLengths = oldLengths;
                area.ProjectedPieces = oldProjected;
                area.Paths = oldPaths;
                area.ExclusionPieces = oldExclusionPieces;
                area.PatternDisplayed = oldShown;
                area.LeadLengths = oldLeadLengths;
                area.LastExitFailure = oldFailure;
                area.StartAnchor = oldStartAnchor;
                area.EndAnchor = oldEndAnchor;
                area.AnchorCandidates = oldCandidates;
                area.ExcludedCandidates = oldExcludedCandidates;
                area.ExcludedPlan = oldExcludedPlan;
                area.ExcludedCandidateFailure = oldExcludedFailure;
                area.ExcludedPlanKey = oldExcludedKey;
                SyncExitFaceUi(area);
                ActivateAreaStateFields(area);
                return;
            }

            // 재생성이 성공한 뒤에만 기존 편집 표시/이력을 지운다 — 실패 시 편집 내용은 보존된다.
            ClearRouteEditState(area);

            projectedCount = routePiecesMode ? area.Pattern.RoutePieces.Count : area.Paths.Count;
            area.PatternDisplayed = projectedCount > 0;
            _multiFacePatternDisplayed = area.PatternDisplayed;
            Viewport.CircuitHoverMode = routePiecesMode;
            Bridge.ClearHover();
            HoverText.Text = "";
            if (routePiecesMode)
            {
                UpdateExitLeadLengths(area);
                UpdateCircuitTable(SerpentineTopology.Compute(area.Pattern.RoutePieces, area.TurnEnabled));
            }
            else
            {
                area.LeadLengths = Array.Empty<double>();
                _leadLengths = area.LeadLengths;
            }
            int connectors = area.ExclusionPieces?.Sum(c => c.Count(p => p.Kind == SerpentineRoutePieceKind.TurnConnector)) ?? 0;
            int leadCount = routePiecesMode
                ? area.Pattern.RoutePieces.Count(p => p.Kind == SerpentineRoutePieceKind.ExitPiece) : 0;
            int turnCount = routePiecesMode
                ? area.Pattern.RoutePieces.Count(p => p.Kind == SerpentineRoutePieceKind.TurnConnector) : 0;
            if (projectedCount == 0)
            {
                StatusText.Text = "생성 가능한 경로가 없습니다. 마진/간격 또는 선택 영역을 확인하세요";
            }
            else if (routePiecesMode)
            {
                int circuits = SerpentineTopology.Compute(area.Pattern.RoutePieces, area.TurnEnabled).Count;
                string counts = area.Exclusions.Count > 0
                    ? string.Format(" · 연결선 {0}개 · 리드 {1}개", turnCount, leadCount)
                    : string.Empty;
                StatusText.Text = string.Format(
                    "{0} 영역 — {1}개 경로 · 길이 {2:F1}mm · 회로 {3}개 · 회로 분할 가능",
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

        private void ConnectorEditMode_Changed(object sender, RoutedEventArgs e)
        {
            bool enabled = ConnectorEditModeBox.IsChecked == true && _pattern != null && _pattern.RoutePieces.Count > 0;
            if (ConnectorEditModeBox.IsChecked == true && !enabled) { ConnectorEditModeBox.IsChecked = false; StatusText.Text = "먼저 턴 커넥터가 있는 패턴을 생성하세요"; return; }
            // 꼭지점 편집과 회로 분할은 상호 배타 — 편집 중에는 뷰포트가 꼭지점 입력을 우선 처리한다.
            if (enabled && Viewport.VertexEditMode) ExitVertexEdit();
            if (enabled && ExcludeModeBox.IsChecked == true) ExcludeModeBox.IsChecked = false;
            Viewport.ConnectorEditMode = enabled;
            if (enabled) { Bridge.ClearHover(); StatusText.Text = "회로 분할 — 연결 구간을 클릭해 회로를 분할하거나 다시 연결합니다"; }
        }

        private void OnConnectorHovered(int id) { _hoverConnector = id; HoverText.Text = id < 0 ? "" : "커넥터 #" + id; }
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
        private void OnConnectorPicked(int id)
        {
            var area = _activeArea;
            if (!Viewport.ConnectorEditMode || area?.Pattern == null) return;
            int local = Array.IndexOf(area.NativePieceIds, id);
            if (local < 0) return;
            var connectors = area.Pattern.RoutePieces.Select((p, i) => (p, i)).Where(x => x.p.Kind == SerpentineRoutePieceKind.TurnConnector).ToList();
            int ci = connectors.FindIndex(x => x.i == local);
            if (ci < 0) return;
            _connectorUndo.Push(area.TurnEnabled.ToArray()); _connectorRedo.Clear(); area.TurnEnabled[ci] = !area.TurnEnabled[ci];
            ApplyConnectorStates();
        }
        private void ApplyConnectorStates()
        {
            CancelPowerSetup();
            var area = _activeArea;
            if (area?.Pattern == null) return;
            ApplyAreaConnectorStates(area);
            Bridge.RedrawView();
            var circuits = SerpentineTopology.Compute(area.Pattern.RoutePieces, area.TurnEnabled);
            var turns = area.Pattern.RoutePieces.Select((p, i) => (p, i)).Where(x => x.p.Kind == SerpentineRoutePieceKind.TurnConnector).ToList();
            double length = area.Pattern.RoutePieces.Select((p, i) => (p, i))
                .Where(x => x.p.Kind != SerpentineRoutePieceKind.TurnConnector || area.TurnEnabled[turns.FindIndex(t => t.i == x.i)])
                .Sum(x => area.RoutePieceLengths.ElementAtOrDefault(x.i));
            area.LeadLengths = new double[circuits.Count];
            UpdateExitLeadLengths(area);
            _leadLengths = area.LeadLengths;
            UpdateCircuitTable(circuits);
            StatusText.Text = string.Format("{0} 영역 · 회로 {1}개 · 길이 합계 {2:F1}mm", area.Name, circuits.Count, length);
        }
        private void UpdateCircuitTable(IReadOnlyList<IReadOnlyList<Vec2>> circuits)
        {
            if (CircuitLengthGrid == null) return;
            var area = _activeArea;
            if (area?.Pattern == null) { CircuitLengthGrid.ItemsSource = null; return; }
            var rows = new List<CircuitLengthRow>();
            var groups = CircuitPieceGroups(area);
            var lengthById = new Dictionary<int, double>();
            var heatingIds = new HashSet<int>();
            for (int i = 0; i < area.NativePieceIds.Length; i++)
            {
                lengthById[area.NativePieceIds[i]] = area.RoutePieceLengths.ElementAtOrDefault(i);
                if (i < area.Pattern.RoutePieces.Count && area.Pattern.RoutePieces[i].Kind != SerpentineRoutePieceKind.ExitPiece)
                    heatingIds.Add(area.NativePieceIds[i]);
            }
            for (int i = 0; i < groups.Count; i++)
            {
                double heating = groups[i].Where(id => heatingIds.Contains(id))
                    .Sum(id => lengthById.TryGetValue(id, out double value) ? value : 0);
                rows.Add(new CircuitLengthRow { Circuit = (i + 1).ToString(), HeatingLength = heating, LeadLength = area.LeadLengths.ElementAtOrDefault(i) });
            }
            CircuitLengthGrid.ItemsSource = rows;
        }

        // Group stable route-piece IDs, not resampled planar points. Disabled
        // turns are separators and contribute neither geometry nor length.
        private List<List<int>> CircuitPieceGroups() => CircuitPieceGroups(_activeArea);

        private List<List<int>> CircuitPieceGroups(WorkingArea area)
        {
            var groups = new List<List<int>>();
            var current = new List<int>();
            int turn = 0;
            if (area?.Pattern == null) return groups;
            for (int id = 0; id < area.Pattern.RoutePieces.Count; id++)
            {
                bool enabled = area.Pattern.RoutePieces[id].Kind != SerpentineRoutePieceKind.TurnConnector || area.TurnEnabled[turn++];
                if (!enabled)
                {
                    if (current.Count > 0) { groups.Add(current); current = new List<int>(); }
                }
                else if (area.Pattern.RoutePieces[id].Points.Count > 0)
                    current.Add(id < area.NativePieceIds.Length ? area.NativePieceIds[id] : id);
            }
            if (current.Count > 0) groups.Add(current);
            return groups;
        }
        private void PowerSetup_Click(object sender, RoutedEventArgs e)
        {
            if (_workingSide.Confirmed == null) { WorkingGenerationReady(); return; }
            if (!WorkingRouteMigrationComplete) { StatusText.Text = "생성 보류: 전원 경로는 아직 작업 방향 첫 충돌 경로로 마이그레이션되지 않았습니다"; return; }
            if (!WorkingGenerationReady()) return;
            if (_pattern == null || _multiFaceFrame == null) { StatusText.Text = "먼저 회로 분할 가능한 패턴을 생성하세요"; return; }
            CancelPowerSetup();
            if (_projectedPieces.Count != _pattern.RoutePieces.Count) { StatusText.Text = "3D 경로 끝점이 없습니다. 패턴을 다시 생성하세요"; return; }
            var groups = CircuitPieceGroups();
            _leadLengths = new double[groups.Count];
            for (int i = 0; i < groups.Count; i++)
            {
                int first = Array.IndexOf(_activeArea.NativePieceIds, groups[i][0]);
                int last = Array.IndexOf(_activeArea.NativePieceIds, groups[i][^1]);
                if (first < 0 || last < 0) continue;
                double[] a = _projectedPieces[first].Start, b = _projectedPieces[last].End;
                _pendingLeads.Add(new PendingLead(i, true, new Vec3(a[0], a[1], a[2])));
                _pendingLeads.Add(new PendingLead(i, false, new Vec3(b[0], b[1], b[2])));
            }
            ConnectorEditModeBox.IsChecked = false;
            Viewport.ConnectorEditMode = false;
            ExitExclusionSketch();
            Viewport.RectangleSelectionMode = false;
            Bridge.ClearHover();
            HoverText.Text = "";
            Viewport.PowerLocationMode = _pendingLeads.Count > 0;
            Viewport.CircuitHoverMode = false;
            SetPowerEditingLock(Viewport.PowerLocationMode);
            UpdateCircuitTable(SerpentineTopology.Compute(_pattern.RoutePieces, _turnEnabled));
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
            var pending = _pendingLeads[0];
            Vec3 end = new(hit[0], hit[1], hit[2]);
            Vec3 source = _multiFaceFrame.Unproject(_multiFaceFrame.Project(pending.Start));
            Vec3 datumEnd = end + _multiFaceFrame.Normal * _insertionDepth;
            Vec3 direction = _multiFaceFrame.Normal * -1.0;
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
            var circuits = SerpentineTopology.Compute(_pattern.RoutePieces, _turnEnabled);
            UpdateCircuitTable(circuits);
            if (_pendingLeads.Count == 0) { Viewport.PowerLocationMode = false; SetPowerEditingLock(false); }
            StatusText.Text = _pendingLeads.Count == 0 ? "전원 연결 경로 완료 — 실제 3D 접속 길이를 회로별 합계에 반영했습니다" : PowerPrompt();
        }
        private void ConnectorUndo_Click(object sender, RoutedEventArgs e)
        {
            var area = _activeArea;
            if (area?.Pattern == null || _connectorUndo.Count == 0) return;
            _connectorRedo.Push(area.TurnEnabled.ToArray());
            area.TurnEnabled.Clear();
            area.TurnEnabled.AddRange(_connectorUndo.Pop());
            ApplyConnectorStates();
        }
        private void ConnectorRedo_Click(object sender, RoutedEventArgs e)
        {
            var area = _activeArea;
            if (area?.Pattern == null || _connectorRedo.Count == 0) return;
            _connectorUndo.Push(area.TurnEnabled.ToArray());
            area.TurnEnabled.Clear();
            area.TurnEnabled.AddRange(_connectorRedo.Pop());
            ApplyConnectorStates();
        }

        private void ClearExclusions_Click(object sender, RoutedEventArgs e)
        {
            var area = _activeArea;
            if (area == null) { StatusText.Text = "작업영역이 없습니다"; return; }
            if (!ConfirmDiscardRouteEdits(area, "제외영역 지우기")) return;
            ExitVertexEdit();
            // 롤백 스냅샷 — 실패 시 제외 목록/경로/표시를 이전 그대로 되돌린다.
            var previousExclusions = area.Exclusions.ToList();
            var previousPattern = area.Pattern;
            var previousPaths = area.Paths;
            var previousExclusionPieces = area.ExclusionPieces;
            var previousPieceIds = area.NativePieceIds;
            var previousLengths = area.RoutePieceLengths;
            var previousProjected = area.ProjectedPieces;
            var previousTurns = area.TurnEnabled;
            var previousDisplayed = area.PatternDisplayed;
            var previousLeadLengths = area.LeadLengths;
            var previousFailure = area.LastExitFailure;
            var previousStartAnchor = area.StartAnchor;
            var previousEndAnchor = area.EndAnchor;
            var previousCandidates = area.AnchorCandidates;
            var previousExcludedCandidates = area.ExcludedCandidates;
            var previousExcludedPlan = area.ExcludedPlan;
            var previousExcludedFailure = area.ExcludedCandidateFailure;
            var previousExcludedKey = area.ExcludedPlanKey;

            area.Exclusions.Clear();
            area.Pattern = null;
            area.Paths = new List<List<Vec2>>();
            area.ExclusionPieces = null;
            area.NativePieceIds = Array.Empty<int>();
            area.RoutePieceLengths = Array.Empty<double>();
            area.ProjectedPieces = new List<RoutePieceResult>();
            area.TurnEnabled = new List<bool>();
            area.PatternDisplayed = false;
            ActivateAreaStateFields(area);
            Bridge.ClearExclusionOverlays();
            if (!RebuildNativePatternGeometry(out _, out _))
            {
                RestoreAreaExclusions(area, previousExclusions);
                RestoreAreaPatternState(area, previousPattern, previousPaths, previousExclusionPieces, previousPieceIds,
                    previousLengths, previousProjected, previousTurns, previousDisplayed,
                    previousStartAnchor, previousEndAnchor, previousCandidates,
                    previousExcludedCandidates, previousExcludedPlan, previousExcludedFailure,
                    previousExcludedKey, previousLeadLengths, previousFailure);
                UpdateActiveAreaHighlight();
                return;
            }
            // 경로 재생성 성공 후에만 채움 메시를 걷어내고 Face 색칠로 되돌린다.
            UpdateActiveAreaHighlight();   // 제외 0: ClearAreaHighlight + ApplyAreaColors
            ClearRouteEditState(area);
            if (area.HasExitFace)
            {
                RecomputeAnchorCandidates(area);
                SyncExitFaceUi(area);
            }
            if (CircuitLengthGrid != null) CircuitLengthGrid.ItemsSource = null;
            UpdateActionStates();
            StatusText.Text = string.Format("{0} 영역의 제외 사각형을 모두 지웠습니다", area.Name);
        }

        private List<DxfView> BuildViews()
        {
            var views = new List<DxfView>();
            double[] x = Bridge.GetAutoAxis(0), y = Bridge.GetAutoAxis(1), z = Bridge.GetAutoAxis(2);
            if (x == null || y == null || z == null) return views;
            views.Add(BuildHlrView(z[0], z[1], z[2], x[0], x[1], x[2]));
            views.Add(BuildHlrView(-z[0], -z[1], -z[2], x[0], x[1], x[2]));
            views.Add(BuildHlrView(-y[0], -y[1], -y[2], x[0], x[1], x[2]));
            views.Add(BuildHlrView(x[0], x[1], x[2], y[0], y[1], y[2]));
            return views;
        }

        private DxfView BuildHlrView(double nx, double ny, double nz,
                                     double xx, double xy, double xz)
        {
            var view = new DxfView();
            Vec3 xAxis = new Vec3(xx, xy, xz).Normalized();

            foreach (var pl in Bridge.HlrEdges(nx, ny, nz, xAxis.X, xAxis.Y, xAxis.Z, true))
            {
                var pts = new List<Vec2>(pl.Points.Length / 2);
                for (int k = 0; k + 1 < pl.Points.Length; k += 2)
                    pts.Add(new Vec2(pl.Points[k], pl.Points[k + 1]));
                var poly = new DxfPolyline { Points = pts };
                if (pl.Visible) view.VisibleEdges.Add(poly);
                else view.HiddenEdges.Add(poly);
            }

            return view;
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            if (_pattern == null)
            {
                StatusText.Text = _multiFacePatternDisplayed
                    ? "다중 Face/제외영역 DXF 출력은 아직 지원하지 않습니다"
                    : "먼저 [패턴 생성]을 실행하세요";
                return;
            }
            string root = OcctEnv.RepoRoot ?? ".";
            string dir = Path.Combine(root, "output");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "heat_pattern.dxf");

            try
            {
                var views = BuildViews();
                DxfExporter.ExportViews(file, views);
                StatusText.Text = "DXF 저장 — " + file;
            }
            catch (Exception ex)
            {
                StatusText.Text = "DXF 저장 실패: " + ex.Message;
                ErrorReporter.ShowError("DXF 저장 실패 — " + file, ex);
            }
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
            var dlg = new OpenFileDialog { Filter = "STEP (*.stp;*.step)|*.stp;*.step|모든 파일 (*.*)|*.*" };
            if (dlg.ShowDialog(this) == true) LoadFile(dlg.FileName);
        }

    }
}
