# 고정 방향 OCCT 투영 기반 3D 경로 계산 전환 계획

> 작성: 계획 세션(읽기 전용). 이 문서는 **실행 계획**이며 구현 완료 보고가 아니다.
> 신규 파일·클래스·메서드 이름은 모두 **제안**이다. 현재 저장소에 존재한다는 의미가 아니다.
> 이번 계획 수립 과정에서 제품 코드·테스트·문서는 수정하지 않았고, 빌드·테스트·투영 시험도 실행하지 않았다.
> 어떤 투영도 실제로 실행하지 않았으므로 성공을 보장하지 않는다.

---

## 0. 목적과 범위

일반 발열경로 조각의 3D 표면 좌표를 **점별 raycast 보정**으로 구하던 계산을, **2D 입력 선분을 고정 작업 법선 방향으로 OCCT 투영**하는 계산으로 교체하는 것을 검토한다.

### 최종 구조

```text
기존 2D 레인·턴 생성
        ↓
입력 조각의 선분별 CAD Edge 생성
        ↓
확정 작업 법선의 반대 방향으로 OCCT 투영
        ↓
원본 지지면·진행 순서·전체 구간·깊이 선택 검증
        ↓
선택 인출점의 원본 edge/parameter 구속 확인
        ↓
오차 관리한 확정 3D 폴리라인 생성
        ↓
기존 스테이징 → 단일 원자적 커밋
        ↓
동일 확정 데이터로 길이·전기·표시·저장·DXF·STEP
```

### 바꾸지 않는 것

- 레인 피치, 제외영역 차감, 사행 순서, 마지막 추가 레인 규칙.
- 영역당 하나의 연속 회로.
- 선택 인출점의 원본 CAD edge/parameter.
- **선택 면 우선 → 해당 구간에 선택 면 결과가 없을 때만 전체 모델 보완**이라는 표면 선택 정책.
- 실패 시 기존 영역과 다른 영역을 보존하는 트랜잭션.
- 확정 3D 폴리라인과 실제 측정 길이를 사용하는 저장·출력 구조.
- 드래그 중 표시 전용 2D 미리보기.

### 이번 변경에 포함되지 않는 것

**미구현 상태인 '원본 CAD 곡선의 정확한 레인 행 교차'는 이 계획의 해결 대상이 아니다.** 투영 비교에서 2D 입력 자체의 경계 문제가 드러나면 별도 원인으로 분리해 보고한다.

---

## 1. 왜 단순 교체가 아닌가

방향 투영 기능 `BRepProj_Projection`은 실제로 사용할 수 있다. 다만 다음 기능까지 제공하지는 않는다.

1. 지정 방향의 **앞쪽 결과만** 선택.
2. 여러 깊이 중 제품 정책에 맞는 결과 선택.
3. 입력 전체가 빠짐없이 투영됐다는 판정.
4. 결과 조각마다 원본 지지 FACE 반환.
5. 원본 인출점 구속 및 회로 순서 보존.

공식 OCCT 7.9.3 구현을 확인한 결과, 이 클래스는 입력을 **방향의 양쪽으로 sweep한 뒤 Section**을 수행한다. 또한 `IsDone() == false`만으로는 정상 무교차와 내부 계산 실패를 구분할 수 없다.

따라서 역할을 나눈다.

| 역할 | 사용할 방식 |
|---|---|
| 기존 점별 방식과 비교하는 첫 시험 | 순수 `BRepProj_Projection` |
| 제품용 계산기 후보 | 유한 방향 sweep + `BRepAlgoAPI_Section` |
| 지지면 추적 | 최초 구현은 원본 FACE별로 계산하여 입력 단계에서 소속 확정 |
| 전체성·깊이·인출점·연속성 판정 | 프로젝트의 공통 검증 계층 |

직접 Section을 사용하는 방식도 **동일 계열의 표준 OCCT 연산**이지만, 방향 투영 wrapper와 동일 결과라는 보장은 없다. 둘의 결과를 실제로 비교해야 한다.

---

## 2. 단계별 실행계획

### 0단계 — 현재 작업과 재현 입력 고정

#### 작업

1. 현재 미커밋 변경을 그대로 보존한다.
   - 제외영역 레인 계산 수정(`LaneIntervalScan2D` 계열).
   - 평면 외곽 턴의 부분 구현(`PlanarExteriorTurnSupport`).
   - 관련 생성·복원 변경.
2. 사용자 JSON과 원본 STEP의 해시를 기록한다.
3. 재현은 원본이 아닌 별도 사본에서 수행한다.
4. 실행한 App/Core/OccBridge의 **실제 로딩 경로와 SHA-256**을 기록한다.
5. 변경 전 자동검증 결과와 기존 실패를 구분해 기록한다.

#### 재현 기준

- 사용자 파일: `C:\Users\yong753\Desktop\발열몰드 저장테스트\261116_Test2.json` (schema 7, importPolicy 2, 원본 `Mold_Cover_Trim.step`).
- 영역 A: 세로 배치, 외곽 간격 0.2 mm, 직경 0.3 mm, 중심 피치 0.5 mm.
- 인계된 2D 결과: 2,500레인 / 5,000조각.
- 확정 작업평면: origin `(-3966.6891533290936, -843.1964433803546, 1455.9999999778886)`, axisX `(0,0,-1)`, axisY `(0,1,0)`, normal `(1,0,0)`.
- 영역 A 시작점 `(-623.813917148922, 68.1962115594577)` (원본 edge 29, parameter −106.300559161514), 종료점 `(625.329310322716, 356.803556631355)` (edge 42, parameter −45.3293105336977).
- 다른 영역: 인계상 472조각. **이 값은 저장소 코드·fixture 어디에서도 확인되지 않았다. 실행 전 실제 개수부터 재확인한다.**

