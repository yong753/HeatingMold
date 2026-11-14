namespace HeatingMold.Core.Geometry;

/// <summary>표면 조회 한 건의 typed 결과. Miss(표면 없음)와 Failure(거부)를 구분한다.</summary>
public enum SurfaceQueryOutcome
{
    /// <summary>표면을 찾지 못했다. 근사 연결 대상이 될 수 있는 유일한 상태다.</summary>
    Miss = 0,
    Hit = 1,
    /// <summary>커널 오류·오래된 컨텍스트·비유한 값 등 근사로 덮을 수 없는 실패.</summary>
    Failure = 2,
}

/// <summary>
/// 표면 조회 결과. 문자열 부분 검사나 마지막 오류 공유로 분기하지 않도록 상태를 값으로 전달한다.
/// </summary>
public readonly record struct SurfaceQueryResult(SurfaceQueryOutcome Outcome, SurfaceMappedSample Sample, string Reason)
{
    public bool IsHit => Outcome == SurfaceQueryOutcome.Hit;

    public static SurfaceQueryResult FromHit(SurfaceMappedSample sample) =>
        new(SurfaceQueryOutcome.Hit, sample, string.Empty);

    public static SurfaceQueryResult FromMiss(string reason) =>
        new(SurfaceQueryOutcome.Miss, default, reason ?? string.Empty);

    public static SurfaceQueryResult FromFailure(string reason) =>
        new(SurfaceQueryOutcome.Failure, default, reason ?? string.Empty);
}

/// <summary>승인된 근사 연결 한 곳. 좌표는 실제 Hit 표본의 작업평면 좌표다.</summary>
public readonly record struct SurfaceApproximationBridge(
    int SegmentIndex, Vec2 Left, Vec2 Right, double AdvanceMm, double Gap3DMm);

/// <summary>
/// 경로 매핑 한 번에서 승인된 근사 연결의 집계. 표시/진단용이며 판정 근거가 아니다.
/// </summary>
public readonly record struct SurfaceApproximationReport(IReadOnlyList<SurfaceApproximationBridge> Bridges)
{
    public static SurfaceApproximationReport None { get; } = new(Array.Empty<SurfaceApproximationBridge>());

    public bool Used => Bridges is { Count: > 0 };
    public int BridgeCount => Bridges?.Count ?? 0;

    public double TotalAdvanceMm
    {
        get
        {
            double total = 0;
            if (Bridges != null) foreach (var bridge in Bridges) total += bridge.AdvanceMm;
            return total;
        }
    }

    public double MaxAdvanceMm
    {
        get
        {
            double max = 0;
            if (Bridges != null) foreach (var bridge in Bridges) max = Math.Max(max, bridge.AdvanceMm);
            return max;
        }
    }

    public double MaxGap3DMm
    {
        get
        {
            double max = 0;
            if (Bridges != null) foreach (var bridge in Bridges) max = Math.Max(max, bridge.Gap3DMm);
            return max;
        }
    }
}

/// <summary>
/// strict 매핑이 실패한 뒤에만 쓰는 제한 근사 매핑.
/// 표면을 찾지 못한 짧은 내부 표본 구간을 앞뒤 실제 Hit 점의 직선으로 연결한다.
/// 정상 구간의 표본 순서·좌표는 <see cref="SurfacePathMapper.TryMapRouteStrict"/>와 동일하며,
/// strict가 성공하면 이 함수도 그 결과를 그대로 돌려준다(bitwise fast path).
/// </summary>
/// <remarks>
/// 거부 규칙(거부는 항상 명시적이다):
/// <list type="bullet">
/// <item>표면 조회가 <see cref="SurfaceQueryOutcome.Failure"/>인 경우(커널 오류, stale 컨텍스트 등).</item>
/// <item>경로 꼭짓점(원본 조각 끝점·꺾임점·인출점)이 Hit가 아닌 경우 — 절대 생략하지 않는다.</item>
/// <item>비유한 좌표, 지원 Face가 없는 Hit, 요청 데이텀과 다른 Hit.</item>
/// <item>누락 구간의 2D 진행 길이 또는 3D 직선거리가 한도를 넘는 경우
/// (<see cref="RouteApproximationPolicy.MaxBridgeAdvanceMm"/> 등).</item>
/// <item>표본 한도 소진.</item>
/// </list>
/// 근사 연결 내부에는 3D 점을 만들지 않고, 실패 위치에 가짜 지지 Face를 넣지 않는다.
/// </remarks>
public static class SurfacePathApproximation
{
    private const int MaxDepth = 18;
    private const string DepthError =
        "Surface mapping could not resolve a discontinuity within depth 18.";
    private const double DatumResidualTolerance = GeometryTolerancePolicy.DistanceTolerance;

