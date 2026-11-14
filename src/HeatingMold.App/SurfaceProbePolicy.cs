using System.Collections.Generic;
using HeatingMold.Core.Geometry;

namespace HeatingMold.App;

/// <summary>표면 조회 후보 한 단계의 판정.</summary>
internal enum SurfaceCandidateState
{
    /// <summary>유효한 3D 점을 얻었다.</summary>
    Hit = 0,

    /// <summary>표면이 없다(선택 면/전역 표면 Miss, 경계 NoIntersection). 근사 허용 후보.</summary>
    Miss = 1,

    /// <summary>커널 오류·오래된 컨텍스트·모호/불명확 상태. 근사로 덮지 않는다.</summary>
    Hard = 2,
}

/// <summary>후보 단계 하나의 typed 결과. Hit일 때만 표본을 담는다.</summary>
internal readonly record struct SurfaceProbeOutcome(
    SurfaceCandidateState State, SurfaceMappedSample Sample, string Reason)
{
    /// <summary>
    /// 유효한 Hit 표본만 <see cref="SurfaceCandidateState.Hit"/>로 만든다.
    /// 비유한 좌표, 지지 Face 없음, 요청점과 다른 데이텀 같은 <b>malformed native 데이터</b>는
    /// <see cref="SurfaceCandidateState.Hard"/>로 분류해 다음 후보(전역 표면 → 정확 경계)를 계속
    /// 시도하게 한다. 이 factory가 선택 면/전역 표면/정확 경계 세 단계의 단일 검증 권위다.
    /// </summary>
    internal static SurfaceProbeOutcome FromHit(SurfaceMappedSample sample) =>
        TryValidateHit(sample, null, out string reason)
            ? new(SurfaceCandidateState.Hit, sample, string.Empty)
            : Hard(reason);

    /// <summary>요청한 작업평면 좌표까지 함께 검증하는 overload(경계 잔차 포함).</summary>
    internal static SurfaceProbeOutcome FromHit(SurfaceMappedSample sample, Vec2 expectedDatum) =>
        TryValidateHit(sample, expectedDatum, out string reason)
            ? new(SurfaceCandidateState.Hit, sample, string.Empty)
            : Hard(reason);

    internal static SurfaceProbeOutcome Miss(string reason) =>
        new(SurfaceCandidateState.Miss, default, reason ?? string.Empty);

    internal static SurfaceProbeOutcome Hard(string reason) =>
        new(SurfaceCandidateState.Hard, default, reason ?? string.Empty);

    private static bool TryValidateHit(SurfaceMappedSample sample, Vec2? expectedDatum, out string reason)
    {
        reason = string.Empty;
        if (!double.IsFinite(sample.DatumPoint.X) || !double.IsFinite(sample.DatumPoint.Y))
        {
            reason = "Hit 데이텀 좌표가 유한하지 않습니다";
            return false;
        }
        if (!double.IsFinite(sample.Point.X) || !double.IsFinite(sample.Point.Y) ||
            !double.IsFinite(sample.Point.Z))
        {
            reason = "Hit 3D 좌표가 유한하지 않습니다";
            return false;
        }
        if (sample.FaceId < 0)
        {
            reason = "Hit에 유효한 지지 Face가 없습니다";
            return false;
        }
        if (expectedDatum.HasValue &&
            sample.DatumPoint.DistanceTo(expectedDatum.Value) > GeometryTolerancePolicy.DistanceTolerance)
        {
            reason = "Hit 데이텀이 요청한 작업평면 좌표와 다릅니다";
            return false;
        }
        return true;
    }
}

/// <summary>
/// 선택 면 → 전역 표면 → 정확 경계 순서의 성공 우선순위를 보존한 typed 판정.
///
/// <para>과거 nullable 규칙은 "세 단계 중 유효한 Hit가 하나라도 있으면 그 점으로 성공"이었다.
/// 따라서 앞 단계의 하드 상태(예: 선택 면 조회의 GeometryFailure)가 뒤 단계의 Hit를 막으면 안 된다.
/// Hit가 전혀 없을 때만 하드 상태를 Failure로 판정하고, 순수 Miss(선택 면 Miss·전역 표면 Miss·
/// 경계 NoIntersection)만 근사 허용 Miss로 남긴다.</para>
///
/// <para>불명확한 비-Miss(결과 객체 없음, Hit인데 3D 점 없음, Tangent인데 점 없음)와 malformed
/// Hit(비유한 좌표·지지 Face 없음·데이텀 불일치)는 단순 Miss로 취급하지 않는다. malformed는
/// <see cref="SurfaceProbeOutcome.FromHit(SurfaceMappedSample)"/>가 Hard로 분류해 다음 후보를
/// 계속 시도하고, 유효한 후보가 하나도 없을 때만 Failure가 된다(근사 허용 Miss로 낮추지 않는다).</para>
/// </summary>
internal static class SurfaceProbePolicy
{
    internal static SurfaceQueryResult Resolve(SurfaceProbeOutcome selected, SurfaceProbeOutcome global,
        SurfaceProbeOutcome boundary)
    {
        if (selected.State == SurfaceCandidateState.Hit) return SurfaceQueryResult.FromHit(selected.Sample);
        if (global.State == SurfaceCandidateState.Hit) return SurfaceQueryResult.FromHit(global.Sample);
        if (boundary.State == SurfaceCandidateState.Hit) return SurfaceQueryResult.FromHit(boundary.Sample);

        var hard = new List<string>(3);
        if (selected.State == SurfaceCandidateState.Hard) hard.Add("선택 면 " + selected.Reason);
        if (global.State == SurfaceCandidateState.Hard) hard.Add("전역 표면 " + global.Reason);
        if (boundary.State == SurfaceCandidateState.Hard) hard.Add("경계 " + boundary.Reason);
        if (hard.Count > 0) return SurfaceQueryResult.FromFailure(string.Join(" · ", hard));

        return SurfaceQueryResult.FromMiss("선택 면·전역 표면·경계 모두 Miss");
    }

    /// <summary>아직 실행하지 않은 단계 자리표시자(성공도 하드도 아님).</summary>
    internal static SurfaceProbeOutcome NotRun { get; } = SurfaceProbeOutcome.Miss("미실행");

    /// <summary>진단 문자열용 요약. 판정에는 쓰지 않는다.</summary>
    internal static string Describe(string label, SurfaceProbeOutcome outcome) =>
        label + "=" + outcome.State + (outcome.Reason.Length == 0 ? string.Empty : "(" + outcome.Reason + ")");
}
