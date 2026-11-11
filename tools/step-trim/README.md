# step-trim

제품(Item)은 그대로 두고, 제품의 실제 외곽선에서 **확정한 작업평면 기준 100 mm 바깥**에서 덮개(Mold)를 잘라내는 독립 C++ 도구입니다.
기존 HeatingMold 앱·솔루션·`src\OccBridge`는 참조하지 않으며, 원본 STEP은 읽기만 합니다.

## 전제 (검사 모드에서 먼저 확인)

| 대상 | 이름 | 기대 형상 |
| --- | --- | --- |
| 제품 | `Item` | 닫힌 솔리드 1개 (투영 점유 판정과 체적 보존 검증에 필요) |
| 덮개 | `Mold` | 열린 서피스 쉘 (두께/솔리드 생성 금지) |

- 이름은 소문자로 비교하며 각각 정확히 1개여야 합니다. 없거나 중복이면 중단합니다.
- 덮개가 참조(조립 인스턴스)로만 존재하고 다른 곳에서도 참조되면 정의를 바꿔 다른 인스턴스까지 바뀌므로 중단합니다.
- `Item`이 닫힌 솔리드가 아니면 투영 외곽 추출이 성립하지 않으므로 중단합니다.

## 빌드

OCCT `third_party\opencascade-7.9.3-vc14-64`(inc, win64\vc14\lib)와 Visual Studio 2022 Community의 vcvars64를 사용합니다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\step-trim\build.ps1
```

산출물: `tools\step-trim\bin\step-trim.exe`

## 실행

`run.ps1`은 OCCT 런타임 DLL 경로를 준비한 뒤 exe를 호출합니다.

```powershell
powershell -NoProfile -Command "& .\tools\step-trim\run.ps1 -ToolArgs --mode,inspect,--input,resource\Mold_Item.step"
```

### CLI

| 인자 | 의미 |
| --- | --- |
| `--mode inspect` | 구조/이름/색/조립/형상 통계/tolerance/자유경계/주축/축별 여유만 보고. 파일을 쓰지 않음 |
| `--mode trim` | 실제 절단. `--output`, `--origin`, `--normal` 필수 |
| `--mode offset-probe` | 합성 400×800 사각형으로 100 mm 확장 API 자체를 검증 (`--input`은 형식상 필요) |
| `--input <path>` | 입력 STEP |
| `--output <path>` | 출력 STEP. 입력과 같은 경로면 거부 |
| `--margin <mm>` | 기본 100. 작업평면 위의 평면 거리 |
| `--origin x,y,z` | 작업평면 원점 (월드 좌표) |
| `--normal x,y,z` | 작업평면 법선 |
| `--xaxis x,y,z` | 작업평면 X축 (법선에 정사영해 직교화) |
| `--debug-tools N`, `--debug-outline` | 진단용. 평면 분할에 곡선을 앞에서 N개만 사용 / 외곽 wire를 덤프 |

**작업평면을 도구가 추정하지 않습니다.** `--origin`/`--normal`이 없거나 무효(영벡터, 법선과 X축 평행 등)면 절단을 거부합니다.
`--margin`은 평면 거리로만 계산합니다. 곡면을 따른 거리는 지원하지 않습니다.

## 파이프라인

1. XDE(`STEPCAFControl_Reader`)로 이름/색/레이어/조립 구조를 유지한 채 읽고, 입력 SHA-256·OCCT 버전·선언 정밀도를 보고합니다.
2. **투영 외곽**: 바운딩박스나 최대면 경계가 아니라 실제 투영 실루엣을 사용합니다.
   - `HLRBRep_Algo` + `HLRBRep_HLRToShape::CompoundOfEdges(OutLine, visible, In3d=true)`로 곡면 실루엣을, 인접 두 면의 외향 법선 부호가 시선과 반대/접선인 모서리로 평면 모서리 실루엣을 모읍니다.
   - `BRepProj_Projection`으로 작업평면에 정투영(투영 후 평면 이탈 0.000000 mm 확인). 점으로 붕괴한 곡선은 제품 최대 tolerance를 하한으로 판정해 제외하고, 그 외 실패는 중단합니다.
   - `BOPAlgo_Builder`로 평면을 분할하고, 각 셀 내부점에서 `IntCurvesFace_ShapeIntersector`로 법선 방향 제품 교차를 검사해 제품 투영 셀만 병합합니다. 셀 내부점은 무게중심→bbox 중심→격자→삼각분할 순으로 찾습니다(비볼록 셀의 무게중심은 외부일 수 있음).
   - 복수 외곽, 미닫힘 wire, 중첩 곡선 구분 불가, 셀 면적 합이 평면 면적과 불일치하면 중단합니다.
3. `ShapeUpgrade_UnifySameDomain` + `ShapeFix_Wire`로 외곽을 정리하되 면적 변화가 1e-6 mm²를 넘으면 중단합니다.
4. `BRepOffsetAPI_MakeOffset`로 바깥 100 mm 확장. 방향은 부호 추정이 아니라 포함관계·면적·경계 최소거리로 확정합니다.
   offset API는 모서리를 원호로 만들기 때문에, 곧바로 **모서리 원호를 인접 두 직선의 연장 교점으로 교체**합니다(사용자 확정: 모서리는 직각). 원호는 중간 산출물이며 최종 경계에 남기지 않습니다. 직선 두 개가 만나는 모서리만 교체하고, 그 밖의 모서리는 사유와 함께 보고합니다. 결과는 둥근 경계를 포함해야 하고 늘어난 면적은 모서리 삼각형 합을 넘을 수 없으며, 직선 구간은 여전히 정확히 margin 떨어져 있어야 합니다.
5. 확장 외곽을 법선 방향으로 제품+덮개 전체 높이(+25 mm 여유)만큼 프리즘화하고 `BRepAlgoAPI_Common(덮개, 절단도구)`로 **덮개의 기존 곡면만** 남깁니다. 두께·뚜껑·솔리드는 만들지 않습니다.
6. 검증: BRep 유효성, 원본 곡면 재사용 여부(재근사된 면은 원본까지 최대거리 보고), 연결요소/shell 수 1, 제품·덮개 접촉면적 전후 동일, 결과 정점이 확장영역 내부.
7. 저장: `.tmp`에 쓴 뒤 재읽기로 검증하고 원자적으로 게시합니다. 제품 검증은 **기준 왕복(baseline round trip)** 값과 비교하므로 STEP 왕복 자체의 오차를 절단 오차로 오인하지 않습니다.

## 실측 결과 (`resource\Mold_Item.step`, margin 100 mm)

작업평면: 원점 `(-4183.68, -843.20, 1396)`, 법선 `(1,0,0)`, X축 `(0,1,0)`
→ 이 축은 **사용자가 확정한 값이 아닙니다.** 검사 모드의 축별 여유(원본, bbox 기준)는 다음과 같고, YZ 투영만 네 방향이 모두 100 mm를 넘습니다.

| 투영 | −A | +A | −B | +B |
| --- | --- | --- | --- | --- |
| XY (Z 법선) | 103.16 | **−0.56** | 188.42 | 148.53 |
| YZ (X 법선) | 188.42 | 148.53 | 179.50 | 179.18 |
| ZX (Y 법선) | 179.50 | 179.18 | 103.16 | **−0.56** |

법선 방향(X)의 부족분은 절단에 영향이 없고, 평면 안 두 방향(Y·Z)이 모두 100 mm를 넘는 축이 X 법선뿐이라 후보로 골랐습니다.
이 값은 bbox 기준의 사전 정황이며 실제 외곽 100 mm 보장은 절단 후 검증에서 확인합니다.

| 항목 | 값 |
| --- | --- |
| 경계 곡선 | HLR outline 16 + 평면 실루엣 모서리 47 = 81 (투영 붕괴 2개 제외) |
| 평면 분할 | cells=2 (외부 518570.674 / 내부 392451.928, 합 911022.602 = 평면 면적) |
| 제품 투영 외곽 | 면적 392451.928 mm², wire 1개 |
| 100 mm 확장 (offset 원호) | 면적 713959.155 mm², 경계 최소거리 99.999997 mm, 표본 297개 편차 최대 0.000003 mm |
| 모서리 직각화 | 후보 6개 중 4개 교체. 확장 면적 722473.233 mm², 경계 거리 99.999997~141.421356 mm(=100√2, 직각 모서리) |
| 절단 후 덮개 | 면 80개, 면적 958841.060 mm² (제거 470155.479 mm²) |
| 원본 곡면 재사용 | 78면. boolean이 재근사한 Extrusion 2면은 원본까지 0.021040 / 0.016298 mm (허용 0.046 mm) |
| 연결 | 컴포넌트 1, shell 1, BRepCheck valid |
| 제품·덮개 접촉면적 | 전 48442.074 / 후 48442.074 mm² |
| STEP 왕복 | 제품 체적 11.578443 mm³, 면적 −0.419677 mm², 덮개 면적 0.000060 mm² (모두 기준 왕복과 동일) |

재현:

```powershell
$env:PATH = "D:\0.Development\2.HeatingMold\third_party\opencascade-7.9.3-vc14-64\win64\vc14\bin;" + $env:PATH
Get-ChildItem "D:\0.Development\2.HeatingMold\third_party\3rdparty-vc14-64" -Recurse -Filter *.dll -File |
    Select-Object -ExpandProperty DirectoryName -Unique |
    ForEach-Object { $env:PATH = $_ + ";" + $env:PATH }
