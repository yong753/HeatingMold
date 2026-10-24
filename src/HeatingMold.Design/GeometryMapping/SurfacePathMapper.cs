using HeatingMold.Core.Geometry;

namespace HeatingMold.Design.GeometryMapping;

/// <summary>작업 평면의 점과 표면에 매핑된 3D 점 및 Face 식별자.</summary>
public readonly record struct SurfaceMappedSample(Vec2 DatumPoint, Vec3 Point, int FaceId);

/// <summary>작업 평면의 발열사 경로를 지정된 표면 영역 전체에 매핑하고 연속성을 확인한다.</summary>
public static class SurfacePathMapper
{
    // 깊이 0에서 사용할 보수적 목표입니다. 승인된 leaf에는 1/4 간격 probe를 두며
    // probe보다 좁은 형상 특징까지 확인한다고 보장하지는 않습니다.
    private const double MaxPlanarSpacing = 0.1;
    private const double MaxChordError = 0.05;
    private const int MaxDepth = 18;
    private const int DefaultMaxProbes = 100_000;

    public static bool AreSamplesContinuous(
        IReadOnlyList<SurfaceMappedSample> samples,
        Func<int, int, bool> adjacent,
        double max3DStep = 2.0)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(adjacent);
        if (!double.IsFinite(max3DStep) || max3DStep <= 0)
            throw new ArgumentOutOfRangeException(nameof(max3DStep));

        for (int index = 1; index < samples.Count; index++)
        {
            SurfaceMappedSample previous = samples[index - 1];
            SurfaceMappedSample current = samples[index];
            bool stepTooLong = (current.Point - previous.Point).Length > max3DStep;
            bool transitionIsNotAdjacent = previous.FaceId != current.FaceId &&
                !adjacent(previous.FaceId, current.FaceId);

            if (stepTooLong || transitionIsNotAdjacent)
                return false;
        }

