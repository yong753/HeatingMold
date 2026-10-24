#pragma once

#include <map>
#include <memory>
#include <set>
#include <utility>
#include <vector>

#include <AIS_Shape.hxx>
#include <AIS_ColoredShape.hxx>
#include <AIS_Point.hxx>
#include <Graphic3d_Camera.hxx>
#include <IntCurvesFace_ShapeIntersector.hxx>
#include <Quantity_Color.hxx>
#include <TopoDS_Face.hxx>
#include <TopoDS_Shape.hxx>
#include <TopoDS_Wire.hxx>
#include <TopTools_IndexedMapOfShape.hxx>
#include <gp.hxx>
#include <gp_Dir.hxx>
#include <gp_Pnt.hxx>

namespace HeatingMoldNative
{
    /// <summary>한 작업 좌표계에서 재사용하는 Native 교차·메시 질의 context입니다.</summary>
    struct WorkingNative final
    {
        long long id;
        long long revision;
        gp_Pnt origin;
        gp_Dir x;
        gp_Dir y;
        gp_Dir n;
        std::unique_ptr<IntCurvesFace_ShapeIntersector> intersector;
        std::vector<double> triangles;
        double deflection = 0.05;
        double rayLength = 0.0;
    };

    /// <summary>작업 좌표계 화면 상태를 복원하는 Native 저장 정보입니다.</summary>
    struct WorkingCoordinateSystemPresentationNative final
    {
        long long revision = -1;
        std::vector<Handle(AIS_Shape)> boundaries;
        Handle(Graphic3d_Camera) camera;
    };

    /// <summary>임시 경로 표시와 이를 만든 작업 좌표계 context의 소유 정보입니다.</summary>
    struct StagedWorkingPolyline final
    {
        Handle(AIS_Shape) ais;
        long long contextId;
        long long revision;
    };

    /// <summary>발열 경로 구간의 표시 객체와 회전 구간 표시 설정입니다.</summary>
    struct HeatingPathSegmentOverlay final
    {
        Handle(AIS_Shape) ais;
        bool turnSegment;
        bool enabled;
        Quantity_Color color;
        double width;
    };

    /// <summary>Native 후보에서 확정하고 모델 publish 뒤 표시할 발열 경로 wire입니다.</summary>
    struct PreparedPathSegmentGeometry final
    {
        TopoDS_Wire wire;
        bool turnSegment = false;
        /// <summary>확정 표시에서 연결 경로 저장소를 사용합니다.</summary>
        bool connectionPathSegment = false;
    };

    /// <summary>후보 곡선의 측정이 끝났지만 후보 묶음에 확정되지 않은 wire입니다.</summary>
    struct StagedPreparedPathSegmentGeometry final
    {
        TopoDS_Wire wire;
        long long contextId = 0;
        long long revision = 0;
        bool turnSegment = false;
        /// <summary>후보 commit 뒤 연결 경로 표시로 보관합니다.</summary>
        bool connectionPathSegment = false;
    };

    /// <summary>문서에 속한 모델·경로·연결부·제외 영역의 AIS 표시 상태입니다.</summary>
    struct NativeDocumentPresentation final
    {
        /// <summary>현재 문서의 음영 모델 표시 객체입니다.</summary>
        Handle(AIS_ColoredShape) aisShape;

        /// <summary>화면에서 선택한 현재 문서 Face 목록입니다.</summary>
        std::set<int> selectedFaceIds;

        /// <summary>확정 경로 등 월드 좌표 오버레이 목록입니다.</summary>
        std::vector<Handle(AIS_Shape)> worldOverlays;

        /// <summary>확정 연결 경로 표시 객체입니다.</summary>
        std::map<int, Handle(AIS_Shape)> connectionPaths;

        /// <summary>임시 연결 경로 미리보기입니다.</summary>
        Handle(AIS_Shape) connectionPathPreview;

        /// <summary>임시 연결점 표시 객체입니다.</summary>
        Handle(AIS_Point) connectionPointPreview;

