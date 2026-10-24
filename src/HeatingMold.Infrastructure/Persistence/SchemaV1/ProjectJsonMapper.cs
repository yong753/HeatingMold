using HeatingMold.Core.Circuits;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Routing;
using System.Text.Json;

namespace HeatingMold.Infrastructure.Persistence.SchemaV1;

/// <summary>Core 설계와 스키마 1 DTO 사이의 변환을 한 곳에서 수행합니다.</summary>
internal static class ProjectJsonMapper
{
    /// <summary>확정 설계의 영속 값만 스키마 1 구조로 변환합니다.</summary>
    public static ProjectV1Dto ToDto(DesignProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        return new ProjectV1Dto(
            project.Id.Value,
            project.SchemaVersion,
            project.Revision.Value,
            project.Name,
            project.CreatedUtc,
            project.ModifiedUtc,
            ToDto(project.SourceModel),
            ToDto(project.WorkingCoordinateSystem),
            project.FaceReferences.Select(ToDto).ToArray(),
            project.HeatingRegions.Select(ToDto).ToArray(),
            project.WireSpecification is null ? null : ToDto(project.WireSpecification),
            project.PlacementConstraints is null ? null : ToDto(project.PlacementConstraints),
            project.HeatingPaths.Select(ToDto).ToArray(),
            project.Circuits.Select(ToDto).ToArray(),
            project.ConnectionPoints.Select(ToDto).ToArray(),
            project.ConnectionPaths.Select(ToDto).ToArray(),
            ToDto(project.Rules));
    }

    /// <summary>스키마 1 DTO를 생성자 검증을 거친 불변 Core 설계로 복원합니다.</summary>
    public static DesignProject ToDomain(ProjectV1Dto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        return new DesignProject(
            new ProjectId(dto.ProjectId),
            dto.SchemaVersion,
            new DesignRevision(dto.DesignRevision),
            Required(dto.Name, "name"),
            dto.CreatedUtc,
            dto.ModifiedUtc,
            ToDomain(Required(dto.SourceModel, "sourceModel")),
            ToDomain(Required(dto.WorkingCoordinateSystem, "workingCoordinateSystem")),
            MapRequiredItems(dto.FaceReferences, "faceReferences", ToDomain),
            MapRequiredItems(dto.HeatingRegions, "heatingRegions", ToDomain),
            dto.WireSpecification is null ? null : ToDomain(dto.WireSpecification),
            dto.PlacementConstraints is null ? null : ToDomain(dto.PlacementConstraints),
            MapRequiredItems(dto.HeatingPaths, "heatingPaths", ToDomain),
            MapRequiredItems(dto.Circuits, "circuits", ToDomain),
            MapRequiredItems(dto.ConnectionPoints, "connectionPoints", ToDomain),
            MapRequiredItems(dto.ConnectionPaths, "connectionPaths", ToDomain),
            ToDomain(Required(dto.Rules, "rules")));
    }

    private static ModelIdentityV1Dto ToDto(ModelIdentity value) => new(
        value.SourceSha256,
        value.SourceUnitToMillimeter,
        value.ImporterName,
        value.ImporterVersion,
        value.FaceFingerprintVersion);

    private static ModelIdentity ToDomain(ModelIdentityV1Dto value) => new(
        Required(value.SourceSha256, "sourceModel.sourceSha256"),
        value.SourceUnitToMillimeter,
        Required(value.ImporterName, "sourceModel.importerName"),
        Required(value.ImporterVersion, "sourceModel.importerVersion"),
        Required(value.FaceFingerprintVersion, "sourceModel.faceFingerprintVersion"));

    private static WorkingCoordinateSystemV1Dto ToDto(WorkingCoordinateSystemDefinition value) => new(
        ToDto(value.Origin),
        ToDto(value.AxisX),
        ToDto(value.AxisY),
        ToDto(value.Normal));

    private static WorkingCoordinateSystemDefinition ToDomain(WorkingCoordinateSystemV1Dto value) => new(
        ToDomain(Required(value.Origin, "workingCoordinateSystem.origin")),
        ToDomain(Required(value.AxisX, "workingCoordinateSystem.axisX")),
        ToDomain(Required(value.AxisY, "workingCoordinateSystem.axisY")),
        ToDomain(Required(value.Normal, "workingCoordinateSystem.normal")));

