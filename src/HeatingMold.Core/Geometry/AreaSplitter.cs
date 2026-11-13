using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Clipper2Lib;
using HeatingMold.Core.Model;

namespace HeatingMold.Core.Geometry;

/// <summary>분할로 만들어진 독립 영역 하나. Outer는 작업평면 2D 폴리곤(마진 적용 전)이다.</summary>
public sealed record AreaSplitBand(
    int Index,
    List<Vec2> Outer,
    double AreaMm2,
    double EstimatedLengthMm);

/// <summary>영역 분할 결과. 실패하면 Bands는 비어 있고 Failure에 사유가 담긴다.</summary>
public sealed record AreaSplitResult
{
    public bool Succeeded { get; init; }
    public string Failure { get; init; } = "";
    public IReadOnlyList<AreaSplitBand> Bands { get; init; } = Array.Empty<AreaSplitBand>();

    /// <summary>내부 절단 좌표(작업평면 2D, 분할 축 기준). 개수 = 영역 수 - 1.</summary>
    public IReadOnlyList<double> Cuts { get; init; } = Array.Empty<double>();

    public double EstimatedTotalLengthMm { get; init; }

    /// <summary>가장 긴 영역과 가장 짧은 영역의 예상 길이 비. 1에 가까울수록 균등하다.</summary>
    public double MaxToMinLengthRatio { get; init; }

    /// <summary>참이면 Y=상수 수평 절단(가로 배치), 거짓이면 X=상수 수직 절단(세로 배치).</summary>
    public bool SplitAxisIsY { get; init; }

    internal static AreaSplitResult Fail(string failure) => new() { Succeeded = false, Failure = failure };
}

/// <summary>
/// 적용영역을 배치 방향에 맞는 평행 절단선으로 여러 독립 영역으로 나눈다.
/// <list type="bullet">
/// <item>가로 배치(사행 레인이 프레임 X 방향)는 <b>수평 절단선</b>으로 위/아래를 나눈다.</item>
/// <item>세로 배치(사행 레인이 프레임 Y 방향)는 <b>수직 절단선</b>으로 좌/우를 나눈다.</item>
/// </list>
/// 여기서 계산하는 예상 길이는 (마진 적용 후 면적 / 간격) + (마진 적용 후 사행 진행 높이)인
/// <b>초기 절단 후보</b>다. 실제 2D 발열경로 길이가 아니므로, 분할을 확정하는 쪽은
/// <see cref="TryBuildAtCuts"/>로 후보를 다시 구성한 뒤 실제 생성기 결과의 길이를 비교해야 한다.
/// 최종 전력 배분과 판정은 생성 후 실측 3D 길이로 다시 계산한다.
/// 각 영역은 마진을 자기 내부에 적용하므로, 여기서는 마진을 영역 경계에 적용하지 않는다.
/// </summary>
public static class AreaSplitter
{
    /// <summary>예상 길이 균등도 목표 비율. 이보다 벌어지면 더 나은 절단을 찾는다.</summary>
    public const double LengthBalanceTolerance = 1.02;

    private const int BisectionSteps = 80;
    private const int RefinementSteps = 80;
    private const int ClipperPrecision = 2;

