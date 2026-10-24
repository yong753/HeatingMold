using System;
using System.Collections.Generic;
using System.Reflection;
using HeatingMold.App;
using HeatingMold.App.Geometry.Occt;
using HeatingMold.App.ViewModels;
using HeatingMold.Core.Primitives;
using HeatingMold.OccBridge;
using Xunit;

namespace HeatingMold.App.Tests;

/// <summary>뷰포트 Face 선택을 영속 참조와 화면 목록에 연결하는 경계를 확인합니다.</summary>
[Collection("WPF Bridge")]
public sealed class FacePickingTests
{
    /// <summary>Face 선택과 해제가 Native 면 번호가 아닌 안정 참조 ID를 갱신합니다.</summary>
    [Fact]
    public void FacePicked_SelectsAndTogglesTheMappedStableReference()
    {
        WpfTestThread.Run(() =>
        {
            Bridge.Reset();
            var window = new MainWindow();
            var faceReferenceId = FaceRefId.New();
            var faceIndex = (FaceReferenceIndex)GetField(window, "_faceReferenceIndex");
            Assert.True(
                faceIndex.TryReplace(
                    new Dictionary<FaceRefId, int> { [faceReferenceId] = 7 },
                    out string diagnostic),
                diagnostic);

            var viewport = (StubViewport)GetField(window, "Viewport");
            RegionSelectionViewModel selection = GetRegionSelection(window);
            viewport.RaiseFacePicked(faceIndex: 7, toggle: false);

            Assert.Equal(faceReferenceId, Assert.Single(selection.SelectedFaceReferences));
            Assert.Single(window.SelectedFacesList.Items);
            Assert.Contains("작업 좌표계를 먼저 확정하세요", window.StatusText.Text);

            viewport.RaiseFacePicked(faceIndex: 7, toggle: true);

            Assert.Empty(selection.SelectedFaceReferences);
            Assert.Empty(window.SelectedFacesList.Items);
            Assert.Contains("적용면에서 제외", window.StatusText.Text);
            Assert.Equal(2, Bridge.ClearExclusionOverlaysCount);
        });
    }

    /// <summary>현재 Face 대응표에 없는 Native 번호는 선택 상태를 변경하지 않습니다.</summary>
    [Fact]
    public void FacePicked_RejectsAnUnmappedNativeFace()
    {
        WpfTestThread.Run(() =>
        {
            Bridge.Reset();
            var window = new MainWindow();
            RegionSelectionViewModel selection = GetRegionSelection(window);
            FaceRefId existingFaceReferenceId = FaceRefId.New();
            selection.Add(existingFaceReferenceId);

            var viewport = (StubViewport)GetField(window, "Viewport");
            viewport.RaiseFacePicked(faceIndex: 99, toggle: false);

            Assert.Equal(existingFaceReferenceId, Assert.Single(selection.SelectedFaceReferences));
            Assert.Contains("안정 Face 참조를 찾지 못했습니다", window.StatusText.Text);
        });
    }

    private static RegionSelectionViewModel GetRegionSelection(MainWindow window) =>
        ((MainWindowViewModel)GetField(window, "_mainWindowViewModel")).RegionSelection;

    private static object GetField(MainWindow window, string name) =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
        ?? throw new InvalidOperationException(string.Concat("테스트 필드를 찾지 못했습니다: ", name));
}
