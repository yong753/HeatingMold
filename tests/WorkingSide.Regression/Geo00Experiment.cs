using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

/// <summary>실제 Mold와 통제 STEP 형상에서 가시 표면과 삽입 깊이 측정 근거를 수집합니다.</summary>
internal static class Geo00Experiment
{
    private const int GridDivisions = 16;
    private const double MaximumSampleSpacingMm = 5.0;
    private const double RequestedAngularToleranceRad = Math.PI / 36.0;
    private const double MaximumSurfaceDeviationMm = 0.05;
    private const double ConnectionToleranceMm = 0.01;
    private const int MaximumProbeCount = 5000;

    private static readonly AxisDefinition[] CandidateAxes =
    [
        new("+X", new Vec3(1, 0, 0), new Vec3(0, 1, 0)),
        new("-X", new Vec3(-1, 0, 0), new Vec3(0, 1, 0)),
        new("+Y", new Vec3(0, 1, 0), new Vec3(0, 0, 1)),
        new("-Y", new Vec3(0, -1, 0), new Vec3(0, 0, 1)),
        new("+Z", new Vec3(0, 0, 1), new Vec3(1, 0, 0)),
        new("-Z", new Vec3(0, 0, -1), new Vec3(1, 0, 0))
    ];

    private static readonly ControlFixtureScenario[] ControlFixtures =
    [
        new("flat", "flat.stp"),
        new("slope30", "slope30.stp"),
        new("slope60", "slope60.stp"),
        new("bsplineSurface", "bsplineSurface.stp"),
        new("halfCylinder", "halfCylinder.stp"),
        new("sphere", "sphere.stp"),
        new("layers", "layers.stp"),
        new("ring", "ring.stp"),
        new("trimmedVisibility", "trimmedVisibility.stp"),
        new("tangentBack", "tangentBack.stp"),
        new("threePanels", "threePanels.stp")
    ];

    /// <summary>입력 STEP을 읽고 축 스캔·깊이 실험 결과를 재현 가능한 JSON으로 저장합니다.</summary>
    public static void Run(string modelPath, string reportPath)
    {
        Run(modelPath, reportPath, CandidateAxes, maximumDepthPathsPerAxis: 1);
    }

    /// <summary>평판·경사·곡면·가림 fixture의 +Z 측정 보고서를 생성합니다.</summary>
    public static IReadOnlyList<string> RunControlFixtures(
        string fixtureDirectory,
        string reportDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportDirectory);

        string fullFixtureDirectory = Path.GetFullPath(fixtureDirectory);
        if (!Directory.Exists(fullFixtureDirectory))
            throw new DirectoryNotFoundException($"GEO-00 fixture 디렉터리를 찾을 수 없습니다: {fullFixtureDirectory}");

        string fullReportDirectory = Path.Combine(Path.GetFullPath(reportDirectory), "control-fixtures");
        AxisDefinition positiveZ = CandidateAxes.Single(axis => axis.Name == "+Z");
        AxisDefinition[] positiveZOnly = [positiveZ];
        var reports = new List<string>(ControlFixtures.Length);

        foreach (ControlFixtureScenario fixture in ControlFixtures)
        {
            string modelPath = Path.Combine(fullFixtureDirectory, fixture.FileName);
            if (!File.Exists(modelPath))
                throw new FileNotFoundException(
                    $"GEO-00 통제 fixture를 찾을 수 없습니다: {fixture.Name}",
                    modelPath);

            string reportPath = Path.Combine(fullReportDirectory, $"{fixture.Name}.json");
            Run(modelPath, reportPath, positiveZOnly, maximumDepthPathsPerAxis: 3);
            reports.Add(reportPath);
        }

