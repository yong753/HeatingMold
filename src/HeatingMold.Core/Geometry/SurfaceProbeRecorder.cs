using System;
using System.Collections.Generic;

namespace HeatingMold.Core.Geometry;

/// <summary>
/// 표면 조회 delegate를 감싸 strict 시도 중 관측한 typed 결과(<see cref="SurfaceQueryOutcome.Miss"/>/
/// <see cref="SurfaceQueryOutcome.Failure"/>)를 값으로 기록한다.
/// </summary>
/// <remarks>
/// strict는 첫 Miss에서 중단하므로, strict 실패가 "단순 Miss"인지 아닌지를 문자열 검사 없이
/// 구분할 수 있다. 근사 재시도는 <b>하드 실패가 한 번도 관측되지 않았고 실제 Miss가 관측된</b>
/// 경우에만 허용한다. 그래야 커널 오류·오래된 컨텍스트·비유한 값·표본/깊이 한도 같은 실패를
/// 재조회로 지워버리지 않는다(일시적 Failure→Hit delegate도 재시도 대상이 아니다).
/// </remarks>
public sealed class SurfaceProbeRecorder
{
    private readonly Func<Vec2, SurfaceQueryResult> _query;
    private Func<Vec2, SurfaceMappedSample?>? _hitProbe;

    public SurfaceProbeRecorder(Func<Vec2, SurfaceQueryResult> query)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
    }

    /// <summary>지금까지 실행한 조회 횟수(진단/회귀용).</summary>
    public int QueryCount { get; private set; }

    /// <summary>단순 Miss가 한 번이라도 관측되었는가.</summary>
    public bool SawMiss { get; private set; }

    /// <summary>typed Failure(커널/오래된 컨텍스트/비유한 등)가 한 번이라도 관측되었는가.</summary>
    public bool SawFailure { get; private set; }

    /// <summary>처음 관측한 Failure 사유(진단용).</summary>
    public string FailureReason { get; private set; } = string.Empty;

    /// <summary>
    /// strict 실패 뒤 근사 재시도를 허용해도 되는가.
    /// 하드 실패가 하나라도 있거나 Miss가 전혀 없었으면(입력/override/정제/예산 한도 실패)
    /// 재시도하지 않는다.
    /// </summary>
    public bool AllowsApproximationRetry => !SawFailure && SawMiss;

    /// <summary>
    /// strict API(<see cref="SurfacePathMapper.TryMapRouteStrict"/>)에 넘길 nullable 어댑터.
    /// Miss와 Failure는 모두 null이 되지만 관측값은 이 recorder에 남는다.
    /// </summary>
    public Func<Vec2, SurfaceMappedSample?> HitProbe => _hitProbe ??= HitProbeCore;

    private SurfaceMappedSample? HitProbeCore(Vec2 uv)
    {
        SurfaceQueryResult result = Query(uv);
        return result.Outcome == SurfaceQueryOutcome.Hit ? result.Sample : null;
    }

    /// <summary>조회하면서 관측값을 기록한다. 근사 traversal도 이 함수를 쓴다.</summary>
    public SurfaceQueryResult Query(Vec2 uv)
    {
        QueryCount++;
        SurfaceQueryResult result = _query(uv);
        if (result.Outcome == SurfaceQueryOutcome.Failure)
        {
            SawFailure = true;
            if (FailureReason.Length == 0) FailureReason = result.Reason ?? string.Empty;
        }
        else if (result.Outcome == SurfaceQueryOutcome.Miss)
        {
            SawMiss = true;
        }
        return result;
    }

    /// <summary>재시도 가부를 설명하는 문구(문자열 판정에 쓰지 않고 오류 메시지에만 쓴다).</summary>
    public string Describe() => SawFailure
        ? "strict 시도 중 하드 오류 관측: " + (FailureReason.Length == 0 ? "사유 없음" : FailureReason)
        : SawMiss
            ? "strict 시도 중 단순 Miss만 관측"
            : "strict 시도 중 단순 Miss 미관측(입력/override/정제/예산 한도 실패)";
}