    /// <summary>
    /// strict를 먼저 시도하고, 그 실패가 <b>단순 Miss</b>로만 설명될 때만 제한 근사를 적용한다.
    /// <paramref name="query"/>는 Miss와 Failure를 구분해야 한다.
    /// </summary>
    public static bool TryMapRoute(
        IReadOnlyList<Vec2> path,
        Func<Vec2, SurfaceQueryResult> query,
        IReadOnlyDictionary<int, SurfaceMappedSample>? vertexOverrides,
        out List<SurfaceMappedSample> mapped,
        out SurfaceApproximationReport report,
        out string failure,
        double gapSpacing = RouteSurfaceValidationPolicy.GapSpacing,
        int maxProbes = RouteSurfaceValidationPolicy.MaxProbes)
    {
        ValidateArguments(path, query, gapSpacing, maxProbes);
        report = SurfaceApproximationReport.None;
        if (!TryValidateOverrides(path, vertexOverrides, out failure))
        {
            mapped = new List<SurfaceMappedSample>();
            return false;
        }
        // 같은 2D 좌표를 반복하는 필수점은 strict fast path에서도 생략되면 안 된다.
        if (!TryValidateDuplicateRequiredPoints(path, vertexOverrides, out failure))
        {
            mapped = new List<SurfaceMappedSample>();
            return false;
        }

        // strict 시도 중 관측한 typed 결과를 기록한다. 하드 실패는 근사로 덮지 않는다.
        var recorder = new SurfaceProbeRecorder(query);
        if (SurfacePathMapper.TryMapRouteStrict(path, recorder.HitProbe, vertexOverrides, out mapped,
                out string strictFailure, gapSpacing, maxProbes))
        {
            failure = string.Empty;
            return true;
        }
        return RetryAfterStrictFailure(path, recorder, vertexOverrides, strictFailure,
            out mapped, out report, out failure, gapSpacing, maxProbes);
    }