#### 다른 영역 보존 기준

조각 수만 비교하지 않는다. 기존 깊은 복사 검사 기능 `VisualRouteSnapshot`(`src\HeatingMold.App\VisualTesting\VisualRouteSnapshot.cs`)을 이용해 다음을 값으로 비교한다.

- 확정 2D/3D 좌표와 순서.
- native ID, 조각 종류, 레인 번호.
- 실제 길이, 지지 FACE.
- 인출점 좌표와 binding.
- 경계, 제외영역, 확정 입력, 수동 편집 상태.

공유 전력 재배분 같은 파생값은 경로 보존 비교에서 제외한다.

#### 통과 기준

원본 불변, 재현 입력, 실제 로딩 DLL, 변경 전 결과가 명확해야 다음 단계로 진행한다.

---

### 1단계 — 실패 조각만 순수 투영 비교

**제품 호출부는 아직 바꾸지 않는다.**

#### 대상

영역 A의 **537번째 조각**(zero-based 536), 발열레인.

```text
시작: (-489.813917148922,  74.3326455745711)
종료: (-489.813917148922, -36.1016837183455)
기존 실패 위치: (-489.81391714892175, -32.960668248954725)
```

실행자는 동일 입력으로 생성한 현재 2D 결과와 이 값이 일치하는지 먼저 확인한다. 조각 번호를 맞추려고 입력이나 경로를 수정하지 않는다.

#### 비교 시험

동일한 원본 면·작업 프레임·입력 선분으로 다음을 비교한다.

1. 기존 점별 매핑.
2. 순수 OCCT 방향 투영(`BRepProj_Projection`).
3. 유한 sweep + `BRepAlgoAPI_Section`.

기존 투영 helper(`BuildProjectedWorkingWire`)는 다음 기능을 포함하므로 이 시험에 그대로 쓰지 않는다.

- 투영 실패 후 점별 표본 폴백.
- 여러 결과 edge의 무조건 일괄 재연결.
- 지원 FACE가 없는 결과 저장.

#### 기록 항목

- 완료 여부와 OCCT 오류·경고.
- 결과 edge/wire 수.
- 입력 선분에서 각 결과가 덮는 범위.
- 투영 방향의 깊이 범위.
- 원본 지지 FACE.
- 입력 전체를 덮는지, 중간에 빠진 구간이 있는지.
- 계산 시간.

#### 판정

| 결과 | 후속 처리 |
|---|---|
| 동일 입력 전체가 올바른 깊이로 투영됨 | 제품용 계산기 프로토타입 진행 |
| 결과는 있지만 다른 깊이이거나 일부 구간만 존재 | 성공으로 처리하지 않고 원인 분석 |
| 두 곡선 방식 모두 필요한 구간을 만들지 못함 | 제품 교체 중단, 원본 trim·경계·2D 입력 대조 |
| 내부 계산 오류 | 틈으로 단정하거나 전역 공차를 키우지 않음 |

**이 한 조각의 성공은 전체 5,000조각의 해결을 뜻하지 않는다.**

---

### 2단계 — 부작용 없는 네이티브 계산기 구현

#### 대상 파일

| 파일 | 역할 |
|---|---|
| `src/OccBridge/RouteProjectionNative.inc` — 신규 제안 | 방향 투영·결과 검증·표본화 |
| `src/OccBridge/OccBridgeApi.h` | 계산 요청·결과 DTO와 공개 API |
| `src/OccBridge/OccBridgeApi.cpp` | C++/CLI 변환, 전역 include, 계산기 연결 |
| `src/OccBridge/WorkingSideNative.inc` | 기존 스테이징을 계속 재사용 |

새 `.inc`의 표준·OCCT 헤더는 `OccBridgeApi.cpp`의 익명 namespace 내부가 아니라 전역 include 위치에 둔다(과거 `anonymous-namespace::std` 모호성 함정).

#### 최초 구현 단위

**입력 선분 하나 × 원본 FACE 하나**로 계산한다.

이렇게 시작하면 다음 두 가지가 계산 전에 확정된다.

- 어느 입력 선분에서 나온 결과인지.
- 어느 원본 FACE 위의 결과인지.

전체 wire를 투영한 다음 불완전한 history로 지지면을 추측하는 구조는 최초 구현에서 제외한다.

성능을 위해 원본 FACE의 보수적 bounding box를 캐시하고, 입력 선분의 투영 범위에 도달할 수 없는 면은 계산에서 제외한다. 표시 mesh를 근거로 면을 제외해서는 안 된다.

#### 계산 순서

1. 확정 작업 프레임에서 입력 선분을 3D Edge로 만든다.
2. 기존 조회 방향인 `-workingNormal`과 기존 작업 컨텍스트의 `rayLength`를 사용한다.
3. Edge를 유한 sweep해 투영용 sheet를 만든다.
4. 후보인 원본 선택 FACE 각각과 Section을 수행한다.
5. 입력 진행 좌표와 깊이로 결과를 정리한다.
6. 선택 면이 덮지 못한 구간에 한해서 전체 모델 보완을 수행한다.
7. 전체 구간과 선택 결과를 검증한다.
8. 검증된 곡선에서 확정 폴리라인을 만든다.
9. immutable 결과만 반환한다.

