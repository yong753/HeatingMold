using HeatingMold.Application.Sessions;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;

namespace HeatingMold.Application.Ports.Geometry;

/// <summary>Native 어댑터에 넘길 원본 모델 바이트와 가져오기 신원입니다.</summary>
public sealed class ModelSource : IDisposable
{
    private const int CopyBufferSize = 81_920;
    private readonly object _sync = new();
    private byte[]? _modelBytes;
    private string? _temporaryFilePath;
    private bool _isDisposed;

    /// <summary>메모리에 둘 원본 모델의 기본 최대 크기(byte)입니다.</summary>
    public const long DefaultInMemoryStorageThresholdBytes = 64L * 1024 * 1024;

    /// <summary>원본 모델 저장소가 허용하는 최대 크기(byte)입니다.</summary>
    public const long MaximumSourceModelBytes = 1024L * 1024 * 1024;

    /// <summary>읽기 전용 원본 모델 식별입니다.</summary>
    public ModelIdentity Identity { get; }

    /// <summary>원본 데이터 길이(byte)입니다.</summary>
    public long LengthBytes { get; }

    /// <summary>원본을 임시 파일에 보관하는지 나타냅니다.</summary>
    public bool IsTemporaryFileBacked { get; }

    /// <summary>작은 메모리 기반 원본의 바이트입니다.</summary>
    public ReadOnlyMemory<byte> ModelBytes
    {
        get
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                if (_modelBytes is null)
                    throw new InvalidOperationException("파일 기반 원본은 OpenRead 또는 CopyTo로 읽어야 합니다.");

                return _modelBytes;
            }
        }
    }

    /// <summary>가져온 원본의 해시와 바이트를 교차 검증해 메모리에 보관합니다.</summary>
    public ModelSource(ModelIdentity identity, ReadOnlySpan<byte> modelBytes)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        if (modelBytes.IsEmpty)
            throw new ArgumentException("원본 모델 바이트가 비어 있습니다.", nameof(modelBytes));
        if (modelBytes.Length > MaximumSourceModelBytes)
            throw new ArgumentOutOfRangeException(nameof(modelBytes));

        byte[] copiedBytes = modelBytes.ToArray();
        ValidateHash(identity, copiedBytes);
        _modelBytes = copiedBytes;
        LengthBytes = copiedBytes.LongLength;
    }

    /// <summary>길이가 확인된 스트림을 해시 검증해 메모리 또는 세션 임시 파일에 보관합니다.</summary>
    public static ModelSource ReadFromStream(
        ModelIdentity identity,
        Stream source,
        long expectedLength,
        CancellationToken cancellationToken = default,
        long inMemoryThresholdBytes = DefaultInMemoryStorageThresholdBytes,
        string? temporaryDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        CapturedContent content = CaptureContent(
            source,
            expectedLength,
            inMemoryThresholdBytes,
            temporaryDirectory,
            cancellationToken);

        if (!string.Equals(content.Sha256, identity.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteTemporaryFile(content.TemporaryFilePath);
            throw new ArgumentException("모델 바이트 해시가 ModelIdentity와 다릅니다.", nameof(source));
        }

        return new ModelSource(identity, content);
    }

    /// <summary>원본 스트림을 한 번 읽어 해시와 가져오기 정보를 가진 모델 원본을 만듭니다.</summary>
    public static ModelSource CaptureFromStream(
        Stream source,
        long expectedLength,
        double sourceUnitToMillimeter,
        string importerName,
        string importerVersion,
        string faceFingerprintVersion,
        CancellationToken cancellationToken = default,
        long inMemoryThresholdBytes = DefaultInMemoryStorageThresholdBytes,
        string? temporaryDirectory = null)
    {
        CapturedContent content = CaptureContent(
            source,
            expectedLength,
            inMemoryThresholdBytes,
            temporaryDirectory,
            cancellationToken);
        try
        {
            var identity = new ModelIdentity(
                content.Sha256,
                sourceUnitToMillimeter,
                importerName,
                importerVersion,
                faceFingerprintVersion);
            return new ModelSource(identity, content);
        }
        catch
        {
            TryDeleteTemporaryFile(content.TemporaryFilePath);
            throw;
        }
    }

    /// <summary>원본을 읽기 전용 스트림으로 열어 파일 경로를 외부에 노출하지 않습니다.</summary>
    public Stream OpenRead()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_modelBytes is not null)
                return new MemoryStream(_modelBytes, writable: false);
            if (_temporaryFilePath is null)
                throw new InvalidOperationException("원본 모델 저장소가 없습니다.");

            return new FileStream(
                _temporaryFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                CopyBufferSize,
                FileOptions.SequentialScan);
        }
    }

    /// <summary>원본을 취소 가능한 청크 단위로 대상 스트림에 복사합니다.</summary>
    public void CopyTo(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("쓸 수 있는 대상 스트림이 필요합니다.", nameof(destination));

        using Stream source = OpenRead();
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        byte[] buffer = new byte[CopyBufferSize];
        long copiedLength = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int bytesRead = source.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0)
                break;

            destination.Write(buffer, 0, bytesRead);
            hash.AppendData(buffer, 0, bytesRead);
            copiedLength += bytesRead;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (copiedLength != LengthBytes)
            throw new InvalidDataException("원본 모델 저장소의 실제 길이가 캡처한 길이와 다릅니다.");

        byte[] actualHash = hash.GetHashAndReset();
        byte[] expectedHash = Convert.FromHexString(Identity.SourceSha256);
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            throw new InvalidDataException("원본 모델 저장소의 SHA-256이 모델 신원과 다릅니다.");
    }

    /// <summary>세션 종료 또는 모델 교체 시 소유한 메모리와 임시 파일을 해제합니다.</summary>
    public void Dispose()
    {
        string? temporaryFilePath;
        lock (_sync)
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _modelBytes = null;
            temporaryFilePath = _temporaryFilePath;
            _temporaryFilePath = null;
        }

        TryDeleteTemporaryFile(temporaryFilePath);
    }

    private ModelSource(ModelIdentity identity, CapturedContent content)
    {
        Identity = identity;
        LengthBytes = content.LengthBytes;
        _modelBytes = content.ModelBytes;
        _temporaryFilePath = content.TemporaryFilePath;
        IsTemporaryFileBacked = _temporaryFilePath is not null;
    }

    private static CapturedContent CaptureContent(
        Stream source,
        long expectedLength,
        long inMemoryThresholdBytes,
        string? temporaryDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("읽을 수 있는 스트림이 필요합니다.", nameof(source));
        if (expectedLength <= 0 || expectedLength > MaximumSourceModelBytes)
            throw new ArgumentOutOfRangeException(nameof(expectedLength));
        if (inMemoryThresholdBytes < 0 || inMemoryThresholdBytes > MaximumSourceModelBytes)
            throw new ArgumentOutOfRangeException(nameof(inMemoryThresholdBytes));

        if (expectedLength <= inMemoryThresholdBytes && expectedLength <= Array.MaxLength)
        {
            byte[] bytes = ReadOwnedBytes(source, expectedLength, cancellationToken);
            string sha256 = ComputeSha256(bytes);
            return new CapturedContent(bytes, null, expectedLength, sha256);
        }

        string path = CreateTemporaryFilePath(temporaryDirectory);
        try
        {
            string sha256 = CopyStreamToTemporaryFile(source, path, expectedLength, cancellationToken);
            return new CapturedContent(null, path, expectedLength, sha256);
        }
        catch
        {
            TryDeleteTemporaryFile(path);
            throw;
        }
    }

    private static byte[] ReadOwnedBytes(
        Stream source,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        byte[] ownedBytes = new byte[checked((int)expectedLength)];
        int offset = 0;
        while (offset < ownedBytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int bytesRead = source.Read(ownedBytes, offset, ownedBytes.Length - offset);
            if (bytesRead == 0)
                throw new EndOfStreamException("원본 모델 스트림이 선언된 길이보다 짧습니다.");

            offset += bytesRead;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (source.ReadByte() != -1)
            throw new InvalidDataException("원본 모델 스트림이 선언된 길이보다 깁니다.");

        return ownedBytes;
    }

    private static string CopyStreamToTemporaryFile(
        Stream source,
        string path,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        using var output = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.SequentialScan);
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        byte[] buffer = new byte[CopyBufferSize];
        long remainingLength = expectedLength;
        while (remainingLength > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int requestedCount = (int)Math.Min(buffer.Length, remainingLength);
            int bytesRead = source.Read(buffer, 0, requestedCount);
            if (bytesRead == 0)
                throw new EndOfStreamException("원본 모델 스트림이 선언된 길이보다 짧습니다.");

            output.Write(buffer, 0, bytesRead);
            hash.AppendData(buffer, 0, bytesRead);
            remainingLength -= bytesRead;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (source.ReadByte() != -1)
            throw new InvalidDataException("원본 모델 스트림이 선언된 길이보다 깁니다.");

        output.Flush(flushToDisk: true);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string CreateTemporaryFilePath(string? temporaryDirectory)
    {
        string directory = string.IsNullOrWhiteSpace(temporaryDirectory)
            ? Path.Combine(Path.GetTempPath(), "HeatingMold", "ModelSource")
            : Path.GetFullPath(temporaryDirectory);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():N}.step");
    }

    private static void TryDeleteTemporaryFile(string? path)
    {
        if (path is null)
            return;

        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 임시 파일 정리 실패가 세션의 확정 상태를 바꾸지 않도록 둡니다.
        }
    }

    private static string ComputeSha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();

    private static void ValidateHash(ModelIdentity identity, ReadOnlySpan<byte> modelBytes)
    {
        byte[] actualHash = System.Security.Cryptography.SHA256.HashData(modelBytes);
        byte[] expectedHash = Convert.FromHexString(identity.SourceSha256);
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            throw new ArgumentException("모델 바이트 해시가 ModelIdentity와 다릅니다.", nameof(modelBytes));
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(ModelSource));
    }

    private sealed record CapturedContent(
        byte[]? ModelBytes,
        string? TemporaryFilePath,
        long LengthBytes,
        string Sha256);
}

