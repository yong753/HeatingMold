# Working-side geometry checkpoint (milestones 1–2)

## Contract and implementation

- `WorkingSideDefinition` is immutable and reuses `ProjectionFrame`. It owns a typed context ID
  and model revision; its ray direction is always `-Frame.Normal`. It has no camera dependency.
- `WorkingSideSession` retains a transactional candidate internally. The UI exposes one active side:
  model load applies +Z after successful shape setup, and axis changes apply immediately. Failed
  changes retain the previous side and dependent state; model loading invalidates prior contexts.
- Internal presentation savepoints retain the prior native presentation and camera while a candidate
  side is prepared. Preparation failures restore that savepoint before releasing the candidate;
  after publication, failures are reported as post-commit and never restore a savepoint that may refer
  to a released context. No preview, confirmation, diagnostic picking, or boundary display is exposed.
- Native `CreateWorkingContext(N, X)` constructs orthogonal X/Y/N axes and places the datum beyond
  the **entire model's conservative AABB support** along N, plus an outside margin. This is not a
  selected-face datum. Contexts cache a full-shape BRep intersector and a whole-model triangulation.
- `QueryWorkingSurface(id, revision, u, v)` casts from that datum along -N, returning the nearest
  forward trimmed-BRep hit, actual face ID, context/revision, datum UV, and a typed status:
  `Hit`, `Miss`, `Tangent`, `InvalidInput`, `StaleContext`, or `GeometryFailure`.
  A nearer tangent is not skipped in favor of a rear hit. Sphere poles use the analytic sphere
  normal; other undefined parameterization normals fail rather than claiming a usable surface.
- The app offers model +/-X/Y/Z direct application and return to the active orthographic front.
  Orbit, pan, zoom, and camera projection never change the active frame.
- Whole-model triangles (absolute mesh target deflection 0.05 mm, angular target 0.1 rad) are
  projected and unioned with the existing Clipper2 wrapper. Input triangles are filled independently;
  resulting rings are **not** passed back through the input-CCW normalization. Clipping uses
  6-decimal coordinates (the existing legacy default remains 2 decimals). Explicit nesting
  distinguishes outer rings, holes, and nested islands. This removes shared seams and includes
  curved silhouettes. No full-model sewing is performed during hovering or diagnostic picking.
- Boundary geometry utilities remain available internally for geometry processing; no boundary
  diagnostic or picking workflow is exposed by the app.

## Intentional safety gates

Pattern generation now maps each complete candidate over the confirmed datum using full-model
first-hit queries and bounded adaptive sampling (maximum 0.1 mm planar leaf spacing, 0.05 mm
chord tolerance, with endpoint/quarter/midpoint probes).
Face identity is retained on final path-ordered samples, so continuity checks permit valid A→B→C
transitions even when A and C do not share an edge. Shared-edge adjacency and a 2 mm maximum 3D
sample step are required. A miss, unselected first hit, non-adjacent transition, or unresolved
discontinuity rejects the candidate before the previous result is cleared. AIS route pieces and
exclusion polylines are staged under private IDs before swapping; staging failures discard only
candidate objects. Temporary route and polyline AIS handles are context/revision-owned; releasing
their context removes their presentations and frees staging IDs. Route-piece output retains stable
IDs, connector styling, picking and circuit hover. Catastrophic AIS/display failure during the native
commit itself is not proven rollback-safe.

This migration is depth-zero only; positive insertion depth is explicitly rejected. Power-location
setup and `ProjectPowerLead` remain gated because their outside-destination and external-continuation
semantics are not migrated. Mapping or staging failures preserve prior geometry and managed route
state; commit-level graphics-system failures remain a documented rollback limitation.

## Limits / not claimed complete

- Mesh deflection is a mesher target, **not a certified Hausdorff or topology bound**. Tiny features
  can be lost; there is no exact HLR/BRep silhouette curve representation yet.
- Route mapping is sampled, not an exact interval proof: the 0.1 mm maximum planar leaf is probed
  at quarter intervals (at most 0.025 mm between probes). Features narrower than that, or positioned
  wholly between probe locations, are not guaranteed to be detected. Full-model first-hit checks
  prevent accepting an observed miss/occluder, but cannot prove absence of an unsampled narrow blocker.
- Refinement uses a local +/-0.2 mm bracket with full-model ray occupancy. An absent inside/outside
  bracket, tangent/undefined surface, or hidden candidate fails explicitly. Very thin features,
  complex corners, extreme scales, or poor imported geometry can be unpickable rather than falling
  back to a hidden/back surface. An accepted bracket is not proof of global silhouette topology.
- `WorkingBoundarySample.BracketWidth` is the **2D local numeric bracket**, not a 3D silhouette
  distance/error bound; near tangency a very small 2D error can have a much larger depth error.
- No whole-face “working surface” coloring/filter is advertised: a partly occluded face is not
  fully usable. Usability is pointwise, via the new nearest-hit query. Dense visible-surface overlays
  and production-scale performance acceleration are not part of this checkpoint.
- Construction is synchronous and contexts are single-document, UI-thread-owned. Contexts are
  handle/revision checked but not a multi-document or thread-safe geometry service.
- Only model-axis side choices are exposed. Current-camera-direction capture is not implemented.
- Offscreen automated tests verify representative geometry/UI contracts, not actual viewer appearance
  or high-DPI/multi-monitor interaction. These require manual verification.

## Verification and manual checklist

- Core unit suite: `dotnet test tests/HeatingMold.Core.Tests/HeatingMold.Core.Tests.csproj -c Release --no-restore`.
- Native and app build plus new integration suite: `& tests/WorkingSide.Regression/Run.ps1`.
- Verified results at this checkpoint: Core and native regression scripts exercise first-hit,
  continuity/staging safety and UI route gates. Working-side checks include direct-side no-op,
  failure rollback, dependency invalidation, and absence of diagnostic click interception. The power suite asserts safe UI gating
  before/after confirmation while preserving direct native geometry and restored-state cleanup tests.
  Its old automatic endpoint-entry expectations are documented as future route-migration work, not
  silently re-enabled or treated as current functionality.
- Required normal MSBuild command compiles native successfully; copying updated DLLs to the running
  app output can fail with MSB3021/MSB3027. The script uses isolated output without stopping processes.
- Manual: open a real mold and verify automatic +Z; switch each axis and confirm immediate view/state
  application; verify same-axis no-op, failed-change rollback, camera-independent active front,
  and reload reset. Verify face/route/connector interactions plus pan/zoom remain unchanged.
