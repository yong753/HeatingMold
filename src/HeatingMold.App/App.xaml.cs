using System.Windows;

namespace HeatingMold.App
{
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Bridge(OCCT) 호출 전에 third_party DLL 경로를 PATH에 추가 (창 생성 전)
            OcctEnv.Setup();
            base.OnStartup(e);

            MainWindow mainWindow = AppCompositionRoot.CreateMainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
        }

        /// <summary>Application 종료 시 Native 문서와 Viewer 자원을 해제합니다.</summary>
        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                AppCompositionRoot.Shutdown();
            }
            finally
            {
                base.OnExit(e);
            }
        }
    }
}
