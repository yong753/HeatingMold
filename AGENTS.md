# HeatingMold project instructions

## 응답 언어 (사용자 확정)

- 사용자에게 제공하는 설명, 구현계획, 작업 결과는 한국어로 작성한다.

## Coordinate and geometry contract

- Design decisions (generation, editing, splitting, boundary/exclusion tests, pitch and margin) use the **confirmed working plane's 2D coordinates**. Camera coordinates and surface UV are not design coordinates.
- A constrained CAD boundary is the original edge projected onto that working plane. Preserve curves as curves; a display tessellation or endpoint chord is not the authoritative constraint.
- Preserve original edge identity, valid parameter interval, orientation, model revision, working-context identity/revision, and the selected original parameter. A CAD parameter is not a sample index or a linear interpolation ratio.
- Separate unbound boundary search, projection onto an already-bound edge, and direct evaluation of a saved parameter. Editing must not silently switch to a neighbouring edge.
- Evaluate a boundary point's 3D position on the original edge at the same parameter selected in 2D. Unprojecting a datum produces a point on the working plane, **not** a point on the model surface.
- General route points still use the existing selected-face/global surface mapping policy. Original topology matching, surface validation, adaptive sampling and actual 3D lengths remain necessary. This is not a ban on all 3D geometry operations.
- Sampling is allowed for display, hit testing and explicitly error-controlled numerical calculations. It must not replace original CAD identity or silently relax exact boundary constraints.
- Keep cursor snap distances separate from geometry validation tolerances. Missing references, stale contexts, degenerate projections, ambiguous solutions, tangent/overlapping generation intersections must be explicit failures; never hide them by increasing tolerance or falling back to a sampled boundary.
- Preserve candidate identity/binding through conversions and UI selection. Equal 2D coordinates do not prove equal original edges or equal 3D positions.

## 제외영역 허용 범위 (사용자 확정)

- 작업영역 내부에 구멍을 만드는 제외영역, 적용영역을 둘 이상으로 분리하는 제외영역은 허용하지 않는다.
- 제외 후에도 구멍 없는 단일 연결 영역이 유지되는 경우(외곽을 깎는 제외)만 허용한다.
- 제외영역 추가/변경은 이 조건을 먼저 검증하고, 위반 시 기존 제외영역·경로·선택점을 보존한 채 명시적으로 거부한다.
- 유효 외곽선은 마진 적용 전 `적용영역 − 제외영역`이다. 제외로 새로 생긴 직선 구간은 원본 CAD edge/parameter로 가장하지 않고 별도 artificial provenance로 보존하며, 3D 평가는 선택 면 표면 매핑을 사용한다.

## Transactions and preservation

- Prepare immutable candidate circuits, IDs, lengths, mapping, fingerprints, endpoint arrays and connector states before committing.
- Validate and stage **all changed pieces**, then perform one native commit. Unchanged areas retain their committed circuits/mapping/native objects; never rebuild them from draft inputs.
- Include boundary binding, geometry, context and validation policy in cache keys. A cache hit must not bypass changed endpoint constraints.
- Separate native retain/replace/delete, including an empty final result. Do not bypass route transactions with a global overlay clear.
- Before commit, any failure preserves existing paths, lengths, IDs, endpoint bindings, edits and other areas. After commit, publish prepared managed references before presentation updates; redraw failure is not geometry rollback.
- Route presentation targets (piece-color IDs/RGB and endpoint markers) are prepared from the committing final area set before the single native commit and applied from that snapshot afterwards. Never re-read the mutable area list at that point: a deletion has already removed the dropped area's native piece IDs, so recoloring from the pre-delete list fails the native all-or-nothing check and raises a spurious warning. Validate that color targets are exactly retained ∪ final, with no duplicates and no staged/deleted IDs. A post-commit presentation failure keeps the committed geometry and must remain visible in the caller's completion status instead of being overwritten.
- Actual circuit length/electrical calculations use the validated 3D route once. Dashed guides contribute no heating length.

## Scope and verification

