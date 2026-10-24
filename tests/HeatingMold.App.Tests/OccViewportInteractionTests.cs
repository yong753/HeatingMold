using HeatingMold.App;

namespace HeatingMold.App.Tests;

/// <summary>뷰포트 입력 모드가 상호 배타적으로 동작하는지 확인합니다.</summary>
[Collection("WPF Bridge")]
public sealed class OccViewportInteractionTests
{
    /// <summary>표면 앵커와 제어점 편집 모드를 동시에 활성화하지 않습니다.</summary>
    [Fact]
    public void SurfaceAnchorAndControlPointEditModes_AreMutuallyExclusive()
    {
        WpfTestThread.Run(() =>
        {
            var viewport = new OccViewport();

            viewport.SurfaceAnchorSelectionMode = true;
            Assert.True(viewport.SurfaceAnchorSelectionMode);
            Assert.False(viewport.ControlPointEditMode);

            viewport.ControlPointEditMode = true;
            Assert.True(viewport.ControlPointEditMode);
            Assert.False(viewport.SurfaceAnchorSelectionMode);

            viewport.SurfaceAnchorSelectionMode = true;
            Assert.True(viewport.SurfaceAnchorSelectionMode);
            Assert.False(viewport.ControlPointEditMode);
        });
    }
}