계산기는 AIS 표시, native stage ID 발급, 확정 경로 변경을 하지 않는다. 최초 구현은 기존 UI/native 호출 스레드에서 직렬 실행하며, 별도의 native 백그라운드 실행을 도입하지 않는다.

#### 결과를 고르는 기준

입력 선분의 진행 좌표를 `s`, 투영 깊이를 `h`로 둔다.

```text
0 ≤ s ≤ 입력 선분 길이
0 ≤ h ≤ 기존 rayLength
```

여기서 진행 좌표는 **경로의 진행 위치**이며, 선택 인출점의 원본 CAD parameter와는 다른 값이다.

처리 규칙:

- 입력 진행 순서에 따라 결과를 정렬한다.
- 여러 양의 깊이가 있으면 선택 면 중 가장 가까운 결과를 선택한다.
- 선택 면 결과가 없는 구간에서만 전체 모델의 가장 가까운 결과를 선택한다.
- 계산 오류나 모호성을 전체 모델 보완으로 숨기지 않는다.
- 뒤쪽 결과를 앞쪽 결과로 사용하지 않는다.
- 실제 미피복 구간을 0.1 mm 연결 공차로 메우지 않는다.
- 서로 다른 결과를 끝점이 가깝다는 이유로 일괄 연결하지 않는다.

#### 구간 검증의 구현 범위

결과 곡선을 입력 진행 좌표로 되돌렸을 때 다음을 확인한다.

1. 진행 좌표의 극값·접힘 위치에서 구간 분할.
2. 결과 간 깊이 순서가 바뀌는 위치 확인.
3. 분할된 구간마다 선택 결과와 피복 범위 결정.
4. 입력 시작부터 끝까지 누락·역행 없이 덮는지 확인.

중간점 하나나 일정 간격 표본만으로 전체성을 판정하지 않는다.

다음은 명시적 실패로 반환한다.

- 단조 구간을 안정적으로 분리하지 못함.
- 교차 계산 미완료 또는 해석 불가능한 겹침.
- 원본 지지면을 확정하지 못함.
- 같은 깊이의 서로 다른 결과를 구분하지 못함.
- 남은 미피복 구간.
- 수치 계산 한계나 자원 한도 도달.

실제 결과에서 필요하지 않은 일반 곡선 해석 프레임워크부터 만들지는 않는다. 필요한 곡선 유형과 비용을 프로토타입에서 확인한다.

---

### 3단계 — 공통 계산 결과와 실패 계약 확정

#### 신규 제안

- `src/HeatingMold.App/ProjectedRouteGeometry.cs` — 확정 준비 결과를 보관하는 immutable 객체.
- `src/HeatingMold.App/MainWindow.RouteProjection.cs` — 생성과 편집이 함께 호출하는 준비 함수.

논리적 인터페이스(제안):

```csharp
bool TryPrepareRouteGeometry(
    CommittedRouteContext context,
    SerpentineRoutePiece piece,
    IReadOnlyDictionary<int, SurfaceMappedSample> exactAnchors,
    out PreparedRouteGeometry geometry,
    out RouteProjectionFailure failure);
```

실제 접근성·타입은 현재 프로젝트 구조에 맞춰 확정한다.

#### 성공 결과에 포함할 것

| 정보 | 용도 |
|---|---|
| 순서 있는 3D 점 | 기존 스테이징 입력 |
| 점별 원본 지지 FACE | 표면 검증·저장 |
| 입력 선분과 결과 구간의 대응 | 누락·역행 검사 |
| 선택한 깊이와 표면 선택 구분 | 잘못된 branch 방지 |
| 인출점 구속 확인 결과 | 원본 edge/parameter 보존 |
| 계산·검증 정책 버전 | 캐시·retain 혼용 방지 |
| 계산 시간과 후보 면 수 | 성능 진단 |

성공 결과는 외부에서 배열을 수정할 수 없게 한다.

#### 실패 종류

최소한 다음을 분리한다.

- 잘못되거나 오래된 작업 컨텍스트.
- OCCT 계산 오류(`KernelFailure`).
- 정상적인 무교차(`NoCoverage`).
- 부분 피복(`PartialCoverage`).
- 깊이·지지면 모호성(`Ambiguous`).
- 인출점 불일치.
- 표본화 실패.
- 수치·자원 한도.

현재의 `The path leaves the selected surface domain.`처럼 여러 원인을 하나로 표현하지 않는다.

---

### 4단계 — 생성·편집·캐시를 같은 경로로 연결

#### 생성 연결

`src/HeatingMold.App/MainWindow.Areas.cs`의 `StageCurvedRoutePiece`(일반 조각 3D 준비·스테이징)에서 현재 점별 strict 매핑 부분을 공통 준비 함수 호출로 교체한다. 이후의 지지면 포함 스테이징은 유지한다.

```text
공통 준비 함수
    ↓
검증된 점 + 원본 지지 FACE
    ↓
기존 StageSurfaceMappedRoutePieceWithFaces
    ↓
기존 트랜잭션
```

#### 편집 연결

`src/HeatingMold.App/MainWindow.VertexEdit.cs`의 `TryBuildVertexCandidate`(MouseUp 후보 검증)에 남아 있는 별도 점별 strict 매핑도 같은 준비 함수로 교체한다.

