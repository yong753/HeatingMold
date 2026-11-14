// Guarded validation process runner.
//
// Purpose: run one validation process tree (launcher + child app) while detecting
// unexpected modal dialogs, so an automated harness never blocks until a timeout
// waiting for a human to close an error window.
//
// This file is compiled by tools\validation\Invoke-GuardedProcess.ps1 / GuardedProcess.ps1
// through Add-Type. Keep it C# 5 compatible: PowerShell 5.1 compiles with the
// .NET Framework CodeDom provider (no string interpolation, no ?., no out var).
//
// Rules implemented here:
//  - Newly created process tree only. The root is created suspended, assigned to a
//    private job object, then resumed. Window ownership is checked by PID, but cleanup
//    is confined to the job; processes are never killed by image name.
//  - While monitoring, the job has KILL_ON_JOB_CLOSE set, so killing the guard host
//    also terminates the tracked tree. Only after a confirmed success is the flag
//    cleared (and re-queried) as the last step, so the app window can stay open.
//  - On failure the job tree is terminated with TerminateJobObject; confirmation
//    requires an empty job query. A PID exit check alone is not treated as proof.
//  - Modal detection: visible owned window whose owner is disabled, a visible
//    standard dialog (#32770) of a tracked process, or the product error reporter
//    title. Dialog buttons are never pressed.
//  - UIA / SendMessage text collection runs in one bounded worker (default 1 s total);
//    a hung provider or many slow child controls cannot extend the watchdog block.
//  - Per report-file writes use a temp file and File.Replace/File.Move. The JSON and
//    TXT pair is not a single atomic transaction.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace HeatingMold.Validation
{
    public sealed class GuardLaunchOptions
    {
        public string ExePath { get; set; }
        public string Arguments { get; set; }
        public string WorkingDirectory { get; set; }
        public string ResultPath { get; set; }
        public int TimeoutSeconds { get; set; }
        public int PollIntervalMs { get; set; }
        public string RunDirectory { get; set; }
        public int TextCaptureLimitMs { get; set; }
    }

    public sealed class GuardDialogInfo
    {
        public string Title { get; set; }
        public string ClassName { get; set; }
        public int ProcessId { get; set; }
        public string ProcessPath { get; set; }
        public string OwnerTitle { get; set; }
        public bool OwnerDisabled { get; set; }
        public string Body { get; set; }
        public int BodyLength { get; set; }
        public string BodySource { get; set; }
        public string BodyError { get; set; }
    }

    public sealed class GuardOutcome
    {
        public string Status { get; set; }
        public string Reason { get; set; }
        public string Message { get; set; }
        public string Detail { get; set; }
        public int RootProcessId { get; set; }
        public int RootExitCode { get; set; }
        public bool RootExited { get; set; }
        public string CommandLine { get; set; }
        public string StartTimeUtc { get; set; }
        public GuardDialogInfo Dialog { get; set; }
        public string LastStep { get; set; }
        public List<int> TrackedProcessIds { get; set; }
        public List<int> RemainingProcessIds { get; set; }
        public string CleanupStatus { get; set; }
        public List<string> Log { get; set; }
        public string JsonPath { get; set; }
        public string TextPath { get; set; }
        public bool ReportWriteFailed { get; set; }
        public string ReportWriteError { get; set; }
    }

    public static class GuardedProcessRunner
    {
        private const int DefaultPollIntervalMs = 250;
        private const int DefaultTextCaptureLimitMs = 1000;
        private const int MaxBodyChars = 8000;
        private const int MaxStepChars = 1000;

        private const uint CreateSuspended = 0x00000004;
        private const uint CreateNewProcessGroup = 0x00000200;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const uint WaitObject0 = 0x00000000;

        private const uint GwOwner = 4;
        private const uint SmtoBlock = 0x0001;
        private const uint SmtoAbortIfHung = 0x0002;
        private const uint WmGetText = 0x000D;

        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const int ErrorMoreData = 234;

        public static GuardOutcome Run(GuardLaunchOptions options)
        {
            var outcome = new GuardOutcome
            {
                Status = "failed",
                Reason = "invalid-options",
                Message = "검증 실행 옵션이 올바르지 않습니다.",
                Log = new List<string>(),
                TrackedProcessIds = new List<int>(),
                RemainingProcessIds = new List<int>(),
                CleanupStatus = "not-required",
                RootExitCode = -1
            };

            if (options == null)
            {
                outcome.Message = "검증 실행 옵션이 없습니다.";
                return outcome;
            }

            int poll = options.PollIntervalMs;
            if (poll <= 0) poll = DefaultPollIntervalMs;
            if (poll < 50) poll = 50;
            if (poll > 2000) poll = 2000;
            int captureLimit = options.TextCaptureLimitMs;
            if (captureLimit <= 0) captureLimit = DefaultTextCaptureLimitMs;
            if (captureLimit < 1) captureLimit = 1;
            if (captureLimit > 10000) captureLimit = 10000;
            int timeoutSeconds = options.TimeoutSeconds;
            if (timeoutSeconds <= 0) timeoutSeconds = 90;

            if (string.IsNullOrWhiteSpace(options.ExePath) || !File.Exists(options.ExePath))
            {
                outcome.Reason = "invalid-exe";
                outcome.Message = "검증할 실행 파일을 찾지 못했습니다: " + (options.ExePath ?? "(없음)");
                return outcome;
            }
            if (string.IsNullOrWhiteSpace(options.RunDirectory))
            {
                outcome.Reason = "invalid-run-directory";
                outcome.Message = "검증 실행 폴더가 지정되지 않았습니다.";
                return outcome;
            }

            string runDirectory;
            try
            {
                runDirectory = Path.GetFullPath(options.RunDirectory);
                Directory.CreateDirectory(runDirectory);
            }
            catch (Exception ex)
            {
                outcome.Reason = "invalid-run-directory";
                outcome.Message = "검증 실행 폴더를 만들지 못했습니다: " + ex.Message;
                return outcome;
            }

            string resultPath = null;
            if (!string.IsNullOrWhiteSpace(options.ResultPath))
            {
                try { resultPath = Path.GetFullPath(options.ResultPath); }
                catch (Exception ex)
                {
                    outcome.Reason = "invalid-result-path";
                    outcome.Message = "결과 파일 경로가 올바르지 않습니다: " + ex.Message;
                    return outcome;
                }
                if (File.Exists(resultPath))
                {
                    // Refuse to reuse a previous run's result file: it would look like
                    // an instant success before the app did anything.
                    outcome.Reason = "result-exists";
                    outcome.Message = "이전 실행의 결과 파일이 이미 있습니다: " + resultPath +
                        " — 새 실행 폴더를 사용하세요.";
                    return outcome;
                }
            }

            string workingDirectory = options.WorkingDirectory;
            if (string.IsNullOrWhiteSpace(workingDirectory))
                workingDirectory = Path.GetDirectoryName(Path.GetFullPath(options.ExePath));
            if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            {
                outcome.Reason = "invalid-working-directory";
                outcome.Message = "검증 작업 폴더를 찾지 못했습니다: " + (workingDirectory ?? "(없음)");
                return outcome;
            }

            string exePath = Path.GetFullPath(options.ExePath);
            string arguments = options.Arguments ?? string.Empty;
            string commandLine = "\"" + exePath + "\"" + (arguments.Length > 0 ? " " + arguments : string.Empty);
            outcome.CommandLine = commandLine;
            outcome.StartTimeUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            IntPtr job = IntPtr.Zero;
            PROCESS_INFORMATION processInfo = new PROCESS_INFORMATION();
            bool rootHandleValid = false;
            try
            {
                job = CreateJobObjectW(IntPtr.Zero, null);
                if (job == IntPtr.Zero)
                {
                    return Fail(outcome, runDirectory, resultPath, "job-create",
                        "검증 프로세스 트리를 추적할 job object를 만들지 못했습니다 (Win32 " +
                        Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture) + ").");
                }

                string limitError;
                if (!TrySetKillOnJobClose(job, out limitError))
                {
                    // Without kill-on-close a killed guard host would orphan the validation
                    // app. Do not launch anything in that state.
                    return Fail(outcome, runDirectory, resultPath, "job-limit",
                        "감시 프로세스가 종료될 때 검증 트리도 함께 정리되도록 job을 설정하지 못했습니다: " +
                        limitError);
                }

                var startup = new STARTUPINFO();
                startup.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                var commandBuffer = new StringBuilder(commandLine);
                bool created = CreateProcessW(
                    exePath, commandBuffer, IntPtr.Zero, IntPtr.Zero, false,
                    CreateSuspended | CreateNewProcessGroup | CreateUnicodeEnvironment,
                    IntPtr.Zero, workingDirectory, ref startup, out processInfo);
                if (!created)
                {
                    return Fail(outcome, runDirectory, resultPath, "launch",
                        "검증 프로세스를 시작하지 못했습니다 (Win32 " +
                        Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture) + ").");
                }
                rootHandleValid = true;
                outcome.RootProcessId = processInfo.dwProcessId;
                outcome.Log.Add("launched pid=" + processInfo.dwProcessId.ToString(CultureInfo.InvariantCulture) +
                    " suspended; assigning to job");

                if (!AssignProcessToJobObject(job, processInfo.hProcess))
                {
                    int code = Marshal.GetLastWin32Error();
                    TerminateProcess(processInfo.hProcess, 1);
                    return Fail(outcome, runDirectory, resultPath, "job-assign",
                        "새 프로세스를 추적 job에 넣지 못했습니다 (Win32 " + code.ToString(CultureInfo.InvariantCulture) +
                        "). 소유 범위를 확인할 수 없으므로 PID 이름 매칭으로 대체하지 않습니다.");
                }

                if (ResumeThread(processInfo.hThread) == unchecked((uint)-1))
                {
                    int code = Marshal.GetLastWin32Error();
                    List<int> suspendedPids = QueryJobProcessIds(job);
                    CleanupTrackedTree(job, outcome, suspendedPids);
                    return Fail(outcome, runDirectory, resultPath, "launch-resume",
                        "새 프로세스를 재개하지 못했습니다 (Win32 " +
                        code.ToString(CultureInfo.InvariantCulture) + ").");
                }

                outcome.Log.Add("resumed; monitoring every " + poll.ToString(CultureInfo.InvariantCulture) + " ms, timeout " +
                    timeoutSeconds.ToString(CultureInfo.InvariantCulture) + " s");
                return Monitor(options, outcome, job, processInfo.hProcess, runDirectory, resultPath,
                    poll, captureLimit, timeoutSeconds);
            }
            catch (Exception ex)
            {
                outcome.Log.Add("unhandled: " + ex);
                if (job != IntPtr.Zero)
                {
                    List<int> left = QueryJobProcessIds(job);
                    CleanupTrackedTree(job, outcome, left);
                }
                return Fail(outcome, runDirectory, resultPath, "guard-exception",
                    "검증 감시 도중 예외가 발생했습니다: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (rootHandleValid)
                {
                    if (processInfo.hThread != IntPtr.Zero) CloseHandle(processInfo.hThread);
                    if (processInfo.hProcess != IntPtr.Zero) CloseHandle(processInfo.hProcess);
                }
                if (job != IntPtr.Zero) CloseHandle(job);
            }
        }

        private static GuardOutcome Monitor(
            GuardLaunchOptions options,
            GuardOutcome outcome,
            IntPtr job,
            IntPtr rootProcess,
            string runDirectory,
            string resultPath,
            int poll,
            int captureLimit,
            int timeoutSeconds)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            int emptyPolls = 0;
            int resultStableChecks = 0;
            long lastResultSize = -1;
            bool resultSeen = false;

            while (true)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    List<int> stuck = QueryJobProcessIds(job);
                    if (stuck == null) stuck = new List<int>();
                    bool confirmed = CleanupTrackedTree(job, outcome, stuck);
                    return Fail(outcome, runDirectory, resultPath, "timeout",
                        "검증 대기 시간 " + timeoutSeconds.ToString(CultureInfo.InvariantCulture) +
                        "초를 넘었습니다. tracked=" + stuck.Count.ToString(CultureInfo.InvariantCulture) +
                        (confirmed ? "개를 정리하고 종료를 확인했습니다." : "개에 정리를 요청했지만 종료가 확인되지 않았습니다."));
                }

                Thread.Sleep(poll);

                bool rootExited = WaitForSingleObject(rootProcess, 0) == WaitObject0;
                outcome.RootExited = rootExited;
                if (rootExited)
                {
                    uint exitCode;
                    if (GetExitCodeProcess(rootProcess, out exitCode))
                        outcome.RootExitCode = unchecked((int)exitCode);
                }

                List<int> pids;
                string jobError;
                pids = TryQueryJobProcessIds(job, out jobError);
                if (pids == null)
                {
                    CleanupTrackedTree(job, outcome, null);
                    return Fail(outcome, runDirectory, resultPath, "job-query",
                        "검증 프로세스 트리 목록을 읽지 못했습니다: " + jobError +
                        " — 소유 범위를 확인할 수 없어 중단합니다.");
                }

                WindowInfo dialog = FindUnexpectedDialog(pids, outcome);
                if (dialog != null)
                {
                    GuardDialogInfo info = BuildDialogInfo(dialog, captureLimit, outcome);
                    outcome.Dialog = info;
                    if (resultPath != null && File.Exists(resultPath))
                        outcome.Detail = "결과 파일이 이미 있었지만 예상하지 않은 대화상자를 우선 오류로 처리합니다.";
                    CleanupTrackedTree(job, outcome, pids);
                    return Fail(outcome, runDirectory, resultPath, "unexpected-dialog",
                        "예상하지 않은 대화상자: \"" + info.Title + "\" (" + info.ClassName +
                        ", PID " + info.ProcessId.ToString(CultureInfo.InvariantCulture) +
                        "). 자동으로 닫거나 승인하지 않습니다.");
                }

                if (rootExited && outcome.RootExitCode != 0 && (resultPath == null || !File.Exists(resultPath)))
                {
                    CleanupTrackedTree(job, outcome, pids);
                    return Fail(outcome, runDirectory, resultPath, "root-nonzero-exit",
                        "검증 프로세스가 결과 파일 없이 종료 코드 " +
                        outcome.RootExitCode.ToString(CultureInfo.InvariantCulture) + "로 끝났습니다.");
                }

                if (File.Exists(resultPath))
                {
                    long size = -1;
                    try { size = new FileInfo(resultPath).Length; }
                    catch (Exception) { }
                    if (size > 0 && size == lastResultSize) resultStableChecks++;
                    else resultStableChecks = 0;
                    lastResultSize = size;
                    resultSeen = resultSeen || size > 0;

                    if (resultStableChecks >= 1)
                    {
                        // One more scan after a short settle delay: a dialog that appears
                        // together with the result must still win over the success.
                        Thread.Sleep(poll);
                        List<int> pidsAfter;
                        string afterError;
                        pidsAfter = TryQueryJobProcessIds(job, out afterError);
                        if (pidsAfter == null)
                        {
                            CleanupTrackedTree(job, outcome, null);
                            return Fail(outcome, runDirectory, resultPath, "job-query",
                                "결과 확인 직후 검증 프로세스 트리 목록을 읽지 못했습니다: " + afterError);
                        }
                        WindowInfo latedialog = FindUnexpectedDialog(pidsAfter, outcome);
                        if (latedialog != null)
                        {
                            GuardDialogInfo info = BuildDialogInfo(latedialog, captureLimit, outcome);
                            outcome.Dialog = info;
                            outcome.Detail = "결과 파일과 대화상자가 함께 감지되어 대화상자를 오류로 우선 처리합니다.";
                            CleanupTrackedTree(job, outcome, pidsAfter);
                            return Fail(outcome, runDirectory, resultPath, "unexpected-dialog",
                                "예상하지 않은 대화상자: \"" + info.Title + "\" (" + info.ClassName +
                                ", PID " + info.ProcessId.ToString(CultureInfo.InvariantCulture) +
                                "). 자동으로 닫거나 승인하지 않습니다.");
                        }

                        outcome.Status = "succeeded";
                        outcome.Reason = "result-written";
                        outcome.Message = "결과 파일이 작성되었습니다. 검증 프로세스 창은 육안 확인을 위해 그대로 둡니다.";
                        return FinalizeSuccess(job, outcome, runDirectory, resultPath, pidsAfter);
                    }
                }
                else if (resultPath == null && rootExited && pids.Count == 0 && outcome.RootExitCode == 0)
                {
                    outcome.Status = "succeeded";
                    outcome.Reason = "completed";
                    outcome.Message = "검증 프로세스가 결과 파일 없이 정상 종료했습니다.";
                    return FinalizeSuccess(job, outcome, runDirectory, resultPath, pids);
                }

                if (rootExited && pids.Count == 0)
                {
                    emptyPolls++;
                    if (emptyPolls >= 2)
                    {
                        outcome.TrackedProcessIds = new List<int>(pids);
                        return Fail(outcome, runDirectory, resultPath, "early-exit",
                            resultSeen
                                ? "결과 파일 크기가 안정되지 않은 채 검증 프로세스가 종료되었습니다."
                                : "검증 프로세스가 결과 파일을 쓰지 않고 종료되었습니다 (종료 코드 " +
                                  outcome.RootExitCode.ToString(CultureInfo.InvariantCulture) + ").");
                    }
                }
                else
                {
                    emptyPolls = 0;
                }
            }
        }

        private static GuardOutcome Fail(
            GuardOutcome outcome,
            string runDirectory,
            string resultPath,
            string reason,
            string message)
        {
            outcome.Status = "failed";
            outcome.Reason = reason;
            outcome.Message = message;
            if (outcome.LastStep == null) outcome.LastStep = ReadLastStep(runDirectory);
            PublishReports(runDirectory, resultPath, outcome);
            return outcome;
        }

        private static GuardOutcome FinalizeSuccess(
            IntPtr job,
            GuardOutcome outcome,
            string runDirectory,
            string resultPath,
            List<int> pids)
        {
            outcome.TrackedProcessIds = pids != null ? new List<int>(pids) : new List<int>();
            outcome.RemainingProcessIds = new List<int>(outcome.TrackedProcessIds);
            outcome.CleanupStatus = "not-required";
            outcome.LastStep = ReadLastStep(runDirectory);

            // Publish the success report while kill-on-close is still armed. If the guard
            // host dies during report I/O, the OS closes the job and terminates the
            // validation tree instead of leaving the app orphaned.
            bool published = PublishReports(runDirectory, resultPath, outcome);
            if (!published)
            {
                CleanupTrackedTree(job, outcome, outcome.TrackedProcessIds);
                PublishReports(runDirectory, resultPath, outcome);
                return outcome;
            }

            // Re-check the orphan protection after the report was published, then detach
            // as the very last blocking step. No report I/O happens after detachment.
            JOBOBJECT_EXTENDED_LIMIT_INFORMATION state;
            string stateError;
            if (!TryReadExtendedLimitInformation(job, out state, out stateError) ||
                (state.BasicLimitInformation.LimitFlags & JobObjectLimitKillOnJobClose) == 0)
            {
                outcome.Status = "failed";
                outcome.Reason = "job-detach";
                outcome.Message = "보고서 게시 후 kill-on-close 상태를 확인하지 못해 실패로 처리합니다: " +
                    (stateError ?? "KILL_ON_JOB_CLOSE is not set");
                string rearmError;
                if (!TrySetKillOnJobClose(job, out rearmError))
                    outcome.Log.Add("re-arm kill-on-close failed: " + rearmError);
                CleanupTrackedTree(job, outcome, outcome.TrackedProcessIds);
                PublishReports(runDirectory, resultPath, outcome);
                return outcome;
            }
            outcome.Log.Add("report published while kill-on-close was still set");

            string detachError;
            if (!TryDetachJob(job, out detachError))
            {
                outcome.Status = "failed";
                outcome.Reason = "job-detach";
                outcome.Message = "검증은 끝났지만 감시 job을 안전하게 해제하지 못해 실패로 처리합니다: " + detachError;
                string rearmError;
                if (!TrySetKillOnJobClose(job, out rearmError))
                    outcome.Log.Add("re-arm kill-on-close failed: " + rearmError);
                CleanupTrackedTree(job, outcome, outcome.TrackedProcessIds);
                PublishReports(runDirectory, resultPath, outcome);
                return outcome;
            }
            outcome.Log.Add("kill-on-close cleared after the report was published; app window may stay open");
            return outcome;
        }

        private static string AppendDetail(string detail, string extra)
        {
            return string.IsNullOrEmpty(detail) ? extra : detail + " / " + extra;
        }

        private static bool CleanupTrackedTree(IntPtr job, GuardOutcome outcome, List<int> tracked)
        {
            outcome.TrackedProcessIds = tracked != null ? new List<int>(tracked) : new List<int>();
            return TerminateTree(job, outcome);
        }

        private static bool TerminateTree(IntPtr job, GuardOutcome outcome)
        {
            List<int> targets = outcome.TrackedProcessIds != null
                ? new List<int>(outcome.TrackedProcessIds)
                : new List<int>();
            if (job == IntPtr.Zero)
            {
                outcome.CleanupStatus = "not-required";
                outcome.RemainingProcessIds = new List<int>();
                return true;
            }

            bool terminated = TerminateJobObject(job, 1);
            if (!terminated)
            {
                int code = Marshal.GetLastWin32Error();
                outcome.Log.Add("TerminateJobObject failed: Win32 " + code.ToString(CultureInfo.InvariantCulture));
                List<int> leftAfterFailure = QueryJobProcessIds(job);
                outcome.CleanupStatus = "unconfirmed";
                outcome.RemainingProcessIds = leftAfterFailure ?? new List<int>(targets);
                outcome.Detail = AppendDetail(outcome.Detail,
                    "정리를 요청했지만 종료가 확인되지 않았습니다 (TerminateJobObject Win32 " +
                    code.ToString(CultureInfo.InvariantCulture) + ").");
                return false;
            }

            // TerminateJobObject starts termination asynchronously. Confirmation requires
            // an actual empty job query; a PID exit check is not used because OpenProcess
            // failure (access denied / unknown) cannot prove the process exited.
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            bool jobEmpty = false;
            List<int> lastKnown = null;
            while (true)
            {
                List<int> left = QueryJobProcessIds(job);
                if (left != null)
                {
                    lastKnown = left;
                    if (left.Count == 0)
                    {
                        jobEmpty = true;
                        break;
                    }
                }
                if (DateTime.UtcNow >= deadline) break;
                Thread.Sleep(50);
            }

            if (jobEmpty)
            {
                outcome.CleanupStatus = "confirmed";
                outcome.RemainingProcessIds = new List<int>();
                outcome.Log.Add("cleanup confirmed: job is empty; tracked=" + JoinPids(targets));
                return true;
            }

            outcome.CleanupStatus = "unconfirmed";
            outcome.RemainingProcessIds = lastKnown != null
                ? lastKnown
                : new List<int>(targets);
            outcome.Log.Add("cleanup unconfirmed: job still reported pids=" +
                JoinPids(outcome.RemainingProcessIds));
            outcome.Detail = AppendDetail(outcome.Detail,
                "종료가 확인되지 않았습니다. job에 남아 있거나 확인하지 못한 프로세스: " +
                JoinPids(outcome.RemainingProcessIds));
            return false;
        }

        private static bool TrySetKillOnJobClose(IntPtr job, out string error)
        {
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            uint size = (uint)Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, size))
            {
                error = "SetInformationJobObject(KILL_ON_JOB_CLOSE) Win32 " +
                    Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture);
                return false;
            }
            JOBOBJECT_EXTENDED_LIMIT_INFORMATION check;
            if (!TryReadExtendedLimitInformation(job, out check, out error)) return false;
            if ((check.BasicLimitInformation.LimitFlags & JobObjectLimitKillOnJobClose) == 0)
            {
                error = "KILL_ON_JOB_CLOSE was not reported by the job after setting it";
                return false;
            }
            error = null;
            return true;
        }

        private static bool TryDetachJob(IntPtr job, out string error)
        {
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = 0;
            uint size = (uint)Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, size))
            {
                error = "SetInformationJobObject(clear) Win32 " +
                    Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture);
                return false;
            }
            JOBOBJECT_EXTENDED_LIMIT_INFORMATION check;
            if (!TryReadExtendedLimitInformation(job, out check, out error)) return false;
            if ((check.BasicLimitInformation.LimitFlags & JobObjectLimitKillOnJobClose) != 0)
            {
                error = "KILL_ON_JOB_CLOSE is still set after clearing it";
                return false;
            }
            error = null;
            return true;
        }

        private static bool TryReadExtendedLimitInformation(
            IntPtr job,
            out JOBOBJECT_EXTENDED_LIMIT_INFORMATION info,
            out string error)
        {
            uint returned;
            uint size = (uint)Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            if (!QueryInformationJobObjectExtended(job, JobObjectExtendedLimitInformation, out info, size, out returned))
            {
                error = "QueryInformationJobObject(ExtendedLimitInformation) Win32 " +
                    Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture);
                return false;
            }
            error = null;
            return true;
        }

        private static string JoinPids(List<int> pids)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < pids.Count; i++)
            {
                if (i > 0) builder.Append(',');
                builder.Append(pids[i].ToString(CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }

        private static List<int> QueryJobProcessIds(IntPtr job)
        {
            string error;
            return TryQueryJobProcessIds(job, out error);
        }

        private static List<int> TryQueryJobProcessIds(IntPtr job, out string error)
        {
            error = null;
            if (job == IntPtr.Zero)
            {
                error = "job handle is not available";
                return null;
            }
            int size = 8 + IntPtr.Size * 1024;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    uint returned;
                    if (QueryInformationJobObject(job, 3, buffer, (uint)size, out returned))
                    {
                        var pids = new List<int>();
                        int count = Marshal.ReadInt32(buffer, 4);
                        int maxBySize = (size - 8) / IntPtr.Size;
                        if (count > maxBySize) count = maxBySize;
                        for (int i = 0; i < count; i++)
                        {
                            long pid = Marshal.ReadIntPtr(new IntPtr(buffer.ToInt64() + 8), i * IntPtr.Size).ToInt64();
                            if (pid > 0 && pid <= int.MaxValue) pids.Add((int)pid);
                        }
                        return pids;
                    }
                    int code = Marshal.GetLastWin32Error();
                    if (code != ErrorMoreData)
                    {
                        error = "QueryInformationJobObject(JobObjectBasicProcessIdList) failed: Win32 " +
                            code.ToString(CultureInfo.InvariantCulture);
                        return null;
                    }
                    int needed = (int)returned + IntPtr.Size * 4;
                    size = needed > size ? needed : size * 2;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            error = "job process list did not fit in 3 attempts";
            return null;
        }

        private sealed class WindowInfo
        {
            public IntPtr Handle;
            public int ProcessId;
            public string ClassName;
            public string Title;
            public IntPtr Owner;
            public string OwnerTitle;
            public bool OwnerDisabled;
            public bool DialogClass;
        }

        private static WindowInfo FindUnexpectedDialog(List<int> pids, GuardOutcome outcome)
        {
            if (pids.Count == 0) return null;
            var tracked = new HashSet<int>(pids);
            WindowInfo found = null;
            EnumWindows(delegate(IntPtr hwnd, IntPtr state)
            {
                uint pidValue;
                GetWindowThreadProcessId(hwnd, out pidValue);
                int pid = unchecked((int)pidValue);
                if (!tracked.Contains(pid)) return true;
                if (!IsWindowVisible(hwnd)) return true;

                string className = GetClassName(hwnd);
                string title = GetWindowTitle(hwnd);
                IntPtr owner = GetWindow(hwnd, GwOwner);
                bool ownerDisabled = owner != IntPtr.Zero && !IsWindowEnabled(owner);
                bool dialogClass = string.Equals(className, "#32770", StringComparison.Ordinal);
                bool reporterTitle = IsReporterTitle(title);
                bool utility = IsUtilityClass(className);
                bool ownedModal = ownerDisabled && !utility;

                if (dialogClass || reporterTitle || ownedModal)
                {
                    var info = new WindowInfo();
                    info.Handle = hwnd;
                    info.ProcessId = pid;
                    info.ClassName = className;
                    info.Title = title;
                    info.Owner = owner;
                    info.OwnerTitle = owner != IntPtr.Zero ? GetWindowTitle(owner) : "";
                    info.OwnerDisabled = ownerDisabled;
                    info.DialogClass = dialogClass;
                    found = info;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static bool IsReporterTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return false;
            if (title.IndexOf("HeatingMold", StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (title.IndexOf("오류", StringComparison.Ordinal) >= 0) return true;
            if (title.IndexOf("경고", StringComparison.Ordinal) >= 0) return true;
            if (title.IndexOf("시작 실패", StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static bool IsUtilityClass(string className)
        {
            if (string.IsNullOrEmpty(className)) return true;
            string[] excluded =
            {
                "IME", "MSCTFIME UI", "CiceroUIWndFrame", "tooltips_class32", "#32768",
                "SysShadow", "Base_PowerMessageWindow", "GDI+ Hook Window Class",
                "DWM Notification Window", "ForegroundStaging"
            };
            for (int i = 0; i < excluded.Length; i++)
            {
                if (string.Equals(className, excluded[i], StringComparison.OrdinalIgnoreCase)) return true;
            }
            if (className.StartsWith("WindowsForms10.ToolTip", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private sealed class CaptureSnapshot
        {
            public string Body;
            public string Source;
            public string Error;
            public bool TimedOut;
        }

        private static GuardDialogInfo BuildDialogInfo(WindowInfo window, int captureLimit, GuardOutcome outcome)
        {
            var info = new GuardDialogInfo();
            info.Title = window.Title ?? string.Empty;
            info.ClassName = window.ClassName ?? string.Empty;
            info.ProcessId = window.ProcessId;
            info.ProcessPath = GetProcessPath(window.ProcessId);
            info.OwnerTitle = window.OwnerTitle ?? string.Empty;
            info.OwnerDisabled = window.OwnerDisabled;

            CaptureSnapshot capture = CaptureBody(window.Handle, captureLimit);
            string body = capture.Body ?? string.Empty;
            info.BodyLength = body.Length;
            if (body.Length > MaxBodyChars) body = body.Substring(0, MaxBodyChars) + "…(잘림)";
            info.Body = body;
            info.BodySource = capture.Source ?? string.Empty;
            info.BodyError = capture.Error ?? string.Empty;
            outcome.Log.Add("dialog detected pid=" + window.ProcessId.ToString(CultureInfo.InvariantCulture) +
                " class=" + info.ClassName + " title=" + info.Title +
                " bodySource=" + info.BodySource + " bodyLength=" + info.BodyLength.ToString(CultureInfo.InvariantCulture));
            return info;
        }

        private static CaptureSnapshot CaptureBody(IntPtr hwnd, int limitMs)
        {
            var workerResult = new CaptureSnapshot();
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(limitMs);

            // UIA and the Win32 fallback run in ONE bounded worker. The watchdog thread
            // waits at most limitMs in total, so a hung UIA provider or many slow child
            // controls cannot extend the failure path beyond the configured cap.
            var thread = new Thread(delegate()
            {
                try
                {
                    var result = new CaptureSnapshot();
                    string uiaBody = null;
                    string uiaError = null;
                    bool uiaFound = false;
                    try
                    {
                        uiaFound = TryCaptureWithUia(hwnd, out uiaBody, out uiaError);
                    }
                    catch (Exception ex)
                    {
                        uiaError = ex.GetType().Name + ": " + ex.Message;
                    }

                    if (uiaFound && !string.IsNullOrEmpty(uiaBody))
                    {
                        result.Body = uiaBody;
                        result.Source = "uia";
                    }
                    else if (DateTime.UtcNow >= deadline)
                    {
                        result.TimedOut = true;
                        result.Source = "timeout";
                        result.Error = "본문 수집 전체 상한 " +
                            limitMs.ToString(CultureInfo.InvariantCulture) + " ms를 넘겨 중단했습니다.";
                    }
                    else
                    {
                        string winBody;
                        string winError;
                        string winSource;
                        bool winTimedOut;
                        TryCaptureWithWin32(hwnd, deadline, out winBody, out winError,
                            out winSource, out winTimedOut);
                        if (!string.IsNullOrEmpty(winBody))
                        {
                            result.Body = winBody;
                            result.Source = winSource;
                            result.TimedOut = winTimedOut;
                            result.Error = winTimedOut
                                ? "본문 수집 전체 상한 " +
                                  limitMs.ToString(CultureInfo.InvariantCulture) +
                                  " ms에 도달해 일부 자식 컨트롤을 읽지 못했습니다."
                                : uiaError;
                        }
                        else
                        {
                            result.TimedOut = winTimedOut;
                            result.Source = winTimedOut ? "timeout" : "unavailable";
                            result.Error = winTimedOut
                                ? "본문 수집 전체 상한 " +
                                  limitMs.ToString(CultureInfo.InvariantCulture) +
                                  " ms에 도달했고 Win32 폴백도 본문을 찾지 못했습니다."
                                : (uiaError ?? winError);
                        }
                    }

                    if (string.IsNullOrEmpty(result.Body))
                    {
                        // GetWindowTextW on another process returns the cached caption
                        // without sending WM_GETTEXT, so this stays safe for hung windows.
                        string title = GetWindowTitle(hwnd);
                        if (!string.IsNullOrEmpty(title))
                        {
                            result.Body = title;
                            if (string.IsNullOrEmpty(result.Source) || result.Source == "unavailable")
                                result.Source = "title-fallback";
                        }
                    }
                    workerResult = result;
                }
                catch (Exception ex)
                {
                    workerResult = new CaptureSnapshot
                    {
                        Source = "error",
                        Error = ex.GetType().Name + ": " + ex.Message
                    };
                }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(limitMs))
            {
                // Do not read worker fields after a timeout: the worker may still be
                // blocked inside UIA. The cached caption is available immediately.
                return new CaptureSnapshot
                {
                    Body = GetWindowTitle(hwnd),
                    Source = "timeout",
                    TimedOut = true,
                    Error = "본문 수집 전체 상한 " +
                        limitMs.ToString(CultureInfo.InvariantCulture) +
                        " ms를 넘어 UIA/Win32 수집을 중단하고 창 제목만 남겼습니다."
                };
            }
            return workerResult;
        }

        private static bool TryCaptureWithUia(IntPtr hwnd, out string body, out string error)
        {
            body = null;
            error = null;
            AutomationElement root = AutomationElement.FromHandle(hwnd);
            if (root == null)
            {
                error = "AutomationElement.FromHandle returned null";
                return false;
            }

            string best = FindEditText(root);
            if (string.IsNullOrEmpty(best)) best = FindControlTypeText(root, ControlType.Document);
            if (string.IsNullOrEmpty(best))
            {
                // Do not fall back to the window Name here: the Win32 fallback can still
                // read native static text from a standard MessageBox, which is more useful.
                error = "no Edit/Document text element";
                return false;
            }
            body = best;
            return true;
        }

        private static string FindEditText(AutomationElement root)
        {
            return FindControlTypeText(root, ControlType.Edit);
        }

        private static string FindControlTypeText(AutomationElement root, ControlType controlType)
        {
            try
            {
                var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, controlType);
                AutomationElementCollection elements = root.FindAll(TreeScope.Descendants, condition);
                string best = null;
                for (int i = 0; i < elements.Count; i++)
                {
                    string text = TryReadElementText(elements[i]);
                    if (!string.IsNullOrEmpty(text) && (best == null || text.Length > best.Length))
                        best = text;
                }
                return best;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string TryReadElementText(AutomationElement element)
        {
            object pattern;
            try
            {
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern))
                {
                    try { return ((ValuePattern)pattern).Current.Value; }
                    catch (Exception) { }
                }
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out pattern))
                {
                    try { return ((TextPattern)pattern).DocumentRange.GetText(4096); }
                    catch (Exception) { }
                }
                return element.Current.Name;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void TryCaptureWithWin32(
            IntPtr hwnd,
            DateTime deadline,
            out string body,
            out string error,
            out string source,
            out bool timedOut)
        {
            body = null;
            error = null;
            source = "win32";
            timedOut = false;
            var edits = new List<string>();
            var statics = new List<string>();
            bool deadlineHit = false;
            bool capped = false;
            int visited = 0;
            const int MaxChildren = 64;

            try
            {
                EnumChildWindows(hwnd, delegate(IntPtr child, IntPtr state)
                {
                    int remaining = RemainingMilliseconds(deadline);
                    if (remaining <= 0)
                    {
                        deadlineHit = true;
                        return false;
                    }
                    visited++;
                    if (visited > MaxChildren)
                    {
                        capped = true;
                        return false;
                    }

                    string className = GetClassName(child);
                    if (string.Equals(className, "Edit", StringComparison.OrdinalIgnoreCase))
                    {
                        if (edits.Count >= 8)
                        {
                            capped = true;
                            return false;
                        }
                        bool readTimedOut;
                        string text = ReadEditText(child, (uint)Math.Min(200, remaining), out readTimedOut);
                        if (readTimedOut)
                        {
                            if (RemainingMilliseconds(deadline) <= 0) deadlineHit = true;
                        }
                        if (!string.IsNullOrEmpty(text)) edits.Add(text);
                        if (deadlineHit) return false;
                    }
                    else if (string.Equals(className, "Static", StringComparison.OrdinalIgnoreCase))
                    {
                        if (statics.Count >= 16)
                        {
                            capped = true;
                            return false;
                        }
                        string text = GetWindowTitle(child);
                        if (!string.IsNullOrEmpty(text)) statics.Add(text);
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
            }

            List<string> texts = edits.Count > 0 ? edits : statics;
            if (texts.Count > 0)
            {
                body = string.Join(Environment.NewLine, texts.ToArray());
                if (body.Length > MaxBodyChars) body = body.Substring(0, MaxBodyChars);
            }

            timedOut = deadlineHit;
            if (deadlineHit) source = "win32-timeout";
            else if (capped) source = "win32-partial";
            else source = "win32";
        }

        private static int RemainingMilliseconds(DateTime deadline)
        {
            double remaining = (deadline - DateTime.UtcNow).TotalMilliseconds;
            if (remaining <= 0) return 0;
            if (remaining > int.MaxValue) return int.MaxValue;
            return (int)remaining;
        }

        private static string ReadEditText(IntPtr hwnd, uint timeoutMs, out bool timedOut)
        {
            timedOut = false;
            const int BufferChars = 8192;
            var buffer = new StringBuilder(BufferChars);
            IntPtr result;
            IntPtr copied = SendMessageTimeoutW(hwnd, WmGetText, new IntPtr(BufferChars - 1), buffer,
                SmtoBlock | SmtoAbortIfHung, timeoutMs, out result);
            if (copied == IntPtr.Zero)
            {
                int code = Marshal.GetLastWin32Error();
                if (code == 1460) timedOut = true; // ERROR_TIMEOUT
                return null;
            }
            return buffer.Length > 0 ? buffer.ToString() : null;
        }

        private static string GetClassName(IntPtr hwnd)
        {
            var buffer = new StringBuilder(512);
            int length = GetClassNameW(hwnd, buffer, buffer.Capacity);
            return length > 0 ? buffer.ToString() : string.Empty;
        }

        private static string GetWindowTitle(IntPtr hwnd)
        {
            var buffer = new StringBuilder(1024);
            int length = GetWindowTextW(hwnd, buffer, buffer.Capacity);
            return length > 0 ? buffer.ToString() : string.Empty;
        }

        private static string GetProcessPath(int pid)
        {
            IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (process == IntPtr.Zero) return string.Empty;
            try
            {
                var buffer = new StringBuilder(1024);
                int size = buffer.Capacity;
                if (QueryFullProcessImageNameW(process, 0, buffer, ref size)) return buffer.ToString();
                return string.Empty;
            }
            finally
            {
                CloseHandle(process);
            }
        }

        private static string ReadLastStep(string runDirectory)
        {
            try
            {
                string path = Path.Combine(runDirectory, "steps.jsonl");
                if (!File.Exists(path)) return string.Empty;
                string last = null;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (!string.IsNullOrWhiteSpace(line)) last = line;
                    }
                }
                if (string.IsNullOrEmpty(last)) return string.Empty;
                return last.Length > MaxStepChars ? last.Substring(0, MaxStepChars) + "…" : last;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static bool PublishReports(string runDirectory, string resultPath, GuardOutcome outcome)
        {
            outcome.JsonPath = Path.Combine(runDirectory, "validation-guard.json");
            outcome.TextPath = Path.Combine(runDirectory, "validation-guard.txt");
            if (outcome.LastStep == null) outcome.LastStep = ReadLastStep(runDirectory);
            var encoding = new UTF8Encoding(false);

            try
            {
                WriteAtomic(outcome.JsonPath, BuildJson(outcome), encoding);
                WriteAtomic(outcome.TextPath, BuildText(outcome), encoding);
                outcome.ReportWriteFailed = false;
                outcome.ReportWriteError = null;
                return true;
            }
            catch (Exception ex)
            {
                outcome.ReportWriteFailed = true;
                outcome.ReportWriteError = ex.GetType().Name + ": " + ex.Message;
                outcome.Log.Add("report write failed: " + outcome.ReportWriteError);

                if (string.Equals(outcome.Status, "succeeded", StringComparison.Ordinal))
                {
                    outcome.Status = "failed";
                    outcome.Reason = "report-write-failed";
                    outcome.Message = "검증 결과는 준비되었지만 감시 보고서를 쓰지 못해 실패로 처리합니다: " +
                        outcome.ReportWriteError;
                }
                else if (!string.Equals(outcome.Reason, "report-write-failed", StringComparison.Ordinal))
                {
                    outcome.Message = outcome.Message + " (감시 보고서 쓰기 실패: " + outcome.ReportWriteError + ")";
                }

                // Correct a success JSON that may already have been published before the
                // TXT write failed, and try once more with the failed status.
                try { WriteAtomic(outcome.JsonPath, BuildJson(outcome), encoding); }
                catch (Exception) { }
                try { WriteAtomic(outcome.TextPath, BuildText(outcome), encoding); }
                catch (Exception) { }
                return false;
            }
        }

        // Replace one report file. Each file replacement is atomic; the JSON/TXT pair is
        // not a single transaction.
        private static void WriteAtomic(string path, string content, Encoding encoding)
        {
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, content, encoding);
                if (File.Exists(path))
                {
                    File.Replace(temp, path, null);
                }
                else
                {
                    File.Move(temp, path);
                }
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception) { }
            }
        }

        private static string BuildJson(GuardOutcome outcome)
        {
            var builder = new StringBuilder();
            builder.AppendLine("{");
            AppendJsonField(builder, "status", outcome.Status, true);
            AppendJsonField(builder, "reason", outcome.Reason, true);
            AppendJsonField(builder, "message", outcome.Message, true);
            AppendJsonField(builder, "detail", outcome.Detail, true);
            builder.AppendLine("  \"rootProcessId\": " + outcome.RootProcessId.ToString(CultureInfo.InvariantCulture) + ",");
            builder.AppendLine("  \"rootExitCode\": " + outcome.RootExitCode.ToString(CultureInfo.InvariantCulture) + ",");
            builder.AppendLine("  \"rootExited\": " + (outcome.RootExited ? "true" : "false") + ",");
            AppendJsonField(builder, "commandLine", outcome.CommandLine, true);
            AppendJsonField(builder, "startTimeUtc", outcome.StartTimeUtc, true);
            AppendJsonField(builder, "lastStep", outcome.LastStep, true);
            AppendJsonField(builder, "jsonPath", outcome.JsonPath, true);
            AppendJsonField(builder, "textPath", outcome.TextPath, true);
            builder.AppendLine("  \"reportWriteFailed\": " + (outcome.ReportWriteFailed ? "true" : "false") + ",");
            AppendJsonField(builder, "reportWriteError", outcome.ReportWriteError, true);
            builder.AppendLine("  \"cleanupStatus\": " + Quote(outcome.CleanupStatus) + ",");
            builder.AppendLine("  \"trackedProcessIds\": " + IntArray(outcome.TrackedProcessIds) + ",");
            builder.AppendLine("  \"remainingProcessIds\": " + IntArray(outcome.RemainingProcessIds) + ",");
            if (outcome.Dialog == null)
            {
                builder.AppendLine("  \"dialog\": null,");
            }
            else
            {
                builder.AppendLine("  \"dialog\": {");
                AppendJsonField(builder, "title", outcome.Dialog.Title, true, "    ");
                AppendJsonField(builder, "className", outcome.Dialog.ClassName, true, "    ");
                builder.AppendLine("    \"processId\": " + outcome.Dialog.ProcessId.ToString(CultureInfo.InvariantCulture) + ",");
                AppendJsonField(builder, "processPath", outcome.Dialog.ProcessPath, true, "    ");
                AppendJsonField(builder, "ownerTitle", outcome.Dialog.OwnerTitle, true, "    ");
                builder.AppendLine("    \"ownerDisabled\": " + (outcome.Dialog.OwnerDisabled ? "true" : "false") + ",");
                AppendJsonField(builder, "body", outcome.Dialog.Body, true, "    ");
                builder.AppendLine("    \"bodyLength\": " + outcome.Dialog.BodyLength.ToString(CultureInfo.InvariantCulture) + ",");
                AppendJsonField(builder, "bodySource", outcome.Dialog.BodySource, true, "    ");
                AppendJsonField(builder, "bodyError", outcome.Dialog.BodyError, false, "    ");
                builder.AppendLine("  },");
            }
            builder.AppendLine("  \"log\": [");
            for (int i = 0; i < outcome.Log.Count; i++)
            {
                builder.Append("    " + Quote(outcome.Log[i]));
                builder.AppendLine(i + 1 < outcome.Log.Count ? "," : string.Empty);
            }
            builder.AppendLine("  ]");
            builder.AppendLine("}");
            return builder.ToString();
        }

        private static void AppendJsonField(StringBuilder builder, string name, string value, bool comma)
        {
            AppendJsonField(builder, name, value, comma, "  ");
        }

        private static void AppendJsonField(StringBuilder builder, string name, string value, bool comma, string indent)
        {
            builder.Append(indent + Quote(name) + ": " + Quote(value));
            builder.AppendLine(comma ? "," : string.Empty);
        }

        private static string IntArray(List<int> values)
        {
            if (values == null) return "[]";
            var builder = new StringBuilder("[");
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0) builder.Append(", ");
                builder.Append(values[i].ToString(CultureInfo.InvariantCulture));
            }
            builder.Append("]");
            return builder.ToString();
        }

        private static string BuildText(GuardOutcome outcome)
        {
            var builder = new StringBuilder();
            builder.AppendLine("HeatingMold validation guard report");
            builder.AppendLine("status: " + outcome.Status);
            builder.AppendLine("reason: " + outcome.Reason);
            builder.AppendLine("message: " + outcome.Message);
            if (!string.IsNullOrEmpty(outcome.Detail)) builder.AppendLine("detail: " + outcome.Detail);
            builder.AppendLine("command: " + outcome.CommandLine);
            builder.AppendLine("start: " + outcome.StartTimeUtc);
            builder.AppendLine("root pid: " + outcome.RootProcessId.ToString(CultureInfo.InvariantCulture) +
                " / exit: " + outcome.RootExitCode.ToString(CultureInfo.InvariantCulture) +
                " / exited: " + outcome.RootExited);
            builder.AppendLine("remaining pids: " + IntArray(outcome.RemainingProcessIds));
            builder.AppendLine("tracked pids: " + IntArray(outcome.TrackedProcessIds));
            builder.AppendLine("cleanup: " + (outcome.CleanupStatus ?? "not-required"));
            if (outcome.Dialog != null)
            {
                builder.AppendLine("dialog title: " + outcome.Dialog.Title);
                builder.AppendLine("dialog class: " + outcome.Dialog.ClassName);
                builder.AppendLine("dialog pid: " + outcome.Dialog.ProcessId.ToString(CultureInfo.InvariantCulture));
                builder.AppendLine("dialog path: " + outcome.Dialog.ProcessPath);
                builder.AppendLine("dialog owner: " + outcome.Dialog.OwnerTitle +
                    " / disabled: " + outcome.Dialog.OwnerDisabled);
                builder.AppendLine("body source: " + outcome.Dialog.BodySource +
                    " / length: " + outcome.Dialog.BodyLength.ToString(CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(outcome.Dialog.BodyError))
                    builder.AppendLine("body error: " + outcome.Dialog.BodyError);
                builder.AppendLine("body:");
                builder.AppendLine(outcome.Dialog.Body);
            }
            if (!string.IsNullOrEmpty(outcome.LastStep))
            {
                builder.AppendLine("last step:");
                builder.AppendLine(outcome.LastStep);
            }
            if (outcome.ReportWriteFailed)
                builder.AppendLine("report write failed: " + outcome.ReportWriteError);
            if (outcome.Log != null && outcome.Log.Count > 0)
            {
                builder.AppendLine("log:");
                for (int i = 0; i < outcome.Log.Count; i++) builder.AppendLine("  " + outcome.Log[i]);
            }
            return builder.ToString();
        }

        private static string Quote(string value)
        {
            if (value == null) return "null";
            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < ' ' || c == '\u2028' || c == '\u2029')
                            builder.Append("\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            builder.Append(c);
                        break;
                }
            }
            builder.Append('"');
            return builder.ToString();
        }

        // Window handles / process APIs -------------------------------------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public IntPtr MinimumWorkingSetSize;
            public IntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public IntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public IntPtr ProcessMemoryLimit;
            public IntPtr JobMemoryLimit;
            public IntPtr PeakProcessMemoryUsed;
            public IntPtr PeakJobMemoryUsed;
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObjectW(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length, out uint returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(
            IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);

        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "QueryInformationJobObject")]
        private static extern bool QueryInformationJobObjectExtended(
            IntPtr job, int infoClass, out JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length, out uint returnLength);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessW(
            string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
            bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory,
            ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder exeName, ref int size);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetClassNameW(IntPtr hwnd, StringBuilder buffer, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder buffer, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeoutW(
            IntPtr hwnd, uint message, IntPtr wParam, StringBuilder lParam,
            uint flags, uint timeout, out IntPtr result);
    }
}