        /// <summary>현재 강조 중인 품질 표본 위치입니다.</summary>
        Handle(AIS_Point) qualityIssueLocation;

        /// <summary>제어점 이동 후보의 임시 곡선 표시 객체입니다.</summary>
        Handle(AIS_Shape) controlPointEditPreview;

        /// <summary>발열 경로·회전 구간별 표시와 편집 상태입니다.</summary>
        std::map<int, HeatingPathSegmentOverlay> pathSegments;

        /// <summary>현재 강조 표시 중인 회로 조각 ID입니다.</summary>
        std::set<int> hoveredCircuitPieces;

        /// <summary>확정 제외 영역의 표시 객체입니다.</summary>
        std::vector<Handle(AIS_Shape)> exclusionOverlays;

        /// <summary>임시 제외 영역 표시 객체입니다.</summary>
        Handle(AIS_Shape) exclusionPreview;

        /// <summary>중복 호버 다시 그리기를 막는 마지막 Face 번호입니다.</summary>
        int lastHoverFace = -1;

        /// <summary>Face 호버/선택 강조 표시가 활성 상태인지 나타냅니다.</summary>
        bool faceHighlightsVisible = true;
    };

    /// <summary>한 번에 열린 Native 문서의 모델과 topology 캐시를 함께 소유합니다.</summary>
    struct NativeDocumentSession final
    {
        /// <summary>작업 좌표계 정렬이 끝난 활성 문서 형상입니다.</summary>
        TopoDS_Shape shape;

        /// <summary>Native 순번으로 참조하는 문서 Face 목록입니다.</summary>
        std::vector<TopoDS_Face> faces;

        /// <summary>현재 문서에서 계산한 Face 인접 관계입니다.</summary>
        std::set<std::pair<int, int>> faceAdjacency;

        /// <summary>인접 관계가 계산된 모델 revision이며 미계산이면 -1입니다.</summary>
        long long faceAdjacencyRevision = -1;

        /// <summary>Face 목록과 같은 순서의 OCCT shape 조회 지도입니다.</summary>
        TopTools_IndexedMapOfShape faceMap;

        /// <summary>모델을 표시 좌표계에 정렬할 때 사용한 자동 축입니다.</summary>
        gp_Dir autoAxes[3] = { gp::DX(), gp::DY(), gp::DZ() };

        /// <summary>문서 모델이 바뀔 때마다 증가하는 revision입니다.</summary>
        long long modelRevision = 0;

        /// <summary>현재 문서의 작업 좌표계 질의 context입니다.</summary>
        std::map<long long, std::unique_ptr<WorkingNative>> workingContexts;

        /// <summary>현재 문서의 투영 경계 표시 객체입니다.</summary>
        std::vector<Handle(AIS_Shape)> workingBoundaryOverlays;

        /// <summary>작업 좌표계별 화면 복원 정보입니다.</summary>
        std::map<long long, WorkingCoordinateSystemPresentationNative> workingPresentations;

        /// <summary>확정 전 임시 작업면 다중선 표시 객체입니다.</summary>
        std::map<int, StagedWorkingPolyline> stagedWorkingPolylines;

        /// <summary>임시 경로 ID와 원본 작업 context의 연결 정보입니다.</summary>
        std::map<int, std::pair<long long, long long>> stagedWorkingPathSegmentOwners;

        /// <summary>후보 검증 중 생성했지만 아직 한 묶음으로 확정하지 않은 곡선 wire입니다.</summary>
        std::map<int, StagedPreparedPathSegmentGeometry> stagedPreparedPathSegments;

        /// <summary>모델 후보와 함께 publish할 최종 곡선 wire입니다.</summary>
        std::map<int, PreparedPathSegmentGeometry> preparedPathSegments;

        /// <summary>현재 문서의 모델·경로·연결부 화면 표시 상태입니다.</summary>
        NativeDocumentPresentation presentation;

        /// <summary>화면 표시가 활성 모델 상태와 동기화되지 않았는지 나타냅니다.</summary>
        bool presentationOutOfSync = false;
    };
}
