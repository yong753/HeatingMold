# Mold.stp 중복면 분석 및 정리

작성일: 2026-10-25
대상: `resource/Mold.stp` → `resource/Mold_Solids.stp`
관련 코드: `src/HeatingMold.App/OcctEnv.cs`, `tools/step-cleanup/`

---

## 1. 요약

`Mold.stp`에는 솔리드 30개와 함께 **자유 곡면 바디 2개**(`SURFACE.135` 86면, `SURFACE.142` 4면)가
들어 있다. 이 곡면 바디가 솔리드 외피와 **오차 0으로 겹쳐** 있어, 뷰어/선택기에서 같은 위치에
면이 두 개씩 보이고 픽 대상이 불안정했다.

정리 후 기본 로드 모델을 `Mold_Solids.stp`(솔리드 30개 / **574면** / **중복쌍 0**)로 교체했다.
원본 파일은 보존하고, 표면 표현을 제품 구조에서 분리하는 **텍스트 레벨 1개 엔티티 삭제**로 만들었다.
OCCT로 다시 읽고 쓰는 방식은 `supplemental geometry`(모델 좌표계)를 잃어버려 채택하지 않았다.

| | 원본 | 새 기본 모델 |
|---|---|---|
| 파일 | `Mold.stp` (2,500,626 B) | `Mold_Solids.stp` (2,500,497 B) |
| 면 수 | 664 | 574 (−90) |
| 솔리드 / shell | 30 / 32 | 30 / 30 |
| 중복 면 쌍 | 17 | **0** |
| 모델 좌표계 | supplemental geometry | supplemental geometry (동일) |

---

## 2. 최초 질문과 답

> "79번 cylinder와 598번 cylinder가 겹쳐 보이는데 왜 면이 두 개인가?"

두 면은 **동일한 원통면**이지만 **서로 다른 바디**에 속한다.

| 항목 | #79 | #598 |
|---|---|---|
| 소속 | solid 1 (shell 1, 닫힌 솔리드) | **솔리드 없음** — shell 31 (열린 면 바디) |
| 반지름 | 4303.000000 | 4303.000000 |
| 축 위치 | (1787.941468, -4229.815659, -7247.110042) | 동일 (좌표차 1e-6) |
| 축 방향 | (-0.0095376, -0.8231352, 0.5677653) | 동일 |
| UV u (각도) | 180.802° ~ 182.500° | 동일 |
| UV v (길이) | −450.00 ~ −322.33 | **−627.50** ~ −322.33 |
| 면적 | 15,802.94 | 38,362.91 |
| orientation | FORWARD | REVERSED |
| 엣지 수 | 5 | 13 |

기하 검증 결과:

- 반지름 차 0, 축 방향 내적 1.000000, **축 선 간 거리 7.896e-07**
- 81개 샘플점 전부 상대 원통 위에 오차 0으로 안착 → **오프셋 없음**
- 두 면의 교차 면적 = **15,802.939112** = #79 면적 그대로 → #79는 #598에 **완전히 포함**
- UV 중점 법선 내적 = **−1.000000** → 등지고(back-to-back) 붙어 있음
- 공유 위상 엣지 0개 (다른 바디이므로 당연)

즉 "면이 두 개"가 아니라 **바디가 두 개**이고, 그 두 바디의 외피가 정확히 겹친 상태다.

---

## 3. 원본 STEP 구조

헤더:

```
FILE_NAME('DT24-020 FRONT INTERIOR PANEL_LSE24_240202.stp')
originating_system: SIEMENS PLM Software NX 12.0
FILE_SCHEMA: AUTOMOTIVE_DESIGN (AP214)
```

엔티티 집계:

| 엔티티 | 수 |
|---|---|
| `MANIFOLD_SOLID_BREP` | 30 |
| `SHELL_BASED_SURFACE_MODEL` | **2** |
| `OPEN_SHELL` | **2** |
| `CLOSED_SHELL` | 30 |
| `ADVANCED_FACE` | 664 |
| `CYLINDRICAL_SURFACE` | 208 |

문제의 두 엔티티:

```
#38=SHELL_BASED_SURFACE_MODEL('SURFACE.135',(#40));   → OPEN_SHELL #40, 86면
#39=SHELL_BASED_SURFACE_MODEL('SURFACE.142',(#41));   → OPEN_SHELL #41,  4면
```

제품 구조 연결:

