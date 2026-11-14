namespace HeatingMold.Core.Geometry;

/// <summary>
/// 평면 턴 외곽 돌출 허용 정책 (사용자 확정 2026-11-14).
/// 턴 연결선에만 적용하며, 확정 작업평면 2D 중심선에서 원본 CAD 외곽까지의 최단거리가
/// 전 구간에서 <see cref="MaxExteriorProtrusionMm"/> 이하일 때만 예외를 승인한다.
/// 전역 기하 공차(<see cref="GeometryTolerancePolicy"/>)는 변경하지 않는다.
/// </summary>
public static class PlanarExteriorTurnPolicy
{
    /// <summary>이 정책의 판정 계약 버전. 정책/증빙 캐시 키에 포함한다.</summary>
    public const string Version = "planar-exterior-turn-v1";

    /// <summary>허용 최대 돌출량(mm). 등호(정확히 이 값)도 허용한다.</summary>
    public const double MaxExteriorProtrusionMm = 5.0;

    /// <summary>초기 표본 간격(mm). 구간은 증명이 안 될 때만 더 쪼갠다.</summary>
    public const double InitialSampleStepMm = 0.5;

    /// <summary>더 이상 쪼개지 않는 최소 구간 길이(mm).</summary>
    public const double MinimumSampleStepMm = 0.001;

    /// <summary>한 턴당 최대 표본 조회 수. 넘으면 '확인 불가'로 거부한다.</summary>
    public const int MaxProbeCount = 4096;

    /// <summary>이 정책이 적용되는 원래 경로 조각 종류.</summary>
    public static bool AppliesTo(SerpentineRoutePieceKind kind) => kind == SerpentineRoutePieceKind.TurnConnector;
}

/// <summary>돌출 판정 상태. '초과'와 '확인 불가'를 구분한다.</summary>
public enum PlanarExteriorTurnStatus
{
    /// <summary>전 구간이 한도 이내임이 증명되었다.</summary>
    Valid,
    /// <summary>이 정책의 적용 대상이 아니다(턴이 아니거나 같은 평면이 아님).</summary>
    NotApplicable,
    /// <summary>관측된 돌출량이 한도를 넘었다.</summary>
    OverLimit,
    /// <summary>외곽 내부에 표면이 없는 구간(내부 구멍·틈)을 지난다.</summary>
    InteriorGap,
    /// <summary>동일 거리의 서로 다른 원본 경계 후보가 있어 하나로 정할 수 없다.</summary>
    Ambiguous,
    /// <summary>원본 CAD 조회가 실패했다(오래된 참조·기하 오류 등).</summary>
    QueryFailed,
    /// <summary>수치적으로 한도 이내임을 증명하지 못했다(초과로 단정하지 않음).</summary>
    Indeterminate,
    /// <summary>입력이 유효하지 않다.</summary>
    InvalidInput,
}

/// <summary>표본 하나에서 얻은 원본 CAD 외곽 정보.</summary>
public enum PlanarExteriorProbeStatus
{
    /// <summary>정상 조회. 돌출량과 경계 증거를 사용할 수 있다.</summary>
    Ok,
    /// <summary>외곽 내부인데 표면 교차가 없다 — 내부 구멍/틈.</summary>
    InteriorGap,
    /// <summary>같은 거리의 서로 다른 provenance 후보가 있다.</summary>
    Ambiguous,
    /// <summary>참조 만료·조회 오류.</summary>
    QueryFailed,
}

