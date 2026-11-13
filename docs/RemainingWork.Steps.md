# 남은 구현 단계별 실행 지시서

- 작성일: 2026-11-13
- 기준: `HEAD e67804b` + 미커밋 변경(마진 제거·외곽 간격 전환·정확 행 교차·간격 표시·시각테스트 schema 3)
- 지위: `docs\RemainingWork.Plan.md`의 남은 작업 §1~§4 중 이 문서와 충돌하는 단계는 이 문서를 따른다.
  기존 계획의 계약·검증 명령·문서화 규칙은 그대로 유효하다.
- 현재 판정: **1~8단계 완료 (2026-11-13).** 자동검증 통과 보고는 있으나, 아래 확정 결함과 미해결 실패가 남아 있다. 각 단계의 완료 증빙은 해당 단계의 `진행 결과`에 기록한다.

이 문서는 실행자가 바로 따라갈 수 있도록 단계마다 **대상 / 작업 / 필수 테스트 / 통과 기준 / 중단 조건**을
분리해 적는다. 각 단계는 앞 단계의 통과 없이 시작하지 않는다. 여기서 "통과"는 **그 단계의 필수 테스트와
통과 기준을 충족했다**는 뜻이며, 시각테스트 5개 전체 통과를 뜻하지 않는다(§0.5).

---

## 0. 공통 원칙과 고정 계약

### 0.1 진행 방식

1. 한 번에 한 단계만 수정한다. 수정 → 집중 테스트 → 독립 리뷰 → 수정·재검증 순서를 지킨다.
2. **실패 원인을 확인하기 전에 시나리오의 면 집합·간격·인출점·검사 단계를 바꾸지 않는다.**
3. 기존 미커밋 변경과 무관한 파일(`tools\deploy\assets\Manual\index.template.html` 등)을 함께 정리하거나 되돌리지 않는다.
4. 자동검증 통과, 실제 마우스 조작, 화면 육안 확인을 구분해 보고한다.
5. 승인 대기 제품 정책을 구현자가 임의로 결정하지 않는다. 해당 항목은 멈추고 보고한다.
6. 테스트가 통과해도 "설계대로 동작함"의 증거가 되지 않는 경우(자기 결과 재합산, 직경 0 고정, 배정만 확인)를
   통과 근거로 쓰지 않는다.

### 0.2 의미 고정 표

| 항목 | 확정 의미 |
|---|---|
| 사용자 간격 입력 | 인접 발열사의 **외곽 사이 간격**(mm) |
| 중심 피치 | 외곽 간격 + 전선 직경. **한 번만** 더한다 |
| 설계 좌표 | 확정 작업평면의 2D 좌표. 카메라 좌표·표면 UV는 설계 좌표가 아니다 |
| 영역 경로 | 영역 하나당 연속된 회로 하나 |
| 자동분할 | 한 번에 최대 4개의 독립 영역. 영구 부모·자식 관계 없음 |
| 자동 인출점 | 부모의 시작·종료 **측면**과 선택 진행 범위를 유지 |
| 실패·취소 | 기존 영역·경로·길이·ID·선택 인출점을 그대로 보존 |
| 길이 균등화 | 절단 위치만 탐색하고, 남는 2D 편차는 결과에 표시 |
| 전기 판정 | 공유 발열전력 + 확정된 실제 3D 길이 기준. 자동분할은 총 전력을 늘리지 않는다 |

### 0.3 금지 사항

- 허용치·공차를 키우거나 기대값을 낮춰 실패를 숨기는 것.
- 마지막 레인만 뒤집기, 임의 레인 삭제, 피치 변경.
- 실패한 일부 자식만 확정하는 부분 분할.
- 실패를 최근접 투영·이웃 행·다른 원본 edge로 조용히 대체하는 것.
- 분할 길이 균등화의 옛 1.10 하드 거부 기준을 되살리는 것(이미 제거됨).
  실측 1.8184배 같은 잔여 편차는 그 자체로 결함이 아니다.

### 0.4 검증 환경 규칙

