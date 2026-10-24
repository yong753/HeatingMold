namespace HeatingMold.Core.Primitives;

/// <summary>유한한 밀리미터 길이를 나타냅니다.</summary>
public readonly record struct LengthMm
{
    /// <summary>밀리미터 단위 숫자값입니다.</summary>
    public double Value { get; }

    /// <summary>유한한 밀리미터 길이를 만듭니다.</summary>
    public LengthMm(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "길이는 유한한 mm 값이어야 합니다.");
        Value = value;
    }

    /// <summary>길이를 미터 단위로 한 번 변환합니다.</summary>
    public double ToMeters() => Value / 1_000.0;

    /// <summary>두 길이를 더합니다.</summary>
    public static LengthMm operator +(LengthMm left, LengthMm right) => new(left.Value + right.Value);

    /// <summary>두 길이의 차이를 구합니다.</summary>
    public static LengthMm operator -(LengthMm left, LengthMm right) => new(left.Value - right.Value);
}

/// <summary>유한한 제곱밀리미터 면적을 나타냅니다.</summary>
public readonly record struct AreaMm2
{
    /// <summary>제곱밀리미터 단위 숫자값입니다.</summary>
    public double Value { get; }

    /// <summary>유한한 제곱밀리미터 면적을 만듭니다.</summary>
    public AreaMm2(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "면적은 유한한 mm² 값이어야 합니다.");
        Value = value;
    }
}

/// <summary>미터당 옴으로 표시하는 유한한 선저항을 나타냅니다.</summary>
public readonly record struct ResistanceOhmsPerMeter
{
    /// <summary>Ω/m 단위 숫자값입니다.</summary>
    public double Value { get; }

    /// <summary>유한한 Ω/m 선저항을 만듭니다.</summary>
    public ResistanceOhmsPerMeter(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "선저항은 유한한 Ω/m 값이어야 합니다.");
        Value = value;
    }
}

/// <summary>유한한 옴 단위 저항을 나타냅니다.</summary>
public readonly record struct ResistanceOhms
{
    /// <summary>Ω 단위 숫자값입니다.</summary>
    public double Value { get; }

    /// <summary>유한한 Ω 저항을 만듭니다.</summary>
    public ResistanceOhms(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "저항은 유한한 Ω 값이어야 합니다.");
        Value = value;
    }
}

/// <summary>유한한 실효 전압을 볼트 단위로 나타냅니다.</summary>
public readonly record struct VoltageVrms
{
    /// <summary>V RMS 단위 숫자값입니다.</summary>
    public double Value { get; }

    /// <summary>유한한 실효 전압을 만듭니다.</summary>
    public VoltageVrms(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "전압은 유한한 V RMS 값이어야 합니다.");
        Value = value;
    }
}

/// <summary>유한한 암페어 전류를 나타냅니다.</summary>
public readonly record struct CurrentAmps
{
    /// <summary>A 단위 숫자값입니다.</summary>
    public double Value { get; }

    /// <summary>유한한 전류를 만듭니다.</summary>
    public CurrentAmps(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "전류는 유한한 A 값이어야 합니다.");
        Value = value;
    }
}

/// <summary>유한한 와트 전력을 나타냅니다.</summary>
public readonly record struct PowerWatts
{
    /// <summary>W 단위 숫자값입니다.</summary>
    public double Value { get; }

    /// <summary>유한한 전력을 만듭니다.</summary>
    public PowerWatts(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "전력은 유한한 W 값이어야 합니다.");
        Value = value;
    }
}
