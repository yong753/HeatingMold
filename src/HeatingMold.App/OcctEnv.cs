using System;
using System.Collections.Generic;
using System.IO;
using HeatingMold.App.Deployment;

namespace HeatingMold.App
{
    /// <summary>
    /// OCCT prebuilt(third_party) 위치 탐색 + 프로세스 환경 구성.
    /// env.bat의 3rdparty PATH가 상대 경로라 CWD 의존(2.1 교훈) → 절대 경로로 직접 구성.
    ///
    /// 배포 패키지(deployment.json 존재)에서는 상위 폴더·개발 저장소를 탐색하지 않는다.
    /// 실행 폴더에 평탄 배치된 DLL 과 occt-resources 만 사용한다.
    /// </summary>
    public static class OcctEnv
    {
        public static string RepoRoot { get; private set; }
        public static string CasRoot { get; private set; }

        public static void Setup()
        {
            RuntimeLayout.Initialize();
            if (RuntimeLayout.IsPackaged)
            {
                SetupPackaged();
                return;
            }

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

        /// <summary>
        /// 배포 패키지 모드. 네이티브 DLL 은 실행 폴더에 평탄 배치, OCCT 리소스는
        /// occt-resources 폴더에 있다. 개발 저장소나 전역 설치를 조회하지 않는다.
        /// </summary>
        private static void SetupPackaged()
        {
            string baseDir = RuntimeLayout.BaseDirectory;

            string old = Environment.GetEnvironmentVariable("PATH") ?? "";
            Environment.SetEnvironmentVariable("PATH", baseDir + ";" + old, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("CSF_OCCTResourcePath",
                RuntimeLayout.OcctResourceDirectory, EnvironmentVariableTarget.Process);

            RuntimeDiagnostics.Initialize();
            RuntimeDiagnostics.SetOccLogEnvironment();
            RuntimeDiagnostics.WriteLine("배포 모드로 시작합니다. 앱 폴더: " + baseDir);
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

        /// <summary>
        /// 기본 예제 모델 경로. Mold_Cover_Trim.step = 제품 STEP 의 YZ 외곽을 100 mm 확장한
        /// 프리즘으로 자른 Mold 셸(열린 셸 1개 / 80면 / 958,841.060 mm²). 가열면은 +X 로
        /// 사용자 확정. 원본 STEP 좌표는 이동하지 않고, 작업 프레임만 예제 프리셋으로 고정한다.
        /// </summary>
        public static string FindMoldStp()
        {
            if (RuntimeLayout.IsPackaged)
            {
                string packaged = RuntimeLayout.FindExampleModel();
                if (!string.IsNullOrEmpty(packaged)) return packaged;
            }

            string[] candidates = { "Mold_Cover_Trim.step" };
            foreach (string name in candidates)
            {
                if (RepoRoot != null)
                {
                    string p = Path.Combine(RepoRoot, "resource", name);
                    if (File.Exists(p)) return p;
                }

                // 배포 산출물(x64\Release\resource)에서도 찾는다.
                string q = Path.Combine(AppContext.BaseDirectory, "resource", name);
                if (File.Exists(q)) return q;
            }
            return null;
        }
    }
}
