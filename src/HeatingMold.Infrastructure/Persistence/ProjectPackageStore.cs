using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using HeatingMold.Application.Ports.Files;
using HeatingMold.Application.Ports.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Validation;
using HeatingMold.Infrastructure.Persistence.SchemaV1;

namespace HeatingMold.Infrastructure.Persistence;

/// <summary>설계 JSON과 원본 STEP을 무결성 검증이 포함된 ZIP 패키지로 읽고 씁니다.</summary>
public sealed class ProjectPackageStore : IProjectPackageStore
{
    private const int PackageFormatVersion = 1;
    private const long MaximumProjectJsonBytes = 64 * 1024 * 1024;
    private const long MaximumSourceModelBytes = ModelSource.MaximumSourceModelBytes;
    private const string ManifestEntryName = "manifest.json";
    private const string ProjectEntryName = "project.json";
    private const string SourceEntryName = "source/model.step";
    private readonly JsonSerializerOptions _jsonOptions = PackageJsonOptions.Create();
    private readonly long _inMemoryThresholdBytes;
    private readonly string? _temporaryDirectory;

    /// <summary>패키지 복원에 사용할 메모리 기준과 세션 임시 저장 위치를 설정합니다.</summary>
    public ProjectPackageStore(
        long inMemoryThresholdBytes = ModelSource.DefaultInMemoryStorageThresholdBytes,
        string? temporaryDirectory = null)
    {
        if (inMemoryThresholdBytes < 0 || inMemoryThresholdBytes > MaximumSourceModelBytes)
            throw new ArgumentOutOfRangeException(nameof(inMemoryThresholdBytes));

        _inMemoryThresholdBytes = inMemoryThresholdBytes;
        _temporaryDirectory = string.IsNullOrWhiteSpace(temporaryDirectory)
            ? null
            : Path.GetFullPath(temporaryDirectory);
    }

    /// <summary>ZIP 구성, SHA-256 해시와 Core 참조를 검증해 프로젝트 패키지를 복원합니다.</summary>
    public OperationResult<ProjectPackageReadResult> Read(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Failure<ProjectPackageReadResult>("InvalidPath", "프로젝트 패키지 경로가 비어 있습니다.");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
            ZipArchiveEntry manifestEntry = RequireEntry(archive, ManifestEntryName, 64 * 1024);
            ZipArchiveEntry projectEntry = RequireEntry(archive, ProjectEntryName, MaximumProjectJsonBytes);
            ZipArchiveEntry sourceEntry = RequireEntry(archive, SourceEntryName, MaximumSourceModelBytes);
            ValidateEntrySet(archive);

            PackageManifest manifest = Deserialize<PackageManifest>(ReadEntry(manifestEntry, cancellationToken))
                ?? throw new InvalidDataException("패키지 manifest가 비어 있습니다.");
            if (manifest.PackageVersion != PackageFormatVersion || manifest.ProjectSchemaVersion != 1)
                return Failure<ProjectPackageReadResult>("PackageVersionUnsupported", "지원하지 않는 프로젝트 패키지 또는 스키마 버전입니다.");
            if (!HasValidManifestMetadata(manifest))
                return Failure<ProjectPackageReadResult>("PackageIntegrityMismatch", "패키지 manifest의 형식, 단위 또는 내부 경로가 올바르지 않습니다.");

            PackageFileReference projectFile = manifest.Project!;
            PackageFileReference sourceFile = manifest.Source!;
            byte[] projectBytes = ReadEntry(projectEntry, cancellationToken);
            if (!HashMatches(projectBytes, projectFile.Sha256))
            {
                return Failure<ProjectPackageReadResult>("PackageIntegrityMismatch", "패키지 내부 파일의 SHA-256 검증에 실패했습니다.");
            }

            ProjectV1Dto projectDto = DeserializeProject(projectBytes);
            DesignProject project = ProjectJsonMapper.ToDomain(projectDto);
            if (project.SchemaVersion != manifest.ProjectSchemaVersion)
                return Failure<ProjectPackageReadResult>("PackageVersionUnsupported", "지원하지 않는 프로젝트 스키마 버전입니다.");
            if (!project.ValidateReferences().IsValid)
                return Failure<ProjectPackageReadResult>("PackageIntegrityMismatch", "프로젝트 내부 참조가 올바르지 않습니다.");
            if (!string.Equals(project.SourceModel.SourceSha256, sourceFile.Sha256, StringComparison.OrdinalIgnoreCase))
                return Failure<ProjectPackageReadResult>("SourceIdentityMismatch", "프로젝트가 참조하는 원본 모델과 패키지 원본이 다릅니다.");

            using Stream sourceStream = sourceEntry.Open();
            ModelSource sourceModel = ModelSource.ReadFromStream(
                project.SourceModel,
                sourceStream,
                sourceEntry.Length,
                cancellationToken,
                _inMemoryThresholdBytes,
                _temporaryDirectory);
            var result = new ProjectPackageReadResult(project, sourceModel);
            return OperationResult<ProjectPackageReadResult>.Success(result);
        }
        catch (OperationCanceledException)
        {
            return Failure<ProjectPackageReadResult>("OperationCancelled", "프로젝트 패키지 읽기가 취소되었습니다.");
        }
        catch (Exception exception) when (
            exception is InvalidDataException or JsonException or ArgumentException or CryptographicException or FormatException)
        {
            return Failure<ProjectPackageReadResult>("PackageIntegrityMismatch", exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure<ProjectPackageReadResult>("ProjectPackageReadFailed", exception.Message);
        }
    }