    private static Vec2V1Dto ToDto(Vec2 value) => new(value.X, value.Y);

    private static Vec2 ToDomain(Vec2V1Dto value) => new(value.X, value.Y);

    private static Vec3V1Dto ToDto(Vec3 value) => new(value.X, value.Y, value.Z);

    private static Vec3 ToDomain(Vec3V1Dto value) => new(value.X, value.Y, value.Z);

    private static FaceReferenceV1Dto ToDto(FaceReference value) => new(
        value.Id.Value,
        value.SourceModelSha256,
        value.ImportIndexHint,
        value.GeometryKind,
        value.SurfaceArea.Value,
        ToDto(value.Center),
        value.BoundaryFingerprint,
        value.AdjacentFaceFingerprints.ToArray());

    private static FaceReference ToDomain(FaceReferenceV1Dto value) => new(
        new FaceRefId(value.FaceReferenceId),
        Required(value.SourceModelSha256, "faceReference.sourceModelSha256"),
        value.ImportIndexHint,
        value.GeometryKind,
        new AreaMm2(value.SurfaceAreaMm2),
        ToDomain(Required(value.Center, "faceReference.center")),
        Required(value.BoundaryFingerprint, "faceReference.boundaryFingerprint"),
        RequiredItems(value.AdjacentFaceFingerprints, "faceReference.adjacentFaceFingerprints"));

    private static HeatingRegionV1Dto ToDto(HeatingRegion value) => new(
        value.Id.Value,
        value.FaceReferences.Select(faceId => faceId.Value).ToArray(),
        value.OriginalBoundaryLoops.Select(ToDto).ToArray(),
        value.InclusionLoops.Select(ToDto).ToArray(),
        value.ExclusionLoops.Select(ToDto).ToArray(),
        value.EffectiveBoundaryLoops.Select(ToDto).ToArray(),
        ToDto(value.ExitSide));

    private static HeatingRegion ToDomain(HeatingRegionV1Dto value) => new(
        new RegionId(value.RegionId),
        RequiredItems(value.FaceReferenceIds, "heatingRegion.faceReferenceIds").Select(id => new FaceRefId(id)),
        MapRequiredItems(value.OriginalBoundaryLoops, "heatingRegion.originalBoundaryLoops", ToDomain),
        MapRequiredItems(value.InclusionLoops, "heatingRegion.inclusionLoops", ToDomain),
        MapRequiredItems(value.ExclusionLoops, "heatingRegion.exclusionLoops", ToDomain),
        MapRequiredItems(value.EffectiveBoundaryLoops, "heatingRegion.effectiveBoundaryLoops", ToDomain),
        ToDomain(Required(value.ExitSide, "heatingRegion.exitSide")));

    private static BoundaryLoopV1Dto ToDto(BoundaryLoop value) => new(
        value.Id.Value,
        value.IsHole,
        value.Segments.Select(ToDto).ToArray());

    private static BoundaryLoop ToDomain(BoundaryLoopV1Dto value) => new(
        new BoundaryLoopId(value.BoundaryLoopId),
        MapRequiredItems(value.Segments, "boundaryLoop.segments", ToDomain),
        value.IsHole);

    private static BoundarySegmentV1Dto ToDto(BoundarySegment value) => new(
        value.Id.Value,
        ToDto(value.Start),
        ToDto(value.End),
        value.SourceFaceReferenceId?.Value);

    private static BoundarySegment ToDomain(BoundarySegmentV1Dto value) => new(
        new BoundarySegmentId(value.BoundarySegmentId),
        ToDomain(Required(value.Start, "boundarySegment.start")),
        ToDomain(Required(value.End, "boundarySegment.end")),
        value.SourceFaceReferenceId is { } faceId ? new FaceRefId(faceId) : null);

    private static ExitSideV1Dto ToDto(ExitSideReference value) => new(
        value.Ranges.Select(range => new BoundarySegmentRangeV1Dto(
            range.BoundarySegmentId.Value,
            range.StartParameter,
            range.EndParameter)).ToArray());

    private static ExitSideReference ToDomain(ExitSideV1Dto value) => new(
        RequiredItems(value.Ranges, "exitSide.ranges").Select(range => new BoundarySegmentRange(
            new BoundarySegmentId(range.BoundarySegmentId),
            range.StartParameter,
            range.EndParameter)));

