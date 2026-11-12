using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App.VisualTesting;

/// <summary>
/// 한 영역의 확정 경로 상태를 값으로 복사한 스냅샷. 원본 목록/객체 참조를 보관하지 않으므로
/// 이후 제품 코드가 목록을 교체하거나 비워도 캡처 시점의 값이 유지된다.
/// 표시 전용·파생 값(활성 영역, 선택 강조, 배분 전력/전압/전류, 초안 텍스트)은 포함하지 않는다.
/// </summary>
internal sealed record VisualRouteSnapshot(
    int AreaId,
    string Name,
    int ColorArgb,
    int[] FaceIds,
    Vec2[][] Region,
    VisualExclusionSnapshot[] Exclusions,
    int Direction,
    double? AppliedMarginMm,
    double? AppliedSpacingMm,
    int? AppliedDirection,
    string AppliedRouteContextKey,
    int CircuitCount,
    int[][] CircuitPieceIndices,
    VisualRoutePieceSnapshot[] Pieces,
    VisualExitSnapshot? StartExit,
    VisualExitSnapshot? EndExit,
    Vec2[] ExitChain,
    Vec3[] ExitChain3D,
    Vec2[][] SplitCutLines,
    bool PatternDisplayed,
    bool RouteEdited);

internal sealed record VisualExclusionSnapshot(double MinX, double MinY, double MaxX, double MaxY);

internal sealed record VisualRoutePieceSnapshot(
    int Index,
    SerpentineRoutePieceKind Kind,
    int LaneIndex,
    double PlaneLengthMm,
    int NativePieceId,
    double ActualLengthMm,
    Vec2[] PlanePoints,
    Vec3[] SurfacePoints,
    int[] SupportFaceIds);

/// <summary>인출점 provenance. Kind: 0=없음, 1=원본 CAD edge, 2=제외영역 변, 3=분할 절단선.</summary>
internal sealed record VisualBindingSnapshot(
    int Kind,
    long ModelRevision,
    long ContextId,
    long ContextRevision,
    long EdgeKey,
    double FirstParameter,
    double LastParameter,
    double Parameter,
    bool Reversed,
    double MinX,
    double MinY,
    double MaxX,
    double MaxY,
    int Side,
    double AX,
    double AY,
    double BX,
    double BY);

internal sealed record VisualExitSnapshot(
    int Role,
    Vec2 Point,
    int SegmentIndex,
    double SegmentParameter,
    VisualBindingSnapshot? Binding);

