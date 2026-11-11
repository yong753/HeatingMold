using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using HeatingMold.App.VisualTesting;
using Microsoft.Win32;

namespace HeatingMold.App
{
    /// <summary>
    /// 일반 앱 창에서 시각 테스트를 새 프로세스로 시작하는 실행 UI.
    /// 현재 창의 모델/편집 상태를 건드리지 않고, 테스트 앱은 시작 인자를 통해
    /// 별도 run 디렉터리와 로그를 준비한다.
    /// </summary>
    public partial class VisualTestLauncherWindow : Window
    {
        private readonly string _repositoryRoot;

        public string LaunchedRunDirectory { get; private set; }

        public VisualTestLauncherWindow()
        {
            InitializeComponent();

            _repositoryRoot = VisualTestOptions.FindRepositoryRoot() ?? AppContext.BaseDirectory;
            // 저장소에는 시나리오 파일이 포함되지 않는다. 사용자가 직접 선택한 JSON만 실행한다.
            ScenarioPathBox.Text = "";
            RunDirectoryBox.Text = Path.Combine(_repositoryRoot, "artifacts", "visual-test",
                "ui-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
            HoldSecondsBox.Text = "3";
            StatusText.Text = "";
        }

        private void BrowseScenario_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "시각 테스트 시나리오 선택",
                Filter = "시나리오 JSON (*.json)|*.json|모든 파일 (*.*)|*.*",
                CheckFileExists = true,
                InitialDirectory = _repositoryRoot
            };
            if (!string.IsNullOrWhiteSpace(ScenarioPathBox.Text))
            {
                try
                {
                    dialog.InitialDirectory = Path.GetDirectoryName(ScenarioPathBox.Text.Trim());
                }
                catch (ArgumentException)
                {
                    // 잘못 입력된 경로는 무시하고 기본 위치에서 연다.
                }
            }
            if (dialog.ShowDialog(this) == true)
                ScenarioPathBox.Text = dialog.FileName;
        }

        private void BrowseRunDirectory_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "시각 테스트 결과 폴더 선택",
                InitialDirectory = _repositoryRoot
            };
            if (!string.IsNullOrWhiteSpace(RunDirectoryBox.Text))
            {
                try
                {
                    string current = Path.GetFullPath(RunDirectoryBox.Text.Trim());
                    dialog.InitialDirectory = Directory.Exists(current)
                        ? current
                        : Path.GetDirectoryName(current) ?? _repositoryRoot;
                }
                catch (ArgumentException)
                {
                    // 잘못 입력된 경로는 무시하고 저장소 루트에서 연다.
                }
            }
            if (dialog.ShowDialog(this) == true)
                RunDirectoryBox.Text = dialog.FolderName;
        }

        private void Run_Click(object sender, RoutedEventArgs e)
        {
            if (VisualTestSession.Current != null)
            {
                ShowError("이 창은 이미 시각 테스트를 실행 중입니다.");
                return;
            }

            string scenario = (ScenarioPathBox.Text ?? "").Trim().Trim('"');
            if (scenario.Length == 0)
            {
                ShowError("시나리오 JSON 파일을 선택하세요.");
                return;
            }
            try
            {
                scenario = Path.GetFullPath(scenario);
            }
            catch (Exception ex)
            {
                ShowError("시나리오 경로가 올바르지 않습니다: " + ex.Message);
                return;
            }
            if (!File.Exists(scenario))
            {
                ShowError("시나리오 JSON 파일을 찾을 수 없습니다.");
                return;
            }

            string runDirectory = (RunDirectoryBox.Text ?? "").Trim().Trim('"');
            if (!Path.IsPathRooted(runDirectory))
            {
                ShowError("결과 폴더는 절대 경로여야 합니다.");
                return;
            }
            try
            {
                runDirectory = Path.GetFullPath(runDirectory);
            }
            catch (Exception ex)
            {
                ShowError("결과 폴더 경로가 올바르지 않습니다: " + ex.Message);
                return;
            }
            if (File.Exists(Path.Combine(runDirectory, "result.json"))
                || File.Exists(Path.Combine(runDirectory, "result.dxf")))
            {
                ShowError("이미 결과 파일이 있는 폴더입니다. 다른 결과 폴더를 선택하세요.");
                return;
            }

            if (!double.TryParse(HoldSecondsBox.Text, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double holdSeconds)
                || !double.IsFinite(holdSeconds) || holdSeconds < 0 || holdSeconds > 60)
            {
                ShowError("관찰 시간은 0~60 사이의 숫자여야 합니다.");
                return;
            }

            string exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                exe = Path.Combine(AppContext.BaseDirectory, "HeatingMold.App.exe");
            if (!File.Exists(exe))
            {
                ShowError("HeatingMold.App.exe를 찾을 수 없습니다.");
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = _repositoryRoot,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("--visual-test");
            startInfo.ArgumentList.Add(scenario);
            startInfo.ArgumentList.Add("--visual-run-dir");
            startInfo.ArgumentList.Add(runDirectory);
            startInfo.ArgumentList.Add("--visual-hold-seconds");
            startInfo.ArgumentList.Add(holdSeconds.ToString("R", CultureInfo.InvariantCulture));
            if (StepModeCheckBox.IsChecked == true)
                startInfo.ArgumentList.Add("--visual-step");

            try
            {
                Process.Start(startInfo);
                LaunchedRunDirectory = runDirectory;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                ShowError("시각 테스트를 시작하지 못했습니다: " + ex.Message);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void ShowError(string message)
        {
            StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            StatusText.Text = message;
        }
    }
}
