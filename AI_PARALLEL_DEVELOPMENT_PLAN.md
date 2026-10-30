# AI 병렬 개발 계획 (2-Agent Parallel Plan)

- 작성일: 2026-10-29
- 상태: **보관용 / 현재 미사용** — 당장 적용하지 않음. 착수 시점에 최신 브랜치 상태로 재확인 필요.
- 기준 브랜치: `feature/phase1-curved-surface-routing` (로컬 `master`는 폐기, 향후 기준으로 사용하지 않음)
- 문서 성격: 작업 배분·충돌 방지 계획서. 실제 기능 구현·검증 결과 문서가 아님.

---

## 1. 전제

- 통합 기준은 `feature/phase1-curved-surface-routing` 단일 브랜치로 고정한다. 에이전트별로 서로 다른 기준 브랜치에서 작업한 뒤 병합하는 방식은 사용하지 않는다.
- 착수 시점의 기준 커밋과 아래 미커밋 작업을 먼저 확정한다.
  - 미커밋 수정: `src/HeatingMold.App/MainWindow.Areas.cs`, `tests/Phase1Curved.Repro/Program.cs`
  - 위 2개 파일은 진행 중인 곡면 seam gap 처리 작업이며, **아직 검증되지 않았다.** 소유자가 체크포인트 커밋으로 기록한 뒤 Agent A에 인계한다. 인계 전에는 어느 에이전트도 덮어쓰지 않는다.
- 에이전트는 별도 브랜치 + 별도 작업 디렉터리(워크트리)에서 작업한다.
- 빌드 산출물 경로가 공유되므로(`x64/Release/OccBridge.dll` 참조), 네이티브 빌드·전체 회귀는 순차 단일 환경에서 수행한다. 에이전트별 격리 빌드가 검증되기 전에는 병렬 실행하지 않는다. 무시된 `third_party` 의존성도 환경별로 확인한다.

## 2. 우선순위 (Phase 1 명세 FR 기준)

| 순위 | 범위 | 근거 (현재 브랜치 실태) |
|---|---|---|
| **P1** | 곡면 발열경로 견고화·검증 (FR-2.2 / 2.3 / 2.4 곡면) | 진행 중인 seam gap WIP 존재. 닫힌 곡면·축평행 게이트는 미검증, 폴백 경로는 보존·미검증 상태. |
| **P2** | 전기 조건·계산·판정·사양표 (FR-1.3 / 3.2 / 3.3 / 4.2), DXF에 경로·사양표 포함 (FR-4.3 중 DXF) | 저항/전류/전력 계산과 적용 가능 면적 산출이 현재 브랜치에 없음. DXF 안내문(`MainWindow.xaml` 392행)이 "경로와 사양표는 아직 포함되지 않습니다"라고 명시. |
| **P3** | STEP 출력(FR-4.3 3D), 배치 영역 지정(FR-2.5), PHASE 2 항목 | STEP writer는 네이티브 신규 작업이라 소유 충돌 위험이 큼. FR-2.5는 별도 합의 필요. 구멍 회피·전개·DWG 등은 PHASE 2. |

## 3. A/B 분담

