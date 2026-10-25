using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

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

            // Bridge(OCCT) 호출 전에 third_party DLL 경로를 PATH에 추가 (창 생성 전)
            OcctEnv.Setup();
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
