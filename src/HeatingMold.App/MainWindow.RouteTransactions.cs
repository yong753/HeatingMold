using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

public partial class MainWindow
{
    /// <summary>실제 3D 경로 조각 끝점 사이에 허용하는 최대 간격(mm).
    /// 이 간격은 연결로 승인되지만 자동 보간·길이 가산은 하지 않는다.</summary>
    private const double RouteJoinTolerance = GeometryTolerancePolicy.DistanceTolerance;

    private Dictionary<ValidatedRouteSampleKey, SurfaceMappedSample[]> _validatedRouteSamples = new();

    /// <summary>마지막 경로 트랜잭션의 표시 갱신 경고. 커밋 이후 실패는 형상을 되돌리지 않으므로
    /// 호출자가 완료 문구에 경고를 보존할 수 있게 남긴다. 다음 트랜잭션 시작 시 지운다.</summary>
    private string _routePresentationWarning;

    /// <summary>초안과 모든 가변 경로 목록을 분리한다. 확정 영역은 성공 시 참조 교체만 한다.</summary>
    private static WorkingArea ForkAreaRoute(WorkingArea source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        var candidate = new WorkingArea
        {
            Name = source.Name,
            Color = source.Color,
            AreaId = source.AreaId,
            Frame = source.Frame,
            Direction = source.Direction,
            MarginText = source.MarginText,
            SpacingText = source.SpacingText,
            Exclusions = source.Exclusions.ToList(),
            ExitFaceId = source.ExitFaceId,
            ExitFaceRevision = source.ExitFaceRevision,
            ExitChain = source.ExitChain.ToList(),
            ExitChain3D = source.ExitChain3D.ToList(),
            ExitChainFaceIds = source.ExitChainFaceIds.ToList(),
            StartAnchor = source.StartAnchor,
            EndAnchor = source.EndAnchor,
            ExitEdgeRefs = source.ExitEdgeRefs.Select(CadExitBoundary2D.CopyReference).ToList(),
            ExitBoundary = source.ExitBoundary,
            RegionExitBoundary = source.RegionExitBoundary,
            RegionCadBoundary = source.RegionCadBoundary,
            SelectedStartExit = source.SelectedStartExit,
            SelectedEndExit = source.SelectedEndExit,
            RegionBoundary = source.RegionBoundary,
            SelectedAnchorCandidate = source.SelectedAnchorCandidate,
            Circuits = source.Circuits.Select(route => route with
            {
                Pieces = route.Pieces.Select(CopyRoutePiece).ToList()
            }).ToList(),
            CircuitPieceIndices = source.CircuitPieceIndices.Select(indices => indices.ToList()).ToList(),
            AnchorCandidates = source.AnchorCandidates.ToList(),
            ExcludedCandidates = source.ExcludedCandidates.Select(CopyExcludedCandidate).ToList(),
            ExcludedPlan = CopyExcludedCandidate(source.ExcludedPlan),
            ExcludedCandidateFailure = source.ExcludedCandidateFailure,
            ExcludedPlanKey = source.ExcludedPlanKey,
            LastExitFailure = source.LastExitFailure,
            Pattern = CopyRoutePattern(source.Pattern),
            ProjectedPieces = source.ProjectedPieces.Select(piece => piece == null ? null : new RoutePieceResult
            {
                Length = piece.Length,
                Start = piece.Start?.ToArray(),
                End = piece.End?.ToArray()
            }).ToList(),
            NativePieceIds = source.NativePieceIds.ToArray(),
            RoutePieceLengths = source.RoutePieceLengths.ToArray(),
            Paths = source.Paths.Select(path => path.ToList()).ToList(),
            ExclusionPieces = source.ExclusionPieces?.Select(component =>
                component.Select(CopyRoutePiece).ToList()).ToList(),
            PatternDisplayed = source.PatternDisplayed,
            RouteEdited = source.RouteEdited,
            AppliedInputs = source.AppliedInputs,
            CommittedMapping = CopyCommittedRouteContext(source.CommittedMapping),
            SplitCutLines = source.SplitCutLines.ToList(),
            TargetPowerW = source.TargetPowerW
        };
        candidate.FaceIdSet.UnionWith(source.FaceIdSet);
        candidate.Region.AddRange(source.Region.Select(ring => ring.ToList()));
        return candidate;
    }

    private static SerpentineRoutePiece CopyRoutePiece(SerpentineRoutePiece piece) =>
        piece with { Points = piece.Points.ToList() };

    private static SerpentineResult CopyRoutePattern(SerpentineResult pattern) => pattern == null ? null : new()
    {
        Path = pattern.Path.ToList(),
        Segments = pattern.Segments.ToList(),
        LaneCount = pattern.LaneCount,
        AngleDeg = pattern.AngleDeg,
        TotalLength = pattern.TotalLength,
        NominalLaneCount = pattern.NominalLaneCount,
        RoutePieces = pattern.RoutePieces.Select(CopyRoutePiece).ToList(),
        StartExit = pattern.StartExit,
        EndExit = pattern.EndExit,
        CandidateId = pattern.CandidateId
    };

