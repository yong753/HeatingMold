# 경로 꼭지점 편집 (Phase 1)

## 목적
생성된 사행 발열 경로의 제어점(꼭지점)을 3D 뷰포트에서 직접 드래그해 수정한다.
KRRI 협의 내용(“자동 경로의 각 꼭지점 포인트를 이동 가능하도록”)에 대응하는 기능이며,
정밀 CAD 편집기가 아니라 **경로 형상의 간단한 수동 보정**을 목표로 한다.

## 사용 방법
1. 발열 경로를 생성한다(제외영역 경로는 지원하지 않음).
2. `4 · 회로 구성`의 **꼭지점 편집** 체크.
3. 뷰가 작업 정면으로 전환되고, 모든 원본 제어점이 노란 점으로 표시된다.
4. 점을 드래그하면 수정된 구간이 미리보기로 표시되고, 놓으면 확정된다.
5. `Esc`: 진행 중인 드래그 취소. 드래그 중이 아니면 편집 모드 종료(카메라 복원).
6. `꼭지점 Undo/Redo`로 확정된 편집을 되돌리거나 다시 적용한다.

편집 모드에서는 회전·팬·줌·면 선택이 비활성화된다(작업 정면 고정).

## 데이터 모델
- 정본은 2D 데이텀 `SerpentineRoutePiece.Points`이다. 3D는 항상 선택면 매핑으로 재계산한다.
- `RouteVertexGroups.Build`는 **이웃 조각이 공유하는 끝점만** 하나의 핸들로 묶는다.
  같은 좌표라도 복귀 경로/교차처럼 위상적으로 무관한 점은 별도 핸들이다.
- 이동은 절대 좌표 지정이 아니라 **프레임 평면에서의 변위**로 적용한다
  (`new = origin + (cursor - press)`), 3D 직접 대입으로 인한 깊이 오차를 피한다.
- 인출 앵커(경로 시작/종료가 인출 체인 위에 있는 점)는 `ExitChain` 위로 스냅하고,
  3D는 실제 공유 모서리 보간점을 쓴다. ExitPiece 존재 여부가 아니라 경로 끝 역할로 판정한다.
- 확정 시 기존 `RebuildNativePatternGeometry`가 전체 영역을 다시 스테이징/커밋한다
  (네이티브 커밋이 전체 교체이므로 영역 단위 부분 갱신은 하지 않는다).

## 검증과 한계
검증:
- 수정된 구간의 2D 표본(0.5mm 간격)이 적용영역(외곽+홀) 안에 있어야 한다.
- 곡면 영역은 수정된 조각을 `SurfacePathMapper.TryMapRouteStrict`로 다시 매핑한다.
  선택면 직접 히트만 인정하며(0.5mm 스냅 폴백 없음), 채택 리프 내부를 0.1mm 간격으로
  추가 표본화해 숨은 홀/도메인 이탈을 걸러낸다.

한계(의도적 비범위):
- 표본 간격보다 좁은 결함은 보장하지 않는다.
- 간격(피치) 유지, 굽힘 반경, 자기교차, 제조성 검증은 하지 않는다.
- 제외영역(`Paths`) 경로는 편집 대상이 아니다.
- 패턴을 다시 생성하면 편집 내용은 사라진다. 삭제 전에 경고하며, 취소하면 기존 편집이 유지된다.
- 편집 이력은 영역별이며 영역 전환/모델 로드 시 초기화된다.

## 관련 파일
- `src/HeatingMold.Core/Geometry/RouteVertexEdit.cs` — 그룹/적용/검증
- `src/HeatingMold.Core/Geometry/SurfacePathMapper.cs` — `TryMapRouteStrict`
- `src/HeatingMold.Core/Geometry/Serpentine.cs` — `SnapOnChain`, `DistanceToChain` 공개
- `src/HeatingMold.App/MainWindow.VertexEdit.cs` — 편집 세션/드래그/Undo
- `src/HeatingMold.App/OccViewport.cs` — `VertexEditMode`와 드래그 이벤트
- `src/OccBridge/WorkingSideNative.inc` — 핸들/미리보기 오버레이
- `tests/HeatingMold.Core.Tests/RouteVertexEditTests.cs`
