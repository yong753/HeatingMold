using System.Globalization;

namespace HeatingMold.Core.Geometry;

/// <summary>길이를 표시하는 경로 조각의 종류.</summary>
public enum RouteLengthLabelKind
{
    /// <summary>가열 레인 구간.</summary>
    HeatingRun,

    /// <summary>가열 레인 사이를 잇는 턴.</summary>
    TurnConnector,
}

/// <summary>
/// 확정 경로 조각 하나의 길이 라벨 원본입니다. 길이는 확정된 실제 3D 길이고,
/// 2D 점은 확정 작업평면 투영입니다. 두 점 목록은 같은 순서·같은 개수여야 합니다.
/// </summary>
public sealed record RouteLengthLabelSource(
    string AreaName,
    int PieceIndex,
    SerpentineRoutePieceKind PieceKind,
    int LaneIndex,
    double LengthMm,
    IReadOnlyList<Vec2> Points2D,
    IReadOnlyList<Vec3>? Points3D = null);

/// <summary>길이 라벨 배치 정책입니다. 길이 단위는 mm입니다.</summary>
public sealed record RouteLengthLabelOptions
{
    /// <summary>경로 선에서 문자 중심까지의 기본 거리.</summary>
    public double OffsetMm { get; init; } = 6.0;

    /// <summary>문자 높이. 문자 폭 추정과 겹침 검사에 사용합니다.</summary>
    public double TextHeightMm { get; init; } = 8.0;

    /// <summary>선을 따라 이동해 볼 최대 칸 수.</summary>
    public int MaxShiftSteps { get; init; } = 3;
}

/// <summary>배치가 끝난 길이 라벨입니다.</summary>
public sealed record RouteLengthLabel(
    string AreaName,
    int PieceIndex,
    RouteLengthLabelKind Kind,
    int LaneIndex,
    double LengthMm,
    string Text,
    Vec2 Position2D,
    double RotationDeg,
    bool HasLeader,
    Vec2 LeaderStart2D,
    Vec2 LeaderEnd2D,
    Vec3? Position3D);

/// <summary>
/// 확정 경로의 실제 길이를 경로 위에 직접 표시하는 라벨을 만듭니다.
/// 레인은 선 위쪽 여백, 턴은 인접 레인 반대쪽 바깥에 배치하며 숫자만 씁니다.
/// </summary>
public static class RouteLengthLabels
{
    /// <summary>도면에 한 번만 쓰는 단위 안내입니다.</summary>
    public const string UnitNote = "단위: mm / 실제 경로 길이";

    private const double Epsilon = 1e-9;
    private const double WidthPerHeight = 0.62;