/// <summary>
/// 한 표본의 판정 자료. <paramref name="WitnessStart"/>/<paramref name="WitnessEnd"/>는
/// 그 점에서 가장 가까운 <b>원본 외곽</b> 경계 요소이며, 직선 요소일 때만 구간 증명에 쓴다.
/// 외곽이 아닌 내부 구멍·제외영역·절단선은 증거로 쓰지 않는다.
/// </summary>
public readonly record struct PlanarExteriorProbeSample(
    PlanarExteriorProbeStatus Status,
    bool IsInsideRegion,
    double ExteriorDistanceMm,
    Vec2 WitnessStart,
    Vec2 WitnessEnd,
    bool WitnessIsStraight,
    bool WitnessIsOuterBoundary,
    double WitnessDistanceMm)
{
    public static PlanarExteriorProbeSample Inside() =>
        new(PlanarExteriorProbeStatus.Ok, true, 0, default, default, false, false, 0);

    public static PlanarExteriorProbeSample Outside(
        double exteriorDistanceMm,
        Vec2 witnessStart,
        Vec2 witnessEnd,
        bool witnessIsStraight,
        bool witnessIsOuterBoundary,
        double witnessDistanceMm) =>
        new(PlanarExteriorProbeStatus.Ok, false, exteriorDistanceMm,
            witnessStart, witnessEnd, witnessIsStraight, witnessIsOuterBoundary, witnessDistanceMm);

    public static PlanarExteriorProbeSample Failed(PlanarExteriorProbeStatus status) =>
        new(status, false, 0, default, default, false, false, 0);
}

public delegate PlanarExteriorProbeSample PlanarExteriorTurnProbe(Vec2 point);

/// <summary>돌출 판정 결과.</summary>
public sealed record PlanarExteriorTurnValidation(
    PlanarExteriorTurnStatus Status,
    double ObservedMaximumMm,
    double CertifiedUpperBoundMm,
    int ProbeCount,
    string Detail)
{
    public bool IsValid => Status == PlanarExteriorTurnStatus.Valid;
}

