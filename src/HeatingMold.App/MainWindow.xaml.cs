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
        // 마지막 패턴 상태 (legacy DXF export); current generation uses the fixed whole-model +Z frame.
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
        private int _hoverConnector = -1;

        private void ResetCircuitEditState(bool clearNative)
        {
            Viewport.ConnectorEditMode = false;
            if (ConnectorEditModeBox != null) ConnectorEditModeBox.IsChecked = false;
            _turnEnabled.Clear();
            _connectorUndo.Clear();
            _connectorRedo.Clear();
            _routePieceLengths = Array.Empty<double>();
            _hoverConnector = -1;
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
            Viewport.ConnectorHovered += OnConnectorHovered;
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
            _exclusions.Add(rect);
            Bridge.AddExclusionOverlay(ExclusionRectangleWire(a, b));
            DrawRegionCanvas();
            StatusText.Text = string.Format("모델 뷰포트 제외영역 추가 — {0}개", _exclusions.Count);
        }

        private void OnFacePicked(int faceIndex, bool toggle)
        {
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

            // The projection frame is fixed to the loaded model's +Z view axes,
            // never derived from a selected Face normal.
            double[] xAxis = Bridge.GetAutoAxis(0);
            double[] yAxis = Bridge.GetAutoAxis(1);
            double[] zAxis = Bridge.GetAutoAxis(2);
            if (xAxis == null || yAxis == null || zAxis == null ||
                xAxis.Length < 3 || yAxis.Length < 3 || zAxis.Length < 3)
            {
                _multiFaceRegionFailure = "전체 모델 기준 투영축(+Z)을 가져오지 못했습니다";
                StatusText.Text = _multiFaceRegionFailure;
                return;
            }
            double[] bounds = Bridge.GetModelBoundsInFrame(
                xAxis[0], xAxis[1], xAxis[2], yAxis[0], yAxis[1], yAxis[2],
                zAxis[0], zAxis[1], zAxis[2]);
            if (bounds == null || bounds.Length < 6)
            {
                _multiFaceRegionFailure = "전체 모델의 고정축 경계를 계산하지 못했습니다";
                StatusText.Text = _multiFaceRegionFailure;
                return;
            }

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

            Vec3 axisX = new Vec3(xAxis[0], xAxis[1], xAxis[2]).Normalized();
            Vec3 axisZ = new Vec3(zAxis[0], zAxis[1], zAxis[2]).Normalized();
            double centerX = (bounds[0] + bounds[1]) * 0.5;
            double centerY = (bounds[2] + bounds[3]) * 0.5;
            // The datum plane is just outside the full-model +Z extent; positive
            // insertion depth moves its ray origin toward -Z from this fixed plane.
            Vec3 origin = axisX * centerX + new Vec3(yAxis[0], yAxis[1], yAxis[2]).Normalized() * centerY
                + axisZ * (bounds[5] + 0.1);
            _multiFaceFrame = new ProjectionFrame(origin, axisZ, axisX);
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
            StatusText.Text = string.Format("전체 모델 +Z 기준 통합 투영영역 — {0}개 Face · 공유 내부 Edge 제거 · Sewing ≤0.01mm · 경계 오차 0.05mm",
                _selectedFaceIds.Count);
        }

        private void GenerateMultiFace()
        {
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
            double total = 0;
            int projectedCount = 0;
            int[] selectedFaces = _selectedFaceIds.OrderBy(id => id).ToArray();
            Vec3 projectDirection = _multiFaceFrame.Normal * -1.0;
            var newLengths = new List<double>();
            bool routePiecesMode = editablePattern != null && _exclusions.Count == 0 && editablePattern.RoutePieces.Count > 0;
            // A newly calculated result replaces the prior result, including when
            // the new settings produce no route or use exclusion components.
            ResetCircuitEditState(clearNative: false);
            _pattern = null;
            _multiFacePatternDisplayed = false;
            if (routePiecesMode)
            {
                Bridge.ClearOverlays();
                int id = 0;
                foreach (var piece in editablePattern.RoutePieces)
                {
                    double[] xyz = piece.Points.SelectMany(point => { Vec3 p = _multiFaceFrame.Unproject(point); return new[] { p.X, p.Y, p.Z }; }).ToArray();
                    var result = Bridge.ProjectRoutePiece(id, piece.Kind.ToString(), xyz, selectedFaces,
                        projectDirection.X, projectDirection.Y, projectDirection.Z, insertionDepth);
                    if (result == null)
                    {
                        Bridge.ClearRoutePieces();
                        _pattern = null;
                        _turnEnabled.Clear();
                        _routePieceLengths = Array.Empty<double>();
                        _multiFacePatternDisplayed = false;
                        StatusText.Text = "경로 구간 투영 실패 — 불완전한 결과를 제거했습니다";
                        return;
                    }
                    newLengths.Add(result.Length); total += result.Length; id++;
                }
                _turnEnabled.Clear();
                _turnEnabled.AddRange(editablePattern.RoutePieces.Where(p => p.Kind == SerpentineRoutePieceKind.TurnConnector).Select(_ => true));
                _pattern = editablePattern;
                projectedCount = editablePattern.RoutePieces.Count;
            }
            else Bridge.ClearOverlays();
            foreach (IReadOnlyList<Vec2> path in paths)
            {
                if (routePiecesMode) break;
                if (path.Count < 2) continue;
                // Keep the source as one OCCT wire on the fixed model datum;
                // OCCT projects its edges and handles display tessellation.
                double[] sourceXyz = path.SelectMany(point =>
                {
                    Vec3 p = _multiFaceFrame.Unproject(point);
                    return new[] { p.X, p.Y, p.Z };
                }).ToArray();
                double[] projection = Bridge.ProjectPathWireOntoSelectedFaces(
                    selectedFaces, sourceXyz, projectDirection.X, projectDirection.Y, projectDirection.Z,
                    insertionDepth);
                if (projection == null || projection.Length < 5 || projection[0] < 0.5)
                {
                    Bridge.ClearOverlays();
                    string diagnostic = projection == null || projection.Length < 5
                        ? "native projection error"
                        : string.Format("code {0:F0}, result wires {1:F0}", projection[4], projection[3]);
                    StatusText.Text = "OCCT 투영/연결 검증 실패(" + diagnostic + ") — 부분 결과를 제거했습니다";
                    return;
                }
                total += projection[1];
                projectedCount++;
            }
            Bridge.RedrawView();
            if (routePiecesMode)
            {
                _routePieceLengths = newLengths.ToArray();
                _connectorUndo.Clear(); _connectorRedo.Clear();
                ApplyConnectorStates();
            }
            StatusText.Text = projectedCount == 0
                ? "생성 가능한 경로가 없습니다. 마진/간격을 확인하세요"
                : string.Format(_exclusions.Count == 0
                    ? "전체 모델 +Z 기준 OCCT 연속 투영 — {0}개 Wire · 길이 {1:F1}mm · 삽입 깊이 {2:F2}mm"
                    : "제외영역 적용 — {0}개 독립 OCCT Wire(제외영역 건너 연결 없음) · 길이 {1:F1}mm · 삽입 깊이 {2:F2}mm",
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
            var turns = _pattern.RoutePieces.Select((p, i) => (p, i)).Where(x => x.p.Kind == SerpentineRoutePieceKind.TurnConnector).ToList();
            for (int i = 0; i < turns.Count; i++) Bridge.SetConnectorEnabled(turns[i].i, _turnEnabled[i], 0.2, 0.8, 0.3);
            var circuits = SerpentineTopology.Compute(_pattern.RoutePieces, _turnEnabled);
            double length = _pattern.RoutePieces.Select((p, i) => (p, i)).Where(x => x.p.Kind != SerpentineRoutePieceKind.TurnConnector || _turnEnabled[turns.FindIndex(t => t.i == x.i)]).Sum(x => _routePieceLengths.ElementAtOrDefault(x.i));
            StatusText.Text = string.Format("회로 {0}개 · 길이 합계 {1:F1}mm", circuits.Count, length);
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

        private void Fit_Click(object sender, RoutedEventArgs e) => Bridge.FitView();

        private void ZoomIn_Click(object sender, RoutedEventArgs e) => Bridge.ZoomView(1.25);

        private void ZoomOut_Click(object sender, RoutedEventArgs e) => Bridge.ZoomView(0.8);

        private void Hlr_Click(object sender, RoutedEventArgs e)
        {
            var hlr = Bridge.HlrProject();
            StatusText.Text = string.Format("HLR — 가시 {0} · 은선 {1} · {2:F0}ms",
                hlr.VisibleEdges, hlr.HiddenEdges, hlr.Milliseconds);
        }
    }
}
