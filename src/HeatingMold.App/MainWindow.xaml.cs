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
        // Legacy pattern state is retained behind the working-side route migration gate.
        private SerpentineResult _pattern;
        private int _patternFaceIndex = -1;
        private List<Vec3> _heatPath3D;   // pattern centerline on the surface (world 3D)
        private List<Vec3> _heatBand3D;   // pattern ribbon band on the surface (world 3D)
        private readonly HashSet<int> _selectedFaceIds = new();
        private List<List<Vec2>> _selectedRegion = new();
        private ProjectionFrame _multiFaceFrame;
        private string _multiFaceRegionFailure;
        private readonly List<(double MinX, double MinY, double MaxX, double MaxY)> _exclusions = new();
        private bool _multiFacePatternDisplayed;
        private readonly List<bool> _turnEnabled = new();
        private readonly Stack<bool[]> _connectorUndo = new(), _connectorRedo = new();
        private double[] _routePieceLengths = Array.Empty<double>();
        private readonly List<RoutePieceResult> _projectedPieces = new();
        private int _hoverConnector = -1;
        private sealed class CircuitLengthRow { public string Circuit { get; set; } public double HeatingLength { get; set; } public double LeadLength { get; set; } public double TotalLength => HeatingLength + LeadLength; }
        private sealed record PendingLead(int CircuitIndex, bool IsStart, Vec3 Start);
        private List<PendingLead> _pendingLeads = new();
        private double[] _leadLengths = Array.Empty<double>();
        private double _insertionDepth;

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
            if (ConnectorEditModeBox == null) return;
            ConnectorEditModeBox.IsEnabled = !locked;
            FaceDragSelectModeBox.IsEnabled = !locked;
            ExcludeModeBox.IsEnabled = !locked;
            FaceCombo.IsEnabled = !locked;
        }

        private string PowerPrompt() => _pendingLeads.Count == 0 ? "전원 연결 완료" :
            $"회로 {_pendingLeads[0].CircuitIndex + 1} · {(_pendingLeads[0].IsStart ? "시작" : "끝")} 단자 (3D 끝점 자동) — 금형 밖 목적지만 클릭하세요";

        private void ResetCircuitEditState(bool clearNative)
        {
            CancelPowerSetup();
            _projectedPieces.Clear();
            Viewport.ConnectorEditMode = false;
            if (ConnectorEditModeBox != null) ConnectorEditModeBox.IsChecked = false;
            _turnEnabled.Clear();
            _connectorUndo.Clear();
            _connectorRedo.Clear();
            _routePieceLengths = Array.Empty<double>();
            _hoverConnector = -1;
            _pendingLeads.Clear();
            _leadLengths = Array.Empty<double>();
            if (Viewport != null) Viewport.PowerLocationMode = false;
            if (CircuitLengthGrid != null) CircuitLengthGrid.ItemsSource = null;
            if (clearNative) Bridge.ClearRoutePieces();
        }

        private sealed class FaceItem
        {
            public int Index;
            public string Label;
            public override string ToString() => Label;
        }

        public MainWindow()
        {
            InitializeComponent();
            Viewport.FacePicked += OnFacePicked;
            Viewport.FaceHovered += OnFaceHovered;
            Viewport.FacesRectanglePicked += OnFacesRectanglePicked;
            Viewport.ExclusionSketchMoved += OnExclusionSketchMoved;
            Viewport.ExclusionSketchCompleted += OnExclusionSketchCompleted;
            Viewport.ExclusionSketchCancelled += () => Bridge.SetExclusionPreview(null);
            Viewport.ConnectorPicked += OnConnectorPicked;
            Viewport.PowerLocationPicked += OnPowerLocationPicked;
            Viewport.PowerLocationMoved += OnPowerLocationMoved;
            Viewport.ConnectorHovered += OnConnectorHovered;
            Viewport.RoutePieceHovered += OnRoutePieceHovered;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!Viewport.InitViewer())
            {
                StatusText.Text = "뷰어 초기화 실패";
                return;
            }

            string mold = OcctEnv.FindMoldStp();
            if (mold != null) LoadFile(mold);
            else StatusText.Text = "뷰어 준비 — '열기'로 STEP 파일 선택";
        }

        private void LoadFile(string path)
        {
            ResetWorkingSide();
            CancelPowerSetup();
            var sw = Stopwatch.StartNew();
            if (!Bridge.LoadStep(path))
            {
                ResetCircuitEditState(clearNative: true);
                _pattern = null;
                _patternFaceIndex = -1;
                _heatPath3D = null;
                _heatBand3D = null;
                _selectedFaceIds.Clear();
                SelectedFacesList.Items.Clear();
                FaceCombo.Items.Clear();
                _selectedRegion.Clear();
                _multiFaceFrame = null;
                _exclusions.Clear();
                _multiFacePatternDisplayed = false;
                FaceDragSelectModeBox.IsChecked = false;
                ExcludeModeBox.IsChecked = false;
                Viewport.RectangleSelectionMode = false;
                Viewport.ExclusionSketchMode = false;
                DrawRegionCanvas();
                StatusText.Text = "로드 실패: " + path;
                return;
            }
            sw.Stop();

            Bridge.SetShapeColor(0.55, 0.65, 0.85);   // 고정 표시색 (스틸 블루)
            ResetCircuitEditState(clearNative: true);
            _pattern = null;
            _patternFaceIndex = -1;
            _heatPath3D = null;
            _heatBand3D = null;
            _selectedFaceIds.Clear();
            SelectedFacesList.Items.Clear();
            _selectedRegion.Clear();
            _multiFaceFrame = null;
            _multiFaceRegionFailure = null;
            _exclusions.Clear();
            _multiFacePatternDisplayed = false;
            FaceDragSelectModeBox.IsChecked = false;
            ExcludeModeBox.IsChecked = false;
            Viewport.RectangleSelectionMode = false;
            Viewport.ExclusionSketchMode = false;
            Bridge.ClearExclusionOverlays();
            Bridge.ClearOverlays();
            DrawRegionCanvas();

            var info = Bridge.GetShapeInfo();
            PopulateFaceCombo(info.Faces);
            StatusText.Text = string.Format("{0} — 솔리드 {1} · 셸 {2} · 면 {3} · 평면 {4} · 로드 {5}ms",
                Path.GetFileName(path), info.Solids, info.Shells, info.Faces, info.Planes, sw.ElapsedMilliseconds);
            _changingWorkingAxis = true;
            WorkingAxisBox.SelectedIndex = 4;
            _changingWorkingAxis = false;
            ApplyWorkingSide(new Vec3(0,0,1));
        }

        private void PopulateFaceCombo(int faceCount)
        {
            FaceCombo.Items.Clear();
            var candidates = new List<Tuple<int, string, double>>();
            double[] gx = Bridge.GetAutoAxis(0), gz = Bridge.GetAutoAxis(2);
            var axisX = gx != null && gx.Length >= 3 ? new Vec3(gx[0], gx[1], gx[2]).Normalized() : new Vec3(1, 0, 0);
            var axisZ = gz != null && gz.Length >= 3 ? new Vec3(gz[0], gz[1], gz[2]).Normalized() : new Vec3(0, 0, 1);
            for (int i = 0; i < faceCount; i++)
            {
                string type = Bridge.GetFaceType(i);
                if (type == null) continue;
                var wires = Bridge.GetFaceUvBoundary(i);
                if (wires == null || wires.Length == 0) continue;

                // Normalized UV (BSpline etc.) is unrelated to real size -> measure the boundary in 3D (mm)
                var pts = new List<Vec3>(64);
                foreach (var w in wires)
                    for (int k = 0; k + 1 < w.Points.Length; k += 2)
                    {
                        var pt = Bridge.SampleUvTo3D(i, w.Points[k], w.Points[k + 1]);
                        if (pt != null) pts.Add(new Vec3(pt[0], pt[1], pt[2]));
                    }
                if (pts.Count < 3) continue;

                var origin = new Vec3(pts.Average(v => v.X), pts.Average(v => v.Y), pts.Average(v => v.Z));
                var frame = new ProjectionFrame(origin, axisZ, axisX);

                double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
                foreach (var vtx in pts)
                {
                    Vec2 q = frame.Project(vtx);
                    if (q.X < minX) minX = q.X;
                    if (q.X > maxX) maxX = q.X;
                    if (q.Y < minY) minY = q.Y;
                    if (q.Y > maxY) maxY = q.Y;
                }
                double wd = maxX - minX, ht = maxY - minY;
                if (wd < 2 || ht < 2) continue;
                candidates.Add(Tuple.Create(i, string.Format("#{0} {1} {2:F0}×{3:F0}", i, type, wd, ht), wd * ht));
            }
            candidates.Sort((a, b) => b.Item3.CompareTo(a.Item3));   // 큰 면 우선
            foreach (var c in candidates) FaceCombo.Items.Add(new FaceItem { Index = c.Item1, Label = c.Item2 });
            if (FaceCombo.Items.Count > 0) FaceCombo.SelectedIndex = 0;
        }

        private void TryGetParams(out double margin, out double spacing, out double width)
        {
            margin = 5; spacing = 10; width = 1;
            if (!double.TryParse(MarginBox.Text, out margin) || margin < 0) margin = 5;
            if (!double.TryParse(SpacingBox.Text, out spacing) || spacing <= 0) spacing = 10;
            if (!double.TryParse(WidthBox.Text, out width) || width <= 0) width = 1;
        }

        private void Generate_Click(object sender, RoutedEventArgs e)
        {
            if (!WorkingGenerationReady()) return;
            if (_selectedFaceIds.Count == 0 && FaceCombo.SelectedItem is FaceItem comboFace)
            {
                _selectedFaceIds.Add(comboFace.Index);
                SelectedFacesList.Items.Add(string.Format("#{0} {1}", comboFace.Index, Bridge.GetFaceType(comboFace.Index)));
                RefreshSelectedRegion();
            }
            if (_selectedFaceIds.Count == 0)
            {
                StatusText.Text = "면을 선택하세요";
                return;
            }
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

        private void FaceDragSelectMode_Changed(object sender, RoutedEventArgs e)
        {
            bool enabled = FaceDragSelectModeBox.IsChecked == true;
            if (enabled && ExcludeModeBox.IsChecked == true) ExcludeModeBox.IsChecked = false;
            Viewport.RectangleSelectionMode = enabled;
            if (enabled) StatusText.Text = "Face 드래그 선택 모드 — 드래그 박스에 걸친 Face 선택, Ctrl로 추가/토글";
        }

        private void ExcludeMode_Changed(object sender, RoutedEventArgs e)
        {
            bool enabled = ExcludeModeBox.IsChecked == true;
            if (enabled && _multiFaceFrame == null)
            {
                ExcludeModeBox.IsChecked = false;
                StatusText.Text = "제외영역을 그리려면 먼저 같은 평면의 Face를 선택하세요";
                return;
            }
            if (enabled && FaceDragSelectModeBox.IsChecked == true) FaceDragSelectModeBox.IsChecked = false;
            Viewport.ExclusionSketchMode = enabled;
            if (!enabled) Bridge.SetExclusionPreview(null);
            if (enabled) StatusText.Text = "모델 뷰포트에서 사각형 제외영역을 드래그하세요";
        }

        private void OnFacesRectanglePicked(int[] faceIds, bool toggle)
        {
            if (faceIds == null) return;
            _selectedFaceIds.Clear();
            SelectedFacesList.Items.Clear();
            _exclusions.Clear();
            Bridge.ClearExclusionOverlays();
            foreach (int faceIndex in faceIds)
            {
                string type = Bridge.GetFaceType(faceIndex);
                FaceItem item = FaceCombo.Items.OfType<FaceItem>().FirstOrDefault(f => f.Index == faceIndex);
                if (item == null)
                {
                    item = new FaceItem { Index = faceIndex, Label = string.Format("#{0} {1} (picked)", faceIndex, type) };
                    FaceCombo.Items.Add(item);
                }
                _selectedFaceIds.Add(faceIndex);
                SelectedFacesList.Items.Add(string.Format("#{0} {1}", faceIndex, type));
            }
            int active = _selectedFaceIds.FirstOrDefault(-1);
            if (active >= 0)
                FaceCombo.SelectedItem = FaceCombo.Items.OfType<FaceItem>().FirstOrDefault(f => f.Index == active);
            RefreshSelectedRegion();
            StatusText.Text = string.Format("드래그 선택 — {0}개 Face", _selectedFaceIds.Count);
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
            var rect = (minX, minY, maxX, maxY);
            ResetCircuitEditState(clearNative: true);
            _pattern = null;
            _multiFacePatternDisplayed = false;
            Bridge.ClearOverlays();
            _exclusions.Add(rect);
            Bridge.AddExclusionOverlay(ExclusionRectangleWire(a, b));
            DrawRegionCanvas();
            StatusText.Text = string.Format("모델 뷰포트 제외영역 추가 — {0}개", _exclusions.Count);
        }

        private void OnFacePicked(int faceIndex, bool toggle)
        {
            if (Viewport.PowerLocationMode) return;
            if (faceIndex < 0)
            {
                if (!toggle)
                {
                    _selectedFaceIds.Clear();
                    SelectedFacesList.Items.Clear();
                    _exclusions.Clear();
                    Bridge.ClearExclusionOverlays();
                    RefreshSelectedRegion();
                }
                StatusText.Text = "면을 찾지 못했습니다";
                return;
            }
            string type = Bridge.GetFaceType(faceIndex);
            FaceItem match = FaceCombo.Items.OfType<FaceItem>().FirstOrDefault(f => f.Index == faceIndex);
            if (match == null)
            {
                // Face missing from combo (filtered by UV size) -> add it so the click selection is honored
                match = new FaceItem { Index = faceIndex, Label = string.Format("#{0} {1} (picked)", faceIndex, type) };
                FaceCombo.Items.Add(match);
            }
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
                _exclusions.Clear();
                Bridge.ClearExclusionOverlays();
            }
            else
            {
                _selectedFaceIds.Add(faceIndex);
                SelectedFacesList.Items.Add(string.Format("#{0} {1}", faceIndex, type));
                StatusText.Text = string.Format("적용면에 추가 — #{0} ({1}) · 총 {2}개", faceIndex, type, _selectedFaceIds.Count);
                _exclusions.Clear();
                Bridge.ClearExclusionOverlays();
            }
            int activeFace = _selectedFaceIds.Contains(faceIndex) ? faceIndex : _selectedFaceIds.FirstOrDefault(-1);
            if (activeFace >= 0)
                FaceCombo.SelectedItem = FaceCombo.Items.OfType<FaceItem>().FirstOrDefault(f => f.Index == activeFace);
            RefreshSelectedRegion();
        }

        private void RefreshSelectedRegion()
        {
            ResetCircuitEditState(clearNative: true);
            Bridge.ClearOverlays();
            _multiFacePatternDisplayed = false;
            _pattern = null;
            _patternFaceIndex = -1;
            _heatPath3D = null;
            _heatBand3D = null;
            _selectedRegion.Clear();
            _multiFaceFrame = null;
            _multiFaceRegionFailure = null;
            if (_selectedFaceIds.Count == 0)
            {
                DrawRegionCanvas();
                return;
            }

            // Legacy region code is retained behind the migration gate, but must use the confirmed frame.
            if (_workingSide.Confirmed == null) { _multiFaceRegionFailure = "작업 방향을 먼저 확정하세요"; return; }
            var fixedFrame = _workingSide.Confirmed.Frame;

            // Build the union boundary from shared source CAD edges first;
            // only then approximate those surviving 3D edges for projection.
            RegionBoundaryLoop[] sourceLoops = Bridge.GetSelectedRegionBoundary(
                _selectedFaceIds.OrderBy(id => id).ToArray(), 0.05);
            if (sourceLoops == null || sourceLoops.Length == 0 || sourceLoops.Any(loop => !loop.Closed))
            {
                _multiFaceRegionFailure = "원본 CAD 위상에서 닫힌 통합 경계를 구성하지 못했습니다. 선택 Face의 공유 Edge 연결을 확인하세요";
                StatusText.Text = _multiFaceRegionFailure;
                return;
            }
            if (sourceLoops.Length != 1)
            {
                _multiFaceRegionFailure = string.Format(
                    "원본 통합 경계가 {0}개 루프로 나뉩니다. 실제 구멍 또는 분리된 적용영역은 현재 경로 모드에서 지원하지 않습니다",
                    sourceLoops.Length);
                StatusText.Text = _multiFaceRegionFailure;
                return;
            }
            List<Vec3> firstPts = new();
            for (int k = 0; k + 2 < sourceLoops[0].Points.Length; k += 3)
                firstPts.Add(new Vec3(sourceLoops[0].Points[k], sourceLoops[0].Points[k + 1], sourceLoops[0].Points[k + 2]));
            if (firstPts.Count < 3)
            {
                _multiFaceRegionFailure = "원본 통합 경계의 적응 샘플 점이 부족합니다";
                StatusText.Text = _multiFaceRegionFailure;
                return;
            }

            _multiFaceFrame = fixedFrame;
            List<Vec2> projected = firstPts.Select(_multiFaceFrame.Project).ToList();
            if (projected.Count < 3 || Math.Abs(RegionOps.SignedArea(projected)) < 1e-8)
            {
                _multiFaceFrame = null;
                _multiFaceRegionFailure = "원본 통합 경계를 작업 평면에 유효하게 투영하지 못했습니다";
                StatusText.Text = _multiFaceRegionFailure;
                return;
            }
            if (RegionOps.SignedArea(projected) < 0) projected.Reverse();
            _selectedRegion.Add(projected);
            DrawRegionCanvas();
            StatusText.Text = string.Format("확정 작업 방향 기준 선택 Face 영역 (전체 모델 외곽 아님) — {0}개 Face · 경로 생성 보류",
                _selectedFaceIds.Count);
        }

        private void GenerateMultiFace()
        {
            CancelPowerSetup();
            if (!WorkingGenerationReady()) return;
            if (_multiFaceFrame == null || _selectedRegion.Count == 0)
            {
                StatusText.Text = _multiFaceRegionFailure ?? "통합 적용영역을 구성하지 못했습니다. 선택 Face를 제외한 부분 생성은 하지 않습니다";
                return;
            }
            TryGetParams(out double margin, out double spacing, out _);
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
            if (_selectedRegion.Count != 1)
            {
                StatusText.Text = "선택영역이 서로 떨어져 있습니다. 하나로 연결된 적용영역을 선택하세요";
                return;
            }
            var paths = new List<IReadOnlyList<Vec2>>();
            SerpentineResult editablePattern = null;
            try
            {
                foreach (List<Vec2> ring in _selectedRegion)
                {
                    if (_exclusions.Count == 0)
                    {
                        var result = Serpentine.Generate(ring, null,
                            new PatternParams { Margin = margin, Spacing = spacing }, constrainConnectors: true);
                        editablePattern = result;
                        if (result.Path.Count >= 2) paths.Add(result.Path);
                    }
                    else
                    {
                        paths.AddRange(Serpentine.GenerateComponents(ring, _exclusions,
                            new PatternParams { Margin = margin, Spacing = spacing }));
                    }
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "경로 계산 실패: " + ex.Message;
                return;
            }
            int[] selectedFaces = _selectedFaceIds.OrderBy(id => id).ToArray();
            if (paths.Count == 0 && (editablePattern == null || editablePattern.RoutePieces.Count == 0))
            {
                StatusText.Text = "생성 가능한 경로가 없습니다. 마진/간격 또는 선택 영역을 확인하세요";
                return;
            }
            bool routePiecesMode = editablePattern != null && _exclusions.Count == 0 && editablePattern.RoutePieces.Count > 0;
            var side = _workingSide.Confirmed;
            double total = 0;
            // Stage every new AIS object under private negative IDs. Existing geometry and managed state stay intact on failure.
            int projectedCount = 0;
            var stagedRoutes=new List<int>(); var stagedLines=new List<int>();
            var stagedPieceResults=new List<RoutePieceResult>();
            var stagedLengths=new List<double>();
            var finalIds=routePiecesMode ? Enumerable.Range(0,editablePattern.RoutePieces.Count).ToArray() : Array.Empty<int>();
            double[] Flatten(IReadOnlyList<Vec2> points) => points.SelectMany(p => { var w=_multiFaceFrame.Unproject(p); return new[]{w.X,w.Y,w.Z}; }).ToArray();
            try
            {
            if (routePiecesMode)
            {
                for (int i=0;i<editablePattern.RoutePieces.Count;i++)
                {
                    int stageId=int.MinValue+i; var piece=editablePattern.RoutePieces[i];
                    var shown=Bridge.StageProjectedWorkingRoutePiece(side.Id.Value,side.Revision.Value,stageId,piece.Kind.ToString(),
                        Flatten(piece.Points),selectedFaces,-_multiFaceFrame.Normal.X,-_multiFaceFrame.Normal.Y,-_multiFaceFrame.Normal.Z);
                    if (shown==null) throw new InvalidOperationException("OCCT projected route-piece validation/staging failed at piece "+i);
                    stagedRoutes.Add(stageId); stagedPieceResults.Add(shown); stagedLengths.Add(shown.Length); total+=shown.Length;
                }
                projectedCount=editablePattern.RoutePieces.Count;
                for(int i=1;i<stagedPieceResults.Count;i++)
                    if(Distance3D(stagedPieceResults[i-1].End,stagedPieceResults[i].Start)>0.01)
                        throw new InvalidOperationException("Projected neighboring route pieces do not share an endpoint.");
            }
            else for(int i=0;i<paths.Count;i++)
            {
                if(paths[i].Count<2) continue;
                int stageId=int.MinValue/2+i;
                var projected=Bridge.StageProjectedWorkingPolyline(side.Id.Value,side.Revision.Value,stageId,Flatten(paths[i]),selectedFaces,
                    -_multiFaceFrame.Normal.X,-_multiFaceFrame.Normal.Y,-_multiFaceFrame.Normal.Z);
                if(projected==null) throw new InvalidOperationException("OCCT projected exclusion-path validation/staging failed at path "+i);
                total+=projected.Length;
                stagedLines.Add(stageId); projectedCount++;
            }
            if(projectedCount==0) throw new InvalidOperationException("No non-empty projected paths were generated.");
            if(!Bridge.CommitWorkingRouteGeometry(side.Id.Value,side.Revision.Value,stagedRoutes.ToArray(),finalIds,stagedLines.ToArray()))
                throw new InvalidOperationException("native route geometry commit failed");
            }
            catch(Exception ex)
            {
                Bridge.DiscardWorkingRouteGeometry(side.Id.Value,side.Revision.Value,stagedRoutes.ToArray(),stagedLines.ToArray());
                StatusText.Text="표시 생성 실패 — 기존 경로/회로 상태를 유지했습니다: "+ex.Message; return;
            }
            ResetCircuitEditState(clearNative:false);
            _pattern=null; _multiFacePatternDisplayed=false; _projectedPieces.Clear();
            if(routePiecesMode)
            {
                _projectedPieces.AddRange(stagedPieceResults);
                _turnEnabled.Clear(); _turnEnabled.AddRange(editablePattern.RoutePieces.Where(p=>p.Kind==SerpentineRoutePieceKind.TurnConnector).Select(_=>true));
                _routePieceLengths=stagedLengths.ToArray();
                _pattern=editablePattern; _connectorUndo.Clear(); _connectorRedo.Clear(); ApplyConnectorStates();
            }
            Viewport.CircuitHoverMode = routePiecesMode;
            Bridge.RedrawView();
            if (routePiecesMode) { _connectorUndo.Clear(); _connectorRedo.Clear(); }
            StatusText.Text = projectedCount == 0
                ? "생성 가능한 경로가 없습니다. 마진/간격 또는 선택 영역을 확인하세요"
                : string.Format(_exclusions.Count == 0
                    ? "선택면 OCCT 투영 — {0}개 경로 · 길이 {1:F1}mm · 깊이 0"
                    : "제외영역 선택면 OCCT 투영 — {0}개 독립 경로 · 길이 {1:F1}mm · 깊이 0",
                    projectedCount, total, insertionDepth);
            _multiFacePatternDisplayed = projectedCount > 0;
            if (_multiFacePatternDisplayed)
            {
                Bridge.HideFaceHighlights();
                HoverText.Text = "";
                if (routePiecesMode)
                {
                    var circuits = SerpentineTopology.Compute(_pattern.RoutePieces, _turnEnabled);
                    StatusText.Text = string.Format("회로 {0}개 · 총 길이 {1:F1}mm · 삽입 깊이 {2:F2}mm · 커넥터 편집 가능",
                        circuits.Count, total, insertionDepth);
                }
            }
        }

        private void ConnectorEditMode_Changed(object sender, RoutedEventArgs e)
        {
            bool enabled = ConnectorEditModeBox.IsChecked == true && _pattern != null && _pattern.RoutePieces.Count > 0;
            if (ConnectorEditModeBox.IsChecked == true && !enabled) { ConnectorEditModeBox.IsChecked = false; StatusText.Text = "먼저 커넥터가 있는 패턴을 생성하세요"; return; }
            if (enabled && (ExcludeModeBox.IsChecked == true || FaceDragSelectModeBox.IsChecked == true))
            { ExcludeModeBox.IsChecked = false; FaceDragSelectModeBox.IsChecked = false; }
            Viewport.ConnectorEditMode = enabled;
            if (enabled) { Bridge.HideFaceHighlights(); StatusText.Text = "커넥터 편집 — 커넥터를 클릭해 회로를 분리/연결"; }
        }

        private void OnConnectorHovered(int id) { _hoverConnector = id; HoverText.Text = id < 0 ? "" : "커넥터 #" + id; }
        private void OnRoutePieceHovered(int id)
        {
            var groups = CircuitPieceGroups();
            int circuit = groups.FindIndex(group => group.Contains(id));
            Bridge.HighlightCircuit(circuit < 0 ? Array.Empty<int>() : groups[circuit].ToArray());
            HoverText.Text = circuit < 0 ? "" : "회로 #" + (circuit + 1);
        }
        private static double Distance3D(double[] a, double[] b) =>
            Math.Sqrt((a[0]-b[0])*(a[0]-b[0]) + (a[1]-b[1])*(a[1]-b[1]) + (a[2]-b[2])*(a[2]-b[2]));
        private void OnConnectorPicked(int id)
        {
            if (!Viewport.ConnectorEditMode || id < 0 || id >= _pattern?.RoutePieces.Count) return;
            var connectors = _pattern.RoutePieces.Select((p, i) => (p, i)).Where(x => x.p.Kind == SerpentineRoutePieceKind.TurnConnector).ToList();
            int ci = connectors.FindIndex(x => x.i == id);
            if (ci < 0) return;
            _connectorUndo.Push(_turnEnabled.ToArray()); _connectorRedo.Clear(); _turnEnabled[ci] = !_turnEnabled[ci];
            ApplyConnectorStates();
        }
        private void ApplyConnectorStates()
        {
            CancelPowerSetup();
            var turns = _pattern.RoutePieces.Select((p, i) => (p, i)).Where(x => x.p.Kind == SerpentineRoutePieceKind.TurnConnector).ToList();
            for (int i = 0; i < turns.Count; i++) Bridge.SetConnectorEnabled(turns[i].i, _turnEnabled[i], 0.2, 0.8, 0.3);
            var circuits = SerpentineTopology.Compute(_pattern.RoutePieces, _turnEnabled);
            double length = _pattern.RoutePieces.Select((p, i) => (p, i)).Where(x => x.p.Kind != SerpentineRoutePieceKind.TurnConnector || _turnEnabled[turns.FindIndex(t => t.i == x.i)]).Sum(x => _routePieceLengths.ElementAtOrDefault(x.i));
            _leadLengths = new double[circuits.Count];
            UpdateCircuitTable(circuits);
            StatusText.Text = string.Format("회로 {0}개 · 길이 합계 {1:F1}mm", circuits.Count, length);
        }
        private void UpdateCircuitTable(IReadOnlyList<IReadOnlyList<Vec2>> circuits)
        {
            var rows = new List<CircuitLengthRow>();
            var groups = CircuitPieceGroups();
            for (int i = 0; i < groups.Count; i++)
            {
                double heating = groups[i].Sum(id => _routePieceLengths[id]);
                rows.Add(new CircuitLengthRow { Circuit = (i + 1).ToString(), HeatingLength = heating, LeadLength = _leadLengths.ElementAtOrDefault(i) });
            }
            CircuitLengthGrid.ItemsSource = rows;
        }

        // Group stable route-piece IDs, not resampled planar points. Disabled
        // turns are separators and contribute neither geometry nor length.
        private List<List<int>> CircuitPieceGroups()
        {
            var groups = new List<List<int>>();
            var current = new List<int>();
            int turn = 0;
            if (_pattern == null) return groups;
            for (int id = 0; id < _pattern.RoutePieces.Count; id++)
            {
                bool enabled = _pattern.RoutePieces[id].Kind != SerpentineRoutePieceKind.TurnConnector || _turnEnabled[turn++];
                if (!enabled)
                {
                    if (current.Count > 0) { groups.Add(current); current = new List<int>(); }
                }
                else if (_pattern.RoutePieces[id].Points.Count > 0) current.Add(id);
            }
            if (current.Count > 0) groups.Add(current);
            return groups;
        }
        private void PowerSetup_Click(object sender, RoutedEventArgs e)
        {
            if (_workingSide.Confirmed == null) { WorkingGenerationReady(); return; }
            if (!WorkingRouteMigrationComplete) { StatusText.Text = "생성 보류: 전원 경로는 아직 작업 방향 첫 충돌 경로로 마이그레이션되지 않았습니다"; return; }
            if (!WorkingGenerationReady()) return;
            if (_pattern == null || _multiFaceFrame == null) { StatusText.Text = "먼저 커넥터 편집 가능한 패턴을 생성하세요"; return; }
            CancelPowerSetup();
            if (_projectedPieces.Count != _pattern.RoutePieces.Count) { StatusText.Text = "3D 경로 끝점이 없습니다. 패턴을 다시 생성하세요"; return; }
            var groups = CircuitPieceGroups();
            _leadLengths = new double[groups.Count];
            for (int i = 0; i < groups.Count; i++)
            {
                double[] a = _projectedPieces[groups[i][0]].Start, b = _projectedPieces[groups[i][^1]].End;
                _pendingLeads.Add(new PendingLead(i, true, new Vec3(a[0], a[1], a[2])));
                _pendingLeads.Add(new PendingLead(i, false, new Vec3(b[0], b[1], b[2])));
            }
            ConnectorEditModeBox.IsChecked = false;
            ExcludeModeBox.IsChecked = false;
            FaceDragSelectModeBox.IsChecked = false;
            Viewport.ConnectorEditMode = false;
            Viewport.ExclusionSketchMode = false;
            Viewport.RectangleSelectionMode = false;
            Bridge.HideFaceHighlights();
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
            double[] hit = Bridge.PickSelectedBoundaryPoint(_selectedFaceIds.OrderBy(id => id).ToArray(), x, y);
            if (hit == null || hit.Length < 4 || hit.Any(v => !double.IsFinite(v))) { Bridge.ClearPowerLeadPreview(); if (!previewOnly) StatusText.Text = "선택 가열면의 외곽 경계를 클릭하세요 · " + PowerPrompt(); return; }
            var pending = _pendingLeads[0];
            Vec3 end = new(hit[0], hit[1], hit[2]);
            Vec3 source = _multiFaceFrame.Unproject(_multiFaceFrame.Project(pending.Start));
            Vec3 datumEnd = end + _multiFaceFrame.Normal * _insertionDepth;
            Vec3 direction = _multiFaceFrame.Normal * -1.0;
            var result = Bridge.ProjectPowerLead(pending.CircuitIndex * 2 + (pending.IsStart ? 0 : 1),
                new[] { source.X, source.Y, source.Z, datumEnd.X, datumEnd.Y, datumEnd.Z },
                new[] { pending.Start.X, pending.Start.Y, pending.Start.Z }, _selectedFaceIds.OrderBy(id => id).ToArray(),
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
        private void ConnectorUndo_Click(object sender, RoutedEventArgs e) { if (_connectorUndo.Count == 0) return; _connectorRedo.Push(_turnEnabled.ToArray()); _turnEnabled.Clear(); _turnEnabled.AddRange(_connectorUndo.Pop()); ApplyConnectorStates(); }
        private void ConnectorRedo_Click(object sender, RoutedEventArgs e) { if (_connectorRedo.Count == 0) return; _connectorUndo.Push(_turnEnabled.ToArray()); _turnEnabled.Clear(); _turnEnabled.AddRange(_connectorRedo.Pop()); ApplyConnectorStates(); }

        private void DrawRegionCanvas()
        {
            if (RegionCanvas == null) return;
            RegionCanvas.Children.Clear();
            if (_selectedRegion.Count == 0 || RegionCanvas.ActualWidth < 2 || RegionCanvas.ActualHeight < 2) return;
            foreach (var ring in _selectedRegion)
            {
                var poly = new Polyline { Stroke = Brushes.SteelBlue, StrokeThickness = 2 };
                foreach (Vec2 p in ring) poly.Points.Add(WorldToCanvas(p));
                if (poly.Points.Count > 0) poly.Points.Add(poly.Points[0]);
                RegionCanvas.Children.Add(poly);
            }
            foreach (var r in _exclusions)
            {
                Point a = WorldToCanvas(new Vec2(r.MinX, r.MinY));
                Point b = WorldToCanvas(new Vec2(r.MaxX, r.MaxY));
                var rect = new Rectangle { Width = Math.Abs(b.X - a.X), Height = Math.Abs(b.Y - a.Y),
                    Stroke = Brushes.Red, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(45, 255, 0, 0)) };
                Canvas.SetLeft(rect, Math.Min(a.X, b.X)); Canvas.SetTop(rect, Math.Min(a.Y, b.Y));
                RegionCanvas.Children.Add(rect);
            }
        }

        private (double MinX, double MinY, double MaxX, double MaxY) RegionBounds()
        {
            var points = _selectedRegion.SelectMany(r => r).ToList();
            return (points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
        }

        private Point WorldToCanvas(Vec2 p)
        {
            var b = RegionBounds();
            double sx = Math.Max(1e-9, b.MaxX - b.MinX), sy = Math.Max(1e-9, b.MaxY - b.MinY);
            double scale = Math.Min((RegionCanvas.ActualWidth - 16) / sx, (RegionCanvas.ActualHeight - 16) / sy);
            double x = (RegionCanvas.ActualWidth - sx * scale) / 2 + (p.X - b.MinX) * scale;
            double y = (RegionCanvas.ActualHeight - sy * scale) / 2 + (b.MaxY - p.Y) * scale;
            return new Point(x, y);
        }

        private void ClearExclusions_Click(object sender, RoutedEventArgs e)
        {
            ResetCircuitEditState(clearNative: true);
            _pattern = null;
            _multiFacePatternDisplayed = false;
            Bridge.ClearOverlays();
            _exclusions.Clear();
            Bridge.ClearExclusionOverlays();
            DrawRegionCanvas();
            StatusText.Text = "제외 사각형을 모두 지웠습니다";
        }

        private void RegionCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawRegionCanvas();

        private List<DxfView> BuildViews()
        {
            var views = new List<DxfView>();
            double[] x = Bridge.GetAutoAxis(0), y = Bridge.GetAutoAxis(1), z = Bridge.GetAutoAxis(2);
            if (x == null || y == null || z == null) return views;
            views.Add(BuildHlrView("back", z[0], z[1], z[2], x[0], x[1], x[2]));
            views.Add(BuildHlrView("front", -z[0], -z[1], -z[2], x[0], x[1], x[2]));
            views.Add(BuildHlrView("top", -y[0], -y[1], -y[2], x[0], x[1], x[2]));
            views.Add(BuildHlrView("side", x[0], x[1], x[2], y[0], y[1], y[2]));
            return views;
        }

        private DxfView BuildHlrView(string label, double nx, double ny, double nz,
                                     double xx, double xy, double xz)
        {
            var view = new DxfView { Label = label };
            var N = new Vec3(nx, ny, nz).Normalized();
            Vec3 xAxis = new Vec3(xx, xy, xz).Normalized();
            Vec3 yAxis = Vec3.Cross(N, xAxis).Normalized();

            foreach (var pl in Bridge.HlrEdges(nx, ny, nz, xAxis.X, xAxis.Y, xAxis.Z, true))
            {
                var pts = new List<Vec2>(pl.Points.Length / 2);
                for (int k = 0; k + 1 < pl.Points.Length; k += 2)
                    pts.Add(new Vec2(pl.Points[k], pl.Points[k + 1]));
                var poly = new DxfPolyline { Points = pts };
                if (pl.Visible) view.VisibleEdges.Add(poly);
                else view.HiddenEdges.Add(poly);
            }

            if (_heatPath3D != null && _heatPath3D.Count >= 2)
                view.HeatLines.Add(new DxfPolyline { Points = _heatPath3D.Select(p => Project(p, xAxis, yAxis)).ToList() });
            if (_heatBand3D != null && _heatBand3D.Count >= 3)
                view.HeatBands.Add(new DxfPolyline { Points = _heatBand3D.Select(p => Project(p, xAxis, yAxis)).ToList(), Closed = true });

            return view;
        }

        private static Vec2 Project(Vec3 p, Vec3 xAxis, Vec3 yAxis)
            => new Vec2(Vec3.Dot(p, xAxis), Vec3.Dot(p, yAxis));

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            if (_pattern == null)
            {
                StatusText.Text = _multiFacePatternDisplayed
                    ? "다중 Face/제외영역 DXF 출력은 아직 지원하지 않습니다"
                    : "먼저 [패턴 생성]을 실행하세요";
                return;
            }
            TryGetParams(out _, out _, out double width);

            string root = OcctEnv.RepoRoot ?? ".";
            string dir = Path.Combine(root, "output");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "heat_pattern_face" + _patternFaceIndex + ".dxf");

            try
            {
                var views = BuildViews();
                DxfExporter.ExportViews(file, views);
                StatusText.Text = "DXF 저장 — " + file;
            }
            catch (Exception ex)
            {
                StatusText.Text = "DXF 저장 실패: " + ex.Message;
            }
        }

        private void StandardView_Click(object sender, RoutedEventArgs e)
        {
            Viewport.CancelPowerLeadPreview();
            string view = (sender as Button)?.Tag as string;
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

        private void Fit_Click(object sender, RoutedEventArgs e) { Viewport.CancelPowerLeadPreview(); Bridge.FitView(); }

        private void ZoomIn_Click(object sender, RoutedEventArgs e) { Viewport.CancelPowerLeadPreview(); Bridge.ZoomView(1.25); }

        private void ZoomOut_Click(object sender, RoutedEventArgs e) { Viewport.CancelPowerLeadPreview(); Bridge.ZoomView(0.8); }

    }
}