    private static ExcludedAnchorCandidate CopyExcludedCandidate(ExcludedAnchorCandidate candidate) =>
        candidate == null ? null : candidate with
        {
            Nodes = candidate.Nodes.ToList(),
            Order = candidate.Order.ToList(),
            EnterLow = candidate.EnterLow.ToList()
        };

    private static CommittedRouteContext CopyCommittedRouteContext(CommittedRouteContext context) =>
        context == null ? null : new()
        {
            FaceIds = context.FaceIds.ToArray(),
            ExitFaceId = context.ExitFaceId,
            ExitChain = context.ExitChain.ToList(),
            ExitChain3D = context.ExitChain3D.ToList(),
            ExitChainFaceIds = context.ExitChainFaceIds.ToList(),
            StartAnchor = context.StartAnchor,
            EndAnchor = context.EndAnchor,
            ExitEdgeRefs = context.ExitEdgeRefs.Select(CadExitBoundary2D.CopyReference).ToList(),
            Boundary = context.Boundary,
            RegionBoundary = context.RegionBoundary,
            SelectedStartExit = context.SelectedStartExit,
            SelectedEndExit = context.SelectedEndExit,
            SelectedAnchorCandidate = context.SelectedAnchorCandidate,
            StartExits = context.StartExits.ToList(),
            EndExits = context.EndExits.ToList(),
            CircuitStarts = context.CircuitStarts.ToList(),
            CircuitEnds = context.CircuitEnds.ToList()
        };

    private static bool HasUncommittedRoute(WorkingArea area) =>
        area.Pattern?.RoutePieces.Count > 0 || area.ExclusionPieces?.Count > 0 || area.Paths.Count > 0;

    /// <summary>분리된 후보만 검증/스테이징한 뒤 target에 준비한 참조를 한 번에 게시한다.</summary>
    private bool RebuildNativePatternGeometry(WorkingArea target, WorkingArea candidate,
        out double activeLength, out int activeCount)
    {
        activeLength = 0;
        activeCount = 0;
        try
        {
            if (target == null || candidate == null || ReferenceEquals(target, candidate))
                throw new InvalidOperationException("원본 영역과 분리된 경로 후보가 필요합니다");
            // 호출자가 후보에 원본 목록을 다시 대입했더라도 원본을 변경하지 않는다.
            var candidates = new Dictionary<WorkingArea, WorkingArea> { [target] = ForkAreaRoute(candidate) };
            return TryCommitAreaRouteCandidates(_areas, candidates, out activeLength, out activeCount);
        }
        catch (Exception ex)
        {
            return ReportRoutePreparationFailure(ex);
        }
    }

