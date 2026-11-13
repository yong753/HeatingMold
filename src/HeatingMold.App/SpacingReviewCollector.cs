using System;
using System.Collections.Generic;
using HeatingMold.Core.Geometry;

namespace HeatingMold.App;

/// <summary>
/// 간격 분석에 필요한 확정 영역 상태의 읽기 전용 스냅샷.
/// 확정 경로가 화면에 표시되지 않은 영역(초안·미리보기)은 수집 대상이 아니다.
/// </summary>
internal sealed class SpacingAreaCandidate
{
    public string AreaId { get; init; } = string.Empty;
    public string AreaName { get; init; } = string.Empty;
    public bool Displayed { get; init; }
    public SerpentineResult? Pattern { get; init; }
    public int[]? NativePieceIds { get; init; }
    public double[]? RoutePieceLengths { get; init; }
    public AppliedGenerationInputs? AppliedInputs { get; init; }
    public byte R { get; init; }
    public byte G { get; init; }
    public byte B { get; init; }
}

/// <summary>
/// 간격 분석 입력으로 보존하는 확정 발열선 하나의 메타데이터.
/// 영역·조각 identity, 확정 당시 간격·직경, 생성 방향을 함께 유지한다.
/// </summary>
internal sealed class SpacingRunEntry
{
    public string AreaId { get; init; } = string.Empty;
    public string AreaName { get; init; } = string.Empty;
    public int PieceIndex { get; init; }
    public IReadOnlyList<Vec2> Points { get; init; } = Array.Empty<Vec2>();
    public double ConfiguredGapMm { get; init; }
    public double WireDiameterMm { get; init; }
    public double DirectionDeg { get; init; }
    public byte R { get; init; }
    public byte G { get; init; }
    public byte B { get; init; }
}

/// <summary>
/// 같은 작업평면의 확정 발열선을 모으는 수집기.
/// 설정 간격·직경이 다른 영역이라도 수집을 끊지 않는다. 수집 도중 문제가 생기면
/// 일부만 분석한 결과를 정상 분석으로 게시하지 않도록 실패를 함께 돌려준다.
/// </summary>
internal static class SpacingReviewCollector
{
    internal delegate double[][]? CommittedChainReader(long contextId, long revision, int[] pieceIds);

    /// <summary>
    /// 확정 발열선을 수집한다. 표시되지 않은 영역은 정상적으로 건너뛰고,
    /// 그 밖의 누락·불일치·읽기 실패는 전부 <paramref name="failures"/>에 남긴 뒤 false를 반환한다.
    /// </summary>
    internal static bool TryCollect(
        long contextId,
        long revision,
        Func<Vec3, Vec2>? project,
        IReadOnlyList<SpacingAreaCandidate>? areas,
        CommittedChainReader? readChains,
        out List<SpacingRunEntry> runs,
        out List<string> failures)
    {
        runs = new List<SpacingRunEntry>();
        failures = new List<string>();

        if (areas == null)
        {
            failures.Add("영역 목록이 없습니다.");
            return false;
        }
        if (project == null)
        {
            failures.Add("작업평면 정보가 없습니다.");
            return false;
        }
        if (readChains == null)
        {
            failures.Add("확정 경로 조회를 사용할 수 없습니다.");
            return false;
        }

        foreach (SpacingAreaCandidate? area in areas)
        {
            if (area == null || !area.Displayed) continue;

            string label = string.IsNullOrWhiteSpace(area.AreaName) ? area.AreaId : area.AreaName;

            SerpentineResult? pattern = area.Pattern;
            if (pattern == null)
            {
                failures.Add($"{label}: 확정 경로 정보가 없습니다.");
                continue;
            }

            IReadOnlyList<SerpentineRoutePiece>? pieces = pattern.RoutePieces;
            int pieceCount = pieces?.Count ?? 0;
            if (pieceCount <= 0)
            {
                failures.Add($"{label}: 확정 경로 조각이 없습니다.");
                continue;
            }

            int[]? pieceIds = area.NativePieceIds;
            double[]? lengths = area.RoutePieceLengths;
            if (pieceIds == null || lengths == null ||
                pieceIds.Length != pieceCount || lengths.Length != pieceCount)
            {
                failures.Add($"{label}: 확정 조각 ID/길이 정보가 일치하지 않습니다.");
                continue;
            }

            AppliedGenerationInputs? applied = area.AppliedInputs;
            if (applied == null)
            {
                failures.Add($"{label}: 확정 당시 간격·직경 정보가 없습니다.");
                continue;
            }
            if (!IsFinite(applied.OuterGapMm) || applied.OuterGapMm <= 0)
            {
                failures.Add($"{label}: 확정 당시 발열사 외곽 간격이 올바르지 않습니다.");
                continue;
            }
            if (!IsFinite(applied.WireDiameterMm) || applied.WireDiameterMm < 0)
            {
                failures.Add($"{label}: 확정 당시 전선 직경이 올바르지 않습니다.");
                continue;
            }
            if (!IsFinite(pattern.AngleDeg))
            {
                failures.Add($"{label}: 확정 생성 방향이 올바르지 않습니다.");
                continue;
            }

            double[][]? chains;
            try
            {
                chains = readChains(contextId, revision, pieceIds);
            }
            catch (Exception exception)
            {
                failures.Add($"{label}: 확정 경로를 읽지 못했습니다 — {exception.Message}");
                continue;
            }
            if (chains == null || chains.Length != pieceCount)
            {
                failures.Add($"{label}: 확정 경로 조각 수가 일치하지 않습니다.");
                continue;
            }

            for (int pieceIndex = 0; pieceIndex < pieceCount; pieceIndex++)
            {
                if (pieces![pieceIndex].Kind != SerpentineRoutePieceKind.HeatingRun) continue;

                double[]? chain = chains[pieceIndex];
                if (chain == null || chain.Length < 6 || chain.Length % 3 != 0)
                {
                    failures.Add($"{label} 조각 {pieceIndex + 1}: 확정 경로 좌표를 읽지 못했습니다.");
                    continue;
                }

                List<Vec2> points = new List<Vec2>(chain.Length / 3);
                bool invalid = false;
                for (int i = 0; i + 2 < chain.Length; i += 3)
                {
                    double x = chain[i];
                    double y = chain[i + 1];
                    double z = chain[i + 2];
                    if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z))
                    {
                        invalid = true;
                        break;
                    }

                    Vec2 point = project(new Vec3(x, y, z));
                    if (!IsFinite(point.X) || !IsFinite(point.Y))
                    {
                        invalid = true;
                        break;
                    }
                    points.Add(point);
                }

                if (invalid || points.Count < 2)
                {
                    failures.Add($"{label} 조각 {pieceIndex + 1}: 확정 경로 좌표를 읽지 못했습니다.");
                    continue;
                }

                if (!IsFinite(lengths[pieceIndex]) || lengths[pieceIndex] <= 0)
                {
                    failures.Add($"{label} 조각 {pieceIndex + 1}: 확정 길이가 올바르지 않습니다.");
                    continue;
                }

                runs.Add(new SpacingRunEntry
                {
                    AreaId = area.AreaId,
                    AreaName = label,
                    PieceIndex = pieceIndex,
                    Points = points,
                    ConfiguredGapMm = applied.OuterGapMm,
                    WireDiameterMm = applied.WireDiameterMm,
                    DirectionDeg = pattern.AngleDeg,
                    R = area.R,
                    G = area.G,
                    B = area.B,
                });
            }
        }

        return failures.Count == 0;
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
