using System;
using System.IO;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

/// <summary>
/// 계산용 1mm 재봉 사본은 시험 전용 opt-in 이다. 제품 기본 표면 조회는 원본
/// g_shape/g_faces 여야 하고, 재봉 사본을 켠 경우에만 같은 intersector를 공유하는
/// 표시 판정(WorkingVisibleNative)이 그 사본을 쓴다.
///
/// 여기서는 기본 예제 STEP + 저장된 작업 프레임만으로(사용자 프로젝트 파일 없이)
/// 문서 1단계 gate의 실패 지점을 고정한다: 원본 모드의 exact 실패 uv는 선택/전역
/// 조회가 모두 Miss이고, u±0.001은 원본 면 hit이며, 재봉 모드에서만 관측된
/// GeometryFailure 대역이 원본 모드에서는 정상 hit 이다. 값은 이 STEP(고정 SHA)에
/// 대해 실측된 것이다.
/// </summary>
static class SurfaceQueryModeChecks
{
    static int count;
    static void Check(bool value, string label)
    { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); ++count; }

    // 261116_Test2.json workingSide 의 확정 프레임.
    static readonly double[] SavedFrame =
    {
        -3966.6891533290936, -843.1964433803546, 1455.9999999778886,
        0.0, 0.0, -1.0,
        0.0, 1.0, 0.0,
        1.0, 0.0, 0.0
    };

    static readonly int[] AreaFaces =
    {
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26,
        28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 40, 41, 44, 45, 46, 47, 48, 49, 50, 51, 57, 58,
        61, 62, 63, 64, 65, 70, 71, 73, 76
    };

    const double FailU = -489.81391714892175;
    const double FailV = -32.960668248954725;

    // 원본 모드에서 정상 hit, 재봉 모드에서 GeometryFailure 로 관측된 대역.
    static readonly double[] FailureBand =
    {
        -32.955, -32.9545, -32.954, -32.9535, -32.953, -32.9525
    };

    public static void Run(string fixtureDir)
    {
        string repoRoot = Path.GetFullPath(Path.Combine(fixtureDir, "..", ".."));
        string modelPath = Path.Combine(repoRoot, "resource", "Mold_Cover_Trim.step");
        Check(File.Exists(modelPath), "default example STEP exists for surface-query mode checks");
        if (!File.Exists(modelPath)) return;

        string saved = Environment.GetEnvironmentVariable("HEATINGMOLD_USE_CALC_SEWN_COPY");
        try
        {
            // ---- opt-in 값 의미: 정확히 ASCII '1' 한 글자만 켠다 ----
            var values = new (string Value, bool Enabled)[]
            {
                (null, false),      // 미설정
                (string.Empty, false),
                ("0", false),
                ("false", false),
                ("true", false),
                ("off", false),
                ("2", false),
                ("11", false),
                ("1 ", false),
                (" 1", false),
                ("123456789012345", false), // 버퍼보다 긴 값
                ("1", true)
            };
            foreach ((string value, bool enabled) in values)
            {
                Environment.SetEnvironmentVariable("HEATINGMOLD_USE_CALC_SEWN_COPY", value);
                Check(Bridge.LoadStep(modelPath), "default example loads with opt-in value '" + (value ?? "<unset>") + "'");
                Check(Bridge.IsCalcSewnSurfaceQueryActive() == enabled,
                    "opt-in value '" + (value ?? "<unset>") + "' gives active=" + enabled);
            }

            // ---- 기본(원본) 모드의 실패 지점 고정 ----
            Environment.SetEnvironmentVariable("HEATINGMOLD_USE_CALC_SEWN_COPY", null);
            Check(Bridge.LoadStep(modelPath), "default example loads with the product default query mode");
            Check(!Bridge.IsCalcSewnSurfaceQueryActive(),
                "default surface query mode is the original shape (sewn copy isolated)");
            string originalMode = Bridge.GetCalcSewnSurfaceQueryDiagnostic();
            Console.WriteLine("  diagnostic: " + originalMode);
            Check(originalMode.Contains("requested=0") && originalMode.Contains("active=original") &&
                  originalMode.Contains("faces=80/80"),
                "default query diagnostic reports the original 80 faces");

            WorkingContext context = Bridge.CreateWorkingContextWithFrame(SavedFrame);
            Check(context != null, "saved working frame is available for the failure-point checks");
            if (context == null) return;

            WorkingHit exact = Bridge.MapWorkingPointOntoFaces(context.Id, context.Revision, AreaFaces, FailU, FailV);
            Check(exact != null && exact.Status == WorkingQueryStatus.Miss,
                "original mode: exact gate uv is a selected-face Miss");
            WorkingHit exactGlobal = Bridge.QueryWorkingSurface(context.Id, context.Revision, FailU, FailV);
            Check(exactGlobal != null && exactGlobal.Status == WorkingQueryStatus.Miss,
                "original mode: exact gate uv is a global Miss");

            WorkingHit plus = Bridge.MapWorkingPointOntoFaces(context.Id, context.Revision, AreaFaces, FailU + 0.001, FailV);
            Check(plus != null && plus.Status == WorkingQueryStatus.Hit && plus.FaceId == 58 &&
                  plus.Point is { Length: >= 3 },
                "original mode: u+0.001 resolves on original face 58");
            WorkingHit minus = Bridge.MapWorkingPointOntoFaces(context.Id, context.Revision, AreaFaces, FailU - 0.001, FailV);
            Check(minus != null && minus.Status == WorkingQueryStatus.Hit && minus.FaceId == 57 &&
                  minus.Point is { Length: >= 3 },
                "original mode: u-0.001 resolves on original face 57");

            foreach (double v in FailureBand)
            {
                WorkingHit band = Bridge.MapWorkingPointOntoFaces(context.Id, context.Revision, AreaFaces, FailU, v);
                Check(band != null && band.Status == WorkingQueryStatus.Hit && band.FaceId == 57,
                    "original mode: band v=" + v.ToString("R") + " is a Hit on original face 57");
            }

            // 재봉 모드에서만 나타나던 커널 예외 대역이 원본 모드에서는 없다는 대조.
            WorkingHit outsideBand = Bridge.MapWorkingPointOntoFaces(context.Id, context.Revision, AreaFaces, FailU, -32.952);
            Check(outsideBand != null && outsideBand.Status == WorkingQueryStatus.Hit && outsideBand.FaceId == 61,
                "original mode: control v=-32.952 is a Hit on original face 61");

            // 환경변수만 바꿔서는 로드된 조회 소스가 바뀌지 않는다(진단도 로드 시점 스냅샷).
            Environment.SetEnvironmentVariable("HEATINGMOLD_USE_CALC_SEWN_COPY", "1");
            Check(!Bridge.IsCalcSewnSurfaceQueryActive(),
                "changing the opt-in without reloading keeps the original query active");
            string stillOriginal = Bridge.GetCalcSewnSurfaceQueryDiagnostic();
            Check(stillOriginal.Contains("requested=0") && stillOriginal.Contains("active=original") &&
                  stillOriginal.Contains("faces=80/80"),
                "diagnostic keeps the loaded snapshot after an environment-only change");
            WorkingHit stillOriginalBand = Bridge.MapWorkingPointOntoFaces(context.Id, context.Revision, AreaFaces,
                FailU, FailureBand[0]);
            Check(stillOriginalBand != null && stillOriginalBand.Status == WorkingQueryStatus.Hit &&
                  stillOriginalBand.FaceId == 57,
                "original query results are unchanged by an environment-only change");
            Environment.SetEnvironmentVariable("HEATINGMOLD_USE_CALC_SEWN_COPY", null);

            // ---- 표시 판정은 같은 intersector 를 공유한다 ----
            Check(Bridge.ReturnToWorkingFront(context.Id, context.Revision),
                "working front view is available for the visibility check");
            WorkingHit visibleDatum = Bridge.MapWorkingPointOntoFaces(context.Id, context.Revision, AreaFaces,
                -482.34375, -110.00856769355369);
            Check(visibleDatum != null && visibleDatum.Status == WorkingQueryStatus.Hit &&
                  visibleDatum.Point is { Length: >= 3 },
                "original mode resolves the visibility datum on the selected faces");
            if (visibleDatum?.Point is not { Length: >= 3 })
            {
                Bridge.ReleaseWorkingContext(context.Id);
                return;
            }
            double[] originalPoint = (double[])visibleDatum.Point.Clone();
            int originalFace = visibleDatum.FaceId;
            bool originalVisible = Bridge.IsWorkingPointVisible(context.Id, context.Revision,
                originalPoint[0], originalPoint[1], originalPoint[2], 0.001);

            // ---- opt-in 재봉 모드: 같은 실패 지점 + 재봉 전용 대역 ----
            Environment.SetEnvironmentVariable("HEATINGMOLD_USE_CALC_SEWN_COPY", "1");
            Check(Bridge.LoadStep(modelPath), "default example loads with the opt-in sewn copy");
            Check(Bridge.IsCalcSewnSurfaceQueryActive(),
                "opt-in value '1' activates the sewn surface query copy");
            string sewnMode = Bridge.GetCalcSewnSurfaceQueryDiagnostic();
            Console.WriteLine("  diagnostic: " + sewnMode);
            Check(sewnMode.Contains("requested=1") && sewnMode.Contains("active=sewn") &&
                  sewnMode.Contains("faces=80/80"),
                "sewn query diagnostic keeps the original 80-face identity mapping");

            WorkingContext sewnContext = Bridge.CreateWorkingContextWithFrame(SavedFrame);
            Check(sewnContext != null, "saved working frame is available in the sewn query mode");
            if (sewnContext == null) return;

            WorkingHit sewnExact = Bridge.MapWorkingPointOntoFaces(sewnContext.Id, sewnContext.Revision, AreaFaces, FailU, FailV);
            Check(sewnExact != null && sewnExact.Status == WorkingQueryStatus.Miss,
                "sewn mode: exact gate uv is still a selected-face Miss");
            WorkingHit sewnExactGlobal = Bridge.QueryWorkingSurface(sewnContext.Id, sewnContext.Revision, FailU, FailV);
            Check(sewnExactGlobal != null && sewnExactGlobal.Status == WorkingQueryStatus.Miss,
                "sewn mode: exact gate uv is still a global Miss");
            foreach (double v in FailureBand)
            {
                WorkingHit band = Bridge.MapWorkingPointOntoFaces(sewnContext.Id, sewnContext.Revision, AreaFaces, FailU, v);
                Check(band != null && band.Status == WorkingQueryStatus.GeometryFailure,
                    "sewn mode: band v=" + v.ToString("R") + " is a GeometryFailure (kernel exception, not a model state)");
            }

            // 재봉 로드 후 환경변수만 지워도 로드된 소스는 그대로다(진단도 스냅샷).
            Environment.SetEnvironmentVariable("HEATINGMOLD_USE_CALC_SEWN_COPY", null);
            Check(Bridge.IsCalcSewnSurfaceQueryActive(),
                "clearing the opt-in without reloading keeps the sewn query active");
            string stillSewn = Bridge.GetCalcSewnSurfaceQueryDiagnostic();
            Check(stillSewn.Contains("requested=1") && stillSewn.Contains("active=sewn") &&
                  stillSewn.Contains("faces=80/80"),
                "sewn diagnostic keeps the loaded snapshot after an environment-only change");
            WorkingHit stillSewnBand = Bridge.MapWorkingPointOntoFaces(sewnContext.Id, sewnContext.Revision, AreaFaces,
                FailU, FailureBand[0]);
            Check(stillSewnBand != null && stillSewnBand.Status == WorkingQueryStatus.GeometryFailure,
                "sewn query results are unchanged by an environment-only change");
            Environment.SetEnvironmentVariable("HEATINGMOLD_USE_CALC_SEWN_COPY", "1");

            WorkingHit sewnDatum = Bridge.MapWorkingPointOntoFaces(sewnContext.Id, sewnContext.Revision, AreaFaces,
                -482.34375, -110.00856769355369);
            Check(sewnDatum != null && sewnDatum.Status == WorkingQueryStatus.Hit &&
                  sewnDatum.Point is { Length: >= 3 },
                "sewn query mode still resolves the visibility datum");
            if (sewnDatum?.Point is { Length: >= 3 })
            {
                double dx = sewnDatum.Point[0] - originalPoint[0];
                double dy = sewnDatum.Point[1] - originalPoint[1];
                double dz = sewnDatum.Point[2] - originalPoint[2];
                Check(Math.Sqrt(dx * dx + dy * dy + dz * dz) <= 1e-6,
                    "both query modes return the same 3D point for the same datum");
                Check(sewnDatum.FaceId == originalFace,
                    "both query modes report the same support face for the same datum");
                Check(Bridge.ReturnToWorkingFront(sewnContext.Id, sewnContext.Revision),
                    "working front view is available in the sewn query mode");
                bool sewnVisible = Bridge.IsWorkingPointVisible(sewnContext.Id, sewnContext.Revision,
                    sewnDatum.Point[0], sewnDatum.Point[1], sewnDatum.Point[2], 0.001);
                Check(sewnVisible == originalVisible,
                    "display visibility uses the same intersector in both query modes");
                Console.WriteLine("  visibility original=" + originalVisible + " sewn=" + sewnVisible);
            }

            // ---- opt-in 해제: 재로드에서만 적용되고 원본 조회로 돌아간다 ----
            Environment.SetEnvironmentVariable("HEATINGMOLD_USE_CALC_SEWN_COPY", null);
            Check(Bridge.LoadStep(modelPath), "default example reloads with the product default query mode");
            Check(!Bridge.IsCalcSewnSurfaceQueryActive(),
                "clearing the environment variable restores the original surface query");
            WorkingContext reloaded = Bridge.CreateWorkingContextWithFrame(SavedFrame);
            Check(reloaded != null, "working frame is rebuilt after the query mode reload");
            if (reloaded != null)
            {
                WorkingHit reloadedExact = Bridge.MapWorkingPointOntoFaces(reloaded.Id, reloaded.Revision, AreaFaces, FailU, FailV);
                Check(reloadedExact != null && reloadedExact.Status == WorkingQueryStatus.Miss,
                    "reloaded original mode reproduces the exact gate uv Miss");
                WorkingHit reloadedBand = Bridge.MapWorkingPointOntoFaces(reloaded.Id, reloaded.Revision, AreaFaces,
                    FailU, FailureBand[0]);
                Check(reloadedBand != null && reloadedBand.Status == WorkingQueryStatus.Hit &&
                      reloadedBand.FaceId == 57,
                    "reloaded original mode removes the sewn-only GeometryFailure band");
                Bridge.ReleaseWorkingContext(reloaded.Id);
            }
            Bridge.ReleaseWorkingContext(sewnContext.Id);
            Bridge.ReleaseWorkingContext(context.Id);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HEATINGMOLD_USE_CALC_SEWN_COPY", saved);
        }

        Console.WriteLine($"{count} surface-query mode checks passed.");
    }
}
