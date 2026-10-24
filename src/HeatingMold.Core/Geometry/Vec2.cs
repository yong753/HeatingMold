namespace HeatingMold.Core.Geometry;

/// <summary>UV 좌표나 작업 평면 좌표에 사용하는 2차원 점과 벡터입니다.</summary>
public readonly record struct Vec2
{
    /// <summary>X 좌표 또는 성분입니다.</summary>
    public double X { get; }

    /// <summary>Y 좌표 또는 성분입니다.</summary>
    public double Y { get; }

    /// <summary>두 성분이 유한한 2차원 값을 만듭니다.</summary>
    public Vec2(double x, double y)
    {
        if (!double.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x), "X 값은 유한해야 합니다.");
        if (!double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(y), "Y 값은 유한해야 합니다.");

        X = x;
        Y = y;
    }

    /// <summary>두 2차원 벡터를 더합니다.</summary>
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);

    /// <summary>첫 번째 2차원 벡터에서 두 번째 벡터를 뺍니다.</summary>
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);

    /// <summary>벡터에 유한한 스칼라를 곱합니다.</summary>
    public static Vec2 operator *(Vec2 value, double scale) => new(value.X * scale, value.Y * scale);

    /// <summary>벡터의 유클리드 길이를 반환합니다.</summary>
    public double Length => CalculateLength(X, Y);

    /// <summary>다른 점까지의 거리를 반환합니다.</summary>
    public double DistanceTo(Vec2 other) => (this - other).Length;

    /// <summary>원점을 기준으로 라디안 각도만큼 회전합니다.</summary>
    public Vec2 Rotate(double angleRad)
    {
        if (!double.IsFinite(angleRad)) throw new ArgumentOutOfRangeException(nameof(angleRad), "회전 각도는 유한해야 합니다.");

        double cosine = Math.Cos(angleRad);
        double sine = Math.Sin(angleRad);
        return new Vec2(X * cosine - Y * sine, X * sine + Y * cosine);
    }

    /// <summary>두 좌표 성분을 분리해 반환합니다.</summary>
    public void Deconstruct(out double x, out double y)
    {
        x = X;
        y = Y;
    }

    /// <summary>좌표값을 간단한 문자열로 표시합니다.</summary>
    public override string ToString() => $"({X:0.####}, {Y:0.####})";

    private static double CalculateLength(double x, double y)
    {
        double scale = Math.Max(Math.Abs(x), Math.Abs(y));
        if (scale == 0) return 0;

        double scaledX = x / scale;
        double scaledY = y / scale;
        double length = scale * Math.Sqrt(scaledX * scaledX + scaledY * scaledY);
        if (!double.IsFinite(length)) throw new ArithmeticException("2차원 벡터의 길이가 표현 범위를 벗어났습니다.");

        return length;
    }
}
