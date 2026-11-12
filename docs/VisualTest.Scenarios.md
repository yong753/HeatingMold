# 시각 테스트 시나리오 형식

작성일: 2026-11-06 (2026-11-12 대표 종합 시나리오·새 행동 추가 반영)
대상 코드: `src/HeatingMold.App/VisualTesting/`, `src/HeatingMold.App/MainWindow.VisualTest.cs`
실행 스크립트: `tests/HeatingMold.VisualTest/Run.ps1`

> 저장소에는 세 개의 대표 시나리오가 포함되어 있습니다.
> `tests/HeatingMold.VisualTest/scenarios/e2e-full.json`은 한 영역의 생성·편집·분할·
> 저장/복원 전체 흐름을, `e2e-multi-area.json`은 서로 독립적인 두 영역(가로/세로)을
> 만들어 한 영역을 바꿔도 다른 영역의 확정 경로가 보존되는지를 검사합니다.
> `e2e-margin-spacing-exit.json`은 기존과 다른 마진·발열사 간격·인출점 위치 조합에서
> 같은 보존 규칙과 공유 전력 재배분을 검사합니다.
> (이전 `e2e-main.json`은 제외영역·꼭지점 편집·분할 후 STEP을 포함하지 않아
> `e2e-full.json`으로 대체되었습니다.) 이 문서는 세 시나리오와 직접 작성하는
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

- 현재 지원 버전은 `schemaVersion: 2` 하나입니다. `areas`와 `steps`를 사용합니다.
- 다른 버전은 실행 전에 거부됩니다.

### 2.2 공통 필드

- `model`: 저장소 기준 상대 경로, SHA-256, 면 수, 솔리드 수
- `exitPoint`: 작업평면 2D 좌표와 허용 오차로 영역 외곽을 직접 지정하는 제품 경로
- `electrical`: 와이어 프리셋(0/1), 허용 전압·전류, 전체 발열전력(kW), 검토 범위, 기대 판정

모델 해시, 면 수, 경계 지문이 맞지 않으면 다른 모델이나 다른 경계로 자동 대체하지
않고 실패합니다.

### 2.3 실행 규칙

- 영역은 화면 표시 이름이 아니라 테스트 식별자(alias)로 추적합니다.
- 영역은 면 집합·마진·간격·방향·최종 기대값을 선언합니다.
- 단계는 허용된 행동 이름만 사용합니다. 알 수 없는 행동·중복 alias·잘못된 참조는
  실행 전에 거부합니다.
- 좌표 기반 행동은 작업평면 좌표를 저장하되 실제 좌표 변환과 핸들러를 통과합니다.
- 현재 제품의 직접 인출점 선택은 `pickExitPoint`로 검증합니다. 작업평면 2D 좌표를
  `BeginExitPointPick` → 영역 경계 투영 → 3D 표면 평가 → 선택점 적용의 실제 제품
  경로에 넣고, 적용된 원본 CAD edge/parameter, artificial·split provenance를 기록합니다.
- `splitArea`는 `childAliases`로 분할 결과 영역을 추적합니다. 부모 alias는 제거되고
  자식 alias가 추가되며, 이후 `checkArea`·`activateArea`가 자식 alias를 참조합니다.
- 영역 기대값(`expect`)은 `pieces`, `turns`, `circuits`, `lengthMm`,
  `heatingPieces`, `heatingLengthMm`, `routeEdited`, `marginMm`, `spacingMm`입니다.
  `heatingPieces`/`heatingLengthMm`는 발열선(`HeatingRun`) 조각만 센 개수와 확정 3D
  길이 합이며, 커넥터를 포함한 전체 길이가 같아도 제외영역이 실제 발열 구간을 바꿨는지
  구분하기 위해 사용합니다.

### 2.4 영역 경로 보존 검사

