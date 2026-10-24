# HeatingMold.Infrastructure

Infrastructure implements file and export ports used by Application. It is the boundary for ZIP/JSON packages, captured STEP source bytes, DXF generation, and managed OCCT adapters.

## Current implementation

- `Persistence/ProjectPackageStore` writes and reads a versioned `.hmproj` ZIP package containing `manifest.json`, `project.json`, and the original bytes at `source/model.step`. `SchemaV1` DTOs and an explicit mapper keep the JSON contract independent from Core runtime types. Required fields, unknown fields, enum strings, and curve-specific payload shapes are validated. The manifest records file type, package/schema versions, units, paths, SHA-256 hashes, and producer metadata. The writer reopens and validates its temporary package before replacing the destination.
- `Persistence/StepModelSourceStore` captures source bytes with an injected units provider, importer version, and fingerprint version. It does not depend on a file path after capture.
- `Geometry/Occt/RuntimeIdMap` keeps stable design IDs separate across committed, staging, and preview geometry. `FaceReferenceResolver` verifies the source hash and face kind, area, center, boundary, and adjacency features before using an import-index hint or a unique fallback match.
- `Geometry/Occt/FaceReferenceFeatureBuilder` canonicalizes closed 3D boundary samples independent of loop start and direction, represents closed surfaces without boundary wires explicitly, and attaches neighboring boundary fingerprints. `ModelIdentity.FaceFingerprintVersion` persists its `occ-face-boundary-samples-v1` identifier in `.hmproj`; the restore map builder checks that the stored and supplied identities match the algorithm used to build current features, and rejects unsupported versions before comparing faces.
- `Geometry/Occt/CurveDefinitionMapper` transfers line, polyline, arc, and rational B-spline definitions through an immutable wire DTO without reducing them to display samples. The App adapter copies this DTO into the native bridge contract, and OCCT stages the resulting exact source wire before projection. Native compilation and runtime verification remain pending.
- `OcctGeometryQueries.MeasureClearance` uses exact OCCT edge distances when the active or prepared-candidate bridge supports them. Without that bridge, Infrastructure measures line/polyline pairs with the managed segment solver and approximates circular arcs with bounded chords. Arc chord deviation targets one sixty-fourth of the connection tolerance; per-arc and total sample budgets cap the work, and the combined approximation and numerical error must remain within the connection tolerance. Near-parallel segment pairs are accepted only when a feasible-point upper bound and an AABB lower bound enclose the distance within that tolerance. Design supplies stable adjacent-segment IDs; the adapter maps them to ordered request-index pairs so only a declared previous-end to next-start contact may be excluded. Unclassified endpoint contacts, declared curved joints, B-spline inputs, repeated-turn arcs, and inputs without a nonlocal pair remain unresolved. Native compilation and runtime verification remain pending.
- `Export/ExportSnapshotBuilder` validates the export snapshot before any writer runs. `DxfDesignExporter` projects effective region boundaries and measured 3D paths into the snapshot's working coordinate system, marks the output in millimeters, and writes an editable LINE/TEXT specification table with explicit unavailable values.
- `DxfDesignExporter` marks each external connection point and labels it with its circuit sequence and start/end role on `HEAT_CONNECTION_POINT`.
- `Export/OrthographicDxfExporter` writes the existing viewer HLR views through the `IOrthographicDxfExporter` port, including visible/hidden lines, heating polylines, and dimensions.
- `Export/StepDesignPayloadBuilder` groups exact curve definitions into circuit heating wires and connection-point wires, and preserves circuit traversal direction. Unassigned heating curves remain visible in a separate output group.
- `Export/StepDesignWriter` writes through an OCCT bridge into a sibling temporary file, verifies nonempty output, then replaces the destination and returns its hash. `CompositeDesignExporter` routes DXF and STEP output. The App composition root supplies an OCCT bridge adapter; the new Native writer has not yet been compiled or run here.

The Infrastructure project targets `net8.0-windows` and directly references Core and Application. The DXF writer uses the existing `netDxf` 2023.11.10 package. Geometry and Electrical calculations remain in their owning modules.

The current package stores the authoritative `DesignProject` and original STEP bytes. Derived geometry, allocation, and electrical caches are not persisted yet; restore must recompute and validate them before the project becomes active. STEP root names, output read-back validation, and CAD interoperability checks remain open.

## Verification

Run the isolated Infrastructure group with:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Verify.ps1 -Group Infrastructure
```

The tests cover empty and edited project round-trips, all four curve definitions, exact curve DTO round-trips and malformed-wire rejection, circuit and connection data, required/unknown/null field rejection, manifest metadata, cancellation preserving the previous package and STEP destination, tampered payload rejection, traversal and duplicate-entry rejection, source STEP capture and identity, an Application open/save flow through the real package store, committed/staging/preview runtime ID isolation, feature-based face matching and ambiguity rejection, editable DXF specification and connection-point output, both DXF export paths, the STEP payload grouping and atomic file handoff, and the no-backend fallback. Native geometry restoration, Native STEP execution, and opening/editing exports in third-party CAD software remain pending.
