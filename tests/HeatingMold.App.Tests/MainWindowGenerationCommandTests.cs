using System;
using System.Reflection;
using System.Windows;
using HeatingMold.App;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Tests;

/// <summary>화면 경로 생성 명령이 확정 작업 좌표계와 Application 흐름을 요구하는지 검사합니다.</summary>
[Collection("WPF Bridge")]
public sealed class MainWindowGenerationCommandTests
{
    /// <summary>작업 좌표계가 없으면 Native 경로를 변경하지 않고 생성을 중단합니다.</summary>
    [Fact]
    public void GenerateClick_WithoutWorkingCoordinateSystem_PreservesNativePathState()
    {
        WpfTestThread.Run(() =>
        {
            Bridge.Reset();
            var window = new MainWindow();
            try
            {
                MethodInfo handler = typeof(MainWindow).GetMethod(
                    "Generate_Click",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("경로 생성 UI 처리기를 찾지 못했습니다.");

                handler.Invoke(window, [window, new RoutedEventArgs()]);

                Assert.Equal("유효한 작업 좌표계가 없습니다", window.StatusText.Text);
                Assert.Equal(0, Bridge.ProjectionCommitCount);
                Assert.Equal(0, Bridge.ClearConnectionPathsCount);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