시각 테스트는 각 행동 전후로 모든 영역의 **확정 경로 스냅샷**을 값으로 복사해
비교합니다(`src/HeatingMold.App/VisualTesting/VisualRouteSnapshot.cs`). 비교 대상은
영역 ID·이름·색·선택 면·유효 경계·제외영역·방향·적용 마진/간격/방향·적용 경로 조건
키·표시 상태·수동 편집 여부·회로 수·회로별 조각 구성·조각별 종류/레인/2D 길이/
native 조각 ID/확정 3D 길이/2D·3D 좌표/지지 면·시작/종료 인출점(좌표·구간·parameter·
CAD/artificial/split 연결)·인출 경계 폴리라인·분할 절단선입니다. 전력 배분·전압·전류·
활성 영역·입력 초안 텍스트 같은 파생/표시 값은 비교하지 않습니다(경로나 영역 구성이
바뀌면 정상적으로 함께 바뀌기 때문입니다).

- 대상 영역을 바꾸는 행동(생성·제외 추가/제거·인출점 선택·꼭지점 편집 확정·영역 삭제)은
  **대상이 아닌 영역**의 스냅샷이 그대로여야 합니다.
- 읽기·표시 행동(영역 전환·입력 초안 복원·영역 검사·전체 검사·전기 검사·DXF/STEP 출력·
  프로젝트 저장)은 **모든 영역**의 스냅샷이 그대로여야 합니다.
- 거부·무효(no-op)·미리보기 취소는 대상 영역까지 포함해 모든 영역의 스냅샷이
  그대로여야 합니다.
- 비교는 허용 오차 없는 엄격 동등 비교이며, 첫 차이는 `영역: 필드이(가) 달라졌습니다`
  형식으로 실패 사유에 남습니다. 총 길이나 조각 수만 비교해 통과시키지 않습니다.

---

## 3. 행동 목록

기존 구현 행동:

`loadModel`(암묵), `selectFaces`, `toggleFaces`, `clearSelection`,
`expectSelection`, `confirmArea`, `activateArea`, `deleteArea`, `setAreaInputs`,
`expectAreaInputs`, `generate`,
`checkArea`, `checkAllAreas`, `addExclusion`, `removeExclusion`, `expectNoPattern`,
`checkExclusion`, `moveVertex`, `exportDxf`

대표 종합 흐름용 추가 행동:

| 행동 | 입력 | 검사 |
|---|---|---|
| `pickExitPoint` | `exitPoint.role`(start/end), `uv`(작업평면 2D), `toleranceMm` | 실제 선택 모드 진입·경계 투영·3D 표면 평가·선택점 적용, CAD/artificial/split 연결 정보 기록 |
| `setElectrical` | `electrical`(preset 0/1, 허용 전압·전류, 전체 발열전력 kW) | 실제 UI 입력을 적용하고 제품이 읽은 한도가 시나리오와 같은지 검사 |
| `checkElectrical` | `electrical`(선택, `apply=false`면 입력을 바꾸지 않고 읽기만 함, `circuitVerdicts[{area,verdict}]`, `expectedWithinLimits`) | 독립 수식(R=rL, V=√(PR), I=√(P/R), 길이비 배분)으로 회로별 전력·저항·전압·전류·판정, 영역별 OK/NG, 합계, 검토 범위 전환 시 재배분 없음, 화면 행 수를 검사 |
| `splitArea` | `area`, `partCount`(2..4), `childAliases`, `confirm`, `confirmAction`, `outcome` | 실제 자동분할 제품 경로, 자식 2..4개·각 1회로·부모 선택 면 유지, 다른 영역 보존, 거부 시 부모와 경로·선택점 보존 |
| `exportStep` | `fileName`, `scope`, `outcome` | 실제 STEP 내보내기 공통 경로, 파일 생성·비어 있지 않음, 요청 범위의 영역·조각 수와 수집 결과 일치, 범위 밖 영역이 payload에 섞이지 않음, 네이티브 자기검증 통과, 거부 시 기존 파일 보존 |
| `exportDxf` | `fileName`, `scope`, `outcome` | 실제 DXF 내보내기 공통 경로, 파일 재읽기로 `HEAT_WIRE`/`HEAT_AREA` 폴리라인 수·영역 색, 전기 사양표의 회로 ID·반올림 수치·합계 행, 범위 밖 회로 ID 부재 검사 |
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
| `src/HeatingMold.App/VisualTesting/VisualScenario.cs` | v2 영역·단계 계약과 검증 |
| `src/HeatingMold.App/VisualTesting/VisualScenarioCompiler.cs` | v2 → 실행 단계 목록, alias 참조 검증 |
| `src/HeatingMold.App/MainWindow.VisualTest.cs` | 행동 실행·중간 검사·최종 검사 |
| `src/HeatingMold.App/VisualTesting/VisualTestReport.cs` | 실행 기록과 실제 생성 DXF 파일 목록 |
| `src/HeatingMold.App/VisualTesting/VisualRouteSnapshot.cs` | 확정 경로 스냅샷과 엄격 비교 |
| `src/HeatingMold.App/VisualTestLauncherWindow.xaml(.cs)` | 앱에서 새 프로세스로 실행하는 UI |
| `tests/HeatingMold.VisualTest/Run.ps1` | 빌드 후 앱을 실행하는 스크립트 |
| `tests/HeatingMold.VisualTest/scenarios/` | 시나리오 JSON. 전체 기능 흐름 `e2e-full.json`, 독립 다중 영역 `e2e-multi-area.json`, 조건 변형 `e2e-margin-spacing-exit.json` |
| `tests/HeatingMold.App.Tests/VisualTestingTests.cs` | 스키마 계약·옵션·리포트·러너 검증 |
| `tests/HeatingMold.App.Tests/VisualRouteSnapshotTests.cs` | 확정 경로 스냅샷 비교 계약 검증 |

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