    /// <summary>
    /// outer(적용영역 외곽) - exclusions(제외 사각형)를 count개 독립 영역으로 나눈다.
    /// count는 1 이상이어야 하며, 1이면 원래 영역을 그대로 한 영역으로 돌려준다.
    /// </summary>
    public static AreaSplitResult Split(
        IReadOnlyList<Vec2> outer,
        IEnumerable<(double MinX, double MinY, double MaxX, double MaxY)> exclusions,
        PatternDirection direction,
        double spacing,
        int count)
    {
        if (count < 1)
            return AreaSplitResult.Fail("분할 영역 수는 1 이상이어야 합니다");
        if (!TryPrepare(outer, exclusions, direction, spacing,
                out SplitContext context, out string failure))
            return AreaSplitResult.Fail(failure);

        if (count == 1)
        {
            AreaSplitBand? single = BuildBand(0, context.OuterRing, context.Holes,
                spacing, context.SplitAxisIsY, out string singleFailure);
            if (single == null) return AreaSplitResult.Fail(singleFailure);
            return new AreaSplitResult
            {
                Succeeded = true,
                Bands = new[] { single },
                Cuts = Array.Empty<double>(),
                EstimatedTotalLengthMm = single.EstimatedLengthMm,
                MaxToMinLengthRatio = 1.0,
                SplitAxisIsY = context.SplitAxisIsY
            };
        }

        // 2) 초기 절단: 원면적/간격 + 진행 높이를 균등 배분한다(단조 함수의 역산).
        double totalEstimate = AccumulatedEstimate(
            context.Rings, context.Min, context.Max, spacing, context.SplitAxisIsY);
        if (!double.IsFinite(totalEstimate) || totalEstimate <= 0)
            return AreaSplitResult.Fail("분할 영역의 예상 길이를 계산할 수 없습니다");
        var cuts = new List<double>(count - 1);
        for (int i = 1; i < count; i++)
        {
            double target = totalEstimate * i / count;
            cuts.Add(FindCut(context.Rings, context.Min, context.Max, target,
                spacing, context.SplitAxisIsY));
        }

        // 3) 마진을 반영한 예상 길이로 절단 위치를 보정한다.
        double lastRatio = double.PositiveInfinity;
        for (int step = 0; step < RefinementSteps; step++)
        {
            List<AreaSplitBand>? bands = BuildBands(
                cuts, context.Min, context.Max, context.Rings, spacing, context.SplitAxisIsY);
            if (bands == null || bands.Count != count)
                return AreaSplitResult.Fail("분할 영역을 구성하지 못했습니다");
            double ratio = LengthRatio(bands);
            if (double.IsFinite(ratio) && ratio <= LengthBalanceTolerance)
                return Success(bands, cuts, context.SplitAxisIsY);
            if (step > 0 && ratio >= lastRatio - 1e-9)
            {
                // 더 이상 개선되지 않는다. 남는 편차는 결과에 그대로 보고하고 확정한다.
                return double.IsFinite(ratio)
                    ? Success(bands, cuts, context.SplitAxisIsY)
                    : AreaSplitResult.Fail("분할 영역의 예상 길이를 계산할 수 없습니다");
            }
            lastRatio = ratio;

            bool moved = false;
            for (int j = 0; j < cuts.Count; j++)
            {
                double left = j == 0 ? context.Min : cuts[j - 1];
                double right = j == cuts.Count - 1 ? context.Max : cuts[j + 1];
                double density = CrossSectionLength(
                    context.Rings, cuts[j], context.SplitAxisIsY, context.Max - context.Min) / spacing + 1.0;
                if (!double.IsFinite(density) || density <= 0)
                    return AreaSplitResult.Fail("절단 위치의 단면을 계산할 수 없습니다");

                // 절단선을 오른쪽으로 옮기면 왼쪽 영역이 커진다. 길이 차이의 절반만 보정한다.
                double delta = (bands[j + 1].EstimatedLengthMm - bands[j].EstimatedLengthMm) / (2.0 * density);
                double limit = Math.Max(1e-9, (right - left) * 0.25);
                delta = Math.Clamp(delta, -limit, limit);
                cuts[j] = Math.Clamp(cuts[j] + delta, left + 1e-9, right - 1e-9);
                if (Math.Abs(delta) > 1e-12) moved = true;
                if (j > 0 && cuts[j] <= cuts[j - 1]) cuts[j] = cuts[j - 1] + 1e-6;
            }
            if (!moved) break;
        }

        List<AreaSplitBand>? finalBands = BuildBands(
            cuts, context.Min, context.Max, context.Rings, spacing, context.SplitAxisIsY);
        if (finalBands == null || finalBands.Count != count)
            return AreaSplitResult.Fail("분할 영역을 구성하지 못했습니다");
        double finalRatio = LengthRatio(finalBands);
        if (!double.IsFinite(finalRatio))
            return AreaSplitResult.Fail("분할 영역의 예상 길이를 계산할 수 없습니다");
        return Success(finalBands, cuts, context.SplitAxisIsY);
    }

