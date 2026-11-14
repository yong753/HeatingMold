using System;
using System.Collections.Generic;

namespace HeatingMold.Core.Geometry;

/// <summary>
/// 차감 전 행 구간 하나에서 제외 사각형을 뺀 조각. <see cref="ParentIntervalIndex"/>는
/// 차감 전 구간 번호이고 <see cref="ParentStartMm"/>/<see cref="ParentEndMm"/>는 그 구간의
/// 원래 양 끝이다. 제외영역 때문에 갈린 조각인지, 원래 경계가 만든 다른 구간인지는
/// 좌표를 다시 비교하지 않고 이 lineage로 판정한다.
/// </summary>
public readonly record struct LaneIntervalFragment(
    int ParentIntervalIndex,
    double StartMm,
    double EndMm,
    double ParentStartMm,
    double ParentEndMm)
{
    public double LengthMm => EndMm - StartMm;
}

/// <summary>한 행의 차감 전 구간과 제외 차감 후 조각.</summary>
public sealed class LaneRowIntervals2D
{
    public LaneRowIntervals2D(
        double sweepMm,
        IReadOnlyList<(double StartMm, double EndMm)> rawIntervals,
        IReadOnlyList<LaneIntervalFragment> fragments)
    {
        SweepMm = sweepMm;
        RawIntervals = rawIntervals ?? Array.Empty<(double, double)>();
        Fragments = fragments ?? Array.Empty<LaneIntervalFragment>();
    }

    public double SweepMm { get; }

    /// <summary>제외 사각형을 빼기 전, 외곽선만으로 만든 진행축 교차 구간.</summary>
    public IReadOnlyList<(double StartMm, double EndMm)> RawIntervals { get; }

    /// <summary>제외 사각형을 1D로 뺀 조각. 좌표는 외곽선 원본 값이며 격자 반올림이 없다.</summary>
    public IReadOnlyList<LaneIntervalFragment> Fragments { get; }

    public bool HasFragments => Fragments.Count > 0;
}

/// <summary>
/// 레인 행의 발열 구간 계산기. 외곽선의 행 교차 구간을 구한 뒤, 그 행을 실제로 가로지르는
/// 제외 사각형 구간만 1D로 차감한다. 외곽선 좌표는 원본 그대로 보존되므로, 선택 인출점이
/// 외곽선 위에 있는데도 제외영역 Boolean 결과의 격자 반올림 때문에 안쪽으로 오판되지 않는다.
///
/// 짧은 구간을 버리는 기준은 소비자 정책이므로 <c>minimumLengthMm</c>로 주입한다
/// (생성기는 1e-9, 공통 레인 격자는 1e-6).
/// </summary>
public static class LaneIntervalScan2D
{
    /// <summary>외곽선만으로 만든 행 교차 구간. 반개구간 [y0,y1) 판정으로 꼭짓점 중복을 막는다.</summary>
    public static List<(double StartMm, double EndMm)> RawIntervals(
        IReadOnlyList<Vec2> outer, double sweepMm, bool sweepAlongY, double minimumLengthMm)
    {
        var result = new List<(double StartMm, double EndMm)>(8);
        if (outer == null || outer.Count < 3) return result;
        if (!double.IsFinite(sweepMm) || !double.IsFinite(minimumLengthMm) || minimumLengthMm < 0)
            return result;

        var cuts = new List<double>(8);
        for (int i = 0; i < outer.Count; i++)
        {
            Vec2 a = outer[i];
            Vec2 b = outer[(i + 1) % outer.Count];
            double aSweep = sweepAlongY ? a.Y : a.X;
            double bSweep = sweepAlongY ? b.Y : b.X;
            if (aSweep <= sweepMm == bSweep <= sweepMm) continue;

            double t = (sweepMm - aSweep) / (bSweep - aSweep);
            double aLane = sweepAlongY ? a.X : a.Y;
            double bLane = sweepAlongY ? b.X : b.Y;
            cuts.Add(aLane + (bLane - aLane) * t);
        }

        cuts.Sort();
        for (int i = 0; i + 1 < cuts.Count; i += 2)
        {
            double start = cuts[i];
            double end = cuts[i + 1];
            if (end - start <= minimumLengthMm) continue;
            result.Add((start, end));
        }
        return result;
    }

    /// <summary>
    /// 차감 전 구간에서 제외 사각형을 순서대로 1D 차감한다. 사각형이 그 행을 실제로
    /// 가로지를 때만(진행축 최소·최대 사이) 적용하며, 남은 조각은 부모 구간 번호를 유지한다.
    /// </summary>
    public static List<LaneIntervalFragment> SubtractExclusions(
        IReadOnlyList<(double StartMm, double EndMm)> rawIntervals,
        IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY)> exclusions,
        double sweepMm,
        bool sweepAlongY,
        double minimumLengthMm)
    {
        var result = new List<LaneIntervalFragment>(rawIntervals?.Count ?? 0);
        if (rawIntervals == null || rawIntervals.Count == 0) return result;
        if (!double.IsFinite(sweepMm) || !double.IsFinite(minimumLengthMm) || minimumLengthMm < 0)
            return result;

        for (int index = 0; index < rawIntervals.Count; index++)
        {
            (double parentStart, double parentEnd) = rawIntervals[index];
            if (parentEnd - parentStart <= minimumLengthMm) continue;

            var pieces = new List<(double Start, double End)>(4) { (parentStart, parentEnd) };
            if (exclusions != null)
            {
                foreach ((double minX, double minY, double maxX, double maxY) in exclusions)
                {
                    double rectMinSweep = sweepAlongY ? minY : minX;
                    double rectMaxSweep = sweepAlongY ? maxY : maxX;
                    if (sweepMm <= rectMinSweep || sweepMm >= rectMaxSweep) continue;

                    double holeStart = sweepAlongY ? minX : minY;
                    double holeEnd = sweepAlongY ? maxX : maxY;
                    if (!double.IsFinite(holeStart) || !double.IsFinite(holeEnd)) continue;
                    if (holeStart > holeEnd) (holeStart, holeEnd) = (holeEnd, holeStart);

                    var next = new List<(double Start, double End)>(pieces.Count + 1);
                    foreach ((double start, double end) in pieces)
                    {
                        if (holeEnd <= start || holeStart >= end)
                        {
                            next.Add((start, end));
                            continue;
                        }
                        if (holeStart - start > minimumLengthMm) next.Add((start, holeStart));
                        if (end - holeEnd > minimumLengthMm) next.Add((holeEnd, end));
                    }
                    pieces = next;
                    if (pieces.Count == 0) break;
                }
            }

            foreach ((double start, double end) in pieces)
            {
                if (end - start <= minimumLengthMm) continue;
                result.Add(new LaneIntervalFragment(index, start, end, parentStart, parentEnd));
            }
        }
        return result;
    }

    /// <summary>한 행의 차감 전 구간과 제외 차감 후 조각을 함께 만든다.</summary>
    public static LaneRowIntervals2D Scan(
        IReadOnlyList<Vec2> outer,
        IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY)> exclusions,
        double sweepMm,
        bool sweepAlongY,
        double minimumLengthMm)
    {
        List<(double StartMm, double EndMm)> raw =
            RawIntervals(outer, sweepMm, sweepAlongY, minimumLengthMm);
        List<LaneIntervalFragment> fragments =
            SubtractExclusions(raw, exclusions, sweepMm, sweepAlongY, minimumLengthMm);
        return new LaneRowIntervals2D(sweepMm, raw, fragments);
    }
}
