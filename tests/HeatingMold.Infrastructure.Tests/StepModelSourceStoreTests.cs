using System.Security.Cryptography;
using HeatingMold.Application.Ports.Geometry;
using HeatingMold.Infrastructure.Persistence;

namespace HeatingMold.Infrastructure.Tests;

/// <summary>원본 STEP 캡처의 바이트 소유권과 가져오기 메타데이터를 검증합니다.</summary>
public sealed class StepModelSourceStoreTests
{
    /// <summary>캡처한 원본은 파일이 사라져도 바이트와 모델 신원을 유지합니다.</summary>
    [Fact]
    public void Capture_CopiesSourceBytesAndImporterMetadata()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string path = Path.Combine(temporaryDirectory.Path, "mold.step");
        byte[] sourceBytes = [3, 5, 8, 13, 21, 34];
        File.WriteAllBytes(path, sourceBytes);
        var store = new StepModelSourceStore(1.0, "occt-step-reader-7.9.3", "face-fingerprint-v1");

        var result = store.Capture(path);
        File.Delete(path);

        Assert.True(result.IsSuccess);
        var source = result.GetValueOrThrow();
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant(),
            source.Identity.SourceSha256);
        Assert.Equal(1.0, source.Identity.SourceUnitToMillimeter);
        Assert.Equal("STEP", source.Identity.ImporterName);
        Assert.Equal("occt-step-reader-7.9.3", source.Identity.ImporterVersion);
        Assert.Equal("face-fingerprint-v1", source.Identity.FaceFingerprintVersion);
        Assert.Equal(sourceBytes, source.ModelBytes.ToArray());
    }

    /// <summary>큰 원본은 세션이 소유한 임시 파일에 보관하고 해제 시 파일을 제거합니다.</summary>
    [Fact]
    public void Capture_StoresLargeModelInTemporaryFileUntilDisposed()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string sourcePath = Path.Combine(temporaryDirectory.Path, "large.step");
        string storageDirectory = Path.Combine(temporaryDirectory.Path, "session-models");
        byte[] sourceBytes = Enumerable.Range(0, 32_000).Select(value => (byte)(value % 239)).ToArray();
        File.WriteAllBytes(sourcePath, sourceBytes);
        var store = new StepModelSourceStore(
            1.0,
            "reader-v1",
            "face-v1",
            inMemoryThresholdBytes: 256,
            temporaryDirectory: storageDirectory);

        var result = store.Capture(sourcePath);

        Assert.True(result.IsSuccess);
        ModelSource source = result.GetValueOrThrow();
        Assert.True(source.IsTemporaryFileBacked);
        Assert.Single(Directory.GetFiles(storageDirectory, "*.step"));
        File.Delete(sourcePath);
        using (Stream captured = source.OpenRead())
        using (var actualBytes = new MemoryStream())
        {
            captured.CopyTo(actualBytes);
            Assert.Equal(sourceBytes, actualBytes.ToArray());
        }

        source.Dispose();

        Assert.Empty(Directory.GetFiles(storageDirectory, "*.step"));
        Assert.Throws<ObjectDisposedException>(() => source.OpenRead());
    }

    /// <summary>스트림 캡처는 짧은 읽기를 이어 처리하고 선언 길이와 취소를 검사합니다.</summary>
    [Fact]
    public void CaptureFromStream_HandlesShortReadsAndRejectsLengthMismatchOrCancellation()
    {
        byte[] sourceBytes = Enumerable.Range(0, 10_000).Select(value => (byte)(value % 251)).ToArray();
        using var chunkedStream = new LimitedReadStream(sourceBytes, maximumReadSize: 113);

        using ModelSource source = ModelSource.CaptureFromStream(
            chunkedStream,
            sourceBytes.Length,
            1.0,
            "STEP",
            "reader-v1",
            "face-v1");

        Assert.Equal(sourceBytes, source.ModelBytes.ToArray());
        Assert.True(chunkedStream.CanRead);

        using var truncatedStream = new MemoryStream([1, 2, 3]);
        Assert.Throws<EndOfStreamException>(() => ModelSource.CaptureFromStream(
            truncatedStream,
            expectedLength: 4,
            sourceUnitToMillimeter: 1.0,
            importerName: "STEP",
            importerVersion: "reader-v1",
            faceFingerprintVersion: "face-v1"));

        using var oversizedStream = new MemoryStream([1, 2, 3]);
        Assert.Throws<InvalidDataException>(() => ModelSource.CaptureFromStream(
            oversizedStream,
            expectedLength: 2,
            sourceUnitToMillimeter: 1.0,
            importerName: "STEP",
            importerVersion: "reader-v1",
            faceFingerprintVersion: "face-v1"));

        using var cancellation = new CancellationTokenSource();
        using var cancellingStream = new CancellingReadStream(sourceBytes, cancellation);
        Assert.Throws<OperationCanceledException>(() => ModelSource.CaptureFromStream(
            cancellingStream,
            sourceBytes.Length,
            1.0,
            "STEP",
            "reader-v1",
            "face-v1",
            cancellation.Token));
    }

    /// <summary>빈 파일은 실패하며 취소된 요청은 원본 파일을 읽지 않습니다.</summary>
    [Fact]
    public void Capture_RejectsEmptySourceAndHonorsCancellation()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        string emptyPath = Path.Combine(temporaryDirectory.Path, "empty.step");
        string sourcePath = Path.Combine(temporaryDirectory.Path, "mold.step");
        File.WriteAllBytes(emptyPath, []);
        File.WriteAllBytes(sourcePath, [1, 2, 3]);
        var store = new StepModelSourceStore(1.0, "reader-v1", "face-v1");

        var emptyResult = store.Capture(emptyPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelledResult = store.Capture(sourcePath, cancellation.Token);

        Assert.False(emptyResult.IsSuccess);
        Assert.Equal("EmptyModelSource", Assert.Single(emptyResult.Issues).Code);
        Assert.False(cancelledResult.IsSuccess);
        Assert.Equal("OperationCancelled", Assert.Single(cancelledResult.Issues).Code);
    }

    /// <summary>입력 경로나 파일 접근 실패는 명시적인 읽기 진단으로 반환합니다.</summary>
    [Fact]
    public void Capture_ReportsMissingPathAndMissingFile()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var store = new StepModelSourceStore(1.0, "reader-v1", "face-v1");

        var blankPath = store.Capture("  ");
        var missingFile = store.Capture(Path.Combine(temporaryDirectory.Path, "missing.step"));

        Assert.False(blankPath.IsSuccess);
        Assert.Equal("InvalidPath", Assert.Single(blankPath.Issues).Code);
        Assert.False(missingFile.IsSuccess);
        Assert.Equal("ModelSourceReadFailed", Assert.Single(missingFile.Issues).Code);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"HeatingMold-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private class LimitedReadStream : MemoryStream
    {
        private readonly int _maximumReadSize;

        public LimitedReadStream(byte[] content, int maximumReadSize)
            : base(content, writable: false) =>
            _maximumReadSize = maximumReadSize;

        public override int Read(byte[] buffer, int offset, int count) =>
            base.Read(buffer, offset, Math.Min(count, _maximumReadSize));
    }

    private sealed class CancellingReadStream : LimitedReadStream
    {
        private readonly CancellationTokenSource _cancellation;

        public CancellingReadStream(byte[] content, CancellationTokenSource cancellation)
            : base(content, maximumReadSize: 113) =>
            _cancellation = cancellation;

        public override int Read(byte[] buffer, int offset, int count)
        {
            int bytesRead = base.Read(buffer, offset, count);
            if (bytesRead > 0)
                _cancellation.Cancel();

            return bytesRead;
        }
    }
}
