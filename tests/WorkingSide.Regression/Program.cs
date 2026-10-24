using System.Reflection;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using HeatingMold.App;
using HeatingMold.Application.Sessions;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Regions;
using HeatingMold.Core.Primitives;
using HeatingMold.Design.Regions;
using HeatingMold.OccBridge;

static class Program
{
    static int count;
    static void Check(bool value,string label)
    { if (!value) throw new Exception(label); Console.WriteLine("PASS "+label); ++count; }
    static ProjectionFrame Frame(WorkingSurfaceContext c) => new(new(c.Frame[0],c.Frame[1],c.Frame[2]),
        new(c.Frame[9],c.Frame[10],c.Frame[11]),new(c.Frame[3],c.Frame[4],c.Frame[5]));
    static WorkingSurfaceHit Query(WorkingSurfaceContext c,Vec3 world)
    { var uv=Frame(c).Project(world); return Bridge.QueryWorkingSurface(c.Id,c.Revision,uv.X,uv.Y); }
    static WorkingSurfaceHit QueryCandidate(long candidateId,WorkingSurfaceContext c,Vec3 world)
    {
        var uv=Frame(c).Project(world);
        return Bridge.QueryPreparedCandidateWorkingSurface(candidateId,c.Id,c.Revision,uv.X,uv.Y);
    }
    /// <summary>평판·경사 fixture report가 세 방향의 깊이 표본을 모두 담는지 확인합니다.</summary>
    static bool HasDirectionalDepthReports(IReadOnlyList<string> reportPaths)
    {
        string[] fixtureNames = ["flat", "slope30", "slope60"];
        string[] requiredPathKinds = ["Longest", "AlongWorkingU", "AlongWorkingV"];

        foreach (string fixtureName in fixtureNames)
        {
            string? reportPath = reportPaths.FirstOrDefault(path =>
                string.Equals(
                    Path.GetFileNameWithoutExtension(path),
                    fixtureName,
                    StringComparison.OrdinalIgnoreCase));
            if (reportPath is null)
                return false;

            using JsonDocument report = JsonDocument.Parse(File.ReadAllText(reportPath));
            JsonElement axes = report.RootElement.GetProperty("axes");
            if (axes.GetArrayLength() != 1)
                return false;

            JsonElement axis = axes[0];
            HashSet<string> selectedPathKinds = axis
                .GetProperty("selectedDepthPaths")
                .EnumerateArray()
                .Select(path => path.GetProperty("pathKind").GetString())
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
            HashSet<string> measuredPathKinds = axis
                .GetProperty("depthCases")
                .EnumerateArray()
                .Select(depthCase => depthCase.GetProperty("pathKind").GetString())
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);

            if (requiredPathKinds.Any(pathKind =>
                !selectedPathKinds.Contains(pathKind) || !measuredPathKinds.Contains(pathKind)))
            {
                return false;
            }
        }

