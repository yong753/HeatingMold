using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using HeatingMold.App.Deployment;
using HeatingMold.App.VisualTesting;

namespace HeatingMold.App
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // 실행 중 발생하는 모든 오류를 복사 가능한 메시지 창으로 표시한다.
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            // 시각 테스트 옵션은 StartupUri가 MainWindow를 만들기 전에 확정한다.
            // 잘못된 옵션/시나리오는 일반 앱으로 폴백하지 않고 실패 결과만 남긴 뒤 종료한다.
            VisualTestOptions visualOptions = null;
            try
            {
                visualOptions = VisualTestOptions.Parse(e.Args);
                if (visualOptions.Enabled)
                {
                    if (!string.IsNullOrEmpty(visualOptions.Error))
                    {
                        VisualTestReport.WriteStartupFailure(visualOptions.RunDirectory, visualOptions.Error);
                        Shutdown(2);
                        Environment.Exit(2);
                        return;
                    }

                    VisualTestSession session = VisualTestSession.Prepare(visualOptions);
                    // Bridge(OCCT) 호출 전에 third_party DLL 경로를 PATH에 추가한다.
                    OcctEnv.Setup();
                    // Setup이 기본 로그 경로를 설정하므로 viewer 초기화 전에 실행 폴더로 덮어쓴다.
                    Environment.SetEnvironmentVariable("HEATINGMOLD_OCC_LOG",
                        Path.Combine(visualOptions.RunDirectory, "occ_viewer_log.txt"));
                    session.Report.WriteRun();
                    VisualTestSession.Current = session;
                }
                else
                {
                    OcctEnv.Setup();
                }
            }
            catch (Exception ex)
            {
                // 배포 패키지에서 실패하면 창이 없으면 사용자는 아무 안내도 못 받는다.
                try { RuntimeDiagnostics.WriteStartupFailure("앱 시작", ex); } catch (Exception) { }

                if (visualOptions != null && visualOptions.Enabled)
                {
                    VisualTestReport.WriteStartupFailure(visualOptions.RunDirectory,
                        "시각 테스트 시작 실패: " + ex);
                }
                else
                {
                    MessageBox.Show(
                        ex.Message + Environment.NewLine + Environment.NewLine +
                        RuntimeDiagnostics.BuildLogHintText(),
                        "HeatingMold 시작 실패", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                Shutdown(2);
                Environment.Exit(2);
                return;
            }

            base.OnStartup(e);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            // 창을 읽고 복사할 수 있도록 앱은 계속 실행한다. (Handled=false면 창이 뜨자마자 프로세스가 종료된다)
            ErrorReporter.ShowError("처리되지 않은 오류가 발생했습니다 (UI 스레드)", e.Exception);
            e.Handled = true;

            // 주 창이 뜨기 전(초기화 단계)에 실패했다면 보이지 않는 프로세스가 남지 않도록 종료한다.
            if (MainWindow == null || !MainWindow.IsLoaded) Shutdown(1);
        }

        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception
                     ?? new Exception(e.ExceptionObject == null ? "알 수 없는 오류" : e.ExceptionObject.ToString());
            ErrorReporter.ShowError(
                e.IsTerminating ? "치명적 오류 — 프로그램을 종료합니다" : "처리되지 않은 오류 (백그라운드 스레드)",
                ex);
        }

        private void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            ErrorReporter.ShowError("처리되지 않은 백그라운드 작업 오류", e.Exception);
            e.SetObserved();
        }
    }
}
