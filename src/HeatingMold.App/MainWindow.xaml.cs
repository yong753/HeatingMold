using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Export;
using HeatingMold.OccBridge;
using Microsoft.Win32;

namespace HeatingMold.App
{
    public partial class MainWindow : Window
    {
        // 마지막 패턴 상태 (DXF 내보내기용). 좌표 = "투영 2D" (면 중심 법선 정사영).
        private SerpentineResult _pattern;
        private int _patternFaceIndex = -1;
        private List<List<Vec2>> _outlineRings;
        private List<Vec2> _heatPath2D;
        private List<Vec3> _heatPath3D;   // pattern centerline on the surface (world 3D)
        private List<Vec3> _heatBand3D;   // pattern ribbon band on the surface (world 3D)

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
                StatusText.Text = "로드 실패: " + path;
                return;
            }
            sw.Stop();

            Bridge.SetShapeColor(0.55, 0.65, 0.85);   // 고정 표시색 (스틸 블루)
            _pattern = null;
            _patternFaceIndex = -1;

            var info = Bridge.GetShapeInfo();
            PopulateFaceCombo(info.Faces);
            StatusText.Text = string.Format("{0} — 솔리드 {1} · 셸 {2} · 면 {3} · 평면 {4} · 로드 {5}ms",
                Path.GetFileName(path), info.Solids, info.Shells, info.Faces, info.Planes, sw.ElapsedMilliseconds);
        }

        private void PopulateFaceCombo(int faceCount)
        {
            FaceCombo.Items.Clear();
            var candidates = new List<Tuple<int, string, double>>();
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

                double[] nrm = Bridge.GetFaceNormal(i);
                if (nrm == null) continue;

                var origin = new Vec3(pts.Average(v => v.X), pts.Average(v => v.Y), pts.Average(v => v.Z));
                var normal = new Vec3(nrm[0], nrm[1], nrm[2]);
                Vec3 farthest = pts.OrderByDescending(v => (v - origin).Length).First();
                var frame = new ProjectionFrame(origin, normal, farthest - origin);

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

        /// <summary>UV 경계 점들을 면 위 3D 점으로 샘플링.</summary>
        private List<Vec3> SampleWire3D(int faceIndex, UvWire w)
        {
            var list = new List<Vec3>(w.Points.Length / 2);
            for (int k = 0; k + 1 < w.Points.Length; k += 2)
            {
                var p = Bridge.SampleUvTo3D(faceIndex, w.Points[k], w.Points[k + 1]);
                if (p != null) list.Add(new Vec3(p[0], p[1], p[2]));
            }
            return list;
        }

        /// <summary>UV 격자를 투영 2D로 사영해 둔 역매핑 테이블 (투영 2D → UV).</summary>
        private UvProjectionGrid BuildUvGrid(int faceIndex, UvWire[] wires, ProjectionFrame frame)
        {
            double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
            foreach (var w in wires)
                for (int k = 0; k + 1 < w.Points.Length; k += 2)
                {
                    double u = w.Points[k], v = w.Points[k + 1];
                    if (u < minU) minU = u;
                    if (u > maxU) maxU = u;
                    if (v < minV) minV = v;
                    if (v > maxV) maxV = v;
                }

            const int n = 32;
            double du = (maxU - minU) / n, dv = (maxV - minV) / n;
            if (!(du > 0)) du = 1;
            if (!(dv > 0)) dv = 1;

            int count = (n + 1) * (n + 1);
            var us = new double[count];
            var vs = new double[count];
            var xs = new double[count];
            var ys = new double[count];
            for (int i = 0; i <= n; i++)
                for (int j = 0; j <= n; j++)
                {
                    int idx = i * (n + 1) + j;
                    double u = minU + i * du, v = minV + j * dv;
                    us[idx] = u; vs[idx] = v;
                    var p = Bridge.SampleUvTo3D(faceIndex, u, v);
                    if (p == null) { xs[idx] = double.NaN; ys[idx] = double.NaN; }
                    else { Vec2 q = frame.Project(new Vec3(p[0], p[1], p[2])); xs[idx] = q.X; ys[idx] = q.Y; }
                }
            return new UvProjectionGrid(n, us, vs, xs, ys);
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
            if (!(FaceCombo.SelectedItem is FaceItem fi))
            {
                StatusText.Text = "면을 선택하세요";
                return;
            }

            string type = Bridge.GetFaceType(fi.Index);

            var wires = Bridge.GetFaceUvBoundary(fi.Index);
            if (wires == null || wires.Length == 0)
            {
                StatusText.Text = "면 경계 추출 실패 — #" + fi.Index;
                return;
            }

            double[] nrm = Bridge.GetFaceNormal(fi.Index);
            if (nrm == null)
            {
                StatusText.Text = "면 법선 계산 실패 — #" + fi.Index;
                return;
            }

            // 1) 경계 UV → 3D 샘플
            var wire3D = new List<List<Vec3>>(wires.Length);
            foreach (var w in wires) wire3D.Add(SampleWire3D(fi.Index, w));
            var allPts = wire3D.Where(l => l.Count > 0).SelectMany(l => l).ToList();
            if (allPts.Count < 3)
            {
                StatusText.Text = "경계 3D 샘플 실패 — #" + fi.Index;
                return;
            }

            // 2) 투영 프레임 — 원점 = 경계 3D 중심, 방향 = 면 중심 법선 (정사영)
            var origin = new Vec3(allPts.Average(p => p.X), allPts.Average(p => p.Y), allPts.Average(p => p.Z));
            var normal = new Vec3(nrm[0], nrm[1], nrm[2]);
            Vec3 farthest = allPts.OrderByDescending(p => (p - origin).Length).First();
            var frame = new ProjectionFrame(origin, normal, farthest - origin);

            // 3) 경계 → 투영 2D
            var planar = wire3D.Select(l => l.Select(frame.Project).ToList()).ToList();
            int outerIdx = 0;
            for (int i = 0; i < wires.Length; i++) { if (wires[i].IsOuter) { outerIdx = i; break; } }
            List<Vec2> outerRing = planar[outerIdx];
            if (outerRing.Count < 3)
            {
                StatusText.Text = "외곽 투영 실패 — #" + fi.Index;
                return;
            }
            var holes = new List<IReadOnlyList<Vec2>>();
            for (int i = 0; i < planar.Count; i++)
            {
                if (i == outerIdx) continue;
                if (planar[i].Count >= 3) holes.Add(planar[i]);
            }

            // 4) 투영 2D → UV 역매핑 격자
            UvProjectionGrid grid = BuildUvGrid(fi.Index, wires, frame);

            TryGetParams(out double margin, out double spacing, out double width);

            var sw = Stopwatch.StartNew();
            SerpentineResult result = Serpentine.Generate(outerRing, holes,
                new PatternParams { Margin = margin, Spacing = spacing });
            sw.Stop();

            if (result.Segments.Count == 0)
            {
                StatusText.Text = string.Format("사행 생성 결과 없음 — 마진 {0}·간격 {1} 확인", margin, spacing);
                return;
            }

            // 5) 리본 밴드(투영 2D) → 역매핑(UV) → 면 위 3D 오버레이 (시점 유지)
            List<List<Vec2>> ribbon = RegionOps.StrokePath(result.Path, width);
            if (ribbon.Count > 0)
            {
                List<Vec2> band = ribbon.OrderByDescending(r => Math.Abs(RegionOps.SignedArea(r))).First();
                var uvFlat = new double[band.Count * 2];
                var band3D = new List<Vec3>(band.Count);
                bool mapped = true;
                for (int i = 0; i < band.Count; i++)
                {
                    if (!grid.TryMap(band[i], out double u, out double v)) { mapped = false; break; }
                    uvFlat[i * 2] = u;
                    uvFlat[i * 2 + 1] = v;
                    var q3 = Bridge.SampleUvTo3D(fi.Index, u, v);
                    if (q3 != null) band3D.Add(new Vec3(q3[0], q3[1], q3[2]));
                }
                _heatBand3D = mapped ? band3D : null;
                // 다른 면의 오버레이는 브리지가 유지하므로 여기서 지우지 않는다.
                if (mapped) Bridge.BuildWire3D(fi.Index, uvFlat, true);
            }

            _pattern = result;
            _patternFaceIndex = fi.Index;
            _outlineRings = planar;
            _heatPath2D = result.Path.ToList();
            _heatPath3D = MapPath3D(fi.Index, grid, result.Path);

            StatusText.Text = string.Format(
                "패턴 — 면 #{0} ({1}) · 레인 {2} · 세그먼트 {3} · 총길이 {4:F1}mm · {5}ms",
                fi.Index, type, result.LaneCount, result.Segments.Count, result.TotalLength, sw.ElapsedMilliseconds);
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

        private void OnFacePicked(int faceIndex)
        {
            if (faceIndex < 0)
            {
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
            FaceCombo.SelectedItem = match;
            StatusText.Text = string.Format("면 선택 — #{0} ({1})", faceIndex, type);
        }

        private List<DxfView> BuildViews()
        {
            var views = new List<DxfView>();
            double[] n = Bridge.GetFaceNormal(_patternFaceIndex);
            if (n != null)
            {
                var N = new Vec3(n[0], n[1], n[2]).Normalized();
                Vec3 x = Math.Abs(N.Z) < 0.9 ? new Vec3(0, 0, 1) : new Vec3(1, 0, 0);
                x = (x - N * Vec3.Dot(x, N)).Normalized();
                views.Add(BuildHlrView("front", N.X, N.Y, N.Z, x.X, x.Y, x.Z));
            }
            views.Add(BuildHlrView("top", 0, 0, -1, 1, 0, 0));
            views.Add(BuildHlrView("side", 0, 1, 0, 1, 0, 0));
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

        private List<Vec3> MapPath3D(int faceIndex, UvProjectionGrid grid, IReadOnlyList<Vec2> path2D)
        {
            var list = new List<Vec3>(path2D.Count);
            foreach (var p in path2D)
            {
                if (!grid.TryMap(p, out double u, out double v)) return null;
                var q = Bridge.SampleUvTo3D(faceIndex, u, v);
                if (q == null) return null;
                list.Add(new Vec3(q[0], q[1], q[2]));
            }
            return list;
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            if (_pattern == null)
            {
                StatusText.Text = "먼저 [패턴 생성]을 실행하세요";
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

        private void FrontView_Click(object sender, RoutedEventArgs e)
        {
            if (!(FaceCombo.SelectedItem is FaceItem fi))
            {
                StatusText.Text = "면을 선택하세요";
                return;
            }
            double[] n = Bridge.GetFaceNormal(fi.Index);
            if (n == null)
            {
                StatusText.Text = "법선 계산 실패 — #" + fi.Index;
                return;
            }
            Bridge.SetViewDirection(n[0], n[1], n[2]);
            StatusText.Text = string.Format("정면 뷰 — 면 #{0} 법선 ({1:F2}, {2:F2}, {3:F2})",
                fi.Index, n[0], n[1], n[2]);
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
