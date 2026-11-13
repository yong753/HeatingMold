# 남은 작업 상세 구현계획

- 작성일: 2026-11-13
- 기준 커밋: `cb5cc60` (변경분 미커밋)
- 대상: 마진 기능 제거 및 발열사 외곽 간격 전환 이후 남은 4개 작업

> **2026-11-13 중간검토 후속:** 단계별 실행 순서와 통과 기준은 `docs\RemainingWork.Steps.md`를 따른다.
> 이 문서와 충돌하는 §1~§4 단계는 그 문서가 대체한다. 계약·검증 명령·문서화 규칙은 계속 유효하다.
> **진행 상태:** 1단계(실패 재현 자산)·2단계(분할 피치 통일)·3단계(정확 행 교차)·4단계(두 실패 원인 판정) 완료, 5단계(간격 분석 누락·부분 성공 제거) 진행 전. 상세 증빙은 Steps 문서에 기록한다.
> **검증 정책:** 단계별 통과 범위와 중간 단계 실패 처리 기준은 Steps 문서 §0.5·§0.6을 따른다.
> 시각테스트 5개를 각각 2회 통과시키는 기준은 7·8단계에 적용하며, 1~6단계 진행의 공통 선행조건이 아니다.

---

## 0. 현재 확정 상태

### 0.1 완료·검증된 것

| 항목 | 상태 |
|---|---|
| 배치 마진 기능 제거(UI·생성·제외 확장·분할·저장·캐시) | 완료 |
| 사용자 입력 = 발열사 **외곽** 간격(기본 5 mm) | 완료 |
| 내부 중심 피치 = 외곽 간격 + 전선 직경 (Case1 5.30 / Case2 5.15) | 완료 |
| 프로젝트 schema 7만 지원(구버전 명시 거부) | 완료 |
| 시각테스트 계약에서 `marginMm` 제거, `SpacingMm` 기본 5 | 완료 |
| Core `LaneGridSpec`/`LaneGridRow`/`LaneGridPartition` | 완료 |
| Core `RouteSpacingAnalyzer` (Version `route-spacing-v1`) | 완료 |
| 분할 탐색에 레인 격자 seed 연결 | 완료 |
| 분할 개수 bounded 재시도(계산 개수 → 최대 4) | 완료 |
| 1단계: 두 실패의 별도 재현 시나리오·fixture·회귀(현재 340건) | 완료 |
| 2단계: 분할 탐색의 외곽 간격/중심 피치 혼용 제거 | 완료 |
| 3단계: 정확 행 교차의 provenance·겹침·목표 구간 끝 처리 | 완료 |
| 4단계: 두 실패의 원인 판정·경계 위 점 표면 매핑 수정 | 완료 |

검증 수치
- Core 테스트: **238 / 238 통과**
- App 테스트: **190 / 190 통과** (2·3단계 신규 검사 포함)
- App Release/x64 빌드: 오류 0
- WorkingSide 회귀: 전 그룹 통과 (split boundary 28, remaining failure fixture 340, working-side 187 등)

### 0.2 변경하면 안 되는 계약

- 설계 기준은 확정 작업평면의 2D 좌표. 카메라 좌표·표면 UV는 설계 좌표가 아니다.
- 영역 하나당 연속된 회로 하나.
- 자동분할은 한 번에 최대 4개, 영구 부모·자식 관계 없음.
- 부모가 지정한 시작·종료 인출 측면 유지, 자동 인출점은 부모 선택 진행 범위 안.
- 마지막 레인만 뒤집기, 임의 레인 삭제, 피치 변경 금지.
- 모든 자식 준비 후 단일 원자적 commit. 실패·취소 시 기존 영역·경로·길이·ID·인출점 보존.
- 공유 발열전력·허용 전압·허용 전류, 실측 3D 길이 기반 전기 판정 불변.
- 검토 탭에 `발열사 간격 점검` 섹션을 만들지 않는다(사용자 확정).
- 제외영역이 적용영역 내부에 구멍을 만들거나 영역을 분리하면 명시적 거부.
- 실패를 숨기기 위해 허용치를 키우거나 기대값을 낮추지 않는다.

---

## 1. 정확 행-경계 교차 자동 인출점

### 1.1 문제

자동 인출점은 다음 순서로 만들어진다.

1. 마진 없는 생성 영역을 스캔해 첫·마지막 레인 행을 고른다.
2. 자식 외곽 다각형에서 그 행의 최좌·최우 극값을 구해 목표점을 만든다.
3. `boundary.Project(target, 5.0)`으로 가장 가까운 경계점을 찾는다.

