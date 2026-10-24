using System;
using System.Collections.Generic;
using System.Linq;
using HeatingMold.Core.Geometry;

namespace HeatingMold.Infrastructure.Geometry.Occt;

/// <summary>비주기 유리 B-spline의 매개변수 위치와 접선을 계산합니다.</summary>
internal sealed class BSplineCurveEvaluator
{
    private readonly BSplineCurveDefinition _definition;
    private readonly double[] _expandedKnots;

    /// <summary>곡선 도함수 경계 계산에 사용하는 최소 양의 매듭 간격입니다.</summary>
    public double MinimumKnotSpan { get; }

    /// <summary>비주기 곡선의 유효 시작 매개변수입니다.</summary>
    public double DomainStart { get; }

    /// <summary>비주기 곡선의 유효 끝 매개변수입니다.</summary>
    public double DomainEnd { get; }

    private BSplineCurveEvaluator(
        BSplineCurveDefinition definition,
        double[] expandedKnots,
        double minimumKnotSpan,
        double domainStart,
        double domainEnd)
    {
        _definition = definition;
        _expandedKnots = expandedKnots;
        MinimumKnotSpan = minimumKnotSpan;
        DomainStart = domainStart;
        DomainEnd = domainEnd;
    }

    /// <summary>유효한 비주기 곡선이면 재사용할 평가기를 만듭니다.</summary>
    public static bool TryCreate(BSplineCurveDefinition definition, out BSplineCurveEvaluator? evaluator)
    {
        ArgumentNullException.ThrowIfNull(definition);
        evaluator = null;
        if (definition.IsPeriodic)
            return false;

        var expandedKnots = new List<double>(definition.Multiplicities.Sum());
        for (int knotIndex = 0; knotIndex < definition.Knots.Count; knotIndex++)
        {
            for (int repeat = 0; repeat < definition.Multiplicities[knotIndex]; repeat++)
                expandedKnots.Add(definition.Knots[knotIndex]);
        }

        int degree = definition.Degree;
        int poleCount = definition.Poles.Count;
        if (expandedKnots.Count != poleCount + degree + 1)
            return false;

        double domainStart = expandedKnots[degree];
        double domainEnd = expandedKnots[poleCount];
        if (!double.IsFinite(domainStart) || !double.IsFinite(domainEnd) ||
            definition.TrimStart < domainStart || definition.TrimEnd > domainEnd)
        {
            return false;
        }

        double minimumKnotSpan = double.PositiveInfinity;
        for (int knotIndex = 1; knotIndex < definition.Knots.Count; knotIndex++)
        {
            double span = definition.Knots[knotIndex] - definition.Knots[knotIndex - 1];
            minimumKnotSpan = Math.Min(minimumKnotSpan, span);
        }

        if (!double.IsFinite(minimumKnotSpan) || minimumKnotSpan <= 0)
            return false;

        evaluator = new BSplineCurveEvaluator(
            definition,
            expandedKnots.ToArray(),
            minimumKnotSpan,
            domainStart,
            domainEnd);
        return true;
    }

    /// <summary>매개변수와 knot 쪽 극한을 지정해 유한한 곡선 점과 접선을 계산합니다.</summary>
    public bool TryEvaluate(
        double parameter,
        bool useLeftLimit,
        out Vec3 point,
        out Vec3 tangent)
    {
        point = default;
        tangent = default;
        if (!double.IsFinite(parameter) || parameter < DomainStart || parameter > DomainEnd)
            return false;

        int poleCount = _definition.Poles.Count;
        int degree = _definition.Degree;
        double[] basis = EvaluateBasis(_expandedKnots, degree, parameter, useLeftLimit);
        double[] lowerDegreeBasis = EvaluateBasis(_expandedKnots, degree - 1, parameter, useLeftLimit);
        if (basis.Length != poleCount || lowerDegreeBasis.Length != poleCount + 1)
            return false;

        Vec3 weightedPoint = default;
        Vec3 weightedPointDerivative = default;
        double totalWeight = 0;
        double totalWeightDerivative = 0;
        for (int poleIndex = 0; poleIndex < poleCount; poleIndex++)
        {
            double basisDerivative = degree * DivideByKnotSpan(
                lowerDegreeBasis[poleIndex],
                _expandedKnots[poleIndex + degree] - _expandedKnots[poleIndex]) -
                degree * DivideByKnotSpan(
                    lowerDegreeBasis[poleIndex + 1],
                    _expandedKnots[poleIndex + degree + 1] - _expandedKnots[poleIndex + 1]);
            double weight = _definition.Weights[poleIndex];
            double weightedBasis = basis[poleIndex] * weight;
            double weightedBasisDerivative = basisDerivative * weight;
            weightedPoint += _definition.Poles[poleIndex] * weightedBasis;
            weightedPointDerivative += _definition.Poles[poleIndex] * weightedBasisDerivative;
            totalWeight += weightedBasis;
            totalWeightDerivative += weightedBasisDerivative;
        }

        if (!double.IsFinite(totalWeight) || totalWeight <= 0 ||
            !double.IsFinite(totalWeightDerivative))
        {
            return false;
        }

        point = weightedPoint * (1 / totalWeight);
        tangent = (
            weightedPointDerivative * totalWeight -
            weightedPoint * totalWeightDerivative) * (1 / (totalWeight * totalWeight));
        return IsFinite(point) && IsFinite(tangent);
    }

    private static double[] EvaluateBasis(
        IReadOnlyList<double> knots,
        int degree,
        double parameter,
        bool useLeftLimit)
    {
        var basis = new double[knots.Count - 1];
        for (int interval = 0; interval < basis.Length; interval++)
        {
            bool containsParameter = useLeftLimit
                ? knots[interval] < parameter && parameter <= knots[interval + 1]
                : knots[interval] <= parameter && parameter < knots[interval + 1];
            basis[interval] = containsParameter ? 1 : 0;
        }

        for (int currentDegree = 1; currentDegree <= degree; currentDegree++)
        {
            var nextBasis = new double[basis.Length - 1];
            for (int index = 0; index < nextBasis.Length; index++)
            {
                double leftSpan = knots[index + currentDegree] - knots[index];
                double rightSpan = knots[index + currentDegree + 1] - knots[index + 1];
                if (leftSpan > 0)
                    nextBasis[index] += (parameter - knots[index]) / leftSpan * basis[index];
                if (rightSpan > 0)
                {
                    nextBasis[index] +=
                        (knots[index + currentDegree + 1] - parameter) / rightSpan * basis[index + 1];
                }
            }

            basis = nextBasis;
        }

        return basis;
    }

    private static double DivideByKnotSpan(double numerator, double span) =>
        span > 0 ? numerator / span : 0;

    private static bool IsFinite(Vec3 value) =>
        double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
}