    private bool TryCommitAreaRouteCandidates(IReadOnlyList<WorkingArea> areas,
        IReadOnlyDictionary<WorkingArea, WorkingArea> candidates, out double activeLength, out int activeCount)
    {
        activeLength = 0;
        activeCount = 0;
        _routePresentationWarning = null;
        WorkingSideDefinition side = _workingSide?.Confirmed;
        if (side == null) return false;

        var stagedRoutes = new List<int>();
        var finalIds = new List<int>();
        var stagedLines = Array.Empty<int>();
        int nextStageId = _nextStageId;
        int nextNativeId = _nextNativePieceId;
        KeyValuePair<WorkingArea, WorkingArea>[] publications;
        Dictionary<ValidatedRouteSampleKey, SurfaceMappedSample[]> preparedSamples;
        double[] endpoints;
        int[] colorIds;
        double[] colorRgbValues;
        double preparedActiveLength = 0;
        int preparedActiveCount = 0;

        try
        {
            publications = candidates.ToArray();
            if (areas.Count != areas.Distinct().Count())
                throw new InvalidOperationException("작업영역 목록에 중복 영역이 있습니다");
            foreach (var entry in publications)
                ValidateAreaRouteCandidate(areas, entry.Key, entry.Value);

            // 반드시 현재 정본에서 캡처한다. 후보의 ID 배열을 지우거나 바꾸어도 삭제 집합은 바뀌지 않는다.
            int[] oldIds = _areas.SelectMany(area => area.NativePieceIds).ToArray();
            var oldIdSet = new HashSet<int>(oldIds);
            if (oldIdSet.Count != oldIds.Length || oldIds.Any(id => id < 0))
                throw new InvalidOperationException("확정 경로 ID가 중복되었거나 유효하지 않습니다");
            var reservedIds = new HashSet<int>(oldIdSet);
            preparedSamples = publications.Length == 0 ? _validatedRouteSamples :
                new Dictionary<ValidatedRouteSampleKey, SurfaceMappedSample[]>(_validatedRouteSamples);

            foreach (var entry in publications)
            {
                WorkingArea candidate = entry.Value;
                if (candidate.Pattern?.RoutePieces.Count > 0)
                {
                    if (!TryRefreshCircuits(candidate, out string circuitFailure))
                        throw new InvalidOperationException("영역 " + candidate.Name + ": " + circuitFailure);
                }
                else
                {
                    candidate.Circuits = new List<CircuitRoute>();
                    candidate.CircuitPieceIndices = new List<List<int>>();
                }

                candidate.CommittedMapping = CommittedRouteContext.Capture(candidate);
                PrepareCandidateRouteGeometry(entry.Key, candidate, side, preparedSamples,
                    reservedIds, ref nextStageId, ref nextNativeId, stagedRoutes, finalIds);
                candidate.PatternDisplayed = candidate.NativePieceIds.Length > 0;
                if (candidate.PatternDisplayed)
                {
                    if (!TryParseAreaParams(candidate.MarginText, candidate.SpacingText,
                            out double margin, out double spacing))
                        throw new InvalidOperationException("영역 " + candidate.Name + "의 마진/간격 입력이 유효하지 않습니다");
                    candidate.AppliedInputs = CaptureAppliedInputs(candidate, margin, spacing);
                }
                else
                {
                    candidate.AppliedInputs = null;
                }
            }

            var retainedIds = new List<int>();
            var endpointValues = new List<double>();
            var colorPieceIds = new List<int>();
            var colorValues = new List<double>();
            foreach (WorkingArea area in areas)
            {
                bool changed = candidates.TryGetValue(area, out WorkingArea candidate);
                WorkingArea committed = changed ? candidate : area;
                // 미확정 영역의 초안은 삭제나 다른 영역의 변경으로 자동 생성되지 않는다.
                if (committed.CommittedMapping == null) continue;
                if (!changed) retainedIds.AddRange(committed.NativePieceIds);
                PrepareCommittedRoutePresentation(committed, endpointValues,
                    out double length, out int count);
                // 색상 대상도 커밋에 참여한 최종 영역에서 미리 고정한다. 삭제 이후에
                // 화면 목록을 다시 읽으면 이미 제거된 조각 ID가 색상 대상으로 넘어간다.
                if (committed.PatternDisplayed && committed.NativePieceIds.Length > 0 &&
                    !AppendRouteColors(committed, colorPieceIds, colorValues))
                    throw new InvalidOperationException(
                        "영역 " + committed.Name + "의 확정 경로 ID가 유효하지 않습니다");
                if (ReferenceEquals(area, _activeArea))
                {
                    preparedActiveLength = length;
                    preparedActiveCount = count;
                }
            }
            endpoints = endpointValues.ToArray();
            colorIds = colorPieceIds.ToArray();
            colorRgbValues = colorValues.ToArray();
            int[] stageArray = stagedRoutes.ToArray();
            int[] finalArray = finalIds.ToArray();
            int[] retainedArray = retainedIds.ToArray();
            var finalSet = new HashSet<int>(finalArray);
            var retainedSet = new HashSet<int>(retainedArray);
            if (finalSet.Count != finalArray.Length || retainedSet.Count != retainedArray.Length ||
                finalSet.Overlaps(retainedSet) || !retainedSet.IsSubsetOf(oldIdSet))
                throw new InvalidOperationException("경로 retain/replace ID 대응이 유효하지 않습니다");
            // 색상 대상은 반드시 이번 커밋이 유지하거나 새로 만든 조각이어야 한다.
            var colorSet = new HashSet<int>(colorIds);
            if (colorSet.Count != colorIds.Length || colorRgbValues.Length != colorIds.Length * 3 ||
                colorIds.Any(id => !retainedSet.Contains(id) && !finalSet.Contains(id)))
                throw new InvalidOperationException("경로 색상 대상 ID 대응이 유효하지 않습니다");
            int[] deletedArray = oldIds.Where(id => !retainedSet.Contains(id) && !finalSet.Contains(id)).ToArray();

            // 빈 최종 결과도 동일한 context 소유 retain/replace/delete 트랜잭션을 거친다.
            if (!Bridge.CommitWorkingRouteGeometryDelta(side.Id.Value, side.Revision.Value,
                    stageArray, finalArray, stagedLines, retainedArray, deletedArray))
                throw new InvalidOperationException("네이티브 경로 차등 커밋 실패");
        }
        catch (Exception ex)
        {
            try
            {
                Bridge.DiscardWorkingRouteGeometry(side.Id.Value, side.Revision.Value,
                    stagedRoutes.ToArray(), stagedLines);
            }
            catch (Exception) { }
            return ReportRoutePreparationFailure(ex);
        }

        // 이 구간에는 계산/할당/표시 호출을 두지 않는다. 모든 게시 값은 커밋 전에 준비되었다.
        for (int i = 0; i < publications.Length; i++)
            PublishAreaRouteCandidate(publications[i].Key, publications[i].Value);
        _nextStageId = nextStageId;
        _nextNativePieceId = nextNativeId;
        _validatedRouteSamples = preparedSamples;
        activeLength = preparedActiveLength;
        activeCount = preparedActiveCount;

        // 색상/마커/Redraw 실패는 이미 적용한 형상과 정본을 되돌리는 이유가 아니다.
        Exception presentationFailure = null;
        bool colorsApplied = false, markersApplied = false, redrawn = false;
        try { colorsApplied = ApplyRouteColors(side, colorIds, colorRgbValues); }
        catch (Exception ex) { presentationFailure = ex; }
        if (!colorsApplied && presentationFailure == null)
            presentationFailure = new InvalidOperationException("발열경로 색상 갱신에 실패했습니다");
        try { markersApplied = Bridge.SetRouteEndpointsNoRedraw(endpoints); }
        catch (Exception ex) { presentationFailure ??= ex; }
        if (!markersApplied && presentationFailure == null)
            presentationFailure = new InvalidOperationException("인출점 마커 갱신에 실패했습니다");
        // 길이 라벨은 커밋된 네이티브 체인에서 다시 만든다. 대상은 이번 커밋에 참여한 최종
        // 영역 집합으로 고정한다. 화면 목록(_areas)은 아직 교체 전이라 삭제된 영역을 담고 있다.
        try
        {
            if (!TryReplaceRouteLengthLabels(side, areas, out string labelFailure))
            {
                presentationFailure ??= new InvalidOperationException(labelFailure);
                try { Bridge.ClearRouteLengthLabels(); } catch (Exception) { }
            }
        }
        catch (Exception ex)
        {
            presentationFailure ??= ex;
            try { Bridge.ClearRouteLengthLabels(); } catch (Exception) { }
        }
        try { redrawn = Bridge.TryRedrawView(); }
        catch (Exception ex) { presentationFailure ??= ex; }
        if (!redrawn && presentationFailure == null)
            presentationFailure = new InvalidOperationException("화면 다시 그리기에 실패했습니다");
        if (presentationFailure != null)
        {
            _routePresentationWarning = presentationFailure.Message;
            try
            {
                if (StatusText != null)
                    StatusText.Text = "경로는 적용되었지만 표시 갱신에 실패했습니다 — 뷰를 다시 그려 주세요";
                ErrorReporter.ShowWarning("경로 표시 갱신 실패", presentationFailure);
            }
            catch (Exception) { }
        }
        return true;
    }