        return true;
    }
    /// <summary>두 후보 표면 hit 위에 놓인 직선을 Native 곡선 입력으로 만듭니다.</summary>
    static CurveDefinitionData? CreateCandidateLineAboveHits(WorkingSurfaceHit? startHit,WorkingSurfaceHit? endHit)
    {
        if (startHit?.Point is not { Length: 3 } start || endHit?.Point is not { Length: 3 } end)
            return null;

        return new CurveDefinitionData
        {
            Kind="Line",
            Direction="Forward",
            Start=new[]{start[0],start[1],start[2]+5},
            End=new[]{end[0],end[1],end[2]+5}
        };
    }
    /// <summary>회귀 입력에 사용할 exact Native 직선 정의를 만듭니다.</summary>
    static CurveDefinitionData NativeLine(Vec3 start,Vec3 end) => new()
    {
        Kind="Line",
        Direction="Forward",
        Start=new[]{start.X,start.Y,start.Z},
        End=new[]{end.X,end.Y,end.Z}
    };
    /// <summary>활성 작업 context에서 exact Native 곡선 간격을 측정합니다.</summary>
    static CurveClearanceResultData MeasureActiveClearance(
        WorkingSurfaceContext context,
        params CurveDefinitionData[] curves) => MeasureActiveClearance(context, Array.Empty<int>(), curves);

    /// <summary>설계에서 확정한 연속 구간 인덱스를 지정해 Native 간격을 측정합니다.</summary>
    static CurveClearanceResultData MeasureActiveClearance(
        WorkingSurfaceContext context,
        int[] localCurveIndexPairs,
        params CurveDefinitionData[] curves) => Bridge.MeasureWorkingCurveClearance(
            context.Id,
            context.Revision,
            curves,
            localCurveIndexPairs,
            .4,
            .01,
            10000);
    static IReadOnlyList<ProjectedModelBoundaryRing> Rings(WorkingSurfaceContext c)
    {
        var f=Frame(c); var triangles=new List<IReadOnlyList<Vec2>>();
        for (int i=0;i<c.Triangles.Length;i+=9)
            triangles.Add(new[]{f.Project(new(c.Triangles[i],c.Triangles[i+1],c.Triangles[i+2])),
                f.Project(new(c.Triangles[i+3],c.Triangles[i+4],c.Triangles[i+5])),
                f.Project(new(c.Triangles[i+6],c.Triangles[i+7],c.Triangles[i+8]))});
        return ProjectedModelBoundary.FromTriangles(triangles,c.MeshDeflection);
    }
    static object Invoke(object target,string name,params object[] args) =>
        target.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(target,args);
    static T Field<T>(object target,string name) =>
        (T)target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(target)!;
    /// <summary>화면의 안정 Face 참조 기반 선택 ViewModel을 반환합니다.</summary>
    static object RegionSelection(object target)
    {
        object mainWindowViewModel=Field<object>(target,"_mainWindowViewModel");
        return mainWindowViewModel.GetType().GetProperty("RegionSelection")!.GetValue(mainWindowViewModel)!;
    }
    /// <summary>화면에서 선택한 안정 Face 참조 목록을 반환합니다.</summary>
    static IReadOnlyList<FaceRefId> SelectedFaceReferences(object target)
    {
        object regionSelection=RegionSelection(target);
        return (IReadOnlyList<FaceRefId>)regionSelection.GetType().GetProperty("SelectedFaceReferences")!.GetValue(regionSelection)!;
    }
    /// <summary>선택 영역의 현재 경계 루프를 반환합니다.</summary>
    static IReadOnlyList<BoundaryLoop> SelectedBoundaryLoops(object target)
    {
        object regionSelection=RegionSelection(target);
        return (IReadOnlyList<BoundaryLoop>)regionSelection.GetType().GetProperty("BoundaryLoops")!.GetValue(regionSelection)!;
    }
    /// <summary>검증용 삼각 경계를 만듭니다.</summary>
    static BoundaryLoop CreateSelectionBoundary(FaceRefId faceReferenceId) => new(
        BoundaryLoopId.New(),
        [
            new BoundarySegment(BoundarySegmentId.New(),new Vec2(0,0),new Vec2(3,0),faceReferenceId),
            new BoundarySegment(BoundarySegmentId.New(),new Vec2(3,0),new Vec2(0,2),faceReferenceId),
            new BoundarySegment(BoundarySegmentId.New(),new Vec2(0,2),new Vec2(0,0),faceReferenceId)
        ],
        isHole:false);
    static void SetField(object target,string name,object value) =>
        target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(target,value);
    [STAThread]
    static void Main(string[] args)
    {
        bool runGeo00 = args.Length == 4 && args[1] == "--geo-00";
        if (args.Length != 1 && !runGeo00)
            throw new ArgumentException("사용법: WorkingSide.Regression <fixture-dir> [--geo-00 <mold.stp> <report.json>]");

        string path=Path.Combine(args[0],"layers.stp");
        Check(Bridge.GetFaceGeometryFeatures().Status==FaceFeatureQueryStatus.InvalidModel,
            "face feature query reports that no model is loaded");
        var initialLoad=Bridge.LoadStepDetailed(path);
        Check(initialLoad.Status==ModelLoadStatus.Committed && initialLoad.ModelRevision==Bridge.GetModelRevision(),
            "detailed load reports model commit and active revision");
        Check(Bridge.LoadStep(path),"legacy boolean load API remains compatible");
        Check(Bridge.CreateWorkingSurfaceContext(0,0,0,1,0,0)==null,"invalid working direction cannot create context");
        var front=Bridge.CreateWorkingSurfaceContext(0,0,1,1,0,0);
        Check(front!=null && front.Triangles.Length>0,"whole-model native context and mesh");
        long loadedRevision=Bridge.GetModelRevision();
        int loadedFaceCount=Bridge.GetShapeInfo().Faces;
        var faceFeatures=Bridge.GetFaceGeometryFeatures();
        var featureByIndex=faceFeatures.Features?.ToDictionary(feature=>feature.FaceIndex);
        Check(faceFeatures.Status==FaceFeatureQueryStatus.Success && featureByIndex?.Count==loadedFaceCount &&
            featureByIndex.Values.All(feature=>!string.IsNullOrWhiteSpace(feature.SurfaceType) &&
                double.IsFinite(feature.SurfaceAreaMm2) && feature.SurfaceAreaMm2>0 &&
                feature.Center is { Length: 3 } && feature.AdjacentFaceIndices is not null),
            "face feature query returns complete surface, area, center, and adjacency data");
        var missingLoad=Bridge.LoadStepDetailed(Path.Combine(args[0],"missing-model.step"));
        bool missingFileRejected=missingLoad.Status==ModelLoadStatus.FailedBeforeCommit && !string.IsNullOrWhiteSpace(missingLoad.Diagnostic);
        string malformedPath=Path.Combine(args[0],$"invalid-load-{Guid.NewGuid():N}.step");
        File.WriteAllText(malformedPath,"not a STEP model");
        bool malformedFileRejected;
        try
        {
            malformedFileRejected=Bridge.LoadStepDetailed(malformedPath).Status==ModelLoadStatus.FailedBeforeCommit;
        }
        finally
        {
            File.Delete(malformedPath);
        }
        Check(missingFileRejected && malformedFileRejected && Bridge.GetModelRevision()==loadedRevision &&
            Bridge.GetShapeInfo().Faces==loadedFaceCount &&
            Bridge.QueryWorkingSurface(front.Id,front.Revision,0,0).Status==WorkingSurfaceQueryStatus.Hit,
            "missing and malformed STEP loads preserve active model, revision, and working context");
        Check(front.Frame[2]>20,"datum beyond whole-model maximum");
        var top=Query(front,new(0,0,0)); var exposed=Query(front,new(40,0,0));
        Check(top.Status==WorkingSurfaceQueryStatus.Hit && Math.Abs(top.Point[2]-20)<1e-6,"nearest front full-model hit hides lower surface");
        double[] topAtFaceUv=top.FaceUv is { Length: 2 } faceUv
            ? Bridge.SampleUvTo3D(top.FaceId,faceUv[0],faceUv[1])
            : null;
        bool firstHitUvRoundTrip=topAtFaceUv is { Length: 3 } sampledPoint &&
            Math.Sqrt(Math.Pow(sampledPoint[0]-top.Point[0],2)+Math.Pow(sampledPoint[1]-top.Point[1],2)+Math.Pow(sampledPoint[2]-top.Point[2],2))<1e-6;
        Check(firstHitUvRoundTrip,
            "first-hit preserves original face UV coordinates");
        Check(exposed.Status==WorkingSurfaceQueryStatus.Hit && Math.Abs(exposed.Point[2])<1e-6,"partly occluded face usable only at exposed points");
        Check(top.FaceId!=exposed.FaceId,"first hit returns actual face ID, not selected filter");
        var hiddenSelection=Bridge.QueryWorkingSurfaceForFaces(front.Id,front.Revision,new[]{exposed.FaceId},0,0);
        Check(hiddenSelection.Status==WorkingSurfaceQueryStatus.Miss && hiddenSelection.Point==null,
            "selected lower face cannot fall through nearer unselected first hit");
        var visibleSelection=Bridge.QueryWorkingSurfaceForFaces(front.Id,front.Revision,new[]{exposed.FaceId},40,0);
        Check(visibleSelection.Status==WorkingSurfaceQueryStatus.Hit && visibleSelection.IsSelectedFace &&
            visibleSelection.FaceId==exposed.FaceId && visibleSelection.Normal?.Length==3 && visibleSelection.FaceUv?.Length==2,
            "selected exposed first hit returns face, XYZ, normal and face UV");
        Check(Bridge.QueryWorkingSurfaceForFaces(front.Id,front.Revision,new[]{top.FaceId},0,0).Status==WorkingSurfaceQueryStatus.Hit,
            "selected front face is accepted as the full-model first hit");
        Check(Bridge.QueryWorkingSurfaceForFaces(front.Id,front.Revision,new[]{top.FaceId},0,0).Status==WorkingSurfaceQueryStatus.Hit &&
            Bridge.QueryWorkingSurfaceForFaces(front.Id,front.Revision,new[]{top.FaceId},double.NaN,0).Status==WorkingSurfaceQueryStatus.InvalidInput,
            "first-hit query retains typed invalid-input status");
        Check(Bridge.QueryWorkingSurfaceForFaces(front.Id,front.Revision+1,new[]{top.FaceId},0,0).Status==WorkingSurfaceQueryStatus.StaleContext,
            "selected-face query rejects stale contexts");
        Check(Query(front,new(80,0,0)).Status==WorkingSurfaceQueryStatus.Miss,"outside silhouette has no hidden/back fallback");
        Check(Bridge.QueryWorkingSurface(front.Id,front.Revision,double.NaN,0).Status==WorkingSurfaceQueryStatus.InvalidInput,"invalid UV status");
        var back=Bridge.CreateWorkingSurfaceContext(0,0,-1,1,0,0);
        Check(back.Frame[2]<0 && Math.Abs(Query(back,new(0,0,0)).Point[2])<1e-6,"flipped side sees lower first hit and opposite outside datum");
        Check(Query(front,new(0,0,0)).Point[2]==20,"new preview leaves confirmed-context geometry unchanged");
        Check(Bridge.QueryWorkingSurface(front.Id,front.Revision+1,0,0).Status==WorkingSurfaceQueryStatus.StaleContext,"native rejects wrong revision");
        Check(Bridge.QueryWorkingSurface(back.Id,front.Revision,0,0).Status==WorkingSurfaceQueryStatus.Hit,"independent same-revision contexts coexist");
        Bridge.ReleaseWorkingSurfaceContext(back.Id);
        Check(Bridge.QueryWorkingSurface(back.Id,back.Revision,0,0).Status==WorkingSurfaceQueryStatus.StaleContext,"released context rejected");
        var layeredRings=Rings(front);
        Check(layeredRings.Count==1 && !layeredRings[0].IsHole,"occluder edges and shared triangles are not outer boundary");

        using var source=new HwndSource(new HwndSourceParameters("Working-side regression") { Width=900,Height=700 });
        Check(Bridge.InitViewer(source.Handle,900,700),"offscreen native viewer initialized");
        Check(Bridge.RebuildModelPresentation() && Bridge.GetShapeInfo().Faces==loadedFaceCount,
            "model presentation can be rebuilt without replacing active geometry");
        Bridge.SetViewFrame(0,0,1,0,1,0);
        Check(!Bridge.IsWorkingPointVisible(front.Id,front.Revision,0,0,0,.001),"camera visibility rejects occluded candidate at its own screen point");
        Check(Bridge.IsWorkingPointVisible(front.Id,front.Revision,40,0,0,.001),"camera visibility allows exposed lower surface");
        int topFace=top.FaceId;
        var depthAnchor = Query(front, new Vec3(0, 0, 0));
        var depthAnchorUv = Frame(front).Project(new Vec3(
            depthAnchor.Point[0], depthAnchor.Point[1], depthAnchor.Point[2]));
        var nativeDepth = Bridge.MeasureWorkingCurveDepthEnvelope(
            front.Id,
            front.Revision,
            new[] { 0.0, 0, 19.7, 1, 0, 19.7 },
            new[] { topFace },
            topFace,
            depthAnchorUv.X,
            depthAnchorUv.Y,
            depthAnchor.Point,
            .3,
            .15,
            .1,
            .05,
            0,
            .01,
            1000);
        Check(nativeDepth.Status == WorkingCurveDepthEnvelopeStatus.Success &&
              Math.Abs(nativeDepth.MinimumInsertionDepthMm - .15) < 1e-6 &&
              Math.Abs(nativeDepth.MaximumInsertionDepthMm - .15) < 1e-6 &&
              nativeDepth.ProbeCount >= 3 && nativeDepth.MinimumSurfacePoint.Length == 3 &&
              nativeDepth.MaximumWireOutsidePoint.Length == 3,
            "native depth envelope measures a flat face and returns extrema, locations, and probe evidence");
        var depthPathStartUv = depthAnchorUv;
        var depthPathEndUv = Frame(front).Project(new Vec3(1, 0, depthAnchor.Point[2]));
        var nativeDepthAdjustment = Bridge.AdjustWorkingCurveInsertionDepth(
            front.Id,
            front.Revision,
            new[]
            {
                depthPathStartUv.X, depthPathStartUv.Y,
                depthPathEndUv.X, depthPathEndUv.Y
            },
            new[] { topFace },
            topFace,
            depthAnchorUv.X,
            depthAnchorUv.Y,
            depthAnchor.Point,
            .3,
            .15,
            .2,
            .1,
            .001,
            .01,
            1000);
        var adjustedDepthEnvelope = nativeDepthAdjustment.Status == WorkingCurveDepthEnvelopeStatus.Success
            ? Bridge.MeasureWorkingCurveDepthEnvelope(
                front.Id,
                front.Revision,
                nativeDepthAdjustment.CenterlineSamples,
                new[] { topFace },
                topFace,
                depthAnchorUv.X,
                depthAnchorUv.Y,
                depthAnchor.Point,
                .3,
                .15,
                .2,
                .1,
                nativeDepthAdjustment.ErrorBoundMm,
                .01,
                1000)
            : null;
        Check(nativeDepthAdjustment.Status == WorkingCurveDepthEnvelopeStatus.Success &&
              nativeDepthAdjustment.CenterlineSamples.Length >= 9 &&
              nativeDepthAdjustment.CenterlineSamples.Length % 3 == 0 &&
              nativeDepthAdjustment.MaximumDepthResidualMm <= .001 &&
              nativeDepthAdjustment.ProbeCount >= nativeDepthAdjustment.CenterlineSamples.Length / 3 &&
              adjustedDepthEnvelope?.Status == WorkingCurveDepthEnvelopeStatus.Success &&
              Math.Abs(adjustedDepthEnvelope.MinimumInsertionDepthMm - .15) <= adjustedDepthEnvelope.ErrorBoundMm &&
              Math.Abs(adjustedDepthEnvelope.MaximumInsertionDepthMm - .15) <= adjustedDepthEnvelope.ErrorBoundMm,
            "native bracket solver returns a flat-face centerline that passes the independent depth envelope query");
        Check(Bridge.AdjustWorkingCurveInsertionDepth(
                  front.Id,
                  front.Revision + 1,
                  new[]
                  {
                      depthPathStartUv.X, depthPathStartUv.Y,
                      depthPathEndUv.X, depthPathEndUv.Y
                  },
                  new[] { topFace },
                  topFace,
                  depthAnchorUv.X,
                  depthAnchorUv.Y,
                  depthAnchor.Point,
                  .3,
                  .15,
                  .2,
                  .1,
                  .001,
                  .01,
                  1000).Status == WorkingCurveDepthEnvelopeStatus.StaleContext,
            "native insertion-depth adjustment rejects a stale working context");
        Check(Bridge.MeasureWorkingCurveDepthEnvelope(
                  front.Id,
                  front.Revision + 1,
                  new[] { 0.0, 0, 19.7, 1, 0, 19.7 },
                  new[] { topFace },
                  topFace,
                  depthAnchorUv.X,
                  depthAnchorUv.Y,
                  depthAnchor.Point,
                  .3,
                  .15,
                  .1,
                  .05,
                  0,
                  .01,
                  1000).Status == WorkingCurveDepthEnvelopeStatus.StaleContext,
            "native depth envelope rejects a stale working context");
        var hiddenDepth = Bridge.MeasureWorkingCurveDepthEnvelope(
            front.Id,
            front.Revision,
            new[] { 0.0, 0, 19.7, 1, 0, 19.7 },
            new[] { exposed.FaceId },
            exposed.FaceId,
            depthAnchorUv.X,
            depthAnchorUv.Y,
            depthAnchor.Point,
            .3,
            .15,
            .1,
            .05,
            0,
            .01,
            1000);
        Check(hiddenDepth.Status == WorkingCurveDepthEnvelopeStatus.Unresolved &&
              hiddenDepth.Diagnostic?.Contains("앵커 first-hit Face") == true,
            "native depth envelope rejects an anchor hidden behind a nearer unselected face");
        var exposedAnchorUv = Frame(front).Project(new Vec3(
            exposed.Point[0], exposed.Point[1], exposed.Point[2]));
        var blockedStationDepth = Bridge.MeasureWorkingCurveDepthEnvelope(
            front.Id,
            front.Revision,
            new[] { 40.0, 0, -.3, 0, 0, -.3 },
            new[] { exposed.FaceId },
            exposed.FaceId,
            exposedAnchorUv.X,
            exposedAnchorUv.Y,
            exposed.Point,
            .3,
            .15,
            .1,
            .05,
            0,
            .01,
            1000);
        Check(blockedStationDepth.Status == WorkingCurveDepthEnvelopeStatus.Unresolved &&
              blockedStationDepth.Diagnostic?.Contains("선택 Face 집합 밖") == true,
            "native depth envelope rejects a curve station blocked by a nearer unselected face");
        int projectionStage=int.MinValue+1200;
        var datumProjection=Bridge.StageProjectedWorkingPolyline(front.Id,front.Revision,projectionStage,
            new[]{0.0,0,30,1,0,30},new[]{topFace},0,0,-1);
        Check(datumProjection!=null && Math.Abs(datumProjection.Start[2]-20)<1e-5 && Math.Abs(datumProjection.End[2]-20)<1e-5,
            "outside datum route projects toward and onto selected model face");
        Bridge.DiscardWorkingPathGeometry(front.Id,front.Revision,Array.Empty<int>(),new[]{projectionStage});
        var priorRoute=Bridge.DisplayWorkingPathSegment(front.Id,front.Revision,900,"Turn",new[]{0.0,0,20,1,0,20});
        var stagedRoute=Bridge.DisplayWorkingPathSegment(front.Id,front.Revision,int.MinValue,"Turn",new[]{2.0,0,20,3,0,20});
        int curveStage=int.MinValue+1201, invalidCurveStage=int.MinValue+1202;
        int splineStage=int.MinValue+1203, polylineCurveStage=int.MinValue+1204;
        var arcDefinition=new CurveDefinitionData
        {
            Kind="Arc",Direction="Forward",Frame=new[]{0.0,0,30,1,0,0,0,1,0,0,0,1},
            Center=new[]{0.0,0,30},RadiusMm=1,StartAngleRad=0,EndAngleRad=Math.PI/2
        };
        var exactArc=Bridge.StageProjectedWorkingCurveSegment(front.Id,front.Revision,curveStage,"HeatSegment",
            arcDefinition,new[]{topFace},0,0,-1);
        var invalidArcDefinition=new CurveDefinitionData
        {
            Kind="Arc",Direction="Forward",Frame=new[]{0.0,0,30,2,0,0,0,1,0,0,0,1},
            Center=new[]{0.0,0,30},RadiusMm=1,StartAngleRad=0,EndAngleRad=Math.PI/2
        };
        var invalidCurve=Bridge.StageProjectedWorkingCurveSegment(front.Id,front.Revision,invalidCurveStage,"HeatSegment",
            invalidArcDefinition,new[]{topFace},0,0,-1);
        var validLineDefinition=new CurveDefinitionData
        {
            Kind="Line",Direction="Forward",Start=new[]{0.0,0,30},End=new[]{1.0,0,30}
        };
        var reusedCurveStage=Bridge.StageProjectedWorkingCurveSegment(front.Id,front.Revision,invalidCurveStage,"HeatSegment",
            validLineDefinition,new[]{topFace},0,0,-1);
        var splineDefinition=new CurveDefinitionData
        {
            Kind="BSpline",Direction="Forward",Degree=1,Poles=new[]{0.0,0,30,1,0,30},
            Weights=new[]{1.0,2.0},Knots=new[]{0.0,1.0},Multiplicities=new[]{2,2},
            IsPeriodic=false,TrimStart=0,TrimEnd=1
        };
        var exactSpline=Bridge.StageProjectedWorkingCurveSegment(front.Id,front.Revision,splineStage,"HeatSegment",
            splineDefinition,new[]{topFace},0,0,-1);
        var polylineDefinition=new CurveDefinitionData
        {
            Kind="Polyline",Direction="Forward",Points=new[]{2.0,0,30,3,0,30}
        };
        var exactPolyline=Bridge.StageProjectedWorkingCurveSegment(front.Id,front.Revision,polylineCurveStage,"HeatSegment",
            polylineDefinition,new[]{topFace},0,0,-1);
        var parallelLineClearance=MeasureActiveClearance(
            front,
            NativeLine(new Vec3(0,0,30),new Vec3(10,0,30)),
            NativeLine(new Vec3(0,2,30),new Vec3(10,2,30)));
        var innerQuarterArc=new CurveDefinitionData
        {
            Kind="Arc",Direction="Forward",Frame=new[]{0.0,0,0,1,0,0,0,1,0,0,0,1},
            Center=new[]{0.0,0,30},RadiusMm=5,StartAngleRad=0,EndAngleRad=Math.PI/2
        };
        var outerQuarterArc=new CurveDefinitionData
        {
            Kind="Arc",Direction="Forward",Frame=new[]{0.0,0,0,1,0,0,0,1,0,0,0,1},
            Center=new[]{0.0,0,30},RadiusMm=8,StartAngleRad=0,EndAngleRad=Math.PI/2
        };
        var arcClearance=MeasureActiveClearance(front,innerQuarterArc,outerQuarterArc);
        var repeatedArcClearance=MeasureActiveClearance(front,new CurveDefinitionData
        {
            Kind="Arc",Direction="Forward",Frame=new[]{0.0,0,0,1,0,0,0,1,0,0,0,1},
            Center=new[]{0.0,0,30},RadiusMm=5,StartAngleRad=0,EndAngleRad=2*Math.PI+.1
        });
        var crossingPolylineClearance=MeasureActiveClearance(front,new CurveDefinitionData
        {
            Kind="Polyline",Direction="Forward",
            Points=new[]{0.0,0,30,10,10,30,0,10,30,10,0,30}
        });
        var localCornerClearance=MeasureActiveClearance(
            front,
            NativeLine(new Vec3(0,0,30),new Vec3(1,0,30)),
            NativeLine(new Vec3(1,0,30),new Vec3(1,1,30)));
        var unclassifiedJointWithOtherPairs=MeasureActiveClearance(
            front,
            NativeLine(new Vec3(0,0,30),new Vec3(1,0,30)),
            NativeLine(new Vec3(1,0,30),new Vec3(1,1,30)),
            NativeLine(new Vec3(0,3,30),new Vec3(1,3,30)));
        var declaredJointWithOtherPairs=MeasureActiveClearance(
            front,
            new[]{0,1},
            NativeLine(new Vec3(0,0,30),new Vec3(1,0,30)),
            NativeLine(new Vec3(1,0,30),new Vec3(1,1,30)),
            NativeLine(new Vec3(0,3,30),new Vec3(1,3,30)));
        var firstJointArc=new CurveDefinitionData
        {
            Kind="Arc",Direction="Forward",Frame=new[]{0.0,0,0,1,0,0,0,1,0,0,0,1},
            Center=new[]{0.0,0,30},RadiusMm=5,StartAngleRad=0,EndAngleRad=Math.PI/2
        };
        var secondJointArc=new CurveDefinitionData
        {
            Kind="Arc",Direction="Forward",Frame=new[]{0.0,0,0,1,0,0,0,1,0,0,0,1},
            Center=new[]{0.0,10,30},RadiusMm=5,StartAngleRad=-Math.PI/2,EndAngleRad=0
        };
        var curvedJointClearance=MeasureActiveClearance(front,firstJointArc,secondJointArc);
        var declaredCurvedJointClearance=MeasureActiveClearance(
            front,new[]{0,1},firstJointArc,secondJointArc);
        var separatedLinearSplineDefinition=new CurveDefinitionData
        {
            Kind="BSpline",Direction="Forward",Degree=1,Poles=new[]{0.0,2,30,1,2,30},
            Weights=new[]{1.0,1.0},Knots=new[]{0.0,1.0},Multiplicities=new[]{2,2},
            IsPeriodic=false,TrimStart=0,TrimEnd=1
        };
        var splineClearance=MeasureActiveClearance(front,validLineDefinition,separatedLinearSplineDefinition);
        var reversedLinearSplineDefinition=new CurveDefinitionData
        {
            Kind="BSpline",Direction="Reversed",Degree=1,Poles=new[]{0.0,2,30,1,2,30},
            Weights=new[]{1.0,1.0},Knots=new[]{0.0,1.0},Multiplicities=new[]{2,2},
            IsPeriodic=false,TrimStart=0,TrimEnd=1
        };
        var reversedSplineClearance=MeasureActiveClearance(
            front,validLineDefinition,reversedLinearSplineDefinition);
        var selfCrossingLinearSplineDefinition=new CurveDefinitionData
        {
            Kind="BSpline",Direction="Forward",Degree=1,
            Poles=new[]{-1.0,-1,30,1,1,30,-1,1,30,1,-1,30},
            Weights=new[]{1.0,2.0,0.75,1.0},
            Knots=new[]{0.0,1.0,2.0,3.0},Multiplicities=new[]{2,1,1,2},
            IsPeriodic=false,TrimStart=0,TrimEnd=3
        };
        var splineSelfIntersectionClearance=MeasureActiveClearance(
            front,
            selfCrossingLinearSplineDefinition,
            NativeLine(new Vec3(10,10,30),new Vec3(11,10,30)));
        var reversedSelfCrossingLinearSplineDefinition=new CurveDefinitionData
        {
            Kind="BSpline",Direction="Reversed",Degree=1,
            Poles=new[]{-1.0,-1,30,1,1,30,-1,1,30,1,-1,30},
            Weights=new[]{1.0,2.0,0.75,1.0},
            Knots=new[]{0.0,1.0,2.0,3.0},Multiplicities=new[]{2,1,1,2},
            IsPeriodic=false,TrimStart=0,TrimEnd=3
        };
        var reversedSplineSelfIntersectionClearance=MeasureActiveClearance(
            front,
            reversedSelfCrossingLinearSplineDefinition,
            NativeLine(new Vec3(10,10,30),new Vec3(11,10,30)));
        var singleSpanQuadraticSplineDefinition=new CurveDefinitionData
        {
            Kind="BSpline",Direction="Forward",Degree=2,
            Poles=new[]{0.0,4,30,1,5,30,2,4,30},
            Weights=new[]{1.0,0.75,1.0},
            Knots=new[]{0.0,1.0},Multiplicities=new[]{3,3},
            IsPeriodic=false,TrimStart=0,TrimEnd=1
        };
        var quadraticSplineClearance=MeasureActiveClearance(
            front,
            validLineDefinition,
            singleSpanQuadraticSplineDefinition);
        var degenerateQuadraticSplineDefinition=new CurveDefinitionData
        {
            Kind="BSpline",Direction="Forward",Degree=2,
            Poles=new[]{0.0,4,30,1,4,30,2,4,30},
            Weights=new[]{1.0,1.0,1.0},
            Knots=new[]{0.0,1.0},Multiplicities=new[]{3,3},
            IsPeriodic=false,TrimStart=0,TrimEnd=1
        };
        var degenerateQuadraticSplineClearance=MeasureActiveClearance(
            front,
            degenerateQuadraticSplineDefinition,
            NativeLine(new Vec3(10,10,30),new Vec3(11,10,30)));
        var multiSpanQuadraticSplineDefinition=new CurveDefinitionData
        {
            Kind="BSpline",Direction="Forward",Degree=2,
            Poles=new[]{0.0,4,30,1,5,30,2,5,30,3,4,30},
            Weights=new[]{1.0,1.0,1.0,1.0},
            Knots=new[]{0.0,1.0,2.0},Multiplicities=new[]{3,1,3},
            IsPeriodic=false,TrimStart=0,TrimEnd=2
        };
        var multiSpanQuadraticSplineClearance=MeasureActiveClearance(
            front,
            multiSpanQuadraticSplineDefinition,
            NativeLine(new Vec3(10,10,30),new Vec3(11,10,30)));
        var unsupportedCubicSplineDefinition=new CurveDefinitionData
        {
            Kind="BSpline",Direction="Forward",Degree=3,
            Poles=new[]{0.0,4,30,1,5,30,2,5,30,3,4,30},
            Weights=new[]{1.0,1.0,1.0,1.0},
            Knots=new[]{0.0,1.0},Multiplicities=new[]{4,4},
            IsPeriodic=false,TrimStart=0,TrimEnd=1
        };
        var unsupportedCubicSplineClearance=MeasureActiveClearance(
            front,
            unsupportedCubicSplineDefinition,
            NativeLine(new Vec3(10,10,30),new Vec3(11,10,30)));
        var pairBudgetClearance=Bridge.MeasureWorkingCurveClearance(
            front.Id,front.Revision,
            new[]
            {
                NativeLine(new Vec3(0,0,30),new Vec3(1,0,30)),
                NativeLine(new Vec3(0,2,30),new Vec3(1,2,30)),
                NativeLine(new Vec3(0,4,30),new Vec3(1,4,30))
            },
            Array.Empty<int>(),
            .4,.01,1);
        var staleClearance=Bridge.MeasureWorkingCurveClearance(
            front.Id,front.Revision+1,
            new[]
            {
                NativeLine(new Vec3(0,0,30),new Vec3(1,0,30)),
                NativeLine(new Vec3(0,2,30),new Vec3(1,2,30))
            },
            Array.Empty<int>(),
            .4,.01,100);
        var clearConnectionPath = Bridge.CheckConnectionPathObstacles(
            front.Id,
            front.Revision,
            new[]
            {
                NativeLine(new Vec3(0,0,20),new Vec3(1,0,20)),
                NativeLine(new Vec3(1,0,20),new Vec3(1,0,30)),
                NativeLine(new Vec3(1,0,30),new Vec3(2,0,30))
            },
            moldExitTransitionIndex: 1,
            connectionToleranceMm: .01);
        var blockedConnectionPath = Bridge.CheckConnectionPathObstacles(
            front.Id,
            front.Revision,
            new[]
            {
                NativeLine(new Vec3(0,0,20),new Vec3(1,0,20)),
                NativeLine(new Vec3(1,0,20),new Vec3(1,0,30)),
                NativeLine(new Vec3(1,0,30),new Vec3(1,0,10))
            },
            moldExitTransitionIndex: 1,
            connectionToleranceMm: .01);
        var staleConnectionPath = Bridge.CheckConnectionPathObstacles(
            front.Id,
            front.Revision + 1,
            new[]
            {
                NativeLine(new Vec3(0,0,20),new Vec3(1,0,20)),
                NativeLine(new Vec3(1,0,20),new Vec3(1,0,30))
            },
            moldExitTransitionIndex: 1,
            connectionToleranceMm: .01);
        var strictToleranceConnectionPath = Bridge.CheckConnectionPathObstacles(
            front.Id,
            front.Revision,
            new[]
            {
                NativeLine(new Vec3(0,0,20),new Vec3(1,0,20)),
                NativeLine(new Vec3(1.02,0,20),new Vec3(1.02,0,30)),
                NativeLine(new Vec3(1.02,0,30),new Vec3(2,0,30))
            },
            moldExitTransitionIndex: 1,
            connectionToleranceMm: .01);
        var relaxedToleranceConnectionPath = Bridge.CheckConnectionPathObstacles(
            front.Id,
            front.Revision,
            new[]
            {
                NativeLine(new Vec3(0,0,20),new Vec3(1,0,20)),
                NativeLine(new Vec3(1.02,0,20),new Vec3(1.02,0,30)),
                NativeLine(new Vec3(1.02,0,30),new Vec3(2,0,30))
            },
            moldExitTransitionIndex: 1,
            connectionToleranceMm: .03);
        Check(exactArc!=null && Math.Abs(exactArc.Length-Math.PI/2)<0.01 &&
              Math.Abs(exactArc.Start[2]-20)<1e-5 && Math.Abs(exactArc.End[2]-20)<1e-5 &&
              reusedCurveStage!=null && Math.Abs(reusedCurveStage.Length-1)<0.01 &&
              exactSpline!=null && Math.Abs(exactSpline.Length-1)<0.01 &&
              exactPolyline!=null && Math.Abs(exactPolyline.Length-1)<0.01 &&
              invalidCurve==null,
            "line, polyline, arc, and rational B-spline definitions stage with projected lengths");
        Check(parallelLineClearance.Status==WorkingCurveClearanceStatus.Success &&
              Math.Abs(parallelLineClearance.MinimumCenterlineDistanceMm-2)<1e-6 &&
              Math.Abs((parallelLineClearance.MinimumCenterlineDistanceMm-.4)-1.6)<1e-6 &&
              parallelLineClearance.FirstLocation?.Length==3 && parallelLineClearance.SecondLocation?.Length==3 &&
              parallelLineClearance.ErrorBoundMm<=.01,
            "Native 직선 최단거리와 위치·오차 정보를 반환합니다");
        Check(arcClearance.Status==WorkingCurveClearanceStatus.Success &&
              Math.Abs(arcClearance.MinimumCenterlineDistanceMm-3)<1e-5,
            "Native 원호 쌍의 중심 반지름 차이를 최단거리로 계산합니다");
        Check(repeatedArcClearance.Status==WorkingCurveClearanceStatus.Unresolved &&
              repeatedArcClearance.Diagnostic?.Contains("한 바퀴",StringComparison.Ordinal)==true,
            "한 바퀴를 넘는 반복 원호를 성공 간격으로 오판하지 않습니다");
        Check(crossingPolylineClearance.Status==WorkingCurveClearanceStatus.Success &&
              crossingPolylineClearance.MinimumCenterlineDistanceMm<=1e-8,
            "Native 다중선의 비이웃 교차를 검출합니다");
        Check(localCornerClearance.Status==WorkingCurveClearanceStatus.Unresolved &&
              localCornerClearance.Diagnostic?.Contains("연결 관계가 없어",StringComparison.Ordinal)==true &&
              unclassifiedJointWithOtherPairs.Status==WorkingCurveClearanceStatus.Unresolved &&
              unclassifiedJointWithOtherPairs.Diagnostic?.Contains("공유 끝점 연결 관계",StringComparison.Ordinal)==true,
            "공유 끝점의 연결 관계가 없으면 다른 쌍의 거리와 무관하게 미확정으로 반환합니다");
        Check(declaredJointWithOtherPairs.Status==WorkingCurveClearanceStatus.Success &&
              Math.Abs(declaredJointWithOtherPairs.MinimumCenterlineDistanceMm-2)<1e-6,
            "명시한 직선 연속 구간의 접점만 제외하고 나머지 쌍의 거리를 측정합니다");
        Check(curvedJointClearance.Status==WorkingCurveClearanceStatus.Unresolved &&
              curvedJointClearance.Diagnostic?.Contains("공유 끝점 연결 관계",StringComparison.Ordinal)==true,
            "연결 관계가 주어지지 않은 곡선 공유 끝점은 미확정으로 반환합니다");
        Check(declaredCurvedJointClearance.Status==WorkingCurveClearanceStatus.Unresolved &&
              declaredCurvedJointClearance.Diagnostic?.Contains("원호 끝점",StringComparison.Ordinal)==true,
            "명시된 연결 관계가 있어도 원호의 국소 접촉은 미확정으로 유지합니다");
        Check(splineClearance.Status==WorkingCurveClearanceStatus.Success &&
              Math.Abs(splineClearance.MinimumCenterlineDistanceMm-2)<1e-6 &&
              reversedSplineClearance.Status==WorkingCurveClearanceStatus.Success &&
              Math.Abs(reversedSplineClearance.MinimumCenterlineDistanceMm-2)<1e-6 &&
              splineSelfIntersectionClearance.Status==WorkingCurveClearanceStatus.Success &&
              splineSelfIntersectionClearance.MinimumCenterlineDistanceMm<=1e-8 &&
              reversedSplineSelfIntersectionClearance.Status==WorkingCurveClearanceStatus.Success &&
              reversedSplineSelfIntersectionClearance.MinimumCenterlineDistanceMm<=1e-8 &&
              quadraticSplineClearance.Status==WorkingCurveClearanceStatus.Success &&
              Math.Abs(quadraticSplineClearance.MinimumCenterlineDistanceMm-4)<1e-6 &&
              degenerateQuadraticSplineClearance.Status==WorkingCurveClearanceStatus.Unresolved &&
              degenerateQuadraticSplineClearance.Diagnostic?.Contains("B-spline",StringComparison.Ordinal)==true &&
              multiSpanQuadraticSplineClearance.Status==WorkingCurveClearanceStatus.Unresolved &&
              multiSpanQuadraticSplineClearance.Diagnostic?.Contains("B-spline",StringComparison.Ordinal)==true &&
              unsupportedCubicSplineClearance.Status==WorkingCurveClearanceStatus.Unresolved &&
              unsupportedCubicSplineClearance.Diagnostic?.Contains("B-spline",StringComparison.Ordinal)==true,
            "Native 유리 1차 B-spline은 방향에 관계없이 자기교차를 찾고 미인증 spline은 미확정으로 둡니다");
        Check(pairBudgetClearance.Status==WorkingCurveClearanceStatus.PairBudgetExceeded &&
              staleClearance.Status==WorkingCurveClearanceStatus.StaleContext,
            "쌍 예산 초과와 오래된 작업 context 상태를 구분합니다");
        Check(clearConnectionPath.Status==WorkingConnectionObstacleStatus.Clear &&
              blockedConnectionPath.Status==WorkingConnectionObstacleStatus.Obstructed &&
              blockedConnectionPath.FirstObstruction is { Length: 3 } obstruction &&
              Math.Abs(obstruction[0]-1)<1e-7 && Math.Abs(obstruction[2]-20)<1e-7 &&
              Math.Abs(blockedConnectionPath.DistanceToObstructionMm-21)<1e-6 &&
              strictToleranceConnectionPath.Status==WorkingConnectionObstacleStatus.Unresolved &&
              relaxedToleranceConnectionPath.Status==WorkingConnectionObstacleStatus.Clear &&
              staleConnectionPath.Status==WorkingConnectionObstacleStatus.StaleContext,
            "Native 연결 경로 질의는 요청 공차, 이탈 접점과 전체 금형 재관통 위치·누적 거리를 판정합니다");
        Bridge.DiscardWorkingPathGeometry(front.Id,front.Revision,
            new[]{curveStage,invalidCurveStage,splineStage,polylineCurveStage},Array.Empty<int>());
        Check(Bridge.SetTurnSegmentEnabled(900,true,.2,.8,.3),
            "discarding exact curve candidates preserves previously committed route geometry");
        var failedStage=Bridge.DisplayWorkingPathSegment(front.Id,front.Revision,int.MinValue+1,"Turn",new[]{double.NaN,0,20});
        int lineStage=int.MinValue/2;
        bool stagedLine=Bridge.StageWorkingPolyline(front.Id,front.Revision,lineStage,new[]{4.0,0,20,5,0,20});
        bool failedLine=Bridge.StageWorkingPolyline(front.Id,front.Revision,lineStage-1,new[]{double.NaN,0,20});
        Check(priorRoute!=null && stagedRoute!=null && failedStage==null && stagedLine && !failedLine,
            "route and exclusion candidates can fail after earlier route/polyline staging");
        Check(!Bridge.CommitWorkingPathGeometry(front.Id,front.Revision,new[]{int.MinValue},Array.Empty<int>(),Array.Empty<int>()),
            "invalid candidate commit is rejected before replacing existing routes");
        Bridge.DiscardWorkingPathGeometry(front.Id,front.Revision,new[]{int.MinValue},new[]{lineStage});
        Check(Bridge.SetTurnSegmentEnabled(900,true,.2,.8,.3),"discarding failed candidate stage preserves prior selectable turnSegment");
        Bridge.ClearPathSegments();
        var releaseA=Bridge.CreateWorkingSurfaceContext(0,0,1,1,0,0);
        var releaseB=Bridge.CreateWorkingSurfaceContext(0,0,1,1,0,0);
        int releaseRouteId=int.MinValue+700, releaseLineId=int.MinValue/2+700;
        Check(Bridge.DisplayWorkingPathSegment(releaseA.Id,releaseA.Revision,releaseRouteId,"Turn",new[]{6.0,0,20,7,0,20})!=null &&
              Bridge.StageWorkingPolyline(releaseA.Id,releaseA.Revision,releaseLineId,new[]{6.0,1,20,7,1,20}),
            "stage route and polyline owned by context A");
        Check(Bridge.DisplayWorkingPathSegment(releaseB.Id,releaseB.Revision,releaseRouteId,"Turn",new[]{8.0,0,20,9,0,20})==null &&
              !Bridge.StageWorkingPolyline(releaseB.Id,releaseB.Revision,releaseLineId,new[]{8.0,1,20,9,1,20}),
            "same-revision context B cannot claim context A's occupied staging IDs");
        Bridge.DiscardWorkingPathGeometry(releaseB.Id,releaseB.Revision,new[]{releaseRouteId},new[]{releaseLineId});
        Check(Bridge.SetTurnSegmentEnabled(releaseRouteId,true,.2,.8,.3),"wrong-owner discard leaves context A staged route intact");
        Bridge.ReleaseWorkingSurfaceContext(releaseA.Id);
        Check(!Bridge.SetTurnSegmentEnabled(releaseRouteId,true,.2,.8,.3),"releasing context A removes its staged route AIS/ID");
        var reusedRoute=Bridge.DisplayWorkingPathSegment(releaseB.Id,releaseB.Revision,releaseRouteId,"Turn",new[]{6.0,0,20,7,0,20});
        bool reusedLine=Bridge.StageWorkingPolyline(releaseB.Id,releaseB.Revision,releaseLineId,new[]{6.0,1,20,7,1,20});
        Check(reusedRoute!=null && reusedLine &&
              Bridge.CommitWorkingPathGeometry(releaseB.Id,releaseB.Revision,new[]{releaseRouteId},new[]{901},new[]{releaseLineId}) &&
              Bridge.SetTurnSegmentEnabled(901,true,.2,.8,.3),
            "context B reuses released IDs and commits only its own staged handles at the same model revision");
        int ownedDiscardId=int.MinValue/2+701;
        Check(Bridge.StageWorkingPolyline(releaseB.Id,releaseB.Revision,ownedDiscardId,new[]{6.0,2,20,7,2,20}),"context B stages independently discardable line");
        Bridge.DiscardWorkingPathGeometry(releaseB.Id,releaseB.Revision,Array.Empty<int>(),new[]{ownedDiscardId});
        Check(Bridge.StageWorkingPolyline(releaseB.Id,releaseB.Revision,ownedDiscardId,new[]{6.0,2,20,7,2,20}),"owning context discard frees its temporary polyline ID");
        Bridge.DiscardWorkingPathGeometry(releaseB.Id,releaseB.Revision,Array.Empty<int>(),new[]{ownedDiscardId});
        var commitA=Bridge.CreateWorkingSurfaceContext(0,0,1,1,0,0);
        var commitB=Bridge.CreateWorkingSurfaceContext(0,0,1,1,0,0);
        int candidateA=int.MinValue+1500,candidateB=int.MinValue+1501;
        Check(Bridge.DisplayWorkingPathSegment(commitA.Id,commitA.Revision,candidateA,"Turn",new[]{10.0,0,20,11,0,20})!=null &&
              Bridge.DisplayWorkingPathSegment(commitB.Id,commitB.Revision,candidateB,"Turn",new[]{12.0,0,20,13,0,20})!=null,
              "two contexts own independent staged routes");
        Check(Bridge.CommitWorkingPathGeometry(commitA.Id,commitA.Revision,new[]{candidateA},new[]{902},Array.Empty<int>()) &&
              Bridge.SetTurnSegmentEnabled(candidateB,true,.2,.8,.3),
              "committing one context preserves another context staged route and ownership");
        Bridge.DiscardWorkingPathGeometry(commitB.Id,commitB.Revision,new[]{candidateB},Array.Empty<int>());
        Check(Bridge.DisplayWorkingPathSegment(commitB.Id,commitB.Revision,candidateB,"Turn",new[]{12.0,0,20,13,0,20})!=null,
              "preserved other-context route remains discardable and releases its negative ID");
        Bridge.ClearOverlays();
        Bridge.SetViewFrame(1,0,1,0,1,0);
        Bridge.ZoomView(1.3);
        Check(Query(front,new(0,0,0)).Point[2]==20,"camera orbit/zoom do not alter fixed working side");

        var window=new MainWindow();
        var viewport=(OccViewport)window.FindName("Viewport");
        var active=Field<WorkingCoordinateSystemSession>(window,"_workingCoordinateSystem");
        Check((bool)Invoke(window,"ApplyWorkingCoordinateSystem",new Vec3(0,0,1)) && active.Confirmed?.Projection.Normal.Z==1,
            "direct axis application creates an active +Z context without a confirmation step");
        var sameSide=active.Confirmed;
        var regionSelection=RegionSelection(window);
        FaceRefId selectedFaceReferenceId=FaceRefId.New();
        BoundaryLoop retainedBoundary=CreateSelectionBoundary(selectedFaceReferenceId);
        var topology=new SelectedTopology(
            [new TopologyComponent([selectedFaceReferenceId],[retainedBoundary])],
            Array.Empty<FaceAdjacency>());
        var topologyResult=GeometryQueryResult<SelectedTopology>.Succeeded(topology);
        regionSelection.GetType().GetMethod("SetTopology")!.Invoke(
            regionSelection,
            new object[]{topologyResult,new ProjectionFrame(default,new Vec3(0,0,1),new Vec3(1,0,0))});
        regionSelection.GetType().GetMethod("Add")!.Invoke(regionSelection,new object[]{selectedFaceReferenceId});
        var retainedRegion=SelectedBoundaryLoops(window);
        ((ListBox)window.FindName("SelectedFacesList")).Items.Add("seed");
        Check((bool)Invoke(window,"ApplyWorkingCoordinateSystem",new Vec3(0,0,1)) && ReferenceEquals(sameSide,active.Confirmed) && retainedRegion.Count==1,
            "same-axis selection is a no-op and retains dependent state");
        int activeFaceCount=Bridge.GetShapeInfo().Faces;
        Invoke(window,"LoadFile",Path.Combine(args[0],"missing-model.step"));
        Check(ReferenceEquals(sameSide,active.Confirmed) && retainedRegion.Count==1 && SelectedFaceReferences(window).Contains(selectedFaceReferenceId) &&
            ((ListBox)window.FindName("SelectedFacesList")).Items.Count==1 &&
            Bridge.GetShapeInfo().Faces==activeFaceCount &&
            Bridge.QueryWorkingSurface(sameSide.Id.Value,sameSide.Revision.Value,0,0).Status==WorkingSurfaceQueryStatus.Hit,
            "failed model open preserves the active WCS, selection, region, and native model");
        var prepare=Field<Func<long,long,bool>>(window,"_prepareWorkingFront");
        SetField(window,"_prepareWorkingFront",new Func<long,long,bool>((_,_)=>false));
        Check(!(bool)Invoke(window,"ApplyWorkingCoordinateSystem",new Vec3(0,0,-1)) && ReferenceEquals(sameSide,active.Confirmed) && retainedRegion.Count==1,
            "failed direction application rolls back active side and dependent state");
        SetField(window,"_prepareWorkingFront",prepare);
        SetField(window,"_beforeWorkingCoordinateSystemCommit",new Action(()=>throw new InvalidOperationException("injected precommit failure")));
        Check(!(bool)Invoke(window,"ApplyWorkingCoordinateSystem",new Vec3(0,0,-1)) && ReferenceEquals(sameSide,active.Confirmed) &&
            retainedRegion.Count==1 && SelectedFaceReferences(window).Contains(selectedFaceReferenceId) && ((ListBox)window.FindName("SelectedFacesList")).Items.Count==1 &&
            Bridge.QueryWorkingSurface(sameSide.Id.Value,sameSide.Revision.Value,0,0).Status==WorkingSurfaceQueryStatus.Hit,
            "failure after camera preparation preserves old context and dependent presentation state");
        SetField(window,"_beforeWorkingCoordinateSystemCommit",new Action(()=>{}));
        Check((bool)Invoke(window,"ApplyWorkingCoordinateSystem",new Vec3(0,0,-1)) && active.Confirmed?.Projection.Normal.Z==-1 &&
            retainedRegion.Count==0 && SelectedFaceReferences(window).Count==0 && ((ListBox)window.FindName("SelectedFacesList")).Items.Count==0,
            "successful side change invalidates dependent selections");
        SetField(window,"_afterWorkingCoordinateSystemCommit",new Action(()=>throw new InvalidOperationException("injected postcommit failure")));
        bool postCommitApplied=(bool)Invoke(window,"ApplyWorkingCoordinateSystem",new Vec3(0,0,1));
        Check(postCommitApplied && active.Confirmed?.Projection.Normal.Z==1 &&
            Bridge.QueryWorkingSurface(active.Confirmed.Id.Value,active.Confirmed.Revision.Value,0,0).Status!=WorkingSurfaceQueryStatus.StaleContext,
            "postcommit cleanup failure keeps the newly confirmed working side active");
        SetField(window,"_afterWorkingCoordinateSystemCommit",new Action(()=>{}));
        Check(window.FindName("WorkingPickBox")==null && typeof(OccViewport).GetProperty("ProjectedModelBoundaryPickMode")==null &&
            typeof(OccViewport).GetEvent("ProjectedModelBoundaryPicked")==null,"diagnostic UI and click interception are removed");
        Check(window.FindName("ProjectedModelBoundaryCanvas")==null && window.FindName("WorkingAxisBox")!=null,
            "boundary diagnostic canvas removed; direct axis selector remains");
        Check(typeof(Bridge).GetMethod("HlrProject")==null && typeof(Bridge).GetMethod("HlrEdges")!=null,
            "HLR smoke API removed while exporter HlrEdges remains");
        viewport.RectangleSelectionMode=true;
        Check(viewport.RectangleSelectionMode,"rectangle interaction remains independent of removed diagnostic mode");
        Check(Bridge.ReturnToWorkingFront(front.Id,front.Revision),"active working front is independent of camera state");
        Check(Bridge.LoadStep(Path.Combine(args[0],"ring.stp")),"load ring fixture");
        Check(Bridge.QueryWorkingSurface(front.Id,front.Revision,0,0).Status==WorkingSurfaceQueryStatus.StaleContext,"model reload invalidates native contexts");
        var ringContext=Bridge.CreateWorkingSurfaceContext(0,0,1,1,0,0); var rings=Rings(ringContext);
        Check(rings.Count==2 && rings.Count(r=>r.IsHole)==1,"whole-model union retains hole across disconnected adjacent sheet faces");
        Check(Query(ringContext,new(50,50,0)).Status==WorkingSurfaceQueryStatus.Miss,"through-hole is not working surface");
        Check(rings.Count(r=>r.NestingDepth==0)==1,"outer contour computation remains available without UI presentation");
Check(Bridge.LoadStep(Path.Combine(args[0],"sphere.stp")),"load sphere with curved silhouette, not trim-edge outline");
        var sphere=Bridge.CreateWorkingSurfaceContext(0,0,1,1,0,0); var sphereRings=Rings(sphere);
        Check(sphereRings.Count==1 && sphereRings[0].Points.Count>20,"curved silhouette exists independently of face wire/seam");
        double area=Math.Abs(RegionOps.SignedArea(sphereRings[0].Points));
        Check(Math.Abs(area-Math.PI*400)<10,"sphere mesh silhouette approximates projected disk, explicit tolerance");
        var tangent=Query(sphere,new(20,0,0));
        Check(tangent.Status==WorkingSurfaceQueryStatus.Tangent || tangent.Status==WorkingSurfaceQueryStatus.Miss,"tangent is explicit or no hit, never back surface fallback");
        var sphereTop=Query(sphere,new(0,0,0));
        Check(sphereTop.Status==WorkingSurfaceQueryStatus.Hit && Math.Abs(sphereTop.Point[2]-20)<1e-5,"curved surface first hit");
        foreach (var n in new[]{new Vec3(1,0,0),new Vec3(-1,0,0),new Vec3(0,1,0),new Vec3(0,-1,0),new Vec3(0,0,1),new Vec3(0,0,-1)})
        {
            var axisX=Math.Abs(n.X)>.9 ? new Vec3(0,1,0) : new Vec3(1,0,0);
            var c=Bridge.CreateWorkingSurfaceContext(n.X,n.Y,n.Z,axisX.X,axisX.Y,axisX.Z);
            var hit=Query(c,new(0,0,0));
            var p=new Vec3(hit.Point[0],hit.Point[1],hit.Point[2]);
            Check(hit.Status==WorkingSurfaceQueryStatus.Hit && (p-n*20).Length<1e-5 && Vec3.Dot(Frame(c).Origin,n)>20,
                "all-axis outside datum and nearest surface: "+n);
            Bridge.ReleaseWorkingSurfaceContext(c.Id);
        }
        Check(Bridge.LoadStep(Path.Combine(args[0],"tangentBack.stp")),"load tangent contact with rear surface behind it");
        var tangentContext=Bridge.CreateWorkingSurfaceContext(0,0,1,1,0,0);
        var grazing=Query(tangentContext,new(20,0,0));
        Check(grazing.Status==WorkingSurfaceQueryStatus.Tangent || grazing.Status==WorkingSurfaceQueryStatus.GeometryFailure,
            "nearest grazing contact is not discarded in favor of a rear surface hit");
        var stalePresentation=Bridge.CaptureWorkingCoordinateSystemPresentation();
        Check(Bridge.LoadStep(Path.Combine(args[0],"trimmedVisibility.stp")),"load holed upper trimmed face with full lower sheet");
        Check(!Bridge.RestoreWorkingCoordinateSystemPresentation(stalePresentation.Id),"presentation savepoints cannot restore across model revision");
        Bridge.ReleaseWorkingCoordinateSystemPresentation(stalePresentation.Id);
        var trimmed=Bridge.CreateWorkingSurfaceContext(0,0,1,1,0,0);
        Bridge.ReturnToWorkingFront(trimmed.Id,trimmed.Revision);
        Check(Query(trimmed,new(0,0,0)).Point[2]==0 && Bridge.IsWorkingPointVisible(trimmed.Id,trimmed.Revision,0,0,0,.001),
            "upper surface extension through trimmed hole does not occlude lower BRep hit");
        Check(Bridge.IsWorkingPointVisible(trimmed.Id,trimmed.Revision,60,0,0,.001),
            "outside upper face trim is not a visibility blocker");
        Check(!Bridge.IsWorkingPointVisible(trimmed.Id,trimmed.Revision,30,0,0,.001),
            "upper face IN hit still blocks the lower surface");
        Check(Bridge.IsWorkingPointVisible(trimmed.Id,trimmed.Revision,10,0,20,.001),
            "ON trim-boundary hit remains eligible for visibility");
        var upperFaceHit=Query(trimmed,new(-20,0,0));
        var upperFaceTopology=Bridge.QueryWorkingSelectedTopology(
            trimmed.Id,trimmed.Revision,new[]{upperFaceHit.FaceId},trimmed.MeshDeflection);
        Check(upperFaceTopology.Status==WorkingTopologyQueryStatus.Success &&
              upperFaceTopology.Components.Length==1 &&
              upperFaceTopology.Components[0].FaceIndices.SequenceEqual(new[]{upperFaceHit.FaceId}) &&
              upperFaceTopology.Components[0].BoundaryLoops.Length==2 &&
              upperFaceTopology.Components[0].BoundaryLoops.All(loop=>loop.IsClosed &&
                  loop.SegmentFaceIndices.Length==loop.Points.Length/3 &&
                  loop.SegmentFaceIndices.All(faceIndex=>faceIndex==upperFaceHit.FaceId)),
            "selected trimmed face returns closed outer and hole loops with source-face ownership");
        var regionBoundary = new[]
        {
            new WorkingRegionLoopData
            {
                IsHole = false,
                Points = new[] { -40.0, -20.0, 40.0, -20.0, 40.0, 20.0, -40.0, 20.0 }
            },
            new WorkingRegionLoopData
            {
                IsHole = true,
                Points = new[] { 15.0, -5.0, 25.0, -5.0, 25.0, 5.0, 15.0, 5.0 }
            }
        };
        var surfaceElements = Bridge.QueryWorkingSurfaceElements(
            trimmed.Id,
            trimmed.Revision,
            new[] { upperFaceHit.FaceId },
            regionBoundary,
            trimmed.MeshDeflection,
            .1);
        double measuredSurfaceArea = surfaceElements.Elements.Sum(element => element.SurfaceAreaMm2);
        Check(surfaceElements.Status == WorkingSurfaceElementQueryStatus.Success &&
              surfaceElements.Elements.Length > 0 &&
              surfaceElements.Elements.All(element => element.FaceIndex == upperFaceHit.FaceId &&
                  element.ModelVertices.Length == 9 && element.ModelCentroid.Length == 3 &&
                  element.FaceUvCentroid.Length == 2 && element.WorkingUvCentroid.Length == 2) &&
              Math.Abs(measuredSurfaceArea - 2700.0) <= surfaceElements.AreaErrorBoundMm2 + .01 &&
              surfaceElements.AreaErrorBoundMm2 < .1,
            "surface-element query clips the selected trimmed face to the outer boundary and hole");
        bool hasTriangleInsideExcludedHole = surfaceElements.Elements.Any(element =>
            element.ModelVertices.Chunk(3).Any(point =>
                (point[0] > -10.0 + .001 && point[0] < 10.0 - .001 &&
                 point[1] > -10.0 + .001 && point[1] < 10.0 - .001) ||
                (point[0] > 15.0 + .001 && point[0] < 25.0 - .001 &&
                 point[1] > -5.0 + .001 && point[1] < 5.0 - .001)));
        Check(!hasTriangleInsideExcludedHole,
            "surface triangles do not fill either the source-face trim or effective-region hole");
        var lowerFaceHit = Query(trimmed, new(0, 0, 0));
        var visibleSurfaceElements = Bridge.QueryWorkingSurfaceElements(
            trimmed.Id,
            trimmed.Revision,
            new[] { upperFaceHit.FaceId, lowerFaceHit.FaceId },
            regionBoundary,
            trimmed.MeshDeflection,
            .1);
        double visibleArea = visibleSurfaceElements.Elements.Sum(element => element.SurfaceAreaMm2);
        bool lowerTrianglesStayInsideUpperTrimHole = visibleSurfaceElements.Elements
            .Where(element => element.FaceIndex == lowerFaceHit.FaceId)
            .All(element => element.ModelVertices.Chunk(3).All(point =>
                point[0] >= -10.001 && point[0] <= 10.001 &&
                point[1] >= -10.001 && point[1] <= 10.001));
        Check(lowerFaceHit.FaceId != upperFaceHit.FaceId &&
              visibleSurfaceElements.Status == WorkingSurfaceElementQueryStatus.Success &&
              Math.Abs(visibleArea - 3100.0) <= visibleSurfaceElements.AreaErrorBoundMm2 + .01 &&
              visibleSurfaceElements.AreaErrorBoundMm2 < 50 && lowerTrianglesStayInsideUpperTrimHole,
            "overlapping selected faces count only first-hit area and retain the lower face inside the upper trim hole");
        var lowerOnlyElements = Bridge.QueryWorkingSurfaceElements(
            trimmed.Id,
            trimmed.Revision,
            new[] { lowerFaceHit.FaceId },
            regionBoundary,
            trimmed.MeshDeflection,
            .1);
        double lowerVisibleArea = lowerOnlyElements.Elements.Sum(element => element.SurfaceAreaMm2);
        Check(lowerOnlyElements.Status == WorkingSurfaceElementQueryStatus.Success &&
              Math.Abs(lowerVisibleArea - 400.0) <= lowerOnlyElements.AreaErrorBoundMm2 + .01 &&
              lowerOnlyElements.AreaErrorBoundMm2 < 50,
            "an unselected upper face still occludes the lower face outside its trimmed hole");
        var holeOnlyBoundary = new[]
        {
            new WorkingRegionLoopData
            {
                IsHole = true,
                Points = new[] { -2.0, -2.0, 2.0, -2.0, 2.0, 2.0, -2.0, 2.0 }
            }
        };
        Check(Bridge.QueryWorkingSurfaceElements(
                  trimmed.Id,
                  trimmed.Revision,
                  Array.Empty<int>(),
                  regionBoundary,
                  trimmed.MeshDeflection,
                  .1).Status ==
                  WorkingSurfaceElementQueryStatus.InvalidInput &&
              Bridge.QueryWorkingSurfaceElements(
                  trimmed.Id,
                  trimmed.Revision,
                  new[] { -1 },
                  regionBoundary,
                  trimmed.MeshDeflection,
                  .1).Status ==
                  WorkingSurfaceElementQueryStatus.InvalidFace &&
              Bridge.QueryWorkingSurfaceElements(
                  trimmed.Id,
                  trimmed.Revision + 1,
                  new[] { upperFaceHit.FaceId },
                  regionBoundary,
                  trimmed.MeshDeflection,
                  .1).Status ==
                  WorkingSurfaceElementQueryStatus.StaleContext &&
              Bridge.QueryWorkingSurfaceElements(
                  trimmed.Id,
                  trimmed.Revision,
                  new[] { upperFaceHit.FaceId },
                  holeOnlyBoundary,
                  trimmed.MeshDeflection,
                  .1).Status ==
                  WorkingSurfaceElementQueryStatus.InvalidRegion,
            "surface-element query distinguishes invalid input, face, region, and stale context");
        Check(Bridge.LoadStep(Path.Combine(args[0],"threePanels.stp")),"load three topologically adjacent working faces");
        var panels=Bridge.CreateWorkingSurfaceContext(0,0,1,1,0,0);
        int faceA=Bridge.QueryWorkingSurface(panels.Id,panels.Revision,-1,0).FaceId;
        int faceB=Bridge.QueryWorkingSurface(panels.Id,panels.Revision,0,0).FaceId;
        int faceC=Bridge.QueryWorkingSurface(panels.Id,panels.Revision,1,0).FaceId;
        bool ab=Bridge.AreWorkingFacesAdjacent(panels.Id,panels.Revision,faceA,faceB);
        bool bc=Bridge.AreWorkingFacesAdjacent(panels.Id,panels.Revision,faceB,faceC);
        bool ac=Bridge.AreWorkingFacesAdjacent(panels.Id,panels.Revision,faceA,faceC);
        Check(faceA!=faceB && faceB!=faceC && faceA!=faceC && ab && bc && !ac,
            "ordered three-face path accepts A-B-C shared-edge transitions while endpoints A/C are not adjacent");
        Check(Bridge.QueryWorkingFaceAdjacency(panels.Id,panels.Revision,faceA,faceB)==WorkingFaceAdjacencyStatus.Adjacent &&
              Bridge.QueryWorkingFaceAdjacency(panels.Id,panels.Revision,faceA,faceC)==WorkingFaceAdjacencyStatus.NotAdjacent &&
              Bridge.QueryWorkingFaceAdjacency(panels.Id,panels.Revision+1,faceA,faceB)==WorkingFaceAdjacencyStatus.StaleContext &&
              Bridge.QueryWorkingFaceAdjacency(panels.Id,panels.Revision,-1,faceB)==WorkingFaceAdjacencyStatus.InvalidFace,
            "typed adjacency query separates shared edge, non-adjacency, stale context, and invalid face");
        var panelFeatures=Bridge.GetFaceGeometryFeatures().Features.ToDictionary(feature=>feature.FaceIndex);
        Check(panelFeatures[faceA].AdjacentFaceIndices.Contains(faceB) &&
              panelFeatures[faceB].AdjacentFaceIndices.Contains(faceA) &&
              panelFeatures[faceB].AdjacentFaceIndices.Contains(faceC) &&
              panelFeatures[faceC].AdjacentFaceIndices.Contains(faceB) &&
              !panelFeatures[faceA].AdjacentFaceIndices.Contains(faceC),
            "face feature adjacency lists match the shared-edge topology in both directions");
        var joinedTopology=Bridge.QueryWorkingSelectedTopology(
            panels.Id,panels.Revision,new[]{faceA,faceB},panels.MeshDeflection);
        Check(joinedTopology.Status==WorkingTopologyQueryStatus.Success &&
              joinedTopology.Components.Length==1 &&
              joinedTopology.Components[0].FaceIndices.OrderBy(face=>face).SequenceEqual(new[]{faceA,faceB}.OrderBy(face=>face)) &&
              joinedTopology.Components[0].BoundaryLoops.Length==1 &&
              joinedTopology.Components[0].BoundaryLoops[0].SegmentFaceIndices.Distinct().Count()==2 &&
              joinedTopology.Adjacencies.Length==1 &&
              joinedTopology.Adjacencies[0].FirstFaceIndex==Math.Min(faceA,faceB) &&
              joinedTopology.Adjacencies[0].SecondFaceIndex==Math.Max(faceA,faceB),
            "shared-edge selection returns one component, one outer loop, and both source faces");
        var separateTopology=Bridge.QueryWorkingSelectedTopology(
            panels.Id,panels.Revision,new[]{faceA,faceC},panels.MeshDeflection);
        Check(separateTopology.Status==WorkingTopologyQueryStatus.Success &&
              separateTopology.Components.Length==2 && separateTopology.Adjacencies.Length==0 &&
              separateTopology.Components.All(component=>component.BoundaryLoops.Length==1),
            "disconnected selected faces remain separate topology components");
        Check(Bridge.QueryWorkingSelectedTopology(panels.Id,panels.Revision,Array.Empty<int>(),panels.MeshDeflection).Status==WorkingTopologyQueryStatus.InvalidInput &&
              Bridge.QueryWorkingSelectedTopology(panels.Id,panels.Revision,new[]{faceA,faceA},panels.MeshDeflection).Status==WorkingTopologyQueryStatus.InvalidInput &&
              Bridge.QueryWorkingSelectedTopology(panels.Id,panels.Revision,new[]{-1},panels.MeshDeflection).Status==WorkingTopologyQueryStatus.InvalidFace &&
              Bridge.QueryWorkingSelectedTopology(panels.Id,panels.Revision+1,new[]{faceA},panels.MeshDeflection).Status==WorkingTopologyQueryStatus.StaleContext,
            "selected-topology query preserves invalid-input, invalid-face, and stale-context states");
        int seamStage=int.MinValue+1300;
        var acrossSeams=Bridge.StageProjectedWorkingPolyline(panels.Id,panels.Revision,seamStage,
            new[]{.25, .5, 5.0, 2.75, .5, 5.0},new[]{faceA,faceB,faceC},0,0,-1);
        Check(acrossSeams!=null,"projected route reconnects across selected three-panel seams");
        Bridge.DiscardWorkingPathGeometry(panels.Id,panels.Revision,Array.Empty<int>(),new[]{seamStage});
        int partialStage=int.MinValue+1301;
        Check(Bridge.StageProjectedWorkingPolyline(panels.Id,panels.Revision,partialStage,
            new[]{.25, .5, 5.0, 1.5, .5, 5.0, 3.5, .5, 5.0, 2.75, .5, 5.0},
            new[]{faceA,faceB,faceC},0,0,-1)==null,"partially out-of-domain projected path is rejected");
        int reloadStage=int.MinValue/2+91;
        Check(Bridge.StageWorkingPolyline(panels.Id,panels.Revision,reloadStage,new[]{0.0,0,0,1,0,0}),
            "working polyline stage created for cleanup/revision test");
        Bridge.ClearOverlays();
        Check(Bridge.StageWorkingPolyline(panels.Id,panels.Revision,reloadStage,new[]{0.0,0,0,1,0,0}),
            "ClearOverlays removes staged AIS and releases its temporary ID");
        int oldDocumentPathId=int.MaxValue-91;
        Check(Bridge.DisplayWorkingPathSegment(panels.Id,panels.Revision,oldDocumentPathId,"Turn",new[]{4.0,0,5,5,0,5})!=null,
            "active document owns its displayed path segment");
        long revisionBeforeCandidatePreparation = Bridge.GetModelRevision();
        int faceCountBeforeCandidatePreparation = Bridge.GetShapeInfo().Faces;
        var discardedCandidate = Bridge.PrepareStepCandidate(Path.Combine(args[0], "threePanels.stp"));
        var candidateFaceFeatures = discardedCandidate.Status == ModelLoadCandidateStatus.Prepared
            ? Bridge.GetPreparedCandidateFaceGeometryFeatures(discardedCandidate.CandidateId)
            : null;
        var candidateBoundary = candidateFaceFeatures?.Status == FaceFeatureQueryStatus.Success &&
            candidateFaceFeatures.Features.Length > 0
            ? Bridge.GetPreparedCandidateFaceUvBoundary(discardedCandidate.CandidateId, candidateFaceFeatures.Features[0].FaceIndex)
            : null;
        double[] candidateBoundaryPoint = candidateBoundary != null && candidateBoundary.Length > 0 &&
            candidateBoundary[0].Points != null && candidateBoundary[0].Points.Length >= 2
            ? Bridge.SamplePreparedCandidateUvTo3D(
                discardedCandidate.CandidateId,
                candidateFaceFeatures.Features[0].FaceIndex,
                candidateBoundary[0].Points[0],
                candidateBoundary[0].Points[1])
            : null;
        WorkingSurfaceContext candidateWorkingContext = discardedCandidate.Status == ModelLoadCandidateStatus.Prepared
            ? Bridge.CreatePreparedCandidateWorkingSurfaceContext(
                discardedCandidate.CandidateId, 0, 0, 1, 1, 0, 0)
            : null;
        WorkingSurfaceHit candidateWorkingHit = candidateWorkingContext == null
            ? null
            : QueryCandidate(discardedCandidate.CandidateId, candidateWorkingContext, new Vec3(0, 0, 5));
        WorkingSurfaceHit candidateHitA = candidateWorkingContext == null
            ? null
            : QueryCandidate(discardedCandidate.CandidateId, candidateWorkingContext, new Vec3(-1, 0, 5));
        WorkingSurfaceHit candidateHitB = candidateWorkingContext == null
            ? null
            : QueryCandidate(discardedCandidate.CandidateId, candidateWorkingContext, new Vec3(0, 0, 5));
        WorkingSurfaceHit candidateHitC = candidateWorkingContext == null
            ? null
            : QueryCandidate(discardedCandidate.CandidateId, candidateWorkingContext, new Vec3(1, 0, 5));
        WorkingSurfaceHit candidateDepthStart = candidateWorkingContext == null
            ? null
            : QueryCandidate(discardedCandidate.CandidateId, candidateWorkingContext, new Vec3(.25, .5, 5));
        WorkingSurfaceHit candidateDepthEnd = candidateWorkingContext == null
            ? null
            : QueryCandidate(discardedCandidate.CandidateId, candidateWorkingContext, new Vec3(.75, .5, 5));
        Vec2 candidateDepthStartUv = candidateWorkingContext != null && candidateDepthStart?.Status == WorkingSurfaceQueryStatus.Hit
            ? Frame(candidateWorkingContext).Project(new Vec3(
                candidateDepthStart.Point[0], candidateDepthStart.Point[1], candidateDepthStart.Point[2]))
            : default;
        Vec2 candidateDepthEndUv = candidateWorkingContext != null && candidateDepthEnd?.Status == WorkingSurfaceQueryStatus.Hit
            ? Frame(candidateWorkingContext).Project(new Vec3(
                candidateDepthEnd.Point[0], candidateDepthEnd.Point[1], candidateDepthEnd.Point[2]))
            : default;
        var candidateDepthAdjustment = candidateWorkingContext != null &&
            candidateDepthStart?.Status == WorkingSurfaceQueryStatus.Hit &&
            candidateDepthEnd?.Status == WorkingSurfaceQueryStatus.Hit
            ? Bridge.AdjustPreparedCandidateWorkingCurveInsertionDepth(
                discardedCandidate.CandidateId,
                candidateWorkingContext.Id,
                candidateWorkingContext.Revision,
                new[]
                {
                    candidateDepthStartUv.X, candidateDepthStartUv.Y,
                    candidateDepthEndUv.X, candidateDepthEndUv.Y
                },
                new[] { candidateDepthStart.FaceId },
                candidateDepthStart.FaceId,
                candidateDepthStartUv.X,
                candidateDepthStartUv.Y,
                candidateDepthStart.Point,
                .3,
                .15,
                .2,
                .1,
                .001,
                .01,
                1000)
            : null;
        var candidateAdjustedDepthEnvelope = candidateDepthAdjustment?.Status == WorkingCurveDepthEnvelopeStatus.Success
            ? Bridge.MeasurePreparedCandidateWorkingCurveDepthEnvelope(
                discardedCandidate.CandidateId,
                candidateWorkingContext.Id,
                candidateWorkingContext.Revision,
                candidateDepthAdjustment.CenterlineSamples,
                new[] { candidateDepthStart.FaceId },
                candidateDepthStart.FaceId,
                candidateDepthStartUv.X,
                candidateDepthStartUv.Y,
                candidateDepthStart.Point,
                .3,
                .15,
                .2,
                .1,
                candidateDepthAdjustment.ErrorBoundMm,
                .01,
                1000)
            : null;
        Check(candidateDepthAdjustment?.Status == WorkingCurveDepthEnvelopeStatus.Success &&
              candidateDepthAdjustment.ProbeCount >= candidateDepthAdjustment.CenterlineSamples.Length / 3 &&
              candidateAdjustedDepthEnvelope?.Status == WorkingCurveDepthEnvelopeStatus.Success &&
              Math.Abs(candidateAdjustedDepthEnvelope.MinimumInsertionDepthMm - .15) <= candidateAdjustedDepthEnvelope.ErrorBoundMm &&
              Math.Abs(candidateAdjustedDepthEnvelope.MaximumInsertionDepthMm - .15) <= candidateAdjustedDepthEnvelope.ErrorBoundMm,
            "prepared candidate depth solver returns a sloped-face centerline accepted by the candidate envelope query");
        var candidateAdjacency = candidateHitA?.Status == WorkingSurfaceQueryStatus.Hit &&
            candidateHitB?.Status == WorkingSurfaceQueryStatus.Hit
            ? Bridge.QueryPreparedCandidateFaceAdjacency(
                discardedCandidate.CandidateId,
                candidateWorkingContext.Id,
                candidateWorkingContext.Revision,
                candidateHitA.FaceId,
                candidateHitB.FaceId)
            : WorkingFaceAdjacencyStatus.Unknown;
        var candidateTopology = candidateHitA?.Status == WorkingSurfaceQueryStatus.Hit &&
            candidateHitB?.Status == WorkingSurfaceQueryStatus.Hit
            ? Bridge.QueryPreparedCandidateSelectedTopology(
                discardedCandidate.CandidateId,
                candidateWorkingContext.Id,
                candidateWorkingContext.Revision,
                new[] { candidateHitA.FaceId, candidateHitB.FaceId },
                candidateWorkingContext.MeshDeflection)
            : null;
        var candidateClearance = candidateWorkingContext != null
            ? Bridge.MeasurePreparedCandidateWorkingCurveClearance(
                discardedCandidate.CandidateId,
                candidateWorkingContext.Id,
                candidateWorkingContext.Revision,
                new[]
                {
                    NativeLine(new Vec3(0,0,30),new Vec3(10,0,30)),
                    NativeLine(new Vec3(0,2,30),new Vec3(10,2,30))
                },
                .4,
                .01,
                100)
            : null;
        var candidateConnectionObstacle = candidateWorkingContext != null &&
            candidateHitA?.Status == WorkingSurfaceQueryStatus.Hit &&
            candidateHitB?.Status == WorkingSurfaceQueryStatus.Hit
            ? Bridge.CheckPreparedCandidateConnectionPathObstacles(
                discardedCandidate.CandidateId,
                candidateWorkingContext.Id,
                candidateWorkingContext.Revision,
                new[]
                {
                    NativeLine(
                        new Vec3(candidateHitA.Point[0],candidateHitA.Point[1],candidateHitA.Point[2]),
                        new Vec3(candidateHitB.Point[0],candidateHitB.Point[1],candidateHitB.Point[2])),
                    NativeLine(
                        new Vec3(candidateHitB.Point[0],candidateHitB.Point[1],candidateHitB.Point[2]),
                        new Vec3(candidateHitB.Point[0],candidateHitB.Point[1],candidateHitB.Point[2]+5)),
                    NativeLine(
                        new Vec3(candidateHitB.Point[0],candidateHitB.Point[1],candidateHitB.Point[2]+5),
                        new Vec3(candidateHitB.Point[0],candidateHitB.Point[1],candidateHitB.Point[2]+10))
                },
                moldExitTransitionIndex: 1,
                connectionToleranceMm: .01)
            : null;
        var candidateSurfaceElements = candidateWorkingContext != null &&
            candidateHitA?.Status == WorkingSurfaceQueryStatus.Hit &&
            candidateHitB?.Status == WorkingSurfaceQueryStatus.Hit &&
            candidateHitC?.Status == WorkingSurfaceQueryStatus.Hit
            ? Bridge.QueryPreparedCandidateSurfaceElements(
                discardedCandidate.CandidateId,
                candidateWorkingContext.Id,
                candidateWorkingContext.Revision,
                new[] { candidateHitA.FaceId, candidateHitB.FaceId, candidateHitC.FaceId },
                new[]
                {
                    new WorkingRegionLoopData
                    {
                        IsHole = false,
                        Points = new[] { -20.0, -20, 20, -20, 20, 20, -20, 20 }
                    }
                },
                candidateWorkingContext.MeshDeflection,
                .1)
            : null;
        CurveDefinitionData? candidatePathDefinition = CreateCandidateLineAboveHits(candidateHitA,candidateHitB);
        int candidatePathStageId = int.MinValue + 741;
        var candidatePathStage = candidatePathDefinition != null && candidateWorkingContext != null &&
            candidateHitA?.Status == WorkingSurfaceQueryStatus.Hit && candidateHitB?.Status == WorkingSurfaceQueryStatus.Hit
            ? Bridge.StagePreparedCandidateWorkingCurveSegment(
                discardedCandidate.CandidateId,
                candidateWorkingContext.Id,
                candidateWorkingContext.Revision,
                candidatePathStageId,
                "TurnSegment",
                candidatePathDefinition,
                new[]{candidateHitA.FaceId,candidateHitB.FaceId},
                0,0,-1,
                .05,.1,2.0,18,100000)
            : null;
        int candidateExactPathStageId = int.MinValue + 744;
        var candidateExactPathStage = candidatePathDefinition != null && candidateWorkingContext != null
            ? Bridge.StagePreparedCandidateExactCurveSegment(
                discardedCandidate.CandidateId,
                candidateWorkingContext.Id,
                candidateWorkingContext.Revision,
                candidateExactPathStageId,
                "HeatingRunSegment",
                candidatePathDefinition,
                .05,
                .1,
                2.0,
                18,
                100000)
            : null;
        int candidateConnectionPathStageId = int.MinValue + 745;
        var candidateConnectionPathStage = candidatePathDefinition != null && candidateWorkingContext != null
            ? Bridge.StagePreparedCandidateExactCurveSegment(
                discardedCandidate.CandidateId,
                candidateWorkingContext.Id,
                candidateWorkingContext.Revision,
                candidateConnectionPathStageId,
                "ConnectionPathSegment",
                candidatePathDefinition,
                .05,
                .1,
                2.0,
                18,
                100000)
            : null;
        bool candidatePathCommitted = candidatePathStage != null && candidateExactPathStage != null &&
            candidateConnectionPathStage != null &&
            candidateWorkingContext != null &&
            Bridge.CommitPreparedCandidateWorkingPathGeometry(
                discardedCandidate.CandidateId,
                candidateWorkingContext.Id,
                candidateWorkingContext.Revision,
                new[] { candidatePathStageId, candidateExactPathStageId, candidateConnectionPathStageId },
                new[] { 11001, 11003, 11004 });
        bool candidateFeaturesReadable = candidateFaceFeatures?.Status == FaceFeatureQueryStatus.Success &&
            candidateFaceFeatures.Features.Length > 0 &&
            candidateBoundary != null && candidateBoundary.Length > 0 &&
            candidateBoundaryPoint != null && candidateBoundaryPoint.Length == 3 &&
            candidateWorkingContext != null && candidateWorkingContext.Revision == discardedCandidate.BaseModelRevision + 1 &&
            candidateWorkingHit?.Status == WorkingSurfaceQueryStatus.Hit && candidateWorkingHit.FaceId >= 0 &&
            candidateAdjacency == WorkingFaceAdjacencyStatus.Adjacent &&
            candidateTopology?.Status == WorkingTopologyQueryStatus.Success &&
            candidateTopology.Components.Length == 1 && candidateTopology.Adjacencies.Length == 1 &&
            candidateClearance?.Status == WorkingCurveClearanceStatus.Success &&
            Math.Abs(candidateClearance.MinimumCenterlineDistanceMm-2)<1e-6 &&
            candidateConnectionObstacle?.Status == WorkingConnectionObstacleStatus.Clear &&
            candidateSurfaceElements?.Status == WorkingSurfaceElementQueryStatus.Success &&
            candidateSurfaceElements.Elements.Length > 0 &&
            candidatePathStage != null && candidatePathCommitted && candidatePathStage.Length > 0 &&
            candidatePathStage.Samples != null && candidatePathStage.Samples.Length >= 6 &&
            candidatePathStage.Samples.Length % 3 == 0 && candidatePathStage.ApproximationComplete &&
            candidatePathStage.ProbeCount > 0 && candidatePathStage.MaximumSampleSpacingMm <= 2.0 &&
            candidatePathStage.Samples.Take(3).SequenceEqual(candidatePathStage.Start) &&
            candidatePathStage.Samples.TakeLast(3).SequenceEqual(candidatePathStage.End) &&
            candidateExactPathStage != null && candidateExactPathStage.Length > 0 &&
            candidateExactPathStage.Samples != null && candidateExactPathStage.Samples.Length >= 6 &&
            candidateExactPathStage.ApproximationComplete &&
            candidateExactPathStage.Samples.Take(3).SequenceEqual(candidateExactPathStage.Start) &&
            candidateExactPathStage.Samples.TakeLast(3).SequenceEqual(candidateExactPathStage.End);
        candidateFeaturesReadable = candidateFeaturesReadable && candidateConnectionPathStage != null &&
            candidateConnectionPathStage.Length > 0 && candidateConnectionPathStage.ApproximationComplete;
        bool candidatePreparedWithoutPublishing =
            discardedCandidate.Status == ModelLoadCandidateStatus.Prepared &&
            discardedCandidate.BaseModelRevision == revisionBeforeCandidatePreparation &&
            discardedCandidate.CandidateId > 0 &&
            candidateFeaturesReadable &&
            Bridge.GetModelRevision() == revisionBeforeCandidatePreparation &&
            Bridge.GetShapeInfo().Faces == faceCountBeforeCandidatePreparation &&
            Bridge.QueryWorkingSurface(panels.Id, panels.Revision, 0, 0).Status == WorkingSurfaceQueryStatus.Hit &&
            Bridge.SetTurnSegmentEnabled(oldDocumentPathId, true, .2, .8, .3);
        Check(candidatePreparedWithoutPublishing,
            "candidate face features and boundaries are queryable without changing active geometry, revision, context, or route presentation");
        Check(Bridge.DiscardStepCandidate(discardedCandidate.CandidateId) &&
              Bridge.GetPreparedCandidateFaceGeometryFeatures(discardedCandidate.CandidateId).Status == FaceFeatureQueryStatus.CandidateNotFound &&
              Bridge.GetPreparedCandidateFaceUvBoundary(discardedCandidate.CandidateId, 0) == null &&
              Bridge.SamplePreparedCandidateUvTo3D(discardedCandidate.CandidateId, 0, 0, 0) == null &&
              candidateWorkingContext != null &&
              QueryCandidate(discardedCandidate.CandidateId, candidateWorkingContext, new Vec3(0, 0, 5)).Status ==
                  WorkingSurfaceQueryStatus.StaleContext &&
              Bridge.MeasurePreparedCandidateWorkingCurveDepthEnvelope(
                  discardedCandidate.CandidateId,
                  candidateWorkingContext.Id,
                  candidateWorkingContext.Revision,
                  new[] { 0.0, 0, 0, 1, 0, 0 },
                  new[] { 0 },
                  0,
                  0,
                  0,
                  new[] { 0.0, 0, 0 },
                  .3,
                  0,
                  .1,
                  .1,
                  0,
                  .01,
                  1000).Status == WorkingCurveDepthEnvelopeStatus.CandidateNotFound &&
              Bridge.PublishStepCandidate(discardedCandidate.CandidateId).Status == ModelLoadStatus.FailedBeforeCommit &&
              Bridge.GetModelRevision() == revisionBeforeCandidatePreparation &&
              Bridge.GetShapeInfo().Faces == faceCountBeforeCandidatePreparation &&
              Bridge.SetTurnSegmentEnabled(oldDocumentPathId, true, .2, .8, .3),
            "discarding prepared candidate curves frees its session while preserving the active route and geometry");

        var staleCandidate = Bridge.PrepareStepCandidate(Path.Combine(args[0], "sphere.stp"));
        var replacementCandidate = Bridge.PrepareStepCandidate(Path.Combine(args[0], "sphere.stp"));
        var staleCandidateContext = staleCandidate.Status == ModelLoadCandidateStatus.Prepared
            ? Bridge.CreatePreparedCandidateWorkingSurfaceContext(staleCandidate.CandidateId,0,0,1,1,0,0)
            : null;
        var replacementCandidateContext = replacementCandidate.Status == ModelLoadCandidateStatus.Prepared
            ? Bridge.CreatePreparedCandidateWorkingSurfaceContext(replacementCandidate.CandidateId,0,0,1,1,0,0)
            : null;
        var replacementHitA = replacementCandidateContext != null
            ? QueryCandidate(replacementCandidate.CandidateId,replacementCandidateContext,new Vec3(-2,0,25))
            : null;
        var replacementHitB = replacementCandidateContext != null
            ? QueryCandidate(replacementCandidate.CandidateId,replacementCandidateContext,new Vec3(2,0,25))
            : null;
        CurveDefinitionData? replacementPathDefinition = CreateCandidateLineAboveHits(replacementHitA,replacementHitB);
        int replacementPathStageId = int.MinValue + 742;
        var replacementPathStage = replacementPathDefinition != null && replacementCandidateContext != null &&
            replacementHitA?.Status == WorkingSurfaceQueryStatus.Hit && replacementHitB?.Status == WorkingSurfaceQueryStatus.Hit
            ? Bridge.StagePreparedCandidateWorkingCurveSegment(
                replacementCandidate.CandidateId,
                replacementCandidateContext.Id,
                replacementCandidateContext.Revision,
                replacementPathStageId,
                "TurnSegment",
                replacementPathDefinition,
                new[]{replacementHitA.FaceId},
                0,0,-1,
                .05,.1,2.0,18,100000)
            : null;
        int replacementConnectionStageId = int.MinValue + 746;
        var replacementConnectionStage = replacementPathDefinition != null && replacementCandidateContext != null
            ? Bridge.StagePreparedCandidateExactCurveSegment(
                replacementCandidate.CandidateId,
                replacementCandidateContext.Id,
                replacementCandidateContext.Revision,
                replacementConnectionStageId,
                "ConnectionPathSegment",
                replacementPathDefinition,
                .05,
                .1,
                2.0,
                18,
                100000)
            : null;
        bool replacementPathCommitted = replacementPathStage != null && replacementConnectionStage != null &&
            replacementCandidateContext != null &&
            Bridge.CommitPreparedCandidateWorkingPathGeometry(
                replacementCandidate.CandidateId,
                replacementCandidateContext.Id,
                replacementCandidateContext.Revision,
                new[]{replacementPathStageId, replacementConnectionStageId},
                new[]{11002, 11005});
        var replacementLoad = Bridge.PublishStepCandidate(replacementCandidate.CandidateId);
        Check(replacementLoad.Status == ModelLoadStatus.Committed &&
              replacementLoad.ModelRevision==Bridge.GetModelRevision() &&
              replacementLoad.ModelRevision == revisionBeforeCandidatePreparation + 1 &&
              replacementCandidateContext != null &&
              replacementCandidateContext.Revision == replacementLoad.ModelRevision &&
              replacementPathStage != null && replacementPathCommitted && replacementPathStage.Length > 0 &&
              replacementConnectionStage != null && replacementConnectionStage.Length > 0 &&
              Bridge.SetTurnSegmentEnabled(11002,true,.2,.8,.3),
            "publishing a prepared candidate replaces the model and displays committed heating and connection curves");
        var staleStageDefinition = new CurveDefinitionData
        {
            Kind="Line",Direction="Forward",Start=new[]{-1.0,0,25},End=new[]{1.0,0,25}
        };
        bool staleCandidatePathRejected = staleCandidateContext != null &&
            Bridge.StagePreparedCandidateWorkingCurveSegment(
                staleCandidate.CandidateId,
                staleCandidateContext.Id,
                staleCandidateContext.Revision,
                int.MinValue + 743,
                "TurnSegment",
                staleStageDefinition,
                new[]{0},
                0,0,-1,
                .05,.1,2.0,18,100000) == null;
        var stalePublish = Bridge.PublishStepCandidate(staleCandidate.CandidateId);
        Check(staleCandidatePathRejected && stalePublish.Status == ModelLoadStatus.FailedBeforeCommit &&
              stalePublish.Diagnostic == "PreparedCandidateStale" &&
              stalePublish.ModelRevision == replacementLoad.ModelRevision &&
              Bridge.GetModelRevision() == replacementLoad.ModelRevision,
            "candidate prepared against an older model revision cannot replace the newer active document");
        Check(!Bridge.SetTurnSegmentEnabled(oldDocumentPathId,true,.2,.8,.3),
            "successful model reload removes the prior document path presentation");
        Check(!Bridge.CommitWorkingPathGeometry(panels.Id,panels.Revision,Array.Empty<int>(),Array.Empty<int>(),new[]{reloadStage}),
            "stale model/context cannot commit a staged working polyline");
        var afterStageReload=Bridge.CreateWorkingSurfaceContext(0,0,1,1,0,0);
        Check(Bridge.StageWorkingPolyline(afterStageReload.Id,afterStageReload.Revision,reloadStage,new[]{0.0,0,0,1,0,0}),
            "model reload removes prior staged polyline handle and frees its ID");
        Bridge.DiscardWorkingPathGeometry(panels.Id,panels.Revision,Array.Empty<int>(),new[]{reloadStage});
        long revisionBeforeFailedReload=Bridge.GetModelRevision();
        int faceCountBeforeFailedReload=Bridge.GetShapeInfo().Faces;
        var failedReload=Bridge.LoadStepDetailed(Path.Combine(args[0],"absent.stp"));
        Check(failedReload.Status==ModelLoadStatus.FailedBeforeCommit &&
              failedReload.ModelRevision==revisionBeforeFailedReload,
            "failed model load reports the unchanged active revision");
        Check(Bridge.GetModelRevision()==revisionBeforeFailedReload &&
              Bridge.GetShapeInfo().Faces==faceCountBeforeFailedReload &&
              Bridge.QueryWorkingSurface(afterStageReload.Id,afterStageReload.Revision,0,0).Status==WorkingSurfaceQueryStatus.Hit,
            "failed reload preserves model, revision, and active working context");
        Check(!Bridge.StageWorkingPolyline(afterStageReload.Id,afterStageReload.Revision,reloadStage,new[]{1.0,0,0,2,0,0}),
            "failed reload preserves staged-polyline ownership");
        var stillLoaded=Bridge.CreateWorkingSurfaceContext(0,0,1,1,0,0);
        Check(stillLoaded!=null,"failed reload leaves the active model available");
        if (stillLoaded != null)
        {
            var sphereFeatures = Bridge.GetFaceGeometryFeatures();
            int[] sphereFaceIds = sphereFeatures.Features?
                .Select(feature => feature.FaceIndex)
                .ToArray() ?? Array.Empty<int>();
            var sphereRegion = new[]
            {
                new WorkingRegionLoopData
                {
                    IsHole = false,
                    Points = new[] { -21.0, -21.0, 21.0, -21.0, 21.0, 21.0, -21.0, 21.0 }
                }
            };

            WorkingSurfaceElementsResult? coarseSphereSurface = sphereFeatures.Status == FaceFeatureQueryStatus.Success
                ? Bridge.QueryWorkingSurfaceElements(
                    stillLoaded.Id,
                    stillLoaded.Revision,
                    sphereFaceIds,
                    sphereRegion,
                    deflectionMm: 0.4,
                    angularToleranceRad: 0.5)
                : null;
            WorkingSurfaceElementsResult? fineSphereSurface = sphereFeatures.Status == FaceFeatureQueryStatus.Success
                ? Bridge.QueryWorkingSurfaceElements(
                    stillLoaded.Id,
                    stillLoaded.Revision,
                    sphereFaceIds,
                    sphereRegion,
                    deflectionMm: 0.05,
                    angularToleranceRad: 0.1)
                : null;

            const double sphereRadiusMm = 20.0;
            double expectedVisibleHemisphereAreaMm2 = 2.0 * Math.PI * sphereRadiusMm * sphereRadiusMm;
            double coarseVisibleAreaMm2 = coarseSphereSurface?.Elements.Sum(element => element.SurfaceAreaMm2) ?? 0.0;
            double fineVisibleAreaMm2 = fineSphereSurface?.Elements.Sum(element => element.SurfaceAreaMm2) ?? 0.0;
            double coarseAreaDeviationMm2 = Math.Abs(coarseVisibleAreaMm2 - expectedVisibleHemisphereAreaMm2);
            double fineAreaDeviationMm2 = Math.Abs(fineVisibleAreaMm2 - expectedVisibleHemisphereAreaMm2);
            bool fineElementsStayOnFirstHitHemisphere = fineSphereSurface?.Elements.All(element =>
                element.ModelCentroid is { Length: 3 } centroid && centroid[2] >= -0.1) == true;

            Check(coarseSphereSurface is not null &&
                  fineSphereSurface is not null &&
                  coarseSphereSurface.Status == WorkingSurfaceElementQueryStatus.Success &&
                  fineSphereSurface.Status == WorkingSurfaceElementQueryStatus.Success &&
                  sphereFaceIds.Length > 0 &&
                  fineSphereSurface.AreaErrorBoundMm2 < coarseSphereSurface.AreaErrorBoundMm2 &&
                  fineAreaDeviationMm2 <= coarseAreaDeviationMm2 + 0.01 &&
                  coarseAreaDeviationMm2 <= coarseSphereSurface.AreaErrorBoundMm2 + 2.0 &&
                  fineAreaDeviationMm2 <= fineSphereSurface.AreaErrorBoundMm2 + 2.0 &&
                  fineElementsStayOnFirstHitHemisphere,
                "curved sphere patch area converges toward its visible hemisphere and reports a shrinking error bound");

            Bridge.ReleaseWorkingSurfaceContext(stillLoaded.Id);
        }

        Check(Bridge.LoadStep(Path.Combine(args[0], "halfCylinder.stp")),
            "반원통 표면 면적 fixture를 불러온다");
        var halfCylinderContext = Bridge.CreateWorkingSurfaceContext(0, 0, 1, 1, 0, 0);
        var halfCylinderFeatures = Bridge.GetFaceGeometryFeatures();
        int[] halfCylinderFaceIds = halfCylinderFeatures.Features?
            .Where(feature => feature.SurfaceType == "Cylinder")
            .Select(feature => feature.FaceIndex)
            .ToArray() ?? Array.Empty<int>();
        var halfCylinderRegion = new[]
        {
            new WorkingRegionLoopData
            {
                IsHole = false,
                Points = new[] { -55.0, -55.0, 55.0, -55.0, 55.0, 55.0, -55.0, 55.0 }
            }
        };

        WorkingSurfaceElementsResult? coarseHalfCylinderSurface = halfCylinderContext is not null &&
                halfCylinderFeatures.Status == FaceFeatureQueryStatus.Success
            ? Bridge.QueryWorkingSurfaceElements(
                halfCylinderContext.Id,
                halfCylinderContext.Revision,
                halfCylinderFaceIds,
                halfCylinderRegion,
                deflectionMm: 0.4,
                angularToleranceRad: 0.5)
            : null;
        WorkingSurfaceElementsResult? fineHalfCylinderSurface = halfCylinderContext is not null &&
                halfCylinderFeatures.Status == FaceFeatureQueryStatus.Success
            ? Bridge.QueryWorkingSurfaceElements(
                halfCylinderContext.Id,
                halfCylinderContext.Revision,
                halfCylinderFaceIds,
                halfCylinderRegion,
                deflectionMm: 0.05,
                angularToleranceRad: 0.1)
            : null;

        const double expectedHalfCylinderAreaMm2 = Math.PI * 50.0 * 100.0;
        double coarseHalfCylinderAreaMm2 = coarseHalfCylinderSurface?.Elements
            .Sum(element => element.SurfaceAreaMm2) ?? 0.0;
        double fineHalfCylinderAreaMm2 = fineHalfCylinderSurface?.Elements
            .Sum(element => element.SurfaceAreaMm2) ?? 0.0;
        double coarseHalfCylinderDeviationMm2 = Math.Abs(
            coarseHalfCylinderAreaMm2 - expectedHalfCylinderAreaMm2);
        double fineHalfCylinderDeviationMm2 = Math.Abs(
            fineHalfCylinderAreaMm2 - expectedHalfCylinderAreaMm2);

        Check(halfCylinderContext is not null &&
              halfCylinderFeatures.Status == FaceFeatureQueryStatus.Success &&
              halfCylinderFaceIds.Length == 1 &&
              coarseHalfCylinderSurface is not null &&
              fineHalfCylinderSurface is not null &&
              coarseHalfCylinderSurface?.Status == WorkingSurfaceElementQueryStatus.Success &&
              fineHalfCylinderSurface?.Status == WorkingSurfaceElementQueryStatus.Success &&
              fineHalfCylinderSurface.AreaErrorBoundMm2 < coarseHalfCylinderSurface.AreaErrorBoundMm2 &&
              coarseHalfCylinderDeviationMm2 <= coarseHalfCylinderSurface.AreaErrorBoundMm2 + 2.0 &&
              fineHalfCylinderDeviationMm2 <= fineHalfCylinderSurface.AreaErrorBoundMm2 + 2.0,
            "반원통 곡면 면적이 5000π mm²에 수렴하고 세밀한 메시의 오차 한계가 감소한다");

        if (halfCylinderContext is not null)
            Bridge.ReleaseWorkingSurfaceContext(halfCylinderContext.Id);

        string designStepPath = Path.Combine(args[0], "design-readback.stp");
        var designStepGroup = new StepGeometryGroupData
        {
            Name = "C001",
            Wires = new[]
            {
                new StepDesignWireData
                {
                    Name = "Heating",
                    Curves = new[]
                    {
                        new StepDesignCurveData
                        {
                            Definition = NativeLine(new Vec3(-10, 0, 0), new Vec3(10, 0, 0)),
                            IsReversed = false
                        }
                    }
                }
            }
        };
        StepDesignWriteResult designStepResult = Bridge.WriteDesignStep(
            designStepPath,
            null,
            includeSourceModel: false,
            groups: new[] { designStepGroup });
        bool designStepReadbackSucceeded = designStepResult.Status == StepDesignWriteStatus.Success &&
            File.Exists(designStepPath) && new FileInfo(designStepPath).Length > 0;
        if (File.Exists(designStepPath))
            File.Delete(designStepPath);
        Check(designStepReadbackSucceeded,
            "STEP writer가 출력한 곡선의 edge 수·끝점·길이를 재읽기에서 검증한다");

        string designStepWithSourcePath = Path.Combine(args[0], "design-with-source-readback.stp");
        StepDesignWriteResult designStepWithSourceResult = Bridge.WriteDesignStep(
            designStepWithSourcePath,
            Path.Combine(args[0], "halfCylinder.stp"),
            includeSourceModel: true,
            groups: new[] { designStepGroup });
        bool designStepWithSourceReadbackSucceeded =
            designStepWithSourceResult.Status == StepDesignWriteStatus.Success &&
            File.Exists(designStepWithSourcePath) &&
            new FileInfo(designStepWithSourcePath).Length > 0;
        if (File.Exists(designStepWithSourcePath))
            File.Delete(designStepWithSourcePath);
        Check(designStepWithSourceReadbackSucceeded,
            "STEP 원본 포함 출력도 원본과 최종 곡선을 재읽기에서 비교한다");

        if (runGeo00)
        {
            Geo00Experiment.Run(args[2], args[3]);
            Check(File.Exists(args[3]),
                "실제 Mold 축 스캔과 깊이 질의가 재현 JSON으로 기록된다");

            string reportDirectory = Path.GetDirectoryName(Path.GetFullPath(args[3]))!;
            IReadOnlyList<string> controlFixtureReports =
                Geo00Experiment.RunControlFixtures(args[0], reportDirectory);
            Check(controlFixtureReports.Count == 11 && controlFixtureReports.All(File.Exists),
                "통제 평판·경사·곡면·가림 fixture 11종의 측정 결과가 기록된다");
            Check(HasDirectionalDepthReports(controlFixtureReports),
                "평판과 30°/60° 경사면 report가 최장·작업 U·작업 V 깊이 측정을 포함한다");
        }

        window.Close();
        Console.WriteLine($"{count} working-side checks passed.");
    }
}