    /// <summary>검증된 설계와 원본 STEP을 임시 파일에서 완성한 뒤 패키지 경로를 교체합니다.</summary>
    public OperationResult<ProjectPackageWriteReceipt> Write(
        string path,
        ProjectSaveSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(path))
            return Failure<ProjectPackageWriteReceipt>("InvalidPath", "프로젝트 패키지 경로가 비어 있습니다.");
        if (!snapshot.Project.ValidateReferences().IsValid)
            return Failure<ProjectPackageWriteReceipt>("InvalidProjectReferences", "참조 검증에 실패한 설계는 저장할 수 없습니다.");
        if (snapshot.Project.SourceModel.SourceSha256 != snapshot.SourceModel.Identity.SourceSha256)
            return Failure<ProjectPackageWriteReceipt>("SourceIdentityMismatch", "프로젝트와 원본 모델의 SHA-256이 다릅니다.");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProjectV1Dto projectDto = ProjectJsonMapper.ToDto(snapshot.Project);
            byte[] projectBytes = JsonSerializer.SerializeToUtf8Bytes(projectDto, _jsonOptions);
            ModelSource sourceModel = snapshot.SourceModel;
            if (projectBytes.LongLength > MaximumProjectJsonBytes || sourceModel.LengthBytes > MaximumSourceModelBytes)
                return Failure<ProjectPackageWriteReceipt>("PackagePayloadTooLarge", "프로젝트 또는 원본 모델이 허용한 패키지 크기를 넘었습니다.");

