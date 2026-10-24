using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;
using HeatingMold.Core.Validation;
using HeatingMold.Design.Regions;
using HeatingMold.Design.Validation;

namespace HeatingMold.Design.HeatingPath;

/// <summary>명시 방향·끝 앵커·간격을 고정한 연속 사행 경로 후보를 만듭니다.</summary>
public sealed class ConstrainedHeatingPathPlanner
{
    private readonly LaneCandidateEnumerator _candidateEnumerator;
    private readonly TurnBuilder _turnBuilder;

    /// <summary>레인 배치 열거기와 제한 회전 생성기를 연결합니다.</summary>
    public ConstrainedHeatingPathPlanner(
        LaneCandidateEnumerator? candidateEnumerator = null,
        TurnBuilder? turnBuilder = null)
    {
        _candidateEnumerator = candidateEnumerator ?? new LaneCandidateEnumerator();
        _turnBuilder = turnBuilder ?? new TurnBuilder();
    }

    /// <summary>2D 영역에서 유효 후보를 검사하고 우선순위가 가장 높은 단일 초안을 반환합니다.</summary>
    public OperationResult<HeatingPathDraft> Generate(HeatingPathGenerationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        OperationResult<LaneCandidateEnumeration> enumerationResult = _candidateEnumerator.Enumerate(request);
        if (!enumerationResult.IsSuccess)
            return OperationResult<HeatingPathDraft>.Failure(enumerationResult.Issues);

        LaneCandidateEnumeration enumeration = enumerationResult.GetValueOrThrow();
        double pitch = request.PlacementConstraints.CenterPitch(request.WireSpecification.DiameterMm).Value;
        double margin = request.PlacementConstraints.EffectiveCenterlineBoundaryMargin(request.WireSpecification.DiameterMm).Value;
        var validCandidates = new List<BuiltCandidate>();
        var rejectedEvidence = new List<string>();

        foreach (LaneLayoutCandidate layout in enumeration.Candidates)
        {
            OperationResult<IReadOnlyList<DraftPiece>> built = BuildPieces(request, layout, pitch);
            if (!built.IsSuccess)
            {
                AddIssueEvidence(built.Issues);
                continue;
            }

            IReadOnlyList<DraftPiece> pieces = built.GetValueOrThrow();
            if (!ValidateCandidate(request.Region, pieces, margin, request.Settings.AnchorCoincidenceToleranceMm, out string failure))
            {
                AddTextEvidence(failure);
                continue;
            }

            DraftPiece firstRun = pieces.First(piece => piece.Kind == HeatingPathSegmentKind.HeatingRun);
            DraftPiece lastRun = pieces.Last(piece => piece.Kind == HeatingPathSegmentKind.HeatingRun);
            double startLeadLength = request.PlacementConstraints.StartAnchor.WorkingUv.DistanceTo(firstRun.Points[0]);
            double endLeadLength = lastRun.Points[^1].DistanceTo(request.PlacementConstraints.EndAnchor.WorkingUv);
            var score = new HeatingPathCandidateScore(
                layout.MaximumUncoveredGapMm,
                startLeadLength,
                endLeadLength,
                layout.CandidateOrdinal);
            validCandidates.Add(new BuiltCandidate(layout, pieces, score));
        }

        if (validCandidates.Count == 0)
        {
            var issue = ValidationIssueFactory.Error(
                "NoValidHeatingPathCandidate",
                "명시 끝점과 간격을 만족하는 연속 사행 후보가 없습니다. 장애물 우회나 자동 피치 변경은 수행하지 않았습니다.",
                "heating-path-generation-v1",
                [request.ProjectId.Value, request.Region.Id.Value]);
            ValidationIssue[] diagnostics = rejectedEvidence
                .Distinct(StringComparer.Ordinal)
                .Take(8)
                .Select(evidence => ValidationIssueFactory.Error(
                    "RejectedHeatingPathCandidate",
                    evidence,
                    "heating-path-generation-v1",
                    [request.ProjectId.Value, request.Region.Id.Value]))
                .Prepend(issue)
                .ToArray();
            return OperationResult<HeatingPathDraft>.Failure(diagnostics);
        }

        BuiltCandidate best = validCandidates
            .OrderByDescending(candidate => candidate.Layout.LaneCount)
            .ThenBy(candidate => candidate.Score.MaximumUncoveredGapMm)
            .ThenBy(candidate => candidate.Score.StartLeadLengthMm + candidate.Score.EndLeadLengthMm)
            .ThenBy(candidate => candidate.Layout.CandidateOrdinal)
            .First();
        OperationResult<HeatingPathDraft> draftResult = CreateDraft(request, best);
        if (!draftResult.IsSuccess)
            return draftResult;
        HeatingPathDraft draft = draftResult.GetValueOrThrow();

        var warnings = enumerationResult.Issues.ToList();
        if (validCandidates.Count < enumeration.Candidates.Count)
        {
            warnings.Add(new ValidationIssue(
                "SomeLaneCandidatesRejected",
                ValidationSeverity.Info,
                "Design.SomeLaneCandidatesRejected",
                [request.ProjectId.Value, request.Region.Id.Value],
                "heating-path-generation-v1",
                evidence: rejectedEvidence.Distinct(StringComparer.Ordinal).Take(8).ToArray()));
        }

        return OperationResult<HeatingPathDraft>.Success(draft, warnings);

        void AddIssueEvidence(IEnumerable<ValidationIssue> candidateIssues)
        {
            foreach (ValidationIssue candidateIssue in candidateIssues)
                AddTextEvidence(candidateIssue.Evidence.FirstOrDefault() ?? candidateIssue.Code);
        }

        void AddTextEvidence(string evidence)
        {
            if (rejectedEvidence.Count < 32)
                rejectedEvidence.Add(evidence);
        }
    }