- 네이티브는 `MSBuild.exe`로 Release/x64, `OutDir`을 저장소 `x64\Release\`로 **명시**해 빌드한다.
- App/테스트/회귀는 `x64\Release\OccBridge.dll`, `HeatingMold.App.dll`, `HeatingMold.Core.dll`을
  각 실행 폴더로 **수동 복사**한 뒤 `--no-build`로 실행한다. `dotnet build`는 이 DLL들을 갱신하지 않는다.
- 실행 전 실제 로드한 DLL의 경로와 SHA-256을 기록한다. 타임스탬프만으로 최신 여부를 단정하지 않는다.
- 앱이 실행 중이면 출력이 잠기므로 격리 `OutDir`을 쓴다.

### 0.5 단계별 테스트 통과 정책

**"앞 단계의 통과"는 그 단계의 필수 테스트·통과 기준을 충족했다는 뜻이며, 시각테스트 5개 전체를
통과했다는 뜻이 아니다.** 단계별 통과 범위는 다음과 같다.

| 단계 | 그 단계에서 통과해야 하는 것 |
|---|---|
| 0 | 구현 단계가 아니다. 공통 원칙·계약을 이후 모든 단계에 적용한다 |
| 1 | 두 실패의 재현, 실행 DLL 증빙, 실패 전후 보존 검사 |
| 2~6 | 해당 단계의 필수 테스트·통과 기준, 변경이 영향을 주는 범위의 회귀 검사. 개별 시각검사가 필수로 지정된 단계는 그 검사도 통과해야 한다 |
| 7 | 새 계약으로 갱신한 시각테스트 5개를 각각 2회 통과 |
| 8 | 최종 코드 기준 전체 회귀(Core·App·WorkingSide·시각테스트 5개) |

- **1~6단계에서 시각테스트 5개 전체를 매번 통과시키는 것은 다음 단계 진행의 공통 선행조건이 아니다.**
- 2·3단계처럼 계약·계산이 바뀌어 기대값이 낡은 시나리오(예: `e2e-split-safety`의 B-1 길이)는
  7단계에서 실측값으로 재확정한다. 그때까지의 실패는 추적 상태로만 기록한다.
- 1~6단계 변경으로 다른 시나리오가 실패하더라도, 원인이 확인되고 7단계 재측정 대상으로 기록되어 있으면
  그 단계를 멈추지 않는다. **원인 미확인 실패는 다음 단계로 넘기지 않는다.**
- 해당 단계에 시각테스트 실행이 필수로 지정되어 있으면(예: 3단계 진행 결과의 실모델 저장·재로드 22/22),
  그 검사는 생략할 수 없다.

### 0.6 중간 단계 실패 처리 기준

| 실패 종류 | 처리 |
|---|---|
| 계약 변경으로 낡아진 수치 기대값(길이·조각 수·절단 좌표) | 변경 근거를 기록하고 7단계에서 실측값으로 재확정한다. 2~4단계 원인 판정 전에 기대값을 바꿔 통과시키지 않는다 |
| 이미 재현되어 후속 단계에 배정된 결함(1단계 자산의 실패) | 추적 상태로 유지한다. 해결·통과로 보고하지 않는다 |
| 새 계약상 근거 있는 정상 거부 | 거부 사유와 기존 상태 보존을 검증하는 테스트로 고정한다. 이것은 통과다 |
| 새 회귀 또는 불변 계약(§0.2) 위반 | 해당 단계의 통과를 저지한다 |
| 원인 미분류, 실행 DLL 불명확 | 정상 변화로 단정하지 않고 그 자리에서 멈춘다 |

- **"로직이 바뀌었다"는 설명만으로 실패를 면제하지 않는다.**
- 정상적인 거부 동작과 테스트 실패는 다르다. 예상한 거부와 상태 보존을 확인하는 테스트는 통과해야 한다.
- 중간 단계 보고에는 다음을 포함한다.
  - 해당 단계 통과/미통과.
  - 전체 시각테스트를 실행하지 않았다면 그 사실과 이유.
  - 추적 중인 실패의 근거·담당 단계·로그 위치.
  - 기대값이 미확정인지 확정인지.


---

## 1단계. 실패 조건과 실행 DLL부터 고정한다

**목적:** 이후 수정의 비교 기준을 만든다. 실패를 지우지 않고 재현 가능한 자산으로 남긴다.

### 대상
- `tests\WorkingSide.Regression\`(fixture 추가 위치)
- `artifacts\visual-test\meas-four-1\result.json` — 영역 B 세로 4분할, 마지막 자식 `AnchorNotAtLaneEnd`
- `artifacts\visual-test\meas-margin-9\result.json` — 영역 B 하단 제외영역 추가 후 `The path leaves the selected surface domain`

### 작업
1. 위 두 실패를 **별도 재현 fixture/시나리오**로 보존한다. 기존 통과 시나리오에 섞지 않는다.
2. 실패 당시 입력을 기록한다: 모델·SHA, 면 집합, 전선 Case, 외곽 간격, 방향, 시작/종료 uv와 binding,
   제외 사각형, 허용 전압/전류/공유 전력, 요청 분할 수.
3. 로그로 확인되지 않는 값은 **추정하지 말고 "미확정"으로 표시**한다.
4. 실행 DLL 3종의 경로·SHA-256과 실제 로드 여부를 기록한다.

### 통과 기준
- 같은 입력으로 같은 실패 지점까지 재현된다.
- 실패 전후로 기존 영역·경로·길이·선택점이 보존됐는지 검사할 수 있다.
- 최신 DLL로 실행했다는 증빙이 있다.

### 중단 조건
- 입력이나 DLL 출처가 불명확하면 제품 코드 수정으로 넘어가지 않는다.

### 진행 결과 (2026-11-13, 완료)
- 별도 재현 시나리오 `repro-b-four-split-anchor.json`은 **18/18단계 OK**, `repro-b-exclusion-surface.json`은 **20/20단계 OK**, 둘 다 `Status=Completed`.
- 세로 4분할은 마지막 자식 `B-4`가 `시작 인출점이 마진 안쪽 사행 구간의 진행축 끝에 있지 않습니다`로 거부됐고, 분할 전 A/B 경로·길이·선택점이 보존됐다.
- 제외영역 `[520,-200,590,-166]` 추가 후 B 재생성은 `The path leaves the selected surface domain`으로 거부됐고, 실패 전후 A 경로와 B의 경로 없음 상태가 보존됐다.
- `tests\WorkingSide.Regression\Fixtures\remaining-failures.json`에 모델 SHA, 면 집합, Case, 외곽 간격, 방향, 시작/종료 uv·binding, 제외 사각형, 전기 한도, 요청 분할 수, 실패 문구, 보존 기대를 기록했다.
- `RemainingFailureFixtureChecks`가 시나리오와 fixture의 입력 일치를 **312건** 검사한다. 시나리오 2의 인출점 tolerance `0.5`는 원본 로그에 없어 **미확정 가정값**으로 명시했다.
- 실행 DLL 증빙: `OccBridge.dll` `D5E5…E9AE`, `HeatingMold.App.dll` `399F…2023`, `HeatingMold.Core.dll` `2D9C…083E`. 세 파일 모두 앱 출력 폴더에서 실제 로드된 것을 확인했다.

---

## 2단계. 외곽 간격과 중심 피치의 혼용 제거 (최우선)

**목적:** 분할 경로만 중심 피치가 `외곽 + 직경 × 2`가 되는 확정 결함을 없앤다.

### 확정 결함(근거)
- UI는 `SpacingBox`에서 외곽 간격 `g`를 읽는다(`MainWindow.xaml.cs`의 `TryGetParams`).
- `MainWindow.Split.cs` 177행이 `spacing + wireDiameter`, 즉 `g + d`를 `TryPrepareBalancedSplit`에 넘긴다.
- 그 값이 `SplitSearch`를 거쳐 `TryGenerateSelectedExitPattern`의 `spacing` 인자로 들어가고,
  `PatternParams { Spacing = g + d, WireDiameter = d }`가 되어 생성기가 `CenterPitch = g + 2d`를 쓴다.
- 일반 생성은 `CenterPitch = g + d`다. 즉 **분할만 피치가 다르다**.
- 확정 입력에는 원래 `g`가 기록되어 stale 검사도 이 불일치를 잡지 못한다.
- 기존 분할 테스트는 전부 직경 0으로 호출해 이 차이를 검출하지 못한다.
  `Evaluate_ReportsSameTotalsAsDirectGeneration`은 평가기가 만든 길이를 다시 합산해 비교하므로 독립 검증이 아니다.

### 대상
- `src\HeatingMold.Core\Geometry\LanePitchSpec.cs`(기존 정본 재사용)
- `src\HeatingMold.App\MainWindow.Split.cs`
- `src\HeatingMold.App\MainWindow.SplitSearch.cs`
- `src\HeatingMold.App\MainWindow.xaml.cs`(`TryGenerateSelectedExitPattern`, `CaptureAppliedInputs`)
- `tests\HeatingMold.App.Tests\SplitLengthSearchTests.cs`

### 작업
1. 분할 탐색·평가에 간격을 **의미가 드러나는 형태**로 전달한다. 새 중복 타입을 만들지 말고
   기존 `LanePitchSpec`(OuterGapMm / WireDiameterMm / CenterPitchMm)을 쓴다.
2. 다음 전달 규칙을 모든 호출부에서 지킨다.

   | 계산 | 사용할 값 |
   |---|---|
   | 예상 길이·밴드 절단(`AreaSplitter`) | 중심 피치 |
   | 레인 격자 seed(`LaneGridSpec.TryBuild`) | 중심 피치 |
   | 자동 인출점 행(`SplitExitPointPlanner`) | 중심 피치 |
   | 실제 경로 생성(`PatternParams`) | 외곽 간격 + 직경을 **각각** |
   | 확정 입력 저장(`AppliedGenerationInputs`) | 실제 생성에 쓴 외곽 간격·직경 |

3. 진입 한 줄만 고치지 않는다. 위 표의 하위 계산이 같은 피치를 쓰는지 함께 확인한다.
4. 직경 0 테스트만으로 통과 판정하지 않는다.

### 필수 테스트
- 외곽 5 mm에서 Case 1(Ø0.30) 실제 인접 레인 중심 거리 = 5.30 mm.
- 같은 조건 Case 2(Ø0.15) = 5.15 mm.
- 일반 생성과 분할 자동 생성이 **같은 피치 규칙**을 쓴다.
- 입력을 바꾸지 않고 분할 자식을 다시 생성하면 레인 행·조각 구성·길이가 같다.
- 저장·복원된 확정 입력의 외곽 간격·직경이 실제 생성 값과 일치한다.
- 길이 비교는 반환 패턴을 다시 합산하는 방식이 아니라 **실제 레인 좌표 차이**로 검사한다.

### 통과 기준
- 직경이 있는 두 Case에서 중복 덧셈이 없다.
- 동일 입력 재생성이 결정적이다.
- 분할 후 저장→재열기→재생성이 같은 결과를 낸다.

### 진행 결과 (2026-11-13, 완료)
- `LanePitchSpec`을 분할 탐색·평가의 단일 전달 객체로 사용한다. `TryPrepareBalancedSplit`과 `TryEvaluateSplit2D`는 `LanePitchSpec`을 받고, 영역 분할·레인 격자·자동 인출점 행·지역 탐색은 `CenterPitchMm`을 쓴다. 실제 생성에만 `OuterGapMm`과 `WireDiameterMm`를 각각 넘긴다.
- 분할 진입점의 `spacing + wireDiameter` 이중 덧셈을 제거했다. 일반 생성과 분할 자동 생성이 같은 `외곽 + 직경` 규칙을 쓴다.
- App 테스트 **184 통과**. 신규 검사는 외곽 5 mm에서 Case1(Ø0.30) 실제 레인 중심거리 **5.30 mm**, Case2(Ø0.15) **5.15 mm**, 같은 입력 재평가 시 레인 좌표·조각 구성 동일을 확인한다.
- 실제 모델 2분할 → 저장 → 같은 실행 재로드 비교가 `Completed`였고, B-1은 `4,276.956 mm`, B-2는 `4,465.184 mm`, 적용 입력은 외곽 `19.35 mm`·직경 `0.15 mm`로 복원 후에도 동일했다.
- WorkingSide 회귀 전 그룹 통과: split boundary 28, remaining failure fixture 312, working-side 187, 나머지 그룹 동일. 실행 DLL은 App `7A1A…E378`, Core `2D9C…083E`, OccBridge `D5E5…E9AE`.
- **7단계에서 갱신할 기존 시나리오 기대값:** `e2e-split-safety`의 B-1 기대 `4,280.265 mm`는 피치 수정 후 실제 `4,276.956 mm`가 되어 현재 실패한다. 이는 2단계 결함 수정의 정상적인 결과이며, 7단계에서 실측값으로 재확정한다. 제품 회귀로 되돌리지 않는다.

---

## 3단계. 정확 행 교차의 모호성 처리 교정

**목적:** 정확 교차를 도입하면서 생긴 provenance 손실과 겹침 무시를 제거한다.

### 진행 결정 (2026-11-13)
- "목표 레인 끝"은 자식 외곽의 최좌·최우가 아니라, **그 행의 실제 스캔라인 발열 구간 끝**으로 한다.
  제외영역 때문에 레인이 영역 안쪽에서 끊기는 경우 외곽 극값은 실제 레인 끝이 아니기 때문이다.
- 시작 행은 생성기가 시작을 허용하는 첫 구간, 종료 행은 마지막 구간의 해당 측면 끝을 목표로 삼는다.
- 후보 선택은 `부모 지정 측면 유지 → 목표 구간 끝에 가장 가까운 후보 → 1e-6 동률의 서로 다른 provenance는 모호성 실패` 순서로 고정한다.

### 확정 결함(근거)
- `SplitBoundarySnapshot.cs` 163행 부근의 중복 판정이 **2D 좌표 + 숫자 parameter**만 비교한다.
  원본 edge·제외변·절단선이 다르면 다른 후보여야 하는데 먼저 지워져 모호성 검사에 도달하지 못한다.
- `SplitRegionBoundary2D.cs` 311행 부근도 같은 방식으로 CAD/artificial/split 후보를 합친다.
- `SplitRegionBoundary2D.cs` 279행은 `tangent && hits.Count == 0`일 때만 실패한다.
  다른 교차점이 하나라도 있으면 **겹치는 경계를 조용히 통과**시킨다. 계획의 즉시 실패 조건 위반이다.
- `SplitExitPointPlanner`의 후보 선택은 목표 레인 끝 최근접이 아니라 측면 절대 최외곽을 고를 수 있다.
  오목 경계의 한 행에 구간이 여럿일 때 의도한 구간 밖을 고를 수 있다.

### 대상
- `src\HeatingMold.App\SplitBoundarySnapshot.cs`
- `src\HeatingMold.App\SplitRegionBoundary2D.cs`
- `src\HeatingMold.App\SplitExitPointPlanner.cs`
- `tests\HeatingMold.App.Tests\SplitExitPointPlannerTests.cs`, `SplitRegionBoundaryTests.cs`
- `tests\WorkingSide.Regression\SplitBoundaryChecks.cs`

### 작업
1. 중복 판정은 **같은 원본 참조 + 같은 선택 parameter**일 때만 중복으로 본다.
   다른 edge·제외변·절단선은 좌표가 같아도 별도 후보로 유지한다.
2. 접선·겹침은 다른 교차점이 있어도 **즉시 명시적 실패**한다.
3. 후보 선택 순서를 고정한다: 부모 지정 측면 유지 → 목표 레인 끝에 가장 가까운 후보 →
   동률(1e-6 이내)의 서로 다른 provenance는 모호성 실패.
4. 선택한 parameter를 원본 CAD에서 평가해 3D 위치를 얻는다. 2D 재투영으로 대체하지 않는다.
5. 실패를 최근접 투영·이웃 행·다른 edge로 대체하지 않는다.

### 필수 테스트
- 같은 좌표·같은 숫자 parameter지만 원본 edge가 다른 두 후보가 모두 유지되고 모호성으로 실패한다.
- CAD 경계와 절단선이 같은 좌표에서 만나는 경우 provenance가 보존된다.
- 겹치는 경계와 수직 교차가 동시에 있는 경우 겹침 실패가 반환된다.
- 오목 경계 한 행에 여러 구간이 있을 때 결정적으로 올바른 구간을 고른다.
- 저장된 parameter의 원본 3D 평가가 일치한다.
- stale·퇴화·후보 없음이 각각 명시적 실패다.

### 통과 기준
- 모호 후보를 지워 성공시키지 않는다.
- 성공한 점은 행 좌표와 원본 binding을 모두 보존한다.

### 진행 결과 (2026-11-13, 구현 완료)
- `ExitBoundaryIdentity`를 추가해 같은 2D 좌표·같은 숫자 parameter라도 원본 edge/제외변/절단선이 다르면 다른 후보로 유지한다. 비교에는 선택 parameter까지 포함한다.
- `SplitBoundarySnapshot.AddLineCandidate`와 `SplitRegionBoundary2D.AddLineCandidate`가 이 동일성 판정을 쓴다.
- `SplitRegionBoundary2D.IntersectLine`은 접선·겹침을 **다른 교차점이 있어도** `TangentOrOverlap`으로 즉시 실패시킨다.
- 자동 인출점 목표는 자식 외곽 극값이 아니라 그 행의 실제 스캔라인 발열 구간 끝이다. 시작은 생성기가 허용하는 첫 구간, 종료는 마지막 구간의 요청 측면 끝을 쓴다.
- 후보 선택은 `요구 측면 필터 → 목표 구간 끝 최근접 → 1e-6 동률의 서로 다른 provenance 모호성 실패` 순서다.
- 선택된 원본 CAD parameter의 3D 평가는 기존 `TryEvaluateExitSample` 경로가 그대로 수행한다(2D 재투영 없음).
- App 테스트 **190 통과**: 동일 CAD edge/parameter, 다른 edge, 다른 선택 parameter, CAD vs 절단선 provenance, 겹침+수직 교차, 오목 행의 실제 구간 끝 선택을 포함한다.
- WorkingSide 회귀 전 그룹 통과(split boundary 28, fixture 312, working-side 187 등). 실제 모델 2분할·저장·재로드 시나리오도 22/22 `Completed`였다.
- **1단계 재현 자산의 변화(4단계에서 분석):** B 세로 4분할 재현은 이제 원래의 `B-4 AnchorNotAtLaneEnd` 대신 `B-1 RequiredExtraLaneOutsideRegion`으로 거부된다. 자동 인출점이 실제 구간 끝을 쓰게 되면서 실패 지점이 이동한 것이며, 시나리오 기대 문구를 임의로 바꾸지 않고 4단계에서 원인을 판정했다(아래 4단계 진행 결과). 제외영역 재현은 20/20으로 그대로 통과한다.

---

## 4단계. 고정한 두 실패의 실제 원인 판정

**선행 조건:** 2·3단계 통과. 이 단계 없이 시나리오 기대값을 다시 확정하지 않는다.

### 대상
- 1단계 fixture
- `src\HeatingMold.Core\Geometry\Serpentine.cs`
- 자동 인출점 배정·표면 매핑 호출부

### 진단 기록 — 분할 실패(`AnchorNotAtLaneEnd`)
- 실패 자식, 절단 좌표, 시작/종료점의 정확 좌표·binding.
- 자동 배정된 행과 실제 생성기 첫 행.
- 첫 레인 스캔 구간과 시작점의 상대 위치.
- 첫 레인 끝 판정이 거부된 수치 근거.

### 진단 기록 — 표면 매핑 실패(`path leaves the selected surface domain`)
- 실패 조각 종류·좌표·지지 면.
- 정확 CAD 경계와 근사 폴리곤의 차이.
- 실제로 표면 밖인지, 판정 기준이 혼용된 것인지 근거.

### 처리 원칙
- 유효한 경로를 잘못 거부한 것이면 **해당 판정만** 고친다.
- 새 계약상 실제로 불가능한 형상이면 명시적 거부 + 기존 상태 보존을 회귀로 고정한다.
- 생성 규칙 자체를 바꿔야 하면 구현하지 말고 보고·승인받는다.
- **성공하는 다른 시나리오로 검증 대상을 바꾼 것을 원래 실패의 해결로 보고하지 않는다.**

### 통과 기준
- 두 실패 모두 원인이 설명된다.
- 각각 "수정 후 통과" 또는 "근거 있는 정상 거부"로 회귀에 남는다.

### 진행 결과 (2026-11-13, 완료)

**실패 1 — B 세로 4분할은 정상 거부로 판정**
- 3단계 이후 실패 지점이 원래 `B-4 AnchorNotAtLaneEnd`에서 `B-1 RequiredExtraLaneOutsideRegion`으로 이동했다.
- 실제 모델 경계로 자동 배정한 결과: B-1 `(329.934254,-173.664867)→(379.634254,-176.421829)`, B-2 `(391.78,-177.071972)→(466.33,-180.873877)`, B-3 `(473.43,-181.221536)→(547.98,-182.026687)`, B-4 `(551.79,-182.026687)→(601.49,-182.026687)`.
- B-1은 마지막 레인이 종료점 반대편에서 끝나 같은 피치로 레인을 한 줄 더 만들어야 하지만, 그 위치에 발열영역 구간이 없다. 마지막 레인 뒤집기·피치 변경·부분 분할을 하지 않는 현재 생성 규칙에서는 성립하지 않는 **정상 거부**다. 부모 영역·경로·길이·선택 인출점은 그대로 보존된다.
- `repro-b-four-split-anchor.json`의 `failureContains`를 `마지막 레인이 종료점 반대편`으로 갱신했고, 최신 DLL로 **18/18 Completed**를 확인했다. fixture에는 현재 실패 지점과 함께 `historicalFailurePoint: B-4`, `historicalFailureClass: AnchorNotAtLaneEnd`, 원인을 기록했다.

**실패 2 — 경계 위 정확한 점을 유효 경로로 잘못 거부한 판정 수정**
- 실패 조각은 B의 3/26 HeatingRun, 2D `(304.37,-132)-(630,-132)`, 실패 uv `(630,-132)`였다. `MapWorkingPointOntoFaces` direct와 `QueryWorkingSurface` global이 모두 Miss였고, 인접 `(629.95,-132)`는 Face 24에 direct/global 모두 Hit였다. 표면 내부 틈이 아니라 **선택 영역 경계 위 점을 네이티브 면 교차가 시작점으로 처리하지 못한 오판**이다.
- `MainWindow.RouteMapping.cs`의 `BuildSurfaceMapDelegate`에서 direct/global이 모두 Miss일 때만 `context.Boundary.Project(uv, ExitBoundaryConstraint.Tolerance)`로 정확한 경계 provenance를 구하고, 투영점이 요청 uv와 tolerance 이내로 같으면 `TryEvaluateExitSample`로 원본 edge parameter의 3D 점을 평가해 반환하도록 고쳤다. 내부 표면 틈을 최근접 점으로 대체하지 않는다.
- 수정 후 B 홈 재생성 성공: **26조각 / 12턴 / 1회로 / 총 4,687.678mm, 발열 13조각 / 4,255.385mm, 적용 외곽 27.7mm·직경 0.30mm**. A의 23조각/11턴/8,207.764mm는 보존됐다.
- `repro-b-exclusion-surface.json`은 실패 재현에서 성공 회귀 시나리오로 갱신했다: s17 `generate B`에 위 기대값, s18 `checkArea B` 같은 기대값, s19 `checkArea A` 보존. 최신 DLL로 **20/20 Completed**를 확인했다. fixture의 failure 2는 `resolved: true`와 기대값·원인으로 갱신했고, `RemainingFailureFixtureChecks`는 resolved 항목에서 마지막 일치 단계의 expect를 비교한다.

**검증**
- App 테스트 **190 통과**.
- WorkingSide 회귀 **EXITCODE=0**: 30 sidebar, 30 area highlight, 19 direct exit-point, 32 vertex edit, 28 split boundary, **340 remaining failure fixture**, 3 electrical refresh, 43 pattern state, 63 geometry rollback, 20 project load, 51 project load exclusion, 187 working-side. `CHECK-GROUP FAILED` 없음.
- 재현 1·2 실행 DLL: App `BD49288672C613B7F78C8F16B28C45522B70DC3AE54EDF4D55CB0C72F3875022`, Core `2D9CA87B3302C36EFB3A7D05C5B1C310A5491231085D3C013F938F72ABD0083E`, OccBridge `D5E5ECE7B37039437DD2BEF5C48F69BFC6A7300DD8F8073B8B801773991BE9AE`.

---

## 5단계. 간격 분석 누락·부분 성공 제거

**목적:** 표시 기능이 실제 요구(간격이 어긋난 위치와 수치 확인)를 만족하도록 분석 범위를 바로잡는다.

### 확정 결함(근거)
- `MainWindow.SpacingReview.cs` 221–236행이 **같은 설정 간격·직경 그룹 안에서만** 분석한다.
  A 14.7 / B 24.7처럼 설정이 다른 인접 영역 사이는 아예 비교하지 않는다.
- 표시 중 영역이라도 ID/길이 배열·확정 입력·native chain이 잘못되면 조용히 건너뛰고,
  남은 결과를 정상 분석처럼 게시한다.
- `RouteSpacingAnalyzer`는 단일 직경·방향 메타데이터만 받는다.
- 서로 다른 방향(가로/세로) 배치를 별도 상태로 구분하지 않는다.

### 대상
- `src\HeatingMold.Core\Geometry\RouteSpacingAnalyzer.cs`
- `src\HeatingMold.App\MainWindow.SpacingReview.cs`
- Core·App 테스트

### 작업
1. 같은 작업평면의 확정 발열선을 수집한다. 미리보기·초안은 제외하고,
   표시 중 경로는 **확정 당시** 간격·직경을 쓴다.
2. 설정 간격·직경이 다르다는 이유로 영역 간 비교를 끊지 않는다.
3. 분석 입력에 영역·조각 identity, 생성 방향, 확정 간격·직경을 보존한다.
4. 직경이 다른 쌍의 외곽 간격은 `중심선 사이 거리 − (d1 + d2) / 2`로 계산한다.
5. 방향이 다른 배치는 별도 상태로 표시하고, 같은 피치 정상·비정상을 억지로 판정하지 않는다.
6. 확정 경로 읽기 실패와 "정상 경로 없음"을 구분한다.
   일부만 분석한 결과를 전체 분석으로 표시하지 않는다.

### 필수 테스트
- 같은 FACE를 공유하는 다른 영역.
- 설정 간격이 다른 이웃 영역.
- 확정 직경이 다른 두 발열선.
- 방향이 다른 두 영역.
- 확정 경로 읽기 실패·잘못된 좌표.
- 분석 실패 후에도 기하·길이·전기 판정이 변하지 않는다.

### 통과 기준
- 설정이 다른 영역 사이도 분석된다.
- 누락·실패가 경고로 드러나고, 부분 결과가 전체로 게시되지 않는다.

### 진행 결과 (2026-11-13, 완료)

- 간격 분석을 설정 간격·직경 그룹 단위에서 **같은 작업평면 전체**로 넓혔다. 분석 입력은 영역 ID·조각 번호·
  확정 간격·직경·생성 방향을 보존하고, 결과는 방향 그룹별 쌍(min/max)을 제공한다.
- 생성 방향이 평행(0.1°, 180° 모듈로)한 레인끼리만 비교한다. 그룹 가입은 **구성원 전체와의 평행**을
  요구하므로, 대표 각도 하나와의 비교에서 생기던 전이 혼입(0°, +0.09°, −0.09°)이 없다.
  방향이 다른 배치뿐이면 `DirectionSeparated`로만 보고하고 수치 정상·비정상을 만들지 않는다.
- 직경이 다른 쌍의 외곽 간격은 `중심선 거리 − (d1 + d2) / 2`로 계산한다.
- 표시 중 영역의 확정 메타데이터(패턴·조각 ID/길이·확정 입력·native chain·좌표) 누락은 조용히
  건너뛰지 않고 사유를 모아 경고로 실패시킨다. 수집이 하나라도 실패하면 부분 결과를 게시하지 않는다.
  수집 로직은 `SpacingReviewCollector`로 분리해 App 테스트에서 직접 검증한다.
- 레거시 4인자 분석 API는 의미(모든 레인 동일 설정·방향)를 그대로 유지한다.
- 독립 리뷰 지적 2건을 수정했다. (1) 방향 그룹 전이 결함 → 구성원 전체 평행 조건 + 경계 테스트,
  (2) 마커 갱신 성공 전에 분석 지문을 게시하던 순서 → 성공 후 게시.
- 검증: Core 테스트 **245 통과**, App 테스트 **200 통과**, WorkingSide 회귀 **EXIT=0, 846 체크**
  (sidebar 30, area highlight 30, direct exit 19, vertex edit 32, split boundary 28, remaining fixture 340,
  electrical 3, pattern state 43, geometry rollback 63, project load 20, project load exclusion 51,
  working-side 187). 로드 DLL SHA-256: OccBridge `D5E5ECE7…E9AE`, App `F4840977…5B9F`,
  Core `C400AB9D…CC9E`.
- 검증 중 회귀 픽스처 하나가 `PatternDisplayed=true`인데 확정 생성 정보를 비워 둔 비현실적 상태임을
  확인해 픽스처를 실제 상태에 맞게 보강했다. 제품 규칙과 기대 문구는 완화하지 않았다.
- 미검증: 실제 마우스·화면 육안, 새 프로세스 복원, 외부 CAD 출력. 시각테스트 5개 전체는 §0.5에 따라
  이 단계 필수 범위가 아니다. `e2e-split-safety` B-1 기대값(4,280.265 mm)은 7단계에서 실측 재확정 대상이다.
  방향 분리는 요약·분석 결과로만 보고되고 화면 마커로는 그려지지 않는다(마커 정책은 6단계 범위).

---

## 6단계. 표시 정책·무효화·성능 완성

### 6-1. 사용자 결정 (2026-11-13 승인 완료)
- **사용자 확정: 레인 쌍마다 최소 간격 한 곳만 표시하는 현재 동작을 제품 정책으로 유지한다.**
  최대값·변동 구간 표시는 추가하지 않는다.
- 따라서 "위치와 간격 확인 기능"은 최소 위치·수치 확인 범위에서 완료로 판정한다.
- 검토 탭에 별도 간격 점검 구역을 만들지 않는다(기존 확정).

### 6-2. 기술 작업
대상: `MainWindow.SpacingReview.cs`, `MainWindow.RouteTransactions.cs`,
`MainWindow.ProjectLoad.cs`, `src\OccBridge\SpacingMarkerNative.inc`

1. 분석 지문에 모델·작업 컨텍스트·프레임, 영역·조각 identity, **중간점 포함 전체 경로 좌표**,
   확정 간격·직경·방향, 분석 정책 버전을 포함한다.
2. 지문을 만들기만 하지 말고 캐시 재사용·게시 시점에 **실제로 비교**한다.
3. native 데이터 수집·표시 변경은 UI 스레드에서 한다.
4. 대량 순수 2D 분석은 불변 스냅샷으로 백그라운드에서 수행한다.
5. 분석 도중 경로·모델이 바뀌면 이전 결과를 게시하지 않는다.
6. 영역 삭제·전체 초기화·프로젝트 복원·창 종료에서 이전 표시와 대기 작업을 정리한다.
7. 표시 실패는 이전 마커를 지우고 완료 상태에 경고만 남긴다. 확정 기하를 롤백하지 않는다.
8. 표본 간격을 임의로 늘리거나 일부 경로를 생략해 성능을 맞추지 않는다.

### 필수 테스트
- 끝점은 같고 중간 좌표만 바뀐 경우 이전 결과가 무효화된다.
- 먼저 시작한 분석이 늦게 끝나도 새 결과를 덮어쓰지 않는다.
- 삭제된 영역의 마커가 남지 않는다.
- 표시 실패 경고가 최종 완료 문구에 유지된다.
- 마커가 선택을 가로채지 않는다.
- 커밋 시간과 분석 시간을 분리해 계측한다.

### 진행 결과 (2026-11-13, 완료)
- 분석 지문을 정책 버전·모델 리비전·작업 컨텍스트 ID/리비전·작업평면 프레임·영역/조각 identity·
  확정 간격·직경·방향·색·**중간점 포함 전체 2D 좌표**로 구성했다(`SpacingAnalysisFingerprint`).
  지문은 만들기만 하지 않고 캐시 재사용과 게시 시점에 실제로 비교한다.
- 수집(native chain 조회·2D 투영)과 마커 교체는 UI 스레드에서, 순수 2D 분석·마커 후보 산출은
  불변 스냅샷을 받은 백그라운드 작업에서 수행한다. 게시 직전 세대 번호와 지문을 다시 대조해
  늦게 끝난 이전 결과를 버리고, 같은 지문의 직전 결과는 재분석 없이 재사용한다(`SpacingReviewCoordinator`).
- 영역 삭제·전체 초기화·모델 교체·프로젝트 복원이 지나는 공통 정리 지점과 창 종료에서
  대기 분석을 취소하고 마커를 지우며 지문·요약을 비운다. 표시 실패는 경고만 남기고 기하는 롤백하지 않는다.
  표본 간격 1 mm와 경로 생략 금지는 그대로다. 수집 시간과 분석 시간을 분리해 계측한다.
- 독립 리뷰 지적 3건 대응: (1) 분석 내부 반복이 취소를 확인하지 않던 중간 결함 → 간격 계산이
  취소 토큰을 받아 레인마다 확인, (2) 교체된 취소 토큰 소스 미정리 → 취소 후 Dispose,
  (3) 비동기 취소·늦은 완료·종료 후 콜백 통합 테스트 부재 → **미해결**(App 테스트가 창을 생성하지
  않는 제약 때문에 코디네이터 동기 검증과 Core 취소 경계 테스트로만 고정).
- 검증: Core 테스트 **246 통과**, App 테스트 **206 통과**, WorkingSide 회귀 **EXIT=0, 846 체크**
  (그룹 구성은 5단계와 동일, 그룹 실패 0). 로드 DLL SHA-256: OccBridge `D5E5ECE7…E9AE`,
  App `5CC2E2D7…D7AFE`, Core `877B7B93…B713E`.
- 미검증: 실제 마우스·화면 육안(마커 위치·가독성·백그라운드 전환 깜빡임), 대량 입력 체감 지연과
  시간 실측값, 새 프로세스 복원, 외부 CAD 출력. 시각테스트 5개 전체는 §0.5에 따라 이 단계 필수 범위가 아니다.

---

## 7단계. 시나리오 목적 보존·기대값 재측정

### 대상
`tests\HeatingMold.VisualTest\scenarios\`의 5개 시나리오.

### 현재 확인된 범위 변경(사실)
- `e2e-split-four`: 세로 B 4분할 → 가로 A 4분할로 대상이 바뀌었다.
- `e2e-margin-spacing-exit`: B 하단 제외영역 추가·재생성·침범 검사 블록이 제거되고
  초안 변경 테스트로 대체되었다.

계획 §3.5는 마진 제거 후 경계 실패 시 조건 조정을 허용한다.
그러나 **조건 조정과 원래 실패 해결은 다르다.** 실패 재현은 1·4단계에 별도로 남긴다.

### 작업
1. 2단계 수정 후 영향을 받는 길이·조각 수·분할 결과를 다시 측정한다.
2. 측정 전에 기대값을 고쳐 쓰지 않는다.
3. 각 시나리오의 검사 목적을 유지한다.
   - 전체 흐름: 생성·제외·초기화·꼭지점 편집·전기·분할·저장/재로드·DXF/STEP
   - 독립 영역: 한 영역 변경이 다른 영역 경로를 건드리지 않음
   - 조건 변형: 다른 간격·인출 측면
   - 분할 안전성: 거부·취소·절단선 binding 복원
   - 최대 4분할
4. 세로 4분할 실패와 하단 제외영역 실패는 **별도 재현 검사**로 유지한다.
5. 조건을 바꿨다면 이유와 줄어든 검증 범위를 결과에 기록한다.

### 통과 기준
- 5개를 새 실행 폴더에서 각 2회 `Status=Completed`.
- 경로·시각 등 비결정 항목을 제외한 단계 데이터가 두 실행에서 동일.
- schema 2·`marginMm` 거부가 유지된다.
- 실패 시 좌표뿐 아니라 길이·ID·binding이 보존된다.
- 분할 후 재생성·저장·복원 결과가 일관된다.

### 주의
단계 수가 같다는 사실은 검증 범위가 같다는 뜻이 아니다.

### 진행 결과 (2026-11-13, 완료)
- 5개 시나리오를 새 실행 폴더 2개(A/B)에서 각각 실행했다. 모두 `Status=Completed`,
  실패 단계 0: `e2e-full` 36/36, `e2e-multi-area` 54/54, `e2e-margin-spacing-exit` 59/59,
  `e2e-split-safety` 42/42, `e2e-split-four` 20/20.
- 결정성: 단계 이름·성공 여부·단계 데이터가 두 실행에서 동일하다. 차이는 산출물 경로와
  DXF 헤더 `$TDUPDATE`(시각) 때문에 생기는 파일 바이트 수(3~4 B)뿐이며, 엔티티·영역·회로·조각 수는 같다.
- 2단계 수정 후 실측으로 기대값을 재확정했다(측정 전에 값을 고치지 않았다).
  - `e2e-full` 3분할: A1 20조각/9턴/6,746.533 mm, A2 16/7/5,405.521, A3 24/11/6,250.376
    (2D 편차 1.251). 복원 A1도 같은 값이다.
  - `e2e-split-safety`: B-1 4,276.956 mm/발열 4,135.48, B-2 4,465.184/4,324.503.
  - `e2e-split-four` A 4분할: A-1 2,689.544 mm, A-2 16조각/7턴/5,395.236(발열 5,289.263),
    A-3 16/7/5,424.17(5,279.503), A-4 20/9/4,883.415(4,544.904). 2D 편차 2.006.
- schema 2·`marginMm` 거부 유지: App 테스트 `Load_RejectsSchema2WithMigrationMessage`가
  `schemaVersion`/`marginMm` 안내를 확인하며 전체 App 테스트가 통과한다.
- 실패 시 보존: `e2e-split-safety`의 거부·취소 단계가 영역·경로·길이·인출점 깊은 비교로 유지됨을 확인한다.
  분할 후 재생성·저장·복원은 `compareProject`로 일치한다.
- **줄어든 검증 범위 기록:** `e2e-split-four`는 세로 B 4분할에서 가로 A 4분할로 대상이 바뀌었고,
  실측 2D 편차가 2.006(보고 2.0168)으로 이전 B 기준 1.0931보다 나쁘다. 현재 제품 정책은
  절단 위치만 탐색하고 편차를 표시·확정하므로 이를 결함으로 되돌리지 않고 수치만 기록한다.
  `e2e-margin-spacing-exit`는 B 하단 제외영역 블록이 초안 변경 테스트로 대체된 상태를 유지한다.
- 7단계에서는 제품 코드를 바꾸지 않았다. 변경 대상은 시나리오 기대값 JSON 3개다.

---

## 8단계. 최종 검증·완료 보고

### 순서
1. 네이티브 Release/x64 빌드(출력 `x64\Release\`).
2. App·테스트 빌드.
3. 최신 DLL 동기화 후 실제 로드 경로·SHA 확인.
4. Core 테스트.
5. App 테스트.
6. `WorkingSide.Regression` 전체(그룹 요약 + `EXIT=0`).
7. 시나리오 5개 각 2회.
8. 독립 리뷰 → 수정 → 관련 검사 재실행.

### 보고에 포함할 것
- 변경 기능과 대상 파일.
- 실제 실행한 테스트 수·종료 코드·로그 위치.
- 실제 로드한 DLL 정보(경로·SHA-256).
- 1·4단계의 원래 두 실패를 어떻게 처리했는지(수정/정상 거부/미해결).
- 변경한 시나리오 조건과 줄어든 검증 범위.
- 분석/표시 성능 계측 결과.
- 승인 대기 항목과 미검증 항목.

실제 마우스 조작, 화면 가독성, 새 프로세스에서의 복원, 외부 CAD에서의 DXF/STEP 확인을
하지 않았다면 "미검증"으로 따로 적는다. `AGENTS.md`에는 확인한 사실만 저장하고,
저장 후 실제 파일 내용을 다시 읽어 검증한다.

### 진행 결과 (2026-11-13, 완료)
- 순서대로 실행했다. 네이티브 Release/x64 빌드 성공(`x64\Release\OccBridge.dll`,
  SHA-256 `D5E5ECE7…E9AE`), App·테스트 빌드 오류 0.
- 실제 로드 DLL: App 테스트 출력 `HeatingMold.App.dll` `13FCF6A6…3FDD`,
  `HeatingMold.Core.dll` `877B7B93…B713E`, `OccBridge.dll` `D5E5ECE7…E9AE`.
- 테스트: Core **246 통과/0 실패**(EXIT=0), App **206 통과/0 실패**(EXIT=0),
  WorkingSide **EXIT=0, 846 체크**, 그룹 실패 0(sidebar 30, area highlight 30, direct exit 19,
  vertex edit 32, split boundary 28, remaining fixture 340, electrical 3, pattern state 43,
  geometry rollback 63, project load 20, project load exclusion 51, working-side 187).
- 시나리오 5개를 새 폴더에서 각 2회 실행해 모두 `Status=Completed`, 실패 단계 0
  (36/54/59/42/20단계). 두 실행의 단계 데이터 차이는 산출물 경로와 DXF `$TDUPDATE`
  때문에 생기는 `dxf`/`path`/`bytes` 필드뿐이다.
- 독립 리뷰가 2건을 지적했고 모두 수정 후 관련 검사를 다시 실행했다.
  (1) 중간: 최소 간격 표본이 두 번째 레인에 있을 때도 첫 레인의 영역·색을 쓰던 문제 →
  표본이 놓인 레인의 소유 정보를 쓰도록 수정. (2) 낮음: 프로젝트 복원 완료 문구가
  간격 표시 실패도 "경로 길이 라벨" 실패로 표시하던 문제 → 실제 경고 문구를 그대로 표시.
  수정 후 App 206·WorkingSide EXIT=0·시나리오 10회를 다시 통과했다.
- 두 원래 실패의 처리: 세로 B 4분할은 마지막 레인 반대편에 발열 구간이 없어 **정상 거부**로
  판정하고 재현 시나리오 기대 문구를 고정했다. 경계 위 정확한 점의 표면 매핑 실패는
  경계 provenance로 **수정**했고 성공 회귀 시나리오로 갱신했다(4단계 기록).
- 성능: 수집/분석 시간을 분리하는 계측 필드만 구현했고 실측 수치는 이번 단계에서
  측정하지 않았다. 개선 배율을 주장하지 않는다.
- 승인 대기: 없음(6-1 확정 완료). 미검증: 실제 마우스 조작·화면 가독성·줌/DPI,
  새 프로세스에서의 복원, 외부 CAD에서 DXF/STEP 열기, 대량 입력에서의 체감 지연.

---

## 부록 A. 단계 순서 요약

| 순서 | 단계 | 선행 | 핵심 통과 기준 |
|---|---|---|---|
| 1 | 실패·DLL 고정 | — | 같은 입력으로 같은 실패 재현, 최신 DLL 증빙 |
| 2 | 간격/피치 혼용 제거 | 1 | Case1 5.30 / Case2 5.15, 일반·분할 동일 규칙 |
| 3 | 정확 교차 보강 | 1 | provenance 보존, 겹침 즉시 실패, 모호성 실패 |
| 4 | 원래 두 실패 판정 | 2, 3 | 원인 설명 + 수정 검증 또는 정상 거부 회귀 |
| 5 | 간격 분석 범위·부분 성공 | 2 | 다른 설정 영역 간 비교, 누락 경고 |
| 6 | 표시 정책·무효화·성능 | 5 + 사용자 결정 | 지문 실제 비교, 이전 결과 미게시, 기하 불변 |
| 7 | 시나리오 목적·기대값 | 2~6 | 5개 × 2회, 결정성, 실패 보존 |
| 8 | 최종 회귀·보고 | 전 단계 | 전 테스트 통과 + 미검증 분리 보고 |

**최우선 순서: 2단계(간격 계산 통일) → 3단계(정확 교차 보강) → 4단계(기존 실패 재판정).**
이 세 단계를 마치기 전에는 시나리오 기대값을 다시 확정하지 않는다.

---

## 부록 B. 승인 대기·미검증 항목

### 승인 대기
- 간격 표시를 레인 쌍별 최소 한 곳으로 축약하는 정책(6-1).
- 생성 규칙 자체를 바꿔야 하는 경우(4단계에서 발견 시).

### 미검증(자동검증과 분리)
- 실제 마우스로 분할·인출점 선택·꼭지점 드래그.
- 화면에서의 간격 마커·수치 가독성, 줌·DPI·창 크기 변화.
- 새 프로세스에서 프로젝트 복원.
- 외부 CAD에서 DXF/STEP 열기.
- 대형 다중 영역 프로젝트에서 분석·표시 시간.

---

## 부록 C. 검증 명령

```powershell
# 네이티브 (OutDir 필수)
& 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe' `
  src\OccBridge\OccBridge.vcxproj /p:Configuration=Release /p:Platform=x64 `
  /p:OutDir=D:\0.Development\2.HeatingMold\x64\Release\

# Core
dotnet test tests\HeatingMold.Core.Tests\HeatingMold.Core.Tests.csproj -c Release

# App (플랫폼 지정 필수)
dotnet build tests\HeatingMold.App.Tests\HeatingMold.App.Tests.csproj -c Release -p:Platform=x64
# DLL 복사 후
dotnet test tests\HeatingMold.App.Tests\HeatingMold.App.Tests.csproj -c Release -p:Platform=x64 --no-build
```

DLL 복사 대상
- `x64\Release\OccBridge.dll` → `tests\HeatingMold.App.Tests\bin\x64\Release\net8.0-windows\`
- `HeatingMold.App.dll`, `HeatingMold.Core.dll` → 같은 폴더 및 회귀 하네스 출력

```powershell
# 화면 회귀
powershell -NoProfile -ExecutionPolicy Bypass -File tests\WorkingSide.Regression\Run.ps1

# 시각테스트
powershell -NoProfile -ExecutionPolicy Bypass -File tests\HeatingMold.VisualTest\Run.ps1 `
  -Scenario <name> -RunDir <dir> -HoldSeconds 1 -TimeoutSeconds 600 -NoBuild
```