- Do not introduce automatic detours, new self-intersection/pitch/bend-radius checks, Undo/Redo, power-connection changes or expanded DXF output without explicit approval.
- Preserve the existing supported-face/region gates and general-edit policy. Do not replace all polygon Boolean/offset operations with CAD Boolean operations.
- Keep legacy APIs unless actual caller/compatibility analysis justifies removal. Historical phase documents are not the current geometry contract.
- Add regressions for inclined/curved edges, sewing subranges, identical projection at different depths, endpoints/periodicity, ambiguity/staleness, fixed-edge dragging, same-FACE multi-area independence, narrow exclusions, and cross-area draft/commit isolation.
- Native builds must match the App reference at `x64/Release/OccBridge.dll`; record the actual loaded DLL when diagnosing native regressions.
- Run focused tests per change and a review/correction/retest cycle for significant changes. Automated scenario completion is not recorded visual or manual-drag verification.
- Never claim the user's failing CAD model is fixed unless that model was reproduced and tested; report unverified coverage explicitly.

## Sample STEP model (verified 2026-11-10)

- `resource\Mold_Solids.stp` removes the surface bodies from the product structure only: the deleted entity is the single `SHAPE_REPRESENTATION_RELATIONSHIP` (`#23`) that linked `MANIFOLD_SURFACE_SHAPE_REPRESENTATION` `#13`. The definitions of `SURFACE.135` (86 faces) and `SURFACE.142` (4 faces) remain in the file as unreferenced data, so external CAD that reads representations directly can still display them. The app follows the product structure and loads 30 solids / 574 faces.
- `tools\step-transform\bake-axes.ps1` applies the load-time `supplemental geometry` axis rotation to every `CARTESIAN_POINT` and `DIRECTION` value (rotation about the STEP origin, no translation). Rotating the supplemental placement as well makes the app's load-time rotation identity (verified `max |R' − I| = 4e-17`, zero point-wise error), so a baked copy opened in CAD uses the same coordinates the app displays.
- A CAD re-export of a baked copy normally drops the `supplemental geometry` representation; the app then falls back to principal inertia axes, so a reimported edited model needs the frame pinned again rather than assumed.
- `resource\Mold_Solids_aligned_surface.stp` re-adds the deleted relationship (`#23`, 129 bytes) to the baked copy, so OCCT loads 30 solids + 2 surface shells (664 faces) while keeping the supplemental frame.
- `tools\step-cleanup\diagnostics\thicken.cpp` thickens a free shell. On `SURFACE.135`, `BRepOffsetAPI_MakeThickSolid::MakeThickSolidByJoin` and `BRepOffset_MakeOffset` fail in every join/self-intersection/mode combination, and `PerformBySimple` only returns a shell; only `BRepOffsetAPI_MakeThickSolid::MakeThickSolidBySimple` produces a solid. `resource\Mold_Solids_surface135_thick3.stp` is that shell at 3 mm toward the mold solids: 1 solid / 206 faces, volume 4,306,377.6 mm³, area 2,869,973.6 mm² (3.01 mm). The side is chosen by sampling candidate faces against the mold solids: +normal 0.134 vs −normal 0.073 inside fraction.
- The thickened body carries 18 degenerate zero-length edges (total 0.096 mm) inherited from the source patch, so a STEP round-trip reports one face `NotClosed`. They are not visible gaps; do not claim the body is watertight without re-checking these edges.

## Default example model `Mold_Cover_Trim.step` (user-confirmed 2026-11-11)