- MouseMove: 기존 2D 표시 전용 미리보기 유지.
- MouseUp: 변경 조각만 공통 준비.
- 커밋: 준비 결과를 재사용하고 같은 계산을 반복하지 않음.

#### 캐시·retain 연결

`src/HeatingMold.App/MainWindow.RouteTransactions.cs`(캐시·조각 유지·원자적 커밋).

1. 표본 배열만 저장하지 않고 검증 정보가 포함된 준비 결과를 저장한다.
2. 캐시와 retain 키에 새 계산 엔진·정책 버전을 포함한다.
3. 기존 모델·작업 컨텍스트·프레임·경계 binding·전체 좌표 조건은 유지한다.
4. 실패 후보는 확정 캐시에 넣지 않는다.
5. 모델 교체·컨텍스트 해제 시 관련 캐시를 폐기한다.

**엔진 변경을 이유로 모든 저장 경로를 stale로 만들거나 다른 영역까지 재생성하지 않는다.**

---

### 5단계 — 승인된 평면 외곽 턴을 별도 검증으로 완성

이 기능은 일반 투영 실패의 만능 폴백이 아니다.

#### 적용 조건

- `TurnConnector`만 대상.
- 실제 원본 표면이 같은 평면.
- 양 끝점은 검증된 실제 3D 점.
- 연결은 그 두 점 사이의 직선.
- 원본 CAD 외곽까지 전 구간 최대 돌출이 5 mm 이하.
- 내부 hole/gap, 곡면, 다른 평면, 계산 오류에는 적용하지 않음.

#### 필요한 보완

현재 부분 구현(`PlanarExteriorTurnSupport`)의 다음 판단을 교체한다.

| 현재 부족한 판단 | 필요한 판단 |
|---|---|
| 두 끝점 법선이 평행하면 평면으로 추정 | 원본 표면 종류와 실제 평면 비교 |
| 표본 폴리곤으로 외부 판정 | 원본 CAD 경계에 근거한 구간 판정 |
| 조회 가능한 경계를 모두 외곽으로 간주 | 내부 경계와 외곽 경계 구분 |
| 복원 시 중간점 한 번 확인 | 저장된 연결선 전체의 동일 정책 검증 |

단일 면의 outer wire만으로 여러 선택 면을 합친 영역의 외곽을 판정해서는 안 된다.

#### 호출 조건

일반 계산이 **정상적인 미피복(`NoCoverage`)** 을 반환한 턴에만 이 검사를 허용한다.

다음 실패에는 적용하지 않는다.

- OCCT 오류.
- 지지면 미확정.
- 다중 깊이 모호성.
- 인출점 불일치.
- 발열레인의 미피복.

생성·편집·복원은 같은 검증 함수를 사용한다. 조건을 증명하지 못하면 실패시키고 기존 경로를 보존한다.

---

### 6단계 — 저장·출력은 기존 authority 유지

현재 실제 프로젝트 schema은 **7**이다(`ProjectJsonStore.CurrentSchemaVersion` 계열).

저장 authority:

- 확정 3D 점.
- 점별 원본 지지 FACE.
- 실제 조각 길이.
- 원본 인출점 provenance.

#### 보존할 경로

| 파일 | 보존 사항 |
|---|---|
| `MainWindow.Project.cs` | 확정 native chain 저장 |
| `MainWindow.ProjectLoad.cs` | 저장된 3D chain을 그대로 복원 |
| `MainWindow.RouteExport.cs` | 확정 chain·길이 수집 |
| `MainWindow.DxfPlan.cs` | 확정 경로의 평면 표시 |
| `MainWindow.StepExport.cs` | 확정 3D 경로 출력 |
| `MainWindow.RouteLengthLabels.cs` | 확정 실측 길이 표시 |
| `MainWindow.Electrical.cs` | 확정 실측 길이로 계산 |

정상 프로젝트 로드에서 새 투영 계산기를 호출해 경로를 다시 만들지 않는다.

이번 구조는 기존 저장 형식으로 표현할 수 있으므로 **schema 변경 없이 진행하는 것을 원칙**으로 한다. 새 영구 필드가 반드시 필요하다는 근거가 생기면 schema 7에 조용히 추가하지 말고 변경 설계를 분리한다.

---

## 3. 수치 정책

| 항목 | 계획 |
|---|---|
| 방향 | 기존 `-workingNormal` |
| 투영 깊이 | 기존 컨텍스트의 유한 `rayLength` |
| 추가 fuzzy | 요청값 0, 커널 실제 적용값 기록. 자동 확대 금지 |
| 진행·깊이의 수치 비교 | OCCT 정밀도 기준을 별도 정책으로 고정 |
| 실제 피복 공백 | 0.1 mm 연결 공차로 메우지 않음 |
| 기존 지지면·연결 검증 | 기존 0.1 mm 정책 유지 |
| 곡선→폴리라인 오차 | 기존 0.05 mm 정책 유지 |
| 보강 조회 간격 | 기존 1 mm, **검증용으로만 사용** |
| 평면 외곽 턴 | 승인된 최대 돌출 5 mm |
| 수치 계산 한계 | 명시적 실패, 부분 성공 금지 |

구현자는 진행 구간 오차와 깊이 동률 판정에 임의의 `0.01`·`0.1` 등을 흩어 넣지 않는다.

원본 CAD의 공유 vertex 공차는 기존 국소 정책대로 취급한다. 이를 전역 연결 공차로 확대하지 않는다.