    /// <summary>길이 숫자 하나를 소수 1자리로 표시합니다.</summary>
    public static bool TryFormatLength(double lengthMm, out string text)
    {
        text = string.Empty;
        if (!double.IsFinite(lengthMm) || lengthMm < 0) return false;
        text = lengthMm.ToString("F1", CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>
    /// 라벨 원본 전체를 한 번에 배치합니다. 하나라도 유효하지 않으면 실패하며
    /// 부분 결과를 돌려주지 않습니다.
    /// </summary>
    public static bool TryBuild(
        IReadOnlyList<RouteLengthLabelSource> sources,
        ProjectionFrame frame,
        RouteLengthLabelOptions? options,
        out List<RouteLengthLabel> labels,
        out string? error)
    {
        labels = new List<RouteLengthLabel>();
        error = null;
        if (sources == null) { error = "길이 라벨 원본이 없습니다"; return false; }
        if (frame == null) { error = "확정 작업평면 프레임이 없습니다"; return false; }
        options ??= new RouteLengthLabelOptions();
        if (!double.IsFinite(options.OffsetMm) || options.OffsetMm <= 0)
        {
            error = "길이 라벨 여백이 유효하지 않습니다";
            return false;
        }
        if (!double.IsFinite(options.TextHeightMm) || options.TextHeightMm <= 0)
        {
            error = "길이 라벨 문자 높이가 유효하지 않습니다";
            return false;
        }

        // 실제로 그려지는 모든 선분을 겹침 장애물로 모읍니다.
        // 라벨 대상이 아닌 연결선도 선이므로 라벨이 덮으면 안 됩니다.
        var obstacles = new List<Obstacle>();
        foreach (var source in sources)
        {
            if (source?.Points2D == null) continue;
            for (int i = 0; i + 1 < source.Points2D.Count; i++)
            {
                var a = source.Points2D[i];
                var b = source.Points2D[i + 1];
                if (!IsFinite(a) || !IsFinite(b)) continue;
                if (Distance(a, b) <= Epsilon) continue;
                obstacles.Add(new Obstacle(source, a, b));
            }
        }

        var targets = new List<Target>();
        foreach (var source in sources)
        {
            if (source == null) { error = "길이 라벨 원본에 빈 항목이 있습니다"; return false; }
            if (source.PieceKind == SerpentineRoutePieceKind.FixedConnector) continue;
            var kind = source.PieceKind == SerpentineRoutePieceKind.HeatingRun
                ? RouteLengthLabelKind.HeatingRun
                : RouteLengthLabelKind.TurnConnector;
            if (!double.IsFinite(source.LengthMm) || source.LengthMm <= 0)
            {
                error = $"영역 {source.AreaName} 경로 조각 {source.PieceIndex + 1}의 확정 길이가 유효하지 않습니다";
                return false;
            }
            if (source.Points2D == null || source.Points2D.Count < 2)
            {
                error = $"영역 {source.AreaName} 경로 조각 {source.PieceIndex + 1}의 투영 점이 부족합니다";
                return false;
            }
            if (source.Points3D != null && source.Points3D.Count != source.Points2D.Count)
            {
                error = $"영역 {source.AreaName} 경로 조각 {source.PieceIndex + 1}의 2D/3D 점 개수가 다릅니다";
                return false;
            }
            foreach (var p in source.Points2D)
            {
                if (!IsFinite(p))
                {
                    error = $"영역 {source.AreaName} 경로 조각 {source.PieceIndex + 1}의 투영 점이 유효하지 않습니다";
                    return false;
                }
            }
            targets.Add(new Target(source, kind));
        }

        var heatingRuns = targets
            .Where(t => t.Kind == RouteLengthLabelKind.HeatingRun)
            .ToList();
        var ordered = targets
            .OrderBy(t => t.Kind == RouteLengthLabelKind.HeatingRun ? 0 : 1)
            .ThenBy(t => t.Source.PieceIndex)
            .ToList();

        var placed = new List<OrientedBox>();
        foreach (var target in ordered)
        {
            if (!TryPlaceOne(target, heatingRuns, obstacles, placed, options, frame, out var label, out error))
                return false;
            labels.Add(label);
            placed.Add(new OrientedBox(
                label.Position2D,
                label.RotationDeg,
                EstimateWidth(label.Text, options.TextHeightMm) * 0.5,
                options.TextHeightMm * 0.5));
        }

        return true;
    }

    private static bool TryPlaceOne(
        Target target,
        IReadOnlyList<Target> heatingRuns,
        IReadOnlyList<Obstacle> obstacles,
        IReadOnlyList<OrientedBox> placed,
        RouteLengthLabelOptions options,
        ProjectionFrame frame,
        out RouteLengthLabel label,
        out string? error)
    {
        label = null!;
        error = null;
        var source = target.Source;

        if (!TryFindAnchor(source.Points2D, out int segment, out double t, out var point, out var direction, out error))
        {
            error = $"영역 {source.AreaName} 경로 조각 {source.PieceIndex + 1}: {error}";
            return false;
        }

        if (!TryFormatLength(source.LengthMm, out string text))
        {
            error = $"영역 {source.AreaName} 경로 조각 {source.PieceIndex + 1}의 길이를 표시할 수 없습니다";
            return false;
        }

        var normal = new Vec2(-direction.Y, direction.X);
        bool leftward = IsLeftward(direction);
        if (target.Kind == RouteLengthLabelKind.HeatingRun)
        {
            // 왕복 진행 방향과 관계없이 같은 쪽(위쪽) 여백을 씁니다.
            if (leftward) normal = new Vec2(-normal.X, -normal.Y);
        }
        else
        {
            var negated = new Vec2(-normal.X, -normal.Y);
            bool flip;
            if (TryTurnInward(target, heatingRuns, out var inward))
            {
                double side = Dot(normal, inward);
                flip = Math.Abs(side) <= 1e-6
                    ? DistanceToHeatingRuns(target, heatingRuns, point, normal, options.OffsetMm)
                        < DistanceToHeatingRuns(target, heatingRuns, point, negated, options.OffsetMm)
                    : side > 0;
            }
            else
            {
                flip = DistanceToHeatingRuns(target, heatingRuns, point, normal, options.OffsetMm)
                    < DistanceToHeatingRuns(target, heatingRuns, point, negated, options.OffsetMm);
            }
            if (flip) normal = negated;
        }

        // 글자가 뒤집히지 않도록 진행 방향을 정규화하고 회전을 (-90, 90]로 맞춥니다.
        var textDirection = leftward
            ? new Vec2(-direction.X, -direction.Y)
            : direction;
        double rotation = NormalizeRotation(Math.Atan2(textDirection.Y, textDirection.X) * 180.0 / Math.PI);

        double width = EstimateWidth(text, options.TextHeightMm);
        double step = options.TextHeightMm * 1.3;
        var otherObstacles = obstacles.Where(o => !ReferenceEquals(o.Source, source)).ToList();

        foreach (double offsetMultiplier in new[] { 1.0, 2.2 })
        {
            for (int index = 0; index <= Math.Max(0, options.MaxShiftSteps); index++)
            {
                foreach (int sign in index == 0 ? new[] { 0 } : new[] { 1, -1 })
                {
                    double shift = step * index * sign;
                    var position = new Vec2(
                        point.X + normal.X * options.OffsetMm * offsetMultiplier + textDirection.X * shift,
                        point.Y + normal.Y * options.OffsetMm * offsetMultiplier + textDirection.Y * shift);
                    var box = new OrientedBox(
                        position, rotation, width * 0.5, options.TextHeightMm * 0.5);
                    if (placed.Any(p => p.Overlaps(box))) continue;
                    if (otherObstacles.Any(o => SegmentIntersectsBox(o.A, o.B, box))) continue;

                    label = BuildLabel(
                        target, text, position, rotation, offsetMultiplier > 1.0,
                        point, normal, options.TextHeightMm, segment, t, frame);
                    return true;
                }
            }
        }

        // 빈 공간을 찾지 못하면 조금 더 띄우고 인출선으로 경로와의 대응을 남깁니다.
        var fallback = new Vec2(
            point.X + normal.X * options.OffsetMm * 2.6,
            point.Y + normal.Y * options.OffsetMm * 2.6);
        label = BuildLabel(
            target, text, fallback, rotation, true,
            point, normal, options.TextHeightMm, segment, t, frame);
        return true;
    }

    private static RouteLengthLabel BuildLabel(
        Target target,
        string text,
        Vec2 position,
        double rotation,
        bool leader,
        Vec2 routePoint,
        Vec2 normal,
        double height,
        int segment,
        double t,
        ProjectionFrame frame)
    {
        var leaderEnd = new Vec2(
            position.X - normal.X * height * 0.5,
            position.Y - normal.Y * height * 0.5);

        Vec3? position3D = null;
        var points3D = target.Source.Points3D;
        if (points3D != null && segment + 1 < points3D.Count)
        {
            var a = points3D[segment];
            var b = points3D[segment + 1];
            var base3 = new Vec3(
                a.X + (b.X - a.X) * t,
                a.Y + (b.Y - a.Y) * t,
                a.Z + (b.Z - a.Z) * t);
            double dx = position.X - routePoint.X;
            double dy = position.Y - routePoint.Y;
            position3D = new Vec3(
                base3.X + frame.AxisX.X * dx + frame.AxisY.X * dy,
                base3.Y + frame.AxisX.Y * dx + frame.AxisY.Y * dy,
                base3.Z + frame.AxisX.Z * dx + frame.AxisY.Z * dy);
        }

        return new RouteLengthLabel(
            target.Source.AreaName,
            target.Source.PieceIndex,
            target.Kind,
            target.Source.LaneIndex,
            target.Source.LengthMm,
            text,
            position,
            rotation,
            leader,
            routePoint,
            leaderEnd,
            position3D);
    }

    /// <summary>조각의 호길이 중앙에서 위치·방향을 구합니다.</summary>
    private static bool TryFindAnchor(
        IReadOnlyList<Vec2> points,
        out int segment,
        out double t,
        out Vec2 point,
        out Vec2 direction,
        out string? error)
    {
        segment = 0;
        t = 0;
        point = default;
        direction = default;
        error = null;

        double total = 0;
        for (int i = 0; i + 1 < points.Count; i++) total += Distance(points[i], points[i + 1]);
        if (!double.IsFinite(total) || total <= Epsilon)
        {
            error = "경로 조각의 2D 투영이 퇴화했습니다";
            return false;
        }

        double target = total * 0.5;
        double run = 0;
        for (int i = 0; i + 1 < points.Count; i++)
        {
            double length = Distance(points[i], points[i + 1]);
            if (length <= Epsilon) continue;
            if (run + length >= target)
            {
                segment = i;
                t = Math.Clamp((target - run) / length, 0.0, 1.0);
                point = new Vec2(
                    points[i].X + (points[i + 1].X - points[i].X) * t,
                    points[i].Y + (points[i + 1].Y - points[i].Y) * t);
                direction = new Vec2(
                    (points[i + 1].X - points[i].X) / length,
                    (points[i + 1].Y - points[i].Y) / length);
                return true;
            }
            run += length;
        }

        for (int i = points.Count - 2; i >= 0; i--)
        {
            double length = Distance(points[i], points[i + 1]);
            if (length <= Epsilon) continue;
            segment = i;
            t = 0.5;
            point = new Vec2((points[i].X + points[i + 1].X) * 0.5, (points[i].Y + points[i + 1].Y) * 0.5);
            direction = new Vec2(
                (points[i + 1].X - points[i].X) / length,
                (points[i + 1].Y - points[i].Y) / length);
            return true;
        }

        error = "경로 조각의 2D 투영이 퇴화했습니다";
        return false;
    }

    /// <summary>
    /// 턴 양쪽에 붙은 가열 레인이 뻗어 있는 방향(안쪽)을 구합니다.
    /// 턴은 직선 연결일 수도 있으므로 U자나 원호를 가정하지 않습니다.
    /// </summary>
    private static bool TryTurnInward(
        Target turn,
        IReadOnlyList<Target> heatingRuns,
        out Vec2 inward)
    {
        inward = default;
        var points = turn.Source.Points2D;
        Target? previous = null;
        Target? next = null;
        foreach (var run in heatingRuns)
        {
            if (!string.Equals(run.Source.AreaName, turn.Source.AreaName, StringComparison.Ordinal)) continue;
            if (run.Source.PieceIndex < turn.Source.PieceIndex &&
                (previous == null || run.Source.PieceIndex > previous.Source.PieceIndex))
                previous = run;
            if (run.Source.PieceIndex > turn.Source.PieceIndex &&
                (next == null || run.Source.PieceIndex < next.Source.PieceIndex))
                next = run;
        }

        var sum = new Vec2(0, 0);
        int count = 0;
        if (previous != null)
        {
            sum += PolylineMidpoint(previous.Source.Points2D) - points[0];
            count++;
        }
        if (next != null)
        {
            sum += PolylineMidpoint(next.Source.Points2D) - points[^1];
            count++;
        }
        if (count == 0) return false;

        var average = new Vec2(sum.X / count, sum.Y / count);
        double length = average.Length;
        if (!double.IsFinite(length) || length <= Epsilon) return false;
        inward = new Vec2(average.X / length, average.Y / length);
        return true;
    }

    private static double DistanceToHeatingRuns(
        Target turn,
        IReadOnlyList<Target> heatingRuns,
        Vec2 point,
        Vec2 normal,
        double offset)
    {
        var candidate = new Vec2(point.X + normal.X * offset, point.Y + normal.Y * offset);
        double best = double.PositiveInfinity;
        foreach (var run in heatingRuns)
        {
            if (ReferenceEquals(run.Source, turn.Source)) continue;
            var points = run.Source.Points2D;
            for (int i = 0; i + 1 < points.Count; i++)
            {
                double distance = PointToSegmentDistance(candidate, points[i], points[i + 1]);
                if (distance < best) best = distance;
            }
        }
        return double.IsFinite(best) ? best : 0;
    }

    private static Vec2 PolylineMidpoint(IReadOnlyList<Vec2> points)
    {
        double total = 0;
        for (int i = 0; i + 1 < points.Count; i++) total += Distance(points[i], points[i + 1]);
        if (!(total > Epsilon)) return points[0];
        double target = total * 0.5;
        double run = 0;
        for (int i = 0; i + 1 < points.Count; i++)
        {
            double length = Distance(points[i], points[i + 1]);
            if (length <= Epsilon) continue;
            if (run + length >= target)
            {
                double t = Math.Clamp((target - run) / length, 0.0, 1.0);
                return new Vec2(
                    points[i].X + (points[i + 1].X - points[i].X) * t,
                    points[i].Y + (points[i + 1].Y - points[i].Y) * t);
            }
            run += length;
        }
        return points[^1];
    }

    private static double NormalizeRotation(double degrees)
    {
        while (degrees > 90 + 1e-9) degrees -= 180;
        while (degrees <= -90 - 1e-9) degrees += 180;
        return degrees;
    }

    private static double EstimateWidth(string text, double height) =>
        Math.Max(1, text.Length) * height * WidthPerHeight;

    private static bool IsLeftward(Vec2 direction) =>
        direction.X < 0 || (Math.Abs(direction.X) <= Epsilon && direction.Y < 0);

    private static bool IsFinite(Vec2 p) => double.IsFinite(p.X) && double.IsFinite(p.Y);

    private static double Distance(Vec2 a, Vec2 b) => (a - b).Length;

    private static double Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;

    private static double PointToSegmentDistance(Vec2 p, Vec2 a, Vec2 b)
    {
        var ab = b - a;
        double lengthSquared = Dot(ab, ab);
        if (lengthSquared <= Epsilon * Epsilon) return (p - a).Length;
        double t = Math.Clamp(Dot(p - a, ab) / lengthSquared, 0.0, 1.0);
        var projection = new Vec2(a.X + ab.X * t, a.Y + ab.Y * t);
        return (p - projection).Length;
    }

    private static bool SegmentIntersectsBox(Vec2 a, Vec2 b, OrientedBox box)
    {
        var segment = b - a;
        double length = segment.Length;
        var axis = length <= Epsilon
            ? new Vec2(1, 0)
            : new Vec2(segment.X / length, segment.Y / length);
        var normal = new Vec2(-axis.Y, axis.X);
        return OverlapsAxis(box.AxisX)
            && OverlapsAxis(box.AxisY)
            && OverlapsAxis(normal);

        bool OverlapsAxis(Vec2 onAxis)
        {
            double center = Dot(box.Center, onAxis);
            double radius = Math.Abs(Dot(box.AxisX, onAxis)) * box.HalfWidth
                + Math.Abs(Dot(box.AxisY, onAxis)) * box.HalfHeight;
            double first = Dot(a, onAxis);
            double second = Dot(b, onAxis);
            double low = Math.Min(first, second);
            double high = Math.Max(first, second);
            return high >= center - radius && low <= center + radius;
        }
    }

    private sealed record Target(RouteLengthLabelSource Source, RouteLengthLabelKind Kind);

    private readonly record struct Obstacle(RouteLengthLabelSource Source, Vec2 A, Vec2 B);

    /// <summary>문자 영역을 근사하는 회전 사각형입니다.</summary>
    private readonly record struct OrientedBox(
        Vec2 Center, double RotationDeg, double HalfWidth, double HalfHeight)
    {
        public Vec2 AxisX
        {
            get
            {
                double radians = RotationDeg * Math.PI / 180.0;
                return new Vec2(Math.Cos(radians), Math.Sin(radians));
            }
        }

        public Vec2 AxisY
        {
            get
            {
                double radians = RotationDeg * Math.PI / 180.0;
                return new Vec2(-Math.Sin(radians), Math.Cos(radians));
            }
        }

        public bool Overlaps(OrientedBox other) =>
            OverlapsOn(this, other, AxisX)
            && OverlapsOn(this, other, AxisY)
            && OverlapsOn(this, other, other.AxisX)
            && OverlapsOn(this, other, other.AxisY);

        private static bool OverlapsOn(OrientedBox first, OrientedBox second, Vec2 axis)
        {
            double mine = Math.Abs(Dot(first.AxisX, axis)) * first.HalfWidth
                + Math.Abs(Dot(first.AxisY, axis)) * first.HalfHeight;
            double theirs = Math.Abs(Dot(second.AxisX, axis)) * second.HalfWidth
                + Math.Abs(Dot(second.AxisY, axis)) * second.HalfHeight;
            return Math.Abs(Dot(second.Center - first.Center, axis)) <= mine + theirs;
        }
    }
}
