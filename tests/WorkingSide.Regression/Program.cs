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
    [STAThread]
    static void Main(string[] args)
    {
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
        var datumProjection=Bridge.StageProjectedWorkingPolyline(front.Id,front.Revision,projectionStage,
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
        Check(Bridge.SetConnectorEnabled(900,true,.2,.8,.3),"discarding failed candidate stage preserves prior selectable connector");
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
        Check(Bridge.SetConnectorEnabled(releaseRouteId,true,.2,.8,.3),"wrong-owner discard leaves context A staged route intact");
        Bridge.ReleaseWorkingContext(releaseA.Id);
        Check(!Bridge.SetConnectorEnabled(releaseRouteId,true,.2,.8,.3),"releasing context A removes its staged route AIS/ID");
        var reusedRoute=Bridge.DisplayWorkingRoutePiece(releaseB.Id,releaseB.Revision,releaseRouteId,"TurnConnector",new[]{6.0,0,20,7,0,20});
        bool reusedLine=Bridge.StageWorkingPolyline(releaseB.Id,releaseB.Revision,releaseLineId,new[]{6.0,1,20,7,1,20});
        Check(reusedRoute!=null && reusedLine &&
              Bridge.CommitWorkingRouteGeometry(releaseB.Id,releaseB.Revision,new[]{releaseRouteId},new[]{901},new[]{releaseLineId}) &&
              Bridge.SetConnectorEnabled(901,true,.2,.8,.3),
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
              Bridge.SetConnectorEnabled(candidateB,true,.2,.8,.3),
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
        var retainedRegion=Field<List<List<Vec2>>>(window,"_selectedRegion"); retainedRegion.Add(new(){new(0,0),new(1,0),new(0,1)});
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
        Check(typeof(Bridge).GetMethod("HlrProject")==null && typeof(Bridge).GetMethod("HlrEdges")!=null,
            "HLR smoke API removed while exporter HlrEdges remains");
        viewport.RectangleSelectionMode=true;
        Check(viewport.RectangleSelectionMode,"rectangle interaction remains independent of removed diagnostic mode");
        Check(Bridge.ReturnToWorkingFront(front.Id,front.Revision),"active working front is independent of camera state");
        Check(Bridge.LoadStep(Path.Combine(args[0],"ring.stp")),"load ring fixture");
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
        var acrossSeams=Bridge.StageProjectedWorkingPolyline(panels.Id,panels.Revision,seamStage,
            new[]{.25, .5, 5.0, 2.75, .5, 5.0},new[]{faceA,faceB,faceC},0,0,-1);
        Check(acrossSeams!=null,"projected route reconnects across selected three-panel seams");
        Bridge.DiscardWorkingRouteGeometry(panels.Id,panels.Revision,Array.Empty<int>(),new[]{seamStage});
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
            coincidentCenter[0]-4,coincidentCenter[1]-4,coincidentCenter[0]+4,coincidentCenter[1]+4,false);
        Check(coincidentRectangle!=null && coincidentRectangle.Contains(frontFace) && !coincidentRectangle.Contains(backFace),
            "rectangle pick skips the coincident back-facing face");
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
        Check(!Bridge.LoadStep(Path.Combine(args[0],"absent.stp")),"failed model load");
        Check(Bridge.QueryWorkingSurface(afterStageReload.Id,afterStageReload.Revision,0,0).Status==WorkingQueryStatus.StaleContext,"failed reload also invalidates context");
        Check(Bridge.CreateWorkingContext(0,0,1,1,0,0)==null,"no context available without model");
        window.Close();
        Console.WriteLine($"{count} working-side checks passed.");
    }
}