            var manifest = new PackageManifest(
                "HeatingMoldProject",
                PackageFormatVersion,
                snapshot.Project.SchemaVersion,
                new PackageUnits("mm", "mm2"),
                new PackageFileReference(ProjectEntryName, ComputeSha256(projectBytes)),
                new PackageFileReference(SourceEntryName, sourceModel.Identity.SourceSha256),
                CreateProducer(snapshot.Project.SourceModel));
            byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, _jsonOptions);
            string fullPath = Path.GetFullPath(path.Trim());
            string? directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory))
                return Failure<ProjectPackageWriteReceipt>("InvalidPath", "저장 경로에 상위 폴더가 없습니다.");
            Directory.CreateDirectory(directory);

            string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            long packageLength;
            string packageHash;
            try
            {
                using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        WriteEntry(archive, ManifestEntryName, manifestBytes, cancellationToken);
                        WriteEntry(archive, ProjectEntryName, projectBytes, cancellationToken);
                        WriteEntry(archive, SourceEntryName, sourceModel, cancellationToken);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    file.Flush(flushToDisk: true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    ValidateWrittenPackage(temporaryPath, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (
                    exception is InvalidDataException or JsonException or ArgumentException or CryptographicException or FormatException)
                {
                    return Failure<ProjectPackageWriteReceipt>("PackageValidationFailed", exception.Message);
                }

                (packageLength, packageHash) = ComputeFileMetadata(temporaryPath, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                ReplaceFile(temporaryPath, fullPath);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }

            return OperationResult<ProjectPackageWriteReceipt>.Success(
                new ProjectPackageWriteReceipt(fullPath, snapshot.Project.Revision, packageLength, packageHash));
        }
        catch (OperationCanceledException)
        {
            return Failure<ProjectPackageWriteReceipt>("OperationCancelled", "프로젝트 패키지 저장이 취소되었습니다.");
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
            ArgumentException or InvalidOperationException)
        {
            return Failure<ProjectPackageWriteReceipt>("ProjectPackageWriteFailed", exception.Message);
        }
    }

    private T? Deserialize<T>(byte[] content) => JsonSerializer.Deserialize<T>(content, _jsonOptions);

    private ProjectV1Dto DeserializeProject(byte[] content)
    {
        using JsonDocument document = JsonDocument.Parse(
            content,
            new JsonDocumentOptions { MaxDepth = _jsonOptions.MaxDepth });
        PackageJsonSchemaValidator.Validate<ProjectV1Dto>(document.RootElement, _jsonOptions);
        return document.RootElement.Deserialize<ProjectV1Dto>(_jsonOptions)
            ?? throw new InvalidDataException("프로젝트 JSON이 비어 있습니다.");
    }

    private static void ValidateEntrySet(ZipArchive archive)
    {
        string[] names = archive.Entries.Select(entry => entry.FullName).ToArray();
        string[] expectedNames = [ManifestEntryName, ProjectEntryName, SourceEntryName];
        if (names.Length != expectedNames.Length ||
            names.Distinct(StringComparer.Ordinal).Count() != expectedNames.Length ||
            !names.ToHashSet(StringComparer.Ordinal).SetEquals(expectedNames))
        {
            throw new InvalidDataException("패키지에는 manifest, project, 원본 모델 항목만 각각 하나씩 있어야 합니다.");
        }
    }

    private static bool HasValidManifestMetadata(PackageManifest manifest)
    {
        if (!string.Equals(manifest.FileType, "HeatingMoldProject", StringComparison.Ordinal) ||
            manifest.Units is not { Length: "mm", Area: "mm2" } ||
            manifest.Project is not { Path: ProjectEntryName } projectFile ||
            manifest.Source is not { Path: SourceEntryName } sourceFile ||
            manifest.Producer is not { } producer)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(producer.ApplicationVersion) &&
            !string.IsNullOrWhiteSpace(producer.GeometryVersion) &&
            IsSha256(projectFile.Sha256) &&
            IsSha256(sourceFile.Sha256);
    }

    private static PackageProducer CreateProducer(ModelIdentity identity)
    {
        string applicationVersion = typeof(ProjectPackageStore).Assembly.GetName().Version?.ToString() ?? "unknown";
        string geometryVersion = $"{identity.ImporterName}/{identity.ImporterVersion}; face-fingerprint/{identity.FaceFingerprintVersion}";
        return new PackageProducer(applicationVersion, geometryVersion);
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string ComputeSha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static ZipArchiveEntry RequireEntry(ZipArchive archive, string name, long maximumLength)
    {
        ZipArchiveEntry[] matches = archive.Entries.Where(entry => entry.FullName == name).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException($"패키지 항목 {name}이 없거나 중복되었습니다.");
        if (matches[0].Length < 0 || matches[0].Length > maximumLength)
            throw new InvalidDataException($"패키지 항목 {name}의 크기가 허용 범위를 벗어났습니다.");
        return matches[0];
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        using Stream input = entry.Open();
        byte[] content = new byte[checked((int)entry.Length)];
        int bytesRead = 0;
        byte[] buffer = new byte[81920];
        while (bytesRead < content.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int requestedCount = Math.Min(buffer.Length, content.Length - bytesRead);
            int read = input.Read(buffer, 0, requestedCount);
            if (read == 0)
                break;

            Buffer.BlockCopy(buffer, 0, content, bytesRead, read);
            bytesRead += read;
        }

        if (bytesRead != content.Length)
            throw new InvalidDataException($"패키지 항목 {entry.FullName}이 잘렸습니다.");

        cancellationToken.ThrowIfCancellationRequested();
        if (input.ReadByte() != -1)
            throw new InvalidDataException($"패키지 항목 {entry.FullName}의 실제 크기가 선언 크기보다 큽니다.");

        return content;
    }

    private static void WriteEntry(
        ZipArchive archive,
        string name,
        byte[] content,
        CancellationToken cancellationToken) =>
        WriteEntry(archive, name, content.AsMemory(), cancellationToken);

    private static void WriteEntry(
        ZipArchive archive,
        string name,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using Stream output = entry.Open();
        const int bufferSize = 81_920;
        for (int offset = 0; offset < content.Length; offset += bufferSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(bufferSize, content.Length - offset);
            output.Write(content.Span.Slice(offset, count));
        }
    }

    private static void WriteEntry(
        ZipArchive archive,
        string name,
        ModelSource sourceModel,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using Stream output = entry.Open();
        using Stream input = sourceModel.OpenRead();
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81_920];
        long remainingLength = sourceModel.LengthBytes;
        while (remainingLength > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int requestedCount = (int)Math.Min(buffer.Length, remainingLength);
            int bytesRead = input.Read(buffer, 0, requestedCount);
            if (bytesRead == 0)
                throw new EndOfStreamException("원본 모델 저장소가 캡처한 길이보다 짧습니다.");

            output.Write(buffer, 0, bytesRead);
            hash.AppendData(buffer, 0, bytesRead);
            remainingLength -= bytesRead;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (input.ReadByte() != -1)
            throw new InvalidDataException("원본 모델 저장소가 캡처한 길이보다 깁니다.");

        byte[] actualHash = hash.GetHashAndReset();
        byte[] expectedHash = Convert.FromHexString(sourceModel.Identity.SourceSha256);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            throw new InvalidDataException("원본 모델 저장소의 SHA-256이 모델 신원과 다릅니다.");
    }

    private static void ReplaceFile(string temporaryPath, string destinationPath)
    {
        if (File.Exists(destinationPath))
            File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null);
        else
            File.Move(temporaryPath, destinationPath);
    }

    private static bool HashMatches(byte[] content, string? expectedHash)
    {
        if (!IsSha256(expectedHash))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(content),
            Convert.FromHexString(expectedHash!));
    }

    /// <summary>확정 전 임시 패키지의 구조와 원본 해시를 스트리밍으로 검증합니다.</summary>
    private void ValidateWrittenPackage(string path, CancellationToken cancellationToken)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
        ZipArchiveEntry manifestEntry = RequireEntry(archive, ManifestEntryName, 64 * 1024);
        ZipArchiveEntry projectEntry = RequireEntry(archive, ProjectEntryName, MaximumProjectJsonBytes);
        ZipArchiveEntry sourceEntry = RequireEntry(archive, SourceEntryName, MaximumSourceModelBytes);
        ValidateEntrySet(archive);

        PackageManifest manifest = Deserialize<PackageManifest>(ReadEntry(manifestEntry, cancellationToken))
            ?? throw new InvalidDataException("패키지 manifest가 비어 있습니다.");
        if (manifest.PackageVersion != PackageFormatVersion || manifest.ProjectSchemaVersion != 1 ||
            !HasValidManifestMetadata(manifest))
        {
            throw new InvalidDataException("저장된 패키지 manifest의 형식 또는 메타데이터가 올바르지 않습니다.");
        }

        PackageFileReference projectFile = manifest.Project!;
        PackageFileReference sourceFile = manifest.Source!;
        byte[] projectBytes = ReadEntry(projectEntry, cancellationToken);
        if (!HashMatches(projectBytes, projectFile.Sha256))
            throw new InvalidDataException("저장된 프로젝트 JSON의 SHA-256 검증에 실패했습니다.");

        ProjectV1Dto projectDto = DeserializeProject(projectBytes);
        DesignProject project = ProjectJsonMapper.ToDomain(projectDto);
        if (project.SchemaVersion != manifest.ProjectSchemaVersion || !project.ValidateReferences().IsValid)
            throw new InvalidDataException("저장된 프로젝트 데이터의 버전 또는 참조가 올바르지 않습니다.");
        if (!string.Equals(project.SourceModel.SourceSha256, sourceFile.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("저장된 프로젝트와 원본 모델의 SHA-256이 다릅니다.");

        using Stream sourceStream = sourceEntry.Open();
        if (!HashMatches(sourceStream, sourceEntry.Length, sourceFile.Sha256, cancellationToken))
            throw new InvalidDataException("저장된 원본 STEP의 길이 또는 SHA-256 검증에 실패했습니다.");
    }

    private static bool HashMatches(
        Stream content,
        long expectedLength,
        string? expectedHash,
        CancellationToken cancellationToken)
    {
        if (!IsSha256(expectedHash))
            return false;

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81_920];
        long bytesRead = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = content.Read(buffer, 0, buffer.Length);
            if (count == 0)
                break;

            bytesRead += count;
            if (bytesRead > expectedLength)
                return false;

            hash.AppendData(buffer, 0, count);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (bytesRead != expectedLength)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            hash.GetHashAndReset(),
            Convert.FromHexString(expectedHash!));
    }

    /// <summary>교체 전 패키지 파일을 청크 단위로 읽어 길이와 SHA-256을 계산합니다.</summary>
    private static (long Length, string Sha256) ComputeFileMetadata(
        string path,
        CancellationToken cancellationToken)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81_920];
        long length = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = input.Read(buffer, 0, buffer.Length);
            if (count == 0)
                break;

            hash.AppendData(buffer, 0, count);
            length += count;
        }

        cancellationToken.ThrowIfCancellationRequested();
        string sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        return (length, sha256);
    }

    private static OperationResult<T> Failure<T>(string code, string evidence) =>
        OperationResult<T>.Failure(
        [new ValidationIssue(code, ValidationSeverity.Error, $"Infrastructure.{code}", [], "project-package-v1", evidence: [evidence])]);

    /// <summary>.hmproj v1 패키지의 버전, 단위, 파일 무결성과 생성 정보를 보관합니다.</summary>
    private sealed record PackageManifest(
        string FileType,
        int PackageVersion,
        int ProjectSchemaVersion,
        PackageUnits? Units,
        PackageFileReference? Project,
        PackageFileReference? Source,
        PackageProducer? Producer);

    /// <summary>패키지 안에서 사용하는 길이와 면적 단위입니다.</summary>
    private sealed record PackageUnits(string Length, string Area);

    /// <summary>패키지 내부 파일 경로와 SHA-256을 함께 기록합니다.</summary>
    private sealed record PackageFileReference(string Path, string Sha256);

    /// <summary>저장 애플리케이션과 형상 가져오기 구현의 버전 정보입니다.</summary>
    private sealed record PackageProducer(string ApplicationVersion, string GeometryVersion);
}