    private static void ValidateAreaRouteCandidate(IReadOnlyList<WorkingArea> areas,
        WorkingArea target, WorkingArea candidate)
    {
        if (target == null || candidate == null || ReferenceEquals(target, candidate) || !areas.Contains(target))
            throw new InvalidOperationException("변경 대상 영역과 분리된 후보가 현재 영역 목록에 대응하지 않습니다");
        if (areas.Contains(candidate) || target.Name != candidate.Name || target.Color != candidate.Color ||
            target.AreaId != candidate.AreaId || !ReferenceEquals(target.Frame, candidate.Frame) ||
            !target.FaceIdSet.SetEquals(candidate.FaceIdSet) || target.Region.Count != candidate.Region.Count)
            throw new InvalidOperationException("경로 트랜잭션으로 확정 영역의 identity/프레임/면 집합을 변경할 수 없습니다");
        for (int i = 0; i < target.Region.Count; i++)
            if (!target.Region[i].SequenceEqual(candidate.Region[i]))
                throw new InvalidOperationException("경로 트랜잭션으로 확정 영역의 원본 경계를 변경할 수 없습니다");
    }

    private void PrepareCandidateRouteGeometry(WorkingArea target, WorkingArea candidate,
        WorkingSideDefinition side, Dictionary<ValidatedRouteSampleKey, SurfaceMappedSample[]> samples,
        HashSet<int> reservedIds, ref int nextStageId, ref int nextNativeId,
        List<int> stagedRoutes, List<int> finalIds)
    {
        int count = candidate.Pattern?.RoutePieces.Count > 0 ? candidate.Pattern.RoutePieces.Count :
            candidate.ExclusionPieces is { Count: > 0 } ? candidate.ExclusionPieces.Sum(component => component.Count) :
            candidate.Paths.Count;
        candidate.NativePieceIds = PrepareNativePieceIds(target, count, reservedIds, ref nextNativeId);
        candidate.RoutePieceLengths = new double[count];
        var projected = new RoutePieceResult[count];
        CommittedRouteContext mapping = candidate.CommittedMapping;

        if (candidate.Pattern?.RoutePieces.Count > 0)
        {
            // 이 후보의 모든 조각이 공유하는 검증 키 접두부를 한 번만 만든다.
            ValidatedRouteSampleKeyPrefix sampleKeyPrefix =
                BuildValidatedRouteSampleKeyPrefix(candidate, side, mapping);
            var stagedIndices = new bool[count];
            for (int c = 0; c < candidate.Circuits.Count; c++)
            {
                CircuitRoute circuit = candidate.Circuits[c];
                List<int> indices = candidate.CircuitPieceIndices[c];
                RoutePieceResult previous = null;
                for (int j = 0; j < circuit.Pieces.Count; j++)
                {
                    int index = indices[j];
                    if (index < 0 || index >= count || stagedIndices[index])
                        throw new InvalidOperationException("회로와 원본 경로 조각의 대응이 중복되었거나 유효하지 않습니다");
                    SerpentineRoutePiece piece = circuit.Pieces[j];
                    if (piece.Points.Count < 2)
                        throw new InvalidOperationException("영역 " + candidate.Name + " 경로 조각이 퇴화했습니다");
                    Dictionary<int, SurfaceMappedSample> overrides = null;
                    if (j == 0)
                        AddExitOverride(ref overrides, 0, circuit.StartExit, piece.Points[0], side, mapping);
                    if (j == circuit.Pieces.Count - 1)
                        AddExitOverride(ref overrides, piece.Points.Count - 1, circuit.EndExit, piece.Points[^1], side, mapping);
                    int stageId = PrepareStageId(ref nextStageId);
                    stagedRoutes.Add(stageId);
                    finalIds.Add(candidate.NativePieceIds[index]);
                    RoutePieceResult shown = StageCurvedRoutePiece(candidate, side, piece, index, count,
                        stageId, exclusionMode: false, mappingContext: mapping,
                        vertexOverrides: overrides, validatedSamples: samples,
                        sampleKeyPrefix: sampleKeyPrefix);
                    ValidateProjectedRoutePiece(shown);
                    projected[index] = shown;
                    candidate.RoutePieceLengths[index] = shown.Length;
                    stagedIndices[index] = true;
                    if (previous != null && Distance3D(previous.End, shown.Start) > RouteJoinTolerance)
                        throw new InvalidOperationException("영역 " + candidate.Name + " 회로 " + (c + 1) + " 조각이 연결되지 않습니다");
                    previous = shown;
                    PrepareConnectorState(candidate, index, stageId);
                }
            }
            // 회로에 포함되지 않은 조각(비활성 턴 등)은 허용하지 않는다. 생성 경로 전체가
            // 하나의 회로이며, 누락된 조각은 조용히 스테이징하지 않고 명시적으로 실패한다.
            for (int i = 0; i < count; i++)
                if (!stagedIndices[i])
                    throw new InvalidOperationException(
                        "영역 " + candidate.Name + " 조각 " + (i + 1) + "이 회로에 포함되지 않았습니다");
        }
        else if (candidate.ExclusionPieces is { Count: > 0 })
        {
            // 호환 제외 경로도 기존 selected-face/global 매핑을 사용하되 엄격 검증을 통일한다.
            ValidatedRouteSampleKeyPrefix sampleKeyPrefix =
                BuildValidatedRouteSampleKeyPrefix(candidate, side, mapping);
            int index = 0;
            foreach (List<SerpentineRoutePiece> component in candidate.ExclusionPieces)
            {
                RoutePieceResult previous = null;
                foreach (SerpentineRoutePiece piece in component)
                {
                    if (piece.Points.Count < 2)
                        throw new InvalidOperationException("제외 경로 조각이 퇴화했습니다");
                    int stageId = PrepareStageId(ref nextStageId);
                    stagedRoutes.Add(stageId);
                    finalIds.Add(candidate.NativePieceIds[index]);
                    RoutePieceResult shown = StageCurvedRoutePiece(candidate, side, piece, index, count,
                        stageId, exclusionMode: true, mappingContext: mapping, validatedSamples: samples,
                        sampleKeyPrefix: sampleKeyPrefix);
                    ValidateProjectedRoutePiece(shown);
                    projected[index] = shown;
                    candidate.RoutePieceLengths[index] = HeatingPieceLength(piece, shown, exclusionMode: true);
                    if (previous != null && Distance3D(previous.End, shown.Start) > RouteJoinTolerance)
                        throw new InvalidOperationException("영역 " + candidate.Name + " 제외 경로 조각이 연결되지 않습니다");
                    previous = shown;
                    index++;
                }
            }
        }
        else
        {
            // 기존 Paths의 BRep 투영 정책은 유지한다. 소유권 없는 world overlay 대신
            // 동일 투영의 route piece로 보관해야 다음 차등 커밋에서 ID/객체를 retain할 수 있다.
            for (int i = 0; i < candidate.Paths.Count; i++)
            {
                List<Vec2> path = candidate.Paths[i];
                if (path.Count < 2) throw new InvalidOperationException("투영할 경로가 퇴화했습니다");
                int stageId = PrepareStageId(ref nextStageId);
                stagedRoutes.Add(stageId);
                finalIds.Add(candidate.NativePieceIds[i]);
                RoutePieceResult shown = Bridge.StageProjectedWorkingRoutePiece(side.Id.Value, side.Revision.Value,
                    stageId, "HeatingRun", Flatten(candidate.Frame, path), mapping.FaceIds,
                    -candidate.Frame.Normal.X, -candidate.Frame.Normal.Y, -candidate.Frame.Normal.Z);
                ValidateProjectedRoutePiece(shown);
                projected[i] = shown;
                candidate.RoutePieceLengths[i] = shown.Length;
            }
        }
        candidate.ProjectedPieces = projected.ToList();
    }

