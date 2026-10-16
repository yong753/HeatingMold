# Working-side geometry checkpoint (milestones 1–2)

## Contract and implementation

- `WorkingSideDefinition` is immutable and reuses `ProjectionFrame`. It owns a typed context ID
  and model revision; its ray direction is always `-Frame.Normal`. It has no camera dependency.
- `WorkingSideSession` keeps preview and confirmed definitions separate. Failed or cancelled
  preview does not replace confirmed geometry. Confirm checks the model revision before replacing
  state. Model loading invalidates both app state and every native context, including failed loads.
- Native revision-owned presentation savepoints retain boundary AIS objects, the diagnostic marker,
  and a copied camera. A candidate is published only after its presentation/front preparation succeeds.
  Failure restores the prior presentation, camera, logical preview/rings/sample, and pick mode before
  releasing the candidate context. If native rollback is impossible, the app fails closed to an
  unconfirmed state. Savepoints are released on cancel/confirmation/reset and invalidated on reload.
- Preview temporarily hides the confirmed marker and suspends diagnostic picking; it does not discard
  the confirmed sample. Cancel restores the original marker/sample, pick mode and pre-preview camera,
  including after replacement previews. Only confirmation discards the previous confirmed diagnostic.
  Pick-mode entry requires a live confirmed native context and no outstanding preview, validated
  before disabling any other interaction modes. Rejected entry/clicks cannot erase the saved sample.
- Native `CreateWorkingContext(N, X)` constructs orthogonal X/Y/N axes and places the datum beyond
  the **entire model's conservative AABB support** along N, plus an outside margin. This is not a
  selected-face datum. Contexts cache a full-shape BRep intersector and a whole-model triangulation.
- `QueryWorkingSurface(id, revision, u, v)` casts from that datum along -N, returning the nearest
  forward trimmed-BRep hit, actual face ID, context/revision, datum UV, and a typed status:
  `Hit`, `Miss`, `Tangent`, `InvalidInput`, `StaleContext`, or `GeometryFailure`.
  A nearer tangent is not skipped in favor of a rear hit. Sphere poles use the analytic sphere
  normal; other undefined parameterization normals fail rather than claiming a usable surface.
- The app offers model +/-X/Y/Z preview, flip, confirm, cancel preview, and return to the confirmed
  orthographic front. Orbit, pan, zoom, and camera projection never change the confirmed frame.
- Whole-model triangles (absolute mesh target deflection 0.05 mm, angular target 0.1 rad) are
  projected and unioned with the existing Clipper2 wrapper. Input triangles are filled independently;
  resulting rings are **not** passed back through the input-CCW normalization. Clipping uses
  6-decimal coordinates (the existing legacy default remains 2 decimals). Explicit nesting
  distinguishes outer rings, holes, and nested islands. This removes shared seams and includes
  curved silhouettes. No full-model sewing is performed during hovering or diagnostic picking.
- Both the separate 2D panel and 3D datum wires draw outer candidates orange and holes blue. The 3D wire is the
  projection on the outside datum, **not a claim that these are surface-space exact CAD edges**.
- Dedicated outer-boundary diagnostic picking works without selected heating faces or a pattern.
  Only depth-zero exterior rings are eligible; holes, seams, and islands inside holes are excluded.
  A local BRep occupancy bracket refines a projected mesh candidate. Screen distance uses floating
  camera NDC projection and a 10-DIP aperture converted to physical pixels. Actual full-model BRep
  visibility is checked along the camera ray at the candidate point, not at the unsnapped cursor.
  Orthographic and perspective camera rays are handled separately; behind-eye points are rejected.
  Only trimmed-face `IN`/`ON` intersections participate in visibility ordering, matching surface queries;
  supporting-surface extensions outside a trim or through a hole are not blockers.
  An accepted point is displayed red and reported with UV/XYZ/face ID and local bracket width.

## Intentional safety gates

Pattern generation and old power-location setup are blocked, including internal generation entry
and deferred power updates. The existing pattern and `ProjectPowerLead` algorithms remain in place;
they have **not** been migrated or reinterpreted. Selected-face projection cannot establish nearest
whole-model visibility or topological continuity. The old power method's outside-destination and
external-continuation semantics are incompatible with the new boundary route and are not called
by this diagnostic flow. The legacy region frame now comes from the confirmed working side rather
than silently selecting +Z, but this does not make the old route first-hit safe.

This checkpoint is depth-zero only. Existing insertion-depth translation is retained behind the
gate; positive-depth semantics remain unresolved. Do not remove the gate merely because a side
has been confirmed. Route projection, continuity, power boundary routing, and depth semantics
are later work.

## Limits / not claimed complete

- Mesh deflection is a mesher target, **not a certified Hausdorff or topology bound**. Tiny features
  can be lost; there is no exact HLR/BRep silhouette curve representation yet.
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
- Verified results: 33 Core tests, 91 working-side integration checks and 72 power-lead regression
  checks passed. Working-side tests inject post-display/post-camera failures to verify rollback from
  empty, confirmed and existing-preview states. The legacy power suite now asserts safe UI gating
  before/after confirmation while preserving direct native geometry and restored-state cleanup tests.
  Its old automatic endpoint-entry expectations are documented as future route-migration work, not
  silently re-enabled or treated as current functionality.
- Required normal MSBuild command compiles native successfully; copying updated DLLs to the running
  app output can fail with MSB3021/MSB3027. The script uses isolated output without stopping processes.
- Manual: open a real mold; preview/confirm each required axis; inspect outside-datum wire vs 2D
  outer/hole colors; orbit without altering N; return to working front; select visible exterior
  boundary and verify red marker; reject holes/internal seams/hidden points; zoom/pan/repeat at
  100/150/200% DPI; flip/replace preview then cancel and verify the old marker/mode/camera return;
  confirm a new side and verify the old diagnostic clears; reload and ensure old diagnostic state clears.