```
#13=MANIFOLD_SURFACE_SHAPE_REPRESENTATION('abc5370A8B7btla-None',(#38,#39),#37474);
#23=SHAPE_REPRESENTATION_RELATIONSHIP('None','...',#36,#13);   ← 이 연결이 문제
#25=ADVANCED_BREP_SHAPE_REPRESENTATION('...',(#3818 ... #3847),#37474);  ← 솔리드 30개
```

`#13`은 `#23`을 통해서만 제품 구조에 연결된다. `#13`이나 `#23`을 참조하는 다른 엔티티는 없다.

### 곡면 바디 2개의 성격

| shell | 면 수 | 면적 (mm²) | 열린 경계 엣지 | 성격 |
|---|---|---|---|---|
| 31 (SURFACE.135) | 86 | 1,428,998.8 | 42 | 광범위한 자유 곡면 패치 |
| 32 (SURFACE.142) | 4 | 1,295,235.1 | 38 | **bbox 기준 직교 평면 4장** |

`SURFACE.142`의 4개 평면은 모델 경계에 정확히 붙어 있다:

```
x = 557.719 / 1947.719     (법선 ±X)
y = -2200.561 / -1270.561  (법선 ±Y)
```

어떤 솔리드와도 접촉하지 않는다(NX의 bounding/reference body 흔적으로 추정 — 정황 근거).

이 4장이 모델 전체 표면적의 **32%**를 차지한다.

### 솔리드와의 겹침 정도

열린 shell 90면을 3×3 UV 샘플 + `BRepClass3d_SolidClassifier`(tol 1e-4)로 분류:

- 솔리드 경계면과 완전 일치: **37면** (면적 445,684.2 mm²)
- 솔리드 내부: 2면
- 솔리드 밖(고유 형상): **51면**

즉 단순 중복이 아니다. 일부는 솔리드 외피를 그대로 복사한 것이고, 일부는 솔리드에 없는 형상이다.

### 완전 중복 쌍 목록 (17쌍)

샘플점이 3개 이상 일치하는 "동일 트림" 쌍. **전부 solid 1 ↔ 곡면 바디**이고
**솔리드끼리의 중복은 0쌍**이다.

```
#17  BSpline   A=  31.21  ↔ #601  BSpline   A=  31.23
#19  BSpline   A= 168.79  ↔ #648  BSpline   A= 175.13
#23  Cylinder A=  11.40  ↔ #629  Cylinder  A=  11.32
#26  BSpline   A=2032.47  ↔ #649  BSpline   A=2032.47
#33  BSpline   A=  26.68  ↔ #650  BSpline   A=  26.68
#38  Extrusion A=21731.32 ↔ #654  Extrusion A=21731.25
#41  Cylinder  A= 190.45  ↔ #578  Cylinder  A= 190.45
#45  Plane     A=1725.01  ↔ #577  Plane     A=1725.01
#46  Sphere    A=   6.88  ↔ #580  Sphere    A=   6.88
#48  Plane     A= 158.42  ↔ #574  Plane     A= 158.42
#58  BSpline   A=  33.22  ↔ #584  BSpline   A=  34.05
#59  Plane     A=  34.20  ↔ #632  Plane     A=  34.20
#61  BSpline   A= 717.59  ↔ #651  BSpline   A= 717.60
#62  BSpline   A=   4.71  ↔ #653  BSpline   A=   4.71
#63  Cylinder  A= 120.60  ↔ #579  Cylinder  A= 120.60
...
```

#79/#598은 여기 잡히지 않는다. 트림 범위가 달라 샘플 격자가 일치하지 않기 때문이며,
따라서 **17쌍은 하한값**이다.

---

## 4. 정리 방식 8종 실험

`tools/step-cleanup/diagnostics/cleanuptest.cpp`로 각 변형을 실제 생성하고 측정했다.

| 변형 | 내용 | 면수 | 총면적 | 중복쌍 | 유효성 | 판정 |
|---|---|---|---|---|---|---|
| 원본 | — | 664 | 3,987,854.0 | 17 | YES | — |
| V1 | 솔리드만 | 574 | 1,263,620.1 | 0 | YES | 형상 판단 필요 |
| V2 | bbox 평면 4장만 제거 | 660 | 2,692,618.9 | 17 | YES | 부분 개선 |
| V3 | V1 + `UnifySameDomain` | **505** | 1,263,620.1 | 0 | YES | 무손실 병합 |
| V4 | 전체 `UnifySameDomain` | 575 | 3,987,853.3 | — | **no** | 사용 금지 |
| V5 | V1 + `ShapeFix_Shape` | 574 | 1,263,620.2 | — | YES | 효과 없음 |
| V6 | V3 + `ShapeFix_Shape` | 505 | 1,263,620.2 | — | YES | V3와 거의 동일 |
| V7 | 솔리드 `BRepBuilderAPI_Sewing` | 574 | — | — | **no** | **솔리드 붕괴** |
| V8 | 전체 Sewing | 664 | — | — | **no** | 사용 금지 |

