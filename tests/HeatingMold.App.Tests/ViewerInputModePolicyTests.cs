using System;
using System.Linq;
using System.Reflection;
using HeatingMold.App;
using HeatingMold.App.ViewModels;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Tests;

public sealed class ViewerInputModePolicyTests
{
    [Theory]
    [InlineData((int)ViewerInputMode.Navigate, 0, 0)]
    [InlineData((int)ViewerInputMode.FaceRectangleSelection, 1, 1)]
    [InlineData((int)ViewerInputMode.RegionInclusionSketch, 1, 2)]
    [InlineData((int)ViewerInputMode.RegionExclusionSketch, 1, 2)]
    [InlineData((int)ViewerInputMode.SurfaceAnchorPicking, 1, 0)]
    [InlineData((int)ViewerInputMode.ControlPointEditing, 1, 3)]
    [InlineData((int)ViewerInputMode.CircuitTopologyEditing, 1, 0)]
    public void GetConfiguration_EnablesOnlyTheRequestedInteraction(
        int modeValue,
        int expectedInteractionCount,
        int expectedDragMode)
    {
        ViewerInputMode mode = (ViewerInputMode)modeValue;
        ViewerInputModeConfiguration configuration = ViewerInputModePolicy.GetConfiguration(mode);

        int interactionCount = new[]
        {
            configuration.UsesFaceRectangle,
            configuration.UsesRegionSketch,
            configuration.PicksSurfaceAnchor,
            configuration.EditsControlPoint,
            configuration.EditsCircuitTopology
        }.Count(enabled => enabled);

        Assert.Equal(expectedInteractionCount, interactionCount);
        Assert.Equal(expectedDragMode, configuration.DragMode);
    }

    [Fact]
    public void GetConfiguration_RejectsUnknownMode()
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => ViewerInputModePolicy.GetConfiguration((ViewerInputMode)int.MaxValue));

        Assert.Equal("mode", exception.ParamName);
    }

    [Fact]
    public void State_ActivationReplacesThePreviousInteraction()
    {
        var state = new ViewerInputModeState();

        state.Activate(ViewerInputMode.FaceRectangleSelection);
        state.Activate(ViewerInputMode.ControlPointEditing);

        Assert.Equal(ViewerInputMode.ControlPointEditing, state.Mode);
        Assert.False(state.Configuration.UsesFaceRectangle);
        Assert.True(state.Configuration.EditsControlPoint);
        Assert.Equal(3, state.Configuration.DragMode);
    }

    /// <summary>뷰포트가 비활성화되면 진행 중인 제외 영역 preview와 포인터 상태를 비웁니다.</summary>
    [Fact]
    public void CancelTransientPointerInput_ClearsExclusionSketchAndDragState()
    {
        WpfTestThread.Run(() =>
        {
            Bridge.Reset();
            var viewport = new OccViewport();
            viewport.SetInputMode(ViewerInputMode.RegionExclusionSketch);
            int clearedConnectionPreviews = Bridge.ClearConnectionPathPreviewCount;
            SetPrivateField(viewport, "_dragging", true);
            SetPrivateField(viewport, "_dragStarted", true);
            SetPrivateField(viewport, "_activeDragMode", 2);
            int cancelledSketches = 0;
            viewport.ExclusionSketchCancelled += () => cancelledSketches++;

            viewport.CancelTransientPointerInput();

            Assert.Equal(1, cancelledSketches);
            Assert.Equal(clearedConnectionPreviews + 1, Bridge.ClearConnectionPathPreviewCount);
            Assert.False(GetPrivateField<bool>(viewport, "_dragging"));
            Assert.False(GetPrivateField<bool>(viewport, "_dragStarted"));
            Assert.Equal(0, GetPrivateField<int>(viewport, "_activeDragMode"));
        });
    }

    /// <summary>테스트할 뷰포트 입력 상태를 지정합니다.</summary>
    private static void SetPrivateField<T>(object target, string name, T value)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().FullName, name);
        field.SetValue(target, value);
    }

    /// <summary>뷰포트의 내부 입력 상태를 읽습니다.</summary>
    private static T GetPrivateField<T>(object target, string name) =>
        (T)(target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(target)
            ?? throw new MissingFieldException(target.GetType().FullName, name));
}
