// Standalone fixtures for the validation guard regression.
// Compiled to a single Windows application by Build-Fixtures.ps1.
//
// Modes (first argument):
//   messagebox                              standard Win32 MessageBox (#32770)
//   reporter [--empty-body|--long-body|--no-owner]
//                                          WPF dialog shaped like HeatingMold's ErrorReporter
//   launcher <childExe> [childArgs...]      starts the child, then exits 0 immediately
//   result <resultPath> [--tool-window]     writes result JSON and keeps a window open
//   result-dialog <resultPath>              writes result JSON, then shows a modal after 200 ms
//   earlyexit                                exits 3 without a result
//   earlyexit-zero                           exits 0 without a result
//   idle [title]                             keeps a window open until killed
//
// The fixtures deliberately do nothing useful with the product. They only create
// windows so the guard can prove it detects modals without pressing any button.

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using WinForms = System.Windows.Forms;

internal static class FixtureHost
{
    private const string DefaultBody =
        "영역 B 경로 조각 181/4488 (HeatingRun) 3D 스테이징 실패 — off-surface 3.003734\n" +
        "표본 137: (-3984.548138,-953.205011,1939.574219)\n" +
        "이 창은 검증 가드 fixture 입니다. 자동으로 닫히지 않습니다.";

    [STAThread]
    private static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "idle";
        if (mode.StartsWith("--visual-", StringComparison.OrdinalIgnoreCase))
        {
            // Run.ps1 passes product visual-test arguments. The mode is chosen by an
            // environment variable so the same stub can succeed or show a modal.
            string overrideMode = Environment.GetEnvironmentVariable("HM_GUARD_FIXTURE_MODE");
            mode = string.IsNullOrEmpty(overrideMode) ? "visual-result" : overrideMode.Trim();
        }
        try
        {
            switch (mode)
            {
                case "messagebox":
                    return RunMessageBox();
                case "reporter":
                    return RunReporter(args);
                case "launcher":
                    return RunLauncher(args);
                case "result":
                    return RunResult(args);
                case "result-dialog":
                    return RunResultThenDialog(args);
                case "earlyexit":
                    return 3;
                case "earlyexit-zero":
                    return 0;
                case "idle":
                    return RunIdle(args);
                case "slow-body":
                    return RunSlowBody();
                case "visual-result":
                    return RunVisualResult(args);
                case "visual-modal":
                    return RunMessageBox();
                default:
                    Console.Error.WriteLine("unknown fixture mode: " + mode);
                    return 2;
            }
        }
        catch (Exception ex)
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "hm-fixture-error.txt");
                File.WriteAllText(path, ex.ToString(), new UTF8Encoding(false));
            }
            catch (Exception) { }
            return 4;
        }
    }

    private static int RunMessageBox()
    {
        WinForms.MessageBox.Show(
            DefaultBody,
            "HeatingMold 오류",
            WinForms.MessageBoxButtons.OK,
            WinForms.MessageBoxIcon.Error);
        return 0;
    }

    private static int RunReporter(string[] args)
    {
        bool emptyBody = HasFlag(args, "--empty-body");
        bool longBody = HasFlag(args, "--long-body");
        bool noOwner = HasFlag(args, "--no-owner");

        string body = DefaultBody;
        if (emptyBody) body = string.Empty;
        if (longBody)
        {
            var builder = new StringBuilder();
            while (builder.Length < 20000) builder.Append("곡선 경계 표본 검증 상세 문장입니다. ");
            body = builder.ToString();
        }

        var application = new System.Windows.Application();
        System.Windows.Window owner = null;
        if (!noOwner)
        {
            owner = new System.Windows.Window
            {
                Title = "HeatingMold 검증 fixture",
                Width = 520,
                Height = 360,
                Left = 120,
                Top = 80
            };
            owner.Show();
        }

        var dialog = new System.Windows.Window
        {
            Title = "오류 — HeatingMold",
            Width = 860,
            Height = 520,
            ShowInTaskbar = false,
            WindowStartupLocation = noOwner
                ? System.Windows.WindowStartupLocation.CenterScreen
                : System.Windows.WindowStartupLocation.CenterOwner
        };
        if (owner != null) dialog.Owner = owner;

        var box = new System.Windows.Controls.TextBox
        {
            Text = body,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = System.Windows.TextWrapping.NoWrap,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12,
            Padding = new System.Windows.Thickness(6)
        };
        dialog.Content = box;
        dialog.ShowDialog();
        application.Shutdown();
        return 0;
    }

    private static int RunLauncher(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("launcher requires a child exe path");
            return 2;
        }
        string child = args[1];
        var info = new ProcessStartInfo(child);
        for (int i = 2; i < args.Length; i++)
        {
            info.Arguments = info.Arguments.Length == 0 ? Quote(args[i]) : info.Arguments + " " + Quote(args[i]);
        }
        info.UseShellExecute = false;
        Process.Start(info);
        return 0;
    }

    private static int RunResult(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("result requires a result path");
            return 2;
        }
        bool toolWindow = HasFlag(args, "--tool-window");
        WriteResult(args[1]);

        var form = new WinForms.Form
        {
            Text = "HeatingMold 검증 결과 창",
            Width = 640,
            Height = 420,
            Left = 160,
            Top = 100
        };
        var label = new WinForms.Label
        {
            Dock = WinForms.DockStyle.Fill,
            Text = "결과 파일을 작성했습니다. 이 창은 육안 확인을 위해 열린 상태로 유지됩니다.",
            TextAlign = System.Drawing.ContentAlignment.MiddleCenter
        };
        form.Controls.Add(label);
        form.Show();

        if (toolWindow)
        {
            var tool = new WinForms.Form
            {
                Text = "검증 도구 창",
                FormBorderStyle = WinForms.FormBorderStyle.FixedToolWindow,
                ShowInTaskbar = false,
                Width = 260,
                Height = 160,
                Left = 820,
                Top = 120
            };
            var box = new WinForms.TextBox { Dock = WinForms.DockStyle.Fill, Multiline = true, Text = "모달이 아닌 owned 도구 창" };
            tool.Controls.Add(box);
            tool.Show(form);
        }

        WinForms.Application.Run(form);
        return 0;
    }

    private static int RunResultThenDialog(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("result-dialog requires a result path");
            return 2;
        }
        WriteResult(args[1]);

        var form = new WinForms.Form
        {
            Text = "HeatingMold 결과+대화상자 fixture",
            Width = 640,
            Height = 420
        };
        var timer = new WinForms.Timer { Interval = 200 };
        timer.Tick += delegate
        {
            timer.Stop();
            WinForms.MessageBox.Show(form, DefaultBody, "HeatingMold 오류",
                WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Error);
        };
        form.Shown += delegate { timer.Start(); };
        WinForms.Application.Run(form);
        return 0;
    }

    private static int RunIdle(string[] args)
    {
        string title = args.Length > 1 ? args[1] : "HeatingMold 검증 대기 창";
        var form = new WinForms.Form { Text = title, Width = 560, Height = 360 };
        var label = new WinForms.Label
        {
            Dock = WinForms.DockStyle.Fill,
            Text = "결과 파일을 쓰지 않고 대기합니다(가드 종료 정리 확인용).",
            TextAlign = System.Drawing.ContentAlignment.MiddleCenter
        };
        form.Controls.Add(label);
        WinForms.Application.Run(form);
        return 0;
    }

    // A reporter-shaped dialog with many child Edit controls and NO message pump.
    // UIA and WM_GETTEXT cannot be answered, so the guard must stop collecting at its
    // total deadline instead of hanging on every child control.
    private static int RunSlowBody()
    {
        var owner = new WinForms.Form
        {
            Text = "HeatingMold 느린 본문 fixture",
            Width = 520,
            Height = 320
        };
        owner.Show();

        var dialog = new WinForms.Form
        {
            Text = "오류 — HeatingMold",
            Width = 900,
            Height = 600,
            ShowInTaskbar = false,
            FormBorderStyle = WinForms.FormBorderStyle.FixedDialog
        };
        for (int i = 0; i < 300; i++)
        {
            dialog.Controls.Add(new WinForms.TextBox
            {
                Text = "느린 본문 컨트롤 " + i,
                Left = 10,
                Top = 10 + (i % 20) * 26,
                Width = 200
            });
        }

        dialog.Owner = owner;
        owner.Enabled = false;
        dialog.Show();
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    // Mimics enough of the visual test contract for Run.ps1: accepts --visual-run-dir,
    // writes result.json there, then keeps the window open for visual review.
    private static int RunVisualResult(string[] args)
    {
        string runDirectory = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--visual-run-dir", StringComparison.OrdinalIgnoreCase))
                runDirectory = args[i + 1];
        }
        if (string.IsNullOrEmpty(runDirectory))
        {
            Console.Error.WriteLine("visual-result requires --visual-run-dir");
            return 2;
        }
        Directory.CreateDirectory(runDirectory);
        WriteResult(Path.Combine(runDirectory, "result.json"),
            "{\"Status\":\"Completed\",\"Automation\":\"Completed\",\"VisualReview\":\"NotRecorded\",\"Steps\":[]}");

        var form = new WinForms.Form
        {
            Text = "HeatingMold 시각테스트 stub 결과 창",
            Width = 640,
            Height = 420
        };
        var label = new WinForms.Label
        {
            Dock = WinForms.DockStyle.Fill,
            Text = "Run.ps1 연결 확인용 결과 창입니다. 가드는 성공 후 이 창을 닫지 않습니다.",
            TextAlign = System.Drawing.ContentAlignment.MiddleCenter
        };
        form.Controls.Add(label);
        WinForms.Application.Run(form);
        return 0;
    }

    private static void WriteResult(string path, string content = null)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temp = path + ".tmp";
        if (content == null) content = "{\"Status\":\"Completed\",\"GeneratedBy\":\"validation-guard-fixture\"}";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
    }

    private static bool HasFlag(string[] args, string flag)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string Quote(string value)
    {
        if (value.IndexOf(' ') < 0 && value.IndexOf('"') < 0) return value;
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
