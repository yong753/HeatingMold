# HeatingMold project instructions

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

## Transactions and preservation

- Prepare immutable candidate circuits, IDs, lengths, mapping, fingerprints, endpoint arrays and connector states before committing.
- Validate and stage **all changed pieces**, then perform one native commit. Unchanged areas retain their committed circuits/mapping/native objects; never rebuild them from draft inputs.
- Include boundary binding, geometry, context and validation policy in cache keys. A cache hit must not bypass changed endpoint constraints.
- Separate native retain/replace/delete, including an empty final result. Do not bypass route transactions with a global overlay clear.
- Before commit, any failure preserves existing paths, lengths, IDs, endpoint bindings, edits and other areas. After commit, publish prepared managed references before presentation updates; redraw failure is not geometry rollback.
- Actual circuit length/electrical calculations use the validated 3D route once. Dashed guides contribute no heating length.

## Scope and verification

- Do not introduce automatic detours, new self-intersection/pitch/bend-radius checks, Undo/Redo, power-connection changes or expanded DXF output without explicit approval.
- Preserve the existing supported-face/region gates and general-edit policy. Do not replace all polygon Boolean/offset operations with CAD Boolean operations.
- Keep legacy APIs unless actual caller/compatibility analysis justifies removal. Historical phase documents are not the current geometry contract.
- Add regressions for inclined/curved edges, sewing subranges, identical projection at different depths, endpoints/periodicity, ambiguity/staleness, fixed-edge dragging, split/reconnect preservation, narrow exclusions, and cross-area draft/commit isolation.
- Native builds must match the App reference at `x64/Release/OccBridge.dll`; record the actual loaded DLL when diagnosing native regressions.
- Run focused tests per change and a review/correction/retest cycle for significant changes. Automated scenario completion is not recorded visual or manual-drag verification.
- Never claim the user's failing CAD model is fixed unless that model was reproduced and tested; report unverified coverage explicitly.

## Circuit generation contract

- A circuit is one continuous serpentine path chunk made of multiple lanes joined by connectors. One straight lane fragment, or one area split by an exclusion, is not automatically a separate circuit.
- Use one sequential generation rule whether or not exclusion regions exist. An exclusion removes heating lanes only; connectors between lanes and across same-lane cuts may pass through the margin-expanded exclusion region. Never split a circuit, detour, or reject generation merely because a connector crosses an exclusion.
- Heating runs must stay outside the margin-expanded exclusion region. A lead added to reach a selected exit is a connector, not a restored heating run.
- Do not reintroduce fixed generation-count caps into the product generation path: lane count, split/fragment count, total interval count, the 20-node exact-search limit, the 4000-fragment fallback, or a 100000-lane loop bound. End lane generation from the actual sweep range and spacing. Numerical exhaustion or resource failure must be explicit; never return a partial route as success.
- Preserve the single `Pattern` route-piece + `TurnEnabled` + `Circuits`/`CircuitPieceIndices` structure, manual split/reconnect, original CAD endpoint binding, surface mapping, actual 3D length, and atomic commit policies.
- Connector exclusion penetration is not a reason to reclassify a conductor as a dashed guide or to change its length semantics.
- The legacy compatibility APIs `GenerateComponents`, `ComputeCandidatesComponents`, and `GenerateAnchoredComponents` still use the exact DP/candidate path. Their limits are not the product policy for the selected-exit generation path; do not copy those limits into new generation code.
