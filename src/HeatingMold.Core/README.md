# HeatingMold.Core

Core owns the shared design vocabulary and framework-independent value types used by every product module. The authoritative Korean terminology and definitions are in [the terminology guide](../../docs/implementation/2026-10-20/TERMINOLOGY.md); shared contracts are in [00.Shared.Contracts.md](../../docs/implementation/2026-10-20/00.Shared.Contracts.md).

Use these names for new design code:

| Concept | Canonical code name | Korean term |
|---|---|---|
| The generated heating-wire design path | `HeatingPath` | 발열사 경로 |
| An ordered geometric segment of that path | `HeatingPathSegment` | 경로 구간 |
| A straight heating section on a lane | `HeatingRunSegment` | 발열 레인 구간 |
| A user-selected circuit endpoint location | `ConnectionPoint` | 연결부 위치 |
| The actual geometry from a path endpoint to that location | `ConnectionPath` | 연결 경로 |
| Model-fixed origin and X/Y/N axes | `WorkingCoordinateSystem` | 작업 좌표계 |
| Wire insertion distance measured from mold surface to wire outside | `InsertionDepthMm` | 삽입 깊이 |
| Minimum outside-to-outside wire clearance | `MinimumClearGapMm` | 최소 외곽 간격 |
| Derived centerline spacing | `CenterPitchMm` | 중심 피치 |
| Area associated with the heating region or a circuit (planned) | `AppliedArea` | 적용면적 |

`PatternParameters` currently names center pitch and boundary margin; the MVP does not implement minimum clear gap or applied-area calculation. `ProjectionFrame` is the low-level coordinate transform contained by the working coordinate system. Do not introduce `Route`, `Connector`, `Terminal`, or `LeadRoute` as new domain names. Keep historic regression harness paths intact, but use these canonical names in new code and APIs.

There is no single behavior module that currently uses every agreed term. Core is the shared vocabulary and value-type owner; behavior is split by responsibility:

| Terms | Current code location | Status |
|---|---|---|
| `HeatingPath`, `HeatingPathSegment`, `HeatingRunSegment`, `TurnSegment`, `FixedJoinSegment`, `PatternParameters` | `HeatingMold.Core` | Used by the current pattern generator and circuit partitioner |
| `WorkingCoordinateSystem` | Core value types, App, OccBridge | Confirmed coordinate system is active; some older checkpoints keep the historical `WorkingSide` name |
| `ConnectionPoint`, `ConnectionPath` | App and OccBridge API | Names are established, but connection-point setup remains behind the migration gate |
| `InsertionDepthMm` | App input and OccBridge geometry API | Term is established; current UI still accepts only zero |
| `CenterPitchMm`, `BoundaryMarginMm` | Core `PatternParameters` and App | Active MVP inputs |
| `MinimumClearGapMm`, `AppliedArea` | Glossary/shared-contract target | Not implemented as calculation behavior yet |

Core does not own WPF state, native handles, file formats, generation algorithms, electrical calculations, or presentation objects. Domain concepts shared across modules belong here; behavior belongs to the module that owns that behavior.
