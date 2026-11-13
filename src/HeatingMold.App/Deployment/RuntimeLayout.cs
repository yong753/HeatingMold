using System;
using System.IO;
using System.Text.Json;

namespace HeatingMold.App.Deployment
{
    /// <summary>
    /// 배포 패키지의 deployment.json 내용.
    /// 경로는 모두 패키지 루트 기준 상대 경로다. 절대 경로/상위 탈출은 허용하지 않는다.
    /// </summary>
    public sealed class DeploymentManifest
    {
        public int SchemaVersion { get; set; }
        public string Layout { get; set; }
        public string ProductName { get; set; }
        public string PackageVersion { get; set; }
        public string BuiltUtc { get; set; }
        public string RuntimeIdentifier { get; set; }
        public string OcctResources { get; set; }
        public string ExampleModel { get; set; }
        public string AppExecutable { get; set; }
    }

    public sealed class DeploymentLayoutException : Exception
    {
        public DeploymentLayoutException(string message) : base(message) { }
    }

    /// <summary>
    /// 실행 폴더에 deployment.json 이 있으면 "배포 모드"로 동작한다.
    ///
    /// 배포 모드에서는 상위 폴더/개발 저장소를 절대 탐색하지 않는다. 개발 PC 에서 우연히
    /// 동작하고 고객 PC 에서만 실패하는 상황을 만들지 않기 위해서다. 반대로 deployment.json 이
    /// 없으면 기존 개발 동작(third_party 탐색)을 그대로 유지한다.
    /// </summary>
    public static class RuntimeLayout
    {
        public const string ManifestFileName = "deployment.json";
        private const int SupportedSchemaVersion = 1;
        private const string PackagedLayoutName = "packaged";

        private static bool _initialized;
        private static DeploymentManifest _manifest;
        private static string _baseDirectory;
        private static string _occtResourceDirectory;

        public static bool IsPackaged { get { return _manifest != null; } }

        public static DeploymentManifest Manifest { get { return _manifest; } }

        public static string BaseDirectory
        {
            get { return _baseDirectory ?? AppContext.BaseDirectory; }
        }

        public static string ManifestPath
        {
            get { return Path.Combine(BaseDirectory, ManifestFileName); }
        }

        /// <summary>OCCT 런타임 리소스(src 폴더 상당). 배포 모드에서만 유효하다.</summary>
        public static string OcctResourceDirectory
        {
            get
            {
                Initialize();
                return _occtResourceDirectory;
            }
        }

        /// <summary>
        /// deployment.json 을 읽는다. 없으면 개발 모드(false). 있는데 깨졌으면 명시적으로 실패한다.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            _baseDirectory = AppContext.BaseDirectory;
            _manifest = null;
            _occtResourceDirectory = null;

            string manifestPath = Path.Combine(_baseDirectory, ManifestFileName);
            if (!File.Exists(manifestPath)) return;

            DeploymentManifest manifest;
            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                manifest = JsonSerializer.Deserialize<DeploymentManifest>(File.ReadAllText(manifestPath), options);
            }
            catch (Exception ex)
            {
                throw new DeploymentLayoutException(
                    "배포 설정 파일을 읽지 못했습니다.\n  " + manifestPath + "\n  " + ex.Message);
            }

            if (manifest == null)
                throw new DeploymentLayoutException("배포 설정 파일이 비어 있습니다.\n  " + manifestPath);

            if (manifest.SchemaVersion != SupportedSchemaVersion)
                throw new DeploymentLayoutException(
                    "배포 설정 파일의 형식 버전이 맞지 않습니다. (기대 " + SupportedSchemaVersion +
                    ", 실제 " + manifest.SchemaVersion + ")\n  " + manifestPath);

            if (!string.Equals(manifest.Layout, PackagedLayoutName, StringComparison.OrdinalIgnoreCase))
                throw new DeploymentLayoutException(
                    "배포 설정 파일의 layout 값이 올바르지 않습니다. (기대 \"" + PackagedLayoutName +
                    "\", 실제 \"" + (manifest.Layout ?? "") + "\")\n  " + manifestPath);

            // 배포 모드 확정. 이후 경로는 모두 패키지 안에서만 해석한다.
            _manifest = manifest;

            string occt = ResolveInsidePackage(manifest.OcctResources, "occtResources", manifestPath);
            if (string.IsNullOrEmpty(occt) || !Directory.Exists(occt))
                throw new DeploymentLayoutException(
                    "배포 패키지의 OCCT 리소스 폴더를 찾을 수 없습니다.\n  " + (occt ?? "(미지정)") +
                    "\n  압축을 풀 때 일부 파일이 빠졌는지 확인하세요.");

            _occtResourceDirectory = occt;
        }

        /// <summary>배포 모드에서 기본 예제 모델 경로. 없으면 null.</summary>
        public static string FindExampleModel()
        {
            Initialize();
            if (_manifest == null) return null;

            string path;
            try
            {
                path = ResolveInsidePackage(_manifest.ExampleModel, "exampleModel", ManifestPath);
            }
            catch (DeploymentLayoutException)
            {
                return null;
            }
            return (!string.IsNullOrEmpty(path) && File.Exists(path)) ? path : null;
        }

        /// <summary>
        /// 상대 경로를 패키지 루트 기준 절대 경로로 바꾼다.
        /// 절대 경로, 빈 값, 패키지 밖으로 나가는 경로는 명시적으로 거부한다.
        /// </summary>
        private static string ResolveInsidePackage(string relative, string fieldName, string manifestPath)
        {
            if (string.IsNullOrWhiteSpace(relative)) return null;

            string normalized = relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalized))
                throw new DeploymentLayoutException(
                    "배포 설정의 " + fieldName + " 값은 절대 경로를 쓸 수 없습니다.\n  \"" + relative + "\"\n  " + manifestPath);

            string full = Path.GetFullPath(Path.Combine(BaseDirectory, normalized));
            if (!IsInsideDirectory(full, BaseDirectory))
                throw new DeploymentLayoutException(
                    "배포 설정의 " + fieldName + " 값이 패키지 폴더 밖을 가리킵니다.\n  \"" + relative + "\"\n  " + manifestPath);

            return full;
        }

        private static bool IsInsideDirectory(string candidate, string directory)
        {
            string root = directory;
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                root += Path.DirectorySeparatorChar;

            if (candidate.Length < root.Length) return false;
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
    }
}