    private static int[] PrepareNativePieceIds(WorkingArea target, int count, HashSet<int> reservedIds,
        ref int nextNativeId)
    {
        if (target.NativePieceIds.Length == count && target.NativePieceIds.All(id => id >= 0) &&
            target.NativePieceIds.Distinct().Count() == count)
            return target.NativePieceIds.ToArray();
        var ids = new int[count];
        for (int i = 0; i < count; i++)
        {
            while (reservedIds.Contains(nextNativeId)) nextNativeId = checked(nextNativeId + 1);
            if (nextNativeId < 0) throw new InvalidOperationException("네이티브 경로 ID 범위를 초과했습니다");
            ids[i] = nextNativeId;
            reservedIds.Add(nextNativeId);
            nextNativeId = checked(nextNativeId + 1);
        }
        return ids;
    }

    private static int PrepareStageId(ref int nextStageId)
    {
        if (nextStageId >= 0 || nextStageId == int.MinValue)
            throw new InvalidOperationException("네이티브 임시 경로 ID 범위를 초과했습니다");
        int id = nextStageId;
        nextStageId--;
        return id;
    }

    private static double HeatingPieceLength(SerpentineRoutePiece piece, RoutePieceResult shown, bool exclusionMode) =>
        exclusionMode && piece.Kind != SerpentineRoutePieceKind.HeatingRun ? 0 : shown.Length;

