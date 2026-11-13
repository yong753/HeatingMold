using HeatingMold.Core.Model;

namespace HeatingMold.Core.Geometry;

/// <summary>공통 레인 격자의 한 행. Sweep 좌표는 진행축(레인 사이를 가르는 축) 값이다.</summary>
public readonly record struct LaneGridRow(
    int Index,
    double SweepCoordinateMm,
    IReadOnlyList<(double StartMm, double EndMm)> Intervals)
{
    public bool HasIntervals => Intervals.Count > 0;

    public double CoveredLengthMm
    {
        get
        {
            double total = 0;
            foreach ((double start, double end) in Intervals)
                total += Math.Max(0, end - start);
            return total;
        }
    }
}

/// <summary>
/// 하나의 부모 영역에 대해 한 번만 만드는 공통 레인 격자.
/// 분할 자식들은 이 격자의 행을 다시 시작하지 않고 연속 구간으로 나눠 가진다.
/// </summary>
public sealed class LaneGridSpec
{
    public const string Version = "lane-grid-v1";
    private const double MergeTolerance = 1e-6;

    private LaneGridSpec(List<Vec2> outer, IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY)> exclusions,
        PatternDirection direction, double originSweepMm, double pitchMm, List<LaneGridRow> rows)
    {
        Outer = outer;
        Exclusions = exclusions;
        Direction = direction;
        OriginSweepMm = originSweepMm;
        PitchMm = pitchMm;
        Rows = rows;
    }

    public IReadOnlyList<Vec2> Outer { get; }
    public IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY)> Exclusions { get; }
    public PatternDirection Direction { get; }
    public double OriginSweepMm { get; }
    public double PitchMm { get; }
    public IReadOnlyList<LaneGridRow> Rows { get; }

    public bool IsSweepAlongY => Direction != PatternDirection.FrameY;

    /// <summary>진행축 좌표. FrameX/Auto는 Y, FrameY는 X.</summary>
    public double SweepOf(Vec2 point) => IsSweepAlongY ? point.Y : point.X;

    /// <summary>레인 방향 좌표. FrameX/Auto는 X, FrameY는 Y.</summary>
    public double LaneOf(Vec2 point) => IsSweepAlongY ? point.X : point.Y;

    public Vec2 PointAt(double sweepMm, double laneMm) => IsSweepAlongY
        ? new Vec2(laneMm, sweepMm)
        : new Vec2(sweepMm, laneMm);

    /// <summary>
    /// 부모 영역에 대한 공통 격자를 만든다. 원점은 부모 시작 인출점의 진행축 좌표이며,
    /// 행은 그 원점에서 중심 피치 간격으로 minIndex..maxIndex까지 만들어진다.
    /// </summary>
    public static bool TryBuild(
        IReadOnlyList<Vec2>? outer,
        IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY)>? exclusions,
        PatternDirection direction,
        double originSweepMm,
        double pitchMm,
        int minIndex,
        int maxIndex,
        out LaneGridSpec grid,
        out string failure)
    {
        grid = null!;
        failure = string.Empty;

        if (outer == null || outer.Count < 3)
        {
            failure = "공통 레인 격자를 만들 적용영역이 없습니다.";
            return false;
        }
        if (!double.IsFinite(originSweepMm))
        {
            failure = "공통 레인 격자의 시작 위치가 유효하지 않습니다.";
            return false;
        }
        if (!double.IsFinite(pitchMm) || pitchMm <= 0)
        {
            failure = "공통 레인 격자의 간격이 유효하지 않습니다.";
            return false;
        }
        if (maxIndex < minIndex)
        {
            failure = "공통 레인 격자의 행 범위가 뒤집혔습니다.";
            return false;
        }

        PatternDirection effective = direction == PatternDirection.Auto ? PatternDirection.FrameX : direction;
        var rects = new List<(double MinX, double MinY, double MaxX, double MaxY)>();
        if (exclusions != null)
        {
            foreach (var rect in exclusions)
            {
                if (!double.IsFinite(rect.MinX) || !double.IsFinite(rect.MinY) ||
                    !double.IsFinite(rect.MaxX) || !double.IsFinite(rect.MaxY)) continue;
                if (rect.MaxX - rect.MinX <= MergeTolerance || rect.MaxY - rect.MinY <= MergeTolerance) continue;
                rects.Add(rect);
            }
        }

        var rows = new List<LaneGridRow>(Math.Max(0, maxIndex - minIndex + 1));
        for (int index = minIndex; index <= maxIndex; ++index)
        {
            double sweep = originSweepMm + index * pitchMm;
            List<(double StartMm, double EndMm)> intervals = Scanline(outer, rects, effective, sweep);
            rows.Add(new LaneGridRow(index, sweep, intervals));
        }

        grid = new LaneGridSpec(new List<Vec2>(outer), rects, effective, originSweepMm, pitchMm, rows);
        return true;
    }

    /// <summary>
    /// 진행축 좌표 sweep에서의 발열 구간을 레인 방향 좌표로 돌려준다.
    /// 외곽 링과 제외 사각형만 사용하는 2D 스캔라인이며, 반개구간 판정으로 중복을 막는다.
    /// </summary>
    public static List<(double StartMm, double EndMm)> Scanline(
        IReadOnlyList<Vec2> outer,
        IReadOnlyList<(double MinX, double MinY, double MaxX, double MaxY)> exclusions,
        PatternDirection direction,
        double sweepMm)
    {
        bool alongY = direction != PatternDirection.FrameY;
        var cuts = new List<double>(8);

        for (int i = 0; i < outer.Count; ++i)
        {
            Vec2 a = outer[i];
            Vec2 b = outer[(i + 1) % outer.Count];
            double aSweep = alongY ? a.Y : a.X;
            double bSweep = alongY ? b.Y : b.X;
            if (aSweep <= sweepMm == bSweep <= sweepMm) continue;

            double t = (sweepMm - aSweep) / (bSweep - aSweep);
            double aLane = alongY ? a.X : a.Y;
            double bLane = alongY ? b.X : b.Y;
            cuts.Add(aLane + (bLane - aLane) * t);
        }

        cuts.Sort();
        var intervals = new List<(double StartMm, double EndMm)>(cuts.Count / 2 + 1);
        for (int i = 0; i + 1 < cuts.Count; i += 2)
        {
            double start = cuts[i];
            double end = cuts[i + 1];
            if (end - start <= MergeTolerance) continue;
            intervals.Add((start, end));
        }

        if (intervals.Count == 0 || exclusions.Count == 0) return intervals;

        List<(double StartMm, double EndMm)> current = intervals;
        foreach (var rect in exclusions)
        {
            double rectMinSweep = alongY ? rect.MinY : rect.MinX;
            double rectMaxSweep = alongY ? rect.MaxY : rect.MaxX;
            if (sweepMm <= rectMinSweep || sweepMm >= rectMaxSweep) continue;

            double holeStart = alongY ? rect.MinX : rect.MinY;
            double holeEnd = alongY ? rect.MaxX : rect.MaxY;
            var next = new List<(double StartMm, double EndMm)>(current.Count + 1);
            foreach ((double start, double end) in current)
            {
                if (holeEnd <= start || holeStart >= end)
                {
                    next.Add((start, end));
                    continue;
                }
                if (holeStart - start > MergeTolerance) next.Add((start, holeStart));
                if (end - holeEnd > MergeTolerance) next.Add((holeEnd, end));
            }
            current = next;
            if (current.Count == 0) break;
        }

        return current;
    }
}