    /// <summary>
    /// 주어진 절단 좌표로 분할 영역만 구성한다. 예상 길이 균등도 한도는 검사하지 않으므로,
    /// 호출자가 실제 2D 생성 결과의 길이로 후보를 평가할 수 있다. 절단 좌표가 영역 범위를
    /// 벗어나거나 밴드를 구성할 수 없으면 명시적으로 실패한다.
    /// </summary>
    public static AreaSplitResult TryBuildAtCuts(
        IReadOnlyList<Vec2> outer,
        IEnumerable<(double MinX, double MinY, double MaxX, double MaxY)> exclusions,
        PatternDirection direction,
        double spacing,
        IReadOnlyList<double> cuts)
    {
        if (!TryPrepare(outer, exclusions, direction, spacing,
                out SplitContext context, out string failure))
            return AreaSplitResult.Fail(failure);

        if (cuts == null || cuts.Count == 0)
        {
            AreaSplitBand? single = BuildBand(0, context.OuterRing, context.Holes,
                spacing, context.SplitAxisIsY, out string singleFailure);
            if (single == null) return AreaSplitResult.Fail(singleFailure);
            return new AreaSplitResult
            {
                Succeeded = true,
                Bands = new[] { single },
                Cuts = Array.Empty<double>(),
                EstimatedTotalLengthMm = single.EstimatedLengthMm,
                MaxToMinLengthRatio = 1.0,
                SplitAxisIsY = context.SplitAxisIsY
            };
        }

        double previous = context.Min;
        for (int i = 0; i < cuts.Count; i++)
        {
            double cut = cuts[i];
            if (!double.IsFinite(cut) || cut <= previous + 1e-9 || cut >= context.Max - 1e-9)
            {
                return AreaSplitResult.Fail(string.Format(CultureInfo.InvariantCulture,
                    "절단 위치 {0}이 분할 가능한 범위를 벗어났습니다", i + 1));
            }
            previous = cut;
        }

        List<AreaSplitBand>? bands = BuildBands(
            cuts.ToList(), context.Min, context.Max, context.Rings, spacing, context.SplitAxisIsY);
        if (bands == null)
            return AreaSplitResult.Fail("절단 위치로 분할 영역을 구성할 수 없습니다");
        return Success(bands, cuts.ToList(), context.SplitAxisIsY);
    }

    /// <summary>
    /// 가로 배치는 수평(Y=상수) 절단, 세로 배치는 수직(X=상수) 절단이다.
    /// Auto는 UI/생성기와 같은 해석인 가로(FrameX)로 본다. 저장된 Auto도 분할에서는
    /// 자동 장축 판정을 다시 하지 않는다.
    /// </summary>
    public static bool ResolveSplitAxisIsY(PatternDirection direction, IReadOnlyList<Vec2> outer)
    {
        _ = outer;
        return direction != PatternDirection.FrameY;
    }

    private static bool TryPrepare(
        IReadOnlyList<Vec2> outer,
        IEnumerable<(double MinX, double MinY, double MaxX, double MaxY)> exclusions,
        PatternDirection direction,
        double spacing,
        out SplitContext context,
        out string failure)
    {
        context = null!;
        failure = string.Empty;
        if (outer == null || outer.Count < 3)
        {
            failure = "분할할 적용영역 외곽이 없습니다";
            return false;
        }
        if (!double.IsFinite(spacing) || spacing <= 0)
        {
            failure = "발열사 간격은 0보다 커야 합니다";
            return false;
        }

        // 1) 유효 적용영역 = 적용영역 - 제외영역. 이 단계에서 갈라지거나 구멍이 나면
        //    이미 한 회로로 배치할 수 없는 영역이므로 분할도 명시적으로 거부한다.
        List<List<Vec2>> effective = RegionOps.SubtractRectangles(
            new List<IReadOnlyList<Vec2>> { outer },
            exclusions ?? Array.Empty<(double, double, double, double)>());
        if (!TryClassifyRings(effective,
                out List<Vec2>? regionOuter, out List<List<Vec2>> holes, out string ringFailure))
        {
            failure = ringFailure;
            return false;
        }
        List<Vec2> outerRing = regionOuter!;

        var rings = new List<List<Vec2>> { outerRing };
        rings.AddRange(holes);

        bool splitAxisIsY = ResolveSplitAxisIsY(direction, outer);
        double Coord(Vec2 p) => splitAxisIsY ? p.Y : p.X;

        double min = rings.SelectMany(r => r).Min(Coord);
        double max = rings.SelectMany(r => r).Max(Coord);
        if (!double.IsFinite(min) || !double.IsFinite(max) || max - min <= 0)
        {
            failure = "분할 축의 영역 범위를 계산할 수 없습니다";
            return false;
        }

        context = new SplitContext(outerRing, holes, rings, min, max, splitAxisIsY);
        return true;
    }

    private sealed record SplitContext(
        List<Vec2> OuterRing,
        List<List<Vec2>> Holes,
        List<List<Vec2>> Rings,
        double Min,
        double Max,
        bool SplitAxisIsY);

