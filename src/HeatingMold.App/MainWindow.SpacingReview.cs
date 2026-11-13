using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

internal sealed class SpacingMarker
{
    internal string AreaId { get; init; } = string.Empty;
    internal int RunIndex { get; init; }
    internal int OtherRunIndex { get; init; }
    internal Vec2 At { get; init; }
    internal Vec2 Other { get; init; }
    internal double ConfiguredOuterGapMm { get; init; }
    internal double ActualOuterGapMm { get; init; }
    internal byte R { get; init; }
    internal byte G { get; init; }
    internal byte B { get; init; }
}

internal readonly record struct SpacingPairSample(
    int RunIndex,
    int OtherRunIndex,
    int AtRunIndex,
    Vec2 At,
    Vec2 Other,
    double ActualOuterGapMm);

internal static class SpacingMarkerSelection
{
    internal static IReadOnlyList<SpacingPairSample> Collapse(
        RouteSpacingResult? result,
        IReadOnlyList<IReadOnlyList<Vec2>>? runs)
    {
        if (result == null || runs == null || result.Status != RouteSpacingStatus.Completed)
            return Array.Empty<SpacingPairSample>();

        var collapsed = new List<SpacingPairSample>();
        foreach (RouteSpacingDirectionGroup group in result.DirectionGroups)
        {
            foreach (RouteSpacingPairResult pair in group.Pairs)
            {
                if (pair.RunIndex < 0 || pair.RunIndex >= runs.Count ||
                    pair.OtherRunIndex < 0 || pair.OtherRunIndex >= runs.Count ||
                    pair.RunIndex == pair.OtherRunIndex)
                {
                    continue;
                }

                int otherIndex = pair.AtRunIndex == pair.OtherRunIndex ? pair.RunIndex : pair.OtherRunIndex;
                IReadOnlyList<Vec2>? otherRun = runs[otherIndex];
                if (otherRun == null || otherRun.Count == 0) continue;

                collapsed.Add(new SpacingPairSample(
                    pair.RunIndex,
                    pair.OtherRunIndex,
                    pair.AtRunIndex,
                    pair.At,
                    NearestPointOnRun(otherRun, pair.At),
                    pair.MinOuterGapMm));
            }
        }

        collapsed.Sort(static (left, right) =>
        {
            int byRun = left.RunIndex.CompareTo(right.RunIndex);
            return byRun != 0 ? byRun : left.OtherRunIndex.CompareTo(right.OtherRunIndex);
        });
        return collapsed;
    }

    private static Vec2 NearestPointOnRun(IReadOnlyList<Vec2> run, Vec2 point)
    {
        Vec2 best = run[0];
        double bestDistance = double.PositiveInfinity;
        if (run.Count == 1) return best;

        for (int index = 1; index < run.Count; index++)
        {
            Vec2 start = run[index - 1];
            Vec2 end = run[index];
            Vec2 delta = end - start;
            double lengthSquared = delta.X * delta.X + delta.Y * delta.Y;
            double t;
            if (lengthSquared <= 1e-12)
            {
                t = 0;
            }
            else
            {
                t = ((point.X - start.X) * delta.X + (point.Y - start.Y) * delta.Y) / lengthSquared;
                t = Math.Clamp(t, 0, 1);
            }

            Vec2 candidate = new(start.X + delta.X * t, start.Y + delta.Y * t);
            double distance = (candidate.X - point.X) * (candidate.X - point.X) +
                              (candidate.Y - point.Y) * (candidate.Y - point.Y);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }
}

public partial class MainWindow
{
    internal const double SpacingMarkerTextHeightMm = 8.0;
    private const double SpacingReviewSampleStepMm = 1.0;
    private const double SpacingMarkerColorBlend = 0.55;

    private readonly SpacingReviewCoordinator _spacingReview = new();
    private CancellationTokenSource _spacingReviewCancellation = new();
    private string _spacingReviewFingerprint = string.Empty;
    private string _spacingReviewSummary = string.Empty;
    private double _spacingReviewCollectionMilliseconds;
    private double _spacingReviewAnalysisMilliseconds;