3번이 최근접 투영이므로, 목표점이 정확 경계와 어긋나 있으면 결과 점의 **행(진행축) 좌표가 최대 약 1 mm 이동**할 수 있다. 그러면 그 점에서 시작하는 경로의 첫 레인이 격자 행과 어긋나고, 이후 분할 품질과 길이 균등화가 흔들린다.

또한 `SplitRegionBoundary2D.Project`는 후보를 하나만 받아 처리하므로, 정확 교차가 여러 개인 경우 선택 근거가 약하다.

### 1.2 목표

- 자동 배정 인출점은 **정해진 격자 행 위에 정확히** 놓인다.
- 원본 CAD identity·parameter, 제외변 identity, 절단선 identity를 그대로 보존한다.
- 수동 마우스 선택의 커서 허용 거리(5 mm)는 별개로 유지한다.

### 1.3 설계

**가. 정확 행 교차 질의 추가**

`src/OccBridge`에 이미 있는 `Bridge.IntersectExitEdgeLine(id, revision, reference, ax, ay, bx, by)`를 재사용한다. 이 API는 원점을 포함한 무한 직선 전체 교차를 반환하며 커서 tolerance 인자를 받지 않는다. 추가 네이티브 변경은 필요 없다.

`src/HeatingMold.App/SplitBoundarySnapshot.cs`에 다음을 추가한다.

```csharp
internal ExitBoundaryFailureReason AddLineHits(
    Vec2 a, Vec2 b, List<ExitBoundaryPoint> hits)
```

- 각 `CadInterval`에 대해 `Bridge.IntersectExitEdgeLine`을 호출한다.
- `Hit`이면 `IsInside`(반평면 + 제외 엄격 내부)로 자식 소속을 확인한다.
- binding은 항상 원본 trim(`Original.FirstParameter`/`LastParameter`)을 유지한다.
- `TangentOrOverlap`, `Ambiguous`, `StaleContext`, `DegenerateProjection`은 즉시 명시적 실패로 반환한다.
- `Miss`는 건너뛴다.

**나. 제외변·절단선 교차**

CAD가 아닌 경계는 네이티브 질의 없이 analytic으로 계산한다.

- 제외 사각형 4변: 행 직선과의 교점을 계산하고, 그 점이 자식 반평면 안이며 실제 제외변 구간 안일 때만 후보로 넣는다.
- 절단선: `SplitSegment`의 잘린 구간(A~B)과 행 직선의 교점을 계산한다. 교점이 구간 안이면 `SplitLineBinding`(full line A/B + 구간 first/last + parameter)을 그대로 만든다.

**다. 후보 선택**

`SplitExitPointPlanner.TryResolveBoundaryPoint`를 다음으로 바꾼다.

1. 행 직선을 만든다. 직선은 그 행의 sweep 좌표를 고정하고, 레인 방향은 자식 링의 최소·최대보다 1 mm 바깥까지 연장한다.
2. 정확 교차 후보를 모두 모은다.
3. 후보가 없으면 **명시적 실패**한다. 최근접 투영으로 대체하지 않는다.
4. 후보 중 (a) 요구 측면(낮은 쪽/높은 쪽)에 있고 (b) 목표 레인 좌표에 가장 가까운 것을 고른다.
   목표 레인 좌표는 자식 외곽의 최좌·최우가 아니라 **그 행의 실제 스캔라인 발열 구간 끝**이다
   (2026-11-13 사용자 확정: 제외영역으로 레인이 안쪽에서 끝나는 경우까지 반영).
5. 같은 거리(1e-6 이내)에 서로 다른 provenance 후보가 둘 이상이면 `Ambiguous`로 실패한다.

**라. 실패 조건**

- 접선·겹침, 같은 2D 좌표의 서로 다른 원본 parameter, stale 참조, 퇴화 투영, 후보 없음.
- 어떤 경우에도 허용치를 늘리거나 이웃 행으로 대체하지 않는다.

### 1.4 대상 파일

- `src/HeatingMold.App/SplitBoundarySnapshot.cs` — `AddLineHits`, 제외변·절단선 교차 헬퍼
- `src/HeatingMold.App/SplitExitPointPlanner.cs` — `TryResolveBoundaryPoint` 교체
- `src/HeatingMold.App/SplitRegionBoundary2D.cs` — 필요 시 행 교차 façade 추가
- `tests/HeatingMold.App.Tests/SplitExitPointPlannerTests.cs` — 정확 행 교차 단위 테스트
- `tests/WorkingSide.Regression/SplitBoundaryChecks.cs` — 실모델 회귀

네이티브는 변경 없음(`IntersectExitEdgeLine`이 이미 존재).

### 1.5 검증

