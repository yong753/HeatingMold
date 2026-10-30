using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;

namespace HeatingMold.Core.Tests;

public class RouteVertexEditTests
{
    private static SerpentineRoutePiece Piece(SerpentineRoutePieceKind kind, int lane, params Vec2[] points) =>
        new(kind, lane, points);

    private static SerpentineResult TwoLaneRoute()
    {
        var pieces = new List<SerpentineRoutePiece>
        {
            Piece(SerpentineRoutePieceKind.HeatingRun, 0, new Vec2(0, 0), new Vec2(10, 0)),
            Piece(SerpentineRoutePieceKind.TurnConnector, 0, new Vec2(10, 0), new Vec2(10, 2)),
            Piece(SerpentineRoutePieceKind.HeatingRun, 1, new Vec2(10, 2), new Vec2(0, 2)),
        };
        return new SerpentineResult
        {
            Path = new List<Vec2> { new(0, 0), new(10, 0), new(10, 2), new(0, 2) },
            Segments = new List<SerpentineSegment>
            {
                new(0, new Vec2(0, 0), new Vec2(10, 0)),
                new(1, new Vec2(10, 2), new Vec2(0, 2)),
            },
            LaneCount = 2,
            AngleDeg = 0,
            TotalLength = 24,
            RoutePieces = pieces,
        };
    }

    [Fact]
    public void Groups_MergeOnlyAdjacentSharedEndpoint()
    {
        var pieces = new List<SerpentineRoutePiece>
        {
            Piece(SerpentineRoutePieceKind.HeatingRun, 0, new Vec2(0, 0), new Vec2(1, 0)),
            Piece(SerpentineRoutePieceKind.TurnConnector, 0, new Vec2(1, 0), new Vec2(2, 0)),
            Piece(SerpentineRoutePieceKind.HeatingRun, 1, new Vec2(2, 0), new Vec2(0, 0)),
        };

        var groups = RouteVertexGroups.Build(pieces);

        // (0,0)은 경로 시작과 마지막 조각 끝에서 값이 같지만 서로 다른 꼭지점이다.
        Assert.Equal(4, groups.Count);
        Assert.Single(groups[0].Occurrences);
        Assert.Equal(2, groups[1].Occurrences.Count);   // (0,1) + (1,0)
        Assert.Equal(2, groups[2].Occurrences.Count);   // (1,1) + (2,0)
        Assert.Single(groups[3].Occurrences);           // (2,1)
        Assert.Equal(groups[0].Datum, groups[3].Datum);
    }

    [Fact]
    public void Groups_KeepInteriorDuplicatesSeparate()
    {
        var pieces = new List<SerpentineRoutePiece>
        {
            Piece(SerpentineRoutePieceKind.HeatingRun, 0, new Vec2(0, 0), new Vec2(0, 0), new Vec2(5, 0)),
        };

        var groups = RouteVertexGroups.Build(pieces);

        Assert.Equal(3, groups.Count);
        Assert.All(groups, group => Assert.Single(group.Occurrences));
    }

    [Fact]
    public void ApplyVertexEdits_RebuildsPathSegmentsAndLength_WithoutMutatingSource()
    {
        var source = TwoLaneRoute();
        var edits = new List<(RouteVertexOccurrence At, Vec2 Value)>
        {
            (new RouteVertexOccurrence(0, 1), new Vec2(11, 0)),
            (new RouteVertexOccurrence(1, 0), new Vec2(11, 0)),
        };

        var edited = SerpentineVertexEdit.ApplyVertexEdits(source, edits);

        // 원본 불변
        Assert.Equal(new Vec2(10, 0), source.RoutePieces[0].Points[1]);
        // 조각 개수/종류 유지
        Assert.Equal(source.RoutePieces.Count, edited.RoutePieces.Count);
        for (int i = 0; i < source.RoutePieces.Count; i++)
            Assert.Equal(source.RoutePieces[i].Kind, edited.RoutePieces[i].Kind);
        // 공유점은 함께 이동
        Assert.Equal(new Vec2(11, 0), edited.RoutePieces[0].Points[1]);
        Assert.Equal(new Vec2(11, 0), edited.RoutePieces[1].Points[0]);
        // Path / Segments / TotalLength 재계산
        Assert.Equal(new Vec2(11, 0), edited.Path[1]);
        Assert.Equal(new Vec2(10, 2), edited.Path[2]);
        Assert.Equal(new Vec2(0, 0), edited.Segments[0].Start);
        Assert.Equal(new Vec2(11, 0), edited.Segments[0].End);
        Assert.Equal(new Vec2(10, 2), edited.Segments[1].Start);
        double expected = new Vec2(0, 0).DistanceTo(new Vec2(11, 0))
                          + new Vec2(11, 0).DistanceTo(new Vec2(10, 2))
                          + new Vec2(10, 2).DistanceTo(new Vec2(0, 2));
        Assert.Equal(expected, edited.TotalLength, 9);
    }