## 4.2 독립 다중 영역 시나리오 `e2e-multi-area.json`

한 영역을 바꿔도 다른 영역의 확정 경로·길이·인출점이 보존되는지를 검사합니다.
같은 기본 예제에서 2D로 겹치지 않는 두 영역을 씁니다.

| alias | 선택 면 | 마진 | 간격 | 방향 |
|---|---|---|---|---|
| A | 44, 56, 69 | 5 mm | 15 mm | 프레임 X(가로 레인) |
| B | 16, 24, 26 | 5 mm | 20 mm | 프레임 Y(세로 레인) |

흐름:

1. A 확정·인출점 직접 선택·생성(60조각/29턴/발열선 30조각/18,015.505 mm)
2. B 확정·인출점 직접 선택·생성(32조각/15턴/발열선 16조각/8,467.619 mm)
3. 전기 입력(Case 1, 3.5 kW 공유)과 전체 검사 — A는 NG, B는 OK인 혼합 판정
4. A 간격을 18 mm로 바꾼 초안에서 B가 영향받지 않는지, 초안 복원 후 A만 재생성되는지
   (48조각/23턴/14,416.975 mm) 확인
5. B 외곽을 가로지르는 홈 제외영역 추가 → B만 무효화 → 재생성(발열선 8,053.498 mm,
   전체 8,410.384 mm) 및 마진 확장 사각형 침범 0 검사
6. B에서 간격 방향 이동은 거부되고, 진행 방향 미리보기는 표시 후 취소되어
   두 영역 모두 보존되는지 확인
7. 전체/현재 B/현재 B 반복/현재 A/전체 전기 검사를 연속 수행해 검토 범위 전환이
   표시 범위만 바꾸고 공유 전력 배분을 다시 계산하지 않는지 확인
8. 전체·현재 영역 DXF/STEP 출력 후, 두 영역 프로젝트 저장 → 같은 실행에서 재로드 →
   저장 시점 JSON과 깊은 비교, 복원 영역 상태·출력 재검사
9. B 삭제 → A의 확정 경로·길이 보존, 공유 전력만 A 단독 배분으로 재계산,
   단일 영역 DXF/STEP 출력

전기 판정이 혼합이므로 `expectedWithinLimits`(모든 회로에 같은 기대) 대신
`circuitVerdicts`로 영역별 NG/OK를 지정합니다.