    private static void PrepareConnectorState(WorkingArea candidate, int pieceIndex, int stageId)
    {
        SerpentineRoutePiece piece = candidate.Pattern.RoutePieces[pieceIndex];
        if (piece.Kind != SerpentineRoutePieceKind.TurnConnector) return;
        // 이 함수는 components 흐름에서만 호출되며 해당 조각은 항상 실제 connector로
        // 스테이징된다(exclusionMode:false). 회로를 나누지 않으므로 모든 커넥터는 활성이다.
        if (!Bridge.SetConnectorEnabled(stageId, true,
                candidate.Color.R / 255.0, candidate.Color.G / 255.0, candidate.Color.B / 255.0, false))
            throw new InvalidOperationException("영역 " + candidate.Name + "의 후보 커넥터 상태를 적용하지 못했습니다");
    }

    private static void ValidateProjectedRoutePiece(RoutePieceResult piece)
    {
        if (piece == null || !double.IsFinite(piece.Length) || piece.Length <= 0 ||
            !IsFiniteRoutePoint(piece.Start) || !IsFiniteRoutePoint(piece.End))
            throw new InvalidOperationException("네이티브 경로 스테이징 결과의 길이/끝점이 유효하지 않습니다");
    }

    private static bool IsFiniteRoutePoint(double[] xyz) => xyz is { Length: 3 } &&
        double.IsFinite(xyz[0]) && double.IsFinite(xyz[1]) && double.IsFinite(xyz[2]);

    private static void PrepareCommittedRoutePresentation(WorkingArea area, List<double> endpoints,
        out double length, out int count)
    {
        length = 0;
        count = 0;
        if (area.NativePieceIds.Length != area.RoutePieceLengths.Length ||
            area.NativePieceIds.Length != area.ProjectedPieces.Count ||
            area.RoutePieceLengths.Any(value => !double.IsFinite(value) || value < 0))
            throw new InvalidOperationException("영역 " + area.Name + "의 확정 경로 ID/길이/끝점 대응이 유효하지 않습니다");
        if (area.Pattern?.RoutePieces.Count > 0)
        {
            if (area.CircuitPieceIndices.Count != area.Circuits.Count)
                throw new InvalidOperationException("영역 " + area.Name + "의 확정 회로 대응이 유효하지 않습니다");
            foreach (List<int> indices in area.CircuitPieceIndices)
            {
                if (indices.Count == 0 || indices.Any(index => index < 0 || index >= area.ProjectedPieces.Count))
                    throw new InvalidOperationException("확정 회로에 유효하지 않은 경로 조각 대응이 있습니다");
                RoutePieceResult first = area.ProjectedPieces[indices[0]];
                RoutePieceResult last = area.ProjectedPieces[indices[^1]];
                if (first == null || last == null || !IsFiniteRoutePoint(first.Start) || !IsFiniteRoutePoint(last.End))
                    throw new InvalidOperationException("확정 회로의 인출점이 유효하지 않습니다");
                endpoints.AddRange(first.Start);
                endpoints.AddRange(last.End);
                foreach (int index in indices) length += area.RoutePieceLengths[index];
            }
            count = area.Circuits.Count;
        }
        else
        {
            foreach (double value in area.RoutePieceLengths) length += value;
            count = area.NativePieceIds.Length;
        }
        if (!double.IsFinite(length)) throw new InvalidOperationException("확정 회로의 길이 합계가 유효하지 않습니다");
    }