- The user designated `resource\Mold_Cover_Trim.step` as the default example model. The file on disk is `.step`, not `.stp`. SHA-256 `8CB03405817606D1917FEF55B46A60011FFB5E93075EECDEF5A8651D4359FCED`.
- Verified structure: one open shell, 80 faces, 0 solids, area 958,841.060 mm², BRepCheck valid, one free-boundary loop of 60 edges. It is the Mold part of `Mold_Item_trim100.step` (identical bbox, area and face count); the trim used the item's YZ outline offset by 100 mm with square corners, so the X extents are unchanged.
- **User-confirmed: the heating face is +X.** The example's working normal is (+1,0,0), not the currently hardcoded +Z. The file has no `supplemental geometry`, so the app aligns it by principal axes; in that loaded frame the broad faces face ±X, and a +Z working plane projects them nearly edge-on.
- Implemented 2026-11-11: `LoadFile` applies the working normal (0,0,1) by default; `ExampleModelPresets` replaces it with (+1,0,0) when the file hashes to `Mold_Cover_Trim.step`, so '예제 열기' and 'STEP 열기' use identical coordinates. The existing in-plane X rule picks (0,1,0) for a ±X normal, which gives plane Y = (0,0,1) (screen up = +Z). The default example lookup (`OcctEnv.FindMoldStp`) and the csproj content copy now target `Mold_Cover_Trim.step`; `Mold_Solids.stp` remains as a regression fixture.
- The visual-test scenario loader rejects `model.solidCount <= 0` (`VisualScenario.Load`), so it cannot yet target this 0-solid model. Change that gate and author the new-model scenario together; do not fake a solid count.
- The native working origin is the projected bbox center, positioned 1 % (minimum 1 mm) in front of the model along the normal; model coordinates are never translated. Record the actual expected origin after the first instrumented load instead of assuming it.
- Pending measurements for this example (do not guess): the face set that yields a single closed region on the open shell, the start/end exit points, and the expected circuit piece/turn counts for the scenario.
- Thickening test (2026-11-11, `tools\step-cleanup\diagnostics\thicken.exe`): a 3 mm offset makes one solid / 212 faces / 2,880,990 mm³ in memory, but the STEP re-read is BRepCheck **INVALID** with 19 free edges and vertex/edge tolerance 6.000 mm. A 1 mm offset re-reads valid (212 faces, 960,222 mm³) but still has 18 free edges (0.053 mm total, one 17-edge open chain and a degenerate point). Both written variants are the outward offset, which covers the original +X face with a new offset surface; the inward variant completes in memory but the tool writes only the first candidate. Do not call this model a closed solid until a STEP round-trip passes with zero free edges and normal tolerances. Known trouble spots: the non-planar 60-edge rim and the pinch near raw coordinates (-4341.6, -591.9, 965.4); `ShapeAnalysis_Shell` reports `oriented=no` on the source shell.
- A thickened solid also changes the app's import alignment from surface principal axes to volume principal axes, so its loaded frame, origin and face IDs must be re-measured rather than inherited from the open shell.

## Build configuration (verified)

- `HeatingMold.Mvp.sln` declares exactly one solution configuration, `Release|x64`. Do not re-add Debug/Any CPU/x86 rows without verifying every project exposes them.
- The right-hand side of each solution→project mapping must be a configuration the project actually exposes:
  - `src\OccBridge\OccBridge.vcxproj` defines only `Release|x64` → map to `Release|x64`.
  - `src\HeatingMold.App\HeatingMold.App.csproj` and `tests\HeatingMold.App.Tests\HeatingMold.App.Tests.csproj` declare `<Platforms>x64</Platforms>` → map to `Release|x64`.
  - `src\HeatingMold.Core`, `src\HeatingMold.Export`, `tests\HeatingMold.Core.Tests` declare no `<Platforms>` → map to `Release|Any CPU`.
- A mapping to a configuration the project does not expose produces VS's "unknown project configuration mapping" dialog; `dotnet build`/`msbuild` on the solution can still succeed, so only reopening in VS validates this.

## Project persistence (save + load implemented)

- The product project file is one UTF-8 JSON document that references the external original STEP by relative path and SHA-256; CAD/BREP geometry is not embedded.
- Save uses a same-directory temporary file, flush, and atomic replace. A save failure must preserve the current work and the previous JSON file; never overwrite the source STEP.
- Project load verifies the JSON format/schema, the source STEP length and SHA-256, the import policy version, and the saved topology identity before restoring. It may ask the user to reselect the original STEP but never substitutes a different hash.
- Load prepares an isolated native STEP candidate, remaps saved Face/Edge signatures to fresh runtime IDs, commits the candidate once, restores the exact saved working-plane frame, and rebuilds areas, boundaries, committed 3D route chains, lengths, endpoint provenance, electrical inputs, and active area.
- Committed routes are staged with their saved support-face provenance and published with one native route commit. Missing support-face provenance is an explicit load failure; do not approximate it from nearest surfaces.
- Load rebuilds the effective heating-area boundary from the saved exclusion rectangles (applied region minus exclusions, before generation margin). With no separate exit face (`ExitFaceId < 0`) the exit boundary uses that same effective outline; a saved exit-face boundary is kept separately. Saved selected, pattern, and circuit exit points are then evaluated against the restored boundary using their saved CAD/artificial provenance, and the committed route mapping is captured only afterwards. The route boundary follows the saved committed context's recorded mode (an explicit saved flag, with legacy inference from its selected exits), not whatever draft selections exist at load time. An unsupported exclusion or invalid saved endpoint is an explicit load failure; never silently clear the point or fall back to the pre-exclusion outline.
- Failure before the native candidate commit preserves the current document. A failure after that commit cannot restore the previous native model; the load reports the failure and clears the incomplete restored document explicitly.
- Do not persist native pointers, temporary stage IDs, AIS/presentation objects, caches, hover/preview state, or runtime-only FACE/Edge IDs as durable identity.