/// <summary>두 스냅샷의 차이를 영역/조각/필드 단위로 보고한다. 총길이 비교로 대체하지 않는다.</summary>
internal static class VisualRouteSnapshotComparer
{
    public static VisualRouteSnapshot Capture(WorkingArea area, WorkingSideDefinition? side, out string error)
    {
        error = string.Empty;
        if (area == null)
        {
            error = "스냅샷을 만들 영역이 없습니다";
            return null;
        }

        SerpentineRoutePiece[] pieces = area.Pattern?.RoutePieces?.ToArray()
            ?? Array.Empty<SerpentineRoutePiece>();
        int[] nativeIds = area.NativePieceIds ?? Array.Empty<int>();
        if (pieces.Length != nativeIds.Length || pieces.Length != area.RoutePieceLengths.Length)
        {
            error = string.Format(CultureInfo.InvariantCulture,
                "{0}: 경로 조각 참조가 일치하지 않습니다 (조각 {1}, native ID {2}, 길이 {3})",
                area.Name, pieces.Length, nativeIds.Length, area.RoutePieceLengths.Length);
            return null;
        }

        Vec3[][] surfacePoints = new Vec3[pieces.Length][];
        int[][] supportFaces = new int[pieces.Length][];
        if (pieces.Length > 0)
        {
            if (side == null)
            {
                error = string.Format(CultureInfo.InvariantCulture,
                    "{0}: 확정 작업 컨텍스트가 없어 3D 경로를 읽을 수 없습니다", area.Name);
                return null;
            }
            CommittedRouteChain[] chains;
            try
            {
                chains = Bridge.GetCommittedRouteChainsDetailed(
                    side.Id.Value, side.Revision.Value, nativeIds);
            }
            catch (Exception ex)
            {
                error = string.Format(CultureInfo.InvariantCulture,
                    "{0}: 확정 3D 경로를 읽지 못했습니다 — {1}", area.Name, ex.Message);
                return null;
            }
            if (chains == null || chains.Length != pieces.Length)
            {
                error = string.Format(CultureInfo.InvariantCulture,
                    "{0}: 확정 3D 경로 개수가 조각 수와 다릅니다 ({1}/{2})",
                    area.Name, chains?.Length ?? 0, pieces.Length);
                return null;
            }
            for (int i = 0; i < pieces.Length; i++)
            {
                CommittedRouteChain chain = chains[i];
                double[] flat = chain?.Points;
                if (flat == null || flat.Length < 6 || flat.Length % 3 != 0)
                {
                    error = string.Format(CultureInfo.InvariantCulture,
                        "{0}: 조각 {1}의 확정 3D 좌표가 올바르지 않습니다", area.Name, i);
                    return null;
                }
                var points = new Vec3[flat.Length / 3];
                for (int p = 0; p < points.Length; p++)
                {
                    points[p] = new Vec3(flat[p * 3], flat[p * 3 + 1], flat[p * 3 + 2]);
                    if (!IsFinite(points[p]))
                    {
                        error = string.Format(CultureInfo.InvariantCulture,
                            "{0}: 조각 {1}의 확정 3D 좌표가 유한하지 않습니다", area.Name, i);
                        return null;
                    }
                }
                surfacePoints[i] = points;
                supportFaces[i] = chain.SupportFaceIds?.ToArray() ?? Array.Empty<int>();
            }
        }

        var pieceSnapshots = new VisualRoutePieceSnapshot[pieces.Length];
        for (int i = 0; i < pieces.Length; i++)
        {
            SerpentineRoutePiece piece = pieces[i];
            pieceSnapshots[i] = new VisualRoutePieceSnapshot(
                i,
                piece.Kind,
                piece.LaneIndex,
                piece.Length,
                nativeIds[i],
                area.RoutePieceLengths[i],
                piece.Points?.ToArray() ?? Array.Empty<Vec2>(),
                surfacePoints[i],
                supportFaces[i]);
        }

        var region = new Vec2[area.Region.Count][];
        for (int r = 0; r < region.Length; r++)
            region[r] = area.Region[r]?.ToArray() ?? Array.Empty<Vec2>();

        var exclusions = new VisualExclusionSnapshot[area.Exclusions.Count];
        for (int e = 0; e < exclusions.Length; e++)
        {
            var rect = area.Exclusions[e];
            exclusions[e] = new VisualExclusionSnapshot(rect.MinX, rect.MinY, rect.MaxX, rect.MaxY);
        }

        var cutLines = new Vec2[area.SplitCutLines.Count][];
        for (int c = 0; c < cutLines.Length; c++)
            cutLines[c] = new[] { area.SplitCutLines[c].A, area.SplitCutLines[c].B };

        var indices = new int[area.CircuitPieceIndices.Count][];
        for (int i = 0; i < indices.Length; i++)
            indices[i] = area.CircuitPieceIndices[i]?.ToArray() ?? Array.Empty<int>();

        return new VisualRouteSnapshot(
            area.AreaId,
            area.Name,
            (area.Color.A << 24) | (area.Color.R << 16) | (area.Color.G << 8) | area.Color.B,
            area.FaceIdSet.OrderBy(id => id).ToArray(),
            region,
            exclusions,
            (int)area.Direction,
            area.AppliedInputs?.Margin,
            area.AppliedInputs?.Spacing,
            area.AppliedInputs is null ? null : (int)area.AppliedInputs.Direction,
            area.AppliedInputs?.RouteContextKey ?? string.Empty,
            area.Circuits.Count,
            indices,
            pieceSnapshots,
            CaptureExit(area.SelectedStartExit, CircuitExitRole.Start),
            CaptureExit(area.SelectedEndExit, CircuitExitRole.End),
            area.ExitChain?.ToArray() ?? Array.Empty<Vec2>(),
            area.ExitChain3D?.ToArray() ?? Array.Empty<Vec3>(),
            cutLines,
            area.PatternDisplayed,
            area.RouteEdited);
    }

    private static VisualExitSnapshot? CaptureExit(ExitBoundaryPoint? point, CircuitExitRole role)
    {
        if (point is not { } p) return null;
        VisualBindingSnapshot? binding = null;
        if (p.Binding is { } cad)
            binding = new VisualBindingSnapshot(1, cad.ModelRevision, cad.WorkingContextId,
                cad.WorkingContextRevision, cad.EdgeKey, cad.FirstParameter, cad.LastParameter,
                cad.Parameter, cad.Reversed, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        else if (p.ArtificialBinding is { } art)
            binding = new VisualBindingSnapshot(2, 0, art.WorkingContextId, art.WorkingContextRevision,
                0, art.FirstParameter, art.LastParameter, art.Parameter, false,
                art.MinX, art.MinY, art.MaxX, art.MaxY, art.Side, 0, 0, 0, 0);
        else if (p.SplitBinding is { } split)
            binding = new VisualBindingSnapshot(3, 0, split.WorkingContextId, split.WorkingContextRevision,
                0, split.FirstParameter, split.LastParameter, split.Parameter, false,
                0, 0, 0, 0, 0, split.AX, split.AY, split.BX, split.BY);
        return new VisualExitSnapshot((int)role, p.Point, p.SegmentIndex, p.SegmentParameter, binding);
    }

    private static bool IsFinite(Vec3 p) =>
        double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);