단위
- 직선 경계에서 행 교차점이 정확히 행 위에 온다(행 좌표 오차 ≤ 1e-9).
- 곡선 경계에서 교차점이 원본 parameter로 평가되고 3D 위치가 일치한다.
- 제외변 교차, 절단선 교차의 identity가 보존된다.
- 접선·겹침·모호성·후보 없음이 각각 명시적 실패로 반환된다.
- 같은 행에 두 교차가 있는 경우 측면 선택이 결정적이다.

실모델(WorkingSide)
- 기본 예제 얼굴 `16,24,26`, FrameY, 외곽 간격 20 mm에서 2·4분할 자동 인출점이 행 위에 온다(행 좌표 오차 ≤ 1e-6).
- 기존 `SplitBoundaryChecks` 19건이 계속 통과한다.

### 1.6 리스크

- 곡선 구간에서 행 직선이 접선에 가까우면 교차가 사라질 수 있다. 이때는 실패가 정답이며, 행을 옮기지 않는다.
- `IntersectExitEdgeLine`은 tolerance 인자가 없으므로 수치적으로 붙는 교차는 `TangentOrOverlap`으로 실패할 수 있다. 이를 허용치 완화로 해결하지 않는다.

---

## 2. 간격 분석 결과의 뷰포트 위치·수치 표시

### 2.1 문제

`RouteSpacingAnalyzer`는 확정 경로의 국부 외곽 간격을 계산하지만, 결과를 볼 수단이 없다. 발주처 요구는 "지정 간격을 유지하기 어려운 구간이 있다면 해당 위치와 간격을 확인할 수 있도록" 하는 것이므로 표시가 필요하다.

### 2.2 제약

- **검토 탭에 별도 섹션을 만들지 않는다**(사용자 확정). 검토 탭은 전기 사양만 유지한다.
- 표시는 표시일 뿐이며 경로·길이·전기 판정을 바꾸지 않는다.
- 마우스 이동마다 전체 분석을 다시 하지 않는다.

### 2.3 설계

**가. 분석 시점**

- 확정 경로 commit이 성공한 뒤 1회 분석한다.
- 영역 삭제·분할·꼭지점 편집 등으로 확정 경로가 바뀌면 다시 분석한다.
- 모델·작업면·경로 지문이 바뀌면 이전 결과를 폐기한다.

**나. 입력 수집**

- 표시/분석용 좌표는 확정 native chain을 작업평면으로 투영해 얻는다.
- 전선 직경은 확정 시 사용한 Case에서 가져온다.
- 분석 대상은 `HeatingRun` 조각만이다. 턴·커넥터는 발열선 간격 계산에서 제외한다.

**다. 결과 모델**

```csharp
sealed class SpacingMarker
{
    public string AreaId;
    public int RunIndex;
    public int OtherRunIndex;
    public Vec2 At;              // 작업평면 좌표
    public double ConfiguredOuterGapMm;
    public double ActualOuterGapMm;
}
```

**라. 표시 세트**

기존 표시와 섞이지 않도록 **별도 native 세트**를 둔다.

- 마커: 작은 원 또는 짧은 수직 틱
- 치수선: 두 레인 사이를 잇는 얇은 선 + 수치 텍스트
- 레이어: Topmost, 선택 불가(`ComputeSelection` 비움)
- 교체는 원자적: 새 세트를 모두 만든 뒤 한 번에 교체

**마. 표시 정책**

- `RouteSpacingStatus.Empty`이면 아무것도 표시하지 않고 상태 문구만 남긴다.
- `InvalidInput`이면 표시하지 않고 완료 상태에 경고를 덧붙인다.
- 표시 실패는 확정 기하를 되돌리지 않는다. 기존 표시를 지우고 경고만 남긴다.
- "과도"의 수치 상한은 아직 확정되지 않았으므로, **임의 임계값으로 NG/OK를 만들지 않는다.** 설정 간격과 실제 간격의 차이만 보여준다.

**바. 성능**

- 분석은 확정 경로 단위로만 수행한다.
- 레인 수가 많은 경우 표본 간격(기본 1 mm)으로 제한한다.
- UI 스레드에서 대량 계산을 하지 않는다. 필요한 경우 계산만 백그라운드로 옮기고, 완료 시 지문을 다시 확인한다.

### 2.4 대상 파일

