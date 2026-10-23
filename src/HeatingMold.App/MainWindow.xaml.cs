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
        private HeatingPath _pattern;
        private readonly HashSet<int> _selectedFaceIds = new();
        private List<List<Vec2>> _selectedRegion = new();
        private ProjectionFrame _multiFaceProjectionFrame;
        private string _multiFaceRegionFailure;
        private readonly List<(double MinX, double MinY, double MaxX, double MaxY)> _exclusions = new();
        private bool _multiFacePatternDisplayed;
        private readonly List<bool> _turnSegmentEnabled = new();
        private double[] _pathSegmentLengths = Array.Empty<double>();
        private readonly List<HeatingPathSegmentResult> _projectedPathSegments = new();
        private int _hoveredTurnSegment = -1;
        private sealed class CircuitLengthRow { public string Circuit { get; set; } public double HeatingLength { get; set; } public double ConnectionLength { get; set; } public double TotalLength => HeatingLength + ConnectionLength; }
        private sealed record PendingConnectionPoint(int CircuitIndex, bool IsStart, Vec3 Start);
        private List<PendingConnectionPoint> _pendingConnectionPoints = new();
        private double[] _connectionLengths = Array.Empty<double>();
        private double _insertionDepthMm;

        private void CancelConnectionPointSetup()
        {
            _pendingConnectionPoints.Clear();
            if (Viewport != null) Viewport.ConnectionPointSelectionMode = false;
            Bridge.ClearConnectionPaths();
            _connectionLengths = Array.Empty<double>();
            SetConnectionPointEditingLock(false);
            if (_pattern != null && _pathSegmentLengths.Length == _pattern.PathSegments.Count)
                UpdateCircuitTable(CircuitPathPartitioner.Compute(_pattern.PathSegments, _turnSegmentEnabled));
        }

        private void SetConnectionPointEditingLock(bool locked)
        {
            if (CircuitSplitModeBox == null) return;
            CircuitSplitModeBox.IsEnabled = !locked;
            FaceDragSelectModeBox.IsEnabled = !locked;
            ExcludeModeBox.IsEnabled = !locked;
            FaceCombo.IsEnabled = !locked;
        }

        private string ConnectionPointPrompt() => _pendingConnectionPoints.Count == 0 ? "연결부 위치 지정 완료" :
            $"회로 {_pendingConnectionPoints[0].CircuitIndex + 1} · {(_pendingConnectionPoints[0].IsStart ? "시작" : "끝")} 연결부 위치 (발열사 경로 끝점 자동) — 금형 밖 목적지만 클릭하세요";

        private void ResetCircuitEditState(bool clearNative)
        {
            CancelConnectionPointSetup();
            _projectedPathSegments.Clear();
            Viewport.CircuitSplitMode = false;
            if (CircuitSplitModeBox != null) CircuitSplitModeBox.IsChecked = false;
            _turnSegmentEnabled.Clear();
            _pathSegmentLengths = Array.Empty<double>();
            _hoveredTurnSegment = -1;
            _pendingConnectionPoints.Clear();
            _connectionLengths = Array.Empty<double>();
            if (Viewport != null) Viewport.ConnectionPointSelectionMode = false;
            if (CircuitLengthGrid != null) CircuitLengthGrid.ItemsSource = null;
            if (clearNative) Bridge.ClearPathSegments();
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
            Viewport.TurnSegmentPicked += OnTurnSegmentPicked;
            Viewport.ConnectionPointPicked += OnConnectionPointPicked;
            Viewport.ConnectionPointMoved += OnConnectionPointMoved;
            Viewport.TurnSegmentHovered += OnTurnSegmentHovered;
            Viewport.HeatingPathSegmentHovered += OnHeatingPathSegmentHovered;
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
            ResetWorkingCoordinateSystem();
            CancelConnectionPointSetup();
            var sw = Stopwatch.StartNew();
            if (!Bridge.LoadStep(path))
            {
                ResetCircuitEditState(clearNative: true);
                _pattern = null;
                _selectedFaceIds.Clear();
                SelectedFacesList.Items.Clear();
                FaceCombo.Items.Clear();
                _selectedRegion.Clear();
                _multiFaceProjectionFrame = null;
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
            _selectedFaceIds.Clear();
            SelectedFacesList.Items.Clear();
            _selectedRegion.Clear();
            _multiFaceProjectionFrame = null;
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
            ApplyWorkingCoordinateSystem(new Vec3(0,0,1));
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

        private void TryGetParams(out double boundaryMarginMm, out double centerPitchMm, out double wireDiameterMm)
        {
            boundaryMarginMm = 5; centerPitchMm = 10; wireDiameterMm = 1;
            if (!double.TryParse(BoundaryMarginBox.Text, out boundaryMarginMm) || boundaryMarginMm < 0) boundaryMarginMm = 5;
            if (!double.TryParse(CenterPitchBox.Text, out centerPitchMm) || centerPitchMm <= 0) centerPitchMm = 10;
            if (!double.TryParse(WireDiameterBox.Text, out wireDiameterMm) || wireDiameterMm <= 0) wireDiameterMm = 1;
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
            if (enabled && _multiFaceProjectionFrame == null)
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
            if (_multiFaceProjectionFrame == null) return false;
            Vec3 o = _multiFaceProjectionFrame.Origin, n = _multiFaceProjectionFrame.Normal;
            double[] hit = Bridge.ScreenPointOnPlane(x, y, o.X, o.Y, o.Z, n.X, n.Y, n.Z);
            if (hit == null || hit.Length < 3 || hit.Any(v => !double.IsFinite(v))) return false;
            world = new Vec3(hit[0], hit[1], hit[2]);
            point = _multiFaceProjectionFrame.Project(world);
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
                Vec3 p = _multiFaceProjectionFrame.Unproject(corners[i]);
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
            if (Viewport.ConnectionPointSelectionMode) return;
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
            _selectedRegion.Clear();
            _multiFaceProjectionFrame = null;
            _multiFaceRegionFailure = null;
            if (_selectedFaceIds.Count == 0)
            {
                DrawRegionCanvas();
                return;
            }

            // Legacy region code is retained behind the migration gate, but must use the confirmed frame.
            if (_workingCoordinateSystem.Confirmed == null) { _multiFaceRegionFailure = "작업 좌표계를 먼저 확정하세요"; return; }
            var fixedFrame = _workingCoordinateSystem.Confirmed.Projection;

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
                    "원본 통합 경계가 {0}개 루프로 나뉩니다. 실제 구멍 또는 분리된 발열 영역은 현재 경로 생성 모드에서 지원하지 않습니다",
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

            _multiFaceProjectionFrame = fixedFrame;
            List<Vec2> projected = firstPts.Select(_multiFaceProjectionFrame.Project).ToList();
            if (projected.Count < 3 || Math.Abs(RegionOps.SignedArea(projected)) < 1e-8)
            {
                _multiFaceProjectionFrame = null;
                _multiFaceRegionFailure = "원본 통합 경계를 작업 평면에 유효하게 투영하지 못했습니다";
                StatusText.Text = _multiFaceRegionFailure;
                return;
            }
            if (RegionOps.SignedArea(projected) < 0) projected.Reverse();
            _selectedRegion.Add(projected);
            DrawRegionCanvas();
            StatusText.Text = string.Format("확정 작업 좌표계 기준 발열 영역 (전체 모델 외곽 아님) — {0}개 Face · 발열사 경로 생성 보류",
                _selectedFaceIds.Count);
        }

        private void GenerateMultiFace()
        {
            CancelConnectionPointSetup();
            if (!WorkingGenerationReady()) return;
            if (_multiFaceProjectionFrame == null || _selectedRegion.Count == 0)
            {
                StatusText.Text = _multiFaceRegionFailure ?? "통합 발열 영역을 구성하지 못했습니다. 선택 Face를 제외한 부분 생성은 하지 않습니다";
                return;
            }
            TryGetParams(out double boundaryMarginMm, out double centerPitchMm, out _);
            if (!double.TryParse(InsertionDepthBox.Text, out double insertionDepthMm) ||
                !double.IsFinite(insertionDepthMm) || insertionDepthMm < 0)
            {
                StatusText.Text = "삽입 깊이는 0 이상의 유한한 mm 값이어야 합니다";
                return;
            }
            _insertionDepthMm = insertionDepthMm;
            if (insertionDepthMm != 0)
            {
                StatusText.Text = "작업 좌표계 기준 전체 모델 첫 충돌 검증은 삽입 깊이 0만 지원합니다";
                return;
            }
            if (_selectedRegion.Count != 1)
            {
                StatusText.Text = "선택한 발열 영역이 서로 떨어져 있습니다. 하나로 연결된 영역을 선택하세요";
                return;
            }
            var paths = new List<IReadOnlyList<Vec2>>();
            HeatingPath editablePattern = null;
            try
            {
                foreach (List<Vec2> ring in _selectedRegion)
                {
                    if (_exclusions.Count == 0)
                    {
                        var result = HeatingPathGenerator.Generate(ring, null,
                            new PatternParameters { BoundaryMarginMm = boundaryMarginMm, CenterPitchMm = centerPitchMm }, requireContinuousPath: true);
                        editablePattern = result;
                        if (result.Path.Count >= 2) paths.Add(result.Path);
                    }
                    else
                    {
                        paths.AddRange(HeatingPathGenerator.GenerateComponents(ring, _exclusions,
                            new PatternParameters { BoundaryMarginMm = boundaryMarginMm, CenterPitchMm = centerPitchMm }));
                    }
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "경로 계산 실패: " + ex.Message;
                return;
            }
            int[] selectedFaces = _selectedFaceIds.OrderBy(id => id).ToArray();
            if (paths.Count == 0 && (editablePattern == null || editablePattern.PathSegments.Count == 0))
            {
                StatusText.Text = "생성 가능한 발열사 경로가 없습니다. 경계 여유/중심 피치 또는 발열 영역을 확인하세요";
                return;
            }
            bool pathSegmentsMode = editablePattern != null && _exclusions.Count == 0 && editablePattern.PathSegments.Count > 0;
            var coordinateSystem = _workingCoordinateSystem.Confirmed;
            double total = 0;
            // Stage every new AIS object under private negative IDs. Existing geometry and managed state stay intact on failure.
            int projectedCount = 0;
            var stagedSegments=new List<int>(); var stagedLines=new List<int>();
            var stagedPieceResults=new List<HeatingPathSegmentResult>();
            var stagedLengths=new List<double>();
            var finalIds=pathSegmentsMode ? Enumerable.Range(0,editablePattern.PathSegments.Count).ToArray() : Array.Empty<int>();
            double[] Flatten(IReadOnlyList<Vec2> points) => points.SelectMany(p => { var w=_multiFaceProjectionFrame.Unproject(p); return new[]{w.X,w.Y,w.Z}; }).ToArray();
            try
            {
            if (pathSegmentsMode)
            {
                for (int i=0;i<editablePattern.PathSegments.Count;i++)
                {
                    int stageId=int.MinValue+i; var piece=editablePattern.PathSegments[i];
                    var shown=Bridge.StageProjectedWorkingPathSegment(coordinateSystem.Id.Value,coordinateSystem.Revision.Value,stageId,piece.Kind.ToString(),
                        Flatten(piece.Points),selectedFaces,-_multiFaceProjectionFrame.Normal.X,-_multiFaceProjectionFrame.Normal.Y,-_multiFaceProjectionFrame.Normal.Z);
                    if (shown==null) throw new InvalidOperationException("OCCT projected path-segment validation/staging failed at piece "+i);
                    stagedSegments.Add(stageId); stagedPieceResults.Add(shown); stagedLengths.Add(shown.Length); total+=shown.Length;
                }
                projectedCount=editablePattern.PathSegments.Count;
                for(int i=1;i<stagedPieceResults.Count;i++)
                    if(Distance3D(stagedPieceResults[i-1].End,stagedPieceResults[i].Start)>0.01)
                        throw new InvalidOperationException("Projected neighboring heating path segments do not share an endpoint.");
            }
            else for(int i=0;i<paths.Count;i++)
            {
                if(paths[i].Count<2) continue;
                int stageId=int.MinValue/2+i;
                var projected=Bridge.StageProjectedWorkingPolyline(coordinateSystem.Id.Value,coordinateSystem.Revision.Value,stageId,Flatten(paths[i]),selectedFaces,
                    -_multiFaceProjectionFrame.Normal.X,-_multiFaceProjectionFrame.Normal.Y,-_multiFaceProjectionFrame.Normal.Z);
                if(projected==null) throw new InvalidOperationException("OCCT projected exclusion-path validation/staging failed at path "+i);
                total+=projected.Length;
                stagedLines.Add(stageId); projectedCount++;
            }
            if(projectedCount==0) throw new InvalidOperationException("No non-empty projected paths were generated.");
            if(!Bridge.CommitWorkingPathGeometry(coordinateSystem.Id.Value,coordinateSystem.Revision.Value,stagedSegments.ToArray(),finalIds,stagedLines.ToArray()))
                throw new InvalidOperationException("native heating-path geometry commit failed");
            }
            catch(Exception ex)
            {
                Bridge.DiscardWorkingPathGeometry(coordinateSystem.Id.Value,coordinateSystem.Revision.Value,stagedSegments.ToArray(),stagedLines.ToArray());
                StatusText.Text="발열사 경로 표시 생성 실패 — 기존 경로/회로 상태를 유지했습니다: "+ex.Message; return;
            }
            ResetCircuitEditState(clearNative:false);
            _pattern=null; _multiFacePatternDisplayed=false; _projectedPathSegments.Clear();
            if(pathSegmentsMode)
            {
                _projectedPathSegments.AddRange(stagedPieceResults);
                _turnSegmentEnabled.Clear(); _turnSegmentEnabled.AddRange(editablePattern.PathSegments.Where(p=>p.Kind==HeatingPathSegmentKind.TurnSegment).Select(_=>true));
                _pathSegmentLengths=stagedLengths.ToArray();
                _pattern=editablePattern; ApplyPathSegmentStates();
            }
            Viewport.CircuitHoverMode = pathSegmentsMode;
            Bridge.RedrawView();
            StatusText.Text = projectedCount == 0
                ? "생성 가능한 발열사 경로가 없습니다. 경계 여유/중심 피치 또는 발열 영역을 확인하세요"
                : string.Format(_exclusions.Count == 0
                    ? "선택 Face OCCT 투영 — 발열사 경로 {0}개 · 길이 {1:F1}mm · 삽입 깊이 0"
                    : "제외영역 선택 Face OCCT 투영 — 독립 발열사 경로 {0}개 · 길이 {1:F1}mm · 삽입 깊이 0",
                    projectedCount, total, insertionDepthMm);
            _multiFacePatternDisplayed = projectedCount > 0;
            if (_multiFacePatternDisplayed)
            {
                Bridge.HideFaceHighlights();
                HoverText.Text = "";
                if (pathSegmentsMode)
                {
                    var circuits = CircuitPathPartitioner.Compute(_pattern.PathSegments, _turnSegmentEnabled);
                    StatusText.Text = string.Format("회로 {0}개 · 총 길이 {1:F1}mm · 삽입 깊이 {2:F2}mm · 회로 분할 구간 편집 가능",
                        circuits.Count, total, insertionDepthMm);
                }
            }
        }

        private void CircuitSplitMode_Changed(object sender, RoutedEventArgs e)
        {
            bool enabled = CircuitSplitModeBox.IsChecked == true && _pattern != null && _pattern.PathSegments.Count > 0;
            if (CircuitSplitModeBox.IsChecked == true && !enabled) { CircuitSplitModeBox.IsChecked = false; StatusText.Text = "먼저 분할 가능한 회전 구간이 있는 패턴을 생성하세요"; return; }
            if (enabled && (ExcludeModeBox.IsChecked == true || FaceDragSelectModeBox.IsChecked == true))
            { ExcludeModeBox.IsChecked = false; FaceDragSelectModeBox.IsChecked = false; }
            Viewport.CircuitSplitMode = enabled;
            if (enabled) { Bridge.HideFaceHighlights(); StatusText.Text = "회로 분할 구간 편집 — 회전 구간을 클릭해 회로를 분리/연결"; }
        }

        private void OnTurnSegmentHovered(int id) { _hoveredTurnSegment = id; HoverText.Text = id < 0 ? "" : "회로 분할 구간 #" + id; }
        private void OnHeatingPathSegmentHovered(int id)
        {
            var groups = CircuitPieceGroups();
            int circuit = groups.FindIndex(group => group.Contains(id));
            Bridge.HighlightCircuit(circuit < 0 ? Array.Empty<int>() : groups[circuit].ToArray());
            HoverText.Text = circuit < 0 ? "" : "회로 #" + (circuit + 1);
        }
        private static double Distance3D(double[] a, double[] b) =>
            Math.Sqrt((a[0]-b[0])*(a[0]-b[0]) + (a[1]-b[1])*(a[1]-b[1]) + (a[2]-b[2])*(a[2]-b[2]));
        private void OnTurnSegmentPicked(int id)
        {
            if (!Viewport.CircuitSplitMode || id < 0 || id >= _pattern?.PathSegments.Count) return;
            var turnSegments = _pattern.PathSegments.Select((p, i) => (p, i)).Where(x => x.p.Kind == HeatingPathSegmentKind.TurnSegment).ToList();
            int ci = turnSegments.FindIndex(x => x.i == id);
            if (ci < 0) return;
            _turnSegmentEnabled[ci] = !_turnSegmentEnabled[ci];
            ApplyPathSegmentStates();
        }
        private void ApplyPathSegmentStates()
        {
            CancelConnectionPointSetup();
            var turns = _pattern.PathSegments.Select((p, i) => (p, i)).Where(x => x.p.Kind == HeatingPathSegmentKind.TurnSegment).ToList();
            for (int i = 0; i < turns.Count; i++) Bridge.SetTurnSegmentEnabled(turns[i].i, _turnSegmentEnabled[i], 0.2, 0.8, 0.3);
            var circuits = CircuitPathPartitioner.Compute(_pattern.PathSegments, _turnSegmentEnabled);
            double length = _pattern.PathSegments.Select((p, i) => (p, i)).Where(x => x.p.Kind != HeatingPathSegmentKind.TurnSegment || _turnSegmentEnabled[turns.FindIndex(t => t.i == x.i)]).Sum(x => _pathSegmentLengths.ElementAtOrDefault(x.i));
            _connectionLengths = new double[circuits.Count];
            UpdateCircuitTable(circuits);
            StatusText.Text = string.Format("회로 {0}개 · 길이 합계 {1:F1}mm", circuits.Count, length);
        }
        private void UpdateCircuitTable(IReadOnlyList<IReadOnlyList<Vec2>> circuits)
        {
            var rows = new List<CircuitLengthRow>();
            var groups = CircuitPieceGroups();
            for (int i = 0; i < groups.Count; i++)
            {
                double heating = groups[i].Sum(id => _pathSegmentLengths[id]);
                rows.Add(new CircuitLengthRow { Circuit = (i + 1).ToString(), HeatingLength = heating, ConnectionLength = _connectionLengths.ElementAtOrDefault(i) });
            }
            CircuitLengthGrid.ItemsSource = rows;
        }

        // Group stable path-segment IDs, not resampled planar points. Disabled
        // turns are separators and contribute neither geometry nor length.
        private List<List<int>> CircuitPieceGroups()
        {
            var groups = new List<List<int>>();
            var current = new List<int>();
            int turn = 0;
            if (_pattern == null) return groups;
            for (int id = 0; id < _pattern.PathSegments.Count; id++)
            {
                bool enabled = _pattern.PathSegments[id].Kind != HeatingPathSegmentKind.TurnSegment || _turnSegmentEnabled[turn++];
                if (!enabled)
                {
                    if (current.Count > 0) { groups.Add(current); current = new List<int>(); }
                }
                else if (_pattern.PathSegments[id].Points.Count > 0) current.Add(id);
            }
            if (current.Count > 0) groups.Add(current);
            return groups;
        }
        private void ConnectionPointSetup_Click(object sender, RoutedEventArgs e)
        {
            if (_workingCoordinateSystem.Confirmed == null) { WorkingGenerationReady(); return; }
            if (!ConnectionPathMigrationComplete) { StatusText.Text = "연결부 위치 지정 보류: 연결 경로가 작업 좌표계 기준 전체 모델 첫 충돌 검증으로 전환되지 않았습니다"; return; }
            if (!WorkingGenerationReady()) return;
            if (_pattern == null || _multiFaceProjectionFrame == null) { StatusText.Text = "먼저 회로 분할 구간 편집 가능한 패턴을 생성하세요"; return; }
            CancelConnectionPointSetup();
            if (_projectedPathSegments.Count != _pattern.PathSegments.Count) { StatusText.Text = "3D 경로 끝점이 없습니다. 패턴을 다시 생성하세요"; return; }
            var groups = CircuitPieceGroups();
            _connectionLengths = new double[groups.Count];
            for (int i = 0; i < groups.Count; i++)
            {
                double[] a = _projectedPathSegments[groups[i][0]].Start, b = _projectedPathSegments[groups[i][^1]].End;
                _pendingConnectionPoints.Add(new PendingConnectionPoint(i, true, new Vec3(a[0], a[1], a[2])));
                _pendingConnectionPoints.Add(new PendingConnectionPoint(i, false, new Vec3(b[0], b[1], b[2])));
            }
            CircuitSplitModeBox.IsChecked = false;
            ExcludeModeBox.IsChecked = false;
            FaceDragSelectModeBox.IsChecked = false;
            Viewport.CircuitSplitMode = false;
            Viewport.ExclusionSketchMode = false;
            Viewport.RectangleSelectionMode = false;
            Bridge.HideFaceHighlights();
            HoverText.Text = "";
            Viewport.ConnectionPointSelectionMode = _pendingConnectionPoints.Count > 0;
            Viewport.CircuitHoverMode = false;
            SetConnectionPointEditingLock(Viewport.ConnectionPointSelectionMode);
            UpdateCircuitTable(CircuitPathPartitioner.Compute(_pattern.PathSegments, _turnSegmentEnabled));
            StatusText.Text = ConnectionPointPrompt();
        }
        private void ConnectionPointCancel_Click(object sender, RoutedEventArgs e)
        {
            CancelConnectionPointSetup();
            StatusText.Text = "연결부 위치 지정 취소 — 연결 경로와 연결 길이를 초기화했습니다";
        }
        private void OnConnectionPointMoved(int x, int y) => UpdateConnectionPoint(x, y, previewOnly: true);

        private void OnConnectionPointPicked(int x, int y)
        {
            Viewport.CancelConnectionPathPreview();
            UpdateConnectionPoint(x, y, previewOnly: false);
        }

        private void UpdateConnectionPoint(int x, int y, bool previewOnly)
        {
            if (!ConnectionPathMigrationComplete) { Bridge.ClearConnectionPathPreview(); return; }
            if (_pendingConnectionPoints.Count == 0 || !Viewport.ConnectionPointSelectionMode) { Bridge.ClearConnectionPathPreview(); return; }
            double[] hit = Bridge.PickSelectedBoundaryPoint(_selectedFaceIds.OrderBy(id => id).ToArray(), x, y);
            if (hit == null || hit.Length < 4 || hit.Any(v => !double.IsFinite(v))) { Bridge.ClearConnectionPathPreview(); if (!previewOnly) StatusText.Text = "선택 발열 영역의 외곽 경계를 클릭하세요 · " + ConnectionPointPrompt(); return; }
            var pending = _pendingConnectionPoints[0];
            Vec3 end = new(hit[0], hit[1], hit[2]);
            Vec3 source = _multiFaceProjectionFrame.Unproject(_multiFaceProjectionFrame.Project(pending.HeatingPathEndpoint));
            Vec3 datumEnd = end + _multiFaceProjectionFrame.Normal * _insertionDepthMm;
            Vec3 direction = _multiFaceProjectionFrame.Normal * -1.0;
            var result = Bridge.ProjectConnectionPath(pending.CircuitIndex * 2 + (pending.IsStart ? 0 : 1),
                new[] { source.X, source.Y, source.Z, datumEnd.X, datumEnd.Y, datumEnd.Z },
                new[] { pending.HeatingPathEndpoint.X, pending.HeatingPathEndpoint.Y, pending.HeatingPathEndpoint.Z }, _selectedFaceIds.OrderBy(id => id).ToArray(),
                direction.X, direction.Y, direction.Z, _insertionDepthMm, previewOnly);
            if (result == null || result.Length < 4 || result[0] != 1)
            {
                if (previewOnly) return; // Invalid hover: do not imply a valid connection path or endpoint.
                string reason = result == null ? "native 오류" : result[0] switch {
                    -2 => "금형 밖 목적지를 선택하세요", -3 => "표면 투영이 끊기거나 중복됩니다 (구멍/재진입/다층)",
                    -4 => "실제 3D 끝점에서 표면 경계까지 연속 연결을 검증하지 못했습니다",
                    -5 => "외부 연결 경로 구간이 금형을 통과하거나 재진입합니다", _ => "OCCT 연결 경로 생성 실패" };
                StatusText.Text = reason + " · " + ConnectionPointPrompt(); return;
            }
            if (previewOnly) return; // Hover must never consume endpoints or update lengths.
            _pendingConnectionPoints.RemoveAt(0);
            _connectionLengths[pending.CircuitIndex] += result[1];
            var circuits = CircuitPathPartitioner.Compute(_pattern.PathSegments, _turnSegmentEnabled);
            UpdateCircuitTable(circuits);
            if (_pendingConnectionPoints.Count == 0) { Viewport.ConnectionPointSelectionMode = false; SetConnectionPointEditingLock(false); }
            StatusText.Text = _pendingConnectionPoints.Count == 0 ? "연결 경로 지정 완료 — 실제 3D 연결 길이를 회로별 합계에 반영했습니다" : ConnectionPointPrompt();
        }
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
            }
        }

        private void StandardView_Click(object sender, RoutedEventArgs e)
        {
            Viewport.CancelConnectionPathPreview();
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

        private void Fit_Click(object sender, RoutedEventArgs e) { Viewport.CancelConnectionPathPreview(); Bridge.FitView(); }

        private void ZoomIn_Click(object sender, RoutedEventArgs e) { Viewport.CancelConnectionPathPreview(); Bridge.ZoomView(1.25); }

        private void ZoomOut_Click(object sender, RoutedEventArgs e) { Viewport.CancelConnectionPathPreview(); Bridge.ZoomView(0.8); }

    }
}
