# 시각 테스트 시나리오 형식

작성일: 2026-11-06 (2026-11-11 시나리오 항목 전체 삭제 반영)
대상 코드: `src/HeatingMold.App/VisualTesting/`, `src/HeatingMold.App/MainWindow.VisualTest.cs`
실행 스크립트: `tests/HeatingMold.VisualTest/Run.ps1`

> 저장소에는 실행할 시나리오 파일이 포함되어 있지 않습니다. 이 문서는 시나리오 JSON을
> 직접 작성할 때 쓰는 계약과 행동 목록만 정의합니다.

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
- `exit`: 인출면 ID와 시작/종료 3D 좌표(후보 식별용)
- `anchor`: 시작/종료점 후보 인덱스
- `electrical`: 와이어 프리셋, 전압, 검토 범위

모델 해시, 면 수, 후보 지문이 맞지 않거나 후보가 모호하면 다른 모델이나 다른 후보로
자동 대체하지 않고 실패합니다.

### 2.3 v2 규칙

- 영역은 화면 표시 이름이 아니라 테스트 식별자(alias)로 추적합니다.
- 영역은 면 집합·마진·간격·방향·인출면·앵커·최종 기대값을 선언합니다.
- 단계는 허용된 행동 이름만 사용합니다. 알 수 없는 행동·중복 alias·잘못된 참조는
  실행 전에 거부합니다.
- 좌표 기반 행동은 작업평면 좌표를 저장하되 실제 좌표 변환과 핸들러를 통과합니다.
- 인출 위치는 화면 좌표가 아니라 **작업영역 면과 인출면이 맞닿는 공유 경계**로
  지정합니다. 후보 번호만 믿지 않고 인출면 ID와 경계 끝점 3D 좌표로 다시 식별합니다.
- 앵커는 가능하면 작업평면 2D 좌표 지문으로 식별합니다.

---

## 3. 행동 목록

1차 구현 행동:

`loadModel`(암묵), `selectFaces`, `toggleFaces`, `clearSelection`,
`expectSelection`, `confirmArea`, `activateArea`, `deleteArea`, `setAreaInputs`,
`pickExit`, `cancelExitPick`, `generate`,
`checkArea`, `checkAllAreas`, `exportDxf`

2차 구현 행동:

`addExclusion`, `removeExclusion`, `moveVertex`
(확인 응답 주입 포함)

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
| `tests/HeatingMold.VisualTest/scenarios/` | 사용자가 작성한 시나리오 JSON (저장소 기본 제공 없음) |
| `tests/HeatingMold.App.Tests/VisualTestingTests.cs` | 스키마 계약·옵션·리포트·러너 검증 |

---

## 5. 판정 원칙

- 생성 성공은 새 패턴·적용 조건·표시 상태가 함께 갱신됐을 때만 인정합니다.
- 예상 거부는 지정한 행동·사유·횟수와 일치할 때만 통과합니다.
- 기존 경로가 남아 있다는 사실만으로 생성 성공을 판정하지 않습니다.
- 최종 검사는 전기 검토 범위와 무관하게 모든 영역을 직접 순회합니다.
- 네이티브 경로·마커는 개수 누적과 alias별 관리 경로의 허용오차 대응을 함께 봅니다.
- 모든 중간 검사 후 화면 관찰 대기 시간을 둡니다.
- 검증되지 않은 값을 통과한 것처럼 기록하지 않습니다.