- 신규 `src/HeatingMold.App/MainWindow.SpacingReview.cs`
- `src/HeatingMold.App/MainWindow.RouteTransactions.cs` — commit 성공 후 분석 트리거
- `src/HeatingMold.App/MainWindow.ProjectLoad.cs` — 복원 후 분석 트리거
- `src/OccBridge/OccBridgeApi.h/.cpp` + `src/OccBridge/SpacingMarkerNative.inc`(신규) — 표시 세트 API
- `tests/HeatingMold.App.Tests/` — 지문 무효화·표시 실패 비전파 단위 테스트
- `tests/WorkingSide.Regression/` — 실모델 표시 회귀

### 2.5 검증

- 평행 레인에서 표시 수치가 분석기 결과와 일치한다.
- 영역 간 경계에서도 표시된다(같은 FACE 공유 영역 포함).
- 방향이 다른 구간은 동일 기준으로 판정하지 않고 별도 상태로 표시된다.
- 경로 변경 후 이전 마커가 남지 않는다.
- 표시 실패 시 확정 경로·길이·전기 판정이 변하지 않는다.
- 검토 탭에 간격 관련 섹션이 생기지 않는다(회귀 검사).

### 2.6 리스크

- 마커가 많으면 화면이 복잡해진다. 표시는 최소/최대 등 대표 위치로 제한할 수 있으나, 이는 사용자 확정이 필요하다.
- 레이어/Topmost 처리가 기존 길이 라벨과 충돌하지 않도록 세트를 완전히 분리해야 한다.

---

## 3. 시각테스트 시나리오 5개 갱신

### 3.1 문제

기존 시나리오는 `marginMm`와 중심 간격 의미의 `spacingMm`로 작성되어 있다. 마진이 제거되고 간격 의미가 바뀌었으므로, 그대로는 계약 검증에서 거부되거나 기대값이 어긋난다.

### 3.2 대상

- `tests/HeatingMold.VisualTest/scenarios/e2e-full.json`
- `tests/HeatingMold.VisualTest/scenarios/e2e-multi-area.json`
- `tests/HeatingMold.VisualTest/scenarios/e2e-margin-spacing-exit.json`
- `tests/HeatingMold.VisualTest/scenarios/e2e-split-safety.json`
- `tests/HeatingMold.VisualTest/scenarios/e2e-split-four.json`

### 3.3 설계

**가. 스키마**

- 시각테스트 `schemaVersion`을 **3**으로 올린다.
- 2는 명시적으로 거부한다(필드 의미가 바뀌었으므로 조용한 해석 금지).
- `marginMm` 필드는 어떤 형태로도 받지 않는다.
- `spacingMm`은 **발열사 외곽 간격**으로 정의하고 주석·문서에 명시한다.

**나. 값 재측정**

기존 값은 중심 간격이었으므로 그대로 쓸 수 없다.

- 기존 중심 간격 `S_old`는 새 의미에서 `외곽 = S_old − 직경`이 된다.
- 그러나 마진 제거로 레인 위상과 조각 수가 달라지므로, **기존 기대 조각 수·턴·길이는 모두 무효**다.
- 각 시나리오를 새 계약으로 실행해 실측값을 얻은 뒤 그 값을 기대값으로 고정한다. 실측 없이 추정값을 넣지 않는다.
- 순서는 **새 계약 준수 확인 → 실측 → 기대값 확정**이다. 실측 전에 기대값을 먼저 고쳐 쓰지 않는다(Steps 7단계).

**다. 단계별 수정**

1. 모든 `setAreaInputs`/`expectAreaInputs`에서 `marginMm` 제거.
2. `spacingMm`을 외곽 간격으로 재설정.
3. `expect`의 `pieces`/`turns`/`lengthMm`/`heatingPieces`/`heatingLengthMm`를 실측값으로 교체.
4. `circuitVerdicts`/`expectedWithinLimits`를 실측 판정으로 교체.
5. 분할 시나리오는 새 절단선 좌표·자식 길이로 교체.

**라. 계약 코드**

- `src/HeatingMold.App/VisualTesting/VisualScenario.cs`의 `SchemaVersion` 허용값을 3으로.
- 2 거부 문구: `시각테스트 스키마 2는 지원하지 않습니다 — marginMm 제거·외곽 간격 의미로 다시 작성하세요.`
- `VisualRouteSnapshot`의 `AppliedOuterGapMm`/`AppliedWireDiameterMm` 비교 유지.

### 3.4 검증

- 각 시나리오를 새 실행 폴더로 **2회** 실행해 2회 모두 `Status=Completed`.
- 두 실행의 단계 데이터가 동일(결정성).
- 스키마 2 파일이 명시적으로 거부되는 단위 테스트.
- `marginMm`가 남아 있으면 거부되는 단위 테스트.

### 3.5 리스크