    /// <summary>자동 속성의 준비된 참조/값만 대입한다. 계산/컬렉션 변경/UI 호출 금지.</summary>
    private static void PublishAreaRouteCandidate(WorkingArea target, WorkingArea candidate)
    {
        target.Direction = candidate.Direction;
        target.MarginText = candidate.MarginText;
        target.SpacingText = candidate.SpacingText;
        target.Exclusions = candidate.Exclusions;
        target.ExitFaceId = candidate.ExitFaceId;
        target.ExitFaceRevision = candidate.ExitFaceRevision;
        target.ExitChain = candidate.ExitChain;
        target.ExitChain3D = candidate.ExitChain3D;
        target.ExitChainFaceIds = candidate.ExitChainFaceIds;
        target.StartAnchor = candidate.StartAnchor;
        target.EndAnchor = candidate.EndAnchor;
        target.ExitEdgeRefs = candidate.ExitEdgeRefs;
        target.ExitBoundary = candidate.ExitBoundary;
        target.RegionExitBoundary = candidate.RegionExitBoundary;
        target.RegionCadBoundary = candidate.RegionCadBoundary;
        target.SelectedStartExit = candidate.SelectedStartExit;
        target.SelectedEndExit = candidate.SelectedEndExit;
        target.SelectedAnchorCandidate = candidate.SelectedAnchorCandidate;
        target.Circuits = candidate.Circuits;
        target.CircuitPieceIndices = candidate.CircuitPieceIndices;
        target.AnchorCandidates = candidate.AnchorCandidates;
        target.ExcludedCandidates = candidate.ExcludedCandidates;
        target.ExcludedPlan = candidate.ExcludedPlan;
        target.ExcludedCandidateFailure = candidate.ExcludedCandidateFailure;
        target.ExcludedPlanKey = candidate.ExcludedPlanKey;
        target.LastExitFailure = candidate.LastExitFailure;
        target.Pattern = candidate.Pattern;
        target.ProjectedPieces = candidate.ProjectedPieces;
        target.NativePieceIds = candidate.NativePieceIds;
        target.RoutePieceLengths = candidate.RoutePieceLengths;
        target.Paths = candidate.Paths;
        target.ExclusionPieces = candidate.ExclusionPieces;
        target.PatternDisplayed = candidate.PatternDisplayed;
        target.RouteEdited = candidate.RouteEdited;
        target.AppliedInputs = candidate.AppliedInputs;
        target.CommittedMapping = candidate.CommittedMapping;
    }

    private bool ReportRoutePreparationFailure(Exception ex)
    {
        try
        {
            if (StatusText != null)
                StatusText.Text = "표시 생성 실패 — 기존 영역/경로를 유지했습니다: " + ex.Message;
            ErrorReporter.ShowError("경로 표시 생성 실패 — 기존 영역/경로를 유지했습니다", ex);
        }
        catch (Exception) { }
        return false;
    }

    /// <summary>문자열은 값 지문, boundary는 불변 질의 객체 identity로 비교한다.</summary>
    private sealed class ValidatedRouteSampleKey : IEquatable<ValidatedRouteSampleKey>
    {
        private readonly string _value;
        private readonly IExitBoundary2D _boundary;
        internal ValidatedRouteSampleKey(string value, IExitBoundary2D boundary)
        { _value = value; _boundary = boundary; }
        public bool Equals(ValidatedRouteSampleKey other) => other != null &&
            ReferenceEquals(_boundary, other._boundary) && string.Equals(_value, other._value, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is ValidatedRouteSampleKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(_value,
            _boundary == null ? 0 : RuntimeHelpers.GetHashCode(_boundary));
    }

