namespace HeatingMold.Core.Model;

/// <summary>원본 모델 바이트와 가져오기 조건을 식별하는 영속 값입니다.</summary>
public sealed record ModelIdentity
{
    /// <summary>원본 모델 바이트의 SHA-256 해시입니다.</summary>
    public string SourceSha256 { get; }

    /// <summary>가져오기 전 단위를 mm로 바꾸는 양의 배율입니다.</summary>
    public double SourceUnitToMillimeter { get; }

    /// <summary>모델을 읽은 가져오기 구현의 식별자입니다.</summary>
    public string ImporterName { get; }

    /// <summary>모델을 읽은 가져오기 구현의 버전입니다.</summary>
    public string ImporterVersion { get; }

    /// <summary>Face 형상 특징을 비교하는 규칙의 버전입니다.</summary>
    public string FaceFingerprintVersion { get; }

    /// <summary>원본 해시, 단위 변환, 가져오기 구현과 Face 특징 버전을 검증합니다.</summary>
    public ModelIdentity(
        string sourceSha256,
        double sourceUnitToMillimeter,
        string importerName,
        string importerVersion,
        string faceFingerprintVersion)
    {
        SourceSha256 = RequireSha256(sourceSha256, nameof(sourceSha256));
        if (!double.IsFinite(sourceUnitToMillimeter) || sourceUnitToMillimeter <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceUnitToMillimeter), "원본 단위의 mm 변환 배율은 양의 유한값이어야 합니다.");

        SourceUnitToMillimeter = sourceUnitToMillimeter;
        ImporterName = RequireText(importerName, nameof(importerName));
        ImporterVersion = RequireText(importerVersion, nameof(importerVersion));
        FaceFingerprintVersion = RequireText(faceFingerprintVersion, nameof(faceFingerprintVersion));
    }

    private static string RequireSha256(string value, string parameterName)
    {
        if (value is null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("원본 SHA-256은 16진수 64자로 입력해야 합니다.", parameterName);

        return value.ToLowerInvariant();
    }

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("가져오기 식별 정보는 비어 있을 수 없습니다.", parameterName);

        return value.Trim();
    }
}
