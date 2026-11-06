using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace HeatingMold.App.VisualTesting;

internal enum VisualTestRunnerState
{
    Idle,
    Running,
    Observing,
    WaitingForNext,
    Paused,
    Completed,
    Failed,
    Stopped
}

internal sealed record VisualTestRunnerProgress(
    VisualTestRunnerState State,
    int StepIndex,
    string StepName,
    string Detail,
    int CompletedSteps,
    int StepCount,
    bool CanNext);

internal sealed class VisualStepOutcome
{
    public bool Success { get; private set; }
    public string Detail { get; private set; } = "";
    public string Error { get; private set; } = "";
    public Dictionary<string, string> Data { get; } = new();

    public static VisualStepOutcome Ok(string detail, Dictionary<string, string> data = null)
    {
        var outcome = new VisualStepOutcome { Success = true, Detail = detail ?? "" };
        if (data != null)
            foreach (var pair in data) outcome.Data[pair.Key] = pair.Value;
        return outcome;
    }

    public static VisualStepOutcome Fail(string error, Dictionary<string, string> data = null)
    {
        var outcome = new VisualStepOutcome { Success = false, Error = error ?? "알 수 없는 단계 실패" };
        if (data != null)
            foreach (var pair in data) outcome.Data[pair.Key] = pair.Value;
        return outcome;
    }
}

/// <summary>
/// 단계 실행 → 화면 관찰 대기 → 다음 단계의 순차 실행기.
/// 실제 기능 호출은 호스트가 담당하고, 이 클래스는 순서/일시정지/한단계/중단과 기록만 맡는다.
/// </summary>
internal sealed class VisualTestRunner
{
    private readonly IReadOnlyList<string> _stepNames;
    private readonly Func<int, CancellationToken, Task<VisualStepOutcome>> _executeStep;
    private readonly VisualTestReport _report;
    private readonly double _holdSeconds;
    private readonly bool _stepMode;
    private readonly Func<string> _takeReportedError;

    private CancellationTokenSource _cts;
    private Task _loop;
    private bool _finished;
    private bool _paused;
    private bool _nextRequested;
    private bool _stopRequested;
    private int _currentIndex = -1;
    private string _currentName = "";
    private string _detail = "";
    private int _completedSteps;
    private string _lastCompletedStep = "";
    private VisualTestRunnerState _state = VisualTestRunnerState.Idle;

    public VisualTestRunner(
        IReadOnlyList<string> stepNames,
        Func<int, CancellationToken, Task<VisualStepOutcome>> executeStep,
        VisualTestReport report,
        double holdSeconds,
        bool stepMode,
        Func<string> takeReportedError)
    {
        _stepNames = stepNames ?? throw new ArgumentNullException(nameof(stepNames));
        _executeStep = executeStep ?? throw new ArgumentNullException(nameof(executeStep));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _holdSeconds = Math.Max(0, holdSeconds);
        _stepMode = stepMode;
        _takeReportedError = takeReportedError;
    }

    public event Action<VisualTestRunnerProgress> ProgressChanged;
    public event Action<VisualTestRunResult> Finished;

    public bool IsPaused => _paused;
    public bool IsFinished => _finished;
    public VisualTestRunnerState State => _state;
    public Task Completion => _loop;

    public void Start()
    {
        if (_loop != null || _finished) return;
        _cts = new CancellationTokenSource();
        _state = VisualTestRunnerState.Running;
        _loop = RunAsync(_cts.Token);
    }

    public void Pause()
    {
        if (_finished || _paused) return;
        _paused = true;
        Notify("일시정지 — 현재 단계가 끝난 뒤 대기합니다");
    }

    public void Resume()
    {
        if (_finished || !_paused) return;
        _paused = false;
        Notify("계속 실행");
    }

    public void RequestNext()
    {
        if (_finished || _paused || _state != VisualTestRunnerState.WaitingForNext) return;
        _nextRequested = true;
        Notify("다음 단계로 진행");
    }

