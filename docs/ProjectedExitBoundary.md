# Projected CAD exit boundary implementation

The design source is the original CAD edge projected onto the confirmed working
plane. Boundary points retain original parameters; their 3D positions are direct
evaluations, not nearest-point searches on the original spatial curve.

## Execution checklist

- [x] Preserve previous uncommitted circuit work in checkpoint `7791c65`.
- [x] Add project-local geometry/preservation instructions.
- [ ] Original-edge subranges and parameter-preserving 2D native queries.
- [ ] Common Core query contract, bound candidates and bound circuit endpoints.
- [ ] Candidate-isolated App integration, fixed-edge editing and validated caches.
- [ ] Native retain/replace/delete and commit/publish/presentation separation.
- [ ] Replace uniform region probes with intersection-partitioned 2D intervals.
- [ ] Focused and full regressions, review, corrections and documentation.

The actual CAD model from the reported exception has not been supplied. Synthetic
and repository-model regressions do not establish that exact model's recovery.

The old `Paths` projection branch remains compatibility-only until reachability
is demonstrated. The existing 2D polygon Boolean/offset algorithms, general
surface mapping, electrical calculations and DXF output scope are preserved.
