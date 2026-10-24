using System;
using System.Collections.Generic;
using System.Linq;
using HeatingMold.Infrastructure.Geometry.Occt;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Geometry.Occt;

/// <summary>Infrastructure 곡선 DTO를 OCCT 브리지 입력 자료형으로 옮깁니다.</summary>
public static class CurveDefinitionBridgeAdapter
{
    /// <summary>불변 곡선 DTO의 자료를 네이티브 브리지 객체에 복사합니다.</summary>
    public static CurveDefinitionData ToNativeData(CurveDefinitionBridgeDto definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new CurveDefinitionData
        {
            Kind = definition.Kind,
            Direction = definition.Direction,
            Start = Copy(definition.Start),
            End = Copy(definition.End),
            Points = Copy(definition.Points),
            Frame = Copy(definition.Frame),
            Center = Copy(definition.Center),
            RadiusMm = definition.RadiusMm ?? 0,
            StartAngleRad = definition.StartAngleRad ?? 0,
            EndAngleRad = definition.EndAngleRad ?? 0,
            Degree = definition.Degree ?? 0,
            Poles = Copy(definition.Poles),
            Weights = Copy(definition.Weights),
            Knots = Copy(definition.Knots),
            Multiplicities = Copy(definition.Multiplicities),
            IsPeriodic = definition.IsPeriodic ?? false,
            TrimStart = definition.TrimStart ?? 0,
            TrimEnd = definition.TrimEnd ?? 0
        };
    }

    private static double[] Copy(IReadOnlyList<double> values) => values == null ? Array.Empty<double>() : values.ToArray();

    private static int[] Copy(IReadOnlyList<int> values) => values == null ? Array.Empty<int>() : values.ToArray();
}