    public void Stop()
    {
        if (_finished) return;
        _stopRequested = true;
        try { _cts?.Cancel(); } catch (Exception) { }
        Notify("중단 요청 — 현재 호출이 끝나는 대로 멈춥니다");
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            for (int i = 0; i < _stepNames.Count; i++)
            {
                await WaitWhilePausedAsync(token);
                if (_stopRequested) { Finish("Stopped", "사용자가 중단했습니다"); return; }

                string pending = _takeReportedError?.Invoke();
                if (!string.IsNullOrEmpty(pending))
                {
                    Finish("Failed", "실행 중 오류 알림: " + pending);
                    return;
                }

                _currentIndex = i;
                _currentName = _stepNames[i];
                _detail = "단계 실행 중";
                SetState(VisualTestRunnerState.Running, _detail);

                _report.StartStep(i, _currentName);
                var started = DateTimeOffset.Now;
                var watch = Stopwatch.StartNew();
                VisualStepOutcome outcome;
                try
                {
                    outcome = await _executeStep(i, token) ?? VisualStepOutcome.Fail("단계가 결과를 반환하지 않았습니다.");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    outcome = VisualStepOutcome.Fail(ex.ToString());
                }
                watch.Stop();

                var record = new VisualStepRecord
                {
                    Index = i,
                    Name = _currentName,
                    StartedAt = started,
                    EndedAt = DateTimeOffset.Now,
                    DurationMs = watch.Elapsed.TotalMilliseconds,
                    Success = outcome.Success,
                    Detail = outcome.Detail,
                    Error = outcome.Error
                };
                foreach (var pair in outcome.Data) record.Data[pair.Key] = pair.Value;
                _report.EndStep(record);

                if (!outcome.Success)
                {
                    _detail = outcome.Error;
                    Finish("Failed", string.Format("단계 {0} 실패: {1}", i + 1, outcome.Error));
                    return;
                }

                _completedSteps = i + 1;
                _lastCompletedStep = _currentName;
                _detail = outcome.Detail;

                pending = _takeReportedError?.Invoke();
                if (!string.IsNullOrEmpty(pending))
                {
                    Finish("Failed", "단계 완료 후 오류 알림: " + pending);
                    return;
                }

                SetState(VisualTestRunnerState.Observing,
                    string.IsNullOrEmpty(outcome.Detail) ? "결과를 화면에서 확인하세요" : outcome.Detail);
                if (_stepMode)
                {
                    await WaitForNextAsync(token);
                    if (_stopRequested) { Finish("Stopped", "사용자가 중단했습니다"); return; }
                }
                else
                {
                    await HoldAsync(token);
                    if (_stopRequested) { Finish("Stopped", "사용자가 중단했습니다"); return; }
                }
            }

            Finish("Completed", "");
        }
        catch (OperationCanceledException)
        {
            Finish("Stopped", "사용자가 중단했습니다");
        }
        catch (Exception ex)
        {
            Finish("Failed", ex.ToString());
        }
    }

    private async Task WaitWhilePausedAsync(CancellationToken token)
    {
        while (_paused && !_stopRequested)
            await Task.Delay(100, token);
    }

    private async Task HoldAsync(CancellationToken token)
    {
        double remainingMs = _holdSeconds * 1000.0;
        while (remainingMs > 0 && !_stopRequested)
        {
            await Task.Delay(100, token);
            if (!_paused) remainingMs -= 100;
        }
    }

    private async Task WaitForNextAsync(CancellationToken token)
    {
        _nextRequested = false;
        SetState(VisualTestRunnerState.WaitingForNext, "화면을 확인한 뒤 [다음 단계]를 누르세요");
        while (!_nextRequested && !_stopRequested)
            await Task.Delay(50, token);
        _nextRequested = false;
    }

    private void Finish(string status, string error)
    {
        if (_finished) return;
        _finished = true;
        VisualTestRunnerState terminal = status == "Completed"
            ? VisualTestRunnerState.Completed
            : status == "Stopped" ? VisualTestRunnerState.Stopped : VisualTestRunnerState.Failed;
        var result = new VisualTestRunResult
        {
            Status = status,
            Automation = status,
            VisualReview = "NotRecorded",
            LastCompletedStep = _lastCompletedStep,
            Error = error ?? "",
            DxfPath = _report.PrimaryDxfPath
        };
        try
        {
            _report.Finish(result);
        }
        catch (Exception ex)
        {
            result.Status = "Failed";
            result.Automation = "Failed";
            result.Error = "결과 기록 실패: " + ex.Message;
            terminal = VisualTestRunnerState.Failed;
        }
        _state = terminal;
        _detail = result.Error;
        Notify(string.IsNullOrEmpty(result.Error) ? "자동 절차가 끝났습니다" : result.Error);
        try { Finished?.Invoke(result); } catch (Exception) { }
    }

    private void SetState(VisualTestRunnerState state, string detail)
    {
        _state = state;
        _detail = detail ?? "";
        Notify(_detail);
    }

    private void Notify(string detail)
    {
        _detail = detail ?? _detail;
        try
        {
            ProgressChanged?.Invoke(new VisualTestRunnerProgress(
                _state, _currentIndex, _currentName, _detail,
                _completedSteps, _stepNames.Count,
                _state == VisualTestRunnerState.WaitingForNext));
        }
        catch (Exception) { }
    }
}
