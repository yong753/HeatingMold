using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace HeatingMold.App.VisualTesting;

/// <summary>
/// 앱 시작 시 확정된 시각 테스트 세션. 일반 실행에서는 Current가 null이다.
/// </summary>
internal sealed class VisualTestSession
{
    public static VisualTestSession Current { get; set; }

    public VisualTestOptions Options { get; private set; }
    public VisualScenario Scenario { get; private set; }
    public VisualTestReport Report { get; private set; }

    public static VisualTestSession Prepare(VisualTestOptions options)
    {
        if (options == null || !options.Enabled) return null;
        if (!string.IsNullOrEmpty(options.Error))
            throw new InvalidOperationException(options.Error);

        // 같은 실행 폴더를 재사용하면 이전 결과를 성공으로 오인할 수 있으므로 거부한다.
        if (Directory.Exists(options.RunDirectory))
        {
            if (File.Exists(Path.Combine(options.RunDirectory, "result.json")))
                throw new InvalidOperationException("실행 폴더에 이미 result.json이 있습니다. 새 실행 폴더를 지정하세요.");
            if (File.Exists(Path.Combine(options.RunDirectory, "result.dxf")))
                throw new InvalidOperationException("실행 폴더에 이미 result.dxf가 있습니다. 새 실행 폴더를 지정하세요.");
        }
        Directory.CreateDirectory(options.RunDirectory);

        VisualScenario scenario = VisualScenario.Load(options.ScenarioPath, options.RepositoryRoot, out string error);
        if (scenario == null) throw new InvalidOperationException(error);

        return new VisualTestSession
        {
            Options = options,
            Scenario = scenario,
            Report = new VisualTestReport(options.RunDirectory, scenario)
        };
    }
}

internal sealed class VisualTestRunResult
{
    public string Status { get; set; } = "Failed";
    public string Automation { get; set; } = "Failed";
    public string VisualReview { get; set; } = "NotRecorded";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public string LastCompletedStep { get; set; } = "";
    public string Error { get; set; } = "";
    public string DxfPath { get; set; } = "";
    /// <summary>이번 실행에서 실제로 생성에 성공한 DXF 파일 목록.</summary>
    public List<string> DxfFiles { get; set; } = new();
    public List<VisualStepRecord> Steps { get; set; } = new();
}

internal sealed class VisualStepRecord
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public double DurationMs { get; set; }
    public bool Success { get; set; }
    public string Detail { get; set; } = "";
    public string Error { get; set; } = "";
    public Dictionary<string, string> Data { get; set; } = new();
}

/// <summary>
/// 실행 폴더의 run.json / steps.jsonl / result.json을 관리한다.
/// 최종 결과는 완전히 작성한 뒤에만 원자적으로 게시한다.
/// </summary>
internal sealed class VisualTestReport
{
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly VisualScenario _scenario;
    private readonly List<VisualStepRecord> _steps = new();
    private readonly List<string> _dxfFiles = new();

    public string RunDirectory { get; }
    public string ResultPath => Path.Combine(RunDirectory, "result.json");
    public string DxfPath => Path.Combine(RunDirectory, "result.dxf");
    /// <summary>실제 생성된 DXF 중 마지막 파일. 없으면 빈 문자열.</summary>
    public string PrimaryDxfPath => _dxfFiles.Count > 0 ? _dxfFiles[^1] : "";
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    /// <summary>DXF 생성이 확인된 뒤에만 호출한다. 보고서에는 실제 존재하는 파일만 남는다.</summary>
    public void RegisterDxf(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        string full = Path.GetFullPath(path);
        if (!_dxfFiles.Any(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase)))
            _dxfFiles.Add(full);
    }

    public VisualTestReport(string runDirectory, VisualScenario scenario)
    {
        RunDirectory = runDirectory;
        _scenario = scenario;
    }

    public void WriteRun()
    {
        var run = new
        {
            startedAt = StartedAt,
            scenarioPath = _scenario.ScenarioPath,
            scenarioName = _scenario.Name,
            modelPath = _scenario.ResolvedModelPath,
            modelSha256 = _scenario.ActualModelSha256,
            faceCount = _scenario.Model.FaceCount,
            solidCount = _scenario.Model.SolidCount
        };
        AtomicWrite(Path.Combine(RunDirectory, "run.json"), JsonSerializer.Serialize(run, PrettyJson));
    }

    public void StartStep(int index, string name)
    {
        AppendStepLine(new
        {
            @event = "start",
            index,
            name,
            at = DateTimeOffset.Now
        });
    }

    public void EndStep(VisualStepRecord step)
    {
        _steps.Add(step);
        AppendStepLine(new
        {
            @event = "end",
            index = step.Index,
            name = step.Name,
            startedAt = step.StartedAt,
            endedAt = step.EndedAt,
            durationMs = step.DurationMs,
            success = step.Success,
            detail = step.Detail,
            error = step.Error,
            data = step.Data
        });
    }

    public void Finish(VisualTestRunResult result)
    {
        result.Steps = new List<VisualStepRecord>(_steps);
        result.DxfFiles = new List<string>(_dxfFiles);
        result.StartedAt = StartedAt;
        result.FinishedAt = DateTimeOffset.Now;
        if (File.Exists(ResultPath))
            throw new IOException("기존 result.json을 덮어쓰지 않습니다.");
        string json = JsonSerializer.Serialize(result, PrettyJson);
        string temp = ResultPath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, ResultPath);
    }

    public static void WriteStartupFailure(string runDirectory, string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(runDirectory))
                runDirectory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "visual-test", "startup-error");
            Directory.CreateDirectory(runDirectory);
            string path = Path.Combine(runDirectory, "result.json");
            if (File.Exists(path))
            {
                File.WriteAllText(Path.Combine(runDirectory, "startup-error.txt"), message ?? "");
                return;
            }
            var now = DateTimeOffset.Now;
            var result = new VisualTestRunResult
            {
                Status = "Failed",
                Automation = "Failed",
                StartedAt = now,
                FinishedAt = now,
                Error = message ?? "시각 테스트 시작 실패"
            };
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(result, PrettyJson));
            File.Move(temp, path);
        }
        catch (Exception)
        {
            // 시작 실패 기록 자체가 실패하면 조용히 종료한다. 앱 종료 코드가 0이 아니므로
            // 호출 스크립트는 실패를 구분할 수 있다.
        }
    }

    private void AppendStepLine(object value)
    {
        string path = Path.Combine(RunDirectory, "steps.jsonl");
        File.AppendAllText(path, JsonSerializer.Serialize(value, CompactJson) + Environment.NewLine);
    }

    private static void AtomicWrite(string path, string text)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, text);
        if (File.Exists(path)) File.Delete(temp);
        else File.Move(temp, path);
    }
}
