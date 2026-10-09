using System;
using System.Collections.Generic;
using System.IO;

namespace HeatingMold.App
{
    /// <summary>
    /// OCCT prebuilt(third_party) 위치 탐색 + 프로세스 환경 구성.
    /// env.bat의 3rdparty PATH가 상대 경로라 CWD 의존(2.1 교훈) → 절대 경로로 직접 구성.
    /// </summary>
    public static class OcctEnv
    {
        public static string RepoRoot { get; private set; }
        public static string CasRoot { get; private set; }

        public static void Setup()
        {
            var di = new DirectoryInfo(AppContext.BaseDirectory);
            while (di != null && !File.Exists(Path.Combine(di.FullName, "third_party", "opencascade-7.9.3-vc14-64", "env.bat")))
                di = di.Parent;
            if (di == null) return;

            RepoRoot = di.FullName;
            CasRoot = Path.Combine(RepoRoot, "third_party", "opencascade-7.9.3-vc14-64");

            var dirs = new List<string> { Path.Combine(CasRoot, "win64", "vc14", "bin") };
            string tp = Path.Combine(RepoRoot, "third_party", "3rdparty-vc14-64");
            if (Directory.Exists(tp)) CollectDllDirs(tp, 0, dirs);

            string old = Environment.GetEnvironmentVariable("PATH") ?? "";
            Environment.SetEnvironmentVariable("PATH", string.Join(";", dirs) + ";" + old, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("CSF_OCCTResourcePath", Path.Combine(CasRoot, "src"), EnvironmentVariableTarget.Process);

            // 뷰어 진단 로그 (OCCT 메시지 + GL 컨텍스트 정보) — 빌드 출력 폴더에 기록
            try
            {
                string logDir = Path.Combine(RepoRoot, "x64", "Release");
                Directory.CreateDirectory(logDir);
                Environment.SetEnvironmentVariable("HEATINGMOLD_OCC_LOG",
                    Path.Combine(logDir, "occ_viewer_log.txt"), EnvironmentVariableTarget.Process);
            }
            catch (Exception) { }
        }

        private static void CollectDllDirs(string dir, int depth, List<string> outDirs)
        {
            if (depth > 4) return;
            try
            {
                if (Directory.GetFiles(dir, "*.dll").Length > 0) outDirs.Add(dir);
                foreach (var sub in Directory.GetDirectories(dir))
                    CollectDllDirs(sub, depth + 1, outDirs);
            }
            catch (Exception) { }
        }

        public static string FindMoldStp()
        {
            if (RepoRoot == null) return null;
            string p = Path.Combine(RepoRoot, "resource", "Mold.stp");
            return File.Exists(p) ? p : null;
        }
    }
}