    /// <summary>
    /// 호출자가 이미 strict를 실행해 실패한 경우에만 쓴다(중복 조회 없이 근사만 수행).
    /// </summary>
    /// <remarks>
    /// <b>전제:</b> <paramref name="recorder"/>는 그 strict 시도에 실제로 사용된 것이어야 한다.
    /// 이 함수는 <see cref="SurfaceProbeRecorder.AllowsApproximationRetry"/>가 false이면(하드 실패
    /// 관측 또는 Miss 미관측) 근사를 시도하지 않고 명시적으로 거부한다. 하드 실패·입력/override/
    /// 정제/예산 한도 실패를 재조회로 지우는 경로는 존재하지 않는다.
    /// </remarks>
    public static bool TryMapRouteAfterStrictFailure(
        IReadOnlyList<Vec2> path,
        SurfaceProbeRecorder recorder,
        IReadOnlyDictionary<int, SurfaceMappedSample>? vertexOverrides,
        out List<SurfaceMappedSample> mapped,
        out SurfaceApproximationReport report,
        out string failure,
        double gapSpacing = RouteSurfaceValidationPolicy.GapSpacing,
        int maxProbes = RouteSurfaceValidationPolicy.MaxProbes)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ValidateArguments(path, recorder, gapSpacing, maxProbes);
        report = SurfaceApproximationReport.None;
        if (!TryValidateOverrides(path, vertexOverrides, out failure))
        {
            mapped = new List<SurfaceMappedSample>();
            return false;
        }
        if (!TryValidateDuplicateRequiredPoints(path, vertexOverrides, out failure))
        {
            mapped = new List<SurfaceMappedSample>();
            return false;
        }
        return RetryAfterStrictFailure(path, recorder, vertexOverrides, string.Empty,
            out mapped, out report, out failure, gapSpacing, maxProbes);
    }

    private static bool RetryAfterStrictFailure(IReadOnlyList<Vec2> path, SurfaceProbeRecorder recorder,
        IReadOnlyDictionary<int, SurfaceMappedSample>? vertexOverrides, string strictFailure,
        out List<SurfaceMappedSample> mapped, out SurfaceApproximationReport report, out string failure,
        double gapSpacing, int maxProbes)
    {
        report = SurfaceApproximationReport.None;
        mapped = new List<SurfaceMappedSample>();
        if (!recorder.AllowsApproximationRetry)
        {
            failure = (strictFailure.Length == 0 ? string.Empty : strictFailure + " | ")
                + "근사 재시도 불가 — " + recorder.Describe();
            return false;
        }
        if (!Walk(path, recorder, vertexOverrides, gapSpacing, maxProbes,
                out mapped, out report, out string approximationFailure))
        {
            mapped = new List<SurfaceMappedSample>();
            report = SurfaceApproximationReport.None;
            failure = strictFailure.Length == 0
                ? approximationFailure
                : strictFailure + " | 근사 연결 불가: " + approximationFailure;
            return false;
        }
        failure = string.Empty;
        return true;
    }

    private static void ValidateArguments(IReadOnlyList<Vec2> path,
        Func<Vec2, SurfaceQueryResult> query, double gapSpacing, int maxProbes)
    {
        if (!double.IsFinite(gapSpacing) || gapSpacing < 0)
            throw new ArgumentOutOfRangeException(nameof(gapSpacing));
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(query);
        if (maxProbes < 1) throw new ArgumentOutOfRangeException(nameof(maxProbes));
    }

    private static void ValidateArguments(IReadOnlyList<Vec2> path,
        SurfaceProbeRecorder recorder, double gapSpacing, int maxProbes)
    {
        if (!double.IsFinite(gapSpacing) || gapSpacing < 0)
            throw new ArgumentOutOfRangeException(nameof(gapSpacing));
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(recorder);
        if (maxProbes < 1) throw new ArgumentOutOfRangeException(nameof(maxProbes));
    }

    /// <summary>strict API와 같은 꼭지점 override 유효성 검사(범위·유한성).</summary>
    private static bool TryValidateOverrides(IReadOnlyList<Vec2> path,
        IReadOnlyDictionary<int, SurfaceMappedSample>? overrides, out string failure)
    {
        failure = string.Empty;
        if (overrides == null) return true;
        foreach (KeyValuePair<int, SurfaceMappedSample> pair in overrides)
        {
            if (pair.Key < 0 || pair.Key >= path.Count)
            {
                failure = "A vertex override targets an index outside the planar path.";
                return false;
            }
            SurfaceMappedSample sample = pair.Value;
            if (!double.IsFinite(sample.DatumPoint.X) || !double.IsFinite(sample.DatumPoint.Y) ||
                !double.IsFinite(sample.Point.X) || !double.IsFinite(sample.Point.Y) ||
                !double.IsFinite(sample.Point.Z))
            {
                failure = "A vertex override contains a nonfinite coordinate.";
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 같은 작업평면 좌표가 연속으로 반복되는 경로에서 필수점(인출점 override)의 identity 검사.
    /// strict와 같은 2D 좌표 생략 규칙을 그대로 쓰되, <b>필수점을 검증 없이 버리지 않는다</b>.
    /// 두 점의 3D 깊이/지지 면이 같으면 정상 중복으로 보고 그대로 진행하고, 다르거나
    /// 표본이 유효하지 않으면 명시적으로 거부한다. (전면 dedup 리팩터링이 아니다.)
    /// </summary>
    public static bool TryValidateDuplicateRequiredPoints(IReadOnlyList<Vec2> path,
        IReadOnlyDictionary<int, SurfaceMappedSample>? vertexOverrides, out string failure)
    {
        failure = string.Empty;
        if (vertexOverrides == null || vertexOverrides.Count == 0) return true;
        ArgumentNullException.ThrowIfNull(path);
        for (int i = 1; i < path.Count; i++)
        {
            if (path[i] != path[i - 1]) continue;
            if (!vertexOverrides.TryGetValue(i, out SurfaceMappedSample sample)) continue;
            if (!IsAcceptableRequiredPoint(sample, path[i], out failure)) return false;
            if (!vertexOverrides.TryGetValue(i - 1, out SurfaceMappedSample previous))
            {
                failure = "같은 작업평면 좌표를 반복하는 경로의 필수점(인덱스 " + i +
                    ")은 앞 점의 3D 깊이/지지 면을 확인할 수 없어 생략할 수 없습니다.";
                return false;
            }
            if (!IsAcceptableRequiredPoint(previous, path[i - 1], out failure)) return false;
            if (previous.Point != sample.Point || previous.FaceId != sample.FaceId)
            {
                failure = "같은 작업평면 좌표를 반복하는 필수점의 3D 깊이/지지 면이 서로 다릅니다 — 인덱스 "
                    + (i - 1) + "/" + i;
                return false;
            }
        }
        return true;
    }

    private static bool IsAcceptableRequiredPoint(SurfaceMappedSample sample, Vec2 requested,
        out string failure)
    {
        failure = string.Empty;
        if (!double.IsFinite(sample.DatumPoint.X) || !double.IsFinite(sample.DatumPoint.Y) ||
            !double.IsFinite(sample.Point.X) || !double.IsFinite(sample.Point.Y) ||
            !double.IsFinite(sample.Point.Z))
        {
            failure = "필수점 표본에 유한하지 않은 좌표가 있습니다.";
            return false;
        }
        if (sample.FaceId < 0)
        {
            failure = "필수점 표본에 유효한 지지 Face가 없습니다.";
            return false;
        }
        if (sample.DatumPoint.DistanceTo(requested) > DatumResidualTolerance)
        {
            failure = "필수점 표본의 작업평면 좌표가 경로 점과 일치하지 않습니다.";
            return false;
        }
        return true;
    }

    private static bool Walk(IReadOnlyList<Vec2> path, SurfaceProbeRecorder recorder,
        IReadOnlyDictionary<int, SurfaceMappedSample>? vertexOverrides, double gapSpacing, int maxProbes,
        out List<SurfaceMappedSample> mapped, out SurfaceApproximationReport report, out string failure) =>
        new Walker(path, recorder, vertexOverrides, gapSpacing, maxProbes)
            .Run(out mapped, out report, out failure);

    private readonly record struct Probe(double T, Vec2 Uv, SurfaceQueryOutcome Outcome, SurfaceMappedSample Sample);

    private sealed class Walker
    {
        private readonly IReadOnlyList<Vec2> _path;
        private readonly SurfaceProbeRecorder _recorder;
        private readonly IReadOnlyDictionary<int, SurfaceMappedSample>? _overrides;
        private readonly double _gapSpacing;
        private readonly int _maxProbes;
        private readonly List<SurfaceMappedSample> _result = new();
        private readonly List<SurfaceApproximationBridge> _bridges = new();
        private int _probes;
        private string _error = string.Empty;

        internal Walker(IReadOnlyList<Vec2> path, SurfaceProbeRecorder recorder,
            IReadOnlyDictionary<int, SurfaceMappedSample>? overrides, double gapSpacing, int maxProbes)
        {
            _path = path;
            _recorder = recorder;
            _overrides = overrides;
            _gapSpacing = gapSpacing;
            _maxProbes = maxProbes;
        }

        internal bool Run(out List<SurfaceMappedSample> mapped, out SurfaceApproximationReport report,
            out string failure)
        {
            mapped = new List<SurfaceMappedSample>();
            report = SurfaceApproximationReport.None;
            failure = string.Empty;
            if (_maxProbes < 1) throw new ArgumentOutOfRangeException(nameof(_maxProbes));
            if (_path.Count == 0) { failure = "The planar path is empty."; return false; }

            if (!TryVertex(0, out SurfaceMappedSample previous))
            {
                failure = _error;
                return false;
            }
            Append(previous);
            for (int i = 1; i < _path.Count; i++)
            {
                if (_path[i] == _path[i - 1])
                {
                    // 같은 2D 좌표라도 필수점(override)의 3D 깊이/지지 면을 검증 없이 버리지 않는다.
                    if (!TryVerifyDuplicateVertex(i)) { failure = _error; return false; }
                    continue;
                }
                if (!TryVertex(i, out SurfaceMappedSample next))
                {
                    failure = _error;
                    return false;
                }
                // strict와 같이 '결과의 마지막 점'에서 다음 꼭짓점으로 정제한다.
                SurfaceMappedSample segmentStart = _result[^1];
                if (!Refine(i - 1, _path[i - 1], segmentStart, _path[i], next, 0))
                {
                    failure = _error;
                    return false;
                }
                previous = next;
            }
            mapped = _result;
            report = new SurfaceApproximationReport(_bridges);
            return true;
        }

        private void Append(SurfaceMappedSample sample)
        {
            if (_result.Count == 0 || _result[^1].DatumPoint != sample.DatumPoint) _result.Add(sample);
        }

        /// <summary>
        /// 연속 중복 좌표의 필수점 검사. override가 없으면 strict와 같이 생략하고,
        /// override가 있으면 유효성과 앞 표본과의 3D/지지 면 동일성을 확인한다.
        /// </summary>
        private bool TryVerifyDuplicateVertex(int index)
        {
            if (_overrides == null || !_overrides.TryGetValue(index, out SurfaceMappedSample sample))
                return true;
            if (!IsAcceptableHit(sample, _path[index], out string reason))
            {
                _error = reason;
                return false;
            }
            if (_result.Count == 0) return true;
            SurfaceMappedSample adopted = _result[^1];
            if (adopted.Point != sample.Point || adopted.FaceId != sample.FaceId)
            {
                _error = "같은 작업평면 좌표를 반복하는 필수점의 3D 깊이/지지 면이 서로 달라 생략할 수 없습니다 — "
                    + Describe(_path[index]) + " (앞 점 face=" + adopted.FaceId + ", 필수점 face=" + sample.FaceId + ")";
                return false;
            }
            return true;
        }

        private bool TryVertex(int index, out SurfaceMappedSample sample)
        {
            sample = default;
            Vec2 point = _path[index];
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            {
                _error = "The planar path contains a nonfinite coordinate.";
                return false;
            }
            if (_overrides != null && _overrides.TryGetValue(index, out SurfaceMappedSample value))
            {
                sample = value;
                if (!IsAcceptableHit(sample, point, out string reason))
                {
                    _error = reason;
                    return false;
                }
                return true;
            }
            if (!TryQuery(point, out sample, out SurfaceQueryOutcome outcome)) return false;
            if (outcome != SurfaceQueryOutcome.Hit)
            {
                _error = "경로 꼭짓점(원본 조각 끝점·꺾임점·인출점)은 근사로 생략할 수 없습니다 — " + Describe(point);
                return false;
            }
            return true;
        }

        private bool TryQuery(Vec2 uv, out SurfaceMappedSample sample, out SurfaceQueryOutcome outcome)
        {
            sample = default;
            outcome = SurfaceQueryOutcome.Miss;
            if (!double.IsFinite(uv.X) || !double.IsFinite(uv.Y))
            {
                _error = "The planar path contains a nonfinite coordinate.";
                outcome = SurfaceQueryOutcome.Failure;
                return false;
            }
            if (_probes >= _maxProbes)
            {
                _error = $"Surface mapping exceeded the {_maxProbes}-probe limit.";
                outcome = SurfaceQueryOutcome.Failure;
                return false;
            }
            _probes++;
            SurfaceQueryResult value = _recorder.Query(uv);
            outcome = value.Outcome;
            if (outcome == SurfaceQueryOutcome.Failure)
            {
                _error = "표면 조회 실패(근사 불가) — " + Describe(uv) +
                    (string.IsNullOrEmpty(value.Reason) ? string.Empty : " · " + value.Reason);
                return false;
            }
            if (outcome == SurfaceQueryOutcome.Miss) return true;
            sample = value.Sample;
            if (!IsAcceptableHit(sample, uv, out string reason))
            {
                _error = reason;
                outcome = SurfaceQueryOutcome.Failure;
                return false;
            }
            return true;
        }

        private static bool IsAcceptableHit(SurfaceMappedSample sample, Vec2 requested, out string reason)
        {
            reason = string.Empty;
            if (!double.IsFinite(sample.Point.X) || !double.IsFinite(sample.Point.Y) ||
                !double.IsFinite(sample.Point.Z))
            {
                reason = "A surface hit contains a nonfinite coordinate.";
                return false;
            }
            if (sample.FaceId < 0)
            {
                reason = "A surface hit has no valid support face.";
                return false;
            }
            if (!double.IsFinite(sample.DatumPoint.X) || !double.IsFinite(sample.DatumPoint.Y) ||
                sample.DatumPoint.DistanceTo(requested) > DatumResidualTolerance)
            {
                reason = "The mapped surface point does not match the requested planar point.";
                return false;
            }
            return true;
        }

        private static string Describe(Vec2 uv) =>
            "(" + uv.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ","
                + uv.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ")";

        private static Vec2 At(Vec2 a, Vec2 b, double t) => a + (b - a) * t;

        /// <summary>
        /// strict와 동일한 적응 정제. 리프가 수용되면 strict와 같은 순서로 표본을 붙인다.
        /// 표본이 없거나 참/거짓 판정이 갈리면 누락 구간별 독립 판정으로 넘긴다.
        /// </summary>
        private bool Refine(int segmentIndex, Vec2 a, SurfaceMappedSample sampleA, Vec2 b,
            SurfaceMappedSample sampleB, int depth)
        {
            double length = a.DistanceTo(b);
            if (!double.IsFinite(length))
            {
                _error = "The planar path contains a nonfinite coordinate.";
                return false;
            }
            if (length <= 0) { Append(sampleB); return true; }

            Vec2 mid = a * 0.5 + b * 0.5;
            Vec2 quarter = a * 0.75 + b * 0.25;
            Vec2 threeQuarter = a * 0.25 + b * 0.75;
            if (!TryQuery(mid, out SurfaceMappedSample mappedMidpoint, out SurfaceQueryOutcome outcomeMid) ||
                !TryQuery(quarter, out SurfaceMappedSample mappedQuarter, out SurfaceQueryOutcome outcomeQuarter) ||
                !TryQuery(threeQuarter, out SurfaceMappedSample mappedThreeQuarter, out SurfaceQueryOutcome outcomeThree))
                return false;

            bool allHit = outcomeMid == SurfaceQueryOutcome.Hit &&
                outcomeQuarter == SurfaceQueryOutcome.Hit && outcomeThree == SurfaceQueryOutcome.Hit;
            if (allHit)
            {
                Vec3 pointA = sampleA.Point, pointB = sampleB.Point;
                bool chordOk =
                    (mappedMidpoint.Point - (pointA * 0.5 + pointB * 0.5)).Length <= RouteSurfaceValidationPolicy.ChordError &&
                    (mappedQuarter.Point - (pointA * 0.75 + pointB * 0.25)).Length <= RouteSurfaceValidationPolicy.ChordError &&
                    (mappedThreeQuarter.Point - (pointA * 0.25 + pointB * 0.75)).Length <= RouteSurfaceValidationPolicy.ChordError;
                if (chordOk)
                {
                    // strict와 같은 보강 표본 격자를 실제로 조회한다. strict가 이 격자에서 놓친
                    // Miss가 있으면 정상 처리하지 않고 누락 구간 판정으로 넘긴다.
                    List<Probe>? grid = null;
                    if (_gapSpacing > 0 && length > _gapSpacing)
                    {
                        if (!ScanLeaf(a, b, length, _gapSpacing, out grid)) return false;
                    }
                    if (grid == null || grid.All(probe => probe.Outcome == SurfaceQueryOutcome.Hit))
                    {
                        Append(mappedQuarter);
                        Append(mappedMidpoint);
                        Append(mappedThreeQuarter);
                        Append(sampleB);
                        return true;
                    }
                    return HandleMisses(segmentIndex, a, sampleA, b, sampleB, grid, depth);
                }
                if (depth >= MaxDepth || mid == a || mid == b)
                {
                    _error = DepthError;
                    return false;
                }
                return Refine(segmentIndex, a, sampleA, mid, mappedMidpoint, depth + 1) &&
                    Refine(segmentIndex, mid, mappedMidpoint, b, sampleB, depth + 1);
            }

            var probes = new List<Probe>(3)
            {
                new(0.25, quarter, outcomeQuarter, mappedQuarter),
                new(0.5, mid, outcomeMid, mappedMidpoint),
                new(0.75, threeQuarter, outcomeThree, mappedThreeQuarter),
            };
            // 초기 적응 표본이 여러 곳에서 Miss일 수 있다. 그 사이를 확인하지 않은 채 하나의
            // 연속 누락으로 합치면 서로 떨어진 좁은 구간까지 거부하게 된다. 정책 1 mm 간격의
            // 실제 조회로 구간 전체의 Hit/Miss 구조를 파악하고, 이미 관측한 적응 표본과
            // 진행 파라미터 순서로 합쳐 누락 구간마다 독립 판정한다.
            List<Probe> merged = probes;
            if (length > RouteApproximationPolicy.ProbeSpacingMm)
            {
                if (!ScanLeaf(a, b, length, RouteApproximationPolicy.ProbeSpacingMm, out List<Probe> discovery))
                    return false;
                merged = MergeProbes(probes, discovery);
            }
            return HandleMisses(segmentIndex, a, sampleA, b, sampleB, merged, depth);
        }

        /// <summary>strict의 리프 보강 표본과 같은 등분 격자를 조회한다(경로 순서).</summary>
        private bool ScanLeaf(Vec2 a, Vec2 b, double length, double spacing, out List<Probe> probes)
        {
            probes = new List<Probe>();
            // 정수 캐스팅 전에 단계 수의 유한성·범위·남은 표본 예산을 확인한다. 넘치면
            // 표본 검사를 생략한 성공으로 넘기지 않고 명시적으로 실패한다.
            double ratio = length / spacing;
            if (!double.IsFinite(ratio) || ratio > int.MaxValue - 1 || ratio - 1 > _maxProbes - _probes)
            {
                _error = "표본 격자 단계 수가 너무 많아 근사 검사를 수행할 수 없습니다 (length="
                    + length.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                    + ", 간격=" + spacing.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                    + ", 남은 표본=" + (_maxProbes - _probes) + ").";
                return false;
            }
            int steps = (int)Math.Ceiling(ratio);
            for (int step = 1; step < steps; step++)
            {
                double t = step / (double)steps;
                Vec2 uv = a + (b - a) * t;
                if (!TryQuery(uv, out SurfaceMappedSample sample, out SurfaceQueryOutcome outcome)) return false;
                probes.Add(new Probe(t, uv, outcome, sample));
            }
            return true;
        }

        /// <summary>
        /// 두 관측 목록을 진행 파라미터 순서로 합친다. 같은 위치(1e-9 이내)는 하나로 합치되
        /// Miss를 우선해 "우연히 Miss를 못 본" 상태를 만들지 않는다. 실패는 여기 들어오지 않는다.
        /// </summary>
        private static List<Probe> MergeProbes(List<Probe> first, List<Probe> second)
        {
            var merged = new List<Probe>(first.Count + second.Count);
            merged.AddRange(first);
            merged.AddRange(second);
            merged.Sort((left, right) => left.T.CompareTo(right.T));
            var unique = new List<Probe>(merged.Count);
            foreach (Probe probe in merged)
            {
                if (unique.Count > 0 && Math.Abs(probe.T - unique[^1].T) <= 1e-9)
                {
                    Probe previous = unique[^1];
                    // 같은 위치의 관측이 갈리면 누락(Miss)을 보존한다.
                    if (previous.Outcome != SurfaceQueryOutcome.Miss &&
                        probe.Outcome == SurfaceQueryOutcome.Miss)
                        unique[^1] = probe;
                    continue;
                }
                unique.Add(probe);
            }
            return unique;
        }

        /// <summary>
        /// 누락 구간마다 앞뒤 실제 Hit 경계를 찾아 독립 판정한다. 경계 탐색은 1 mm 간격으로
        /// 실제 조회하며, 승인된 연결만 기록한다. 나머지 정상 구간은 다시 적응 정제한다.
        /// </summary>
        private bool HandleMisses(int segmentIndex, Vec2 a, SurfaceMappedSample sampleA, Vec2 b,
            SurfaceMappedSample sampleB, List<Probe> probes, int depth)
        {
            double length = a.DistanceTo(b);
            var hits = new List<Probe>(probes.Count + 2)
            {
                new(0, a, SurfaceQueryOutcome.Hit, sampleA),
            };
            hits.AddRange(probes.Where(probe => probe.Outcome == SurfaceQueryOutcome.Hit));
            hits.Add(new(1, b, SurfaceQueryOutcome.Hit, sampleB));

            var spans = new List<(Probe Left, Probe Right, double Advance, double Gap3D)>();
            for (int i = 0; i < probes.Count;)
            {
                if (probes[i].Outcome != SurfaceQueryOutcome.Miss) { i++; continue; }
                int start = i;
                while (i < probes.Count && probes[i].Outcome == SurfaceQueryOutcome.Miss) i++;
                double tFrom = probes[start].T;
                double tTo = probes[i - 1].T;

                Probe left = hits.Last(probe => probe.T < tFrom);
                Probe right = hits.First(probe => probe.T > tTo);
                if (!ScanForward(a, b, length, left, tFrom, out left)) return false;
                if (!ScanBackward(a, b, length, right, tTo, out right)) return false;

                double advance = length * (right.T - left.T);
                double gap3D = (left.Sample.Point - right.Sample.Point).Length;
                if (!double.IsFinite(advance) || !double.IsFinite(gap3D) || !(right.T > left.T))
                {
                    _error = "근사 연결 구간의 길이를 확정할 수 없습니다 — " + Describe(left.Uv) + " → " + Describe(right.Uv);
                    return false;
                }
                if (advance > RouteApproximationPolicy.MaxBridgeAdvanceMm)
                {
                    _error = "표면 미검출 구간의 2D 진행 길이 " + advance.ToString("0.###") +
                        " mm 가 근사 한도 " + RouteApproximationPolicy.MaxBridgeAdvanceMm.ToString("0.###") +
                        " mm 를 넘습니다 — " + Describe(left.Uv) + " → " + Describe(right.Uv);
                    return false;
                }
                if (gap3D > RouteApproximationPolicy.MaxBridge3DGapMm)
                {
                    _error = "표면 미검출 구간의 3D 거리 " + gap3D.ToString("0.###") +
                        " mm 가 근사 한도 " + RouteApproximationPolicy.MaxBridge3DGapMm.ToString("0.###") +
                        " mm 를 넘습니다 — " + Describe(left.Uv) + " → " + Describe(right.Uv);
                    return false;
                }
                spans.Add((left, right, advance, gap3D));
            }

            Vec2 cursorUv = a;
            SurfaceMappedSample cursorSample = sampleA;
            foreach ((Probe left, Probe right, double advance, double gap3D) in spans)
            {
                if (cursorUv != left.Uv)
                {
                    if (!Refine(segmentIndex, cursorUv, cursorSample, left.Uv, left.Sample, depth + 1))
                        return false;
                }
                Append(right.Sample);
                _bridges.Add(new SurfaceApproximationBridge(segmentIndex, left.Uv, right.Uv, advance, gap3D));
                cursorUv = right.Uv;
                cursorSample = right.Sample;
            }
            if (cursorUv != b)
                return Refine(segmentIndex, cursorUv, cursorSample, b, sampleB, depth + 1);
            return true;
        }

        /// <summary>Hit에서 누락 구간 쪽으로 1 mm 간격으로 전진해 마지막 Hit를 찾는다.</summary>
        private bool ScanForward(Vec2 a, Vec2 b, double length, Probe from, double cap, out Probe boundary)
        {
            boundary = from;
            double step = RouteApproximationPolicy.ProbeSpacingMm / length;
            if (!double.IsFinite(step) || step <= 0)
            {
                _error = "근사 연결 경계 탐색 간격이 유효하지 않습니다.";
                return false;
            }
            for (double t = from.T + step; t < cap; t += step)
            {
                Vec2 uv = At(a, b, t);
                if (!TryQuery(uv, out SurfaceMappedSample sample, out SurfaceQueryOutcome outcome)) return false;
                if (outcome != SurfaceQueryOutcome.Hit) break;
                boundary = new Probe(t, uv, outcome, sample);
            }
            return true;
        }

        /// <summary>Hit에서 누락 구간 안쪽으로 1 mm 간격으로 후진해 첫 Hit를 찾는다.</summary>
        private bool ScanBackward(Vec2 a, Vec2 b, double length, Probe from, double cap, out Probe boundary)
        {
            boundary = from;
            double step = RouteApproximationPolicy.ProbeSpacingMm / length;
            if (!double.IsFinite(step) || step <= 0)
            {
                _error = "근사 연결 경계 탐색 간격이 유효하지 않습니다.";
                return false;
            }
            for (double t = from.T - step; t > cap; t -= step)
            {
                Vec2 uv = At(a, b, t);
                if (!TryQuery(uv, out SurfaceMappedSample sample, out SurfaceQueryOutcome outcome)) return false;
                if (outcome != SurfaceQueryOutcome.Hit) break;
                boundary = new Probe(t, uv, outcome, sample);
            }
            return true;
        }
    }
}