    private static WireSpecificationV1Dto ToDto(WireSpecification value) => new(
        value.MaterialName,
        value.DiameterMm.Value,
        value.ResistanceOhmsPerMeter.Value);

    private static WireSpecification ToDomain(WireSpecificationV1Dto value) => new(
        Required(value.MaterialName, "wireSpecification.materialName"),
        new LengthMm(value.DiameterMm),
        new ResistanceOhmsPerMeter(value.ResistanceOhmsPerMeter));

    private static PlacementConstraintsV1Dto ToDto(PlacementConstraints value) => new(
        value.MinimumClearGapMm.Value,
        value.BoundaryMarginMm.Value,
        value.InsertionDepthMm.Value,
        value.InsertionDepthReference,
        value.Direction,
        ToDto(value.StartAnchor),
        ToDto(value.EndAnchor),
        ToDto(value.ExitSide));

    private static PlacementConstraints ToDomain(PlacementConstraintsV1Dto value) => new(
        new LengthMm(value.MinimumClearGapMm),
        new LengthMm(value.BoundaryMarginMm),
        new LengthMm(value.InsertionDepthMm),
        value.Direction,
        ToDomain(Required(value.StartAnchor, "placementConstraints.startAnchor")),
        ToDomain(Required(value.EndAnchor, "placementConstraints.endAnchor")),
        ToDomain(Required(value.ExitSide, "placementConstraints.exitSide")),
        value.InsertionDepthReference);

    private static SurfaceAnchorV1Dto ToDto(SurfaceAnchor value) => new(
        value.FaceReferenceId.Value,
        ToDto(value.FaceUv),
        ToDto(value.WorkingUv),
        ToDto(value.ModelPoint));

    private static SurfaceAnchor ToDomain(SurfaceAnchorV1Dto value) => new(
        new FaceRefId(value.FaceReferenceId),
        ToDomain(Required(value.FaceUv, "surfaceAnchor.faceUv")),
        ToDomain(Required(value.WorkingUv, "surfaceAnchor.workingUv")),
        ToDomain(Required(value.ModelPoint, "surfaceAnchor.modelPoint")));

    private static HeatingPathV1Dto ToDto(HeatingPath value) => new(
        value.Id.Value,
        ToDto(value.GenerationSource),
        value.ControlPoints.Select(ToDto).ToArray(),
        value.GeneratedSegments.Select(ToDto).ToArray(),
        value.Segments.Select(ToDto).ToArray());

    private static HeatingPath ToDomain(HeatingPathV1Dto value) => new(
        new HeatingPathId(value.HeatingPathId),
        ToDomain(Required(value.GenerationSource, "heatingPath.generationSource")),
        MapRequiredItems(value.ControlPoints, "heatingPath.controlPoints", ToDomain),
        MapRequiredItems(value.GeneratedSegments, "heatingPath.generatedSegments", ToDomain),
        MapRequiredItems(value.Segments, "heatingPath.segments", ToDomain));

    private static HeatingPathGenerationSourceV1Dto ToDto(HeatingPathGenerationSource value) => new(
        value.RegionId.Value,
        ToDto(value.PlacementConstraints),
        ToDto(value.WireSpecification),
        value.RuleVersion);

    private static HeatingPathGenerationSource ToDomain(HeatingPathGenerationSourceV1Dto value) => new(
        new RegionId(value.RegionId),
        ToDomain(Required(value.PlacementConstraints, "heatingPath.generationSource.placementConstraints")),
        ToDomain(Required(value.WireSpecification, "heatingPath.generationSource.wireSpecification")),
        Required(value.RuleVersion, "heatingPath.generationSource.ruleVersion"));

    private static ControlPointV1Dto ToDto(ControlPoint value) => new(value.Id.Value, ToDto(value.Position));

    private static ControlPoint ToDomain(ControlPointV1Dto value) => new(
        new ControlPointId(value.ControlPointId),
        ToDomain(Required(value.Position, "controlPoint.position")));

    private static HeatingPathSegmentV1Dto ToDto(HeatingPathSegment value) => new(
        value.Id.Value,
        value.Kind,
        value.StartControlPointId.Value,
        value.EndControlPointId.Value,
        value.Sequence,
        value.LaneIndex,
        value.IsActive,
        ToDto(value.Curve));