**1 mm 검증 간격을 확정 경로 점 간격으로 강제하지 않는다.** 직선은 두 점이면 충분하며, 불필요하게 저장·STEP 데이터를 늘리지 않아야 한다.

---

## 4. 필요한 OCCT API 상세

기준: 로컬 **OCCT 7.9.3** 헤더(`third_party\opencascade-7.9.3-vc14-64\inc\`)와 해당 버전 공식 구현.

### 4.1 입력 형상과 투영 sheet

| 역할 | API | 사용 방법·주의 |
|---|---|---|
| 입력 선분 생성 | `BRepBuilderAPI_MakeEdge(p0, p1)` | 작업평면 좌표를 기존 프레임으로 3D 변환한 두 점 사용. `IsDone()` 확인 |
| 유한 방향 sweep | `BRepPrimAPI_MakePrism(shape, gp_Vec, Copy, Canonize)` | `gp_Vec(direction) * rayLength`. Edge를 넣으면 FACE가 생성됨 |
| sweep 결과 확인 | `Build()`, `IsDone()`, `Shape()` | 유효한 결과인지 확인하고 사용 |
| sweep 대응 조회 | `Generated(sourceShape)` | 향후 묶음 계산 검토 시 사용 가능. 최초 선분별 구현에서는 역추적에 의존하지 않음 |
| 후보 면 범위 | `BRepBndLib::AddOptimal(face, box, false, true)` | triangulation 없이 원본 기하와 원본 공차를 반영한 보수적 범위 |
| 원본 위상 대응 | `TopExp::MapShapesAndAncestors(...)` | 실제 공유 edge와 원본 FACE 관계 확인 |

무한 prism(`gp_Dir` + `Inf=true`)은 사용하지 않는다. 제품의 기존 유한 조회 범위와 달라지기 때문이다.

### 4.2 첫 비교 시험용 방향 투영

```cpp
BRepProj_Projection projection(sourceEdge, targetShape, direction);
```

사용할 결과 API: `IsDone()`, `Init()`, `More()`, `Next()`, `Current()`, `Shape()`.

#### 반드시 지킬 해석

- `IsDone()`만으로 입력 전체 성공이라고 판정하지 않는다.
- 반환 wire 하나만 임의로 고르지 않는다.
- 방향 반대편 결과가 섞일 수 있다.
- `Generated()`·`Couple()`·`Ancestor()`가 이 클래스에 있다고 가정하지 않는다.
- 이 시험에서 기존 점별 폴백을 호출하지 않는다.

### 4.3 제품용 후보의 핵심 교차 연산

`BRepAlgoAPI_Section` 권장 호출 순서(개념 코드, 완성 구현 아님):

```cpp
// 개념 코드: 단일 원본 FACE와 단일 입력 선분의 sheet.
TopTools_ListOfShape arguments;
arguments.Append(originalFace);

TopTools_ListOfShape tools;
tools.Append(projectionSheet);

BRepAlgoAPI_Section section;
section.SetArguments(arguments);
section.SetTools(tools);

section.SetNonDestructive(Standard_True);
section.SetToFillHistory(Standard_True);
section.SetRunParallel(Standard_False);
section.SetUseOBB(Standard_True);
section.SetFuzzyValue(0.0);

section.Approximation(Standard_True);
section.ComputePCurveOn1(Standard_True);
section.ComputePCurveOn2(Standard_True);

section.Build();

if (!section.IsDone() || section.HasErrors())
{
    // KernelFailure
}

// 성공했지만 EDGE가 없다면 이 FACE와는 정상 무교차.
// 결과 EDGE가 있어도 전체 피복 성공 판정은 아직 아님.
```

| 설정 | 이유 |
|---|---|
| `SetNonDestructive(true)` | 원본 CAD subshape 변경 방지 |
| `SetToFillHistory(true)` | 결과 관계의 진단·검증 자료 확보 |
| `SetRunParallel(false)` | 최초 구현의 재현성·진단 단순화 |
| `SetUseOBB(true)` | 교차 후보 처리의 범위 최적화 |
| `Approximation(true)` | 방향 투영 wrapper와 같은 계열의 곡선 생성 설정 |
| 양쪽 pcurve 생성 | 원본 면 trim과 투영 sheet상의 관계 확인 |

근사 곡선을 얻었다고 무조건 허용하지 않는다. 원본 지지면 잔차와 표본화 오차는 별도로 검증한다.

오류·경고 수집: `HasErrors()`, `HasWarnings()`, `DumpErrors()`, `DumpWarnings()`, `GetReport()`.

`Shape()` 호출 자체가 성공 여부를 대신 확인해 주지 않는다.

참고: 두-형상 생성자와 `Init1/Init2`는 7.9.3에서 Obsolete이므로 `SetArguments`/`SetTools`를 우선 사용한다.

### 4.4 History API의 사용 한계

사용 가능한 API: `Generated(shape)`, `Modified(shape)`, `IsDeleted(shape)`, `History()`, `HasAncestorFaceOn1(edge, face)`, `HasAncestorFaceOn2(edge, face)`.

그러나 ancestor 조회는 **새 교차곡선에서 생성된 edge**에 한정되며, common edge에는 보장되지 않는다.

따라서:

- 여러 history 결과를 합쳐 원본 FACE를 추측하지 않는다.
- 원본 FACE별 계산에서는 해당 FACE가 소속의 기본 근거다.
- ancestor 결과는 가능한 경우 교차 확인에 사용한다.
- 공유 edge의 중복 결과는 실제 원본 위상 관계로 확인한다.
- 같은 좌표라는 이유만으로 서로 다른 원본 면을 합치지 않는다.

`SimplifyResult()`는 최초 구현에서 호출하지 않는다. edge/face 통합이 구간·소속 대응을 복잡하게 만들 수 있다.

### 4.5 곡선·parameter·orientation

| 역할 | API | 주의 |
|---|---|---|
| 3D 곡선 평가 | `BRepAdaptor_Curve(edge)` | location이 반영된 평가 |
| 면의 pcurve 기반 평가 | `BRepAdaptor_Curve(edge, face)` | pcurve가 없으면 실패 가능 |
| 유효 범위 | `FirstParameter()`, `LastParameter()` | 원본 trim 범위 사용 |
| 점·미분 | `Value()`, `D1()` 등 | 진행 방향·깊이 분석 |
| 연속성 분할 | `NbIntervals()`, `Intervals()` | 필요한 연속성 구간으로 분리 |
| 원본 3D 곡선 | `BRep_Tool::Curve(...)` | location 미적용 실수 주의 |
| 면 위 2D 곡선 | `BRep_Tool::CurveOnSurface(...)` | null 여부, trim, parameter 대응 확인 |
| parameter 대응 상태 | `SameParameter()`, `SameRange()` | 3D 곡선과 pcurve의 동일 parameter를 무조건 가정하지 않음 |
| 원본 공차 | `BRep_Tool::Tolerance(...)` | 전역 공차 확대 근거로 쓰지 않음 |

parameter 증가 방향과 입력 경로 진행 방향이 같다고 가정하지 않는다. 결과는 입력 선분의 진행 좌표로 정렬해야 한다.

### 4.6 결과 구간·깊이 분석

#### 곡선 교차

`Geom2dInt_GInter`

- 명시적인 `IntRes2d_Domain`을 사용한다.
- `IsDone()` 확인 후 결과를 읽는다.
- `NbPoints()` / `Point(i)`, `NbSegments()` / `Segment(i)`.
- 상속 `IntRes2d_Intersection`은 `IsEmpty()`, `SetReversedParameters(bool)`도 제공하며, NotDone/OutOfRange에서 예외가 발생할 수 있다.

투영 sheet의 진행·깊이 좌표로 곡선을 표현할 때 **새로운 근사 곡선으로 refit하지 않고**, 기존 parameter를 유지하는 adaptor를 사용한다.

겹침 segment가 나오거나 교차 해석이 불완전하면 성공으로 넘기지 않는다.

#### BSpline 구간 분석

`GeomConvert_BSplineCurveToBezierCurve` — `NbArcs()`, `Arc(i)`, `Knots(...)`.

필요한 BSpline 구간의 범위·진행성 분석에 사용할 수 있다. 이는 새 fitting이 아닌 분해다.

다만 rational weight, 낮은 연속성, 특이 구간까지 자동으로 해결해 주는 API는 아니다. 필요한 범위를 안정적으로 처리하지 못하면 명시적 실패와 전환 중단 사유로 기록한다.

### 4.7 확정 폴리라인 생성

#### 우선 사용할 표본화

`GCPnts_UniformDeflection`:

```cpp
GCPnts_UniformDeflection sampler(
    curveAdaptor,
    chordError,
    firstParameter,
    lastParameter,
    Standard_True);
```

- C2 구간으로 먼저 분리한다.
- `IsDone()` 확인.
- `NbPoints()`, `Parameter(i)`, `Value(i)` 사용.
- 필요한 입력 진행 방향으로 다시 정렬.
- 인출점·면 전환·구간 경계점을 반드시 보존.

`Deflection()`이 반환하는 값은 설정값이지 실측 최대오차가 아니다.

#### 제한적으로 사용할 표본화

`GCPnts_QuasiUniformDeflection`(기본 연속성 `GeomAbs_C1`).

낮은 연속성 구간에서 검토할 수 있지만, 성공했다고 전체 최대편차나 표면 구간 전체성을 증명했다고 해서는 안 된다.

#### 길이

- 진단용 곡선 길이: `GCPnts_AbscissaPoint::Length(...)`.
- 제품 길이: 기존 스테이징에서 확정 폴리라인을 측정한 값.

두 길이를 전기 계산·표시·저장 사이에서 혼용하지 않는다.

### 4.8 지지면 검증과 평면 턴

| 역할 | API |
|---|---|
| 실제 표면 종류 | `BRepAdaptor_Surface::GetType()` |
| 실제 평면 | `BRepAdaptor_Surface::Plane()` |
| 원본 surface | `BRep_Tool::Surface(...)` |
| 표면 잔차 | `GeomAPI_ProjectPointOnSurf` |
| trim 내부·경계 판정 | `BRepTopAdaptor_FClass2d` |
| 기존 trimmed-face 거리 확인 | `BRepExtrema_DistShapeShape` |
| 단일 FACE의 outer wire | `BRepTools::OuterWire(face)` |
| 결과 형상 검사 | `BRepCheck_Analyzer` |

기존 지원면 검증에는 trimmed-face 거리만으로 유효점을 오판했던 문제를 보완한 로직(`PointOnSupportFaceNative`)이 있다. 이 로직을 제거하거나 공차 확대로 대체하지 않는다.

`OuterWire()`는 단일 FACE 기준이므로, 여러 면을 합친 영역의 외곽 판정으로 그대로 사용하지 않는다.

### 4.9 이번 방향에 사용하지 않을 API

표면 법선 투영인 `BRepOffsetAPI_NormalProjection`은 `Couple()`, `Ancestor()`, `Generated()`를 갖고 있지만 투영 방향이 제품의 고정 작업 법선과 다르다. provenance API가 편리하다는 이유로 바꾸면 설계 2D 위치와 피치가 달라질 수 있어 이번 계획에서는 사용하지 않는다.

다음은 정상 경로의 실패 보완책으로 사용하지 않는다.

- 최근접 점 스냅.
- 좌표 미세 이동.
- fuzzy·공차 자동 확대.
- 무조건 Sewing.
- 점별 raycast 재시도.
- 여러 결과 wire의 무조건 재연결.

### 4.10 링크

`src\OccBridge\OccBridge.vcxproj`의 Release|x64 링크 목록에는 `TKMath, TKG2d, TKG3d, TKGeomBase, TKGeomAlgo, TKBRep, TKTopAlgo, TKBool, TKShHealing, TKMesh, TKPrim, TKOffset, TKFeat, TKBO, TKDESTEP, TKXSBase, TKCDF, TKLCAF, TKCAF, TKXCAF, TKService, TKV3d, TKOpenGl, TKHLR`가 이미 포함되어 있다. 새 projection/Section/Prism에 필요한 라이브러리는 추정으로 추가하지 말고 실제 unresolved symbol로 판단한다.

---

## 5. 검증 계획

### 신규 테스트 위치(제안)

| 파일 | 검증 |
|---|---|
| `tests/WorkingSide.Regression/FixedDirectionProjectionChecks.cs` | 실제 OCCT 투영·깊이·지지면·피복 |
| `tests/HeatingMold.App.Tests/ProjectedRouteGeometryTests.cs` | immutable 결과, 실패 구분, 캐시 버전 |
| `GeometryRollbackChecks.cs` | 새 계산 실패 시 전 영역 보존 |
| `VertexEditChecks.cs` | 생성·MouseUp 공통 계산과 준비 결과 재사용 |
| `ProjectLoadChecks.cs` | 저장 3D 그대로 복원, 불필요한 재투영 없음 |
| `SupportFaceChecks.cs` | 기존 지원면 오판 수정 유지 |

### 필수 사례

| 구분 | 통과 기준 |
|---|---|
| 평면·경사·곡면 | 동일 2D 위치를 유지한 올바른 결과 |
| 실제 surface gap | 잘못 연결하거나 부분 성공하지 않음 |
| 1 mm보다 좁은 틈 | 표본 간격만으로 안전하다고 판정하지 않음 |
| 앞·뒤 여러 표면 | 기존 선택 면 우선·양의 최근접 정책 |
| 같은 2D, 다른 깊이 | binding·지지면 혼동 없음 |
| 공유 edge·seam·주기 곡선 | 원본 identity·trim·방향 유지 |
| 오래된 컨텍스트 | 계산·게시 거부 |
| 일부 조각 실패·취소 | 기존 영역과 다른 영역 깊은 보존 |
| 생성 후 편집 | 같은 계산기 사용, 중복 계산 방지 |
| 저장 후 재열기 | 확정 좌표·지지면·길이 유지 |
| DXF·STEP·전기·라벨 | 동일 확정 데이터 사용 |

### 사용자 파일 종단 검증

1. 동일 저장 인출점과 0.2 mm 입력으로 A 전체 생성.
2. 모든 조각의 순서·지지면·길이 확인.
3. 다른 영역의 깊은 보존 확인(실제 조각 수 재확인 후).
4. A 실패 경로에서도 다른 영역과 기존 문서 보존 확인.
5. 별도 파일로 저장·재열기.
6. DXF·STEP 출력과 기존 read-back 검사.
7. 실제 마우스·화면 확인은 자동검증과 별도로 기록.

전체 생성에 실패하면 첫 실패 위치와 원인을 보고하며, 일부 성공 조각을 커밋하지 않는다.

---

## 6. 빌드·실행 순서

실행 에이전트가 수행할 순서:

1. 네이티브를 절대 `OutDir`인 저장소의 `x64\Release\`에 빌드.
2. 관리 솔루션·테스트 빌드.
3. 관리 빌드 후 최신 `OccBridge.dll`을 실제 회귀·테스트·앱 실행 위치에 다시 반영.
4. 실제 실행 위치의 App/Core/OccBridge 해시 확인.
5. Core·App 테스트를 빌드 없이 실행.
6. WorkingSide 회귀 실행.
7. 지정한 실행 파일로 시각테스트 실행.

### 기존 스크립트의 범위

`tests\WorkingSide.Regression\Run.ps1`은 네이티브·솔루션 빌드와 WorkingSide 회귀를 수행한다. **이 스크립트만으로 Core·App 단위 테스트까지 실행됐다고 보고하면 안 된다.**

`WorkingSide.Regression.csproj`는 App/Core/OccBridge를 모두 `x64\WorkingSideBuild\`에서 참조하므로, App/Core 코드 변경 시에도 이 폴더의 DLL을 새로 만들어야 한다. 관리 빌드가 오래된 `OccBridge.dll`을 그 폴더로 복사하는 함정이 이미 확인되어 있다. 최신 `x64\Release\OccBridge.dll`을 다시 복사한 뒤 빌드 없이 실행한다.

시각테스트는 명시적인 실행 파일과 새 결과 폴더를 사용한다.

```powershell
powershell -ExecutionPolicy Bypass `
  -File tests\HeatingMold.VisualTest\Run.ps1 `
  -Scenario e2e-full `
  -AppExe "<검증한 출력 폴더>\HeatingMold.App.exe" `
  -ModelPath "<저장소>\resource\Mold_Cover_Trim.step" `
  -RunDir "<새 실행 폴더>" `
  -HoldSeconds 1 `
  -TimeoutSeconds 600
```

기존 5개 시나리오(`e2e-full`, `e2e-multi-area`, `e2e-margin-spacing-exit`, `e2e-split-safety`, `e2e-split-four`)를 실행하되, 현재 알려진 `e2e-full`·`e2e-split-four`의 기존 기대값 불일치는 변경 전 기준선과 분리한다. 새 계산 결과를 독립적으로 확인하기 전에 기대값을 맞춰 바꾸지 않는다.

`Run.ps1`은 `$ErrorActionPreference='Stop'`이므로, 의도된 `[Error]` 출력이 있는 실패경로 검사는 cmd로 출력을 병합해 실행한다.

---

## 7. 전환·중단 기준

### 제품 전환 조건

- 실패 조각이 같은 입력·올바른 깊이로 해결됨.
- 실제 틈과 모호성을 잘못 성공시키지 않음.
- 원본 CAD가 변경되지 않음.
- 전체 A 생성과 다른 영역 깊은 보존 확인.
- 생성·편집이 같은 준비 결과를 사용.
- 저장·복원·출력·전기·라벨의 authority 유지.
- 변경 영향 회귀와 독립 리뷰 통과.
- 후보 면 수, Section 횟수, 시간, 메모리 실측 완료.

현재 성능 승인 수치는 정해져 있지 않다. 따라서 속도 개선 배율이나 전체 소요 시간을 약속하지 않고 실측 결과로 전환을 판단한다.

### 즉시 중단할 경우

1. 실패 조각이 완전한 `[0, L]` 단일 branch로 복원되지 않음.
2. section edge 하나라도 target face와 source segment sheet의 양쪽 ancestor를 유일하게 확정하지 못함.
3. 겹침·접선·동일 depth 복수 branch 때문에 selected/global 우선순위를 결정할 수 없음.
4. 0.1 mm를 사용하지 않고는 source coverage를 연속으로 증명할 수 없음.
5. A 전체 처리 시간이 승인된 예산을 넘거나 전면 모델 Section 없이는 동작하지 않음.
6. hole·다중깊이·원본 변형 없음 시험 중 하나라도 잘못 성공함.
7. 생성과 MouseUp 편집이 같은 prepare packet을 사용하지 못함.

추가로 다음 경우에도 제품 호출부 교체를 중단한다.

- 입력 전체 피복에 공차 확대나 좌표 이동이 필요함.
- 지지면이나 깊이를 추측해야 함.
- 정상적인 곡면·공유 경계 회귀가 실패함.
- 원래 구조보다 복잡한 범용 해석기를 만들어야만 진행 가능함.
- 계산량을 줄이기 위해 면·조각·레인을 누락해야 함.
- 다른 영역이나 저장된 확정 경로를 재생성해야 함.

**핵심은 'OCCT 투영을 호출해 보니 성공했다'가 아니라, 투영 결과를 기존 제품 계약에 맞게 확정할 수 있는지 검증하는 것이다.** 그 검증이 통과하면 점별 재매핑과 우회 경로를 일반 생성 경로에서 제거한다.

---

## 8. 참고: 현재 코드에서 확인한 사실

| 항목 | 사실 |
|---|---|
| 프로젝트 schema | 7 (`src\HeatingMold.App\Persistence\ProjectJsonStore.cs`) |
| 저장 DTO 필드 | `CommittedChains[].Points`, `SupportFaceIds`, `NativePieceIds`, `RoutePieceLengths`, `ChainsAvailable`, `ChainError` (`ProjectDocument.cs:105-119, 201-208`) |
| 저장 authority | `CaptureCommittedChains` (`MainWindow.Project.cs:310-400`) |
| 정상 load | `StageRestoredChains`가 저장 3D/support를 그대로 staging, 재투영 없음 (`MainWindow.ProjectLoad.cs:577-676`, 길이 비교 `640-646`, commit `679-713`) |
| 출력 수집 | `CollectExportCircuits` (`MainWindow.RouteExport.cs:21-147`) |
| 교체 대상 | `MainWindow.Areas.cs:506-572`(생성), `MainWindow.VertexEdit.cs:516-531`(편집) |
| 기존 방향 투영 helper | `WorkingSideNative.inc` `BuildProjectedWorkingWire` — 표본 폴백·전체 edge 재연결·support 미기록 포함, 그대로 재사용 금지 |
| 지원면 검증 | `PointOnSupportFaceNative` — trimmed 거리 실패 시 surface 투영 + trim IN/ON 교차 확인, 유지 |
| 깊은 보존 비교 | `VisualRouteSnapshot` (`src\HeatingMold.App\VisualTesting\VisualRouteSnapshot.cs`) |

### 아직 확인되지 않은 것

- 사용자 파일의 다른 영역 실제 조각 수(인계값 472는 근거 없음).
- 실패 조각이 순수 투영으로 복원되는지.
- A 전체 5,000조각의 처리 시간·메모리.
- 다른 영역 깊은 보존의 실제 결과.