    private static AreaSplitResult Success(List<AreaSplitBand> bands, List<double> cuts, bool splitAxisIsY) =>
        new()
        {
            Succeeded = true,
            Bands = bands,
            Cuts = cuts.ToList(),
            EstimatedTotalLengthMm = bands.Sum(b => b.EstimatedLengthMm),
            MaxToMinLengthRatio = LengthRatio(bands),
            SplitAxisIsY = splitAxisIsY
        };

    private static double LengthRatio(IReadOnlyList<AreaSplitBand> bands)
    {
        if (bands.Count == 0) return double.PositiveInfinity;
        double min = bands.Min(b => b.EstimatedLengthMm);
        double max = bands.Max(b => b.EstimatedLengthMm);
        if (!double.IsFinite(min) || min <= 0) return double.PositiveInfinity;
        return max / min;
    }

    /// <summary>외곽(CCW)과 홀(CW)을 구분한다. 외곽이 둘 이상이거나 홀이 있으면 실패다.</summary>
    private static bool TryClassifyRings(List<List<Vec2>> rings, out List<Vec2>? outer,
        out List<List<Vec2>> holes, out string failure)
    {
        outer = null;
        holes = new List<List<Vec2>>();
        failure = "";
        var outers = new List<List<Vec2>>();
        foreach (List<Vec2> ring in rings)
        {
            if (ring.Count < 3) continue;
            if (RegionOps.SignedArea(ring) > 0) outers.Add(ring);
            else holes.Add(ring);
        }
        if (outers.Count == 0)
        {
            failure = "적용영역 - 제외영역이 비어 있습니다";
            return false;
        }
        if (outers.Count > 1)
        {
            failure = "제외영역이 적용영역을 둘 이상으로 분리합니다 - 허용되지 않는 제외영역입니다";
            return false;
        }
        if (holes.Count > 0)
        {
            failure = "제외영역이 적용영역 내부에 구멍을 만듭니다 - 허용되지 않는 제외영역입니다";
            return false;
        }
        outer = outers[0];
        return true;
    }

    /// <summary>슬래브 [min,t]의 (면적/간격 + 진행 높이)를 돌려주는 단조 증가 함수.</summary>
    private static double AccumulatedEstimate(List<List<Vec2>> rings, double min, double t,
        double spacing, bool splitAxisIsY)
    {
        double area = SlabArea(rings, min, t, splitAxisIsY);
        return area / spacing + Math.Max(0.0, t - min);
    }

    /// <summary>누적 예상 길이가 target이 되는 절단 좌표를 이분 탐색으로 찾는다.</summary>
    private static double FindCut(List<List<Vec2>> rings, double min, double max, double target,
        double spacing, bool splitAxisIsY)
    {
        double lo = min, hi = max;
        for (int i = 0; i < BisectionSteps; i++)
        {
            double mid = (lo + hi) * 0.5;
            double value = AccumulatedEstimate(rings, min, mid, spacing, splitAxisIsY);
            if (value < target) lo = mid;
            else hi = mid;
        }
        return (lo + hi) * 0.5;
    }

    /// <summary>절단 좌표에서 분할 축에 수직인 단면 길이(면적 변화율).</summary>
    private static double CrossSectionLength(List<List<Vec2>> rings, double at, bool splitAxisIsY, double span)
    {
        double delta = Math.Max(1e-4, span * 1e-6);
        double area = SlabArea(rings, at - delta, at + delta, splitAxisIsY);
        return area / (2.0 * delta);
    }

    private static double SlabArea(List<List<Vec2>> rings, double from, double to, bool splitAxisIsY)
    {
        List<List<Vec2>> clipped = ClipToSlab(rings, from, to, splitAxisIsY);
        // 클리핑 결과의 링 부호는 외곽 양수/홀 음수이므로 합이 순면적이다.
        return Math.Max(0.0, NetArea(clipped));
    }

    private static double NetArea(List<List<Vec2>> rings)
    {
        double sum = 0;
        foreach (List<Vec2> ring in rings) sum += RegionOps.SignedArea(ring);
        return sum;
    }

