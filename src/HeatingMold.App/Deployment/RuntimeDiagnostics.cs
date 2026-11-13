using System;
using System.IO;
using System.Text;
using System.Text.Json;
using HeatingMold.App.Deployment;

namespace HeatingMold.App.Deployment
{
    /// <summary>
    /// 실행 진단 로그. 고객 PC 에서 원인을 물어볼 수 있도록 실행마다 폴더를 만든다.
    ///
    ///   %LOCALAPPDATA%\HeatingMold\Logs\&lt;yyyyMMdd-HHmmss&gt;-&lt;pid&gt;\
    ///       app.log            앱/시작 단계 기록
    ///       occ_viewer_log.txt OCCT 메시지 + GL 컨텍스트 정보
    ///       environment.json   실행 환경 스냅샷
    ///
    /// 로그 기록 실패가 앱을 추가로 실패시키면 안 된다. 모든 경로는 예외를 삼킨다.
    /// </summary>
    public static class RuntimeDiagnostics
    {
        private static readonly object Gate = new object();
        private static bool _initialized;
        private static string _sessionDirectory;
        private static string _appLogPath;

        public static string SessionDirectory
        {
            get { Initialize(); return _sessionDirectory; }
        }

        public static string AppLogPath
        {
            get { Initialize(); return _appLogPath; }
        }

        public static void Initialize()
        {
            if (_initialized) return;
            lock (Gate)
            {
                if (_initialized) return;
                _initialized = true;
                try
                {
                    string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    if (string.IsNullOrEmpty(local)) local = Path.GetTempPath();

                    string root = Path.Combine(local, "HeatingMold", "Logs");
                    string name = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Environment.ProcessId;
                    string dir = Path.Combine(root, name);
                    Directory.CreateDirectory(dir);

                    _sessionDirectory = dir;
                    _appLogPath = Path.Combine(dir, "app.log");

                    WriteLine("=== HeatingMold 시작 ===");
                    WriteEnvironmentSnapshot(dir);
                }
                catch (Exception)
                {
                    _sessionDirectory = null;
                    _appLogPath = null;
                }
            }
        }

        /// <summary>OCCT 뷰어 로그 경로를 이번 실행 폴더로 지정한다. 실패해도 무시한다.</summary>
        public static void SetOccLogEnvironment()
        {
            Initialize();
            if (string.IsNullOrEmpty(_sessionDirectory)) return;
            try
            {
                Environment.SetEnvironmentVariable("HEATINGMOLD_OCC_LOG",
                    Path.Combine(_sessionDirectory, "occ_viewer_log.txt"), EnvironmentVariableTarget.Process);
            }
            catch (Exception) { }
        }

        /// <summary>로그 경로를 남긴다. 로그 폴더를 못 만들었으면 null.</summary>
        public static string GetLogHint()
        {
            Initialize();
            return _sessionDirectory;
        }

        public static void WriteLine(string message)
        {
            if (!_initialized) Initialize();
            string path = _appLogPath;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                lock (Gate)
                {
                    File.AppendAllText(path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + (message ?? "") + Environment.NewLine,
                        new UTF8Encoding(false));
                }
            }
            catch (Exception) { }
        }

        public static void WriteStartupFailure(string stage, Exception ex)
        {
            WriteLine("!! 시작 실패: " + (stage ?? ""));
            if (ex != null) WriteLine(ex.ToString());
        }

        /// <summary>오류 대화상자에 붙일 짧은 로그 안내 문구.</summary>
        public static string BuildLogHintText()
        {
            string dir = GetLogHint();
            if (string.IsNullOrEmpty(dir)) return "진단 로그 폴더를 만들지 못했습니다.";
            return "진단 로그: " + dir;
        }

        private static void WriteEnvironmentSnapshot(string directory)
        {
            try
            {
                DeploymentManifest manifest = RuntimeLayout.Manifest;
                var snapshot = new
                {
                    enteredAtUtc = DateTime.UtcNow.ToString("o"),
                    productName = manifest != null ? manifest.ProductName : "HeatingMold",
                    packageVersion = manifest != null ? manifest.PackageVersion : null,
                    packaged = RuntimeLayout.IsPackaged,
                    executablePath = Environment.ProcessPath,
                    baseDirectory = RuntimeLayout.BaseDirectory,
                    occtResources = RuntimeLayout.IsPackaged ? RuntimeLayout.OcctResourceDirectory : null,
                    osVersion = Environment.OSVersion.VersionString,
                    osArchitecture = Environment.Is64BitOperatingSystem ? "x64" : "x86",
                    processArchitecture = Environment.Is64BitProcess ? "x64" : "x86",
                    runtimeVersion = Environment.Version.ToString(),
                    processorCount = Environment.ProcessorCount,
                    currentDirectory = SafeCurrentDirectory(),
                    commandLine = Environment.CommandLine
                };

                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(Path.Combine(directory, "environment.json"),
                    JsonSerializer.Serialize(snapshot, options), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                WriteLine("환경 정보를 기록하지 못했습니다: " + ex.Message);
            }
        }

        private static string SafeCurrentDirectory()
        {
            try { return Environment.CurrentDirectory; }
            catch (Exception) { return null; }
        }
    }
}