    private static bool IsFinite(Vec2 p) => double.IsFinite(p.X) && double.IsFinite(p.Y);

    /// <summary>
    /// 캡처 시점과 현재 상태를 비교한다. 다르면 첫 차이를 사람이 읽을 수 있는 문장으로 돌려준다.
    /// 표시·파생 값은 비교하지 않으므로 다른 영역의 경로 변경으로 배분 전력이 바뀌어도 실패가 아니다.
    /// </summary>
    public static bool IsUnchanged(VisualRouteSnapshot before, VisualRouteSnapshot after, out string difference)
    {
        difference = string.Empty;
        if (before is null || after is null)
        {
            difference = "비교할 스냅샷이 없습니다";
            return false;
        }
        string area = before.Name;
        if (before.AreaId != after.AreaId) { difference = Diff(area, "영역 ID", before.AreaId, after.AreaId); return false; }
        if (!string.Equals(before.Name, after.Name, StringComparison.Ordinal)) { difference = Diff(area, "영역 이름", before.Name, after.Name); return false; }
        if (before.ColorArgb != after.ColorArgb) { difference = Diff(area, "영역 색", before.ColorArgb, after.ColorArgb); return false; }
        if (!SameInts(before.FaceIds, after.FaceIds)) { difference = string.Format(CultureInfo.InvariantCulture, "{0}: 선택 면 집합이 달라졌습니다", area); return false; }
        if (!SameRings(before.Region, after.Region)) { difference = string.Format(CultureInfo.InvariantCulture, "{0}: 유효 경계 외곽이 달라졌습니다", area); return false; }
        if (!SameExclusions(before.Exclusions, after.Exclusions)) { difference = string.Format(CultureInfo.InvariantCulture, "{0}: 제외영역이 달라졌습니다", area); return false; }
        if (before.Direction != after.Direction) { difference = Diff(area, "생성 방향", before.Direction, after.Direction); return false; }
        if (!NullableDouble(before.AppliedMarginMm, after.AppliedMarginMm)) { difference = Diff(area, "적용 마진", before.AppliedMarginMm, after.AppliedMarginMm); return false; }
        if (!NullableDouble(before.AppliedSpacingMm, after.AppliedSpacingMm)) { difference = Diff(area, "적용 간격", before.AppliedSpacingMm, after.AppliedSpacingMm); return false; }
        if (before.AppliedDirection != after.AppliedDirection) { difference = Diff(area, "적용 방향", before.AppliedDirection, after.AppliedDirection); return false; }
        if (!string.Equals(before.AppliedRouteContextKey, after.AppliedRouteContextKey, StringComparison.Ordinal))
        {
            difference = string.Format(CultureInfo.InvariantCulture, "{0}: 적용 경로 조건 키가 달라졌습니다", area);
            return false;
        }
        if (before.PatternDisplayed != after.PatternDisplayed) { difference = Diff(area, "경로 표시 상태", before.PatternDisplayed, after.PatternDisplayed); return false; }
        if (before.RouteEdited != after.RouteEdited) { difference = Diff(area, "수동 편집 표시", before.RouteEdited, after.RouteEdited); return false; }
        if (before.CircuitCount != after.CircuitCount) { difference = Diff(area, "회로 수", before.CircuitCount, after.CircuitCount); return false; }
        if (!SameIntArrays(before.CircuitPieceIndices, after.CircuitPieceIndices))
        {
            difference = string.Format(CultureInfo.InvariantCulture, "{0}: 회로별 조각 구성이 달라졌습니다", area);
            return false;
        }
        if (before.Pieces.Length != after.Pieces.Length)
        {
            difference = Diff(area, "경로 조각 수", before.Pieces.Length, after.Pieces.Length);
            return false;
        }
        for (int i = 0; i < before.Pieces.Length; i++)
        {
            VisualRoutePieceSnapshot a = before.Pieces[i];
            VisualRoutePieceSnapshot b = after.Pieces[i];
            if (a.Kind != b.Kind) { difference = Diff(area + " 조각 " + i, "종류", a.Kind, b.Kind); return false; }
            if (a.LaneIndex != b.LaneIndex) { difference = Diff(area + " 조각 " + i, "레인", a.LaneIndex, b.LaneIndex); return false; }
            if (!StrictDouble(a.PlaneLengthMm, b.PlaneLengthMm)) { difference = Diff(area + " 조각 " + i, "2D 길이", a.PlaneLengthMm, b.PlaneLengthMm); return false; }
            if (a.NativePieceId != b.NativePieceId) { difference = Diff(area + " 조각 " + i, "native 조각 ID", a.NativePieceId, b.NativePieceId); return false; }
            if (!StrictDouble(a.ActualLengthMm, b.ActualLengthMm)) { difference = Diff(area + " 조각 " + i, "확정 3D 길이", a.ActualLengthMm, b.ActualLengthMm); return false; }
            if (!SamePoints2D(a.PlanePoints, b.PlanePoints)) { difference = string.Format(CultureInfo.InvariantCulture, "{0} 조각 {1}: 2D 좌표가 달라졌습니다", area, i); return false; }
            if (!SamePoints3D(a.SurfacePoints, b.SurfacePoints)) { difference = string.Format(CultureInfo.InvariantCulture, "{0} 조각 {1}: 확정 3D 좌표가 달라졌습니다", area, i); return false; }
            if (!SameInts(a.SupportFaceIds, b.SupportFaceIds)) { difference = string.Format(CultureInfo.InvariantCulture, "{0} 조각 {1}: 지지 면이 달라졌습니다", area, i); return false; }
        }
        if (!SameExit(before.StartExit, after.StartExit, area, "시작")) { difference = Diff(area, "시작 인출점", Describe(before.StartExit), Describe(after.StartExit)); return false; }
        if (!SameExit(before.EndExit, after.EndExit, area, "종료")) { difference = Diff(area, "종료 인출점", Describe(before.EndExit), Describe(after.EndExit)); return false; }
        if (!SamePoints2D(before.ExitChain, after.ExitChain)) { difference = string.Format(CultureInfo.InvariantCulture, "{0}: 인출 경계 폴리라인이 달라졌습니다", area); return false; }
        if (!SamePoints3D(before.ExitChain3D, after.ExitChain3D)) { difference = string.Format(CultureInfo.InvariantCulture, "{0}: 인출 경계 3D 좌표가 달라졌습니다", area); return false; }
        if (!SameCutLines(before.SplitCutLines, after.SplitCutLines)) { difference = string.Format(CultureInfo.InvariantCulture, "{0}: 분할 절단선이 달라졌습니다", area); return false; }
        return true;
    }