    internal string SpacingReviewSummary => _spacingReviewSummary;
    internal string SpacingReviewFingerprint => _spacingReviewFingerprint;
    internal double SpacingReviewCollectionMilliseconds => _spacingReviewCollectionMilliseconds;
    internal double SpacingReviewAnalysisMilliseconds => _spacingReviewAnalysisMilliseconds;

    /// <summary>대기 중인 분석과 게시된 간격 표시 상태를 모두 버립니다.</summary>
    internal void InvalidateSpacingReview()
    {
        CancelPendingSpacingReview();
        _spacingReview.Invalidate();
        _spacingReviewFingerprint = string.Empty;
        _spacingReviewSummary = string.Empty;
        _spacingReviewCollectionMilliseconds = 0;
        _spacingReviewAnalysisMilliseconds = 0;
    }

    private void CancelPendingSpacingReview()
    {
        CancellationTokenSource previous = _spacingReviewCancellation;
        try { previous.Cancel(); }
        catch (ObjectDisposedException) { }
        previous.Dispose();
        _spacingReviewCancellation = new CancellationTokenSource();
    }

    /// <summary>
    /// 확정 경로의 발열사 간격 표시를 갱신합니다. 수집(확정 체인 읽기·2D 투영)은 UI 스레드에서
    /// 수행하고, 대량 2D 비교는 불변 스냅샷으로 백그라운드에서 계산한 뒤 UI 스레드에서 게시합니다.
    /// 수집 실패는 즉시 경고로 반환하고, 분석·게시 실패는 완료 상태 경고로 남기며 확정 기하는
    /// 되돌리지 않습니다.
    /// </summary>
    private bool TryRefreshSpacingMarkers(WorkingSideDefinition side, IReadOnlyList<WorkingArea> areas, out string warning)
    {
        warning = string.Empty;
        CancelPendingSpacingReview();
        _spacingReviewSummary = string.Empty;
        _spacingReviewFingerprint = string.Empty;

        if (side == null || side.Id.Value <= 0 || areas == null)
        {
            _spacingReview.Invalidate();
            warning = "발열사 간격 표시에 필요한 작업면 정보가 없습니다.";
            return false;
        }

        var candidates = new List<SpacingAreaCandidate>();
        foreach (WorkingArea area in areas)
        {
            if (area == null || !area.PatternDisplayed) continue;
            candidates.Add(new SpacingAreaCandidate
            {
                AreaId = area.AreaId.ToString(CultureInfo.InvariantCulture),
                AreaName = area.Name,
                Displayed = true,
                Pattern = area.Pattern,
                NativePieceIds = area.NativePieceIds,
                RoutePieceLengths = area.RoutePieceLengths,
                AppliedInputs = area.AppliedInputs,
                R = area.Color.R,
                G = area.Color.G,
                B = area.Color.B
            });
        }

        var collectionWatch = Stopwatch.StartNew();
        bool collected = SpacingReviewCollector.TryCollect(
            side.Id.Value,
            side.Revision.Value,
            side.Frame.Project,
            candidates,
            Bridge.GetCommittedRouteChains,
            out List<SpacingRunEntry> runs,
            out List<string> failures);
        collectionWatch.Stop();
        _spacingReviewCollectionMilliseconds = collectionWatch.Elapsed.TotalMilliseconds;

        if (!collected)
        {
            // 일부만 수집한 결과를 정상 분석처럼 게시하지 않는다. 호출부가 기존 마커를 지운다.
            _spacingReview.Invalidate();
            warning = "발열사 간격 분석을 완료하지 못했습니다 — " + string.Join(" / ", failures.Take(5));
            return false;
        }

        if (runs.Count == 0)
        {
            _spacingReview.Invalidate();
            try
            {
                Bridge.ClearSpacingMarkers();
            }
            catch (Exception exception)
            {
                warning = "발열사 간격 표시를 지우지 못했습니다 — " + exception.Message;
                return false;
            }

            _spacingReviewSummary = "발열사 간격 표시: 확정된 발열선이 없습니다.";
            RedrawAfterSpacingReview();
            return true;
        }

        long modelRevision = _modelRevision;
        long contextId = side.Id.Value;
        long contextRevision = side.Revision.Value;
        string fingerprint = SpacingAnalysisFingerprint.Build(
            modelRevision, contextId, contextRevision, side.Frame, runs);
        long generation = _spacingReview.BeginRequest(fingerprint);

        if (_spacingReview.TryGetPublished(fingerprint, out SpacingMarker[] cachedMarkers, out string cachedSummary))
        {
            if (!TryReplaceSpacingMarkers(contextId, contextRevision, side.Frame, cachedMarkers, out warning))
                return false;

            _spacingReviewFingerprint = fingerprint;
            _spacingReviewSummary = cachedSummary;
            RedrawAfterSpacingReview();
            return true;
        }

        var inputs = new List<RouteSpacingRunInput>(runs.Count);
        foreach (SpacingRunEntry entry in runs)
        {
            inputs.Add(new RouteSpacingRunInput(
                entry.AreaId,
                entry.PieceIndex,
                entry.Points,
                entry.ConfiguredGapMm,
                entry.WireDiameterMm,
                entry.DirectionDeg));
        }

        IReadOnlyList<SpacingRunEntry> snapshot = runs.ToArray();
        CancellationToken token = _spacingReviewCancellation.Token;
        Task<SpacingAnalysisOutcome> analysis = Task.Run(
            () => AnalyzeSpacingReview(inputs, snapshot, token), token);
        analysis.ContinueWith(
            task =>
            {
                try
                {
                    System.Windows.Threading.Dispatcher? dispatcher = Dispatcher;
                    dispatcher?.BeginInvoke(new Action(() => CompleteSpacingReview(
                        generation, fingerprint, modelRevision, contextId, contextRevision, side.Frame, task)));
                }
                catch (Exception)
                {
                    // 창이 닫히는 중이면 게시할 대상이 없다. 확정 기하는 그대로 둔다.
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return true;
    }

    private static SpacingAnalysisOutcome AnalyzeSpacingReview(
        IReadOnlyList<RouteSpacingRunInput> inputs,
        IReadOnlyList<SpacingRunEntry> runs,
        CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        token.ThrowIfCancellationRequested();
        RouteSpacingResult result = RouteSpacingAnalyzer.Analyze(inputs, SpacingReviewSampleStepMm, token);
        token.ThrowIfCancellationRequested();
        List<SpacingMarker> markers = BuildSpacingMarkers(result, runs);
        watch.Stop();

        double minConfigured = runs.Min(entry => entry.ConfiguredGapMm);
        double maxConfigured = runs.Max(entry => entry.ConfiguredGapMm);
        return new SpacingAnalysisOutcome(
            result,
            markers,
            runs.Count,
            minConfigured,
            maxConfigured,
            watch.Elapsed.TotalMilliseconds);
    }

    private static List<SpacingMarker> BuildSpacingMarkers(
        RouteSpacingResult result,
        IReadOnlyList<SpacingRunEntry> runs)
    {
        var markers = new List<SpacingMarker>();
        if (result.Status != RouteSpacingStatus.Completed) return markers;

        var polylines = runs.Select(entry => entry.Points).ToList();
        foreach (SpacingPairSample pair in SpacingMarkerSelection.Collapse(result, polylines))
        {
            if (pair.RunIndex < 0 || pair.RunIndex >= runs.Count ||
                pair.OtherRunIndex < 0 || pair.OtherRunIndex >= runs.Count)
            {
                continue;
            }

            int markerRunIndex = pair.AtRunIndex >= 0 && pair.AtRunIndex < runs.Count
                ? pair.AtRunIndex
                : pair.RunIndex;
            int otherRunIndex = markerRunIndex == pair.OtherRunIndex ? pair.RunIndex : pair.OtherRunIndex;
            if (otherRunIndex < 0 || otherRunIndex >= runs.Count)
            {
                continue;
            }

            SpacingRunEntry entry = runs[markerRunIndex];
            markers.Add(new SpacingMarker
            {
                AreaId = entry.AreaId,
                RunIndex = entry.PieceIndex,
                OtherRunIndex = runs[otherRunIndex].PieceIndex,
                At = pair.At,
                Other = pair.Other,
                ConfiguredOuterGapMm = entry.ConfiguredGapMm,
                ActualOuterGapMm = pair.ActualOuterGapMm,
                R = BlendSpacingMarkerColor(entry.R),
                G = BlendSpacingMarkerColor(entry.G),
                B = BlendSpacingMarkerColor(entry.B)
            });
        }

        return markers;
    }

    private void CompleteSpacingReview(
        long generation,
        string fingerprint,
        long modelRevision,
        long contextId,
        long contextRevision,
        ProjectionFrame frame,
        Task<SpacingAnalysisOutcome> task)
    {
        // 늦게 끝난 이전 분석은 새 결과를 덮지 않는다.
        if (!_spacingReview.IsCurrent(generation, fingerprint)) return;
        WorkingSideDefinition? confirmed = _workingSide?.Confirmed;
        if (_modelRevision != modelRevision || confirmed == null ||
            confirmed.Id.Value != contextId || confirmed.Revision.Value != contextRevision)
        {
            return;
        }

        if (task.IsCanceled) return;
        if (task.IsFaulted)
        {
            FailSpacingReview(task.Exception?.GetBaseException().Message ?? "발열사 간격을 계산하지 못했습니다.");
            return;
        }

        SpacingAnalysisOutcome outcome = task.Result;
        _spacingReviewAnalysisMilliseconds = outcome.AnalysisMilliseconds;
        if (outcome.Result.Status == RouteSpacingStatus.InvalidInput)
        {
            FailSpacingReview(outcome.Result.Message);
            return;
        }

        string summary = outcome.Markers.Count == 0
            ? BuildEmptySpacingSummary(outcome.Result, outcome.RunCount)
            : BuildSpacingSummary(outcome);
        if (!TryReplaceSpacingMarkers(contextId, contextRevision, frame, outcome.Markers, out string failure))
        {
            FailSpacingReview(failure);
            return;
        }

        _spacingReviewFingerprint = fingerprint;
        _spacingReviewSummary = summary;
        _spacingReview.TryPublish(generation, fingerprint, outcome.Markers.ToArray(), summary);
        RedrawAfterSpacingReview();
    }

    private void FailSpacingReview(string message)
    {
        _spacingReviewSummary = "발열사 간격 표시: " + message;
        _routePresentationWarning = message;
        try { Bridge.ClearSpacingMarkers(); }
        catch (Exception) { }
        try
        {
            if (StatusText != null && !StatusText.Text.Contains(message, StringComparison.Ordinal))
                StatusText.Text += " · 경고: " + message;
        }
        catch (Exception) { }
        RedrawAfterSpacingReview();
    }

    private static string BuildSpacingSummary(SpacingAnalysisOutcome outcome)
    {
        double minActual = outcome.Markers.Min(marker => marker.ActualOuterGapMm);
        double maxActual = outcome.Markers.Max(marker => marker.ActualOuterGapMm);
        double minConfigured = outcome.MinConfiguredOuterGapMm;
        double maxConfigured = outcome.MaxConfiguredOuterGapMm;
        string configuredText = Math.Abs(maxConfigured - minConfigured) <= 1e-9
            ? minConfigured.ToString("F1", CultureInfo.InvariantCulture)
            : string.Format(
                CultureInfo.InvariantCulture,
                "{0:F1}~{1:F1}",
                minConfigured,
                maxConfigured);

        var summary = new StringBuilder();
        summary.AppendFormat(
            CultureInfo.InvariantCulture,
            "발열사 간격 표시: {0}개 위치, 실제 외곽 간격 {1:F1}~{2:F1} mm (설정 {3} mm)",
            outcome.Markers.Count,
            minActual,
            maxActual,
            configuredText);
        AppendSpacingDirectionNote(summary, outcome.Result);
        return summary.ToString();
    }

    private static string BuildEmptySpacingSummary(RouteSpacingResult result, int runCount)
    {
        if (runCount == 0)
            return "발열사 간격 표시: 확정된 발열선이 없습니다.";
        if (result.Status == RouteSpacingStatus.DirectionSeparated)
            return "발열사 간격 표시: " + result.Message;
        return "발열사 간격 표시: 이웃 발열선이 없어 표시할 위치가 없습니다.";
    }

    private static void AppendSpacingDirectionNote(StringBuilder builder, RouteSpacingResult result)
    {
        if (result.DirectionGroups.Count > 1)
        {
            builder.AppendFormat(
                CultureInfo.InvariantCulture,
                " · 생성 방향이 다른 {0}개 그룹은 서로 비교하지 않았습니다",
                result.DirectionGroups.Count);
        }

        if (result.UncomparedRunCount > 0)
        {
            builder.AppendFormat(
                CultureInfo.InvariantCulture,
                " · 이웃이 없는 방향의 레인 {0}개는 비교하지 않았습니다",
                result.UncomparedRunCount);
        }
    }

    private bool TryReplaceSpacingMarkers(
        long contextId,
        long contextRevision,
        ProjectionFrame frame,
        IReadOnlyList<SpacingMarker> markers,
        out string failure)
    {
        failure = string.Empty;
        if (markers == null || markers.Count == 0)
        {
            try
            {
                Bridge.ClearSpacingMarkers();
                return true;
            }
            catch (Exception exception)
            {
                failure = "발열사 간격 표시를 지우지 못했습니다 — " + exception.Message;
                return false;
            }
        }

        int markerCount = markers.Count;
        var anchors = new double[markerCount * 3];
        var targets = new double[markerCount * 3];
        var texts = new string[markerCount];
        var colors = new double[markerCount * 3];
        for (int index = 0; index < markerCount; index++)
        {
            SpacingMarker marker = markers[index];
            Vec3 anchor = frame.Unproject(marker.At);
            Vec3 target = frame.Unproject(marker.Other);
            anchors[index * 3] = anchor.X;
            anchors[index * 3 + 1] = anchor.Y;
            anchors[index * 3 + 2] = anchor.Z;
            targets[index * 3] = target.X;
            targets[index * 3 + 1] = target.Y;
            targets[index * 3 + 2] = target.Z;
            texts[index] = marker.ActualOuterGapMm.ToString("F1", CultureInfo.InvariantCulture);
            colors[index * 3] = marker.R / 255.0;
            colors[index * 3 + 1] = marker.G / 255.0;
            colors[index * 3 + 2] = marker.B / 255.0;
        }

        try
        {
            if (Bridge.ReplaceSpacingMarkers(
                    contextId,
                    contextRevision,
                    anchors,
                    targets,
                    texts,
                    colors,
                    SpacingMarkerTextHeightMm))
            {
                return true;
            }

            failure = "발열사 간격 표시를 갱신하지 못했습니다.";
            return false;
        }
        catch (Exception exception)
        {
            failure = "발열사 간격 표시를 갱신하지 못했습니다 — " + exception.Message;
            return false;
        }
    }

    private void RedrawAfterSpacingReview()
    {
        try { Bridge.TryRedrawView(); }
        catch (Exception) { }
    }

    private static byte BlendSpacingMarkerColor(byte value)
    {
        double blended = value + (255 - value) * SpacingMarkerColorBlend;
        return (byte)Math.Clamp(Math.Round(blended, MidpointRounding.AwayFromZero), 0, 255);
    }

    private sealed record SpacingAnalysisOutcome(
        RouteSpacingResult Result,
        List<SpacingMarker> Markers,
        int RunCount,
        double MinConfiguredOuterGapMm,
        double MaxConfiguredOuterGapMm,
        double AnalysisMilliseconds);
}