| 구분 | **Agent A — 곡면 경로 견고화 (P1)** | **Agent B — 전기 계산·사양표·DXF (P2)** |
|---|---|---|
| 소유 파일 | `src/HeatingMold.App/MainWindow.Areas.cs` (WIP 인계 후), `src/HeatingMold.Core/Geometry/SurfacePathMapper.cs`, `tests/Phase1Curved.Repro/Program.cs` (WIP 인계 후), `tests/HeatingMold.Core.Tests/SurfacePathMapperTests.cs`, `docs/Phase1.CurvedSurfaceRouting.md` | 신규 `src/HeatingMold.Core/Electrical/…` (계산·판정·면적, 순수 C#), 신규 `tests/HeatingMold.Electrical.Tests/`, `src/HeatingMold.Export/DxfExporter.cs`, 신규 `tests/HeatingMold.Export.Tests/` |
| 작업 내용 | seam gap 처리 검증·완성(비단조 제3 면 출현, 전/후면, 갭 fail-closed), 닫힌 곡면·축평행 게이트 실검증, 리드 sag 보정 | FR-3.2 계산 코어(R=r×L, I=V/R, P=V²/R, 허용범위 판정), FR-3.3 면적 산출, 전기 사양표 DTO, DXF에 경로·사양표 반영, 재읽기 회귀 |
| 완료 기준 | 재현 케이스에서 측정 수치 문서화, 미검증 항목은 "미검증"으로 명시(폴백·게이트를 통과로 주장하지 않음), 평면 영역의 기존 BRepProj 경로 무변경 | 단위 테스트로 계산식·판정 경계 검증, DXF 레이어/폴리라인/치수/TEXT 재읽기 회귀, UI 미연결 상태 명시 |

## 4. 충돌 방지 규칙

1. **공용 금지 목록 (양쪽 모두 수정 금지)**: `MainWindow.xaml`, `MainWindow.xaml.cs`, `.sln`, 기존 `.csproj`, `Core/Geometry/*`(A 소유), 네이티브 소스. B가 신규 csproj를 만들어도 솔루션 등록은 통합 담당자가 한다.
2. **네이티브 단일 소유**: 이번 스프린트에서 `OccBridge` 수정은 A에 한정(필요 시 계약 기록 후). B는 관리형 전용. STEP 출력(P3)은 별도 순차 단계에서 소유권을 넘겨받아 진행한다.
3. **WIP 인계**: 미커밋 2개 파일은 소유자 체크포인트 커밋 후 A에 인계. B는 해당 파일에 접근하지 않는다.
4. **병합 순서**: P1(A) → 통합 검사 → P2(B). B가 필요로 하는 입력(회로별 길이, 선재 제원)은 DTO 계약으로만 정의하고, App UI 연결은 통합 담당자가 A 병합 후 수행한다.
5. **인계 산출물**: 각 에이전트는 변경 파일 목록, 기준 SHA, 계약 변경 여부, 재현 입력, **실제로 수행한** 검사 결과를 남긴다. 파일 교집합뿐 아니라 공용 DTO·공차·Face ID 의미 변경 여부도 확인한다.
6. **환경 격리**: 빌드 출력 경로를 에이전트별로 분리하거나, 분리 검증 전까지 네이티브 포함 검증을 순차 수행한다. 공유 `x64/Release`에 동시 빌드 금지.

## 5. 착수 전 체크리스트

- [ ] 미커밋 WIP 2개 파일 체크포인트 커밋 (소유자)
- [ ] 기준 SHA 고정 및 문서에 기록
- [ ] 에이전트별 브랜치·워크트리 생성
- [ ] `third_party`·OCCT DLL 경로, `CSF_OCCTResourcePath` 등 실행 환경 확인
- [ ] 기존 `HeatingMold.Mvp.sln` 및 회귀 스크립트가 참조하는 네이티브 DLL 경로 재확인
- [ ] 통합 담당자(단일 창구) 지정

## 6. 미해결·추후 결정 사항

- 전기 계산 계수(선재 Ω/m, 허용 전류·전력)는 발주처 제공 자료 기준. 확정 전까지 프리셋으로 두고 교체 지점을 코드에 명시.
- STEP 출력 방식(네이티브 writer 위치, 인터페이스)은 P3 착수 시 별도 계약.
- FR-2.5 배치 영역 지정 방식은 PHASE 2 협의 문서의 결정 필요 항목과 연동.
- 구멍 회피, 평면 전개, DWG 출력, UV 메트릭 라우팅, 다중 면 스티칭, 주기 시임, 닫힌 영역, 축평행 원통/원뿔 지원은 PHASE 2 범위.
- 관리형 테스트 통과를 네이티브/실제 STEP 모델 검증 통과로 대체하지 않는다.

## 7. 참고 문서

- `docs/Phase1.CurvedSurfaceRouting.md` — 현재 곡면 라우팅 구현·검증 기록(기준 커밋 f4a5444)
- `docs/PHASE 1 기능 구현 명세서.txt` — 기능 요구 FR-1.1 ~ FR-4.3
- `docs/PHASE 2 협의 필요사항.txt` — PHASE 2 범위 및 합의 필요 항목
- `docs/WorkingSide.Checkpoint.md` — 단일 문서/UI 스레드 소유 및 전원 설정 게이트
- `tests/PowerLead.Regression/README.md` — 전원 리드 관련 현재 체크포인트