    private static string Diff(string area, string field, object before, object after) =>
        string.Format(CultureInfo.InvariantCulture, "{0}: {1}이(가) 달라졌습니다 ({2} → {3})",
            area, field, before ?? "없음", after ?? "없음");

    private static string Describe(VisualExitSnapshot? exit) => exit is null
        ? null
        : string.Format(CultureInfo.InvariantCulture, "({0:F6}, {1:F6})", exit.Point.X, exit.Point.Y);

    private static bool StrictDouble(double a, double b) => a.Equals(b);

    private static bool NullableDouble(double? a, double? b) => a.HasValue == b.HasValue && (!a.HasValue || a.Value.Equals(b!.Value));

    private static bool SameInts(int[] a, int[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static bool SameIntArrays(int[][] a, int[][] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (!SameInts(a[i], b[i])) return false;
        return true;
    }

    private static bool SamePoints2D(Vec2[] a, Vec2[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (!a[i].Equals(b[i]) || !IsFinite(a[i])) return false;
        return true;
    }

    private static bool SamePoints2D(Vec2[][] a, Vec2[][] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (!SamePoints2D(a[i], b[i])) return false;
        return true;
    }

    private static bool SamePoints3D(Vec3[] a, Vec3[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (!a[i].Equals(b[i]) || !IsFinite(a[i])) return false;
        return true;
    }

    private static bool SameRings(Vec2[][] a, Vec2[][] b) => SamePoints2D(a, b);

    private static bool SameExclusions(VisualExclusionSnapshot[] a, VisualExclusionSnapshot[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (!a[i].Equals(b[i])) return false;
        return true;
    }

    private static bool SameCutLines(Vec2[][] a, Vec2[][] b) => SamePoints2D(a, b);

    private static bool SameExit(VisualExitSnapshot? a, VisualExitSnapshot? b, string area, string role)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (a.Role != b.Role) return false;
        if (!a.Point.Equals(b.Point)) return false;
        if (a.SegmentIndex != b.SegmentIndex) return false;
        if (!a.SegmentParameter.Equals(b.SegmentParameter)) return false;
        if (a.Binding is null || b.Binding is null) return a.Binding is null && b.Binding is null;
        return a.Binding.Equals(b.Binding);
    }
}
