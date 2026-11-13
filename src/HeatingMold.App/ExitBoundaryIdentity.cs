using System;
using HeatingMold.Core.Geometry;

namespace HeatingMold.App;

/// <summary>
/// 인출 경계 후보의 provenance 동일성. 좌표나 표시용 segment parameter가 아니라
/// 원본 참조와 그 참조에서 선택한 parameter가 같을 때만 같은 후보로 본다.
/// </summary>
internal static class ExitBoundaryIdentity
{
    private const double ParameterTolerance = 1e-9;

    internal static bool Same(ExitBoundaryPoint a, ExitBoundaryPoint b)
    {
        if (a.Binding is { } aCad && b.Binding is { } bCad)
        {
            return aCad.ModelRevision == bCad.ModelRevision
                   && aCad.WorkingContextId == bCad.WorkingContextId
                   && aCad.WorkingContextRevision == bCad.WorkingContextRevision
                   && aCad.EdgeKey == bCad.EdgeKey
                   && SameParameter(aCad.FirstParameter, bCad.FirstParameter)
                   && SameParameter(aCad.LastParameter, bCad.LastParameter)
                   && aCad.Reversed == bCad.Reversed
                   && SameParameter(aCad.Parameter, bCad.Parameter);
        }
        if (a.ArtificialBinding is { } aArtificial && b.ArtificialBinding is { } bArtificial)
        {
            return aArtificial.WorkingContextId == bArtificial.WorkingContextId
                   && aArtificial.WorkingContextRevision == bArtificial.WorkingContextRevision
                   && SameParameter(aArtificial.MinX, bArtificial.MinX)
                   && SameParameter(aArtificial.MinY, bArtificial.MinY)
                   && SameParameter(aArtificial.MaxX, bArtificial.MaxX)
                   && SameParameter(aArtificial.MaxY, bArtificial.MaxY)
                   && aArtificial.Side == bArtificial.Side
                   && SameParameter(aArtificial.FirstParameter, bArtificial.FirstParameter)
                   && SameParameter(aArtificial.LastParameter, bArtificial.LastParameter)
                   && SameParameter(aArtificial.Parameter, bArtificial.Parameter);
        }
        if (a.SplitBinding is { } aSplit && b.SplitBinding is { } bSplit)
        {
            return aSplit.WorkingContextId == bSplit.WorkingContextId
                   && aSplit.WorkingContextRevision == bSplit.WorkingContextRevision
                   && SameParameter(aSplit.AX, bSplit.AX)
                   && SameParameter(aSplit.AY, bSplit.AY)
                   && SameParameter(aSplit.BX, bSplit.BX)
                   && SameParameter(aSplit.BY, bSplit.BY)
                   && SameParameter(aSplit.FirstParameter, bSplit.FirstParameter)
                   && SameParameter(aSplit.LastParameter, bSplit.LastParameter)
                   && SameParameter(aSplit.Parameter, bSplit.Parameter);
        }

        // Provenance가 없는 테스트/구형 fixture는 표시용 segment와 parameter가 모두 같을 때만
        // 같은 점으로 본다. 실제 제품 경계 후보는 항상 위 세 종류 중 하나를 가진다.
        bool aHasProvenance = a.Binding.HasValue || a.ArtificialBinding.HasValue || a.SplitBinding.HasValue;
        bool bHasProvenance = b.Binding.HasValue || b.ArtificialBinding.HasValue || b.SplitBinding.HasValue;
        if (aHasProvenance || bHasProvenance) return false;
        return a.SegmentIndex == b.SegmentIndex
               && SameParameter(a.SegmentParameter, b.SegmentParameter);
    }

    private static bool SameParameter(double a, double b) =>
        double.IsFinite(a) && double.IsFinite(b) && Math.Abs(a - b) <= ParameterTolerance;
}
