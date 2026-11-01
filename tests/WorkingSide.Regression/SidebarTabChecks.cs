using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using HeatingMold.App;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

/// <summary>
/// 사이드바 탭 분리 회귀 검사.
/// 탭 이동이 작업 데이터를 지우지 않고, 입력 모드만 탭에 맞게 켜고 끄는지 확인한다.
/// </summary>
static class SidebarTabChecks
{
    static int count;
    static void Check(bool value, string label)
    { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); ++count; }

    static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    static object Invoke(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    public static void Run(MainWindow window)
    {
        var tabs = (TabControl)window.FindName("SidebarTabs")!;
        var viewport = (OccViewport)window.FindName("Viewport")!;
        var faceList = (ListBox)window.FindName("SelectedFacesList")!;
        var excludeBox = (CheckBox)window.FindName("ExcludeModeBox")!;
        var selected = Field<HashSet<int>>(window, "_selectedFaceIds");
        var areas = (IList)Field<object>(window, "_areas");

        Check(window.FindName("VertexEditModeBox") is CheckBox vertexBox &&
              vertexBox.Content as string == "발열경로 편집" &&
              window.FindName("VertexUndoButton") is Button &&
              window.FindName("VertexRedoButton") is Button,
            "vertex edit controls live in the viewport toolbar under the heating-path name");
        Check(tabs.Items.Count == 5 && tabs.SelectedIndex == 0,
            "sidebar exposes five tabs and starts on the area tab");
        Check(window.FindName("ActiveAreaSummaryText") == null &&
              tabs.Items.Cast<TabItem>().Select(t => t.Header as string)
                  .SequenceEqual(new[] { "영역", "제외", "패턴", "편집", "결과" }),
            "sidebar drops the active-area summary card and keeps labelled tabs");
        Check(viewport.FaceSelectionEnabled && window.FindName("RegionPreviewPanel") == null,
            "area tab enables face selection and the shared 2D preview is removed");

        selected.Add(11);
        faceList.Items.Add("seed");
        tabs.SelectedIndex = 4;

        Check(!viewport.FaceSelectionEnabled, "results tab disables viewport face selection");
        Check(selected.Contains(11) && faceList.Items.Count == 1, "tab switch preserves pending face selection");

        int areaCount = areas.Count;
        Invoke(window, "ConfirmWorkingArea");
        Check(areas.Count == areaCount, "WorkingArea confirmation is gated to the area tab");
        Invoke(window, "OnFacePicked", 12, false);
        Check(selected.Count == 1 && !selected.Contains(12), "face picks are ignored outside the area tab");

        tabs.SelectedIndex = 1;
        Check(window.FindName("RegionPreviewPanel") == null, "exclude tab no longer hosts a shared 2D preview");
        SetField(window, "_multiFaceFrame",
            new ProjectionFrame(new Vec3(0, 0, 100), new Vec3(0, 0, 1), new Vec3(1, 0, 0)));
        excludeBox.IsChecked = true;
        Check(viewport.ExclusionSketchMode, "exclude sketch mode can be enabled on the exclude tab");
        tabs.SelectedIndex = 2;
        Check(!viewport.ExclusionSketchMode && excludeBox.IsChecked != true,
            "leaving the exclude tab cancels sketch mode");
        Check(window.FindName("AnchorCandidateList") is ListBox, "pattern tab exposes the anchor candidate list");

        tabs.SelectedIndex = 3;
        viewport.VertexEditMode = true;
        viewport.ConnectorEditMode = true;
        tabs.SelectedIndex = 0;
        Check(!viewport.VertexEditMode && !viewport.ConnectorEditMode,
            "leaving the edit tab exits transient vertex/connector modes");
        Check(viewport.FaceSelectionEnabled,
            "returning to the area tab restores face selection");
        tabs.SelectedIndex = 2;
        viewport.VertexEditMode = true;
        tabs.SelectedIndex = 4;
        Check(!viewport.VertexEditMode, "vertex edit started outside the edit tab also exits on any tab switch");

        var undo = Field<Stack<bool[]>>(window, "_connectorUndo");
        undo.Push(new[] { true });
        tabs.SelectedIndex = 4;
        tabs.SelectedIndex = 0;
        Check(undo.Count == 1, "committed connector history survives tab round-trip");
        undo.Clear();

        Invoke(window, "OnFacePicked", 13, false);
        Check(selected.Contains(13), "area tab accepts face picks again");

        // 테스트가 심은 상태를 되돌려 이후 검사에 영향을 주지 않게 한다.
        selected.Clear();
        faceList.Items.Clear();
        SetField(window, "_multiFaceFrame", null);
        tabs.SelectedIndex = 0;
        Check(viewport.FaceSelectionEnabled && tabs.SelectedIndex == 0,
            "sidebar checks leave the window on the area tab");

        Console.WriteLine($"{count} sidebar tab checks passed.");
    }
}
