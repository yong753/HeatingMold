# 시각 테스트 시나리오 형식

작성일: 2026-11-06 (2026-11-12 대표 종합 시나리오·새 행동 추가 반영)
대상 코드: `src/HeatingMold.App/VisualTesting/`, `src/HeatingMold.App/MainWindow.VisualTest.cs`
실행 스크립트: `tests/HeatingMold.VisualTest/Run.ps1`

> 저장소에는 전체 기능을 한 번에 수행하는 대표 종합 시나리오
> `tests/HeatingMold.VisualTest/scenarios/e2e-full.json`이 포함되어 있습니다.
> (이전 `e2e-main.json`은 제외영역·꼭지점 편집·분할 후 STEP을 포함하지 않아
> 전체 기능 시나리오로 대체되었습니다.) 이 문서는 그 시나리오와 직접 작성하는
> JSON의 계약·행동 목록을 정의합니다.

---

## 1. 목적과 범위

실제 사용자가 앱에서 하는 행동을 순서대로 재현하고, 각 행동 직후의 중간 상태와
마지막 생성 결과를 함께 검사합니다. OS 마우스·키보드 자동화는 사용하지 않고,
앱의 실제 기능과 실제 네이티브 선택·표시 경로를 호출합니다.

자동 절차 완료(`Automation=Completed`)와 육안 합격(`VisualReview=NotRecorded`)은
별도로 기록합니다. 자동 완료는 화면의 육안 합격을 의미하지 않습니다.

시나리오는 독립된 앱 프로세스와 독립된 결과 폴더에서 실행합니다.

---

## 2. 시나리오 계약

### 2.1 스키마 버전

- `schemaVersion: 1`은 고정 단계 순서로 실행하고 시나리오는 그 단계의 입력값만 지정합니다.
  - 단계: 모델 로드 → 작업영역 선택·확정 → 인출면·시작/종료점 선택 → 발열경로 생성 →
    전기 사양 확인 → DXF 저장·재읽기
- `schemaVersion: 2`는 `areas`와 `steps`를 사용합니다.
- 그 밖의 `schemaVersion`은 실행 전에 거부됩니다.

### 2.2 공통 필드 (v1·v2)

- `model`: 저장소 기준 상대 경로, SHA-256, 면 수, 솔리드 수
- `area`: 선택 면 번호, 마진/간격(mm), 방향(0=자동)
- `exit`: 인출면 ID와 시작/종료 3D 좌표(후보 식별용, 기존 인출면 후보 경로)
- `exitPoint`: 작업평면 2D 좌표와 허용 오차로 영역 외곽을 직접 지정하는 현재 제품 경로
- `anchor`: 시작/종료점 후보 인덱스(기존 경로)
- `electrical`: 와이어 프리셋(0/1), 허용 전압·전류, 전체 발열전력(kW), 검토 범위, 기대 판정

모델 해시, 면 수, 후보 지문이 맞지 않거나 후보가 모호하면 다른 모델이나 다른 후보로
자동 대체하지 않고 실패합니다.

### 2.3 v2 규칙

- 영역은 화면 표시 이름이 아니라 테스트 식별자(alias)로 추적합니다.
- 영역은 면 집합·마진·간격·방향·인출면·앵커·최종 기대값을 선언합니다.
- 단계는 허용된 행동 이름만 사용합니다. 알 수 없는 행동·중복 alias·잘못된 참조는
  실행 전에 거부합니다.
- 좌표 기반 행동은 작업평면 좌표를 저장하되 실제 좌표 변환과 핸들러를 통과합니다.
- 현재 제품의 직접 인출점 선택은 `pickExitPoint`로 검증합니다. 작업평면 2D 좌표를
  `BeginExitPointPick` → 영역 경계 투영 → 3D 표면 평가 → 선택점 적용의 실제 제품
  경로에 넣고, 적용된 원본 CAD edge/parameter, artificial·split provenance를 기록합니다.
- `pickExit`·`anchor`는 기존 인출면 후보 경로의 호환 계약이며, 새 시나리오의 기본
  인출점 선택에는 사용하지 않습니다.
- `splitArea`는 `childAliases`로 분할 결과 영역을 추적합니다. 부모 alias는 제거되고
  자식 alias가 추가되며, 이후 `checkArea`·`activateArea`가 자식 alias를 참조합니다.

---

## 3. 행동 목록

기존 구현 행동:

`loadModel`(암묵), `selectFaces`, `toggleFaces`, `clearSelection`,
`expectSelection`, `confirmArea`, `activateArea`, `deleteArea`, `setAreaInputs`,
`expectAreaInputs`, `pickExit`, `cancelExitPick`, `generate`,
`checkArea`, `checkAllAreas`, `addExclusion`, `removeExclusion`, `expectNoPattern`,
`checkExclusion`, `moveVertex`, `exportDxf`

대표 종합 흐름용 추가 행동:

