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
        private bool _colorBlue = true;

        // S3 — 마지막 패턴 상태 (DXF 내보내기용)
        private SerpentineResult _pattern;
        private int _patternFaceIndex = -1;
        private List<List<Vec2>> _outlineRings;
        private List<Vec2> _heatPathUv;

        private sealed class FaceItem
        {
            public int Index;
            public string Label;
            public override string ToString() => Label;
        }

        public MainWindow()
        {
            InitializeComponent();
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

            Bridge.SetShapeColor(0.55, 0.65, 0.85);
            _colorBlue = true;
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
                if (Bridge.GetFaceType(i) != "Plane") continue;
                var wires = Bridge.GetFaceUvBoundary(i);
                if (wires == null || wires.Length == 0) continue;

                double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
                foreach (var w in wires)
                {
                    for (int k = 0; k + 1 < w.Points.Length; k += 2)
                    {
                        double u = w.Points[k], v = w.Points[k + 1];
                        if (u < minU) minU = u;
                        if (u > maxU) maxU = u;
                        if (v < minV) minV = v;
                        if (v > maxV) maxV = v;
                    }
                }
                double wd = maxU - minU, ht = maxV - minV;
                if (wd < 1 || ht < 1) continue;
                candidates.Add(Tuple.Create(i, string.Format("#{0} 평면 {1:F0}×{2:F0}", i, wd, ht), wd * ht));
            }
            candidates.Sort((a, b) => b.Item3.CompareTo(a.Item3));   // 큰 면 우선
            foreach (var c in candidates) FaceCombo.Items.Add(new FaceItem { Index = c.Item1, Label = c.Item2 });
            if (FaceCombo.Items.Count > 0) FaceCombo.SelectedIndex = 0;
        }

        private static List<Vec2> ToVec2List(double[] flat)
        {
            var list = new List<Vec2>(flat.Length / 2);
            for (int i = 0; i + 1 < flat.Length; i += 2) list.Add(new Vec2(flat[i], flat[i + 1]));
            return list;
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
                StatusText.Text = "면을 선택하세요 (평면만 목록에 표시)";
                return;
            }

            var wires = Bridge.GetFaceUvBoundary(fi.Index);
            if (wires == null || wires.Length == 0)
            {
                StatusText.Text = "면 경계 추출 실패 — #" + fi.Index;
                return;
            }

            UvWire outer = wires.FirstOrDefault(w => w.IsOuter) ?? wires[0];
            List<Vec2> outerRing = ToVec2List(outer.Points);
            var holes = wires.Where(w => !ReferenceEquals(w, outer))
                             .Select(w => (IReadOnlyList<Vec2>)ToVec2List(w.Points))
                             .Where(h => h.Count >= 3)
                             .ToList();

            TryGetParams(out double margin, out double spacing, out double width);

            var sw = Stopwatch.StartNew();
            SerpentineResult result = Serpentine.Generate(outerRing, holes,
                new PatternParams { Margin = margin, Spacing = spacing, RibbonWidth = width });
            sw.Stop();

            if (result.Segments.Count == 0)
            {
                StatusText.Text = string.Format("사행 생성 결과 없음 — 마진 {0}·간격 {1} 확인", margin, spacing);
                return;
            }

            // 리본 밴드 외곽선(선폭) → 3D 오버레이 + 정면 시점
            List<List<Vec2>> ribbon = RegionOps.StrokePath(result.Path, width);
            if (ribbon.Count > 0)
            {
                List<Vec2> band = ribbon.OrderByDescending(r => Math.Abs(RegionOps.SignedArea(r))).First();
                var flat = new double[band.Count * 2];
                for (int i = 0; i < band.Count; i++) { flat[i * 2] = band[i].X; flat[i * 2 + 1] = band[i].Y; }
                Bridge.BuildWire3D(fi.Index, flat, true);
            }
            double[] n = Bridge.GetFaceNormal(fi.Index);
            if (n != null) Bridge.SetViewDirection(n[0], n[1], n[2]);

            _pattern = result;
            _patternFaceIndex = fi.Index;
            _outlineRings = wires.Select(w => ToVec2List(w.Points)).ToList();
            _heatPathUv = result.Path.ToList();

            StatusText.Text = string.Format(
                "패턴 — 면 #{0} · 레인 {1} · 세그먼트 {2} · 총길이 {3:F1}mm · {4}ms",
                fi.Index, result.LaneCount, result.Segments.Count, result.TotalLength, sw.ElapsedMilliseconds);
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
                DxfExporter.Export(file, _outlineRings, _heatPathUv, width);
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

        private void Color_Click(object sender, RoutedEventArgs e)
        {
            _colorBlue = !_colorBlue;
            if (_colorBlue) Bridge.SetShapeColor(0.55, 0.65, 0.85);
            else Bridge.SetShapeColor(0.90, 0.45, 0.20);
            StatusText.Text = _colorBlue ? "색상: 스틸 블루" : "색상: 오렌지";
        }

        private void Hlr_Click(object sender, RoutedEventArgs e)
        {
            var hlr = Bridge.HlrProject();
            StatusText.Text = string.Format("HLR — 가시 {0} · 은선 {1} · {2:F0}ms",
                hlr.VisibleEdges, hlr.HiddenEdges, hlr.Milliseconds);
        }
    }
}