    [Fact]
    public void ApplyVertexEdits_RejectsSplitSharedVertex()
    {
        var source = TwoLaneRoute();
        var edits = new List<(RouteVertexOccurrence At, Vec2 Value)>
        {
            (new RouteVertexOccurrence(0, 1), new Vec2(11, 0)),
        };

        Assert.Throws<ArgumentException>(() => SerpentineVertexEdit.ApplyVertexEdits(source, edits));
    }

    [Fact]
    public void PreviewApply_CopiesOnlyAffectedPieces_AndMatchesFullEdit()
    {
        var source = TwoLaneRoute();
        var edits = new List<(RouteVertexOccurrence At, Vec2 Value)>
        {
            (new RouteVertexOccurrence(0, 1), new Vec2(11, 0)),
            (new RouteVertexOccurrence(1, 0), new Vec2(11, 0)),
        };

        Assert.True(SerpentineVertexPreview.TryApplyAffected(source, edits,
            out var affected, out var changed, out string failure), failure);

        Assert.Equal(2, affected.Count);                    // 조각 0, 1만
        Assert.False(affected.ContainsKey(2));              // 마지막 조각은 복사하지 않음
        Assert.Equal(new Vec2(11, 0), affected[0][1]);
        Assert.Equal(new Vec2(11, 0), affected[1][0]);
        // 마지막 점(0,1)은 앞 선분만, 첫 점(1,0)은 뒤 선분만 → 2개
        Assert.Equal(2, changed.Count);

        var full = SerpentineVertexEdit.ApplyVertexEdits(source, edits);
        foreach (var entry in affected)
            Assert.Equal(full.RoutePieces[entry.Key].Points, entry.Value);
        Assert.Equal(new Vec2(10, 0), source.RoutePieces[0].Points[1]);   // 원본 불변
    }

    [Fact]
    public void PreviewApply_RejectsSplitSharedVertex()
    {
        var source = TwoLaneRoute();
        var edits = new List<(RouteVertexOccurrence At, Vec2 Value)>
        {
            (new RouteVertexOccurrence(0, 1), new Vec2(11, 0)),
        };

        Assert.False(SerpentineVertexPreview.TryApplyAffected(source, edits,
            out _, out _, out string failure));
        Assert.Contains("공유", failure);
    }

    [Fact]
    public void Validator_AcceptsInsideAndRejectsOutside()
    {
        var region = new List<IReadOnlyList<Vec2>>
        {
            new List<Vec2> { new(0, 0), new(10, 0), new(10, 10), new(0, 10) },
        };

        Assert.True(RouteVertexValidator.SegmentsStayInRegion(region,
            new List<(Vec2 A, Vec2 B)> { (new Vec2(1, 1), new Vec2(9, 9)) }, out _));

        Assert.False(RouteVertexValidator.SegmentsStayInRegion(region,
            new List<(Vec2 A, Vec2 B)> { (new Vec2(1, 1), new Vec2(11, 5)) }, out string failure));
        Assert.Contains("벗어납니다", failure);
    }

    [Fact]
    public void Validator_RespectsHole()
    {
        var region = new List<IReadOnlyList<Vec2>>
        {
            new List<Vec2> { new(0, 0), new(10, 0), new(10, 10), new(0, 10) },
            new List<Vec2> { new(4, 4), new(6, 4), new(6, 6), new(4, 6) },
        };

        Assert.False(RouteVertexValidator.SegmentsStayInRegion(region,
            new List<(Vec2 A, Vec2 B)> { (new Vec2(1, 5), new Vec2(9, 5)) }, out _));
        Assert.True(RouteVertexValidator.SegmentsStayInRegion(region,
            new List<(Vec2 A, Vec2 B)> { (new Vec2(1, 5), new Vec2(3, 5)) }, out _));
    }

    [Fact]
    public void SnapOnChain_ClampsToNearestSegmentPoint()
    {
        var chain = new List<Vec2> { new(0, 0), new(10, 0) };

        Vec2 snapped = Serpentine.SnapOnChain(chain, new Vec2(5, 3));

        Assert.Equal(5, snapped.X, 9);
        Assert.Equal(0, snapped.Y, 9);
        Assert.Equal(3, Serpentine.DistanceToChain(chain, new Vec2(5, 3)), 9);
    }

    [Fact]
    public void TryMapRouteStrict_DetectsHoleThatSparseRouteMapMisses()
    {
        Func<Vec2, SurfaceMappedSample?> hit = uv =>
        {
            // 2D 스캔라인의 좁은 구멍: 5.45 ~ 5.85 구간은 표면이 없다.
            if (uv.X >= 5.45 && uv.X <= 5.85) return null;
            return new SurfaceMappedSample(uv, new Vec3(uv.X, 0, 0), 0);
        };
        var path = new List<Vec2> { new(0, 0), new(10, 0) };

        Assert.True(SurfacePathMapper.TryMapRoute(path, hit, out _, out _));
        Assert.False(SurfacePathMapper.TryMapRouteStrict(path, hit, out _, out string failure, 0.1));
        Assert.Contains("leaves the selected surface domain", failure);
    }
}