    private static HeatingPathSegment ToDomain(HeatingPathSegmentV1Dto value) => new(
        new HeatingPathSegmentId(value.HeatingPathSegmentId),
        value.Kind,
        new ControlPointId(value.StartControlPointId),
        new ControlPointId(value.EndControlPointId),
        value.Sequence,
        value.IsActive,
        ToDomain(Required(value.Curve, "heatingPathSegment.curve")),
        value.LaneIndex);

    private static CurveDefinitionV1Dto ToDto(CurveDefinition value) => value switch
    {
        LineCurveDefinition line => new CurveDefinitionV1Dto
        {
            CurveType = nameof(CurveKind.Line),
            Direction = line.Direction,
            Start = ToDto(line.Start),
            End = ToDto(line.End)
        },
        PolylineCurveDefinition polyline => new CurveDefinitionV1Dto
        {
            CurveType = nameof(CurveKind.Polyline),
            Direction = polyline.Direction,
            Points = polyline.Points.Select(ToDto).ToArray()
        },
        ArcCurveDefinition arc => new CurveDefinitionV1Dto
        {
            CurveType = nameof(CurveKind.Arc),
            Direction = arc.Direction,
            Plane = ToDto(arc.Plane),
            Center = ToDto(arc.Center),
            RadiusMm = arc.RadiusMm.Value,
            StartAngleRad = arc.StartAngleRad,
            EndAngleRad = arc.EndAngleRad
        },
        BSplineCurveDefinition spline => new CurveDefinitionV1Dto
        {
            CurveType = nameof(CurveKind.BSpline),
            Direction = spline.Direction,
            Degree = spline.Degree,
            Poles = spline.Poles.Select(ToDto).ToArray(),
            Weights = spline.Weights.ToArray(),
            Knots = spline.Knots.ToArray(),
            Multiplicities = spline.Multiplicities.ToArray(),
            IsPeriodic = spline.IsPeriodic,
            TrimStart = spline.TrimStart,
            TrimEnd = spline.TrimEnd
        },
        _ => throw new JsonException($"저장할 수 없는 곡선 정의 형식입니다: {value.GetType().Name}.")
    };

    private static CurveDefinition ToDomain(CurveDefinitionV1Dto value)
    {
        CurveKind kind = value.CurveType switch
        {
            nameof(CurveKind.Line) => CurveKind.Line,
            nameof(CurveKind.Polyline) => CurveKind.Polyline,
            nameof(CurveKind.Arc) => CurveKind.Arc,
            nameof(CurveKind.BSpline) => CurveKind.BSpline,
            _ => throw new JsonException($"지원하지 않는 곡선 종류입니다: {value.CurveType}.")
        };
        ValidateCurvePayload(value, kind);

        return kind switch
        {
            CurveKind.Line => new LineCurveDefinition(
                ToDomain(Required(value.Start, "curve.start")),
                ToDomain(Required(value.End, "curve.end")),
                value.Direction),
            CurveKind.Polyline => new PolylineCurveDefinition(
                MapRequiredItems(value.Points, "curve.points", ToDomain),
                value.Direction),
            CurveKind.Arc => new ArcCurveDefinition(
                ToDomain(Required(value.Plane, "curve.plane")),
                ToDomain(Required(value.Center, "curve.center")),
                new LengthMm(RequiredValue(value.RadiusMm, "curve.radiusMm")),
                RequiredValue(value.StartAngleRad, "curve.startAngleRad"),
                RequiredValue(value.EndAngleRad, "curve.endAngleRad"),
                value.Direction),
            CurveKind.BSpline => new BSplineCurveDefinition(
                RequiredValue(value.Degree, "curve.degree"),
                MapRequiredItems(value.Poles, "curve.poles", ToDomain),
                RequiredItems(value.Weights, "curve.weights"),
                RequiredItems(value.Knots, "curve.knots"),
                RequiredItems(value.Multiplicities, "curve.multiplicities"),
                RequiredValue(value.IsPeriodic, "curve.isPeriodic"),
                RequiredValue(value.TrimStart, "curve.trimStart"),
                RequiredValue(value.TrimEnd, "curve.trimEnd"),
                value.Direction),
            _ => throw new JsonException("지원하지 않는 곡선 정의입니다.")
        };
    }