/// <summary>
/// 평면 턴 연결선 전체의 외곽 돌출을 '증명'하는 검증기.
///
/// <para>거리 함수 d(t) = 원본 외곽까지의 최단거리는 1-Lipschitz 이므로, 구간 [a,b]에서
/// max d ≤ max(d(a), d(b)) + (b-a)/2 라는 보수적 상계를 쓸 수 있다.</para>
///
/// <para>직선 외곽 요소 E가 경계의 일부이면 d(P) ≤ dist(P, E) 이고, 점에서 직선분까지의
/// 거리는 선분 P(t)에 대해 볼록이므로 max_t d(P(t)) ≤ max(dist(P(a),E), dist(P(b),E)) 이다.
/// 이 두 상계 중 작은 값을 구간 상계로 채택한다. 따라서 고정 간격 표본의 최댓값을
/// 그대로 성공으로 간주하지 않고, 상계가 한도 이하일 때만 승인한다.</para>
/// </summary>
public static class PlanarExteriorTurnValidator
{
    public static PlanarExteriorTurnValidation Validate(
        Vec2 start,
        Vec2 end,
        PlanarExteriorTurnProbe probe,
        double limitMm = PlanarExteriorTurnPolicy.MaxExteriorProtrusionMm)
    {
        if (probe == null)
        {
            return Invalid("검증 함수가 없습니다");
        }
        if (!double.IsFinite(start.X) || !double.IsFinite(start.Y) ||
            !double.IsFinite(end.X) || !double.IsFinite(end.Y))
        {
            return Invalid("연결선 좌표가 유한하지 않습니다");
        }
        if (!double.IsFinite(limitMm) || limitMm <= 0)
        {
            return Invalid("허용 한도가 유효하지 않습니다");
        }

        double length = start.DistanceTo(end);
        if (!double.IsFinite(length) || length <= 0)
        {
            return Invalid("연결선 길이가 0입니다");
        }

        int probeCount = 0;
        PlanarExteriorTurnStatus? failure = null;
        string failureDetail = "";

        Vec2 PointAt(double t) =>
            new(start.X + (end.X - start.X) * t, start.Y + (end.Y - start.Y) * t);

        PlanarExteriorProbeSample Evaluate(Vec2 point)
        {
            PlanarExteriorProbeSample sample;
            try
            {
                sample = probe(point);
            }
            catch (Exception ex)
            {
                failure ??= PlanarExteriorTurnStatus.QueryFailed;
                if (failureDetail.Length == 0)
                {
                    failureDetail = "원본 CAD 조회 중 예외: " + ex.Message;
                }
                return PlanarExteriorProbeSample.Failed(PlanarExteriorProbeStatus.QueryFailed);
            }

            probeCount++;
            switch (sample.Status)
            {
                case PlanarExteriorProbeStatus.QueryFailed:
                    failure ??= PlanarExteriorTurnStatus.QueryFailed;
                    if (failureDetail.Length == 0)
                    {
                        failureDetail = "원본 CAD 외곽 조회에 실패했습니다";
                    }
                    break;
                case PlanarExteriorProbeStatus.Ambiguous:
                    if (failure is null or PlanarExteriorTurnStatus.InteriorGap)
                    {
                        failure = PlanarExteriorTurnStatus.Ambiguous;
                        failureDetail = "같은 거리의 서로 다른 원본 경계 후보가 있습니다";
                    }
                    break;
                case PlanarExteriorProbeStatus.InteriorGap:
                    failure ??= PlanarExteriorTurnStatus.InteriorGap;
                    if (failureDetail.Length == 0)
                    {
                        failureDetail = "외곽 내부에 표면이 없는 구간을 지납니다";
                    }
                    break;
            }

            return sample;
        }

        SegmentBound MakeBound(SegmentNode a, SegmentNode b)
        {
            double bound = TryCertify(a, b, limitMm, length);
            return new SegmentBound(a, b, bound, bound <= limitMm);
        }

        int steps = (int)Math.Ceiling(length / PlanarExteriorTurnPolicy.InitialSampleStepMm);
        if (steps < 1)
        {
            steps = 1;
        }
        if (steps + 1 > PlanarExteriorTurnPolicy.MaxProbeCount)
        {
            return new PlanarExteriorTurnValidation(
                PlanarExteriorTurnStatus.Indeterminate,
                double.NaN,
                double.PositiveInfinity,
                0,
                $"연결선 길이 {length:0.###} mm가 표본 예산을 넘습니다");
        }

        var nodes = new SegmentNode[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            double t = (double)i / steps;
            Vec2 point = PointAt(t);
            nodes[i] = new SegmentNode(t, point, Evaluate(point));
        }

        var segments = new List<SegmentBound>(steps);
        for (int i = 0; i < steps; i++)
        {
            segments.Add(MakeBound(nodes[i], nodes[i + 1]));
        }

        bool budgetExhausted = false;
        bool split = true;
        while (split)
        {
            split = false;
            var next = new List<SegmentBound>(segments.Count * 2);
            foreach (var segment in segments)
            {
                if (segment.IsCertified) { next.Add(segment); continue; }

                double span = (segment.End.T - segment.Start.T) * length;
                if (budgetExhausted || span <= PlanarExteriorTurnPolicy.MinimumSampleStepMm)
                {
                    next.Add(segment);
                    continue;
                }
                if (probeCount >= PlanarExteriorTurnPolicy.MaxProbeCount)
                {
                    budgetExhausted = true;
                    next.Add(segment);
                    continue;
                }

                double mid = (segment.Start.T + segment.End.T) * 0.5;
                Vec2 midPoint = PointAt(mid);
                var middle = new SegmentNode(mid, midPoint, Evaluate(midPoint));
                var left = MakeBound(segment.Start, middle);
                var right = MakeBound(middle, segment.End);
                next.Add(left);
                next.Add(right);
                split = true;
            }
            segments = next;
        }

        double observed = 0;
        bool anyObserved = false;
        bool anyUncertified = false;
        double certifiedBound = 0;
        foreach (var segment in segments)
        {
            foreach (var node in new[] { segment.Start, segment.End })
            {
                if (node.Sample.Status == PlanarExteriorProbeStatus.Ok &&
                    double.IsFinite(node.Value) && node.Value >= 0)
                {
                    observed = Math.Max(observed, node.Value);
                    anyObserved = true;
                }
            }

            if (segment.IsCertified)
            {
                certifiedBound = Math.Max(certifiedBound, segment.Bound);
            }
            else
            {
                anyUncertified = true;
            }
        }

        if (!anyObserved)
        {
            observed = double.NaN;
        }

        if (failure is PlanarExteriorTurnStatus status)
        {
            return new PlanarExteriorTurnValidation(status, observed, certifiedBound, probeCount, failureDetail);
        }

        if (anyObserved && observed > limitMm)
        {
            return new PlanarExteriorTurnValidation(
                PlanarExteriorTurnStatus.OverLimit,
                observed,
                certifiedBound,
                probeCount,
                $"관측 돌출량 {observed:0.####} mm가 한도 {limitMm:0.###} mm를 넘습니다");
        }

        if (anyUncertified)
        {
            return new PlanarExteriorTurnValidation(
                PlanarExteriorTurnStatus.Indeterminate,
                observed,
                double.PositiveInfinity,
                probeCount,
                "고정 표본 최댓값은 한도 이내지만 전 구간 상계를 증명하지 못했습니다");
        }

        return new PlanarExteriorTurnValidation(
            PlanarExteriorTurnStatus.Valid,
            observed,
            certifiedBound,
            probeCount,
            $"전 구간 상계 {certifiedBound:0.####} mm ≤ 한도 {limitMm:0.###} mm");
    }