    private static List<List<Vec2>> ClipToSlab(List<List<Vec2>> rings, double from, double to, bool splitAxisIsY)
    {
        if (!(to > from)) return new List<List<Vec2>>();
        PathsD subject = new();
        foreach (List<Vec2> ring in rings) subject.Add(ToPath(ring));
        if (subject.Count == 0) return new List<List<Vec2>>();

        PathD slab = splitAxisIsY
            ? ToPath(new[]
            {
                new Vec2(-1e9, from), new Vec2(1e9, from),
                new Vec2(1e9, to), new Vec2(-1e9, to)
            })
            : ToPath(new[]
            {
                new Vec2(from, -1e9), new Vec2(to, -1e9),
                new Vec2(to, 1e9), new Vec2(from, 1e9)
            });
        PathsD result = Clipper.Intersect(subject, new PathsD { slab }, FillRule.NonZero, ClipperPrecision);
        return FromPaths(result);
    }

    /// <summary>절단 좌표들로 실제 밴드 영역을 구성한다. 홀이 생기면 null.</summary>
    private static List<AreaSplitBand>? BuildBands(List<double> cuts, double min, double max,
        List<List<Vec2>> rings, double spacing, bool splitAxisIsY)
    {
        var bands = new List<AreaSplitBand>();
        var bounds = new List<double>(cuts.Count + 2) { min };
        bounds.AddRange(cuts);
        bounds.Add(max);
        for (int i = 0; i + 1 < bounds.Count; i++)
        {
            List<List<Vec2>> clipped = ClipToSlab(rings, bounds[i], bounds[i + 1], splitAxisIsY);
            if (!TryClassifyRings(clipped, out List<Vec2>? bandOuter, out List<List<Vec2>> bandHoles, out _))
                return null;
            if (bandHoles.Count > 0) return null;   // 잘린 영역에 홀이 생기면 독립 영역으로 쓸 수 없다
            AreaSplitBand? band = BuildBand(i, bandOuter!, bandHoles, spacing, splitAxisIsY, out _);
            if (band == null) return null;
            bands.Add(band);
        }
        return bands;
    }

    /// <summary>
    /// 영역 하나의 예상 발열선 길이. 마진을 적용한 발열영역의 면적/간격에
    /// 사행 진행 높이(레인 사이 턴 연결선 근사)를 더한다.
    /// </summary>
    private static AreaSplitBand? BuildBand(int index, List<Vec2> outer, List<List<Vec2>> holes,
        double spacing, bool splitAxisIsY, out string failure)
    {
        failure = "";
        List<List<Vec2>> patternRegion = RegionOps.BuildPatternRegion(outer, holes);
        if (!TryClassifyRings(patternRegion, out _, out List<List<Vec2>> patternHoles, out _))
        {
            failure = string.Format(CultureInfo.InvariantCulture,
                "분할 영역 {0}은 마진 적용 후 발열영역이 남지 않습니다", index + 1);
            return null;
        }
        if (patternHoles.Count > 0)
        {
            failure = string.Format(CultureInfo.InvariantCulture,
                "분할 영역 {0}은 마진 적용 후 구멍이 생깁니다", index + 1);
            return null;
        }
        double area = Math.Abs(RegionOps.SignedArea(outer));
        double netArea = Math.Abs(NetArea(patternRegion));
        double height = ExtentAlong(patternRegion, splitAxisIsY);
        double estimate = netArea / spacing + height;
        if (!double.IsFinite(estimate) || estimate <= 0)
        {
            failure = string.Format(CultureInfo.InvariantCulture,
                "분할 영역 {0}의 예상 길이를 계산할 수 없습니다", index + 1);
            return null;
        }
        return new AreaSplitBand(index, outer.ToList(), area, estimate);
    }

    private static double ExtentAlong(List<List<Vec2>> rings, bool axisIsY)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (List<Vec2> ring in rings)
            foreach (Vec2 p in ring)
            {
                double v = axisIsY ? p.Y : p.X;
                if (v < min) min = v;
                if (v > max) max = v;
            }
        return double.IsFinite(min) && double.IsFinite(max) ? Math.Max(0.0, max - min) : 0.0;
    }

    private static PathD ToPath(IReadOnlyList<Vec2> ring)
    {
        var path = new PathD(ring.Count);
        foreach (Vec2 p in ring) path.Add(new PointD(p.X, p.Y));
        return path;
    }

    private static List<List<Vec2>> FromPaths(PathsD paths)
    {
        var result = new List<List<Vec2>>(paths.Count);
        foreach (PathD path in paths)
        {
            var ring = new List<Vec2>(path.Count);
            foreach (PointD p in path) ring.Add(new Vec2(p.x, p.y));
            if (ring.Count >= 3) result.Add(ring);
        }
        return result;
    }
}
