using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace HeatingMold.App.VisualTesting;

/// <summary>
/// 시각 테스트 실행 인자. 일반 앱 실행에서는 Enabled=false라 기존 동작이 바뀌지 않는다.
/// 경로는 앱 시작 시 한 번만 확정하고, 실행 중에는 다시 해석하지 않는다.
/// </summary>
internal sealed class VisualTestOptions
{
    public bool Enabled { get; private set; }
    public string ScenarioPath { get; private set; } = "";
    public string RunDirectory { get; private set; } = "";
    public double HoldSeconds { get; private set; } = 3;
    public bool StepMode { get; private set; }
    public string RepositoryRoot { get; private set; } = "";
    public string Error { get; private set; }

    public static VisualTestOptions Parse(string[] args)
    {
        var options = new VisualTestOptions
        {
            RepositoryRoot = FindRepositoryRoot() ?? Directory.GetCurrentDirectory()
        };
        options.RunDirectory = Path.Combine(options.RepositoryRoot, "artifacts", "visual-test",
            DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        if (args == null || !args.Any(a => string.Equals(a, "--visual-test", StringComparison.OrdinalIgnoreCase)))
            return options;

        options.Enabled = true;
        try
        {
            string scenario = null;
            string runDirectory = null;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i] ?? "";
                if (string.Equals(arg, "--visual-test", StringComparison.OrdinalIgnoreCase))
                {
                    RequireOnce(seen, arg);
                    scenario = RequireValue(args, ref i, arg);
                }
                else if (string.Equals(arg, "--visual-run-dir", StringComparison.OrdinalIgnoreCase))
                {
                    RequireOnce(seen, arg);
                    runDirectory = RequireValue(args, ref i, arg);
                }
                else if (string.Equals(arg, "--visual-hold-seconds", StringComparison.OrdinalIgnoreCase))
                {
                    RequireOnce(seen, arg);
                    string text = RequireValue(args, ref i, arg);
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                        || !double.IsFinite(seconds) || seconds < 0 || seconds > 60)
                        throw new ArgumentException("--visual-hold-seconds는 0~60 사이의 숫자여야 합니다.");
                    options.HoldSeconds = seconds;
                }
                else if (string.Equals(arg, "--visual-step", StringComparison.OrdinalIgnoreCase))
                {
                    RequireOnce(seen, arg);
                    options.StepMode = true;
                }
            }

            // 실패 기록도 사용자가 지정한 실행 폴더에 남도록 시나리오 검증보다 먼저 확정한다.
            if (!string.IsNullOrWhiteSpace(runDirectory))
            {
                if (!Path.IsPathRooted(runDirectory))
                    throw new ArgumentException("--visual-run-dir는 절대 경로여야 합니다.");
                options.RunDirectory = Path.GetFullPath(runDirectory);
            }

            if (string.IsNullOrWhiteSpace(scenario))
                throw new ArgumentException("--visual-test 뒤에 시나리오 JSON 경로가 필요합니다.");

            options.ScenarioPath = ResolvePath(scenario, options.RepositoryRoot);
            if (!File.Exists(options.ScenarioPath))
                throw new FileNotFoundException("시나리오 파일을 찾을 수 없습니다.", options.ScenarioPath);
        }
        catch (Exception ex)
        {
            options.Error = ex.Message;
        }
        return options;
    }

    private static void RequireOnce(HashSet<string> seen, string option)
    {
        if (!seen.Add(option))
            throw new ArgumentException(option + " 옵션이 중복되었습니다.");
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1])
            || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException(option + " 뒤에 값이 필요합니다.");
        return args[++index];
    }

    private static string ResolvePath(string path, string repoRoot)
    {
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        return Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(repoRoot, path));
    }

    /// <summary>앱 출력 폴더나 작업 폴더에서 솔루션 파일을 찾아 저장소 루트를 확정한다.</summary>
    internal static string FindRepositoryRoot()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            for (int depth = 0; dir != null && depth < 10; depth++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "HeatingMold.Mvp.sln")))
                    return dir.FullName;
            }
        }
        return null;
    }
}