    private static PlanarExteriorTurnValidation Invalid(string detail) =>
        new(PlanarExteriorTurnStatus.InvalidInput, double.NaN, double.NaN, 0, detail);

    private static double TryCertify(SegmentNode a, SegmentNode b, double limitMm, double lengthMm)
    {
        if (a.Sample.Status != PlanarExteriorProbeStatus.Ok ||
            b.Sample.Status != PlanarExteriorProbeStatus.Ok)
        {
            return double.PositiveInfinity;
        }

        double lipschitz = Math.Max(a.Value, b.Value) + Math.Abs(b.T - a.T) * lengthMm * 0.5;

        double straight = double.PositiveInfinity;
        TryStraightBound(a, b, out straight);

        return Math.Min(lipschitz, straight);
    }

    /// <summary>
    /// 직선 외곽 요소를 이용한 볼록 상계. 요소는 반드시 원본 외곽이어야 하며,
    /// 그 요소까지의 거리는 점-선분 거리의 볼록성으로 구간 최댓값이 양 끝점에 있다.
    /// </summary>
    private static bool TryStraightBound(SegmentNode a, SegmentNode b, out double bound)
    {
        bound = double.PositiveInfinity;
        bool found = false;

        double TryElement(Vec2 elementStart, Vec2 elementEnd)
        {
            double da = DistanceToSegment(a.Point, elementStart, elementEnd);
            double db = DistanceToSegment(b.Point, elementStart, elementEnd);
            return Math.Max(da, db);
        }

        if (a.Sample.WitnessIsStraight && a.Sample.WitnessIsOuterBoundary)
        {
            bound = Math.Min(bound, TryElement(a.Sample.WitnessStart, a.Sample.WitnessEnd));
            found = true;
        }
        if (b.Sample.WitnessIsStraight && b.Sample.WitnessIsOuterBoundary)
        {
            bound = Math.Min(bound, TryElement(b.Sample.WitnessStart, b.Sample.WitnessEnd));
            found = true;
        }

        return found;
    }

    public static double DistanceToSegment(Vec2 point, Vec2 start, Vec2 end)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= 0)
        {
            return point.DistanceTo(start);
        }

        double t = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared;
        if (t < 0) t = 0;
        else if (t > 1) t = 1;

        return point.DistanceTo(new Vec2(start.X + dx * t, start.Y + dy * t));
    }

    private readonly record struct SegmentNode(double T, Vec2 Point, PlanarExteriorProbeSample Sample)
    {
        public double Value => Sample.IsInsideRegion ? 0 : Sample.ExteriorDistanceMm;
    }

    private readonly struct SegmentBound
    {
        public SegmentBound(SegmentNode start, SegmentNode end, double bound, bool isCertified)
        {
            Start = start;
            End = end;
            Bound = bound;
            IsCertified = isCertified;
        }

        public SegmentNode Start { get; }
        public SegmentNode End { get; }
        public double Bound { get; }
        public bool IsCertified { get; }
    }
}