    private OperationResult<IReadOnlyList<DraftPiece>> BuildPieces(
        HeatingPathGenerationRequest request,
        LaneLayoutCandidate layout,
        double centerPitchMm)
    {
        double tolerance = request.Settings.AnchorCoincidenceToleranceMm;
        double turnExtension = layout.LaneCount > 1
            ? TurnBuilder.RequiredTangentExtension(centerPitchMm)
            : 0;
        LaneRun[] runs = layout.Runs.Select(run => TrimForTurn(run, turnExtension)).ToArray();
        if (runs.Any(run => run.Start.DistanceTo(run.End) <= tolerance))
        {
            return Failure(
                "LaneTooShortForTurn",
                "U턴의 접선 구간을 확보하면 레인 발열 길이가 남지 않습니다.",
                request);
        }

        var pieces = new List<DraftPiece>();
        Vec2 startAnchor = request.PlacementConstraints.StartAnchor.WorkingUv;
        AddLead(startAnchor, runs[0].Start);
        for (int index = 0; index < runs.Length; index++)
        {
            LaneRun run = runs[index];
            pieces.Add(new DraftPiece(
                HeatingPathSegmentKind.HeatingRun,
                run.LaneIndex,
                [run.Start, run.End],
                0));

            if (index < runs.Length - 1)
            {
                OperationResult<TurnBuildResult> turn = _turnBuilder.Build(
                    run.End,
                    runs[index + 1].Start,
                    run.IsForward,
                    centerPitchMm,
                    request.Settings.MaximumTurnDeviationMm,
                    request.Settings.MaximumTurnSampleSpacingMm,
                    "heating-path-generation-v1");
                if (!turn.IsSuccess)
                    return OperationResult<IReadOnlyList<DraftPiece>>.Failure(turn.Issues);

                pieces.Add(new DraftPiece(
                    HeatingPathSegmentKind.Turn,
                    run.LaneIndex,
                    turn.GetValueOrThrow().Points,
                    turn.GetValueOrThrow().MaximumDeviationMm));
            }
        }

        AddLead(runs[^1].End, request.PlacementConstraints.EndAnchor.WorkingUv);
        if (pieces.Count == 0)
            return Failure("EmptyHeatingPathCandidate", "경로 후보에 유효한 발열 구간이 없습니다.", request);

        return OperationResult<IReadOnlyList<DraftPiece>>.Success(pieces);

        void AddLead(Vec2 start, Vec2 end)
        {
            if (start == end)
                return;

            double length = start.DistanceTo(end);
            int sampleCount = Math.Max(1, (int)Math.Ceiling(length / request.Settings.MaximumTurnSampleSpacingMm));
            Vec2[] samples = Enumerable.Range(0, sampleCount + 1)
                .Select(index => start + (end - start) * ((double)index / sampleCount))
                .ToArray();
            pieces.Add(new DraftPiece(HeatingPathSegmentKind.FixedJoin, null, samples, 0));
        }
    }

