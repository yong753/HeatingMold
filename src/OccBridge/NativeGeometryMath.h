#pragma once

#include <cmath>

namespace HeatingMoldNativeGeometry
{
    /// <summary>유한한 두 실수의 중점을 덧셈 overflow 없이 계산합니다.</summary>
    inline double StableMidpoint(double first, double second) noexcept
    {
        if (std::signbit(first) != std::signbit(second))
            return (first + second) / 2.0;

        return first + (second - first) / 2.0;
    }
}
