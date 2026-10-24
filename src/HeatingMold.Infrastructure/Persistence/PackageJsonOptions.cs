using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeatingMold.Infrastructure.Persistence;

/// <summary>스키마 DTO를 camelCase와 안정된 enum 문자열로 읽고 씁니다.</summary>
internal static class PackageJsonOptions
{
    /// <summary>엄격한 JSON 깊이와 고정 필드 정책을 적용한 직렬화 옵션입니다.</summary>
    public static JsonSerializerOptions Create() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = 96,
        Converters =
        {
            new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false)
        }
    };
}