    private static void ValidateCurvePayload(CurveDefinitionV1Dto value, CurveKind kind)
    {
        string[] expectedFields = kind switch
        {
            CurveKind.Line => ["start", "end"],
            CurveKind.Polyline => ["points"],
            CurveKind.Arc => ["plane", "center", "radiusMm", "startAngleRad", "endAngleRad"],
            CurveKind.BSpline =>
            [
                "degree", "poles", "weights", "knots", "multiplicities", "isPeriodic", "trimStart", "trimEnd"
            ],
            _ => throw new JsonException($"지원하지 않는 곡선 정의입니다: {kind}.")
        };
        (string Name, bool HasValue)[] fields =
        [
            ("start", value.Start is not null),
            ("end", value.End is not null),
            ("points", value.Points is not null),
            ("plane", value.Plane is not null),
            ("center", value.Center is not null),
            ("radiusMm", value.RadiusMm.HasValue),
            ("startAngleRad", value.StartAngleRad.HasValue),
            ("endAngleRad", value.EndAngleRad.HasValue),
            ("degree", value.Degree.HasValue),
            ("poles", value.Poles is not null),
            ("weights", value.Weights is not null),
            ("knots", value.Knots is not null),
            ("multiplicities", value.Multiplicities is not null),
            ("isPeriodic", value.IsPeriodic.HasValue),
            ("trimStart", value.TrimStart.HasValue),
            ("trimEnd", value.TrimEnd.HasValue)
        ];

        foreach ((string fieldName, bool hasValue) in fields)
        {
            bool isExpected = expectedFields.Contains(fieldName, StringComparer.Ordinal);
            if (isExpected != hasValue)
                throw new JsonException($"곡선 종류 {value.CurveType}의 필드 구성이 올바르지 않습니다: {fieldName}.");
        }
    }

    private static ProjectionFrameV1Dto ToDto(ProjectionFrame value) => new(
        ToDto(value.Origin),
        ToDto(value.AxisX),
        ToDto(value.Normal));

    private static ProjectionFrame ToDomain(ProjectionFrameV1Dto value) => new(
        ToDomain(Required(value.Origin, "projectionFrame.origin")),
        ToDomain(Required(value.Normal, "projectionFrame.normal")),
        ToDomain(Required(value.AxisX, "projectionFrame.axisX")));

    private static CircuitV1Dto ToDto(Circuit value) => new(
        value.Id.Value,
        value.Sequence,
        value.Segments.Select(segment => new CircuitSegmentReferenceV1Dto(
            segment.SegmentId.Value,
            segment.Sequence,
            segment.IsReversed)).ToArray(),
        value.StartConnectionPointId.Value,
        value.EndConnectionPointId.Value,
        ToDto(value.Supply));

    private static Circuit ToDomain(CircuitV1Dto value) => new(
        new CircuitId(value.CircuitId),
        value.Sequence,
        RequiredItems(value.Segments, "circuit.segments").Select(segment => new CircuitSegmentReference(
            new HeatingPathSegmentId(segment.SegmentId),
            segment.Sequence,
            segment.IsReversed)),
        new ConnectionPointId(value.StartConnectionPointId),
        new ConnectionPointId(value.EndConnectionPointId),
        ToDomain(Required(value.Supply, "circuit.supply")));

    private static SupplyV1Dto ToDto(Supply value) => new(
        value.ActualVoltageVrms?.Value,
        value.MaximumVoltageVrms?.Value,
        value.MaximumCurrentAmps?.Value,
        value.MaximumPowerWatts?.Value);

    private static Supply ToDomain(SupplyV1Dto value) => new(
        value.ActualVoltageVrms is { } actualVoltage ? new VoltageVrms(actualVoltage) : null,
        value.MaximumVoltageVrms is { } maximumVoltage ? new VoltageVrms(maximumVoltage) : null,
        value.MaximumCurrentAmps is { } maximumCurrent ? new CurrentAmps(maximumCurrent) : null,
        value.MaximumPowerWatts is { } maximumPower ? new PowerWatts(maximumPower) : null);

    private static ConnectionPointV1Dto ToDto(ConnectionPoint value) => new(
        value.Id.Value,
        value.CircuitId.Value,
        value.Role,
        value.HeatingPathSegmentId.Value,
        value.ControlPointId.Value,
        ToDto(value.SurfaceAnchor),
        ToDto(value.ExternalConnectionPoint));