- 마진 제거로 일부 영역이 경계에 닿아 생성이 실패할 수 있다. 이때 기대값을 낮추지 말고, 실패 원인을 기록하고 시나리오 조건(영역·인출점)을 조정한다.
- 분할 시나리오의 절단선 좌표는 레인 격자 seed 변경으로 달라질 수 있다.

---

## 4. 화면 회귀 종료 코드 이상

### 4.1 문제

`tests\WorkingSide.Regression`이 `CHECK-GROUP FAILED` 0건인데도 종료 코드 `-532462766`(0xE0434352, 미처리 관리 예외)을 반환한다. 로그도 프로젝트 로드 그룹의 `project load preserves the manually edited route flag` 직후에 끊긴다.

### 4.2 가설

1. `ProjectLoadChecks.RunExclusionOutlineCheck` 구간에서 예외가 발생했고, 그룹 래퍼 밖이라 잡히지 않았다.
2. stdout이 블록 버퍼링되어 마지막 출력이 유실됐다.

### 4.3 진단 절차

1. `Program.cs`의 그룹 실행 순서와 try/catch 범위를 확인한다.
2. `ProjectLoadChecks.Run` 전체를 try/catch로 감싸 예외 타입·메시지·스택을 **파일에 직접** 기록한다(`artifacts\ws-failure.txt`).
3. 각 그룹 종료 시 `Console.Out.Flush()`를 호출한다.
4. 하네스 실행은 `cmd /d /c "... > log 2>&1"` 형태로 한다(`*>`는 stderr를 오류로 승격시켜 스크립트를 중단시킨다).

### 4.4 예상 원인별 수정

- schema 7 관련 픽스처 오류: 픽스처를 갱신한다.
- 마진 제거로 영역 경계 준비 실패: 해당 픽스처의 영역/인출점 조건을 새 계약에 맞춘다.
- 종료 시 네이티브 정리 예외: 정리 순서를 조정하고 예외를 로그로 남긴다.

### 4.5 완료 기준

- 종료 코드 0.
- `CHECK-GROUP FAILED` 0건.
- 로그에 모든 그룹의 `... checks passed.` 요약 줄이 남는다.

---

## 5. 작업 순서와 검증 게이트

**실행 순서와 단계별 통과 기준은 `docs\RemainingWork.Steps.md` 부록 A를 따른다.**
이 표는 이 계획의 §1~§4가 어느 실행 단계에 대응하는지만 보여준다.

| 계획 항목 | 대응 단계 | 그 단계에서 요구하는 통과 범위 |
|---|---|---|
| (선행) 실패·실행 DLL 고정 | 1 | 두 실패 재현, DLL 증빙, 실패 전후 보존 검사 |
| (공통) 간격·중심 피치 통일 | 2 | Case1 5.30 / Case2 5.15, 일반·분할 동일 규칙 |
| §1 정확 행-경계 교차 | 3 | provenance 보존, 겹침 즉시 실패, 실제 구간 끝 목표 |
| (공통) 두 실패 원인 판정 | 4 | 원인 설명 + 수정 검증 또는 정상 거부 회귀 |
| §2 간격 표시 | 5, 6 | 다른 설정 영역 간 비교, 지문 실제 비교, 확정 기하 불변 |
| §3 시각테스트 갱신 | 7 | 새 계약으로 5개 시나리오 각 2회 `Completed` |
| §4 화면 회귀 종료 코드 | 8 | WorkingSide `EXIT=0`, 전 그룹 요약 출력 |

- 단계별 통과는 그 단계의 필수 테스트·통과 기준 충족을 뜻하며, 1~6단계에서 시각테스트 5개 전체를
  통과시키는 것은 공통 선행조건이 아니다(Steps §0.5).
- 각 단계는 빌드·집중 테스트를 통과한 뒤 다음으로 넘어간다. 대규모 변경을 한꺼번에 적용하지 않는다.

---

## 6. 공통 검증 명령

```powershell
# 네이티브
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
- `HeatingMold.App.dll`, `HeatingMold.Core.dll` → 같은 폴더
- 회귀 하네스도 같은 3종을 수동 복사해야 한다(`dotnet build`가 갱신하지 않는다).

시각테스트
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests\HeatingMold.VisualTest\Run.ps1 `
  -Scenario <name> -RunDir <dir> -HoldSeconds 1 -TimeoutSeconds 600 -NoBuild
```

---

## 7. 문서화·기록 규칙

- 완료된 항목만 `AGENTS.md`에 "검증됨"으로 기록한다.
- 자동 검증과 육안·마우스 확인을 분리해 기록한다.
- 실패한 시나리오·미검증 범위를 성공으로 쓰지 않는다.
- 허용치·임계값을 바꿨다면 근거와 함께 기록한다.
