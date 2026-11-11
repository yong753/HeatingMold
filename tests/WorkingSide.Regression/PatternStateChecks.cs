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

    static bool RefreshCircuits(object area)
    {
        var method = typeof(MainWindow).GetMethod(
            "TryRefreshCircuits", BindingFlags.Static | BindingFlags.NonPublic)!;
        var args = new[] { area, null };
        bool ok = (bool)method.Invoke(null, args)!;
        if (!ok) throw new Exception("circuit refresh failed: " + args[1]);
        return ok;
    }

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
            RoutePieces = new[] { new SerpentineRoutePiece(SerpentineRoutePieceKind.HeatingRun, 0, path) },
            StartExit = new CircuitExitPoint(CircuitExitRole.Start, path[0], 0, 0)
            {
                Binding = new ExitEdgeBinding(1, 1, 1, 1, 0, 20, 0, false),
            },
            EndExit = new CircuitExitPoint(CircuitExitRole.End, path[^1], 0, 1)
            {
                Binding = new ExitEdgeBinding(1, 1, 1, 1, 0, 20, 20, false),
            }
        };
    }

    /// <summary>검토 범위가 요구하는 원본 경계 계약을 만족하는 결정적 mock.
    /// 저장 parameter p를 2D 점 (-10+p, 0)으로 평가한다.</summary>
    private sealed class MockReviewBoundary : IExitBoundary2D
    {
        public ExitBoundaryResolution Project(Vec2 point, double tolerance)
        {
            var projected = new Vec2(Math.Clamp(point.X, -10.0, 10.0), 0);
            double distance = point.DistanceTo(projected);
            return distance > tolerance
                ? ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.NoIntersection)
                : ExitBoundaryResolution.Ok(new ExitBoundaryPoint(projected, 0, 0)
                {
                    Binding = new ExitEdgeBinding(1, 1, 1, 1, 0, 20, projected.X + 10.0, false),
                });
        }

        public ExitBoundaryResolution ResolveOutward(Vec2 endpoint, Vec2 next, double tolerance)
        {
            Vec2 direction = endpoint - next;
            if (direction.Length <= tolerance)
                return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.DegenerateSegment);
            if (Math.Abs(direction.Y) > tolerance)
                return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.NoIntersection);
            double t = -endpoint.Y / direction.Y;
            var hit = endpoint + direction * t;
            if (hit.X < -10 - tolerance || hit.X > 10 + tolerance)
                return ExitBoundaryResolution.Fail(ExitBoundaryFailureReason.NoIntersection);
            return ExitBoundaryResolution.Ok(new ExitBoundaryPoint(hit, 0, 0)
            {
                Binding = new ExitEdgeBinding(1, 1, 1, 1, 0, 20, hit.X + 10.0, false),
            });
        }

        public ExitBoundaryResolution ProjectBound(ExitEdgeBinding binding, Vec2 point, double tolerance) =>
            Project(point, tolerance);

        public ExitBoundaryResolution Evaluate(ExitEdgeBinding binding) =>
            ExitBoundaryResolution.Ok(new ExitBoundaryPoint(new Vec2(-10 + binding.Parameter, 0), 0, 0)
            {
                Binding = binding,
            });
    }

    static object Area(object window, Type areaType, WorkingSideDefinition side, string name, int id)
    {
        var value = Activator.CreateInstance(areaType, nonPublic: true)!;
        areaType.GetProperty("Name")!.SetValue(value, name);
        areaType.GetProperty("Frame")!.SetValue(value, side.Frame);
        areaType.GetProperty("AreaId")!.SetValue(value, id);
        areaType.GetProperty("ExitFaceId")!.SetValue(value, 0);
        areaType.GetProperty("ExitFaceRevision")!.SetValue(value, Bridge.GetModelRevision());
        ((List<Vec2>)areaType.GetProperty("ExitChain")!.GetValue(value)!).AddRange(
            new[] { new Vec2(-10, 0), new Vec2(10, 0) });
        ((List<List<Vec2>>)areaType.GetProperty("Region")!.GetValue(value)!).Add(new()
            { new(-12, -8), new(12, -8), new(12, 8), new(-12, 8) });
        areaType.GetProperty("RegionBoundary")!.SetValue(value, new ExactRegionContainment(
            new MockExactRegionBoundary(new Vec2[] { new(-12, -8), new(12, -8), new(12, 8), new(-12, 8) })));
        areaType.GetProperty("ExitBoundary")!.SetValue(value, new MockReviewBoundary());
        // 발열 전력은 영역별 입력이 아니라 검토 계산이 배분하는 파생값이므로 여기서 정하지 않는다.
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
        RefreshCircuits(a);
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

        // 검토 범위는 전기 사양표·확대 보기·내보내기 안내가 같은 목록을 쓰는지 확인한다.
        areaType.GetProperty("Pattern")!.SetValue(b, Pattern(new Vec2(-10, 0), new Vec2(10, 0)));
        RefreshCircuits(b);
        areaType.GetProperty("AppliedInputs")!.SetValue(b, Invoke(window, "CaptureAppliedInputs", b, 5.0, 50.0));
        areaType.GetProperty("NativePieceIds")!.SetValue(a, new[] { -4501 });
        areaType.GetProperty("RoutePieceLengths")!.SetValue(a, new[] { 20.0 });
        areaType.GetProperty("NativePieceIds")!.SetValue(b, new[] { -4502 });
        areaType.GetProperty("RoutePieceLengths")!.SetValue(b, new[] { 30.0 });
        var scopeBox = (ComboBox)window.FindName("ReviewScopeBox");
        var electricalGrid = (DataGrid)window.FindName("ElectricalSpecGrid");
        var expandedGrid = (DataGrid)window.FindName("ExpandedElectricalGrid");
        var exportScope = (TextBlock)window.FindName("ExportScopeText");
        Invoke(window, "RefreshElectricalTable");
        Invoke(window, "UpdateCircuitTable");
        Check(scopeBox.SelectedIndex == 0 && electricalGrid.Items.Count == 3
            && expandedGrid.Items.Count == 3,
            "all-area review scope lists every circuit plus the total row in one table");
        Check(exportScope.Text.Contains("전체 영역"),
            "all-area export scope is stated explicitly");
        scopeBox.SelectedIndex = 1;
        Check(electricalGrid.Items.Count == 2 && exportScope.Text.Contains("PatternStateA"),
            "current-area review scope filters the specification table and export notice");
        Check(exportScope.Text.Contains("다른 영역은 포함되지 않습니다"),
            "current-area export warns that other areas are excluded");
        scopeBox.SelectedIndex = 0;

        // 회로 길이는 별도 표가 아니라 사양표의 길이 열이 보여 준다.
        Check(window.FindName("CircuitLengthGrid") == null,
            "the duplicated circuit-length table is removed from the review tab");

        // 사용자 확정: 발열 전력은 전체가 나누어 쓰는 하나의 입력이며, 회로별 배분은 실측 길이 비율이다.
        Check(window.FindName("TargetPowerBox") == null,
            "the per-area target-power input is removed from the review tab");
        var powerBox = (TextBox)window.FindName("AllowedPowerBox");
        var summary = (TextBlock)window.FindName("ElectricalSummaryText");
        var specGrid = (DataGrid)window.FindName("ElectricalSpecGrid");
        Check(powerBox.Text == "3.5",
            "a new document starts with the confirmed 3.5 kW shared heating power");

        // 20 mm 대 30 mm → 전체 3.5 kW를 2:3으로 나눠 1.4 kW / 2.1 kW.
        Invoke(window, "UpdateCircuitTable");
        double? powerA = null, powerB = null;
        foreach (object item in specGrid.Items)
        {
            string id = (string)item.GetType().GetProperty("CircuitId")!.GetValue(item)!;
            if (id.StartsWith("PatternStateA-")) powerA = (double?)item.GetType().GetProperty("TargetPowerW")!.GetValue(item);
            if (id.StartsWith("PatternStateB-")) powerB = (double?)item.GetType().GetProperty("TargetPowerW")!.GetValue(item);
        }
        Check(powerA.HasValue && powerB.HasValue
            && Math.Abs(powerA.Value - 1_400.0) < 0.001 && Math.Abs(powerB.Value - 2_100.0) < 0.001,
            "the shared heating power is allocated by measured circuit length (1.4 kW / 2.1 kW)");
        Check(summary.Text.Contains("배분 3.50 kW / 한도 3.50 kW"),
            "the summary states the allocated total against the shared limit");

        // 표시 범위를 현재 영역으로 좁혀도 배분은 전체 영역 기준이므로 값이 바뀌지 않는다.
        scopeBox.SelectedIndex = 1;
        Invoke(window, "UpdateCircuitTable");
        double? currentOnlyPower = null;
        foreach (object item in specGrid.Items)
        {
            string id = (string)item.GetType().GetProperty("CircuitId")!.GetValue(item)!;
            if (id.StartsWith("PatternStateA-")) currentOnlyPower = (double?)item.GetType().GetProperty("TargetPowerW")!.GetValue(item);
        }
        Check(currentOnlyPower.HasValue && Math.Abs(currentOnlyPower.Value - 1_400.0) < 0.001,
            "filtering the table to the current area does not re-allocate the shared power");
        Check(summary.Text.Contains("전력은 전체 배분"),
            "the current-area scope states that the allocation still covers every area");
        scopeBox.SelectedIndex = 0;

        // 준비되지 않은 영역은 배분 대상에서 빠지고, 남은 영역이 전체 발열 전력을 나눠 쓴다.
        // 남은 회로가 허용치 안에 들어와도 경로 없는 영역이 있으면 '전체 OK'가 아니다.
        powerBox.Text = "0.02";
        object? savedPatternB = areaType.GetProperty("Pattern")!.GetValue(b);
        areaType.GetProperty("Pattern")!.SetValue(b, null);
        Invoke(window, "UpdateCircuitTable");
        Invoke(window, "UpdateExportScopeText");
        double? onlyPowerA = null;
        foreach (object item in specGrid.Items)
        {
            string id = (string)item.GetType().GetProperty("CircuitId")!.GetValue(item)!;
            if (id.StartsWith("PatternStateA-")) onlyPowerA = (double?)item.GetType().GetProperty("TargetPowerW")!.GetValue(item);
        }
        Check(specGrid.Items.Count == 2 && onlyPowerA.HasValue && Math.Abs(onlyPowerA.Value - 20.0) < 0.001,
            "an area without a generated route is excluded from the shared allocation");
        Check(!summary.Text.Contains("전체 OK") && summary.Text.Contains("준비 1/2")
            && !exportScope.Text.Contains("준비 2/2"),
            "an unready area blocks the whole OK verdict and the export");

        // 잘못된 입력에서는 배분을 다시 계산할 수 없으므로 이전 배분값을 남기지 않는다.
        int areaCountBefore = ((IList)Field<object>(window, "_areas")).Count;
        powerBox.Text = "abc";
        Invoke(window, "UpdateCircuitTable");
        object? clearedA = areaType.GetProperty("TargetPowerW")!.GetValue(a);
        object? clearedB = areaType.GetProperty("TargetPowerW")!.GetValue(b);
        Check(clearedA == null && clearedB == null && summary.Text.Contains("전기 입력 오류"),
            "an invalid shared power clears the previous allocation instead of keeping it");
        powerBox.Text = "3.5";

        // 전기 입력은 파생 계산만 바꾼다. 확정 경로의 길이·네이티브 조각 ID·인출점은 그대로여야 한다.
        double[] lengthsA = (double[])areaType.GetProperty("RoutePieceLengths")!.GetValue(a)!;
        int[] idsA = (int[])areaType.GetProperty("NativePieceIds")!.GetValue(a)!;
        Check(lengthsA.Length == 1 && Math.Abs(lengthsA[0] - 20.0) < 1e-9
            && idsA.Length == 1 && idsA[0] == -4501
            && ((IList)Field<object>(window, "_areas")).Count == areaCountBefore,
            "editing the shared power leaves committed lengths, native ids and the area list untouched");

        areaType.GetProperty("Pattern")!.SetValue(b, savedPatternB);
        Invoke(window, "UpdateCircuitTable");
        Invoke(window, "UpdateExportScopeText");
        Check(specGrid.Items.Count == 3 && summary.Text.Contains("배분 3.50 kW")
            && exportScope.Text.Contains("준비 2/2"),
            "restoring the route returns both areas to the shared allocation and exportable review");

        // (d)에서 위조한 음수 조각 ID는 네이티브에 존재하지 않으므로, 다음 삭제 검증의
        // oldIds에서 제거해 차등 커밋이 실제 존재하지 않는 ID 삭제를 시도하지 않게 한다.
        areaType.GetProperty("NativePieceIds")!.SetValue(a, Array.Empty<int>());
        areaType.GetProperty("RoutePieceLengths")!.SetValue(a, Array.Empty<double>());
        areaType.GetProperty("NativePieceIds")!.SetValue(b, Array.Empty<int>());
        areaType.GetProperty("RoutePieceLengths")!.SetValue(b, Array.Empty<double>());

        selector.SelectedItem = b;
        Check(ReferenceEquals(Field<object>(window, "_activeArea"), b),
            "selecting an area in the persistent selector activates it");
        Invoke(window, "ActivateArea", a);
        Invoke(window, "RefreshAreaList");

        // 삭제는 남은 영역 중 확정 상태(CommittedMapping 존재)만 재검증한다. 확정 영역의
        // 준비가 실패하면 목록과 활성 영역을 바꾸지 않고 유지해야 한다.
        object good = Area(window, areaType, side, "PatternDeleteGood", 4503);
        areaType.GetProperty("Pattern")!.SetValue(good, Pattern(new Vec2(-10, 0), new Vec2(10, 0)));
        RefreshCircuits(good);
        object bad = Area(window, areaType, side, "PatternDeleteBad", 4504);
        areaType.GetProperty("Pattern")!.SetValue(bad, Pattern(new Vec2(-10, 0), new Vec2(10, 0)));
        RefreshCircuits(bad);
        // bad를 확정 상태로 위장하되 길이 배열을 어긋나게 만들어 확정 재검증이 실패하게 한다.
        var capture = typeof(MainWindow).Assembly.GetType("HeatingMold.App.CommittedRouteContext")!;
        areaType.GetProperty("CommittedMapping")!.SetValue(bad,
            capture.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(method => method.Name == "Capture" && method.GetParameters().Length == 1)
                .Invoke(null, new[] { bad }));
        areaType.GetProperty("NativePieceIds")!.SetValue(bad, new[] { -4504 });
        areaType.GetProperty("RoutePieceLengths")!.SetValue(bad, Array.Empty<double>());
        // good/bad를 목록 상자에 반영한 뒤 실제로 good을 선택해야 삭제 대상이 good이 된다.
        Invoke(window, "RefreshAreaList");
        ((ListBox)window.FindName("ConfirmedAreasList")).SelectedItem = good;
        Invoke(window, "ActivateArea", good);

        var areas = (IList)Field<object>(window, "_areas");
        int before = areas.Count;
        Invoke(window, "DeleteSelectedArea_Click", null!, null!);
        Check(areas.Count == before && areas.Contains(good),
            "failed area deletion restores the removed area to the list");
        Check(ReferenceEquals(Field<object>(window, "_activeArea"), good),
            "failed area deletion restores the previously active area");
        Check(((TextBlock)window.FindName("StatusText")).Text.Contains("유지"),
            "failed area deletion reports that the existing areas were retained");

        // 확정 재검증이 성공하면 삭제가 진행되고 남은 초안은 건드리지 않는다.
        areaType.GetProperty("CommittedMapping")!.SetValue(bad, null);
        areaType.GetProperty("NativePieceIds")!.SetValue(bad, Array.Empty<int>());
        areaType.GetProperty("RoutePieceLengths")!.SetValue(bad, Array.Empty<double>());
        Invoke(window, "DeleteSelectedArea_Click", null!, null!);
        // 성공 시 _areas 필드가 새 목록으로 교체되므로 검증은 현재 필드에서 다시 읽는다.
        var remaining = (IList)Field<object>(window, "_areas");
        Check(remaining.Count == before - 1 && !remaining.Contains(good),
            "successful area deletion removes the selected area from the list");
        Check(remaining.Contains(bad) && Field<object>(window, "_activeArea") != null
            && !ReferenceEquals(Field<object>(window, "_activeArea"), good),
            "successful area deletion retains untouched drafts and activates the remainder");

        areas.Remove(bad);
        Invoke(window, "ClearAllAreas", false);
        Console.WriteLine($"{count} pattern state checks passed.");
    }
}