    private static ConnectionPoint ToDomain(ConnectionPointV1Dto value) => new(
        new ConnectionPointId(value.ConnectionPointId),
        new CircuitId(value.CircuitId),
        value.Role,
        new HeatingPathSegmentId(value.HeatingPathSegmentId),
        new ControlPointId(value.ControlPointId),
        ToDomain(Required(value.SurfaceAnchor, "connectionPoint.surfaceAnchor")),
        ToDomain(Required(value.ExternalConnectionPoint, "connectionPoint.externalConnectionPoint")));

    private static ConnectionPathV1Dto ToDto(ConnectionPath value) => new(
        value.Id.Value,
        value.ConnectionPointId.Value,
        value.Segments.Select(segment => new ConnectionPathSegmentV1Dto(
            segment.Id.Value,
            segment.Sequence,
            segment.ConductorRole,
            segment.GeometryDomain,
            segment.AdditionalAllowanceMm.Value,
            segment.IsMoldExitTransition,
            ToDto(segment.Curve))).ToArray());

    private static ConnectionPath ToDomain(ConnectionPathV1Dto value) => new(
        new ConnectionPathId(value.ConnectionPathId),
        new ConnectionPointId(value.ConnectionPointId),
        RequiredItems(value.Segments, "connectionPath.segments").Select(segment => new ConnectionPathSegment(
            new ConnectionPathSegmentId(segment.ConnectionPathSegmentId),
            segment.Sequence,
            segment.ConductorRole,
            segment.GeometryDomain,
            new LengthMm(segment.AdditionalAllowanceMm),
            ToDomain(Required(segment.Curve, "connectionPathSegment.curve")),
            segment.IsMoldExitTransition)));

    private static DesignRuleSetV1Dto ToDto(DesignRuleSet value) => new(
        value.GenerationRuleVersion,
        value.ValidationRuleVersion,
        value.AreaRuleVersion,
        value.ElectricalRuleVersion,
        ToDto(value.GeometryTolerances));

    private static DesignRuleSet ToDomain(DesignRuleSetV1Dto value) => new(
        Required(value.GenerationRuleVersion, "rules.generationRuleVersion"),
        Required(value.ValidationRuleVersion, "rules.validationRuleVersion"),
        Required(value.AreaRuleVersion, "rules.areaRuleVersion"),
        Required(value.ElectricalRuleVersion, "rules.electricalRuleVersion"),
        ToDomain(Required(value.GeometryTolerances, "rules.geometryTolerances")));

    private static GeometryToleranceProfileV1Dto ToDto(GeometryToleranceProfile value) => new(
        value.ConnectionToleranceMm,
        value.MeshDeflectionMm,
        value.MeshAngularToleranceRad,
        value.MappingLeafLengthMm,
        value.MappingChordDeviationMm,
        value.MappingMaximumDepth,
        value.MappingMaximumProbes,
        value.MaximumContinuousSampleSpacingMm);

    private static GeometryToleranceProfile ToDomain(GeometryToleranceProfileV1Dto value) => new(
        value.ConnectionToleranceMm,
        value.MeshDeflectionMm,
        value.MeshAngularToleranceRad,
        value.MappingLeafLengthMm,
        value.MappingChordDeviationMm,
        value.MappingMaximumDepth,
        value.MappingMaximumProbes,
        value.MaximumContinuousSampleSpacingMm);

    private static T Required<T>(T? value, string fieldName)
        where T : class => value ?? throw new JsonException($"필수 프로젝트 필드가 없습니다: {fieldName}.");

    private static T RequiredValue<T>(T? value, string fieldName)
        where T : struct => value ?? throw new JsonException($"필수 프로젝트 필드가 없습니다: {fieldName}.");

    private static T[] RequiredItems<T>(T[]? values, string fieldName) => values
        ?? throw new JsonException($"필수 프로젝트 배열이 없습니다: {fieldName}.");

    private static TTarget[] MapRequiredItems<TSource, TTarget>(
        TSource[]? values,
        string fieldName,
        Func<TSource, TTarget> map)
        where TSource : class
    {
        TSource[] requiredValues = RequiredItems(values, fieldName);
        var mapped = new TTarget[requiredValues.Length];
        for (int index = 0; index < requiredValues.Length; index++)
        {
            mapped[index] = map(Required(requiredValues[index], $"{fieldName}[{index}]"));
        }

        return mapped;
    }
}
