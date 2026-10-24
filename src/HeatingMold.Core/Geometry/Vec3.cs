namespace HeatingMold.Core.Geometry;

/// <summary>모델 좌표에 사용하는 3차원 점과 벡터입니다.</summary>
public readonly record struct Vec3
{
    /// <summary>X 좌표 또는 성분입니다.</summary>
    public double X { get; }

    /// <summary>Y 좌표 또는 성분입니다.</summary>
    public double Y { get; }

    /// <summary>Z 좌표 또는 성분입니다.</summary>
    public double Z { get; }

    /// <summary>세 성분이 유한한 3차원 값을 만듭니다.</summary>
    public Vec3(double x, double y, double z)
    {
        if (!double.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x), "X 값은 유한해야 합니다.");
        if (!double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(y), "Y 값은 유한해야 합니다.");
        if (!double.IsFinite(z)) throw new ArgumentOutOfRangeException(nameof(z), "Z 값은 유한해야 합니다.");

        X = x;
        Y = y;
        Z = z;
    }

    /// <summary>두 3차원 벡터를 더합니다.</summary>
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    /// <summary>첫 번째 3차원 벡터에서 두 번째 벡터를 뺍니다.</summary>
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    /// <summary>벡터에 유한한 스칼라를 곱합니다.</summary>
    public static Vec3 operator *(Vec3 value, double scale) => new(value.X * scale, value.Y * scale, value.Z * scale);

    /// <summary>벡터의 유클리드 길이를 반환합니다.</summary>
    public double Length
    {
        get
        {
            double scale = Math.Max(Math.Abs(X), Math.Max(Math.Abs(Y), Math.Abs(Z)));
            if (scale == 0) return 0;

            double scaledX = X / scale;
            double scaledY = Y / scale;
            double scaledZ = Z / scale;
            double length = scale * Math.Sqrt(scaledX * scaledX + scaledY * scaledY + scaledZ * scaledZ);
            if (!double.IsFinite(length)) throw new ArithmeticException("3차원 벡터의 길이가 표현 범위를 벗어났습니다.");

            return length;
        }
    }

    /// <summary>영벡터가 아닌 벡터를 길이 1로 정규화합니다.</summary>
    public Vec3 Normalized()
    {
        double scale = Math.Max(Math.Abs(X), Math.Max(Math.Abs(Y), Math.Abs(Z)));
        if (scale == 0) throw new InvalidOperationException("영벡터는 정규화할 수 없습니다.");

        double scaledX = X / scale;
        double scaledY = Y / scale;
        double scaledZ = Z / scale;
        double scaledLength = Math.Sqrt(scaledX * scaledX + scaledY * scaledY + scaledZ * scaledZ);
        return new Vec3(scaledX / scaledLength, scaledY / scaledLength, scaledZ / scaledLength);
    }

    /// <summary>두 벡터의 내적을 반환합니다.</summary>
    public static double Dot(Vec3 a, Vec3 b)
    {
        double dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        if (!double.IsFinite(dot)) throw new ArithmeticException("3차원 벡터의 내적이 표현 범위를 벗어났습니다.");

        return dot;
    }

    /// <summary>두 벡터의 외적을 반환합니다.</summary>
    public static Vec3 Cross(Vec3 a, Vec3 b) => new(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    /// <summary>세 좌표 성분을 분리해 반환합니다.</summary>
    public void Deconstruct(out double x, out double y, out double z)
    {
        x = X;
        y = Y;
        z = Z;
    }

    /// <summary>좌표값을 간단한 문자열로 표시합니다.</summary>
    public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###})";
}
