using System.Security.Cryptography;
using HeatingMold.Application.Ports.Export;

namespace HeatingMold.Infrastructure.Export;

/// <summary>완성된 임시 출력 파일을 메타데이터와 함께 원자적으로 확정합니다.</summary>
internal static class AtomicExportFile
{
    /// <summary>메타데이터를 먼저 계산하고 취소 확인 뒤 목적 파일을 교체합니다.</summary>
    public static ExportReceipt Commit(
        string temporaryPath,
        string destinationPath,
        ExportFormat format,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();

        var temporaryFile = new FileInfo(temporaryPath);
        long fileLength = temporaryFile.Length;
        string hash;
        using (FileStream stream = File.OpenRead(temporaryPath))
            hash = ComputeSha256(stream, cancellationToken);

        var receipt = new ExportReceipt(destinationPath, format, fileLength, hash);
        cancellationToken.ThrowIfCancellationRequested();
        ReplaceFile(temporaryPath, destinationPath);
        return receipt;
    }

    /// <summary>입력 스트림을 나눠 읽어 해시를 만들고 읽기 사이마다 취소를 확인합니다.</summary>
    internal static string ComputeSha256(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            int bytesRead = stream.Read(buffer, 0, buffer.Length);
            cancellationToken.ThrowIfCancellationRequested();
            if (bytesRead == 0)
                break;

            hash.AppendData(buffer, 0, bytesRead);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>임시 파일 정리 실패가 원래 출력 결과를 덮지 않도록 삭제를 시도합니다.</summary>
    public static void TryDelete(string? temporaryPath)
    {
        if (string.IsNullOrWhiteSpace(temporaryPath) || !File.Exists(temporaryPath))
            return;

        try
        {
            File.Delete(temporaryPath);
        }
        catch (IOException)
        {
            // 출력 실패나 취소 결과를 정리 오류로 바꾸지 않습니다.
        }
        catch (UnauthorizedAccessException)
        {
            // 출력 실패나 취소 결과를 정리 오류로 바꾸지 않습니다.
        }
    }

    private static void ReplaceFile(string temporaryPath, string destinationPath)
    {
        if (File.Exists(destinationPath))
            File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null);
        else
            File.Move(temporaryPath, destinationPath);
    }
}