/// <summary>Publish 전 Native 후보의 최종 곡선·질의·소유권을 보관합니다.</summary>
public interface IPreparedGeometry : IDisposable
{
    /// <summary>기하 어댑터가 보정한 확정 후보이며 기본값은 요청 후보를 뜻합니다.</summary>
    DesignProject? EffectiveProject => null;

    /// <summary>후보 설계에서 생성·측정한 최종 기하입니다.</summary>
    GeometrySnapshot Snapshot { get; }

    /// <summary>후보 문서에 한정된 읽기 전용 기하 질의 포트입니다.</summary>
    IGeometryQueries Queries { get; }

    /// <summary>Native 문서 revision과 작업 좌표계 frame을 연결한 검증 context입니다.</summary>
    WorkingCoordinateSystem? CoordinateSystem => null;

    /// <summary>후보가 Publish될 경우 활성 상태가 될 런타임 문서 정보입니다.</summary>
    RuntimeDocument RuntimeDocument { get; }

    /// <summary>모든 검증 이후 후보를 활성 상태로 확정합니다.</summary>
    GeometryCommitReceipt Publish();
}

/// <summary>현재 Native 문서와 별도 staging 후보를 준비하는 기하 작업 포트입니다.</summary>
public interface IGeometryWorkspace
{
    /// <summary>원본 모델 후보 문서를 준비하며 성공 전 활성 문서를 바꾸지 않습니다.</summary>
    IPreparedGeometry PrepareModel(
        DesignProject candidate,
        ModelSource modelSource,
        CancellationToken cancellationToken = default);

    /// <summary>현재 문서에서 확정 후보의 곡선과 연결 경로를 준비합니다.</summary>
    IPreparedGeometry Prepare(DesignProject candidate, CancellationToken cancellationToken = default);

    /// <summary>저장 설계를 후보 문서에서 복원해 검증 전용 스테이징을 만듭니다.</summary>
    IPreparedGeometry Restore(
        DesignProject project,
        ModelSource modelSource,
        CancellationToken cancellationToken = default);
}
