using System.IO;
using System.Reflection;
using System.Windows.Controls;
using HeatingMold.App;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

/// <summary>
/// 분할 영역의 인출점 소속을 근사 외곽 폴리곤까지의 거리(0.02mm)가 아니라
/// 원본 CAD 구간과 유지 반평면으로 판정하는지 실제 예제 모델로 확인한다.
/// 과거 3분할·4분할 자동분할이 실패한 좌표를 그대로 재현해, 그 자리에서
/// 원본 CAD 인출점이 실제로 존재하고 자식 경계가 이를 받아들이는지 고정한다.
/// </summary>
static class SplitBoundaryChecks
{
    static int count;
    static void Check(bool value, string label)
    { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); ++count; }

    static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    static object Invoke(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    static double DistanceToSegment(Vec2 a, Vec2 b, Vec2 p)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double length = dx * dx + dy * dy;
        double t = length <= 0 ? 0 : ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length;
        t = Math.Clamp(t, 0, 1);
        double x = a.X + dx * t - p.X, y = a.Y + dy * t - p.Y;
        return Math.Sqrt(x * x + y * y);
    }

    static double DistanceToRing(IReadOnlyList<Vec2> ring, Vec2 point)
    {
        double best = double.MaxValue;
        for (int i = 0; i < ring.Count; ++i)
            best = Math.Min(best, DistanceToSegment(ring[i], ring[(i + 1) % ring.Count], point));
        return best;
    }

    /// <summary>수직 절단선 하나로 폴리곤을 자른다. 회귀용 밴드이며 클리퍼 격자를 흉내 내지 않는다.</summary>
    static List<Vec2> ClipX(IReadOnlyList<Vec2> ring, double limit, bool keepGreater)
    {
        bool Inside(Vec2 p) => keepGreater ? p.X >= limit : p.X <= limit;
        var result = new List<Vec2>();
        for (int i = 0; i < ring.Count; ++i)
        {
            Vec2 a = ring[i], b = ring[(i + 1) % ring.Count];
            bool inA = Inside(a), inB = Inside(b);
            if (inA) result.Add(a);
            if (inA == inB) continue;
            double t = (limit - a.X) / (b.X - a.X);
            result.Add(new Vec2(limit, a.Y + (b.Y - a.Y) * t));
        }
        return result;
    }

    /// <summary>주어진 u(진행 방향 좌표) 행에서 폴리곤과 만나는 v의 최소/최대. 인출점 후보 행 계산과 같은 규칙.</summary>
    static double ExtremeAt(IReadOnlyList<Vec2> ring, double row, bool low)
    {
        double best = low ? double.MaxValue : double.MinValue;
        for (int i = 0; i < ring.Count; ++i)
        {
            Vec2 a = ring[i], b = ring[(i + 1) % ring.Count];
            if (!((a.X <= row && b.X > row) || (b.X <= row && a.X > row))) continue;
            double t = (row - a.X) / (b.X - a.X);
            double x = a.Y + (b.Y - a.Y) * t;
            best = low ? Math.Min(best, x) : Math.Max(best, x);
        }
        return best;
    }

    static void Cleanup(MainWindow window, string fixtureDir)
    {
        Invoke(window, "ClearAllAreas", false);
        ((TextBox)window.FindName("MarginBox")).Text = "5";
        ((TextBox)window.FindName("SpacingBox")).Text = "50";
        Bridge.LoadStep(Path.Combine(fixtureDir, "layers.stp"));
        Invoke(window, "ApplyWorkingSide", new Vec3(0, 0, 1));
    }

    public static void Run(MainWindow window, string fixtureDir)
    {
        string examplePath = Path.GetFullPath(Path.Combine(fixtureDir, "../../resource/Mold_Cover_Trim.step"));
        if (!File.Exists(examplePath))
        {
            Console.WriteLine("SKIP split boundary checks: default example model is missing");
            return;
        }

        Check(Bridge.LoadStep(examplePath), "load default example model for split boundary checks");
        Check((bool)Invoke(window, "ApplyWorkingSide", new Vec3(1, 0, 0)),
            "confirm +X side for split boundary checks");
        var side = Field<WorkingSideSession>(window, "_workingSide").Confirmed;
        Check(side != null, "confirmed +X side is available for split boundary checks");
        if (side == null) { Cleanup(window, fixtureDir); return; }

        // 영역 B: 기본 예제에서 시나리오 2~4가 세로 배치로 쓰는 세 면.
        int[] faces = { 16, 24, 26 };
        var loops = Bridge.GetSelectedRegionBoundary(faces,
            HeatingMold.Core.Geometry.GeometryTolerancePolicy.DistanceTolerance);
        Check(loops != null && loops.Length == 1 && loops[0].Closed &&
              loops[0].EdgeReferences != null && loops[0].EdgeReferences.Length > 0,
            "split boundary region exposes original CAD references");
        if (loops == null || loops.Length != 1) { Cleanup(window, fixtureDir); return; }

        var parentRing = new List<Vec2>();
        for (int k = 0; k + 2 < loops[0].Points.Length; k += 3)
            parentRing.Add(side.Frame.Project(new Vec3(loops[0].Points[k], loops[0].Points[k + 1], loops[0].Points[k + 2])));
        if (RegionOps.SignedArea(parentRing) < 0) parentRing.Reverse();

        var parentCad = new CadExitBoundary2D(side.Id!.Value, side.Revision!.Value, loops[0].EdgeReferences);
        double parentMinU = parentRing.Min(p => p.X), parentMaxU = parentRing.Max(p => p.X);
        Console.WriteLine($"split boundary parent u range: {parentMinU:F3}..{parentMaxU:F3}");
        Check(Math.Abs(parentMinU - 304.370) < 2.0 && Math.Abs(parentMaxU - 630.000) < 2.0,
            "split boundary parent outline matches the recorded example geometry");

        // (1) 3분할 중간 영역: 과거 B-2 시작점 투영이 NoIntersection으로 실패한 자리.
        //     절단 좌표는 그때 실제 분할이 만든 값이다.
        const double threeLow = 413.333, threeHigh = 516.667, threeRow = 418.340;
        var middle = ClipX(ClipX(parentRing, threeLow, true), threeHigh, false);
        Check(middle.Count >= 3, "3-split middle band keeps a closed outline");
        if (middle.Count >= 3)
        {
            var cuts = new List<(Vec2 A, Vec2 B)>
            {
                (new Vec2(threeLow, -400), new Vec2(threeLow, 400)),
                (new Vec2(threeHigh, -400), new Vec2(threeHigh, 400)),
            };
            bool built = SplitRegionBoundary2D.TryCreate(parentCad, parentRing, middle,
                Array.Empty<(double MinX, double MinY, double MaxX, double MaxY)>(),
                cuts, out SplitRegionBoundary2D threeBoundary, out string threeFailure);
            Check(built, "3-split middle boundary builds from the original CAD boundary: " + threeFailure);
            if (built)
            {
                var target = new Vec2(threeRow, ExtremeAt(middle, threeRow, low: true));
                var resolution = threeBoundary.Project(target, 5.0);
                Check(!resolution.Succeeded || resolution.Point!.Value.Binding.HasValue,
                    "3-split middle exit resolves to an original CAD curve, not the cut line");
                Check(resolution.Succeeded, "3-split middle exit point resolves: " + resolution.Failure);
                if (resolution.Succeeded)
                {
                    var resolved = resolution.Point!.Value;
                    double ringGap = DistanceToRing(middle, resolved.Point);
                    Console.WriteLine($"3-split middle: target={target.X:F3},{target.Y:F3} " +
                        $"resolved={resolved.Point.X:F3},{resolved.Point.Y:F3} ringGap={ringGap:F4}");
                    Check(resolved.Binding.HasValue && resolved.SplitBinding == null,
                        "3-split middle exit keeps the original edge identity");
                    Check(resolved.Point.DistanceTo(target) < 0.5,
                        "3-split middle exit stays within the geometric search distance");
                    Check(ringGap > 0.02,
                        "3-split middle exit is farther than the old 0.02mm ring filter yet still valid");
                }
            }
        }

        // (2) 4분할 첫 영역: 과거 B-1 종료점 투영이 NoIntersection으로 실패한 자리.
        const double fourHigh = 387.500, fourRow = 369.999;
        var first = ClipX(parentRing, fourHigh, false);
        Check(first.Count >= 3, "4-split first band keeps a closed outline");
        if (first.Count >= 3)
        {
            var cuts = new List<(Vec2 A, Vec2 B)>
            {
                (new Vec2(fourHigh, -400), new Vec2(fourHigh, 400)),
            };
            bool built = SplitRegionBoundary2D.TryCreate(parentCad, parentRing, first,
                Array.Empty<(double MinX, double MinY, double MaxX, double MaxY)>(),
                cuts, out SplitRegionBoundary2D fourBoundary, out string fourFailure);
            Check(built, "4-split first boundary builds from the original CAD boundary: " + fourFailure);
            if (built)
            {
                var target = new Vec2(fourRow, ExtremeAt(first, fourRow, low: true));
                var resolution = fourBoundary.Project(target, 5.0);
                Check(!resolution.Succeeded || resolution.Point!.Value.Binding.HasValue,
                    "4-split first exit resolves to an original CAD curve, not the cut line");
                Check(resolution.Succeeded, "4-split first exit point resolves: " + resolution.Failure);
                if (resolution.Succeeded)
                {
                    var resolved = resolution.Point!.Value;
                    double ringGap = DistanceToRing(first, resolved.Point);
                    Console.WriteLine($"4-split first: target={target.X:F3},{target.Y:F3} " +
                        $"resolved={resolved.Point.X:F3},{resolved.Point.Y:F3} ringGap={ringGap:F4}");
                    Check(resolved.Binding.HasValue && resolved.SplitBinding == null,
                        "4-split first exit keeps the original edge identity");
                    Check(resolved.Point.DistanceTo(target) < 0.5,
                        "4-split first exit stays within the geometric search distance");
                    Check(ringGap > 0.02,
                        "4-split first exit is farther than the old 0.02mm ring filter yet still valid");
                }
            }
        }

        Cleanup(window, fixtureDir);
        Console.WriteLine($"{count} split boundary checks passed.");
    }
}
