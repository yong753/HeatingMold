using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HeatingMold.App
{
    /// <summary>설치 폴더와 개발 저장소에서 OCCT 실행 환경을 구성합니다.</summary>
    public static class OcctEnv
    {
        private const string PackagedNativeDirectory = "native";
        private const string PackagedOcctDirectory = "occt";
        private const string PackagedThirdPartyDirectory = "3rdparty";
        private const string OcctFolderName = "opencascade-7.9.3-vc14-64";
        private const string ThirdPartyFolderName = "3rdparty-vc14-64";

        /// <summary>개발 실행에서 발견한 저장소 루트입니다.</summary>
        public static string RepoRoot { get; private set; }

        /// <summary>현재 실행에 사용할 OCCT 루트입니다.</summary>
        public static string CasRoot { get; private set; }

        /// <summary>설치 리소스를 우선하고 없으면 개발 저장소의 리소스를 사용해 환경을 설정합니다.</summary>
        public static void Setup()
        {
            string appDirectory = Path.GetFullPath(AppContext.BaseDirectory);
            RepoRoot = FindDevelopmentRepositoryRoot(appDirectory);
            CasRoot = ResolveCasRoot(appDirectory, RepoRoot);

            IReadOnlyList<string> runtimeDirectories = CollectRuntimeDirectories(appDirectory, RepoRoot);
            AddRuntimeDirectoriesToPath(runtimeDirectories);
            SetOcctResourcePath(CasRoot);
            SetViewerLogPath();
        }

        /// <summary>패키지 리소스를 먼저 찾고 없으면 개발 저장소의 Mold.stp를 반환합니다.</summary>
        public static string FindMoldStp() => ResolveMoldStp(AppContext.BaseDirectory, RepoRoot);

        /// <summary>실행 폴더와 개발 저장소에서 사용할 수 있는 모델 파일을 찾습니다.</summary>
        internal static string ResolveMoldStp(string appDirectory, string repositoryRoot)
        {
            string packagedModelPath = Path.Combine(appDirectory, "resource", "Mold.stp");
            if (File.Exists(packagedModelPath))
                return packagedModelPath;

            if (string.IsNullOrWhiteSpace(repositoryRoot))
                return null;

            string developmentModelPath = Path.Combine(repositoryRoot, "resource", "Mold.stp");
            return File.Exists(developmentModelPath) ? developmentModelPath : null;
        }

        /// <summary>패키지와 개발 저장소에 있는 기존 Native DLL 폴더를 순서대로 모읍니다.</summary>
        internal static IReadOnlyList<string> CollectRuntimeDirectories(string appDirectory, string repositoryRoot)
        {
            var directories = new List<string>();
            AddDirectory(directories, appDirectory);

            string packagedNativeRoot = Path.Combine(appDirectory, PackagedNativeDirectory);
            AddDirectory(directories, Path.Combine(packagedNativeRoot, PackagedOcctDirectory, "bin"));
            CollectDllDirectories(
                Path.Combine(packagedNativeRoot, PackagedThirdPartyDirectory),
                depth: 0,
                directories);

            if (!string.IsNullOrWhiteSpace(repositoryRoot))
            {
                string developmentOcctRoot = Path.Combine(repositoryRoot, "third_party", OcctFolderName);
                AddDirectory(directories, Path.Combine(developmentOcctRoot, "win64", "vc14", "bin"));
                CollectDllDirectories(
                    Path.Combine(repositoryRoot, "third_party", ThirdPartyFolderName),
                    depth: 0,
                    directories);
            }

            return directories.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        /// <summary>실행 폴더의 OCCT를 우선하고 개발 저장소의 OCCT를 보조 경로로 찾습니다.</summary>
        private static string ResolveCasRoot(string appDirectory, string repositoryRoot)
        {
            string packagedCasRoot = Path.Combine(appDirectory, PackagedNativeDirectory, PackagedOcctDirectory);
            if (Directory.Exists(Path.Combine(packagedCasRoot, "src")))
                return packagedCasRoot;

            if (string.IsNullOrWhiteSpace(repositoryRoot))
                return null;

            string developmentCasRoot = Path.Combine(repositoryRoot, "third_party", OcctFolderName);
            return Directory.Exists(Path.Combine(developmentCasRoot, "src"))
                ? developmentCasRoot
                : null;
        }

        /// <summary>실행 폴더의 상위에서 OCCT 개발 저장소를 찾습니다.</summary>
        private static string FindDevelopmentRepositoryRoot(string appDirectory)
        {
            var directory = new DirectoryInfo(appDirectory);
            while (directory is not null)
            {
                string environmentBatch = Path.Combine(
                    directory.FullName,
                    "third_party",
                    OcctFolderName,
                    "env.bat");
                if (File.Exists(environmentBatch))
                    return directory.FullName;

                directory = directory.Parent;
            }

            return null;
        }

        /// <summary>폴더를 재귀 탐색해 DLL을 가진 경로를 실행 검색 목록에 추가합니다.</summary>
        private static void CollectDllDirectories(string root, int depth, List<string> directories)
        {
            if (depth > 6 || !Directory.Exists(root))
                return;

            try
            {
                if (Directory.EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly).Any())
                    AddDirectory(directories, root);

                foreach (string childDirectory in Directory.EnumerateDirectories(root))
                    CollectDllDirectories(childDirectory, depth + 1, directories);
            }
            catch (IOException)
            {
                // 접근할 수 없는 한 하위 폴더가 다른 실행 경로 탐색을 막지 않게 합니다.
            }
            catch (UnauthorizedAccessException)
            {
                // 접근할 수 없는 한 하위 폴더가 다른 실행 경로 탐색을 막지 않게 합니다.
            }
        }

        /// <summary>존재하는 폴더를 중복 없이 실행 검색 목록에 추가합니다.</summary>
        private static void AddDirectory(List<string> directories, string path)
        {
            if (!Directory.Exists(path))
                return;

            string fullPath = Path.GetFullPath(path);
            if (!directories.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                directories.Add(fullPath);
        }

        /// <summary>현재 프로세스 PATH 앞에 패키지 Native DLL 경로를 중복 없이 추가합니다.</summary>
        private static void AddRuntimeDirectoriesToPath(IReadOnlyList<string> directories)
        {
            string existingPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process) ?? string.Empty;
            var knownDirectories = new HashSet<string>(
                existingPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);
            string[] newDirectories = directories
                .Where(directory => knownDirectories.Add(directory))
                .ToArray();

            if (newDirectories.Length == 0)
                return;

            Environment.SetEnvironmentVariable(
                "PATH",
                string.Join(Path.PathSeparator, newDirectories.Concat([existingPath]).Where(value => value.Length > 0)),
                EnvironmentVariableTarget.Process);
        }

        /// <summary>현재 OCCT 설치의 리소스 폴더를 프로세스 환경 변수에 등록합니다.</summary>
        private static void SetOcctResourcePath(string casRoot)
        {
            if (string.IsNullOrWhiteSpace(casRoot))
            {
                Environment.SetEnvironmentVariable(
                    "CSF_OCCTResourcePath",
                    null,
                    EnvironmentVariableTarget.Process);
                return;
            }

            string resourcePath = Path.Combine(casRoot, "src");
            if (Directory.Exists(resourcePath))
            {
                Environment.SetEnvironmentVariable(
                    "CSF_OCCTResourcePath",
                    resourcePath,
                    EnvironmentVariableTarget.Process);
            }
            else
            {
                Environment.SetEnvironmentVariable(
                    "CSF_OCCTResourcePath",
                    null,
                    EnvironmentVariableTarget.Process);
            }
        }

        /// <summary>뷰어 진단 로그를 사용자 로컬 데이터 폴더에 기록하도록 설정합니다.</summary>
        private static void SetViewerLogPath()
        {
            try
            {
                string localDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(localDataRoot))
                    localDataRoot = Path.GetTempPath();

                string logDirectory = Path.Combine(localDataRoot, "HeatingMold", "Logs");
                Directory.CreateDirectory(logDirectory);
                Environment.SetEnvironmentVariable(
                    "HEATINGMOLD_OCC_LOG",
                    Path.Combine(logDirectory, "occ_viewer_log.txt"),
                    EnvironmentVariableTarget.Process);
            }
            catch (IOException)
            {
                // 로그 경로를 준비하지 못해도 모델 열기와 설계 작업은 계속합니다.
            }
            catch (UnauthorizedAccessException)
            {
                // 로그 경로를 준비하지 못해도 모델 열기와 설계 작업은 계속합니다.
            }
        }
    }
}
