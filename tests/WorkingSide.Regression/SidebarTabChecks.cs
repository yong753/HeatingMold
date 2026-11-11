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
/// 제외 도구는 확정 작업영역이 있을 때만 뷰포트 툴바에서 활성화되어야 한다.
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
    static bool LogicalContains(object root, object target)
    {
        if (ReferenceEquals(root, target)) return true;
        if (root is not DependencyObject node) return false;
        foreach (object child in LogicalTreeHelper.GetChildren(node))
            if (LogicalContains(child, target)) return true;
        return false;
    }

    public static void Run(MainWindow window)
    {
        var tabs = (TabControl)window.FindName("SidebarTabs")!;
        var viewport = (OccViewport)window.FindName("Viewport")!;
        var faceList = (ListBox)window.FindName("SelectedFacesList")!;
        var excludeBox = (CheckBox)window.FindName("ExcludeModeBox")!;
        var clearExclusions = (Button)window.FindName("ClearExclusionsButton")!;
        var selected = Field<HashSet<int>>(window, "_selectedFaceIds");
        var areas = (IList)Field<object>(window, "_areas");

        Check(window.FindName("VertexEditModeBox") is CheckBox vertexBox &&
              vertexBox.Content as string == "발열경로 편집" &&
              window.FindName("VertexUndoButton") == null &&
              window.FindName("VertexRedoButton") == null,
            "vertex edit control lives in the viewport toolbar without undo/redo");
        Check(tabs.Items.Count == 4 && tabs.SelectedIndex == 0,
            "sidebar exposes four task tabs and starts on the area tab");
        Check(window.FindName("ActiveAreaSummaryText") == null &&
              tabs.Items.Cast<TabItem>().Select(t => t.Header as string)
                  .SequenceEqual(new[] { "영역", "경로", "검토", "내보내기" }),
            "sidebar uses task-oriented labels and keeps one active-area selector");
        Check(window.FindName("ActiveAreaSelectorBox") is ComboBox &&
              window.FindName("ActiveAreaStateText") is TextBlock,
            "active-area selector and result state are available above every tab");
        object pathContent = ((TabItem)tabs.Items[1]).Content;
        object reviewContent = ((TabItem)tabs.Items[2]).Content;
        object targetPowerBox = window.FindName("TargetPowerBox")!;
        object splitAreaButton = window.FindName("SplitAreaButton")!;
        Check(LogicalContains(reviewContent, targetPowerBox) &&
              LogicalContains(reviewContent, splitAreaButton),
            "target power input and automatic split live on the review tab");
        Check(!LogicalContains(pathContent, targetPowerBox) &&
              !LogicalContains(pathContent, splitAreaButton),
            "the path tab stays generation-only");
        object electricalGrid = window.FindName("ElectricalSpecGrid")!;
        object reviewScope = window.FindName("ReviewScopeBox")!;
        object presetBox = window.FindName("WirePresetBox")!;
        Check(LogicalContains(reviewContent, electricalGrid) &&
              LogicalContains(reviewContent, reviewScope) &&
              LogicalContains(reviewContent, presetBox),
            "the review tab is one unified electrical specification section");
        Check(window.FindName("CircuitLengthGrid") == null &&
              window.FindName("WireDiameterBox") == null &&
              window.FindName("WireResistanceBox") == null,
            "the duplicated length table and fixed wire readout boxes are removed");
        Check(window.FindName("ExpandedElectricalGrid") is DataGrid &&
              window.FindName("ExpandedCircuitGrid") == null,
            "the viewport expander shows the electrical table instead of a duplicate length table");
        Check(excludeBox.IsChecked != true && !excludeBox.IsEnabled && !clearExclusions.IsEnabled,
            "exclude controls start disabled in the viewport toolbar");
        Check(viewport.FaceSelectionEnabled && window.FindName("RegionPreviewPanel") == null,
            "area tab enables face selection and the shared 2D preview is removed");

        selected.Add(11);
        faceList.Items.Add("seed");
        tabs.SelectedIndex = 3;

        Check(!viewport.FaceSelectionEnabled, "review and export tabs disable viewport face selection");
        Check(selected.Contains(11) && faceList.Items.Count == 1, "tab switch preserves pending face selection");

        int areaCount = areas.Count;
        Invoke(window, "ConfirmWorkingArea");
        Check(areas.Count == areaCount, "WorkingArea confirmation is gated to the area tab");
        Invoke(window, "OnFacePicked", 12, false);
        Check(selected.Count == 1 && !selected.Contains(12), "face picks are ignored outside the area tab");

        // 확정 작업영역을 하나 만들어 제외 도구의 활성 조건을 검사한다.
        var areaType = typeof(MainWindow).Assembly.GetType("HeatingMold.App.WorkingArea")!;
        var area = Activator.CreateInstance(areaType, nonPublic: true)!;
        areaType.GetProperty("Frame")!.SetValue(area,
            new ProjectionFrame(new Vec3(0, 0, 100), new Vec3(0, 0, 1), new Vec3(1, 0, 0)));
        areas.Add(area);
        Invoke(window, "ActivateArea", area);
        Check(ReferenceEquals(Field<object>(window, "_activeArea"), area) &&
              excludeBox.IsEnabled,
            "exclude control enables for a confirmed working area");

        tabs.SelectedIndex = 0;
        excludeBox.IsChecked = true;
        Check(viewport.ExclusionSketchMode,
            "exclude sketch mode can be enabled from the viewport toolbar");

        selected.Add(21);
        areaCount = areas.Count;
        Invoke(window, "ConfirmWorkingArea");
        Check(areas.Count == areaCount,
            "working area confirmation is blocked while exclusion sketch is active");
        selected.Remove(21);

        var exclusions = (IList)areaType.GetProperty("Exclusions")!.GetValue(area)!;
        exclusions.Add((0d, 0d, 10d, 10d));
        Invoke(window, "UpdateActionStates");
        Check(clearExclusions.IsEnabled,
            "exclude reset enables when the active area has exclusions");

        tabs.SelectedIndex = 1;
        Check(!viewport.ExclusionSketchMode && excludeBox.IsChecked != true,
            "leaving the area tab cancels sketch mode");
        Check(exclusions.Count == 1, "tab switch preserves exclusion data");
        Check(window.FindName("AnchorCandidateList") is ListBox, "pattern tab exposes the anchor candidate list");

        tabs.SelectedIndex = 1;
        viewport.VertexEditMode = true;
        Invoke(window, "UpdateActionStates");
        Check(!excludeBox.IsEnabled, "vertex edit locks the exclude control");
        viewport.VertexEditMode = true;
        tabs.SelectedIndex = 0;
        Check(!viewport.VertexEditMode,
            "leaving the path tab exits transient vertex mode");
        Check(viewport.FaceSelectionEnabled,
            "returning to the area tab restores face selection");
        tabs.SelectedIndex = 1;
        viewport.VertexEditMode = true;
        tabs.SelectedIndex = 2;
        Check(!viewport.VertexEditMode, "vertex edit started outside the area tab also exits on any tab switch");

        Check(typeof(MainWindow).GetField("_connectorUndo",
                BindingFlags.Instance | BindingFlags.NonPublic) == null,
            "connector undo history is removed");
        tabs.SelectedIndex = 0;

        Invoke(window, "OnFacePicked", 13, false);
        Check(selected.Contains(13), "area tab accepts face picks again");

        // 테스트가 심은 상태를 되돌려 이후 검사에 영향을 주지 않게 한다.
        Invoke(window, "ClearAllAreas", false);
        Invoke(window, "UpdateActionStates");
        Check(!viewport.ExclusionSketchMode && !excludeBox.IsEnabled && !clearExclusions.IsEnabled,
            "clearing areas disables the exclude controls");
        selected.Clear();
        faceList.Items.Clear();
        tabs.SelectedIndex = 0;
        Check(viewport.FaceSelectionEnabled && tabs.SelectedIndex == 0,
            "sidebar checks leave the window on the area tab");

        Console.WriteLine($"{count} sidebar tab checks passed.");
    }
}
