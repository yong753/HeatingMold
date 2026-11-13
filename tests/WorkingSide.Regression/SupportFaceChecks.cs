using System.IO;
using System.Reflection;
using HeatingMold.App;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

/// <summary>
/// 실제 기본 예제의 자기교차/복잡 trim BSpline 면에서 BRepExtrema의 trimmed-face
/// 거리가 유효한 내부 표본을 바깥으로 오판하던 회귀. 표본은 원본 surface 위에
/// 있고 pcurve 포함 판정도 IN이면 스테이징이 성공해야 하며, 표면에서 0.1mm를
/// 넘게 떨어진 점은 계속 거부되어야 한다.
/// </summary>
static class SupportFaceChecks
{
    static int count;
    static void Check(bool value, string label)
    { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); ++count; }

    public static void Run(string fixtureDir)
    {
        string repoRoot = Path.GetFullPath(Path.Combine(fixtureDir, "..", ".."));
        string modelPath = Path.Combine(repoRoot, "resource", "Mold_Cover_Trim.step");
        Check(File.Exists(modelPath), "default example STEP exists for support-face regression");
        if (!File.Exists(modelPath)) return;
        Check(Bridge.LoadStep(modelPath), "default example STEP loads for support-face regression");

        // 261116_Test.json에 저장된 확정 작업평면 프레임 그대로.
        WorkingContext context = Bridge.CreateWorkingContextWithFrame(new[]
        {
            -3966.6891533290936, -843.1964433803546, 1455.9999999778886,
            0.0, 0.0, -1.0,
            0.0, 1.0, 0.0,
            1.0, 0.0, 0.0
        });
        Check(context != null, "saved working frame is restored for support-face regression");
        if (context == null) return;

        int[] faces =
        {
            16, 24, 26, 27, 39, 41, 42, 43, 44, 45, 52, 53, 54, 55, 56,
            59, 60, 66, 67, 68, 69, 71, 72, 74, 75, 76, 77, 78, 79
        };
        double u1 = -483.57421875, u2 = -482.34375, v = -110.00856769355369;
        WorkingHit first = Bridge.MapWorkingPointOntoFaces(context.Id, context.Revision, faces, u1, v);
        WorkingHit second = Bridge.MapWorkingPointOntoFaces(context.Id, context.Revision, faces, u2, v);
        Check(first != null && first.Status == WorkingQueryStatus.Hit && first.Point is { Length: >= 3 },
            "support-face regression datum has a selected-face hit");
        Check(second != null && second.Status == WorkingQueryStatus.Hit && second.Point is { Length: >= 3 },
            "support-face regression neighbour has a selected-face hit");
        if (first?.Point is not { Length: >= 3 } || second?.Point is not { Length: >= 3 })
        {
            Bridge.ReleaseWorkingContext(context.Id);
            return;
        }

        var xyz = new[]
        {
            first.Point[0], first.Point[1], first.Point[2],
            second.Point[0], second.Point[1], second.Point[2]
        };
        int stage = int.MinValue + 9100;
        RoutePieceResult staged = Bridge.StageSurfaceMappedRoutePieceWithFaces(
            context.Id, context.Revision, stage, "HeatingRun", xyz, new[] { first.FaceId, second.FaceId });
        Check(staged != null,
            "point on the trimmed face surface but outside BRepExtrema's euclidean face is accepted");
        Bridge.DiscardWorkingRouteGeometry(context.Id, context.Revision, new[] { stage }, Array.Empty<int>());

        // 표면에서 법선 방향으로 1mm 떨어진 점은 기존 0.1mm 지원면 검사로 거부되어야 한다.
        WorkingHit normalHit = first;
        double nx = normalHit.Normal is { Length: >= 3 } ? normalHit.Normal[0] : 0;
        double ny = normalHit.Normal is { Length: >= 3 } ? normalHit.Normal[1] : 0;
        double nz = normalHit.Normal is { Length: >= 3 } ? normalHit.Normal[2] : 0;
        if (nx * nx + ny * ny + nz * nz < 1e-12) { nx = 1; ny = 0; nz = 0; }
        double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        nx /= length; ny /= length; nz /= length;
        int offStage = stage + 1;
        RoutePieceResult offSurface = Bridge.StageSurfaceMappedRoutePieceWithFaces(
            context.Id, context.Revision, offStage, "HeatingRun",
            new[]
            {
                first.Point[0] + nx, first.Point[1] + ny, first.Point[2] + nz,
                second.Point[0] + nx, second.Point[1] + ny, second.Point[2] + nz
            },
            new[] { first.FaceId, second.FaceId });
        Check(offSurface == null, "point 1mm off the support face is still rejected");
        Bridge.DiscardWorkingRouteGeometry(context.Id, context.Revision, new[] { offStage }, Array.Empty<int>());

        Bridge.ReleaseWorkingContext(context.Id);
        Console.WriteLine($"{count} support-face checks passed.");
    }
}