& .\tools\step-trim\bin\step-trim.exe --mode trim --input resource\Mold_Item.step `
    --output resource\Mold_Item_trim100.step `
    --origin -4183.68,-843.20,1396 --normal 1,0,0 --xaxis 0,1,0 --margin 100
```

회귀: `powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\step-trim\Test.ps1`
(검사 → offset-probe → `artifacts\step-trim`에 절단 → 결과 재검사, 전후 원본 SHA-256 비교)

## 확장 방식과 한계

- 실제 제품 외곽에서는 **정확(exact) offset이 실패**하고 `SetApprox(true)` + `GeomAbs_Arc`만 성공합니다. 근사 변환 후에도 실측 편차가 0.000003 mm였고, 도구는 이 편차를 매번 다시 측정해 보고합니다.
- **모서리는 직각(연장선 교점)입니다** (사용자 확정). offset API가 만든 모서리 원호를 인접 두 직선의 교점으로 바꿔 경계를 다시 만듭니다. 이 모델에서는 모서리 후보 6개 중 4개가 교체되었고, 나머지 2개는 이웃이 직선이 아닌 좁은 홈 구간이라 사유를 출력하고 원호로 남겼습니다. 직각 모서리 지점은 외곽에서 100√2 = 141.421 mm 떨어집니다(직선 구간은 100 mm).
- **모든 계산은 확정 작업평면 위에서** 합니다. 3D 곡선을 평면에서 직접 자르지 않습니다.
- 결과는 절단 전과 같은 열린 서피스입니다. 두께 부여·봉합·솔리드 변환은 하지 않습니다.
- 덮개가 확장 경계까지 존재하지 않는 방향이 있으면 연장·보충 없이 실패합니다(검사 모드의 축별 여유로 사전 확인 가능).
- 실패 시 tolerance를 키우거나 가장 큰 조각을 고르는 자동 복구를 하지 않습니다.
- `BRepAlgoAPI_Common`이 원본 곡면을 재근사하는 경우가 있으므로 결과 면의 원본 대비 거리를 항상 보고합니다.

## 미확정 (사용자 확정 필요)

1. 100 mm가 작업평면 위의 평면 거리인지, 곡면을 따른 거리인지.
2. 기준 작업평면의 원점·법선·X축. 현재 값은 검사 정황으로 고른 후보입니다.

(모서리 처리는 사용자 확정으로 직각이 되었습니다.)
