using System.Windows;
using HeatingMold.App;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Tests;

/// <summary>Application 종료 시 Native 자원 해제 순서를 확인합니다.</summary>
[Collection("WPF Bridge")]
public sealed class AppExitTests
{
    /// <summary>WPF 종료 경로가 CompositionRoot를 통해 Native 정리를 한 번 호출합니다.</summary>
    [Fact]
    public void OnExit_ShutsDownNativeDocumentAndViewer()
    {
        WpfTestThread.Run(() =>
        {
            Bridge.Reset();
            var app = new TestableApp();

            app.InvokeOnExit();

            Assert.Equal(1, Bridge.ShutdownCount);
        });
    }

    private sealed class TestableApp : App
    {
        public void InvokeOnExit() => base.OnExit(null);
    }
}