| 행동 | 입력 | 검사 |
|---|---|---|
| `pickExitPoint` | `exitPoint.role`(start/end), `uv`(작업평면 2D), `toleranceMm` | 실제 선택 모드 진입·경계 투영·3D 표면 평가·선택점 적용, CAD/artificial/split 연결 정보 기록 |
| `setElectrical` | `electrical`(preset 0/1, 허용 전압·전류, 전체 발열전력 kW) | 실제 UI 입력을 적용하고 제품이 읽은 한도가 시나리오와 같은지 검사 |
| `checkElectrical` | `electrical`(선택, `apply=false`면 입력을 바꾸지 않고 읽기만 함, `circuitVerdicts[{area,verdict}]`, `expectedWithinLimits`) | 독립 수식(R=rL, V=√(PR), I=√(P/R), 길이비 배분)으로 회로별 전력·저항·전압·전류·판정, 영역별 OK/NG, 합계, 검토 범위 전환 시 재배분 없음, 화면 행 수를 검사 |
| `splitArea` | `area`, `partCount`(2..4), `childAliases`, `confirm`, `confirmAction`, `outcome` | 실제 자동분할 제품 경로, 자식 2..4개·각 1회로·부모 선택 면 유지, 다른 영역 보존, 거부 시 부모와 경로·선택점 보존 |
| `exportStep` | `fileName`, `scope`, `outcome` | 실제 STEP 내보내기 공통 경로, 파일 생성·비어 있지 않음, 거부 시 기존 파일 보존 |
| `saveProject` | `fileName`, `outcome` | 실제 저장 공통 경로, 파일 생성, 저장 시점 JSON 체크포인트 보관 |
| `loadProject` | `fileName`, `compareProject`, `confirm`, `outcome` | 실제 프로젝트 열기 공통 경로, 영역 alias를 저장된 AreaId로 재결속, 복원 상태를 임시 저장한 JSON과 체크포인트 비교 |

`splitArea`의 확인 대화상자는 제품 코드가 `ResolveVisualTestConfirm("영역 분할")`을
호출하므로 시나리오에 `confirm: true`, `confirmAction: "영역 분할"`을 지정합니다.

`moveVertex` 등 확인 대화상자를 사용하는 행동의 응답은 테스트 실행 중에만 주입되고,
완료·실패·중단 시 큐가 비워져 일반 앱 동작에 남지 않습니다.

---

## 4. 파일 구성

| 파일 | 내용 |
|---|---|
| `src/HeatingMold.App/VisualTesting/VisualScenario.cs` | v1/v2 영역·단계 계약과 검증 |
| `src/HeatingMold.App/VisualTesting/VisualScenarioCompiler.cs` | v1/v2 → 실행 단계 목록, alias 참조 검증 |
| `src/HeatingMold.App/MainWindow.VisualTest.cs` | 행동 실행·중간 검사·최종 검사 |
| `src/HeatingMold.App/VisualTesting/VisualTestReport.cs` | 실행 기록과 실제 생성 DXF 파일 목록 |
| `src/HeatingMold.App/VisualTestLauncherWindow.xaml(.cs)` | 앱에서 새 프로세스로 실행하는 UI |
| `tests/HeatingMold.VisualTest/Run.ps1` | 빌드 후 앱을 실행하는 스크립트 |
| `tests/HeatingMold.VisualTest/scenarios/` | 시나리오 JSON. 전체 기능 흐름 `e2e-full.json` 포함 |
| `tests/HeatingMold.App.Tests/VisualTestingTests.cs` | 스키마 계약·옵션·리포트·러너 검증 |

---

## 4.1 대표 종합 시나리오 `e2e-full.json`

기본 예제(`Mold_Cover_Trim.step`, 80면/0솔리드, +X 작업면) 한 개로 다음을 한 번에
통과시키는 것이 목적입니다.

1. 세 면 선택·영역 확정
2. 작업평면 2D 좌표로 시작/종료 인출점 직접 선택
3. 프레임 X 방향 60조각/29턴/1회로 생성
4. 외곽 코너 제외영역 추가·침범 검사·경로 초기화·재생성·제외 초기화·재생성
5. 꼭지점 그룹 이동 확정과 편집 후 길이·상태 검사
6. 편집 상태 DXF·STEP·프로젝트 저장
7. 전기 입력 적용과 독립 수식 기반 회로별 판정 검사(NG)
8. 자동 3분할과 자식 영역 3개 확인, 전기 OK 재판정
9. 분할 후 DXF·STEP·프로젝트 저장
10. 같은 프로세스에서 프로젝트를 다시 열어 저장 시점 상태와 비교
11. 복원된 자식 영역의 기하·전기 재검사와 DXF·STEP 재출력

분할 후 STEP도 필수입니다. `BuildAreaColoredModelNative`는 선택 면을 영역 프리즘과
3D 공통 연산해 영역별 조각으로 교체하므로, 평면 절단이 곡면을 분리하지 못하던
`area-nofaces` 실패 없이 분할·복원 상태를 출력합니다.

---

## 5. 판정 원칙

- 생성 성공은 새 패턴·적용 조건·표시 상태가 함께 갱신됐을 때만 인정합니다.
- 예상 거부는 지정한 행동·사유·횟수와 일치할 때만 통과합니다.
- 기존 경로가 남아 있다는 사실만으로 생성 성공을 판정하지 않습니다.
- `checkElectrical`은 제품 계산기를 다시 호출해 자기검증하지 않고 독립 수식과
  승인된 한도로 검사합니다. 계산 불가(`—`)와 NG를 같은 결과로 취급하지 않습니다.
- 프로젝트 복원 비교는 저장 시점 JSON과 복원 후 현재 상태를 메모리에서 직렬화한
  JSON을 깊은 비교합니다(임시 파일 저장 없음).
  `*Utc`, `routeContextKey`, `edgeKey`, `revision`, `modelRevision`,
  `workingContextId`, `workingContextRevision`, `nativePieceId(s)`처럼 재가져오기에서
  바뀌는 런타임 식별자는 제외하고, `areaId`·선택 면 ID·topology signature·경계
  parameter·확정 3D 길이·전기 입력은 비교 대상으로 유지합니다.
- 최종 검사는 전기 검토 범위와 무관하게 모든 영역을 직접 순회합니다.
- 네이티브 경로·마커는 개수 누적과 alias별 관리 경로의 허용오차 대응을 함께 봅니다.
- 모든 중간 검사 후 화면 관찰 대기 시간(기본 1초)을 둡니다.
- 검증되지 않은 값을 통과한 것처럼 기록하지 않습니다.