## Topology identity (implemented for load)

- Saved FACE/Edge runtime IDs are not durable. Projects store per-Face and per-Edge topology signatures and remap them to fresh runtime IDs after reimport; raw IDs are diagnostic provenance only.
- An edge signature includes its adjacent Face signatures. Face/edge signatures must be nonempty, unique, and byte-stable across reimport of the same STEP, hash, and import policy. Missing, duplicate, ambiguous, or changed signatures are explicit load-time failures.
- Every exit-edge copy must preserve `EdgeSignature`; never reconstruct a saved binding by nearest XY or nearest-edge matching.
- The exact saved working frame and per-point route support-face provenance are persisted and restored; raw runtime IDs are rebased.

## Circuit generation contract

- A circuit is one continuous serpentine path chunk made of multiple lanes joined by connectors. One straight lane fragment, or one area split by an exclusion, is not automatically a separate circuit.
- Use one sequential generation rule whether or not exclusion regions exist. An exclusion removes heating lanes only; connectors between lanes and across same-lane cuts may pass through the margin-expanded exclusion region. Never split a circuit, detour, or reject generation merely because a connector crosses an exclusion.
- Heating runs must stay outside the margin-expanded exclusion region. A lead added to reach a selected exit is a connector, not a restored heating run.
- Never reverse only the last lane to face the selected exit. Keep the established pitch and natural serpentine alternation. If the natural last heating run ends on the near side of the selected end, connect it directly; if it ends on the far side, generate exactly one further lane at the same pitch following normal progression, then connect back to the exact selected end. That finishing lane may pass the selected end's sweep coordinate. Any heating interval at that exact row is used even when short; only a row with no heating interval at all is an explicit generation failure that preserves the existing result. Final heating runs stay axis-parallel; the terminal lead is a separate real conductor connector, never an endpoint replacement or a retagged heating run.
- Do not reintroduce fixed generation-count caps into the product generation path: lane count, split/fragment count, total interval count, the 20-node exact-search limit, the 4000-fragment fallback, or a 100000-lane loop bound. End lane generation from the actual sweep range and spacing. Numerical exhaustion or resource failure must be explicit; never return a partial route as success.
- A generated area is exactly one continuous circuit covering every route piece in its original order. Preserve the single `Pattern` + `Circuits`/`CircuitPieceIndices` structure, original CAD endpoint binding, surface mapping, actual 3D length, and atomic commit policies. Do not reintroduce a turn-enabled split/merge state.
- Multiple confirmed areas may reference the same CAD Face. Their effective heating regions (`applied region − exclusions`, before generation margin) are a user input precondition: they must not overlap. Do not add automatic overlap rejection, region allocation, detours, or partial-region drafting tools.
- Connector exclusion penetration is not a reason to reclassify a conductor as a dashed guide or to change its length semantics.
- The legacy compatibility APIs `GenerateComponents`, `ComputeCandidatesComponents`, and `GenerateAnchoredComponents` still use the exact DP/candidate path. Their limits are not the product policy for the selected-exit generation path; do not copy those limits into new generation code.

## DXF export scope (user-confirmed)

- Product DXF output is exactly **one plan view on the confirmed working plane**, plus the confirmed heating routes and the electrical specification table. The previous four orthographic views are not part of the product output.
- Mold outline and heating routes must use the same confirmed working-plane origin and X/Y axes. Do not renormalize DXF coordinates to a min-corner origin.
- On-screen heating route overlays and plan-view heating-route entities use the owning area's opaque color, the same color as the selected-area outline. This is display/export color only and never changes geometry, lengths, IDs, or endpoint binding.
- Export heating geometry from the committed native route chains (ordered 3D points preserved at staging/commit), not from regeneration, remapping, or display tessellation. Exclude staged, missing, stale, disabled and preview geometry explicitly.
- The plan-view length dimensions and the electrical table must use the committed 3D route lengths. Table rows must match the exported circuit IDs one-to-one in order.
- Plan-view dimension text height is 20 mm. The vertical dimension line sits on the left of the mold with its extension lines extending right; the horizontal dimension line stays below the mold.
- The plan view draws the effective heating-area boundary on the `HEAT_AREA` layer: the applied region minus exclusions, with no generation margin. Each boundary uses its area's viewport color as a true-color entity; the layer color is only a neutral fallback. Multiple or degenerate rings, exclusions that leave a hole or split the area, and working-plane mismatches are explicit export failures.
- Degenerate or missing projections, duplicate/missing circuit piece indices, and context/model-revision mismatches are explicit export failures that preserve the previous file state.