    /// <summary>
    /// 같은 후보의 모든 조각이 공유하는 검증 키 접두부를 한 번만 만든다. 조각별 좌표와
    /// endpoint override는 <see cref="ValidatedRouteSampleKeyPrefix.Build"/>에서 덧붙인다.
    /// </summary>
    private ValidatedRouteSampleKeyPrefix BuildValidatedRouteSampleKeyPrefix(WorkingArea area,
        WorkingSideDefinition side, CommittedRouteContext context)
    {
        var key = new StringBuilder("strict-v2|gap=0.1|chord=0.05|max-probes=100000|support=0.1|datum=0.1|selected-global|")
            .Append(GeometryTolerancePolicy.Version).Append('|');
        void Number(double value) => key.Append(value.ToString("R", CultureInfo.InvariantCulture)).Append(',');
        void Point(Vec2 point) { Number(point.X); Number(point.Y); }
        void Point3(Vec3 point) { Number(point.X); Number(point.Y); Number(point.Z); }
        void Binding(ExitEdgeBinding? binding)
        {
            if (!binding.HasValue) { key.Append("unbound;"); return; }
            ExitEdgeBinding b = binding.Value;
            key.Append(b.ModelRevision).Append(',').Append(b.WorkingContextId).Append(',')
                .Append(b.WorkingContextRevision).Append(',').Append(b.EdgeKey).Append(',');
            Number(b.FirstParameter); Number(b.LastParameter); Number(b.Parameter);
            key.Append(b.Reversed).Append(';');
        }
        void Artificial(ArtificialEdgeBinding? binding)
        {
            if (!binding.HasValue) { key.Append("no-artificial;"); return; }
            ArtificialEdgeBinding b = binding.Value;
            key.Append(b.WorkingContextId).Append(',')
                .Append(b.WorkingContextRevision).Append(',')
                .Append(b.Side).Append(',');
            Number(b.MinX); Number(b.MinY); Number(b.MaxX); Number(b.MaxY);
            Number(b.FirstParameter); Number(b.LastParameter); Number(b.Parameter);
            key.Append(';');
        }
        void Split(SplitLineBinding? binding)
        {
            if (!binding.HasValue) { key.Append("no-split;"); return; }
            SplitLineBinding b = binding.Value;
            key.Append(b.WorkingContextId).Append(',')
                .Append(b.WorkingContextRevision).Append(',');
            Number(b.AX); Number(b.AY); Number(b.BX); Number(b.BY);
            Number(b.FirstParameter); Number(b.LastParameter); Number(b.Parameter);
            key.Append(';');
        }
        void Exits(IReadOnlyList<CircuitExitPoint> exits)
        {
            key.Append(exits.Count).Append(':');
            foreach (CircuitExitPoint exit in exits)
            {
                key.Append(exit.Role).Append(',').Append(exit.BoundarySegmentIndex).Append(',');
                Number(exit.BoundaryParameter); Point(exit.Point); Binding(exit.Binding);
                Artificial(exit.ArtificialBinding);
                Split(exit.SplitBinding);
            }
        }
        key.Append(_modelRevision).Append('|').Append(side.Id.Value).Append('|').Append(side.Revision.Value).Append('|');
        Point3(side.Frame.Origin); Point3(side.Frame.AxisX); Point3(side.Frame.AxisY); Point3(side.Frame.Normal);
        Point3(area.Frame.Origin); Point3(area.Frame.AxisX); Point3(area.Frame.AxisY); Point3(area.Frame.Normal);
        key.Append('|').Append(BuildRouteContextKey(area)).Append('|').Append(area.Direction).Append('|');
        key.Append(context.FaceIds.Length).Append(':');
        foreach (int faceId in context.FaceIds) key.Append(faceId).Append(',');
        key.Append('|').Append(context.ExitFaceId).Append('|').Append(area.ExitFaceRevision).Append('|');
        key.Append(context.ExitEdgeRefs.Count).Append(':');
        foreach (ExitEdgeReference edge in context.ExitEdgeRefs)
        {
            key.Append(edge.Revision).Append(',').Append(edge.EdgeKey).Append(',').Append(edge.FaceId).Append(',');
            Number(edge.FirstParameter); Number(edge.LastParameter);
            key.Append(edge.Reversed).Append(',').Append(edge.StartIndex).Append(',').Append(edge.EndIndex).Append(';');
        }
        AnchoredCandidate selected = context.SelectedAnchorCandidate;
        key.Append('|').Append(selected?.CandidateId.Length ?? -1).Append(':').Append(selected?.CandidateId).Append('|');
        if (selected != null)
        {
            Point(selected.Start); Point(selected.End); Number(selected.AngleDeg);
            key.Append(selected.StartLeftToRight).Append('|');
            Binding(selected.StartBinding); Binding(selected.EndBinding);
        }
        Exits(context.StartExits); Exits(context.EndExits);
        key.Append('|').Append(area.Pattern?.CandidateId?.Length ?? -1).Append(':').Append(area.Pattern?.CandidateId);
        return new ValidatedRouteSampleKeyPrefix(key.ToString(), context.Boundary);
    }

    /// <summary>후보 공통 검증 키 접두부 + 조각별 형상/override 접미부.</summary>
    private sealed class ValidatedRouteSampleKeyPrefix
    {
        private readonly string _prefix;
        private readonly IExitBoundary2D _boundary;

        internal ValidatedRouteSampleKeyPrefix(string prefix, IExitBoundary2D boundary)
        {
            _prefix = prefix;
            _boundary = boundary;
        }

        internal ValidatedRouteSampleKey Build(SerpentineRoutePiece piece,
            IReadOnlyDictionary<int, SurfaceMappedSample> overrides)
        {
            var key = new StringBuilder(_prefix);
            void Number(double value) => key.Append(value.ToString("R", CultureInfo.InvariantCulture)).Append(',');
            void Point(Vec2 point) { Number(point.X); Number(point.Y); }
            void Point3(Vec3 point) { Number(point.X); Number(point.Y); Number(point.Z); }

            key.Append('|').Append(piece.Kind).Append(',').Append(piece.LaneIndex).Append(',')
                .Append(piece.Points.Count).Append(':');
            foreach (Vec2 point in piece.Points) Point(point);
            key.Append('|').Append(overrides?.Count ?? 0).Append(':');
            if (overrides != null)
                foreach (var entry in overrides.OrderBy(pair => pair.Key))
                {
                    key.Append(entry.Key).Append(',').Append(entry.Value.FaceId).Append(',');
                    Point(entry.Value.DatumPoint); Point3(entry.Value.Point);
                }
            return new ValidatedRouteSampleKey(key.ToString(), _boundary);
        }
    }
}