        return true;
    }

    /// <summary>
    /// 양 끝점·1/4점·중간점을 표본화하고, 평면 간격(0.1 mm)과 현 오차(0.05 mm)가
    /// 허용 범위에 들 때까지 세분화한다. hit 함수는 선택된 표면 영역 전체를 조회해야 한다.
    /// 누락되거나 잘못된 hit가 있거나 세분화 한도에 도달하면 경로 전체를 거부하고,
    /// 일부만 매핑한 결과는 반환하지 않는다. 연속 중복점만 제거하여 앞서 지난 꼭짓점으로
    /// 의도적으로 되돌아오는 경로는 보존한다.
    /// </summary>
    public static bool TryMap(
        IReadOnlyList<Vec2> path,
        Func<Vec2, Vec3?> hit,
        out List<Vec3> mapped,
        out string failure,
        int maxProbes = DefaultMaxProbes)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(hit);
        if (maxProbes < 1) throw new ArgumentOutOfRangeException(nameof(maxProbes));
        mapped = new List<Vec3>();
        failure = string.Empty;
        if (path.Count == 0)
        {
            failure = "평면 경로가 비어 있습니다.";
            return false;
        }

        var result = new List<Vec3>();
        string error = string.Empty;
        int probes = 0;

        bool TryProbe(Vec2 point, out Vec3 value)
        {
            value = default;
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            {
                error = "평면 경로에 유한하지 않은 좌표가 있습니다.";
                return false;
            }
            if (probes >= maxProbes)
            {
                error = $"표면 매핑이 {maxProbes}회 질의 한도를 넘었습니다.";
                return false;
            }
            probes++;
            Vec3? sample = hit(point);
            if (!sample.HasValue)
            {
                error = "경로가 선택한 표면 영역을 벗어납니다.";
                return false;
            }
            value = sample.Value;
            if (!double.IsFinite(value.X) || !double.IsFinite(value.Y) || !double.IsFinite(value.Z))
            {
                error = "표면 hit에 유한하지 않은 모델 좌표가 있습니다.";
                return false;
            }
            return true;
        }

        void Append(Vec3 point)
        {
            if (result.Count == 0 || result[^1] != point)
                result.Add(point);
        }

        bool Refine(Vec2 a, Vec3 mappedA, Vec2 b, Vec3 mappedB, int depth)
        {
            Vec2 midpoint = a * 0.5 + b * 0.5;
            if (!TryProbe(midpoint, out Vec3 mappedMidpoint))
                return false;
            Vec2 quarter = a * 0.75 + b * 0.25;
            Vec2 threeQuarter = a * 0.25 + b * 0.75;
            if (!TryProbe(quarter, out Vec3 mappedQuarter) ||
                !TryProbe(threeQuarter, out Vec3 mappedThreeQuarter)) return false;

            // 무한 chord 직선까지의 거리 대신 같은 매개변수의 점을 비교합니다.
            // 따라서 chord 선을 따라 발생한 점프도 세분화 대상이 됩니다.
            Vec3 chordMidpoint = mappedA * 0.5 + mappedB * 0.5;
            Vec3 chordQuarter = mappedA * 0.75 + mappedB * 0.25;
            Vec3 chordThreeQuarter = mappedA * 0.25 + mappedB * 0.75;
            if (a.DistanceTo(b) <= MaxPlanarSpacing &&
                (mappedMidpoint - chordMidpoint).Length <= MaxChordError &&
                (mappedQuarter - chordQuarter).Length <= MaxChordError &&
                (mappedThreeQuarter - chordThreeQuarter).Length <= MaxChordError)
            {
                // 승인된 leaf의 정렬된 기하에 포함된 probe입니다. 이를 버리면 양 끝점이
                // 같더라도 그 사이의 좁은 Face/domain 전이를 놓칠 수 있습니다.
                Append(mappedQuarter);
                Append(mappedMidpoint);
                Append(mappedThreeQuarter);
                Append(mappedB);
                return true;
            }
            if (depth >= MaxDepth || midpoint == a || midpoint == b)
            {
                error = $"표면 매핑이 깊이 {MaxDepth} 안에서 불연속 또는 표본 공차를 해소하지 못했습니다.";
                return false;
            }
            return Refine(a, mappedA, midpoint, mappedMidpoint, depth + 1) &&
                Refine(midpoint, mappedMidpoint, b, mappedB, depth + 1);
        }

        if (!TryProbe(path[0], out Vec3 previous))
        {
            failure = error;
            return false;
        }
        Append(previous);
        for (int i = 1; i < path.Count; i++)
        {
            if (path[i] == path[i - 1])
                continue;
            if (!TryProbe(path[i], out Vec3 next) ||
                !Refine(path[i - 1], previous, path[i], next, 0))
            {
                failure = error;
                return false;
            }
            previous = next;
        }
        mapped = result;
        return true;
    }

    /// <summary>경로 순서를 유지하고 Face 식별자를 보존하며 설정된 공차까지 적응 매핑합니다.</summary>
    public static bool TryMapWithMetadata(
        IReadOnlyList<Vec2> path,
        Func<Vec2, SurfaceMappedSample?> hit,
        out List<SurfaceMappedSample> mapped,
        out string failure,
        int maxProbes = DefaultMaxProbes,
        double maximumPlanarSpacingMm = MaxPlanarSpacing,
        double maximumChordErrorMm = MaxChordError,
        int maximumDepth = MaxDepth)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(hit);
        if (maxProbes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxProbes));
        if (!double.IsFinite(maximumPlanarSpacingMm) || maximumPlanarSpacingMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumPlanarSpacingMm));
        if (!double.IsFinite(maximumChordErrorMm) || maximumChordErrorMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumChordErrorMm));
        if (maximumDepth < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumDepth));
        if (path.Count == 0)
        {
            mapped = [];
            failure = "평면 경로가 비어 있습니다.";
            return false;
        }

        var result = new List<SurfaceMappedSample>();
        string error = string.Empty;
        int probeCount = 0;

        bool TryProbe(Vec2 workingUv, out SurfaceMappedSample sample)
        {
            sample = default;
            if (!double.IsFinite(workingUv.X) || !double.IsFinite(workingUv.Y))
            {
                error = "평면 경로에 유한하지 않은 좌표가 있습니다.";
                return false;
            }
            if (probeCount++ >= maxProbes)
            {
                error = $"표면 매핑이 {maxProbes}개 질의 한도를 넘었습니다.";
                return false;
            }

            SurfaceMappedSample? hitSample = hit(workingUv);
            if (!hitSample.HasValue)
            {
                error = "경로가 선택한 표면 영역을 벗어납니다.";
                return false;
            }

            sample = hitSample.Value;
            if (!double.IsFinite(sample.Point.X) ||
                !double.IsFinite(sample.Point.Y) ||
                !double.IsFinite(sample.Point.Z))
            {
                error = "표면 hit에 유한하지 않은 모델 좌표가 있습니다.";
                return false;
            }

            return true;
        }

        void Append(SurfaceMappedSample sample)
        {
            if (result.Count == 0 || result[^1].DatumPoint != sample.DatumPoint)
                result.Add(sample);
        }

        bool Refine(
            Vec2 startUv,
            SurfaceMappedSample mappedStart,
            Vec2 endUv,
            SurfaceMappedSample mappedEnd,
            int depth)
        {
            Vec2 midpointUv = startUv * 0.5 + endUv * 0.5;
            Vec2 quarterUv = startUv * 0.75 + endUv * 0.25;
            Vec2 threeQuarterUv = startUv * 0.25 + endUv * 0.75;
            if (!TryProbe(midpointUv, out SurfaceMappedSample mappedMidpoint) ||
                !TryProbe(quarterUv, out SurfaceMappedSample mappedQuarter) ||
                !TryProbe(threeQuarterUv, out SurfaceMappedSample mappedThreeQuarter))
            {
                return false;
            }

            Vec3 chordMidpoint = mappedStart.Point * 0.5 + mappedEnd.Point * 0.5;
            Vec3 chordQuarter = mappedStart.Point * 0.75 + mappedEnd.Point * 0.25;
            Vec3 chordThreeQuarter = mappedStart.Point * 0.25 + mappedEnd.Point * 0.75;
            bool withinTolerance = startUv.DistanceTo(endUv) <= maximumPlanarSpacingMm &&
                (mappedMidpoint.Point - chordMidpoint).Length <= maximumChordErrorMm &&
                (mappedQuarter.Point - chordQuarter).Length <= maximumChordErrorMm &&
                (mappedThreeQuarter.Point - chordThreeQuarter).Length <= maximumChordErrorMm;
            if (withinTolerance)
            {
                Append(mappedQuarter);
                Append(mappedMidpoint);
                Append(mappedThreeQuarter);
                Append(mappedEnd);
                return true;
            }

            if (depth >= maximumDepth || midpointUv == startUv || midpointUv == endUv)
            {
                error = $"표면 매핑이 깊이 {maximumDepth} 안에서 연속성 또는 공차를 만족하지 못했습니다.";
                return false;
            }

            return Refine(startUv, mappedStart, midpointUv, mappedMidpoint, depth + 1) &&
                Refine(midpointUv, mappedMidpoint, endUv, mappedEnd, depth + 1);
        }

        if (!TryProbe(path[0], out SurfaceMappedSample first))
        {
            mapped = [];
            failure = error;
            return false;
        }

        Append(first);
        for (int index = 1; index < path.Count; index++)
        {
            if (path[index] == path[index - 1])
                continue;
            if (!TryProbe(path[index], out SurfaceMappedSample next) ||
                !Refine(path[index - 1], result[^1], path[index], next, depth: 0))
            {
                mapped = [];
                failure = error;
                return false;
            }
        }

        mapped = result;
        failure = string.Empty;
        return true;
    }
}
