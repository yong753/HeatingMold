using System.Collections;
using System.Reflection;
using System.Windows.Controls;
using HeatingMold.App;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

/// <summary>
/// 제외영역 변경으로 활성 패턴이 무효화될 때 회로 길이표와 전기 사양표가
/// 같은 트랜잭션 안에서 즉시 비워지는지 확인한다.
/// </summary>
static class ElectricalRefreshChecks
{
    static int count;
    static void Check(bool value, string label)
    { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); ++count; }

    static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    static object Invoke(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, args);

    static SerpentineResult Pattern(Vec2 a, Vec2 b)
    {
        var points = new List<Vec2> { a, b };
        return new SerpentineResult
        {
            Path = points,
            Segments = new[] { new SerpentineSegment(0, a, b) },
            LaneCount = 1,
            AngleDeg = 0,
            TotalLength = a.DistanceTo(b),
            RoutePieces = new[] { new SerpentineRoutePiece(SerpentineRoutePieceKind.HeatingRun, 0, points) }
        };
    }

    public static void Run(MainWindow window)
    {
        var side = Field<WorkingSideSession>(window, "_workingSide").Confirmed;
        Check(side != null, "confirmed +Z side is available for electrical refresh checks");
        if (side == null) return;

        var areaType = typeof(MainWindow).Assembly.GetType("HeatingMold.App.WorkingArea")!;
        var area = Activator.CreateInstance(areaType, nonPublic: true)!;
        areaType.GetProperty("Name")!.SetValue(area, "ElectricalRefreshCheck");
        areaType.GetProperty("Frame")!.SetValue(area, side.Frame);
        areaType.GetProperty("AreaId")!.SetValue(area, 4302);
        ((IList)areaType.GetProperty("Exclusions")!.GetValue(area)!).Add((0d, -2d, 4d, 2d));
        areaType.GetProperty("Pattern")!.SetValue(area, Pattern(new Vec2(-8, 0), new Vec2(8, 0)));
        // 네이티브에 실제 존재하지 않는 조각 ID를 위조하면 새 차등 커밋의 삭제 검증이
        // 실패해 무효화가 차단되므로, 확정 경로가 없는 초안 상태로 시작한다.
        areaType.GetProperty("NativePieceIds")!.SetValue(area, Array.Empty<int>());
        areaType.GetProperty("RoutePieceLengths")!.SetValue(area, Array.Empty<double>());
        areaType.GetProperty("PatternDisplayed")!.SetValue(area, true);
        ((IList)Field<object>(window, "_areas")).Add(area);
        Invoke(window, "ActivateArea", area);

        var circuitGrid = (DataGrid)window.FindName("CircuitLengthGrid");
        var electricalGrid = (DataGrid)window.FindName("ElectricalSpecGrid");
        circuitGrid.ItemsSource = new[] { "stale circuit" };
        electricalGrid.ItemsSource = new[] { "stale electrical" };

        Invoke(window, "ClearExclusions_Click", null!, null!);

        Check(areaType.GetProperty("Pattern")!.GetValue(area) == null,
            "clearing exclusions invalidates the generated pattern");
        Check(circuitGrid.Items.Count == 0,
            "clearing exclusions refreshes the circuit length table in the same transaction");
        Check(electricalGrid.Items.Count == 0,
            "clearing exclusions refreshes the electrical specification table in the same transaction");

        Invoke(window, "ClearAllAreas", false);
        Console.WriteLine($"{count} electrical refresh checks passed.");
    }
}
