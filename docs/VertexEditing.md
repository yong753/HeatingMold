# 발열경로 편집 (Phase 1)

## 목적
생성된 사행 발열 경로의 제어점(꼭지점)을 3D 뷰포트에서 직접 드래그해 수정한다.
KRRI 협의 내용(“자동 경로의 각 꼭지점 포인트를 이동 가능하도록”)에 대응하는 기능이며,
정밀 CAD 편집기가 아니라 **경로 형상의 간단한 수동 보정**을 목표로 한다.

## 사용 방법
1. 발열 경로를 생성한다(제외영역 경로는 지원하지 않음).
2. 뷰포트 상단 툴바의 **발열경로 편집** 체크.
3. 뷰가 작업 정면으로 전환되고, 모든 원본 제어점이 노란 점으로 표시된다.
4. 점을 드래그하면 수정된 구간이 미리보기로 표시되고, 놓으면 확정된다.
5. `Esc`: 진행 중인 드래그 취소. 드래그 중이 아니면 편집 모드 종료(카메라 복원).
6. `경로 Undo/Redo`로 확정된 편집을 되돌리거나 다시 적용한다.

편집 모드에서는 회전·팬·줌·면 선택이 비활성화된다(작업 정면 고정).

## 데이터 모델
- 정본은 2D 데이텀 `SerpentineRoutePiece.Points`이다. 3D는 항상 실제 모델 표면 매핑으로 재계산한다.
- 턴 커넥터는 **항상 직선 2점** `[레인 끝, 다음 레인 시작]`이다(호 내부점·경계 추적 폴백 없음).
  따라서 턴 커넥터의 편집 핸들은 양 끝점만 노출된다. 직선이 면 밖으로 나가면 매핑 단계에서 거부된다.
- `RouteVertexGroups.Build`는 **이웃 조각이 공유하는 끝점만** 하나의 핸들로 묶는다.
  같은 좌표라도 복귀 경로/교차처럼 위상적으로 무관한 점은 별도 핸들이다.
- 이동은 절대 좌표 지정이 아니라 **프레임 평면에서의 변위**로 적용한다
  (`new = origin + (cursor - press)`), 3D 직접 대입으로 인한 깊이 오차를 피한다.
- 표면 매핑 규칙(`BuildSurfaceMapDelegate`): 같은 데이텀 점은 항상 같은 3D 점이 된다.
  1. 인출점(경로의 시작/종료 역할)은 원본 CAD 인출 edge 참조로 3D를 평가하고, 실패하면
     실제 공유 모서리 폴리라인 보간으로만 대체한다. 같은 좌표의 일반 점은 역할로 구분한다.
  2. 일반 점은 선택면 우선 레이캐스트(`MapWorkingPointOntoFaces`) 히트를 채택한다.
  3. 선택면에 없으면 전역 레이캐스트(`QueryWorkingSurface`) 히트를 **선택면 소속과 무관하게** 채택한다
     — 발열선은 실제 모델 표면 위에 있으므로 발열영역 밖으로 나가도 인접 면 위에 놓인다.
  4. 모델에도 표면이 없으면(실루엣 밖) 매핑 실패로 거부한다(fail-closed). 0.5mm 스냅 폴백은 쓰지 않는다.
- 평면 전용 영역도 같은 표면 매핑 경로를 쓴다(과거 BRepProj 분기 제거).
- 확정 시 기존 `RebuildNativePatternGeometry`가 전체 영역을 다시 스테이징/커밋한다
  (네이티브 커밋이 전체 교체이므로 영역 단위 부분 갱신은 하지 않는다).

## 성능 처리
- 드래그 이동은 뷰포트에서 최신 커서만 33ms 간격으로 병합한다. 이는 입력 폭주를 줄이는 장치이며,
  매핑이 느리면 UI 스레드가 그대로 점유되므로 프레임 보장은 아니다.
- 드래그 미리보기는 변경된 조각만 복사·매핑한다(`SerpentineVertexPreview.TryApplyAffected`).
  전체 `SerpentineResult` 재구성은 확정 시에만 수행한다.
- 미리보기 조각은 모두 갱신한 뒤 Redraw를 한 번만 호출하고, 커넥터 상태도 Rebuild 끝의
  Redraw 한 번으로 반영한다.
- 같은 핸들 호버는 재색칠/Redraw를 생략하고, 이동 없는 클릭은 재생성·Undo 기록을 만들지 않는다.
- 확정 표본 재사용: 검증 단계의 strict 매핑 결과를 스테이징에 그대로 넘기고(`_pendingStageSamples`),
  편집 세션 동안 바뀌지 않은 조각의 표본은 기준 캐시(`_vertexBaselineSamples`)로 재사용한다.
  전체 재스테이징은 유지되지만 재매핑 비용은 변경 조각과 최초 1회로 줄어든다.

## 검증과 한계
검증:
- 매핑된 모든 점은 실제 모델 면 위에 있어야 한다(스테이징이 점별 지지면 거리를 확인).
- 인접 샘플의 면 전이는 별도로 검사하지 않고 매핑 결과를 그대로 스테이징한다. 점별 지지면 거리 검증은 유지된다.
- 확정 시 변경된 조각은 `SurfacePathMapper.TryMapRouteStrict`(채택 리프 0.1mm 간격 표본)로
  모델 표면 연속성을 확인한다.
- 발열영역(2D 외곽/홀) 포함 여부는 생성 기준일 뿐 편집 검증에 쓰지 않는다.

한계(의도적 비범위):
- 표본 간격보다 좁은 결함은 보장하지 않는다.
- 간격(피치) 유지, 굽힘 반경, 자기교차, 제조성 검증은 하지 않는다.
- 모델 실루엣 밖(표면 없음)은 거부한다.
- 제외영역(`Paths`) 경로는 편집 대상이 아니다.
- 패턴을 다시 생성하면 편집 내용은 사라진다. 삭제 전에 경고하며, 취소하면 기존 편집이 유지된다.
- 편집 이력은 영역별로 유지된다. 모델을 다시 로드할 때만 초기화된다.

## 관련 파일
- `src/HeatingMold.Core/Geometry/RouteVertexEdit.cs` — 그룹/적용/검증
- `src/HeatingMold.Core/Geometry/SurfacePathMapper.cs` — `TryMapRouteStrict`
- `src/HeatingMold.Core/Geometry/Serpentine.cs` — `SnapOnChain`, `DistanceToChain` 공개
- `src/HeatingMold.App/MainWindow.VertexEdit.cs` — 편집 세션/드래그/Undo
- `src/HeatingMold.App/OccViewport.cs` — `VertexEditMode`와 드래그 이벤트
- `src/OccBridge/WorkingSideNative.inc` — 핸들/미리보기 오버레이
- `tests/HeatingMold.Core.Tests/RouteVertexEditTests.cs`
