namespace HeatingMold.Core.Primitives;

/// <summary>파생 결과가 어떤 확정 설계와 규칙에서 계산되었는지 식별합니다.</summary>
public sealed record DependencyStamp
{
    /// <summary>계산에 사용한 확정 설계 revision입니다.</summary>
    public DesignRevision DesignRevision { get; }

    /// <summary>원본 모델 바이트의 SHA-256 해시입니다.</summary>
    public string SourceModelSha256 { get; }

    /// <summary>작업 좌표계 값의 SHA-256 지문입니다.</summary>
    public string WorkingCoordinateSystemFingerprint { get; }

    /// <summary>계산에 관련된 입력 값의 SHA-256 해시입니다.</summary>
    public string InputFingerprint { get; }

    /// <summary>결과에 적용한 생성·검증 규칙의 버전입니다.</summary>
    public string RuleVersion { get; }

    /// <summary>계산 근거를 이루는 해시와 규칙 버전을 검증해 보관합니다.</summary>
    /// <param name="designRevision">확정 설계 revision입니다.</param>
    /// <param name="sourceModelSha256">원본 모델 바이트의 SHA-256 해시입니다.</param>
    /// <param name="workingCoordinateSystemFingerprint">작업 좌표계 값의 SHA-256 지문입니다.</param>
    /// <param name="inputFingerprint">계산 관련 입력의 SHA-256 해시입니다.</param>
    /// <param name="ruleVersion">적용한 규칙 버전입니다.</param>
    public DependencyStamp(
        DesignRevision designRevision,
        string sourceModelSha256,
        string workingCoordinateSystemFingerprint,
        string inputFingerprint,
        string ruleVersion)
    {
        DesignRevision = designRevision;
        SourceModelSha256 = NormalizeSha256(sourceModelSha256, nameof(sourceModelSha256));
        WorkingCoordinateSystemFingerprint = NormalizeSha256(workingCoordinateSystemFingerprint, nameof(workingCoordinateSystemFingerprint));
        InputFingerprint = NormalizeSha256(inputFingerprint, nameof(inputFingerprint));

        if (string.IsNullOrWhiteSpace(ruleVersion))
            throw new ArgumentException("규칙 버전을 입력해야 합니다.", nameof(ruleVersion));

        RuleVersion = ruleVersion.Trim();
    }

    private static string NormalizeSha256(string value, string parameterName)
    {
        if (value is null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("SHA-256 값은 16진수 64자로 입력해야 합니다.", parameterName);

        return value.ToLowerInvariant();
    }
}