### 판정 근거

**V3 (솔리드 UnifySameDomain)**
- 면 574 → 505 (69면 병합, 12% 감소)
- 면적 `1,263,620.1` **완전 동일**
- 부피 1,843,326.2 → 1,843,407.8 (**+0.004%**)
- 유효성 통과 → 형상 보존된 순수 정리

**V7 (Sewing)은 정리 도구가 아니다**
- 솔리드 30개가 **shell 30개로 붕괴** (solids = 0)
- 유효성 실패
- `BRepBuilderAPI_Sewing`은 형상 재구성 도구이지 클린업 도구가 아니다

**V4 (전체 Unify)**
- orphan face 68개, 유효성 실패
- 바디 경계를 넘어 병합을 시도하면 위상이 깨진다

**곡면 바디는 어떤 tolerance로도 닫히지 않는다**

```
shell 31: tol 1e-4 / 1e-3 / 1e-2 / 5e-2 / 1e-1 → 모두 openEdges=42, faces=86
shell 32: tol 1e-4 / 1e-3 / 1e-2 / 5e-2 / 1e-1 → 모두 openEdges=38, faces=4
```

원래 열린 패치로 만들어진 것이므로 "닫아서 솔리드로 승격"은 대상이 아니다.

---

## 5. 채택 방식: 텍스트 레벨 엔티티 제거

### 왜 OCCT 재출력(V1)이 아니라 텍스트 편집인가

OCCT로 `STEPControl_Writer` 재출력하면 파일 구조가 새로 만들어지면서
**`supplemental geometry` 표현(`#10`/`#11`)이 사라진다.**

`ComputeAutoAxes()`는 이 표현을 최우선으로 사용해 모델 좌표계를 잡는다. 사라지면
관성 주축(principal inertia axes)으로 폴백하므로 **모델 방향이 달라진다.**

측정 결과:

| 파일 | axes source | 비고 |
|---|---|---|
| `Mold.stp` | supplemental geometry | 기준 |
| OCCT 재출력 V1 | **principal inertia axes** | 방향 변경 ❌ |
| 텍스트 편집 | **supplemental geometry** | 기준과 동일 ✅ |

### 실제 변경 내용

`#13`(표면 표현)을 참조하는 `#23`(관계 엔티티) 한 개만 제거한다.

```
#13=MANIFOLD_SURFACE_SHAPE_REPRESENTATION('abc5370A8B7btla-None',(#38,#39),#37474);
#23=SHAPE_REPRESENTATION_RELATIONSHIP('None',
'relationship between abc5370A8B7btla-None and abc5370A8B7btla-None',#36,
#13);                        ← 이 3줄(129 bytes) 삭제
```

- 나머지 엔티티는 **바이트 단위로 그대로** 유지된다
- 삭제 후에도 파일은 유효한 STEP이다 (빈 리스트를 만들지 않음)
- `#38`/`#39` 이하 곡면 엔티티는 참조되지 않는 상태로 남지만 리더가 무시한다

검증 결과 (`Mold_Solids.stp`):

```
### axes source: supplemental geometry
### totals: solids=30 shells=30 faces=574
### face types: Plane=287 Cylinder=189 Cone=54 Sphere=3 Torus=8 BSpline=29 Extrusion=4
coincident-face-pairs=0  (solid<->solid=0, solid<->surface=0)
```

---

## 6. 적용 내역