        return reports;
    }

    private static void Run(
        string modelPath,
        string reportPath,
        IReadOnlyList<AxisDefinition> axes,
        int maximumDepthPathsPerAxis)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);
        string fullModelPath = Path.GetFullPath(modelPath);
        string fullReportPath = Path.GetFullPath(reportPath);
        if (!File.Exists(fullModelPath))
            throw new FileNotFoundException("GEO-00 실험 STEP 파일을 찾을 수 없습니다.", fullModelPath);

        var totalTimer = Stopwatch.StartNew();
        var report = new ExperimentReport
        {
            ModelPath = fullModelPath,
            ModelLengthBytes = new FileInfo(fullModelPath).Length,
            ModelSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullModelPath))),
            GridDivisions = GridDivisions,
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        ModelLoadResult load = Bridge.LoadStepDetailed(fullModelPath);
        report.LoadStatus = load.Status.ToString();
        report.ModelRevision = load.ModelRevision;
        report.LoadDiagnostic = load.Diagnostic;
        if (load.Status != ModelLoadStatus.Committed &&
            load.Status != ModelLoadStatus.CommittedPresentationOutOfSync)
        {
            WriteReport(report, fullReportPath, totalTimer);
            throw new InvalidOperationException(
                $"GEO-00 STEP 로드에 실패했습니다: {load.Status} / {load.Diagnostic}");
        }

        ShapeInfo shape = Bridge.GetShapeInfo();
        report.Shape = new ShapeSummary(shape.Solids, shape.Shells, shape.Faces, shape.Planes);

        FaceGeometryFeaturesResult featureResult = Bridge.GetFaceGeometryFeatures();
        report.FaceFeatureStatus = featureResult.Status.ToString();
        if (featureResult.Status != FaceFeatureQueryStatus.Success || featureResult.Features is null)
        {
            report.FaceFeatureDiagnostic = featureResult.Status.ToString();
            WriteReport(report, fullReportPath, totalTimer);
            throw new InvalidOperationException(
                $"GEO-00 Face 특징을 읽지 못했습니다: {featureResult.Status}");
        }

        Dictionary<int, FaceGeometryFeatureData> features = featureResult.Features
            .ToDictionary(feature => feature.FaceIndex);
        report.SurfaceTypes = featureResult.Features
            .GroupBy(feature => feature.SurfaceType ?? "Unknown", StringComparer.Ordinal)
            .Select(group => new SurfaceTypeSummary(
                group.Key,
                group.Count(),
                group.Sum(feature => feature.SurfaceAreaMm2)))
            .OrderByDescending(summary => summary.FaceCount)
            .ThenBy(summary => summary.SurfaceType, StringComparer.Ordinal)
            .ToArray();
        report.LargestFaces = featureResult.Features
            .OrderByDescending(feature => feature.SurfaceAreaMm2)
            .Take(20)
            .Select(feature => new FaceSummary(
                feature.FaceIndex,
                feature.SurfaceType ?? "Unknown",
                feature.SurfaceAreaMm2,
                feature.Center ?? Array.Empty<double>()))
            .ToArray();

        foreach (AxisDefinition axis in axes)
            report.Axes.Add(ProbeAxis(axis, features, maximumDepthPathsPerAxis));

        WriteReport(report, fullReportPath, totalTimer);
        Console.WriteLine($"GEO-00 결과 저장: {fullReportPath}");
        Console.WriteLine($"STEP SHA-256: {report.ModelSha256}");
        Console.WriteLine($"축 스캔: {report.Axes.Count}개, 깊이 질의: {report.Axes.Sum(axis => axis.DepthCases.Count)}개");
    }

    private static AxisProbeReport ProbeAxis(
        AxisDefinition axis,
        IReadOnlyDictionary<int, FaceGeometryFeatureData> features,
        int maximumDepthPaths)
    {
        var timer = Stopwatch.StartNew();
        var result = new AxisProbeReport { Name = axis.Name };
        WorkingSurfaceContext? context;
        try
        {
            context = Bridge.CreateWorkingSurfaceContext(
                axis.Normal.X,
                axis.Normal.Y,
                axis.Normal.Z,
                axis.XAxis.X,
                axis.XAxis.Y,
                axis.XAxis.Z);
        }
        catch (Exception exception)
        {
            result.Diagnostic = exception.Message;
            result.DurationMilliseconds = timer.ElapsedMilliseconds;
            return result;
        }

        if (context is null)
        {
            result.Diagnostic = "작업 좌표계 context 생성에 실패했습니다.";
            result.DurationMilliseconds = timer.ElapsedMilliseconds;
            return result;
        }

        if (context.Triangles is null || context.Triangles.Length < 9)
        {
            result.Diagnostic = "작업 좌표계 메시가 비어 있거나 불완전합니다.";
            Bridge.ReleaseWorkingSurfaceContext(context.Id);
            result.DurationMilliseconds = timer.ElapsedMilliseconds;
            return result;
        }

        result.ContextCreated = true;
        result.MeshDeflectionMm = context.MeshDeflection;
        try
        {
            PlaneFrame frame = ReadFrame(context);
            ProjectedBounds bounds = FindBounds(context.Triangles, frame);
            result.ProjectedBoundsMm = [bounds.MinimumU, bounds.MinimumV, bounds.MaximumU, bounds.MaximumV];

            var hits = new List<GridHit>();
            int missCount = 0;
            int tangentCount = 0;
            int failureCount = 0;
            for (int row = 0; row <= GridDivisions; row++)
            {
                double v = Interpolate(bounds.MinimumV, bounds.MaximumV, row, GridDivisions);
                for (int column = 0; column <= GridDivisions; column++)
                {
                    double u = Interpolate(bounds.MinimumU, bounds.MaximumU, column, GridDivisions);
                    WorkingSurfaceHit hit = Bridge.QueryWorkingSurface(
                        context.Id,
                        context.Revision,
                        u,
                        v);
                    switch (hit.Status)
                    {
                        case WorkingSurfaceQueryStatus.Hit:
                            if (hit.Point is { Length: 3 } point && hit.FaceUv is { Length: 2 } faceUv)
                            {
                                hits.Add(new GridHit(
                                    u,
                                    v,
                                    hit.FaceId,
                                    point,
                                    faceUv,
                                    hit.Normal ?? Array.Empty<double>()));
                            }
                            else
                            {
                                failureCount++;
                            }
                            break;
                        case WorkingSurfaceQueryStatus.Miss:
                            missCount++;
                            break;
                        case WorkingSurfaceQueryStatus.Tangent:
                            tangentCount++;
                            break;
                        default:
                            failureCount++;
                            break;
                    }
                }
            }

            result.GridSampleCount = (GridDivisions + 1) * (GridDivisions + 1);
            result.HitCount = hits.Count;
            result.MissCount = missCount;
            result.TangentCount = tangentCount;
            result.FailureCount = failureCount;
            result.TopVisibleFaces = hits
                .GroupBy(hit => hit.FaceIndex)
                .Select(group =>
                {
                    features.TryGetValue(group.Key, out FaceGeometryFeatureData? feature);
                    return new VisibleFaceSummary(
                        group.Key,
                        feature?.SurfaceType ?? "Unknown",
                        feature?.SurfaceAreaMm2 ?? 0,
                        group.Count());
                })
                .OrderByDescending(face => face.GridHitCount)
                .ThenByDescending(face => face.SurfaceAreaMm2)
                .Take(20)
                .ToArray();

            IReadOnlyList<GridHitPair> candidates = FindSameFacePaths(
                context,
                hits,
                features,
                maximumDepthPaths);
            if (candidates.Count == 0)
            {
                result.DepthPathStatus = "NotFound";
                result.DepthPathDiagnostic = "격자에서 같은 first-hit Face로 이어지고 중간 표본도 통과하는 직선 경로를 찾지 못했습니다.";
            }
            else
            {
                result.DepthPathStatus = "Selected";
                result.SelectedDepthPaths = candidates
                    .Select(candidate => new DepthPathSummary(
                        candidate.PathKind,
                        candidate.FaceIndex,
                        candidate.SurfaceType,
                        candidate.Start.DatumPoint,
                        candidate.End.DatumPoint,
                        Distance(
                            candidate.Start.U,
                            candidate.Start.V,
                            candidate.End.U,
                            candidate.End.V)))
                    .ToArray();

                foreach (GridHitPair candidate in candidates)
                    result.DepthCases.AddRange(MeasureDepthCases(context, candidate));
            }
        }
        catch (Exception exception)
        {
            result.Diagnostic = exception.Message;
            if (result.DepthPathStatus == "NotEvaluated")
                result.DepthPathStatus = "Failed";
        }
        finally
        {
            Bridge.ReleaseWorkingSurfaceContext(context.Id);
            result.DurationMilliseconds = timer.ElapsedMilliseconds;
        }

        return result;
    }

    private static IReadOnlyList<GridHitPair> FindSameFacePaths(
        WorkingSurfaceContext context,
        IReadOnlyList<GridHit> hits,
        IReadOnlyDictionary<int, FaceGeometryFeatureData> features,
        int maximumPathCount)
    {
        var selectedPaths = new List<GridHitPair>(maximumPathCount);
        string[] pathKinds = ["Longest", "AlongWorkingU", "AlongWorkingV"];

        IEnumerable<IGrouping<int, GridHit>> faceGroups = hits
            .GroupBy(hit => hit.FaceIndex)
            .OrderByDescending(group => group.Count())
            .Take(8);

        foreach (IGrouping<int, GridHit> faceGroup in faceGroups)
        {
            GridHit[] faceHits = faceGroup.ToArray();
            var pairs = new List<(GridHit Start, GridHit End, double Length)>();
            for (int firstIndex = 0; firstIndex < faceHits.Length; firstIndex++)
            {
                for (int secondIndex = firstIndex + 1; secondIndex < faceHits.Length; secondIndex++)
                {
                    GridHit first = faceHits[firstIndex];
                    GridHit second = faceHits[secondIndex];
                    double length = Distance(first.U, first.V, second.U, second.V);
                    if (length > 0)
                        pairs.Add((first, second, length));
                }
            }

            foreach (string pathKind in pathKinds)
            {
                IEnumerable<(GridHit Start, GridHit End, double Length)> rankedPairs = pathKind switch
                {
                    "AlongWorkingU" => pairs
                        .Where(pair => pair.Start.V == pair.End.V)
                        .OrderByDescending(pair => Math.Abs(pair.End.U - pair.Start.U))
                        .ThenByDescending(pair => pair.Length),
                    "AlongWorkingV" => pairs
                        .Where(pair => pair.Start.U == pair.End.U)
                        .OrderByDescending(pair => Math.Abs(pair.End.V - pair.Start.V))
                        .ThenByDescending(pair => pair.Length),
                    _ => pairs
                        .OrderByDescending(pair => pair.Length)
                        .ThenByDescending(pair => Math.Abs(pair.End.U - pair.Start.U))
                };

                foreach ((GridHit start, GridHit end, double _) in rankedPairs.Take(12))
                {
                    if (HasSameEndpoints(selectedPaths, faceGroup.Key, start, end) ||
                        !IsContinuousFirstHitOnFace(context, faceGroup.Key, start, end))
                    {
                        continue;
                    }

                    features.TryGetValue(faceGroup.Key, out FaceGeometryFeatureData? feature);
                    selectedPaths.Add(new GridHitPair(
                        pathKind,
                        faceGroup.Key,
                        feature?.SurfaceType ?? "Unknown",
                        start,
                        end));
                    if (selectedPaths.Count >= maximumPathCount)
                        return selectedPaths;
                }
            }
        }

        return selectedPaths;
    }

    private static bool HasSameEndpoints(
        IReadOnlyList<GridHitPair> paths,
        int faceIndex,
        GridHit start,
        GridHit end) => paths.Any(path =>
            path.FaceIndex == faceIndex &&
            ((path.Start.U == start.U && path.Start.V == start.V &&
             path.End.U == end.U && path.End.V == end.V) ||
            (path.Start.U == end.U && path.Start.V == end.V &&
             path.End.U == start.U && path.End.V == start.V)));

    private static bool IsContinuousFirstHitOnFace(
        WorkingSurfaceContext context,
        int faceIndex,
        GridHit start,
        GridHit end)
    {
        const int verificationIntervals = 12;
        for (int index = 1; index < verificationIntervals; index++)
        {
            double parameter = (double)index / verificationIntervals;
            double u = Interpolate(start.U, end.U, parameter);
            double v = Interpolate(start.V, end.V, parameter);
            WorkingSurfaceHit hit = Bridge.QueryWorkingSurface(
                context.Id,
                context.Revision,
                u,
                v);
            if (hit.Status != WorkingSurfaceQueryStatus.Hit || hit.FaceId != faceIndex)
                return false;
        }

        return true;
    }

    private static IEnumerable<DepthCaseSummary> MeasureDepthCases(
        WorkingSurfaceContext context,
        GridHitPair path)
    {
        double[] surfacePath = [path.Start.U, path.Start.V, path.End.U, path.End.V];
        int[] selectedFaceIds = [path.FaceIndex];
        var cases = new List<DepthCaseSummary>();
        foreach (double wireDiameterMm in new[] { 0.15, 0.30 })
        {
            foreach (double insertionDepthMm in new[] { 0.0, 0.15, 1.0 })
            {
                var timer = Stopwatch.StartNew();
                CurveDepthAdjustmentResultData adjustment = Bridge.AdjustWorkingCurveInsertionDepth(
                    context.Id,
                    context.Revision,
                    surfacePath,
                    selectedFaceIds,
                    path.FaceIndex,
                    path.Start.FaceU,
                    path.Start.FaceV,
                    path.Start.ModelPoint,
                    wireDiameterMm,
                    insertionDepthMm,
                    MaximumSampleSpacingMm,
                    RequestedAngularToleranceRad,
                    MaximumSurfaceDeviationMm,
                    ConnectionToleranceMm,
                    MaximumProbeCount);

                CurveDepthEnvelopeResultData? envelope = null;
                if (adjustment.Status == WorkingCurveDepthEnvelopeStatus.Success &&
                    adjustment.CenterlineSamples is { Length: >= 6 })
                {
                    envelope = Bridge.MeasureWorkingCurveDepthEnvelope(
                        context.Id,
                        context.Revision,
                        adjustment.CenterlineSamples,
                        selectedFaceIds,
                        path.FaceIndex,
                        path.Start.FaceU,
                        path.Start.FaceV,
                        path.Start.ModelPoint,
                        wireDiameterMm,
                        insertionDepthMm,
                        MaximumSampleSpacingMm,
                        RequestedAngularToleranceRad,
                        MaximumSurfaceDeviationMm,
                        ConnectionToleranceMm,
                        MaximumProbeCount);
                }

                timer.Stop();
                cases.Add(new DepthCaseSummary(
                    path.PathKind,
                    path.FaceIndex,
                    path.SurfaceType,
                    path.Start.DatumPoint,
                    path.End.DatumPoint,
                    path.Start.ModelPoint,
                    path.End.ModelPoint,
                    path.Start.FaceUv,
                    path.End.FaceUv,
                    path.Start.Normal,
                    path.End.Normal,
                    Distance(path.Start.U, path.Start.V, path.End.U, path.End.V),
                    wireDiameterMm,
                    insertionDepthMm,
                    adjustment.Status.ToString(),
                    adjustment.MaximumDepthResidualMm,
                    adjustment.ErrorBoundMm,
                    adjustment.ProbeCount,
                    adjustment.Diagnostic,
                    envelope?.Status.ToString(),
                    envelope?.MinimumInsertionDepthMm,
                    envelope?.MaximumInsertionDepthMm,
                    envelope?.ErrorBoundMm,
                    envelope?.ProbeCount,
                    envelope?.Diagnostic,
                    timer.ElapsedMilliseconds));
            }
        }

        return cases;
    }

    private static PlaneFrame ReadFrame(WorkingSurfaceContext context)
    {
        double[] frame = context.Frame
            ?? throw new InvalidOperationException("Native 작업 좌표계 frame이 반환되지 않았습니다.");
        if (frame.Length != 12)
            throw new InvalidOperationException("Native 작업 좌표계 frame은 원점과 세 축의 12개 값이어야 합니다.");

        return new PlaneFrame(
            ReadVector(frame, 0),
            ReadVector(frame, 3),
            ReadVector(frame, 6),
            ReadVector(frame, 9));
    }

    private static ProjectedBounds FindBounds(double[] triangles, PlaneFrame frame)
    {
        double minimumU = double.PositiveInfinity;
        double minimumV = double.PositiveInfinity;
        double maximumU = double.NegativeInfinity;
        double maximumV = double.NegativeInfinity;
        for (int index = 0; index < triangles.Length; index += 3)
        {
            Vec3 projected = Project(ReadVector(triangles, index), frame);
            minimumU = Math.Min(minimumU, projected.X);
            minimumV = Math.Min(minimumV, projected.Y);
            maximumU = Math.Max(maximumU, projected.X);
            maximumV = Math.Max(maximumV, projected.Y);
        }

        if (!double.IsFinite(minimumU) || !double.IsFinite(minimumV) ||
            !double.IsFinite(maximumU) || !double.IsFinite(maximumV) ||
            maximumU <= minimumU || maximumV <= minimumV)
        {
            throw new InvalidOperationException("작업 축 투영에서 유한한 모델 범위를 얻지 못했습니다.");
        }

        return new ProjectedBounds(minimumU, minimumV, maximumU, maximumV);
    }

    private static Vec3 Project(Vec3 point, PlaneFrame frame)
    {
        Vec3 offset = point - frame.Origin;
        return new Vec3(Vec3.Dot(offset, frame.XAxis), Vec3.Dot(offset, frame.YAxis), Vec3.Dot(offset, frame.Normal));
    }

    private static Vec3 ReadVector(double[] values, int offset) =>
        new(values[offset], values[offset + 1], values[offset + 2]);

    private static double Interpolate(double start, double end, int index, int divisions) =>
        Interpolate(start, end, (double)index / divisions);

    private static double Interpolate(double start, double end, double parameter) =>
        start + (end - start) * parameter;

    private static double Distance(double firstU, double firstV, double secondU, double secondV)
    {
        double deltaU = secondU - firstU;
        double deltaV = secondV - firstV;
        return Math.Sqrt(deltaU * deltaU + deltaV * deltaV);
    }

    private static void WriteReport(ExperimentReport report, string reportPath, Stopwatch timer)
    {
        timer.Stop();
        report.CompletedAtUtc = DateTimeOffset.UtcNow;
        report.DurationMilliseconds = timer.ElapsedMilliseconds;
        string? directory = Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
    }

    /// <summary>축 방향과 작업 평면의 고정 X축을 보관합니다.</summary>
    private sealed record AxisDefinition(string Name, Vec3 Normal, Vec3 XAxis);

    /// <summary>Native fixture 파일과 보고서 이름을 연결합니다.</summary>
    private sealed record ControlFixtureScenario(string Name, string FileName);

    /// <summary>작업 평면 프레임의 원점과 세 단위축입니다.</summary>
    private sealed record PlaneFrame(Vec3 Origin, Vec3 XAxis, Vec3 YAxis, Vec3 Normal);

    /// <summary>삼각 메시의 평면 투영 범위(mm)입니다.</summary>
    private sealed record ProjectedBounds(double MinimumU, double MinimumV, double MaximumU, double MaximumV);

    /// <summary>first-hit 격자 표본 하나와 원본 Face 참조를 보관합니다.</summary>
    private sealed record GridHit(
        double U,
        double V,
        int FaceIndex,
        double[] ModelPoint,
        double[] FaceUv,
        double[] Normal)
    {
        public double[] DatumPoint => [U, V];
        public double FaceU => FaceUv[0];
        public double FaceV => FaceUv[1];
    }

    /// <summary>같은 first-hit Face 안에서 검사한 직선 작업 경로의 양 끝입니다.</summary>
    private sealed record GridHitPair(
        string PathKind,
        int FaceIndex,
        string SurfaceType,
        GridHit Start,
        GridHit End);

    /// <summary>전체 STEP 모델의 재현 정보와 스캔 결과를 직렬화합니다.</summary>
    private sealed class ExperimentReport
    {
        public string ModelPath { get; set; } = string.Empty;
        public long ModelLengthBytes { get; set; }
        public string ModelSha256 { get; set; } = string.Empty;
        public int GridDivisions { get; set; }
        public DateTimeOffset StartedAtUtc { get; set; }
        public DateTimeOffset CompletedAtUtc { get; set; }
        public long DurationMilliseconds { get; set; }
        public string LoadStatus { get; set; } = string.Empty;
        public string? LoadDiagnostic { get; set; }
        public long ModelRevision { get; set; }
        public string? FaceFeatureStatus { get; set; }
        public string? FaceFeatureDiagnostic { get; set; }
        public ShapeSummary? Shape { get; set; }
        public SurfaceTypeSummary[] SurfaceTypes { get; set; } = [];
        public FaceSummary[] LargestFaces { get; set; } = [];
        public List<AxisProbeReport> Axes { get; } = [];
    }

    /// <summary>STEP 원형의 전체 topology 규모입니다.</summary>
    private sealed record ShapeSummary(int Solids, int Shells, int Faces, int Planes);

    /// <summary>OCCT 표면 종류별 면 개수와 원본 면적 합계입니다.</summary>
    private sealed record SurfaceTypeSummary(string SurfaceType, int FaceCount, double TotalAreaMm2);

    /// <summary>면적이 큰 원본 Face의 중심과 종류입니다.</summary>
    private sealed record FaceSummary(int FaceIndex, string SurfaceType, double AreaMm2, double[] Center);

    /// <summary>한 작업축의 투영 격자 first-hit 분포와 깊이 질의입니다.</summary>
    private sealed class AxisProbeReport
    {
        public string Name { get; set; } = string.Empty;
        public bool ContextCreated { get; set; }
        public string? Diagnostic { get; set; }
        public double MeshDeflectionMm { get; set; }
        public double[] ProjectedBoundsMm { get; set; } = [];
        public int GridSampleCount { get; set; }
        public int HitCount { get; set; }
        public int MissCount { get; set; }
        public int TangentCount { get; set; }
        public int FailureCount { get; set; }
        public long DurationMilliseconds { get; set; }
        public VisibleFaceSummary[] TopVisibleFaces { get; set; } = [];
        public string DepthPathStatus { get; set; } = "NotEvaluated";
        public string? DepthPathDiagnostic { get; set; }
        public DepthPathSummary[] SelectedDepthPaths { get; set; } = [];
        public List<DepthCaseSummary> DepthCases { get; } = [];
    }

    /// <summary>깊이 보정에 사용한 같은 Face 직선 후보 경로를 보관합니다.</summary>
    private sealed record DepthPathSummary(
        string PathKind,
        int FaceIndex,
        string SurfaceType,
        double[] StartWorkingUv,
        double[] EndWorkingUv,
        double LengthMm);

    /// <summary>작업축 투영 격자에서 가시성이 가장 높았던 원본 Face입니다.</summary>
    private sealed record VisibleFaceSummary(
        int FaceIndex,
        string SurfaceType,
        double SurfaceAreaMm2,
        int GridHitCount);

    /// <summary>깊이 보정과 독립 envelope 재측정의 원시 수치입니다.</summary>
    private sealed record DepthCaseSummary(
        string PathKind,
        int FaceIndex,
        string SurfaceType,
        double[] StartWorkingUv,
        double[] EndWorkingUv,
        double[] StartModelPoint,
        double[] EndModelPoint,
        double[] StartFaceUv,
        double[] EndFaceUv,
        double[] StartSurfaceNormal,
        double[] EndSurfaceNormal,
        double PathLengthMm,
        double WireDiameterMm,
        double RequiredInsertionDepthMm,
        string AdjustmentStatus,
        double MaximumDepthResidualMm,
        double AdjustmentErrorBoundMm,
        int AdjustmentProbeCount,
        string? AdjustmentDiagnostic,
        string? EnvelopeStatus,
        double? MinimumMeasuredDepthMm,
        double? MaximumMeasuredDepthMm,
        double? EnvelopeErrorBoundMm,
        int? EnvelopeProbeCount,
        string? EnvelopeDiagnostic,
        long DurationMilliseconds);
}
