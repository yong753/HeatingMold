using System.Collections;
using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using HeatingMold.App;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

static class Program
{
    static int count;
    static void Check(bool value,string label)
    { if (!value) throw new Exception(label); Console.WriteLine("PASS "+label); ++count; }
    static ProjectionFrame Frame(WorkingContext c) => new(new(c.Frame[0],c.Frame[1],c.Frame[2]),
        new(c.Frame[9],c.Frame[10],c.Frame[11]),new(c.Frame[3],c.Frame[4],c.Frame[5]));
    static WorkingHit Query(WorkingContext c,Vec3 world)
    { var uv=Frame(c).Project(world); return Bridge.QueryWorkingSurface(c.Id,c.Revision,uv.X,uv.Y); }
    static ExitEdgeHit EvalEdge(WorkingContext c,ExitEdgeReference r,Vec3 world,double tolerance=0.05)
    { var uv=Frame(c).Project(world); return Bridge.EvaluateExitEdge(c.Id,c.Revision,r.Revision,r.EdgeKey,uv.X,uv.Y,tolerance); }
    static Vec3 P(double[] values) => new(values[0],values[1],values[2]);
    static IReadOnlyList<WorkingBoundaryRing> Rings(WorkingContext c)
    {
        var f=Frame(c); var triangles=new List<IReadOnlyList<Vec2>>();
        for (int i=0;i<c.Triangles.Length;i+=9)
            triangles.Add(new[]{f.Project(new(c.Triangles[i],c.Triangles[i+1],c.Triangles[i+2])),
                f.Project(new(c.Triangles[i+3],c.Triangles[i+4],c.Triangles[i+5])),
                f.Project(new(c.Triangles[i+6],c.Triangles[i+7],c.Triangles[i+8]))});
        return WorkingBoundary.FromTriangles(triangles,c.MeshDeflection);
    }
    static object Invoke(object target,string name,params object[] args) =>
        target.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(target,args);
    static T Field<T>(object target,string name) =>
        (T)target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(target)!;
    static void SetField(object target,string name,object value) =>
        target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(target,value);
    static void RunGroup(string name, Action group)
    {
        try { group(); }
        catch (Exception ex)
        {
            Console.WriteLine("CHECK-GROUP FAILED " + name + ": " + ex);
            for (Exception inner = ex.InnerException; inner != null; inner = inner.InnerException)
                Console.WriteLine("  inner: " + inner);
            throw;
        }
    }
    [STAThread]
    static void Main(string[] args)
    {
        // Failure paths must not block automated runs on a modal error dialog.
        ErrorReporter.SuppressDialogs = true;
        string path=Path.Combine(args[0],"layers.stp");
        Check(Bridge.LoadStep(path),"load layered model without face selection");
        Check(Bridge.CreateWorkingContext(0,0,0,1,0,0)==null,"invalid working direction cannot create context");
        var front=Bridge.CreateWorkingContext(0,0,1,1,0,0);
        Check(front!=null && front.Triangles.Length>0,"whole-model native context and mesh");
        Check(front.Frame[2]>20,"datum beyond whole-model maximum");
        var top=Query(front,new(0,0,0)); var exposed=Query(front,new(40,0,0));
        Check(top.Status==WorkingQueryStatus.Hit && Math.Abs(top.Point[2]-20)<1e-6,"nearest front full-model hit hides lower surface");
        Check(exposed.Status==WorkingQueryStatus.Hit && Math.Abs(exposed.Point[2])<1e-6,"partly occluded face usable only at exposed points");
        Check(top.FaceId!=exposed.FaceId,"first hit returns actual face ID, not selected filter");
        var hiddenSelection=Bridge.QueryWorkingSurfaceForFaces(front.Id,front.Revision,new[]{exposed.FaceId},0,0);
        Check(hiddenSelection.Status==WorkingQueryStatus.Miss && hiddenSelection.Point==null,
            "selected lower face cannot fall through nearer unselected first hit");
        var visibleSelection=Bridge.QueryWorkingSurfaceForFaces(front.Id,front.Revision,new[]{exposed.FaceId},40,0);
        Check(visibleSelection.Status==WorkingQueryStatus.Hit && visibleSelection.IsSelectedFace &&
            visibleSelection.FaceId==exposed.FaceId && visibleSelection.Normal?.Length==3,
            "selected exposed first hit returns face, XYZ and surface normal");
        Check(Bridge.QueryWorkingSurfaceForFaces(front.Id,front.Revision,new[]{top.FaceId},0,0).Status==WorkingQueryStatus.Hit,
            "selected front face is accepted as the full-model first hit");
        Check(Bridge.QueryWorkingSurfaceForFaces(front.Id,front.Revision,new[]{top.FaceId},0,0).Status==WorkingQueryStatus.Hit &&
            Bridge.QueryWorkingSurfaceForFaces(front.Id,front.Revision,new[]{top.FaceId},double.NaN,0).Status==WorkingQueryStatus.InvalidInput,
            "first-hit query retains typed invalid-input status");
        Check(Bridge.QueryWorkingSurfaceForFaces(front.Id,front.Revision+1,new[]{top.FaceId},0,0).Status==WorkingQueryStatus.StaleContext,
            "selected-face query rejects stale contexts");
        Check(Query(front,new(80,0,0)).Status==WorkingQueryStatus.Miss,"outside silhouette has no hidden/back fallback");
        Check(Bridge.QueryWorkingSurface(front.Id,front.Revision,double.NaN,0).Status==WorkingQueryStatus.InvalidInput,"invalid UV status");
        var back=Bridge.CreateWorkingContext(0,0,-1,1,0,0);
        Check(back.Frame[2]<0 && Math.Abs(Query(back,new(0,0,0)).Point[2])<1e-6,"flipped side sees lower first hit and opposite outside datum");
        Check(Query(front,new(0,0,0)).Point[2]==20,"new preview leaves confirmed-context geometry unchanged");
        Check(Bridge.QueryWorkingSurface(front.Id,front.Revision+1,0,0).Status==WorkingQueryStatus.StaleContext,"native rejects wrong revision");
        Check(Bridge.QueryWorkingSurface(back.Id,front.Revision,0,0).Status==WorkingQueryStatus.Hit,"independent same-revision contexts coexist");
        Bridge.ReleaseWorkingContext(back.Id);
        Check(Bridge.QueryWorkingSurface(back.Id,back.Revision,0,0).Status==WorkingQueryStatus.StaleContext,"released context rejected");
        var layeredRings=Rings(front);
        Check(layeredRings.Count==1 && !layeredRings[0].IsHole,"occluder edges and shared triangles are not outer boundary");

        using var source=new HwndSource(new HwndSourceParameters("Working-side regression") { Width=900,Height=700 });
        Check(Bridge.InitViewer(source.Handle,900,700),"offscreen native viewer initialized");
        Bridge.SetViewFrame(0,0,1,0,1,0);
        Check(!Bridge.IsWorkingPointVisible(front.Id,front.Revision,0,0,0,.001),"camera visibility rejects occluded candidate at its own screen point");
        Check(Bridge.IsWorkingPointVisible(front.Id,front.Revision,40,0,0,.001),"camera visibility allows exposed lower surface");
        int topFace=top.FaceId;
        int projectionStage=int.MinValue+1200;
        var datumProjection=Bridge.StageProjectedWorkingPolyline(front.Id,front.Revision,projectionStage,"HeatingRun",
            new[]{0.0,0,30,1,0,30},new[]{topFace},0,0,-1);
        Check(datumProjection!=null && Math.Abs(datumProjection.Start[2]-20)<1e-5 && Math.Abs(datumProjection.End[2]-20)<1e-5,
            "outside datum route projects toward and onto selected model face");
        Bridge.DiscardWorkingRouteGeometry(front.Id,front.Revision,Array.Empty<int>(),new[]{projectionStage});
        var priorRoute=Bridge.DisplayWorkingRoutePiece(front.Id,front.Revision,900,"TurnConnector",new[]{0.0,0,20,1,0,20});
        var stagedRoute=Bridge.DisplayWorkingRoutePiece(front.Id,front.Revision,int.MinValue,"TurnConnector",new[]{2.0,0,20,3,0,20});
        var failedStage=Bridge.DisplayWorkingRoutePiece(front.Id,front.Revision,int.MinValue+1,"TurnConnector",new[]{double.NaN,0,20});
        int lineStage=int.MinValue/2;
        bool stagedLine=Bridge.StageWorkingPolyline(front.Id,front.Revision,lineStage,new[]{4.0,0,20,5,0,20});
        bool failedLine=Bridge.StageWorkingPolyline(front.Id,front.Revision,lineStage-1,new[]{double.NaN,0,20});
        Check(priorRoute!=null && stagedRoute!=null && failedStage==null && stagedLine && !failedLine,
            "route and exclusion candidates can fail after earlier route/polyline staging");
        Check(!Bridge.CommitWorkingRouteGeometry(front.Id,front.Revision,new[]{int.MinValue},Array.Empty<int>(),Array.Empty<int>()),
            "invalid candidate commit is rejected before replacing existing routes");
        Bridge.DiscardWorkingRouteGeometry(front.Id,front.Revision,new[]{int.MinValue},new[]{lineStage});
        Check(Bridge.SetConnectorEnabled(900,true,.2,.8,.3,false),"discarding failed candidate stage preserves prior selectable connector");
        Bridge.ClearRoutePieces();
        var releaseA=Bridge.CreateWorkingContext(0,0,1,1,0,0);
        var releaseB=Bridge.CreateWorkingContext(0,0,1,1,0,0);
        int releaseRouteId=int.MinValue+700, releaseLineId=int.MinValue/2+700;
        Check(Bridge.DisplayWorkingRoutePiece(releaseA.Id,releaseA.Revision,releaseRouteId,"TurnConnector",new[]{6.0,0,20,7,0,20})!=null &&
              Bridge.StageWorkingPolyline(releaseA.Id,releaseA.Revision,releaseLineId,new[]{6.0,1,20,7,1,20}),
            "stage route and polyline owned by context A");
        Check(Bridge.DisplayWorkingRoutePiece(releaseB.Id,releaseB.Revision,releaseRouteId,"TurnConnector",new[]{8.0,0,20,9,0,20})==null &&
              !Bridge.StageWorkingPolyline(releaseB.Id,releaseB.Revision,releaseLineId,new[]{8.0,1,20,9,1,20}),
            "same-revision context B cannot claim context A's occupied staging IDs");
        Bridge.DiscardWorkingRouteGeometry(releaseB.Id,releaseB.Revision,new[]{releaseRouteId},new[]{releaseLineId});
        Check(Bridge.SetConnectorEnabled(releaseRouteId,true,.2,.8,.3,false),"wrong-owner discard leaves context A staged route intact");
        Bridge.ReleaseWorkingContext(releaseA.Id);
        Check(!Bridge.SetConnectorEnabled(releaseRouteId,true,.2,.8,.3,false),"releasing context A removes its staged route AIS/ID");
        var reusedRoute=Bridge.DisplayWorkingRoutePiece(releaseB.Id,releaseB.Revision,releaseRouteId,"TurnConnector",new[]{6.0,0,20,7,0,20});
        bool reusedLine=Bridge.StageWorkingPolyline(releaseB.Id,releaseB.Revision,releaseLineId,new[]{6.0,1,20,7,1,20});
        Check(reusedRoute!=null && reusedLine &&
              Bridge.CommitWorkingRouteGeometry(releaseB.Id,releaseB.Revision,new[]{releaseRouteId},new[]{901},new[]{releaseLineId}) &&
              Bridge.SetConnectorEnabled(901,true,.2,.8,.3,false),
            "context B reuses released IDs and commits only its own staged handles at the same model revision");
        int ownedDiscardId=int.MinValue/2+701;
        Check(Bridge.StageWorkingPolyline(releaseB.Id,releaseB.Revision,ownedDiscardId,new[]{6.0,2,20,7,2,20}),"context B stages independently discardable line");
        Bridge.DiscardWorkingRouteGeometry(releaseB.Id,releaseB.Revision,Array.Empty<int>(),new[]{ownedDiscardId});
        Check(Bridge.StageWorkingPolyline(releaseB.Id,releaseB.Revision,ownedDiscardId,new[]{6.0,2,20,7,2,20}),"owning context discard frees its temporary polyline ID");
        Bridge.DiscardWorkingRouteGeometry(releaseB.Id,releaseB.Revision,Array.Empty<int>(),new[]{ownedDiscardId});
        var commitA=Bridge.CreateWorkingContext(0,0,1,1,0,0);
        var commitB=Bridge.CreateWorkingContext(0,0,1,1,0,0);
        int candidateA=int.MinValue+1500,candidateB=int.MinValue+1501;
        Check(Bridge.DisplayWorkingRoutePiece(commitA.Id,commitA.Revision,candidateA,"TurnConnector",new[]{10.0,0,20,11,0,20})!=null &&
              Bridge.DisplayWorkingRoutePiece(commitB.Id,commitB.Revision,candidateB,"TurnConnector",new[]{12.0,0,20,13,0,20})!=null,
              "two contexts own independent staged routes");
        Check(Bridge.CommitWorkingRouteGeometry(commitA.Id,commitA.Revision,new[]{candidateA},new[]{902},Array.Empty<int>()) &&
              Bridge.SetConnectorEnabled(candidateB,true,.2,.8,.3,false),
              "committing one context preserves another context staged route and ownership");
        Bridge.DiscardWorkingRouteGeometry(commitB.Id,commitB.Revision,new[]{candidateB},Array.Empty<int>());
        Check(Bridge.DisplayWorkingRoutePiece(commitB.Id,commitB.Revision,candidateB,"TurnConnector",new[]{12.0,0,20,13,0,20})!=null,
              "preserved other-context route remains discardable and releases its negative ID");
        Bridge.ClearOverlays();
        Bridge.SetViewFrame(1,0,1,0,1,0);
        Bridge.ZoomView(1.3);
        Check(Query(front,new(0,0,0)).Point[2]==20,"camera orbit/zoom do not alter fixed working side");

        var window=new MainWindow();
        var viewport=(OccViewport)window.FindName("Viewport");
        var active=Field<WorkingSideSession>(window,"_workingSide");
        Check((bool)Invoke(window,"ApplyWorkingSide",new Vec3(0,0,1)) && active.Confirmed?.Frame.Normal.Z==1,
            "direct axis application creates an active +Z context without a confirmation step");
        var sameSide=active.Confirmed;
        var areaType = typeof(MainWindow).Assembly.GetType("HeatingMold.App.WorkingArea")!;
        var retainedArea = Activator.CreateInstance(areaType, nonPublic: true)!;
        areaType.GetProperty("Frame")!.SetValue(retainedArea, sameSide.Frame);
        var retainedRegion = (List<List<Vec2>>)areaType.GetProperty("Region")!.GetValue(retainedArea)!;
        retainedRegion.Add(new(){new(0,0),new(1,0),new(0,1)});
        ((IList)Field<object>(window,"_areas")).Add(retainedArea);
        var selectedFaceIds=Field<HashSet<int>>(window,"_selectedFaceIds"); selectedFaceIds.Add(7);
        ((ListBox)window.FindName("SelectedFacesList")).Items.Add("seed");
        Check((bool)Invoke(window,"ApplyWorkingSide",new Vec3(0,0,1)) && ReferenceEquals(sameSide,active.Confirmed) && retainedRegion.Count==1,
            "same-axis selection is a no-op and retains dependent state");
        var prepare=Field<Func<long,long,bool>>(window,"_prepareWorkingFront");
        SetField(window,"_prepareWorkingFront",new Func<long,long,bool>((_,_)=>false));
        Check(!(bool)Invoke(window,"ApplyWorkingSide",new Vec3(0,0,-1)) && ReferenceEquals(sameSide,active.Confirmed) && retainedRegion.Count==1,
            "failed direction application rolls back active side and dependent state");
        SetField(window,"_prepareWorkingFront",prepare);
        SetField(window,"_beforeWorkingSideCommit",new Action(()=>throw new InvalidOperationException("injected precommit failure")));
        Check(!(bool)Invoke(window,"ApplyWorkingSide",new Vec3(0,0,-1)) && ReferenceEquals(sameSide,active.Confirmed) &&
            retainedRegion.Count==1 && selectedFaceIds.Contains(7) && ((ListBox)window.FindName("SelectedFacesList")).Items.Count==1 &&
            Bridge.QueryWorkingSurface(sameSide.Id.Value,sameSide.Revision.Value,0,0).Status==WorkingQueryStatus.Hit,
            "failure after camera preparation preserves old context and dependent presentation state");
        SetField(window,"_beforeWorkingSideCommit",new Action(()=>{}));
        Check((bool)Invoke(window,"ApplyWorkingSide",new Vec3(0,0,-1)) && active.Confirmed?.Frame.Normal.Z==-1 &&
            retainedRegion.Count==0 && selectedFaceIds.Count==0 && ((ListBox)window.FindName("SelectedFacesList")).Items.Count==0,
            "successful side change invalidates dependent selections");
        Check(window.FindName("WorkingPickBox")==null && typeof(OccViewport).GetProperty("WorkingBoundaryPickMode")==null &&
            typeof(OccViewport).GetEvent("WorkingBoundaryPicked")==null,"diagnostic UI and click interception are removed");
        Check(window.FindName("WorkingBoundaryCanvas")==null && window.FindName("WorkingAxisBox")==null,
            "boundary diagnostic canvas removed; axis selector removed because working direction is fixed to +Z");
        SidebarTabChecks.Run(window);
        AreaHighlightChecks.Run(window);
        RunGroup("ExclusionExitChecks", () => ExclusionExitChecks.Run(window, args[0]));
        RunGroup("SelectedExitChecks", () => SelectedExitChecks.Run(window, args[0]));
        RunGroup("VertexEditChecks", () => VertexEditChecks.Run(window, args[0]));
        RunGroup("ElectricalRefreshChecks", () => ElectricalRefreshChecks.Run(window));
        RunGroup("PatternStateChecks", () => PatternStateChecks.Run(window));
        RunGroup("GeometryRollbackChecks", () => GeometryRollbackChecks.Run(window, args[0]));
        Check(typeof(Bridge).GetMethod("HlrProject")==null && typeof(Bridge).GetMethod("HlrEdges")!=null,
            "HLR smoke API removed while exporter HlrEdges remains");
        viewport.RectangleSelectionMode=true;
        Check(viewport.RectangleSelectionMode,"rectangle interaction remains independent of removed diagnostic mode");
        // 새 검사 그룹(인출면/편집)이 exitSides.stp·layers.stp를 재로드해 기존 작업 컨텍스트를
        // 무효화하므로, 카메라 복귀 검사용 컨텍스트를 이 지점에서 새로 만든다.
        front = Bridge.CreateWorkingContext(0, 0, 1, 1, 0, 0);
        Check(front != null, "camera check recreates a live working context after group reloads");
        Check(Bridge.ReturnToWorkingFront(front.Id, front.Revision), "active working front is independent of camera state");
        Check(Bridge.LoadStep(Path.Combine(args[0], "ring.stp")), "load ring fixture");
        Check(Bridge.QueryWorkingSurface(front.Id,front.Revision,0,0).Status==WorkingQueryStatus.StaleContext,"model reload invalidates native contexts");
        var ringContext=Bridge.CreateWorkingContext(0,0,1,1,0,0); var rings=Rings(ringContext);
        Check(rings.Count==2 && rings.Count(r=>r.IsHole)==1,"whole-model union retains hole across disconnected adjacent sheet faces");
        Check(Query(ringContext,new(50,50,0)).Status==WorkingQueryStatus.Miss,"through-hole is not working surface");
        Check(rings.Count(r=>r.NestingDepth==0)==1,"outer contour computation remains available without UI presentation");
Check(Bridge.LoadStep(Path.Combine(args[0],"sphere.stp")),"load sphere with curved silhouette, not trim-edge outline");
        var sphere=Bridge.CreateWorkingContext(0,0,1,1,0,0); var sphereRings=Rings(sphere);
        Check(sphereRings.Count==1 && sphereRings[0].Points.Count>20,"curved silhouette exists independently of face wire/seam");
        double area=Math.Abs(RegionOps.SignedArea(sphereRings[0].Points));
        Check(Math.Abs(area-Math.PI*400)<10,"sphere mesh silhouette approximates projected disk, explicit tolerance");
        var tangent=Query(sphere,new(20,0,0));
        Check(tangent.Status==WorkingQueryStatus.Tangent || tangent.Status==WorkingQueryStatus.Miss,"tangent is explicit or no hit, never back surface fallback");
        var sphereTop=Query(sphere,new(0,0,0));
        Check(sphereTop.Status==WorkingQueryStatus.Hit && Math.Abs(sphereTop.Point[2]-20)<1e-5,"curved surface first hit");
        foreach (var n in new[]{new Vec3(1,0,0),new Vec3(-1,0,0),new Vec3(0,1,0),new Vec3(0,-1,0),new Vec3(0,0,1),new Vec3(0,0,-1)})
        {
            var axisX=Math.Abs(n.X)>.9 ? new Vec3(0,1,0) : new Vec3(1,0,0);
            var c=Bridge.CreateWorkingContext(n.X,n.Y,n.Z,axisX.X,axisX.Y,axisX.Z);
            var hit=Query(c,new(0,0,0));
            var p=new Vec3(hit.Point[0],hit.Point[1],hit.Point[2]);
            Check(hit.Status==WorkingQueryStatus.Hit && (p-n*20).Length<1e-5 && Vec3.Dot(Frame(c).Origin,n)>20,
                "all-axis outside datum and nearest surface: "+n);
            Bridge.ReleaseWorkingContext(c.Id);
        }
        Check(Bridge.LoadStep(Path.Combine(args[0],"tangentBack.stp")),"load tangent contact with rear surface behind it");
        var tangentContext=Bridge.CreateWorkingContext(0,0,1,1,0,0);
        var grazing=Query(tangentContext,new(20,0,0));
        Check(grazing.Status==WorkingQueryStatus.Tangent || grazing.Status==WorkingQueryStatus.GeometryFailure,
            "nearest grazing contact is not discarded in favor of a rear surface hit");
        var stalePresentation=Bridge.CaptureWorkingPresentation();
        Check(Bridge.LoadStep(Path.Combine(args[0],"trimmedVisibility.stp")),"load holed upper trimmed face with full lower sheet");
        Check(!Bridge.RestoreWorkingPresentation(stalePresentation.Id),"presentation savepoints cannot restore across model revision");
        Bridge.ReleaseWorkingPresentation(stalePresentation.Id);
        var trimmed=Bridge.CreateWorkingContext(0,0,1,1,0,0);
        Bridge.ReturnToWorkingFront(trimmed.Id,trimmed.Revision);
        Check(Query(trimmed,new(0,0,0)).Point[2]==0 && Bridge.IsWorkingPointVisible(trimmed.Id,trimmed.Revision,0,0,0,.001),
            "upper surface extension through trimmed hole does not occlude lower BRep hit");
        Check(Bridge.IsWorkingPointVisible(trimmed.Id,trimmed.Revision,60,0,0,.001),
            "outside upper face trim is not a visibility blocker");
        Check(!Bridge.IsWorkingPointVisible(trimmed.Id,trimmed.Revision,30,0,0,.001),
            "upper face IN hit still blocks the lower surface");
        Check(Bridge.IsWorkingPointVisible(trimmed.Id,trimmed.Revision,10,0,20,.001),
            "ON trim-boundary hit remains eligible for visibility");
        Check(Bridge.LoadStep(Path.Combine(args[0],"threePanels.stp")),"load three topologically adjacent working faces");
        var panels=Bridge.CreateWorkingContext(0,0,1,1,0,0);
        int faceA=Bridge.QueryWorkingSurface(panels.Id,panels.Revision,-1,0).FaceId;
        int faceB=Bridge.QueryWorkingSurface(panels.Id,panels.Revision,0,0).FaceId;
        int faceC=Bridge.QueryWorkingSurface(panels.Id,panels.Revision,1,0).FaceId;
        bool ab=Bridge.AreWorkingFacesAdjacent(panels.Id,panels.Revision,faceA,faceB);
        bool bc=Bridge.AreWorkingFacesAdjacent(panels.Id,panels.Revision,faceB,faceC);
        bool ac=Bridge.AreWorkingFacesAdjacent(panels.Id,panels.Revision,faceA,faceC);
        Check(faceA!=faceB && faceB!=faceC && faceA!=faceC && ab && bc && !ac,
            "ordered three-face path accepts A-B-C shared-edge transitions while endpoints A/C are not adjacent");
        int seamStage=int.MinValue+1300;
        var acrossSeams=Bridge.StageProjectedWorkingPolyline(panels.Id,panels.Revision,seamStage,"HeatingRun",
            new[]{.25, .5, 5.0, 2.75, .5, 5.0},new[]{faceA,faceB,faceC},0,0,-1);
        Check(acrossSeams!=null,"projected route reconnects across selected three-panel seams");
        Bridge.DiscardWorkingRouteGeometry(panels.Id,panels.Revision,Array.Empty<int>(),new[]{seamStage});
        int partialStage=int.MinValue+1301;
        Check(Bridge.StageProjectedWorkingPolyline(panels.Id,panels.Revision,partialStage,"HeatingRun",
            new[]{.25, .5, 5.0, 1.5, .5, 5.0, 3.5, .5, 5.0, 2.75, .5, 5.0},
            new[]{faceA,faceB,faceC},0,0,-1)==null,"partially out-of-domain projected path is rejected");
        int reloadStage=int.MinValue/2+91;
        Check(Bridge.StageWorkingPolyline(panels.Id,panels.Revision,reloadStage,new[]{0.0,0,0,1,0,0}),
            "working polyline stage created for cleanup/revision test");
        Bridge.ClearOverlays();
        Check(Bridge.StageWorkingPolyline(panels.Id,panels.Revision,reloadStage,new[]{0.0,0,0,1,0,0}),
            "ClearOverlays removes staged AIS and releases its temporary ID");
        Check(Bridge.LoadStep(Path.Combine(args[0],"sphere.stp")),"reload model with candidate polyline staged");
        Check(!Bridge.CommitWorkingRouteGeometry(panels.Id,panels.Revision,Array.Empty<int>(),Array.Empty<int>(),new[]{reloadStage}),
            "stale model/context cannot commit a staged working polyline");
        var afterStageReload=Bridge.CreateWorkingContext(0,0,1,1,0,0);
        Check(Bridge.StageWorkingPolyline(afterStageReload.Id,afterStageReload.Revision,reloadStage,new[]{0.0,0,0,1,0,0}),
            "model reload removes prior staged polyline handle and frees its ID");
        Bridge.DiscardWorkingRouteGeometry(panels.Id,panels.Revision,Array.Empty<int>(),new[]{reloadStage});
        Check(Bridge.LoadStep(Path.Combine(args[0],"pickCoincident.stp")),"load coincident sheets with opposite orientations");
        Bridge.SetViewFrame(0,0,1,0,1,0);
        int frontFace=-1,backFace=-1;
        for (int id=0;;++id)
        {
            var probe=Bridge.GetFaceNormal(id); if (probe==null) break;
            if (probe[2]>0.5) frontFace=id; else if (probe[2]<-0.5) backFace=id;
        }
        Check(frontFace>=0 && backFace>=0 && frontFace!=backFace,"coincident fixture keeps both opposite-facing faces");
        var coincidentCenter=Bridge.WorldToScreen(0,0,0);
        Check(coincidentCenter!=null,"coincident sheet center projects to screen");
        Check(Bridge.SelectFace(coincidentCenter[0],coincidentCenter[1],false)==frontFace,
            "click at equal depth selects the camera-facing face");
        Check(Bridge.HoverFace(coincidentCenter[0],coincidentCenter[1])==frontFace,
            "hover reports the same camera-facing face");
        Bridge.ClearHover();
        var coincidentRectangle=Bridge.SelectFacesInRectangle(
            coincidentCenter[0]-4,coincidentCenter[1]-4,coincidentCenter[0]+4,coincidentCenter[1]+4);
        // 면 집합 선택은 가시성과 무관하게 사각형과 교차하는 모든 모델 면을 포함한다.
        Check(coincidentRectangle!=null && coincidentRectangle.Length==2 &&
              coincidentRectangle.Contains(frontFace) && coincidentRectangle.Contains(backFace),
            "rectangle pick includes both coincident faces regardless of occlusion");
        var coincidentReverse=Bridge.SelectFacesInRectangle(
            coincidentCenter[0]+4,coincidentCenter[1]+4,coincidentCenter[0]-4,coincidentCenter[1]-4);
        Check(coincidentReverse!=null && coincidentReverse.Length==2 &&
              coincidentReverse.Contains(frontFace) && coincidentReverse.Contains(backFace),
            "reverse drag selects the same face set");
        var coincidentAll=Bridge.SelectFacesInRectangle(0,0,899,699);
        Check(coincidentAll!=null && coincidentAll.Length==2 &&
              coincidentAll.Contains(frontFace) && coincidentAll.Contains(backFace),
            "whole-view rectangle includes every model face");
        var coincidentEdge=Bridge.WorldToScreen(30,0,0);
        Check(coincidentEdge!=null,"coincident sheet edge projects to screen");
        var edgeRectangle=Bridge.SelectFacesInRectangle(
            coincidentEdge[0]-5,coincidentCenter[1]-4,coincidentEdge[0]+5,coincidentCenter[1]+4);
        Check(edgeRectangle!=null && edgeRectangle.Length==2 &&
              edgeRectangle.Contains(frontFace) && edgeRectangle.Contains(backFace),
            "rectangle crossing the face edge still includes the partially covered face");
        Bridge.ClearFaceSelection();
        var emptyRectangle=Bridge.SelectFacesInRectangle(1,1,2,2);
        var afterEmpty=Bridge.GetSelectedFaceIds();
        Check(emptyRectangle!=null && emptyRectangle.Length==0 &&
              afterEmpty!=null && afterEmpty.Length==0,
            "empty rectangle replaces the selection with an empty set");
        Bridge.SetViewFrame(0,0,-1,0,1,0);
        var flippedCenter=Bridge.WorldToScreen(0,0,0);
        Check(flippedCenter!=null && Bridge.SelectFace(flippedCenter[0],flippedCenter[1],false)==backFace,
            "opposite camera selects the other coincident face");
        Check(Bridge.LoadStep(Path.Combine(args[0],"pickStacked.stp")),"load near sheet covering larger far sheet");
        Bridge.SetViewFrame(0,0,1,0,1,0);
        var stackedCenter=Bridge.WorldToScreen(0,0,0);
        var stackedOuter=Bridge.WorldToScreen(40,0,-50);
        Check(stackedCenter!=null && stackedOuter!=null,"stacked fixture points project to screen");
        int nearFace=Bridge.SelectFace(stackedCenter[0],stackedCenter[1],false);
        int farFace=Bridge.SelectFace(stackedOuter[0],stackedOuter[1],false);
        var nearProbe=nearFace>=0?Bridge.GetFaceNormal(nearFace):null;
        var farProbe=farFace>=0?Bridge.GetFaceNormal(farFace):null;
        Check(nearProbe!=null && farProbe!=null && Math.Abs(nearProbe[5])<1e-6 && Math.Abs(farProbe[5]+50)<1e-6 && nearFace!=farFace,
            "occluded click never falls through to the surface behind the nearest hit");
        var stackedRectangle=Bridge.SelectFacesInRectangle(
            stackedCenter[0]-8,stackedCenter[1]-8,stackedCenter[0]+8,stackedCenter[1]+8);
        Check(stackedRectangle!=null && stackedRectangle.Contains(nearFace) && stackedRectangle.Contains(farFace),
            "rectangle pick includes the face hidden behind the near sheet");
        // 프리뷰: 드래그 중에는 하이라이트만 바뀌고, 취소하면 시작 시점 선택으로 돌아온다.
        Bridge.ClearFaceSelection();
        Bridge.SelectFace(stackedCenter[0],stackedCenter[1],false);
        var beforePreview=Bridge.GetSelectedFaceIds();
        Bridge.BeginFaceRectanglePreview();
        Bridge.PreviewFacesInRectangle(stackedOuter[0]-8,stackedOuter[1]-8,stackedOuter[0]+8,stackedOuter[1]+8);
        var duringPreview=Bridge.GetSelectedFaceIds();
        Bridge.CancelFaceRectanglePreview();
        var afterCancel=Bridge.GetSelectedFaceIds();
        Check(beforePreview!=null && beforePreview.Length==1 && beforePreview[0]==nearFace,
            "preview baseline captures the current face selection");
        Check(duringPreview!=null && duringPreview.Length==1 && duringPreview[0]==farFace,
            "preview highlights the face inside the drag rectangle");
        Check(afterCancel!=null && afterCancel.Length==1 && afterCancel[0]==nearFace,
            "cancel restores the selection captured at drag start");
        var confirmedFar=Bridge.SelectFacesInRectangle(
            stackedOuter[0]-8,stackedOuter[1]-8,stackedOuter[0]+8,stackedOuter[1]+8);
        Check(confirmedFar!=null && confirmedFar.Length==1 && confirmedFar[0]==farFace,
            "release confirms the same face set as the drag preview");
        // 프리뷰도 확정과 같은 질의를 쓰므로 가려진 면을 함께 하이라이트한다.
        Bridge.ClearFaceSelection();
        Bridge.BeginFaceRectanglePreview();
        Bridge.PreviewFacesInRectangle(
            stackedCenter[0]-8,stackedCenter[1]-8,stackedCenter[0]+8,stackedCenter[1]+8);
        var hiddenPreview=Bridge.GetSelectedFaceIds();
        Bridge.CancelFaceRectanglePreview();
        Check(hiddenPreview!=null && hiddenPreview.Length==2 &&
              hiddenPreview.Contains(nearFace) && hiddenPreview.Contains(farFace),
            "preview highlights the hidden face as well as the visible one");
        // 픽셀 이미지 비의존: 사각형 선택은 face-ID 렌더를 만들지 않는다.
        long buildsBeforeRectangle=Bridge.GetFaceIdBuildCount();
        Bridge.SelectFacesInRectangle(stackedOuter[0]-8,stackedOuter[1]-8,stackedOuter[0]+8,stackedOuter[1]+8);
        Bridge.SetViewFrame(0,0,-1,0,1,0);
        Bridge.SelectFacesInRectangle(stackedOuter[0]-8,stackedOuter[1]-8,stackedOuter[0]+8,stackedOuter[1]+8);
        long buildsAfterRectangle=Bridge.GetFaceIdBuildCount();
        Check(buildsAfterRectangle==buildsBeforeRectangle,
            "rectangle selection does not build face-id images");
        Check(Bridge.LoadStep(Path.Combine(args[0],"exitSides.stp")),"load exit-side fixture (box plus detached sheet)");
        int boxTop=-1,boxBottom=-1,boxPlusX=-1,boxMinusX=-1,boxPlusY=-1,boxMinusY=-1,nakedSheet=-1;
        for (int id=0;;++id)
        {
            var probe=Bridge.GetFaceNormal(id); if (probe==null) break;
            if (probe[2]>0.9) { if (Math.Abs(probe[5]-20)<1e-6) boxTop=id; else if (Math.Abs(probe[5])<1e-6) nakedSheet=id; }
            else if (probe[2]<-0.9 && Math.Abs(probe[5])<1e-6) boxBottom=id;
            if (probe[0]>0.9 && Math.Abs(probe[3]-60)<1e-6) boxPlusX=id;
            if (probe[0]<-0.9 && Math.Abs(probe[3])<1e-6) boxMinusX=id;
            if (probe[1]>0.9 && Math.Abs(probe[4]-40)<1e-6) boxPlusY=id;
            if (probe[1]<-0.9 && Math.Abs(probe[4])<1e-6) boxMinusY=id;
        }
        Check(boxTop>=0 && boxBottom>=0 && boxPlusX>=0 && boxMinusX>=0 && boxPlusY>=0 && boxMinusY>=0 && nakedSheet>=0,
            "exit-side fixture exposes all six box faces and the detached sheet");
        var topNeighbours=new HashSet<int>{boxPlusX,boxMinusX,boxPlusY,boxMinusY};
        var exitSides=Bridge.GetBoundaryExitSides(new[]{boxTop},0.05);
        Check(exitSides!=null && exitSides.Length==4,"box top boundary yields four side candidates");
        double exitPerimeter=0; bool exitTopOnly=exitSides!=null; var exitFaceIds=new HashSet<int>();
        if (exitSides!=null) foreach (var side in exitSides)
        {
            Check(side.Points!=null && side.Points.Length>=6 && side.FaceId!=boxTop,"exit-side candidate carries face id and polyline");
            exitFaceIds.Add(side.FaceId);
            for (int i=0;i+2<side.Points.Length;i+=3)
            {
                if (Math.Abs(side.Points[i+2]-20)>1e-3) exitTopOnly=false;
                if (i>=3)
                {
                    double dx=side.Points[i]-side.Points[i-3],dy=side.Points[i+1]-side.Points[i-2],dz=side.Points[i+2]-side.Points[i-1];
                    exitPerimeter+=Math.Sqrt(dx*dx+dy*dy+dz*dz);
                }
            }
        }
        Check(exitFaceIds.SetEquals(topNeighbours),"top-face boundary maps to the four neighbouring side faces");
        Check(Math.Abs(exitPerimeter-200)<0.5 && exitTopOnly,"side candidates trace the exact top perimeter on the boundary plane");
        var mergedNeighbours=new HashSet<int>{boxMinusX,boxPlusY,boxMinusY,boxBottom};
        var mergedSides=Bridge.GetBoundaryExitSides(new[]{boxTop,boxPlusX},0.05);
        double mergedLength=0; var mergedFaces=new HashSet<int>(); bool mergedPolyline=true;
        if (mergedSides!=null) foreach (var side in mergedSides)
        {
            mergedPolyline&=side.Points!=null && side.Points.Length>=6;
            mergedFaces.Add(side.FaceId);
            for (int i=3;i+2<side.Points.Length;i+=3)
            {
                double dx=side.Points[i]-side.Points[i-3],dy=side.Points[i+1]-side.Points[i-2],dz=side.Points[i+2]-side.Points[i-1];
                mergedLength+=Math.Sqrt(dx*dx+dy*dy+dz*dz);
            }
        }
        Check(mergedSides!=null && mergedPolyline && mergedFaces.SetEquals(mergedNeighbours) && Math.Abs(mergedLength-240)<0.5,
            "two-face region drops its seam and keeps the four remaining neighbours as boundary runs");
        var nakedSides=Bridge.GetBoundaryExitSides(new[]{nakedSheet},0.05);
        Check(nakedSides==null || nakedSides.Length==0,"detached sheet naked edges yield no exit-side candidate");
        Check(Bridge.GetBoundaryExitSides(new[]{-1},0.05)==null && Bridge.GetBoundaryExitSides(Array.Empty<int>(),0.05)==null,
            "invalid exit-side input is rejected");
        var boxExitContext=Bridge.CreateWorkingContext(0,0,1,1,0,0);
        Check(boxExitContext!=null,"box exit-edge working context created");
        var boxRefs=new List<ExitEdgeReference>();
        if (exitSides!=null) foreach (var side in exitSides)
        {
            Check(side.EdgeReferences!=null && side.EdgeReferences.Length==1,
                "straight boundary run keeps exactly one original edge reference");
            foreach (var reference in side.EdgeReferences ?? Array.Empty<ExitEdgeReference>())
            {
                boxRefs.Add(reference);
                Check(reference.Revision==Bridge.GetModelRevision() && reference.EdgeKey>=1 &&
                    reference.FaceId==side.FaceId && reference.StartIndex==0 &&
                    reference.EndIndex==side.Points.Length/3-1 &&
                    reference.LastParameter>reference.FirstParameter,
                    "original edge reference carries revision, face, parameter range and point span");
            }
        }
        Check(boxRefs.Count==4,"four top boundary edges keep four independent original references");
        if (boxExitContext!=null && exitSides!=null)
        {
            bool boxHits=true,boxTangents=true;
            foreach (var side in exitSides)
            {
                var reference=side.EdgeReferences?[0];
                if (reference==null || side.Points==null || side.Points.Length<6) { boxHits=false; continue; }
                int mid=(side.Points.Length/3)/2;
                var world=new Vec3(side.Points[mid*3],side.Points[mid*3+1],side.Points[mid*3+2]);
                var hit=EvalEdge(boxExitContext,reference,world);
                boxHits&=hit!=null && hit.Status==ExitEdgeQueryStatus.Hit &&
                    hit.FaceId==reference.FaceId && hit.Point!=null && (P(hit.Point)-world).Length<=1e-3;
                boxTangents&=hit!=null && hit.Tangent!=null && Math.Abs(P(hit.Tangent).Length-1)<=1e-6;
            }
            Check(boxHits,"straight edge evaluation lands on the original edge point");
            Check(boxTangents,"straight edge evaluation returns a unit tangent");
            var edge=exitSides[0].EdgeReferences[0];
            var start=new Vec3(exitSides[0].Points[0],exitSides[0].Points[1],exitSides[0].Points[2]);
            var next=new Vec3(exitSides[0].Points[3],exitSides[0].Points[4],exitSides[0].Points[5]);
            var direction=(next-start).Normalized();
            var startHit=EvalEdge(boxExitContext,edge,start);
            var insideHit=EvalEdge(boxExitContext,edge,start+direction*0.001);
            var outsideClamp=EvalEdge(boxExitContext,edge,start-direction*0.01);
            var farOutsideMiss=EvalEdge(boxExitContext,edge,start-direction*0.2);
            Check(startHit!=null && startHit.Status==ExitEdgeQueryStatus.Hit && startHit.Point!=null &&
                (P(startHit.Point)-start).Length<=1e-6,
                "open edge endpoint evaluates exactly at the trim end");
            Check(insideHit!=null && insideHit.Status==ExitEdgeQueryStatus.Hit && insideHit.Point!=null &&
                (P(insideHit.Point)-(start+direction*0.001)).Length<=1e-6,
                "working point just inside an open edge end evaluates on the edge");
            Check(outsideClamp!=null && outsideClamp.Status==ExitEdgeQueryStatus.Hit &&
                outsideClamp.Point!=null && (P(outsideClamp.Point)-start).Length<=1e-6,
                "working point just past an open edge end clamps to the trim end");
            Check(farOutsideMiss!=null && farOutsideMiss.Status==ExitEdgeQueryStatus.Miss,
                "working point far past an open edge end is rejected");
            var interior=EvalEdge(boxExitContext,edge,new Vec3(30,20,20));
            Check(interior!=null && interior.Status==ExitEdgeQueryStatus.Miss,
                "interior working point is not accepted as an edge hit");
            var datum=Frame(boxExitContext).Project(start);
            Check(Bridge.EvaluateExitEdge(boxExitContext.Id,boxExitContext.Revision,edge.Revision+1,edge.EdgeKey,
                datum.X,datum.Y,0.05).Status==ExitEdgeQueryStatus.StaleContext,
                "stale original edge revision is rejected");
            Check(Bridge.EvaluateExitEdge(boxExitContext.Id,boxExitContext.Revision,edge.Revision,edge.EdgeKey+1000000,
                datum.X,datum.Y,0.05).Status==ExitEdgeQueryStatus.StaleContext,
                "unknown original edge key is rejected");
            Check(Bridge.EvaluateExitEdge(boxExitContext.Id,boxExitContext.Revision,edge.Revision,edge.EdgeKey,
                double.NaN,0,0.05).Status==ExitEdgeQueryStatus.InvalidInput,
                "invalid original edge datum is rejected");
        }
        if (boxExitContext!=null) Bridge.ReleaseWorkingContext(boxExitContext.Id);
        Check(Bridge.LoadStep(Path.Combine(args[0],"exitSplit.stp")),"load split long-side fixture");
        int splitTopA=-1,splitTopB=-1,splitSideA=-1,splitSideB=-1;
        for (int id=0;;++id)
        {
            var probe=Bridge.GetFaceNormal(id); if (probe==null) break;
            if (Math.Abs(probe[2])>0.9 && Math.Abs(probe[5]-20)<1e-6)
            {
                if (Math.Abs(probe[3]-15)<1e-6) splitTopA=id;
                else if (Math.Abs(probe[3]-45)<1e-6) splitTopB=id;
            }
            if (Math.Abs(probe[1])>0.9 && Math.Abs(probe[4])<1e-6)
            {
                if (Math.Abs(probe[3]-15)<1e-6) splitSideA=id;
                else if (Math.Abs(probe[3]-45)<1e-6) splitSideB=id;
            }
        }
        Check(splitTopA>=0 && splitTopB>=0 && splitSideA>=0 && splitSideB>=0,
            "split long-side fixture exposes both top halves and both -Y neighbours");
        var splitSides=Bridge.GetBoundaryExitSides(new[]{splitTopA,splitTopB},0.05);
        Check(splitSides!=null && splitSides.Length==4,
            "split long side merges its two half runs into one side candidate");
        int mergedSideIndex=-1; double mergedSideLength=0; bool mergedSegments=false;
        if (splitSides!=null) for (int i=0;i<splitSides.Length;i++)
        {
            var side=splitSides[i];
            if (side?.Points==null || side.Points.Length<6) continue;
            bool onMinusY=true; double length=0;
            for (int k=0;k+2<side.Points.Length;k+=3)
            {
                if (Math.Abs(side.Points[k+1])>1e-3 || Math.Abs(side.Points[k+2]-20)>1e-3) { onMinusY=false; break; }
                if (k>=3)
                {
                    double dx=side.Points[k]-side.Points[k-3],dy=side.Points[k+1]-side.Points[k-2],dz=side.Points[k+2]-side.Points[k-1];
                    length+=Math.Sqrt(dx*dx+dy*dy+dz*dz);
                }
            }
            if (!onMinusY) continue;
            mergedSideIndex=i; mergedSideLength=length;
            var ids=new HashSet<int>(side.SegmentFaceIds ?? Array.Empty<int>());
            mergedSegments=side.SegmentFaceIds!=null && side.SegmentFaceIds.Length==side.Points.Length/3-1
                && ids.SetEquals(new HashSet<int>{splitSideA,splitSideB});
        }
        Check(mergedSideIndex>=0 && Math.Abs(mergedSideLength-60)<0.5 && mergedSegments,
            "merged candidate spans the whole long side with per-segment neighbour face ids");
        int singleFaceCandidates=0;
        if (splitSides!=null) foreach (var side in splitSides)
        {
            if (side?.SegmentFaceIds==null || side.Points==null) continue;
            if (side.SegmentFaceIds.Length!=side.Points.Length/3-1) continue;
            if (side.SegmentFaceIds.Distinct().Count()==1) singleFaceCandidates++;
        }
        Check(singleFaceCandidates==3,"the three unmerged boundaries keep a single neighbour face");
        var mergedSide=mergedSideIndex>=0 ? splitSides[mergedSideIndex] : null;
        var mergedRefs=mergedSide?.EdgeReferences;
        bool refsContiguous=mergedRefs!=null && mergedRefs.Length==2 &&
            (mergedRefs[0].EndIndex==mergedRefs[1].StartIndex || mergedRefs[1].EndIndex==mergedRefs[0].StartIndex);
        var refFaces=mergedRefs==null ? new HashSet<int>() : new HashSet<int>(mergedRefs.Select(r=>r.FaceId));
        Check(refsContiguous && refFaces.SetEquals(new HashSet<int>{splitSideA,splitSideB}),
            "merged split run keeps two contiguous original half-edge references");
        if (mergedSide!=null && mergedRefs!=null && refsContiguous)
        {
            var splitContext=Bridge.CreateWorkingContext(0,0,1,1,0,0);
            bool refHits=splitContext!=null;
            if (splitContext!=null)
            {
                foreach (var reference in mergedRefs)
                {
                    int mid=(reference.StartIndex+reference.EndIndex)/2;
                    var world=new Vec3(mergedSide.Points[mid*3],mergedSide.Points[mid*3+1],mergedSide.Points[mid*3+2]);
                    var hit=EvalEdge(splitContext,reference,world);
                    refHits&=hit!=null && hit.Status==ExitEdgeQueryStatus.Hit &&
                        hit.FaceId==reference.FaceId && hit.Point!=null && (P(hit.Point)-world).Length<=1e-3;
                }
                Bridge.ReleaseWorkingContext(splitContext.Id);
            }
            Check(refHits,"each half of a merged split run evaluates against its own original edge");
        }
        Check(Bridge.LoadStep(Path.Combine(args[0],"exitSplitThree.stp")),"load three-way split fixture");
        int threeTopA=-1,threeTopB=-1,threeTopC=-1,threeSideA=-1,threeSideB=-1,threeSideC=-1;
        for (int id=0;;++id)
        {
            var probe=Bridge.GetFaceNormal(id); if (probe==null) break;
            if (Math.Abs(probe[2])>0.9 && Math.Abs(probe[5]-20)<1e-6)
            {
                if (Math.Abs(probe[3]-10)<1e-6) threeTopA=id;
                else if (Math.Abs(probe[3]-30)<1e-6) threeTopB=id;
                else if (Math.Abs(probe[3]-50)<1e-6) threeTopC=id;
            }
            if (Math.Abs(probe[1])>0.9 && Math.Abs(probe[4])<1e-6)
            {
                if (Math.Abs(probe[3]-10)<1e-6) threeSideA=id;
                else if (Math.Abs(probe[3]-30)<1e-6) threeSideB=id;
                else if (Math.Abs(probe[3]-50)<1e-6) threeSideC=id;
            }
        }
        Check(threeTopA>=0 && threeTopB>=0 && threeTopC>=0 && threeSideA>=0 && threeSideB>=0 && threeSideC>=0,
            "three-way split fixture exposes every top third and every -Y neighbour");
        var threeSides=Bridge.GetBoundaryExitSides(new[]{threeTopA,threeTopB,threeTopC},0.05);
        Check(threeSides!=null && threeSides.Length==4,
            "three-way split long side merges into one side candidate");
        bool threeMerged=false;
        if (threeSides!=null) foreach (var side in threeSides)
        {
            if (side?.Points==null || side.Points.Length<6) continue;
            bool onMinusY=true; double length=0;
            for (int k=0;k+2<side.Points.Length;k+=3)
            {
                if (Math.Abs(side.Points[k+1])>1e-3 || Math.Abs(side.Points[k+2]-20)>1e-3) { onMinusY=false; break; }
                if (k>=3)
                {
                    double dx=side.Points[k]-side.Points[k-3],dy=side.Points[k+1]-side.Points[k-2],dz=side.Points[k+2]-side.Points[k-1];
                    length+=Math.Sqrt(dx*dx+dy*dy+dz*dz);
                }
            }
            if (!onMinusY || Math.Abs(length-60)>=0.5) continue;
            var ids=new HashSet<int>(side.SegmentFaceIds ?? Array.Empty<int>());
            threeMerged=side.SegmentFaceIds!=null && side.SegmentFaceIds.Length==side.Points.Length/3-1
                && ids.SetEquals(new HashSet<int>{threeSideA,threeSideB,threeSideC});
        }
        Check(threeMerged,"merged candidate crosses the middle sliver and keeps all three neighbour ids");
        Check(Bridge.LoadStep(Path.Combine(args[0],"curvedExit.stp")),"load curved exit fixture");
        int curvedTop=-1,curvedBottom=-1;
        for (int id=0;;++id)
        {
            var probe=Bridge.GetFaceNormal(id); if (probe==null) break;
            if (probe[2]>0.9 && Math.Abs(probe[5]-30)<1e-6) curvedTop=id;
            if (probe[2]<-0.9 && Math.Abs(probe[5])<1e-6) curvedBottom=id;
        }
        Check(curvedTop>=0 && curvedBottom>=0,"curved fixture exposes both cylinder disks");
        var curvedSides=Bridge.GetBoundaryExitSides(new[]{curvedTop},0.05);
        Check(curvedSides!=null && curvedSides.Length==1,
            "cylinder top boundary yields one curved side candidate");
        var curved=curvedSides?[0];
        Check(curved?.Points!=null && curved.Points.Length>=6 && curved.EdgeReferences?.Length==1,
            "curved run keeps one original circular edge reference");
        double curvedRadius=0; bool curvedPlanar=true;
        if (curved?.Points!=null) for (int i=0;i+2<curved.Points.Length;i+=3)
        {
            curvedRadius=Math.Max(curvedRadius,Math.Sqrt(curved.Points[i]*curved.Points[i]+curved.Points[i+1]*curved.Points[i+1]));
            curvedPlanar&=Math.Abs(curved.Points[i+2]-30)<=1e-3;
        }
        Check(Math.Abs(curvedRadius-20)<=0.1 && curvedPlanar,
            "curved candidate samples stay on the top circle");
        var curvedContext=Bridge.CreateWorkingContext(0,0,1,1,0,0);
        var curvedRef=curved?.EdgeReferences?[0];
        Check(curvedContext!=null && curvedRef!=null,"curved edge working context created");
        if (curvedContext!=null && curvedRef!=null)
        {
            bool curvedHits=true,curvedTangents=true,radiusExact=true;
            for (int k=0;k<8;k++)
            {
                double angle=Math.PI*2*(k+0.5)/8;
                var world=new Vec3(20*Math.Cos(angle),20*Math.Sin(angle),30);
                var hit=EvalEdge(curvedContext,curvedRef,world);
                if (hit==null || hit.Status!=ExitEdgeQueryStatus.Hit || hit.Point==null)
                { curvedHits=false; continue; }
                var point=P(hit.Point);
                curvedHits&=(point-world).Length<=1e-6;
                radiusExact&=Math.Abs(Math.Sqrt(point.X*point.X+point.Y*point.Y)-20)<=1e-6;
                curvedTangents&=hit.Tangent!=null && Math.Abs(P(hit.Tangent).Length-1)<=1e-6 &&
                    Math.Abs(point.X*hit.Tangent[0]+point.Y*hit.Tangent[1])<=1e-6;
            }
            Check(curvedHits,"curved edge evaluation hits the exact circle at every angle");
            Check(radiusExact,"curved edge evaluation keeps the analytic radius");
            Check(curvedTangents,"curved edge evaluation returns a tangent perpendicular to the radius");
            var datum=Frame(curvedContext).Project(new Vec3(20,0,30));
            var tight=Bridge.EvaluateExitEdge(curvedContext.Id,curvedContext.Revision,
                curvedRef.Revision,curvedRef.EdgeKey,datum.X,datum.Y,0.0001);
            Check(tight.Status==ExitEdgeQueryStatus.Hit,
                "closed curved edge evaluation succeeds with a tight tolerance");
            Check(tight.Status!=ExitEdgeQueryStatus.Ambiguous,
                "closed curved edge is not misclassified as ambiguous at a unique point");
            Bridge.ReleaseWorkingContext(curvedContext.Id);
        }
        Check(!Bridge.LoadStep(Path.Combine(args[0],"absent.stp")),"failed model load");
        Check(Bridge.QueryWorkingSurface(afterStageReload.Id,afterStageReload.Revision,0,0).Status==WorkingQueryStatus.StaleContext,"failed reload also invalidates context");
        Check(Bridge.CreateWorkingContext(0,0,1,1,0,0)==null,"no context available without model");
        // 기본 예제: 사각형 선택이 화면 배율/창 크기와 무관하게 모든 모델 면을 반환하는지 확인한다.
        var examplePath=Path.GetFullPath(Path.Combine(args[0],"../../resource/Mold_Cover_Trim.step"));
        Check(File.Exists(examplePath),"default example STEP exists");
        if (File.Exists(examplePath))
        {
            Check(Bridge.LoadStep(examplePath),"load default example model");
            Bridge.SetViewFrame(1,0,0,0,1,0);
            int modelFaceCount=0;
            for (int id=0;;++id) { if (Bridge.GetFaceNormal(id)==null) break; ++modelFaceCount; }
            var wholeView=Bridge.SelectFacesInRectangle(0,0,899,699);
            Check(modelFaceCount==80,"default example exposes 80 faces");
            Check(wholeView!=null && wholeView.Length==modelFaceCount,
                $"whole-view rectangle selects every face ({wholeView?.Length}/{modelFaceCount})");
            // 작업면 생성은 형상을 다시 mesh/Redisplay하므로 선택 구조도 갱신돼야 한다.
            var exampleContext=Bridge.CreateWorkingContext(1,0,0,0,1,0);
            Check(exampleContext!=null,"default example creates a working context");
            if (exampleContext!=null)
            {
                var afterRemesh=Bridge.SelectFacesInRectangle(0,0,899,699);
                Check(afterRemesh!=null && afterRemesh.Length==modelFaceCount,
                    $"remesh keeps rectangle selection complete ({afterRemesh?.Length}/{modelFaceCount})");
                // 전체 80면 확정: 원본 위상이 완결이면 재봉합 없이 닫힌 단일 루프와
                // 원본 edge 참조를 반환해야 한다. 임시 병합은 원본에 없는
                // 다중 공유 Edge를 만들어 이 모델을 비다양체로 오판했다.
                var allFaces=new int[modelFaceCount];
                for(int i=0;i<modelFaceCount;++i) allFaces[i]=i;
                RegionBoundaryLoop[] regionLoops=null;
                string regionFailure=null;
                try { regionLoops=Bridge.GetSelectedRegionBoundary(allFaces,0.05,out regionFailure); }
                catch(Exception ex) { regionFailure=ex.GetType().FullName+" | "+ex.Message+" | "+ex.StackTrace; }
                Check(regionLoops!=null && regionLoops.Length==1,
                    $"default example 80-face region yields one loop ({regionLoops?.Length??0}) {regionFailure}");
                if(regionLoops!=null && regionLoops.Length==1)
                {
                    Check(regionLoops[0].Closed,"default example 80-face region loop is closed");
                    Check(regionLoops[0].Points!=null && regionLoops[0].Points.Length>=9,
                        "default example 80-face region has a non-degenerate boundary");
                    Check(regionLoops[0].EdgeReferences!=null && regionLoops[0].EdgeReferences.Length>0,
                        "default example 80-face region retains original edge references");
                    // 인출면 후보는 인출점 선택의 필수 조건이 아니다. 전체 선택에는
                    // 비선택 인접면이 없으므로 후보가 비는 것이 정상이다.
                    var noExitSides=Bridge.GetBoundaryExitSides(allFaces,0.05,1,0,0);
                    Check(noExitSides==null || noExitSides.Length==0,
                        "full-model selection has no separate exit-face candidate");
                    // 후보 조회가 영역 경계 결과를 바꾸지 않는다(참조 스테이징 격리).
                    var againLoops=Bridge.GetSelectedRegionBoundary(allFaces,0.05,out string againFailure);
                    Check(againLoops!=null && againLoops.Length==1,
                        $"exit-candidate lookup leaves the 80-face region intact ({againLoops?.Length??0}) {againFailure}");
                }
                // STEP 내보내기: 모델과 발열사 솔리드를 하나의 파일로 쓰고, 별도 리더로
                // 다시 읽어 파트 수와 경계를 검증한다. 경로는 배치와 무관한 형상 검증용이다.
                string stepPath=Path.Combine(Path.GetTempPath(),
                    "heatingmold_step_export_"+Guid.NewGuid().ToString("N")+".step");
                try
                {
                    var straightWire=new StepExportArea("테스트 A","테스트 A",255,0,0,
                        new[]{new StepExportPiece("테스트 A 발열경로",
                            new double[]{0,0,0, 15,0.4,0, 30,0.6,0, 45,0.4,0, 60,0,0})});
                    var turnedWire=new StepExportArea("테스트 B","테스트 B",0,128,255,
                        new[]{new StepExportPiece("테스트 B 발열경로",
                            new double[]{0,0,10, 12,0.3,10.2, 24,0.5,10.6, 36,0.3,10.8, 48,0,11})});
                    bool exported=Bridge.ExportStepWithRoutes(stepPath,"Mold_Cover_Trim",0.30,
                        new[]{straightWire,turnedWire},out string stepFailure);
                    Check(exported,"STEP export writes model plus two wire parts ("+stepFailure+")");
                    Check(File.Exists(stepPath)&&new FileInfo(stepPath).Length>0,
                        "STEP export file is non-empty");
                    // 조각 단위 API: 조각마다 중심선 1개 + 발열사 솔리드 1개를 만들고
                    // 다시 읽어 컴포넌트 수/형상을 검증한다.
                    string piecePath=Path.Combine(Path.GetTempPath(),
                        "heatingmold_step_piece_"+Guid.NewGuid().ToString("N")+".step");
                    try
                    {
                        var lanePiece=new StepExportPiece("테스트 A 레인 1",
                            new double[]{0,0,0, 15,0.4,0, 30,0.6,0, 45,0.4,0, 60,0,0});
                        var turnPiece=new StepExportPiece("테스트 A 턴 1",
                            new double[]{60,0,0, 62,0.2,1, 64,0.3,2, 66,0.2,3, 68,0,4});
                        var pieceArea=new StepExportArea("테스트 A","테스트 A",255,0,0,
                            new[]{lanePiece,turnPiece});
                        bool pieceExported=Bridge.ExportStepWithRoutes(piecePath,"Mold_Cover_Trim",0.30,
                            new[]{pieceArea},out string pieceFailure);
                        Check(pieceExported,
                            "STEP piece export writes one solid per piece ("+pieceFailure+")");
                        Check(File.Exists(piecePath)&&new FileInfo(piecePath).Length>0,
                            "STEP piece export file is non-empty");
                    }
                    finally { try { if(File.Exists(piecePath)) File.Delete(piecePath); } catch { } }
                    // 발열영역 색상: 작업 프레임 2D 링으로 모델 사본 표면을 구획하고
                    // 면별 STEP 색을 기록한다. 영역 밖 링은 전체 실패로 거부되어야 한다.
                    string areaPath=Path.Combine(Path.GetTempPath(),
                        "heatingmold_step_area_"+Guid.NewGuid().ToString("N")+".step");
                    try
                    {
                        double[] areaFrame=exampleContext.Frame;
                        double minU=double.MaxValue,minV=double.MaxValue;
                        double maxU=double.MinValue,maxV=double.MinValue;
                        double[] mesh=exampleContext.Triangles;
                        for(int i=0;i+2<mesh.Length;i+=3)
                        {
                            double dx=mesh[i]-areaFrame[0];
                            double dy=mesh[i+1]-areaFrame[1];
                            double dz=mesh[i+2]-areaFrame[2];
                            double u=dx*areaFrame[3]+dy*areaFrame[4]+dz*areaFrame[5];
                            double v=dx*areaFrame[6]+dy*areaFrame[7]+dz*areaFrame[8];
                            if(u<minU) minU=u;
                            if(u>maxU) maxU=u;
                            if(v<minV) minV=v;
                            if(v>maxV) maxV=v;
                        }
                        Check(maxU>minU && maxV>minV,"working mesh projects to a non-degenerate area");
                        double pad=1.0;
                        double[] areaFrameValues={
                            areaFrame[0],areaFrame[1],areaFrame[2],
                            areaFrame[3],areaFrame[4],areaFrame[5],
                            areaFrame[6],areaFrame[7],areaFrame[8]};
                        var areaWire=new StepExportPiece("영역 A 레인 1",
                            new double[]{0,0,0, 15,0.4,0, 30,0.6,0, 45,0.4,0, 60,0,0});
                        var coveredArea=new StepExportArea("영역 A","영역 A",255,0,0,
                            new[]{areaWire},allFaces,areaFrameValues,
                            new double[]{
                                minU-pad,minV-pad, maxU+pad,minV-pad,
                                maxU+pad,maxV+pad, minU-pad,maxV+pad});
                        bool areaExported=Bridge.ExportStepWithRoutes(areaPath,"Mold_Cover_Trim",0.30,
                            new[]{coveredArea},out string areaFailure);
                        Check(areaExported,
                            "STEP area export paints the selected surface ("+areaFailure+")");
                        Check(File.Exists(areaPath)&&new FileInfo(areaPath).Length>0,
                            "STEP area export file is non-empty");
                        var outsideArea=new StepExportArea("영역 B","영역 B",0,128,255,
                            new[]{areaWire},allFaces,areaFrameValues,
                            new double[]{
                                maxU+50,minV-pad, maxU+60,minV-pad,
                                maxU+60,maxV+pad, maxU+50,maxV+pad});
                        bool outsideExported=Bridge.ExportStepWithRoutes(areaPath,"Mold_Cover_Trim",0.30,
                            new[]{outsideArea},out string outsideFailure);
                        Check(!outsideExported&&outsideFailure=="area-nofaces:0",
                            "STEP area export rejects a region that covers no face ("+outsideFailure+")");
                    }
                    finally { try { if(File.Exists(areaPath)) File.Delete(areaPath); } catch { } }
                    // 한글 경로: 관리→네이티브 경로 변환이 ACP가 아니라 UTF-8이어야
                    // OCCT가 파일을 만들 수 있다(사용자 기본 파일명이 한글이다).
                    string koreanDir=Path.Combine(Path.GetTempPath(),"발열몰드 경로 검증");
                    Directory.CreateDirectory(koreanDir);
                    string koreanStepPath=Path.Combine(koreanDir,"발열경로_"+Guid.NewGuid().ToString("N")+".step");
                    bool exportedKorean=Bridge.ExportStepWithRoutes(koreanStepPath,"Mold_Cover_Trim",0.30,
                        new[]{straightWire},out string koreanFailure);
                    Check(exportedKorean,"STEP export writes to a Korean output path ("+koreanFailure+")");
                    Check(File.Exists(koreanStepPath)&&new FileInfo(koreanStepPath).Length>0,
                        "STEP export to a Korean path is non-empty");
                    try { if(File.Exists(koreanStepPath)) File.Delete(koreanStepPath); } catch { }
                    try { if(Directory.GetFileSystemEntries(koreanDir).Length==0) Directory.Delete(koreanDir); } catch { }
                    bool rejectedDiameter=Bridge.ExportStepWithRoutes(stepPath,"Mold_Cover_Trim",0.0,
                        new[]{straightWire},out string diameterFailure);
                    Check(!rejectedDiameter&&diameterFailure=="invalid-diameter",
                        "STEP export rejects a non-positive wire diameter ("+diameterFailure+")");
                    var degenerate=new StepExportArea("테스트 C","테스트 C",0,0,0,
                        new[]{new StepExportPiece("퇴화 경로", new double[]{1,2,3, 1,2,3})});
                    bool rejectedWire=Bridge.ExportStepWithRoutes(stepPath,"Mold_Cover_Trim",0.30,
                        new[]{degenerate},out string wireFailure);
                    Check(!rejectedWire&&wireFailure!=null&&wireFailure.StartsWith("piece-build:"),
                        "STEP export rejects a degenerate wire path ("+wireFailure+")");
                }
                finally
                {
                    try { if(File.Exists(stepPath)) File.Delete(stepPath); } catch { }
                }
                Bridge.ReleaseWorkingContext(exampleContext.Id);
            }
        }
        ProjectLoadChecks.Run(window,args[0]);
        // STEP 열기도 같은 UTF-8 경로 규약을 쓴다. 한글 경로 사본으로 확인한다.
        if (File.Exists(examplePath))
        {
            string koreanModelDir=Path.Combine(Path.GetTempPath(),"발열몰드 경로 검증");
            string koreanModelPath=Path.Combine(koreanModelDir,"한글모델.step");
            try
            {
                Directory.CreateDirectory(koreanModelDir);
                File.Copy(examplePath,koreanModelPath,true);
                Check(Bridge.LoadStep(koreanModelPath),"load STEP from a Korean path");
                int koreanFaces=0;
                for (int id=0;;++id) { if (Bridge.GetFaceNormal(id)==null) break; ++koreanFaces; }
                Check(koreanFaces==80,$"Korean-path STEP load exposes 80 faces ({koreanFaces})");
            }
            finally
            {
                try { if(File.Exists(koreanModelPath)) File.Delete(koreanModelPath); } catch { }
                try { if(Directory.GetFileSystemEntries(koreanModelDir).Length==0) Directory.Delete(koreanModelDir); } catch { }
            }
        }
        window.Close();
        Console.WriteLine($"{count} working-side checks passed.");
    }
}
