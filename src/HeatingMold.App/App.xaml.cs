using System.Windows;

namespace HeatingMold.App
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Bridge(OCCT) 호출 전에 third_party DLL 경로를 PATH에 추가 (창 생성 전)
            OcctEnv.Setup();
            base.OnStartup(e);
        }
    }
}
