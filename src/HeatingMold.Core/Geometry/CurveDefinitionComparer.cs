namespace HeatingMold.Core.Geometry;

/// <summary>배열 참조가 아니라 원본 수학 값으로 곡선 정의를 비교합니다.</summary>
public static class CurveDefinitionComparer
{
    /// <summary>두 곡선의 종류와 모든 정의 값이 같은지 확인합니다.</summary>
    public static bool AreEquivalent(CurveDefinition? first, CurveDefinition? second)
    {
        if (ReferenceEquals(first, second))
            return true;
        if (first is null || second is null ||
            first.Kind != second.Kind ||
            first.Direction != second.Direction)
        {
            return false;
        }

        return (first, second) switch
        {
            (LineCurveDefinition left, LineCurveDefinition right) =>
                left.Start == right.Start && left.End == right.End,

            (PolylineCurveDefinition left, PolylineCurveDefinition right) =>
                left.Points.SequenceEqual(right.Points),

            (ArcCurveDefinition left, ArcCurveDefinition right) =>
                SameFrame(left.Plane, right.Plane) &&
                left.Center == right.Center &&
                left.RadiusMm == right.RadiusMm &&
                left.StartAngleRad.Equals(right.StartAngleRad) &&
                left.EndAngleRad.Equals(right.EndAngleRad),

            (BSplineCurveDefinition left, BSplineCurveDefinition right) =>
                left.Degree == right.Degree &&
                left.Poles.SequenceEqual(right.Poles) &&
                left.Weights.SequenceEqual(right.Weights) &&
                left.Knots.SequenceEqual(right.Knots) &&
                left.Multiplicities.SequenceEqual(right.Multiplicities) &&
                left.IsPeriodic == right.IsPeriodic &&
                left.TrimStart.Equals(right.TrimStart) &&
                left.TrimEnd.Equals(right.TrimEnd),

            _ => false
        };
    }

    private static bool SameFrame(ProjectionFrame first, ProjectionFrame second) =>
        first.Origin == second.Origin &&
        first.AxisX == second.AxisX &&
        first.AxisY == second.AxisY &&
        first.Normal == second.Normal;
}
