using System.Diagnostics;
using System.IO;
using System.Windows;
using HeatingMold.OccBridge;
using Microsoft.Win32;

namespace HeatingMold.App
{
    public partial class MainWindow : Window
    {
        private bool _colorBlue = true;

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

            var info = Bridge.GetShapeInfo();
            StatusText.Text = string.Format("{0} — 솔리드 {1} · 셸 {2} · 면 {3} · 평면 {4} · 로드 {5}ms",
                Path.GetFileName(path), info.Solids, info.Shells, info.Faces, info.Planes, sw.ElapsedMilliseconds);
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
