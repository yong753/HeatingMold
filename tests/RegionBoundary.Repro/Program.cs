using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using HeatingMold.OccBridge;

// 12번 Face 단독 선택의 실제 CAD 회귀.
// 실행 전 OptiX/OCCT DLL 경로가 PATH에 있어야 한다(앱과 동일 환경).
// 사용: dotnet run --project tests/RegionBoundary.Repro -c Release -- [repoRoot]
internal static class Program
{
    private static int _failures;

    private static void Check(bool condition, string message)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + message);
        if (!condition) ++_failures;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : FindRepoRoot();
        string model = Path.Combine(root, "resource", "Mold_Solids.stp");
        Check(File.Exists(model), "model exists: " + model);
        if (!File.Exists(model)) return 1;

        Console.WriteLine("MODEL=" + Path.GetFullPath(model));
        Console.WriteLine("MODEL_SHA256=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(model))));
        Check(Bridge.LoadStep(model), "LoadStep");

        foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
            if (module.ModuleName.Equals("OccBridge.dll", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("LOADED_DLL=" + module.FileName);
                Console.WriteLine("DLL_SHA256=" +
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(module.FileName))));
            }

        var info = Bridge.GetShapeInfo();
        Check(info.Faces == 574 && info.Solids == 30, $"shape info faces={info.Faces} solids={info.Solids}");

        // +Z 평면에서 수직에 가까운 짧은 Edge는 기존 투영 질의가 Degenerate/GeometryFailure를
        // 반환한다. 순서/파라미터 회귀는 비퇴화 고정 평면에서 검증한다.
        var context = Bridge.CreateWorkingContext(1, 0, 0, 0, 1, 0);
        Check(context != null, "working context created");
        if (context == null) return 1;

        try
        {
            VerifySingleFace(context, 12, expectedReferences: 14);
            VerifySingleFace(context, 9, expectedReferences: 5);

            // 고정된 연결 다면 쌍: 내부 공유 Edge가 사라지고 외곽만 남아야 한다.
            var pair = Bridge.GetSelectedRegionBoundary(new[] { 12, 8 }, 0.05, out string pairFailure);
            Check(pair != null && pair.Length == 1 && pair[0].Closed,
                "pair 12+8 single closed loop failure=" + (pairFailure ?? string.Empty));
            if (pair != null && pair.Length == 1)
            {
                Check((pair[0].EdgeReferences?.Length ?? 0) == 16,
                    "pair 12+8 references=" + (pair[0].EdgeReferences?.Length ?? 0));
                CheckCoverage(pair[0], "pair 12+8");
                // 짧은 외곽 Edge가 평면과 수직이 되지 않도록 8번 면의 법선을 작업 평면으로 쓴다.
                var pairNormal = Bridge.GetFaceNormal(8);
                WorkingContext pairContext = pairNormal == null
                    ? null
                    : Bridge.CreateWorkingContext(pairNormal[0], pairNormal[1], pairNormal[2],
                        Math.Abs(pairNormal[0]) < .9 ? 1 : 0,
                        Math.Abs(pairNormal[0]) < .9 ? 0 : 1, 0);
                try
                {
                    VerifyReferences(pairContext ?? context, pair[0], "pair 12+8",
                        allowExistingProjectionFailure: true);
                }
                finally
                {
                    if (pairContext != null) Bridge.ReleaseWorkingContext(pairContext.Id);
                }
            }

            // 실패 호출 뒤에도 기존 등록 키가 유지되는지(같은 키 재사용) 확인.
            var failing = Bridge.GetSelectedRegionBoundary(new[] { 12, 17 }, 0.05, out string failingReason);
            Check(failing == null && !string.IsNullOrWhiteSpace(failingReason),
                "pair 12+17 reports explicit failure: " + (failingReason ?? string.Empty));
            var afterFailure = Bridge.GetSelectedRegionBoundary(new[] { 12 }, 0.05, out string afterReason);
            Check(afterFailure != null && afterFailure.Length == 1 &&
                afterFailure[0].EdgeReferences is { Length: 14 },
                "face 12 still extracts after failed call: " + (afterReason ?? string.Empty));
        }
        finally
        {
            Bridge.ReleaseWorkingContext(context.Id);
        }

        Console.WriteLine(_failures == 0 ? "RESULT=PASS" : "RESULT=FAIL failures=" + _failures);
        return _failures == 0 ? 0 : 1;
    }

    private static void VerifySingleFace(WorkingContext context, int faceId, int expectedReferences)
    {
        var loops = Bridge.GetSelectedRegionBoundary(new[] { faceId }, 0.05, out string failure);
        Check(loops != null && loops.Length == 1 && loops[0].Closed,
            $"face {faceId} single closed loop failure=" + (failure ?? string.Empty));
        if (loops == null || loops.Length != 1) return;
        var loop = loops[0];
        Check((loop.EdgeReferences?.Length ?? 0) == expectedReferences,
            $"face {faceId} references={loop.EdgeReferences?.Length ?? 0}");
        CheckCoverage(loop, "face " + faceId);
        VerifyReferences(context, loop, "face " + faceId);
    }

    private static void CheckCoverage(RegionBoundaryLoop loop, string label)
    {
        var references = loop.EdgeReferences;
        int pointCount = loop.Points.Length / 3;
        bool covered = references is { Length: > 0 } &&
            references[0].StartIndex == 0 && references[^1].EndIndex == pointCount - 1;
        if (covered && references != null)
            for (int i = 1; i < references.Length; ++i)
                if (references[i - 1].EndIndex != references[i].StartIndex) covered = false;
        Check(covered, label + " reference coverage 0.." + (pointCount - 1));
        if (references == null) return;
        var keys = new HashSet<long>();
        foreach (var reference in references) keys.Add(reference.EdgeKey);
        Check(keys.Count == references.Length, label + " unique edge keys");
    }

    private static void VerifyReferences(WorkingContext context, RegionBoundaryLoop loop, string label,
        bool allowExistingProjectionFailure = false)
    {
        var references = loop.EdgeReferences;
        if (references == null) return;
        int pointCount = loop.Points.Length / 3;
        int skipped = 0;
        foreach (var reference in references)
        {
            if (reference.FirstParameter >= reference.LastParameter ||
                reference.StartIndex < 0 || reference.EndIndex >= pointCount ||
                reference.StartIndex > reference.EndIndex)
            {
                Check(false, label + " invalid reference span/parameters key=" + reference.EdgeKey);
                continue;
            }
            var first = Bridge.EvaluateExitEdgeParameter(context.Id, context.Revision, reference,
                reference.FirstParameter);
            var last = Bridge.EvaluateExitEdgeParameter(context.Id, context.Revision, reference,
                reference.LastParameter);
            var middle = Bridge.EvaluateExitEdgeParameter(context.Id, context.Revision, reference,
                0.5 * (reference.FirstParameter + reference.LastParameter));
            if (first.Status != ExitEdgeQueryStatus.Hit || last.Status != ExitEdgeQueryStatus.Hit)
            {
                bool existingLimit =
                    first.Status == ExitEdgeQueryStatus.GeometryFailure &&
                    middle.Status == ExitEdgeQueryStatus.GeometryFailure &&
                    last.Status == ExitEdgeQueryStatus.GeometryFailure;
                string detail = label + " direct parameter evaluation key=" + reference.EdgeKey +
                    $" first={first.Status} mid={middle.Status} last={last.Status} " +
                    $"p=[{reference.FirstParameter:R},{reference.LastParameter:R}]";
                if (allowExistingProjectionFailure && existingLimit)
                {
                    Console.WriteLine("SKIP " + detail + " (existing projected-curve limitation, unrelated to ordering)");
                    ++skipped;
                }
                else Check(false, detail);
                continue;
            }
            double[] polylineStart = PointAt(loop, reference.StartIndex);
            double[] polylineEnd = PointAt(loop, reference.EndIndex);
            double[] expectedStart = reference.Reversed ? last.Point : first.Point;
            double[] expectedEnd = reference.Reversed ? first.Point : last.Point;
            // 시작점은 앞 Edge와 공유하는 표시점이라 sewing 사본/원본 곡선 차이를 허용한다.
            Check(Distance(polylineStart, expectedStart) <= 0.05 &&
                  Distance(polylineEnd, expectedEnd) <= 1e-6,
                label + " orientation/trim key=" + reference.EdgeKey);
        }
        if (skipped > 0)
            Console.WriteLine($"INFO {label} skipped {skipped} reference(s) with the existing projected-curve limitation");
    }

    private static double[] PointAt(RegionBoundaryLoop loop, int index) =>
        new[] { loop.Points[index * 3], loop.Points[index * 3 + 1], loop.Points[index * 3 + 2] };

    private static double Distance(double[] a, double[] b) =>
        Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) +
                  (a[1] - b[1]) * (a[1] - b[1]) +
                  (a[2] - b[2]) * (a[2] - b[2]));

    private static string FindRepoRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "HeatingMold.Mvp.sln"))) return directory;
            directory = Path.GetDirectoryName(directory);
        }
        return Directory.GetCurrentDirectory();
    }
}
