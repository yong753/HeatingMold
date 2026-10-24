using HeatingMold.Core.Circuits;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;

namespace HeatingMold.Application.UseCases;

/// <summary>기존 프로젝트의 변경되지 않은 값을 보존하며 다음 revision 후보를 만듭니다.</summary>
internal static class DesignProjectCandidateFactory
{
    /// <summary>지정한 컬렉션만 교체하고 revision과 수정 시각을 갱신합니다.</summary>
    public static DesignProject CreateNext(
        DesignProject source,
        IEnumerable<HeatingRegion>? heatingRegions = null,
        IEnumerable<HeatingPath>? heatingPaths = null,
        IEnumerable<Circuit>? circuits = null,
        IEnumerable<ConnectionPoint>? connectionPoints = null,
        IEnumerable<ConnectionPath>? connectionPaths = null,
        WireSpecification? wireSpecification = null,
        bool replaceWireSpecification = false,
        PlacementConstraints? placementConstraints = null,
        bool replacePlacementConstraints = false)
    {
        ArgumentNullException.ThrowIfNull(source);

        DateTimeOffset modifiedUtc = DateTimeOffset.UtcNow;
        if (modifiedUtc < source.CreatedUtc)
            modifiedUtc = source.CreatedUtc;

        return new DesignProject(
            source.Id,
            source.SchemaVersion,
            source.Revision.Next(),
            source.Name,
            source.CreatedUtc,
            modifiedUtc,
            source.SourceModel,
            source.WorkingCoordinateSystem,
            source.FaceReferences,
            heatingRegions ?? source.HeatingRegions,
            replaceWireSpecification ? wireSpecification : source.WireSpecification,
            replacePlacementConstraints ? placementConstraints : source.PlacementConstraints,
            heatingPaths ?? source.HeatingPaths,
            circuits ?? source.Circuits,
            connectionPoints ?? source.ConnectionPoints,
            connectionPaths ?? source.ConnectionPaths,
            source.Rules);
    }
}