    private static LaneRun TrimForTurn(LaneRun run, double extension)
    {
        if (extension == 0)
            return run;

        Vec2 direction = (run.End - run.Start) * (1 / run.Start.DistanceTo(run.End));
        return new LaneRun(
            run.LaneIndex,
            run.Start + direction * extension,
            run.End - direction * extension,
            run.IsForward);
    }

    private static bool ValidateCandidate(
        HeatingRegion region,
        IReadOnlyList<DraftPiece> pieces,
        double requiredMarginMm,
        double toleranceMm,
        out string failure)
    {
        foreach (DraftPiece piece in pieces)
        {
            IReadOnlyList<Vec2> samples = AddMidpoints(piece.Points);
            for (int index = 0; index < samples.Count; index++)
            {
                Vec2 point = samples[index];
                if (!IsInsideEffectiveRegion(region, point))
                {
                    failure = piece.Kind == HeatingPathSegmentKind.FixedJoin
                        ? "시작·끝 앵커 연결선이 선택된 유효 영역을 벗어납니다."
                        : "레인 또는 U턴 후보가 홀·제외·외곽 경계를 벗어납니다.";
                    return false;
                }

                if (piece.Kind != HeatingPathSegmentKind.FixedJoin)
                {
                    double distance = DistanceToBoundary(region, point);
                    if (distance - piece.MaximumDeviationMm < requiredMarginMm - toleranceMm)
                    {
                        failure = "레인 또는 U턴 후보가 경계 여유를 오차 한도까지 고려해 만족하지 못합니다.";
                        return false;
                    }
                }

                if (index < samples.Count - 1 && SegmentProperlyCrossesBoundary(region, point, samples[index + 1]))
                {
                    failure = "후보 곡선 표본 사이 선분이 외곽·홀·제외 경계를 가로지릅니다.";
                    return false;
                }
            }
        }

        failure = string.Empty;
        return true;
    }

    private static OperationResult<HeatingPathDraft> CreateDraft(
        HeatingPathGenerationRequest request,
        BuiltCandidate candidate)
    {
        Vec2 firstPoint = candidate.Pieces[0].Points[0];
        var controls = new List<HeatingPathDraftControlPoint>
        {
            new(ControlPointId.New(), firstPoint)
        };
        var segments = new List<HeatingPathDraftSegment>();
        for (int index = 0; index < candidate.Pieces.Count; index++)
        {
            DraftPiece piece = candidate.Pieces[index];
            Vec2[] connectedPoints = piece.Points.ToArray();
            Vec2 expectedStart = controls[^1].WorkingUv;
            double startMismatch = expectedStart.DistanceTo(connectedPoints[0]);
            if (startMismatch > request.Settings.AnchorCoincidenceToleranceMm)
            {
                return Failure<HeatingPathDraft>(
                    "DisconnectedHeatingPathDraft",
                    "인접 경로 조각의 끝점 차이가 공유 절점 공차를 넘어 초안을 연결할 수 없습니다.",
                    request);
            }

            connectedPoints[0] = expectedStart;
            var endControlPoint = new HeatingPathDraftControlPoint(
                ControlPointId.New(),
                connectedPoints[^1]);
            segments.Add(new HeatingPathDraftSegment(
                HeatingPathSegmentId.New(),
                piece.Kind,
                controls[^1].Id,
                endControlPoint.Id,
                index,
                connectedPoints,
                piece.LaneIndex));
            controls.Add(endControlPoint);
        }

        try
        {
            return OperationResult<HeatingPathDraft>.Success(new HeatingPathDraft(
                request.ProjectId,
                request.BaseRevision,
                HeatingPathId.New(),
                request.Region.Id,
                candidate.Layout.LaneCount,
                candidate.Score,
                controls,
                segments));
        }
        catch (ArgumentException exception)
        {
            return Failure<HeatingPathDraft>(
                "InvalidHeatingPathDraft",
                $"경로 초안의 공유 절점 또는 구간 순서가 유효하지 않습니다: {exception.Message}",
                request);
        }
    }