| 파일 | 변경 |
|---|---|
| `resource/Mold_Solids.stp` | 신규. 솔리드 30개 / 574면 (2,500,497 B) |
| `resource/Mold.stp` | 보존 (원본, 664면) |
| `src/HeatingMold.App/OcctEnv.cs` | `FindMoldStp()`가 `Mold_Solids.stp` 우선, 없으면 `Mold.stp` 폴백. 산출물 `resource\` 폴더도 탐색 |
| `src/HeatingMold.App/HeatingMold.App.csproj` | `resource\Mold_Solids.stp`를 `Content` + `CopyToOutputDirectory=PreserveNewest`로 출력에 포함 |
| `tools/step-cleanup/remove-surface-bodies.ps1` | 위 변환 스크립트 (재현용) |
| `tools/step-cleanup/diagnostics/` | 분석 도구 3종 + build/run 스크립트 |

출력 확인: `src/HeatingMold.App/bin/x64/Release/net8.0-windows/resource/Mold_Solids.stp` (2,500,497 B)

---

## 7. 재현 방법

```powershell
# 1) 기본 모델 재생성 (원본 → 정리본)
cd D:\0.Development\2.HeatingMold
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\tools\step-cleanup\remove-surface-bodies.ps1 `
  -In .\resource\Mold.stp -Out .\resource\Mold_Solids.stp

# 2) 진단 도구 빌드 (최초 1회)
cd tools\step-cleanup\diagnostics
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1

# 3) 중복 면 검사
powershell -NoProfile -ExecutionPolicy Bypass -File .\run.ps1 `
  -Tool dupcheck -Args "..\..\..\resource\Mold_Solids.stp"

# 4) 특정 면 상세 비교 (79 vs 598)
powershell -NoProfile -ExecutionPolicy Bypass -File .\run.ps1 `
  -Tool faceinspect -Args "..\..\..\resource\Mold.stp","79","598"

# 5) 정리 변형 8종 벤치마크
powershell -NoProfile -ExecutionPolicy Bypass -File .\run.ps1 `
  -Tool cleanuptest -Args "..\..\..\resource\Mold.stp",".\out"
```

### 도구 설명

| 도구 | 용도 |
|---|---|
| `faceinspect` | 앱과 동일한 파이프라인(로드→축 정렬→면 인덱스)으로 전 면 덤프. 두 면의 기하 비교(반지름/축거리/샘플 일치/공통면적/법선/위상 엣지), 솔리드·shell 표, 동일 원통 그룹핑, 열린 shell 면의 솔리드 대비 분류 |
| `dupcheck` | 일반 중복 면 검출기. 면마다 3×3 UV 샘플 → 공간 해시 → 3점 이상 일치하는 면 쌍 보고. 바디 조합(솔리드↔솔리드 / 솔리드↔곡면)별 집계 |
| `cleanuptest` | 정리 변형 8종 적용 + 지표 측정 + 각 변형을 STEP으로 출력 |

---

## 8. 영향과 주의사항

**면 인덱스가 바뀐다**
곡면 바디 90면이 빠지므로 `TopExp_Explorer` 순서가 달라진다. 기존에 664면 기준으로
기록한 인덱스(예: 597, 598)는 새 기본 모델에 존재하지 않는다.

**테스트 영향**
`tests/PatternRegion.Repro/Program.cs`는 `resource/Mold.stp`를 직접 열고
`Faces > 600`을 확인하며 면 {18, 74, 597, 598}을 사용한다. 이 테스트는 원본을 보므로
**영향 없음**이지만, 대상 모델을 정리본으로 바꾸면 597/598이 사라져 실패한다.

**솔리드에도 자유 엣지가 남아 있다**
정리 후에도 `openEdges = 7`이다. 완전 밀폐가 아니므로 두께/오프셋 연산 전 확인이 필요하다.

**추가 정리 여지 (미적용)**
- `V3`(솔리드 `UnifySameDomain`)는 면적·부피 변화 없이 505면까지 줄어든다. 기본 모델에는
  적용하지 않았다. 원본 형상 대비 위상이 달라지는 것을 피하기 위해서이며, 필요하면
  `cleanuptest` 결과물(`v3_unify_solids.stp`)로 검토 가능하다.
- 곡면 바디 51면은 솔리드에 없는 고유 형상이다. 폐기했지만, 별도 기준면이 필요한
  워크플로가 생기면 원본에서 추출해야 한다.

**미해결**
- `SURFACE.135`가 설계 기준면인지 익스포트 잔여물인지는 STEP만으로 확정하지 못했다.
  근거는 양쪽 다 있다(고유 형상 51면 / bbox 기준면 동반). NX 원본 확인이 필요하다.
- 솔리드 2~30 사이의 일치(예: `#90` solid2 ↔ `#114` solid3, R=6 원통 동일)는
  조립 접촉면으로 판단해 그대로 두었다. 지우면 바디가 합쳐진다.
