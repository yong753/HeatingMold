using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using HeatingMold.App;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.OccBridge;

/// <summary>
/// 영역별 배치 조건 초안, 명시적 숫자 검증, 마지막 생성 조건 대비 오래된 결과 판정,
/// 그리고 삭제 실패 시 영역 목록 복구를 확인한다.
/// </summary>
static class PatternStateChecks
{
    static int count;
    static void Check(bool value, string label)
    { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); ++count; }

    static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    static object Invoke(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, args);

    static SerpentineResult Pattern(params Vec2[] points)
    {
        var path = points.ToList();
        var segments = new List<SerpentineSegment>();
        for (int i = 1; i < path.Count; i++) segments.Add(new SerpentineSegment(0, path[i - 1], path[i]));
        return new SerpentineResult
        {
            Path = path,
            Segments = segments,
            LaneCount = 1,
            AngleDeg = 0,
            TotalLength = path.Zip(path.Skip(1), (a, b) => a.DistanceTo(b)).Sum(),
            RoutePieces = new[] { new SerpentineRoutePiece(SerpentineRoutePieceKind.HeatingRun, 0, path) }
        };
    }

    static object Area(object window, Type areaType, WorkingSideDefinition side, string name, int id)
    {
        var value = Activator.CreateInstance(areaType, nonPublic: true)!;
        areaType.GetProperty("Name")!.SetValue(value, name);
        areaType.GetProperty("Frame")!.SetValue(value, side.Frame);
        areaType.GetProperty("AreaId")!.SetValue(value, id);
        ((List<List<Vec2>>)areaType.GetProperty("Region")!.GetValue(value)!).Add(new()
            { new(-12, -8), new(12, -8), new(12, 8), new(-12, 8) });
        ((IList)Field<object>(window, "_areas")).Add(value);
        return value;
    }

    public static void Run(MainWindow window)
    {
        var side = Field<WorkingSideSession>(window, "_workingSide").Confirmed;
        Check(side != null, "confirmed +Z side is available for pattern state checks");
        if (side == null) return;

        var areaType = typeof(MainWindow).Assembly.GetType("HeatingMold.App.WorkingArea")!;
        var marginBox = (TextBox)window.FindName("MarginBox");
        var spacingBox = (TextBox)window.FindName("SpacingBox");
        var generateButton = (Button)window.FindName("GenerateButton");
        var inputError = (TextBlock)window.FindName("PatternInputErrorText");

        object a = Area(window, areaType, side, "PatternStateA", 4501);
        object b = Area(window, areaType, side, "PatternStateB", 4502);
        Invoke(window, "ActivateArea", a);

        marginBox.Text = "7";
        spacingBox.Text = "60";
        Check((string)areaType.GetProperty("MarginText")!.GetValue(a)! == "7",
            "editing margin stores the draft on the active area");
        Invoke(window, "ActivateArea", b);
        Check(marginBox.Text == "5" && spacingBox.Text == "50",
            "switching to another area restores its own draft values");
        Invoke(window, "ActivateArea", a);
        Check(marginBox.Text == "7" && spacingBox.Text == "60",
            "returning to an area restores both edited draft strings without cross-writing");

        marginBox.Text = "abc";
        Check(!generateButton.IsEnabled && inputError.Visibility == Visibility.Visible,
            "invalid margin disables generation and shows an input error");
        marginBox.Text = "NaN";
        Check(!generateButton.IsEnabled, "NaN margin is rejected instead of silently defaulted");
        marginBox.Text = "-1";
        Check(!generateButton.IsEnabled, "negative margin is rejected");
        marginBox.Text = "5";
        spacingBox.Text = "0";
        Check(!generateButton.IsEnabled, "non-positive spacing is rejected");
        spacingBox.Text = "50";
        Check(generateButton.IsEnabled && inputError.Visibility == Visibility.Collapsed,
            "valid inputs clear the error and re-enable generation");

        areaType.GetProperty("Pattern")!.SetValue(a, Pattern(new Vec2(-10, 0), new Vec2(10, 0)));
        object applied = Invoke(window, "CaptureAppliedInputs", a, 5.0, 50.0);
        areaType.GetProperty("AppliedInputs")!.SetValue(a, applied);

        marginBox.Text = "7";
        Check((bool)Invoke(window, "IsPatternInputStale", a),
            "changed margin marks the displayed pattern as stale");
        marginBox.Text = "5.0";
        Check(!(bool)Invoke(window, "IsPatternInputStale", a),
            "numerically equal draft text is not treated as stale");
        marginBox.Text = "5";
        spacingBox.Text = "60";
        Check((bool)Invoke(window, "IsPatternInputStale", a),
            "changed spacing marks the displayed pattern as stale");
        spacingBox.Text = "50";

        Invoke(window, "RefreshAreaList");
        var selector = (ComboBox)window.FindName("ActiveAreaSelectorBox");
        var stateText = (TextBlock)window.FindName("ActiveAreaStateText");
        Check(selector.IsEnabled && selector.Items.Count == 2 && ReferenceEquals(selector.SelectedItem, a),
            "the active-area selector lists working areas and follows the active one");
        Check(stateText.Text == "생성 완료",
            "an applied, unedited pattern is reported as generated");
        marginBox.Text = "7";
        Check(stateText.Text == "재생성 필요",
            "changing draft inputs reports the displayed result as stale");
        marginBox.Text = "5";
        areaType.GetProperty("RouteEdited")!.SetValue(a, true);
        Invoke(window, "UpdateActionStates");
        Check(stateText.Text == "수동 편집됨",
            "a manually edited pattern is reported separately from a generated one");
        areaType.GetProperty("RouteEdited")!.SetValue(a, false);
        Invoke(window, "UpdateActionStates");

        // 검토 범위는 회로 길이 표·전기 표·내보내기 안내가 같은 목록을 쓰는지 확인한다.
        areaType.GetProperty("Pattern")!.SetValue(b, Pattern(new Vec2(-10, 0), new Vec2(10, 0)));
        areaType.GetProperty("NativePieceIds")!.SetValue(a, new[] { -4501 });
        areaType.GetProperty("RoutePieceLengths")!.SetValue(a, new[] { 20.0 });
        areaType.GetProperty("NativePieceIds")!.SetValue(b, new[] { -4502 });
        areaType.GetProperty("RoutePieceLengths")!.SetValue(b, new[] { 30.0 });
        var scopeBox = (ComboBox)window.FindName("ReviewScopeBox");
        var circuitGrid = (DataGrid)window.FindName("CircuitLengthGrid");
        var exportScope = (TextBlock)window.FindName("ExportScopeText");
        Invoke(window, "RefreshElectricalTable");
        Invoke(window, "UpdateCircuitTable");
        Check(scopeBox.SelectedIndex == 0 && circuitGrid.Items.Count == 2,
            "all-area review scope lists circuits from every generated area");
        Check(exportScope.Text.Contains("전체 영역"),
            "all-area export scope is stated explicitly");
        scopeBox.SelectedIndex = 1;
        Check(circuitGrid.Items.Count == 1 && exportScope.Text.Contains("PatternStateA"),
            "current-area review scope filters the circuit table and export notice");
        Check(exportScope.Text.Contains("다른 영역은 포함되지 않습니다"),
            "current-area export warns that other areas are excluded");
        scopeBox.SelectedIndex = 0;

        selector.SelectedItem = b;
        Check(ReferenceEquals(Field<object>(window, "_activeArea"), b),
            "selecting an area in the persistent selector activates it");
        Invoke(window, "ActivateArea", a);
        Invoke(window, "RefreshAreaList");

        // 삭제 후보를 목록에서 먼저 제거한 뒤 재구성이 실패하면 목록과 활성 영역을 복구해야 한다.
        object good = Area(window, areaType, side, "PatternDeleteGood", 4503);
        areaType.GetProperty("Pattern")!.SetValue(good, Pattern(new Vec2(-10, 0), new Vec2(10, 0)));
        object bad = Area(window, areaType, side, "PatternDeleteBad", 4504);
        areaType.GetProperty("Pattern")!.SetValue(bad, Pattern(new Vec2(1000, 1000), new Vec2(1100, 1000)));
        Invoke(window, "ActivateArea", good);
        ((ListBox)window.FindName("ConfirmedAreasList")).SelectedItem = good;

        var areas = (IList)Field<object>(window, "_areas");
        int before = areas.Count;
        Invoke(window, "DeleteSelectedArea_Click", null!, null!);
        Check(areas.Count == before && areas.Contains(good),
            "failed area deletion restores the removed area to the list");
        Check(ReferenceEquals(Field<object>(window, "_activeArea"), good),
            "failed area deletion restores the previously active area");
        Check(((TextBlock)window.FindName("StatusText")).Text.Contains("유지"),
            "failed area deletion reports that the existing areas were retained");

        areas.Remove(bad);
        Invoke(window, "ClearAllAreas", false);
        Console.WriteLine($"{count} pattern state checks passed.");
    }
}