    /// <summary>경로 초안 생성 진단을 해당 프로젝트와 영역에 고정해 반환합니다.</summary>
    private static OperationResult<T> Failure<T>(
        string code,
        string evidence,
        HeatingPathGenerationRequest request) => OperationResult<T>.Failure(
        [ValidationIssueFactory.Error(
            code,
            evidence,
            "heating-path-generation-v1",
            [request.ProjectId.Value, request.Region.Id.Value])]);

    private static IReadOnlyList<Vec2> AddMidpoints(IReadOnlyList<Vec2> points)
    {
        var expanded = new List<Vec2>(points.Count * 2);
        for (int index = 0; index < points.Count - 1; index++)
        {
            expanded.Add(points[index]);
            expanded.Add(points[index] * 0.5 + points[index + 1] * 0.5);
        }

        expanded.Add(points[^1]);
        return expanded;
    }

    private static bool IsInsideEffectiveRegion(HeatingRegion region, Vec2 point)
    {
        const double boundaryToleranceMm = 1e-8;
        foreach (BoundarySegment segment in region.EffectiveBoundaryLoops.SelectMany(loop => loop.Segments))
        {
            if (DistanceToSegment(point, segment.Start, segment.End) <= boundaryToleranceMm)
                return true;
        }

        return region.EffectiveBoundaryLoops.Count(loop => PointInRing(loop.Vertices, point)) % 2 == 1;
    }

    private static double DistanceToBoundary(HeatingRegion region, Vec2 point) =>
        region.EffectiveBoundaryLoops
            .SelectMany(loop => loop.Segments)
            .Min(segment => DistanceToSegment(point, segment.Start, segment.End));

    private static double DistanceToSegment(Vec2 point, Vec2 start, Vec2 end)
    {
        Vec2 direction = end - start;
        double lengthSquared = direction.X * direction.X + direction.Y * direction.Y;
        double parameter = Math.Clamp(
            ((point.X - start.X) * direction.X + (point.Y - start.Y) * direction.Y) / lengthSquared,
            0,
            1);
        return point.DistanceTo(start + direction * parameter);
    }

    private static bool SegmentProperlyCrossesBoundary(HeatingRegion region, Vec2 start, Vec2 end)
    {
        const double tolerance = 1e-10;
        foreach (BoundarySegment edge in region.EffectiveBoundaryLoops.SelectMany(loop => loop.Segments))
        {
            double first = Cross(end - start, edge.Start - start);
            double second = Cross(end - start, edge.End - start);
            double third = Cross(edge.End - edge.Start, start - edge.Start);
            double fourth = Cross(edge.End - edge.Start, end - edge.Start);
            bool crossesPath = first > tolerance && second < -tolerance || first < -tolerance && second > tolerance;
            bool crossesEdge = third > tolerance && fourth < -tolerance || third < -tolerance && fourth > tolerance;
            if (crossesPath && crossesEdge)
                return true;
        }

        return false;
    }

    private static bool PointInRing(IReadOnlyList<Vec2> ring, Vec2 point)
    {
        bool inside = false;
        for (int current = 0, previous = ring.Count - 1; current < ring.Count; previous = current++)
        {
            Vec2 first = ring[current];
            Vec2 second = ring[previous];
            bool crosses = (first.Y > point.Y) != (second.Y > point.Y) &&
                point.X < (second.X - first.X) * (point.Y - first.Y) / (second.Y - first.Y) + first.X;
            if (crosses)
                inside = !inside;
        }

        return inside;
    }

    private static double Cross(Vec2 first, Vec2 second) => first.X * second.Y - first.Y * second.X;

    private static OperationResult<IReadOnlyList<DraftPiece>> Failure(
        string code,
        string evidence,
        HeatingPathGenerationRequest request) => OperationResult<IReadOnlyList<DraftPiece>>.Failure(
        [ValidationIssueFactory.Error(code, evidence, "heating-path-generation-v1", [request.ProjectId.Value, request.Region.Id.Value])]);

    private sealed record DraftPiece(
        HeatingPathSegmentKind Kind,
        int? LaneIndex,
        IReadOnlyList<Vec2> Points,
        double MaximumDeviationMm);

    private sealed record BuiltCandidate(
        LaneLayoutCandidate Layout,
        IReadOnlyList<DraftPiece> Pieces,
        HeatingPathCandidateScore Score);
}