---

## 4.3 조건 변형 시나리오 `e2e-margin-spacing-exit.json`

같은 기본 예제·같은 두 면 조합에서 **마진·발열사 간격·인출점 위치만** 기존과 다르게
바꿔, 조건이 달라져도 생성 규칙과 영역 간 보존 규칙이 유지되는지 검사합니다.
`e2e-multi-area.json`이 기본 조건(마진 5 mm, 간격 15/20 mm, 오른쪽·아래쪽 인출)을
검증하므로, 이 시나리오는 그 값에 의존하지 않는지 확인하는 대조군입니다.

| alias | 선택 면 | 마진 | 간격 | 방향 | 인출점 |
|---|---|---|---|---|---|
| A | 44, 56, 69 | 8 mm | 24 mm | 프레임 X(가로 레인) | 왼쪽 변(`-362.7109`, v -300 → -36) |
| B | 16, 24, 26 | 12 mm | 28 mm | 프레임 Y(세로 레인) | 위쪽 변(`u 600`, v 252.376 → 경사 위쪽 변 `u 370`, v 217.95) |

흐름:

1. A 생성(24조각/11턴/발열선 12조각/8,030.735 mm) → B 생성(20/9/10/5,239.919 mm)
2. 표준 전기(Case 1, 3.5 kW 공유, 16 A) — A는 NG, B는 OK인 혼합 판정
3. B 하단 홈 `[520,-200,590,-166]` 추가 → B만 무효화 → 재생성(5,174.13 mm,
   발열선 4,841.326 mm) → 마진 12 mm 확장 사각형 침범 0, A 보존
4. A에서 간격 방향(Y) 이동은 거부되고, 진행 방향(X) 미리보기는 표시 후 취소되어
   두 영역 모두 보존되는지 확인 — `e2e-multi-area`와 방향이 반대입니다
5. A 초안을 10 mm/30 mm로 바꿔도 B 초안·경로가 영향받지 않고, 초안을 원 조건
   (8 mm/24 mm)으로 되돌려 재생성하면 같은 24조각/8,030.735 mm가 나오는지 확인
6. 허용 전류를 5 A로 낮추면 두 영역 모두 NG, 16 A로 복원하면 A NG·B OK로 돌아오는지,
   검토 범위(전체/현재 A/현재 B)를 반복 전환해도 공유 전력 배분이 바뀌지 않는지 확인
7. 전체·현재 B DXF/STEP 출력, 두 영역 저장 → 같은 실행에서 재로드(`compareProject`),
   복원 상태·출력 재검사
8. A 삭제 → B의 확정 경로·길이 보존, 남은 B가 공유 3.5 kW 전부를 배분받아
   **OK에서 NG로 바뀌는지** 확인(영역 삭제에 따른 공유 전력 재배분), 단일 영역 출력

`apply: false`인 `checkElectrical`은 제품에 적용된 허용 전압·전류·전력을 그대로
지정해야 합니다. DTO 기본값(16 A)을 생략한 채 제품 상태가 5 A이면 "제품에 적용된
허용 전류가 시나리오와 다릅니다"로 실행 전에 실패합니다.

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
- 행동 전후 확정 경로 스냅샷 비교로 비대상·전체 영역의 경로 변경을 검출합니다.
  총 길이와 조각 수가 같아도 조각별 3D 좌표·native ID·인출점 연결·적용 조건이
  달라지면 실패합니다.
- DXF·STEP은 파일이 만들어졌다는 사실만으로 통과시키지 않고, 요청 범위의 영역·회로·
  조각·색이 실제 산출물과 일치하는지 확인합니다. 전체 범위에서 현재 영역 범위로
  전환했을 때 범위 밖 회로·영역이 섞이면 실패합니다.
- 모든 중간 검사 후 화면 관찰 대기 시간(기본 1초)을 둡니다.
- 검증되지 않은 값을 통과한 것처럼 기록하지 않습니다.
