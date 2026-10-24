using System.Reflection;
using System.Text.Json;

namespace HeatingMold.Infrastructure.Persistence;

/// <summary>JSON을 DTO로 읽기 전에 필수 필드와 중첩 배열을 확인합니다.</summary>
internal static class PackageJsonSchemaValidator
{
    private static readonly NullabilityInfoContext Nullability = new();

    /// <summary>필수 DTO 속성이 빠지거나 null인 JSON을 거부합니다.</summary>
    public static void Validate<T>(JsonElement root, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateValue(root, typeof(T), "$", options.PropertyNamingPolicy);
    }

    private static void ValidateValue(
        JsonElement value,
        Type valueType,
        string jsonPath,
        JsonNamingPolicy? namingPolicy)
    {
        Type? nullableValueType = Nullable.GetUnderlyingType(valueType);
        if (nullableValueType is not null)
            valueType = nullableValueType;

        if (value.ValueKind == JsonValueKind.Null)
            throw new JsonException($"프로젝트 배열 항목이나 루트 값에 null을 사용할 수 없습니다: {jsonPath}.");

        if (valueType.IsArray)
        {
            if (value.ValueKind != JsonValueKind.Array)
                throw new JsonException($"프로젝트 필드가 배열이 아닙니다: {jsonPath}.");

            Type itemType = valueType.GetElementType()!;
            int index = 0;
            foreach (JsonElement item in value.EnumerateArray())
            {
                ValidateValue(item, itemType, $"{jsonPath}[{index}]", namingPolicy);
                index++;
            }

            return;
        }

        if (!IsSchemaDto(valueType))
            return;
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException($"프로젝트 필드가 객체가 아닙니다: {jsonPath}.");

        foreach (PropertyInfo property in valueType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
                continue;

            string propertyName = namingPolicy?.ConvertName(property.Name) ?? property.Name;
            bool isRequired = IsRequired(property);
            if (!value.TryGetProperty(propertyName, out JsonElement propertyValue))
            {
                if (isRequired)
                    throw new JsonException($"필수 프로젝트 필드가 없습니다: {jsonPath}.{propertyName}.");
                continue;
            }

            if (isRequired && propertyValue.ValueKind == JsonValueKind.Null)
                throw new JsonException($"필수 프로젝트 필드가 null입니다: {jsonPath}.{propertyName}.");

            if (propertyValue.ValueKind != JsonValueKind.Null)
                ValidateValue(propertyValue, property.PropertyType, $"{jsonPath}.{propertyName}", namingPolicy);
        }
    }

    private static bool IsRequired(PropertyInfo property)
    {
        if (Nullable.GetUnderlyingType(property.PropertyType) is not null)
            return false;
        if (property.PropertyType.IsValueType)
            return true;

        return Nullability.Create(property).ReadState == NullabilityState.NotNull;
    }

    private static bool IsSchemaDto(Type valueType) =>
        valueType.Namespace == "HeatingMold.Infrastructure.Persistence.SchemaV1" &&
        valueType.IsClass;
}
