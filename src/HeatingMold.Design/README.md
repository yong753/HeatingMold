# HeatingMold.Design

`HeatingMold.Design` owns deterministic design rules that operate on Core models. It does not load files, call WPF, or invoke OCCT directly. Native measurements enter through `IGeometryQueries`; a design decision that cannot be confirmed from the supplied measurement returns a validation issue instead of an assumed success.

## Main flows

| Area | Entry point | Responsibility |
|---|---|---|
| Region | `RegionBuilder`, `RegionAdjustmentService` | Build a valid region from selected Face topology and apply include/exclude boundaries without bridging holes or disconnected components. |
| Heating path | `ConstrainedHeatingPathPlanner` | Enumerate lane counts and endpoint orientations from the requested clear gap, wire diameter, direction, anchors, and exit side. |
| Surface mapping | `SurfacePathMapper`, `HeatingPathMaterializer` | Map candidate samples through first-hit queries, preserve Face transitions, and create stable Core path and control-point IDs. |
| Editing | `ControlPointEditService`, `ManualEditPreserver` | Apply edits to candidate paths and decide whether existing manual changes still correspond to a regenerated path. |
| Circuit topology | `CircuitPathTopologyService`, `CircuitIdentityMapper` | Split or merge path ownership while preserving the selected circuit identities and their independent supplies. |
| Connection path | `ConnectionPathPlanner` | Validate the requested exit, external endpoint, material roles, and obstacle-query result for each connection. |
| Validation | `DesignValidator` | Check inputs and references, region boundaries, continuity, same-side constraints, insertion depth, and minimum clear gap in a fixed order. |
| Applied area | `AppliedAreaAllocator` | Assign measured trimmed surface elements to one nearest active circuit, preserve unassigned area, refine boundary or area-error cases, and return convergence evidence with the final surface elements. |
| Quality | `CenterPitchQualityAnalyzer` | Report sampled surface-to-centerline distance, adjacent-lane chord spacing, target-pitch deviation, locations, and approximation bounds without a thermal pass judgment. |

## Measurement and result rules

The placement input is a clear outer-surface gap `g` and conductor diameter `d`; center pitch is derived as `g + d`. Design does not reduce either input to force a candidate to fit. Path generation returns one connected heating path or a reasoned failure.

Clearance is decided from `ClearanceMeasurement`, including its error bound and completeness. `HeatingPathCurveAdjacencyResolver` derives local endpoint pairs only from neighboring active segments that share the same stable control point, and Design passes those stable IDs with the clearance query. Infrastructure maps them to request indices and both Native and managed line/polyline paths exclude only the declared previous-end to next-start contact. Unclassified cross-curve endpoint contacts, collinear overlap, nonlocal crossings, curved shared-endpoint cases, repeated-turn arcs, and every B-spline input remain blocking or unresolved until their exact rules are certified. A complete sample set alone does not turn those cases into a pass.

Applied-area values come from `SurfaceElementSet` and retain its measured total area, approximation settings, error bound, and conservation residual. The new `surface-area-v3` rule compares every vertex, edge midpoint, and center, then certifies the whole planar triangle when exact line, arc, or polyline circuit distances have a margin greater than twice the triangle's maximum center-to-vertex distance plus the tie tolerance. Uncertified elements are refined and remain unassigned when the refinement limit is reached. Saved `surface-area-v2` projects keep sampled vertex, edge-midpoint, and center behavior; `surface-area-v1` keeps vertex-and-center behavior. The result describes geometric applied area only; it does not estimate temperature or heating performance.

Quality reports use every surface-element vertex and centroid to measure distance to the nearest active heating-run curve sample. Their distance bound includes curve deviation and half the curve sample spacing, plus surface-mesh deviation and the largest triangle edge. Adjacent lane pairs report sampled 3D chord min/average/max, the midpoint of the largest observed spacing, target-pitch deviation, and a curve-approximation bound. The default report keeps the total probe count and at most 64 farthest surface locations, so a large tessellation does not create an unbounded session result. These values describe sampled geometry; they do not certify continuous coverage or thermal performance. When area allocation already produced the final surface elements, Application reuses those elements for quality analysis.

## Verification

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Verify.ps1 -Group Design -Configuration Release -Platform x64
```

Design tests use fake geometry queries for deterministic placement, validation, topology, and area rules. The area fixtures cover circuit assignment, an edge-midpoint boundary missed by vertex and center probes, legacy rule-version behavior, boundary refinement, measured-area error refinement, stable `CircuitId` tie-breaking, stale geometry rejection, and a 9,600 mm² effective-area conservation case. They do not replace native validation of curved clearances, trimmed surface area, or the real mold model.

## Remaining geometry work

`DES-09` remains partial until native curve-distance support confirms arcs and B-splines and representative native turn/short-loop fixtures pass. Managed regression covers a shared corner, collinear backtracking, and a nonlocal self-intersection for exact line/polyline geometry. `DES-10` still needs native cylinder/curved-face convergence fixtures, real mold allocation checks, and end-to-end verification that exclusions and circuit boundaries preserve the measured area. App now displays the sampled spacing, surface-distance location, and approximation limits; review of native curved geometry remains open.
