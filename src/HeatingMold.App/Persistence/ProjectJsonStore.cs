using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeatingMold.App.Persistence
{
    /// <summary>
    /// 프로젝트 JSON 저장소. 같은 폴더의 임시 파일에 완전히 쓴 뒤 원자적으로 교체한다.
    /// 저장 실패 시 기존 JSON과 현재 작업 상태는 그대로 남는다.
    /// </summary>
    public static class ProjectJsonStore
    {
        public const string FileExtension = ".json";
        public const int CurrentSchemaVersion = 4;
        private const int MinSupportedSchemaVersion = 1;

        /// <summary>이 앱이 읽을 수 있는 스키마 버전인지. 쓰기는 항상 CurrentSchemaVersion을 쓴다.</summary>
        public static bool IsSupportedSchemaVersion(int version) =>
            version >= MinSupportedSchemaVersion && version <= CurrentSchemaVersion;

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.Strict,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        public static byte[] Serialize(ProjectDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (!IsSupportedSchemaVersion(document.SchemaVersion))
                throw new InvalidOperationException("지원하지 않는 프로젝트 스키마 버전입니다: " + document.SchemaVersion);
            try
            {
                return JsonSerializer.SerializeToUtf8Bytes(document, Options);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "프로젝트 JSON을 만들지 못했습니다. 숫자/경로/열거형 값이 유효한지 확인하세요. " + ex.Message, ex);
            }
        }

        public static ProjectDocument Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("프로젝트 파일 경로가 비어 있습니다.", nameof(path));

            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("프로젝트 파일을 찾을 수 없습니다.", fullPath);

            try
            {
                using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                ProjectDocument document = JsonSerializer.Deserialize<ProjectDocument>(stream, Options);
                if (document == null)
                    throw new InvalidDataException("프로젝트 JSON이 비어 있습니다.");
                if (!string.Equals(document.Format, "HeatingMold.Project", StringComparison.Ordinal))
                    throw new InvalidDataException("HeatingMold 프로젝트 파일이 아닙니다.");
                if (!IsSupportedSchemaVersion(document.SchemaVersion))
                    throw new InvalidDataException("지원하지 않는 프로젝트 스키마 버전입니다: " + document.SchemaVersion);
                return document;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("프로젝트 JSON을 읽지 못했습니다: " + ex.Message, ex);
            }
        }

        public static string ComputeFingerprint(ProjectDocument document)
        {
            ProjectDocument copy = document.CloneForFingerprint();
            byte[] bytes = Serialize(copy);
            return ComputeSha256(bytes);
        }

        public static ProjectSaveResult Save(ProjectDocument document, string path)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("프로젝트 파일 경로가 비어 있습니다.", nameof(path));

            string fullPath = Path.GetFullPath(path);
            if (!string.Equals(Path.GetExtension(fullPath), FileExtension, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("프로젝트 파일 확장자는 .json 이어야 합니다.");

            string directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory))
                throw new InvalidOperationException("프로젝트 파일의 폴더를 확인할 수 없습니다.");

            byte[] bytes = Serialize(document);
            string tempPath = Path.Combine(
                directory,
                "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                using (var stream = new FileStream(
                    tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                if (File.Exists(fullPath))
                    File.Replace(tempPath, fullPath, null, ignoreMetadataErrors: true);
                else
                    File.Move(tempPath, fullPath);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }

            return new ProjectSaveResult(fullPath, bytes.Length, ComputeSha256(bytes));
        }

        public static string ComputeFileSha256(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sha = SHA256.Create();
            return ToHex(sha.ComputeHash(stream));
        }

        public static string TryComputeFileSha256(string path)
        {
            try
            {
                return string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : ComputeFileSha256(path);
            }
            catch
            {
                return null;
            }
        }

        public static string ComputeSha256(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return ToHex(sha.ComputeHash(bytes));
        }

        /// <summary>프로젝트 JSON이 원본 STEP을 덮어쓰지 않게 한다.</summary>
        public static void EnsureNotSourceFile(string projectPath, string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || string.IsNullOrWhiteSpace(sourcePath)) return;
            string project = Path.GetFullPath(projectPath);
            string source = Path.GetFullPath(sourcePath);
            if (string.Equals(project, source, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("원본 STEP 파일과 같은 경로에는 프로젝트를 저장할 수 없습니다.");
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // 원래 저장 예외를 유지한다.
            }
        }

        private static string ToHex(byte[] bytes)
        {
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }

    public sealed class ProjectSaveResult
    {
        public ProjectSaveResult(string fullPath, long byteCount, string sha256)
        {
            FullPath = fullPath;
            ByteCount = byteCount;
            Sha256 = sha256;
        }

        public string FullPath { get; }
        public long ByteCount { get; }
        public string Sha256 { get; }
    }
}