## Verification environment (verified)

- Build `src\OccBridge\OccBridge.vcxproj` with Visual Studio `MSBuild.exe`, not `dotnet build`; the C++ project requires the Visual C++ targets.
- Run native/App tests with the OCCT `win64\vc14\bin` and bundled third-party DLL directories on `PATH`. `tests\WorkingSide.Regression\Run.ps1` sets this up and builds into isolated output, avoiding locks from a running app.
- The WorkingSide regression harness invokes private UI methods by name with fixed parameter counts. Keep existing parameterless entry points when adding overloads that share precomputed state.
- Hand-built regression routes must map their 2D endpoints to 3D through `Bridge.MapWorkingPointOntoFaces`; working-frame coordinates are not world model coordinates, and staging raw frame u/v as world points fails.

## STEP trim tool `tools\step-trim` (verified 2026-11-11)

- Independent console tool (`step-trim.cpp` + `build.ps1` + `run.ps1` + `Test.ps1`); it does not reference or modify the app, the solution, or `src\OccBridge`. The source STEP is read-only; output defaults to a separate file.
- `--mode inspect` reports structure, names, colors, assembly paths, tolerances, free boundaries and axis-aligned projection surplus without writing anything. `--mode trim` requires explicit `--origin`/`--normal`; it never guesses the working plane. The 100 mm margin is planar distance on that plane only.
- Verified on `resource\Mold_Item.step` (Mold = open shell 87 faces, Item = closed solid 85 faces): projected outline area 392,451.928 mm², one wire; 100 mm offset area 713,959.155 mm²; squared-corner boundary 722,473.233 mm² with boundary distance 99.999997–141.421356 mm (141.421 = 100·√2 at right-angle corners); trimmed Mold 80 faces / 958,841.060 mm² (removed 470,155.479); contact area before = after = 48,442.074 mm²; BRepCheck valid, one connected component, one shell. Output `resource\Mold_Item_trim100.step` written 2026-11-11; the source hash is unchanged.
- **Corners are square, not arcs (user-confirmed 2026-11-11).** `BRepOffsetAPI_MakeOffset` only produces corner arcs, so after the offset each corner arc is replaced by the intersection of the two adjacent straight lines and the boundary is rebuilt. Only corners whose both neighbours are lines are squared; others are reported with the reason and keep the arc. On this model 4 of 6 candidates were squared; the two skipped ones are the narrow groove near local (256.8, -431) whose neighbours are line/circle and circle/circle. Never silently keep a corner arc when the user asked for square corners.
- `HLRBRep_HLRToShape::CompoundOfEdges(type, visible, In3d=false)` returns 2D edges without usable 3D curves and crashed the plane splitter; `In3d=true` returns the original 3D curves (measured 211 mm off the working plane), so project them with `BRepProj_Projection` before use. Only the `OutLine` type plus planar silhouette edges (adjacent-face normal sign test) may be used as splitter tools; feeding all sharp/smooth/sewn/outline curves corrupts the arrangement (unsplit original face plus overlapping cells).
- `BRepOffsetAPI_MakeOffset` with `SetApprox(false)` fails on this outline in every join/distance combination; only `SetApprox(true)` + `GeomAbs_Arc` on a face input succeeds. Measured deviation is 99.999997–100.000003 mm over 297 samples, so always re-measure and report the deviation instead of assuming exactness.
- STEP round trip of this file changes the Item by about 11.58 mm³ volume and −0.42 mm² area, so the tool compares the written Item against a baseline round trip of the unmodified document, not against the in-memory value.
- `BRepAlgoAPI_Common` may re-approximate original faces: 78 of 80 Mold faces reuse the original surfaces; the two rebuilt Extrusion faces stay within 0.021040 / 0.016298 mm of the original Mold.
- Unconfirmed by the user: whether 100 mm is planar distance or distance along the surface, and which working plane is authoritative. The committed example run uses origin `(-4183.68, -843.20, 1396)`, normal `(1,0,0)`, X axis `(0,1,0)` chosen only because the YZ projection is the only axis with more than 100 mm surplus in all four directions.
