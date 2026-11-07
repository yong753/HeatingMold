// =====================================================================
// 사각형 선택 보충 (mouse-release refinement)
//
// 드래그 중 미리보기는 기존 기본 해상도 face-ID 이미지만 사용한다. 마우스를 놓은
// 뒤에만, 그 이미지에서 선택 사각형 안에 픽셀을 얻지 못한 면을 대상으로 국소
// 고해상도 렌더를 한 번 수행한다. 보충 렌더도 원본 모델 전체를 그리므로 깊이
// 판정은 그대로 유지된다 — 완전히 가려진 뒤쪽 면은 여기서도 나타나지 않는다.
//
// 범위: 화면 픽셀 샘플링에서 빠진 얇은 투영 조각의 복구. 해석적 CAD 경계의
// 완전한 가시성 보장이 아니며, 상한을 넘으면 보충을 생략하고 기본 결과만 쓴다.
// =====================================================================

    enum FaceRefineStatusNative
    {
        FaceRefineStatus_None = 0,
        FaceRefineStatus_Ok = 1,
        FaceRefineStatus_NoCandidate = 2,
        FaceRefineStatus_Capped = 3,
        FaceRefineStatus_RenderFailed = 4,
        FaceRefineStatus_Unsupported = 5
    };

    // 진단 전용: 마지막 사각형 질의에서 기본 이미지가 돌려준 face id 목록.
    std::vector<int> g_faceRefineLastBaseIds;

    // 진단 전용: 비어 있지 않으면 보충 렌더 이미지를 이 경로에 저장한다.
    std::string g_faceRefineDumpPath;

    /// 진단 전용: RGB 픽셀맵을 24비트 BMP로 저장한다. Image_AlienPixMap은 FreeImage가
    /// 없으면 저장에 실패하므로 직접 기록한다.
    bool FaceRefineWriteBmpNative(const Image_PixMap& image, const char* path)
    {
        if (image.IsEmpty() || image.Format() != Image_Format_RGB || path == nullptr) return false;
        const int width = static_cast<int>(image.SizeX());
        const int height = static_cast<int>(image.SizeY());
        if (width <= 0 || height <= 0) return false;

        const int stride = (width * 3 + 3) & ~3;
        const unsigned int dataSize = static_cast<unsigned int>(stride) * static_cast<unsigned int>(height);
        const unsigned int fileSize = 54u + dataSize;

        std::ofstream file(path, std::ios::binary);
        if (!file) return false;

        unsigned char header[54] = {};
        header[0] = 'B';
        header[1] = 'M';
        header[2] = static_cast<unsigned char>(fileSize & 0xFFu);
        header[3] = static_cast<unsigned char>((fileSize >> 8) & 0xFFu);
        header[4] = static_cast<unsigned char>((fileSize >> 16) & 0xFFu);
        header[5] = static_cast<unsigned char>((fileSize >> 24) & 0xFFu);
        header[10] = 54;
        header[14] = 40;
        header[18] = static_cast<unsigned char>(width & 0xFF);
        header[19] = static_cast<unsigned char>((width >> 8) & 0xFF);
        header[20] = static_cast<unsigned char>((width >> 16) & 0xFF);
        header[21] = static_cast<unsigned char>((width >> 24) & 0xFF);
        const int topDownHeight = -height;   // 음수 높이 = 위에서 아래로 저장
        header[22] = static_cast<unsigned char>(topDownHeight & 0xFF);
        header[23] = static_cast<unsigned char>((topDownHeight >> 8) & 0xFF);
        header[24] = static_cast<unsigned char>((topDownHeight >> 16) & 0xFF);
        header[25] = static_cast<unsigned char>((topDownHeight >> 24) & 0xFF);
        header[26] = 1;
        header[28] = 24;
        header[34] = static_cast<unsigned char>(dataSize & 0xFFu);
        header[35] = static_cast<unsigned char>((dataSize >> 8) & 0xFFu);
        header[36] = static_cast<unsigned char>((dataSize >> 16) & 0xFFu);
        header[37] = static_cast<unsigned char>((dataSize >> 24) & 0xFFu);
        file.write(reinterpret_cast<const char*>(header), 54);

        std::vector<unsigned char> row(static_cast<size_t>(stride), 0);
        for (int y = 0; y < height; ++y)
        {
            const Standard_Byte* source = image.Row(static_cast<Standard_Size>(y));
            if (source == nullptr) return false;
            for (int x = 0; x < width; ++x)
            {
                row[static_cast<size_t>(x) * 3 + 0] = source[static_cast<size_t>(x) * 3 + 2];
                row[static_cast<size_t>(x) * 3 + 1] = source[static_cast<size_t>(x) * 3 + 1];
                row[static_cast<size_t>(x) * 3 + 2] = source[static_cast<size_t>(x) * 3 + 0];
            }
            file.write(reinterpret_cast<const char*>(row.data()), stride);
        }
        file.flush();
        return static_cast<bool>(file);
    }

    struct FaceRefineStatsNative
    {
        int       Status;
        int       CandidateFaces;
        int       CandidateTiles;
        int       RenderPasses;
        long long OutputPixels;
        long long ProjectedTriangles;
        long long CandidatePixels;
        double    ProjectMs;
        double    RenderMs;
        double    TotalMs;

        FaceRefineStatsNative()
            : Status(FaceRefineStatus_None), CandidateFaces(0), CandidateTiles(0), RenderPasses(0),
              OutputPixels(0), ProjectedTriangles(0), CandidatePixels(0),
              ProjectMs(0.0), RenderMs(0.0), TotalMs(0.0) {}
    };

    FaceRefineStatsNative g_faceRefineStats;

    // 실험값 — 실제 측정으로 확정된 값이 아니다.
    const int       kFaceRefineSupersample       = 8;
    const long long kFaceRefineMaxOutputPixels   = 2000000;
    const int       kFaceRefineMaxPasses         = 8;
    const int       kFaceRefineMaxCandidates     = 192;
    const long long kFaceRefineMaxCandidatePixels = 4000000;
    const int       kFaceRefineBlockTiles        = 2;   // 2x2 타일 = 64x64 기본 픽셀

    inline bool FaceRefineFiniteNative(double value)
    {
        return value == value && value > -1.0e300 && value < 1.0e300;
    }

    inline double FaceRefineClampCoordinateNative(double value)
    {
        if (!FaceRefineFiniteNative(value)) return 0.0;
        if (value < -1.0e6) return -1.0e6;
        if (value > 1.0e6) return 1.0e6;
        return value;
    }

    inline int FaceRefineFloorNative(double value)
    {
        const int truncated = static_cast<int>(value);
        return (value < 0.0 && static_cast<double>(truncated) != value) ? truncated - 1 : truncated;
    }

    inline int FaceRefineCeilNative(double value)
    {
        const int truncated = static_cast<int>(value);
        return (value > 0.0 && static_cast<double>(truncated) != value) ? truncated + 1 : truncated;
    }

    inline double FaceRefineMin3Native(double a, double b, double c)
    {
        double result = a < b ? a : b;
        return result < c ? result : c;
    }

    inline double FaceRefineMax3Native(double a, double b, double c)
    {
        double result = a > b ? a : b;
        return result > c ? result : c;
    }

    /// 화면 좌표(top-down, 연속)로 투영한다. 실패하면 false.
    inline bool FaceRefineProjectPointNative(const Handle(Graphic3d_Camera)& camera,
                                             const gp_Pnt& point, int width, int height,
                                             double& screenX, double& screenY)
    {
        const gp_Pnt ndc = camera->Project(point);
        screenX = (ndc.X() + 1.0) * 0.5 * static_cast<double>(width);
        screenY = (1.0 - (ndc.Y() + 1.0) * 0.5) * static_cast<double>(height);
        return FaceRefineFiniteNative(screenX) && FaceRefineFiniteNative(screenY);
    }

    inline double FaceRefineCrossNative(double ax, double ay, double bx, double by,
                                        double px, double py)
    {
        return (bx - ax) * (py - ay) - (by - ay) * (px - ax);
    }

    /// 부호와 무관하게 점이 삼각형 안(경계 포함)인지 판정한다.
    inline bool FaceRefineInsideTriangleNative(double ax, double ay, double bx, double by,
                                               double cx, double cy, double px, double py)
    {
        const double d1 = FaceRefineCrossNative(ax, ay, bx, by, px, py);
        const double d2 = FaceRefineCrossNative(bx, by, cx, cy, px, py);
        const double d3 = FaceRefineCrossNative(cx, cy, ax, ay, px, py);
        const bool hasNegative = d1 < 0.0 || d2 < 0.0 || d3 < 0.0;
        const bool hasPositive = d1 > 0.0 || d2 > 0.0 || d3 > 0.0;
        return !(hasNegative && hasPositive);
    }

    /// 삼각형과 축정렬 사각형의 교차(SAT, 접촉 포함). 타일 후보 판정에 쓴다.
    inline bool FaceRefineTriangleHitsRectNative(double ax, double ay, double bx, double by,
                                                 double cx, double cy,
                                                 double rx0, double ry0, double rx1, double ry1)
    {
        if (FaceRefineMax3Native(ax, bx, cx) < rx0 || FaceRefineMin3Native(ax, bx, cx) > rx1) return false;
        if (FaceRefineMax3Native(ay, by, cy) < ry0 || FaceRefineMin3Native(ay, by, cy) > ry1) return false;

        const double rectX[4] = { rx0, rx1, rx1, rx0 };
        const double rectY[4] = { ry0, ry0, ry1, ry1 };

        const double edgeX[3] = { bx - ax, cx - bx, ax - cx };
        const double edgeY[3] = { by - ay, cy - by, ay - cy };

        for (int axis = 0; axis < 5; ++axis)
        {
            double axisX = 0.0;
            double axisY = 0.0;
            if (axis == 0)      { axisX = 1.0; axisY = 0.0; }
            else if (axis == 1) { axisX = 0.0; axisY = 1.0; }
            else
            {
                axisX = -edgeY[axis - 2];
                axisY =  edgeX[axis - 2];
            }
            const double triangleLength = axisX * axisX + axisY * axisY;
            if (triangleLength <= 1.0e-18) continue;

            const double t0 = ax * axisX + ay * axisY;
            const double t1 = bx * axisX + by * axisY;
            const double t2 = cx * axisX + cy * axisY;
            double triangleMin = t0;
            double triangleMax = t0;
            if (t1 < triangleMin) triangleMin = t1;
            if (t1 > triangleMax) triangleMax = t1;
            if (t2 < triangleMin) triangleMin = t2;
            if (t2 > triangleMax) triangleMax = t2;

            double rectMin = rectX[0] * axisX + rectY[0] * axisY;
            double rectMax = rectMin;
            for (int corner = 1; corner < 4; ++corner)
            {
                const double projected = rectX[corner] * axisX + rectY[corner] * axisY;
                if (projected < rectMin) rectMin = projected;
                if (projected > rectMax) rectMax = projected;
            }

            if (triangleMax < rectMin || rectMax < triangleMin) return false;
        }
        return true;
    }

    /// 한 면의 투영 삼각형을 선택 사각형 안에서 기본 해상도 픽셀 중심으로 덮어 보고,
    /// 확실히 덮인 샘플이 하나도 없으면 보충 후보로 등록한다.
    /// 반환: 0 = 후보 아님, 1 = 후보, -1 = 예산 초과.
    int CollectFaceRefineCandidateNative(int faceId,
                                         const Handle(Graphic3d_Camera)& camera,
                                         int viewWidth, int viewHeight,
                                         int coreX0, int coreX1, int coreY0, int coreY1,
                                         std::set<long long>& tileKeys, long long& budgetUsed)
    {
        if (faceId < 0 || faceId >= static_cast<int>(g_faces.size())) return 0;

        TopLoc_Location location;
        const Handle(Poly_Triangulation) triangulation = BRep_Tool::Triangulation(g_faces[faceId], location);
        if (triangulation.IsNull() || triangulation->NbTriangles() < 1 || triangulation->NbNodes() < 3)
            return 0;

        const gp_Trsf transform = location.Transformation();
        const bool moved = !location.IsIdentity();

        const double selX0 = static_cast<double>(coreX0);
        const double selX1 = static_cast<double>(coreX1) + 1.0;
        const double selY0 = static_cast<double>(coreY0);
        const double selY1 = static_cast<double>(coreY1) + 1.0;

        std::vector<double> triangles;   // 삼각형마다 (ax,ay,bx,by,cx,cy)
        double boxX0 = 1.0e300, boxX1 = -1.0e300, boxY0 = 1.0e300, boxY1 = -1.0e300;

        for (Standard_Integer t = 1; t <= triangulation->NbTriangles(); ++t)
        {
            Standard_Integer index1 = 0, index2 = 0, index3 = 0;
            triangulation->Triangle(t).Get(index1, index2, index3);
            const Standard_Integer nodeCount = triangulation->NbNodes();
            if (index1 < 1 || index2 < 1 || index3 < 1 ||
                index1 > nodeCount || index2 > nodeCount || index3 > nodeCount)
                continue;

            gp_Pnt p1 = triangulation->Node(index1);
            gp_Pnt p2 = triangulation->Node(index2);
            gp_Pnt p3 = triangulation->Node(index3);
            if (moved)
            {
                p1.Transform(transform);
                p2.Transform(transform);
                p3.Transform(transform);
            }

            double ax = 0.0, ay = 0.0, bx = 0.0, by = 0.0, cx = 0.0, cy = 0.0;
            if (!FaceRefineProjectPointNative(camera, p1, viewWidth, viewHeight, ax, ay)) continue;
            if (!FaceRefineProjectPointNative(camera, p2, viewWidth, viewHeight, bx, by)) continue;
            if (!FaceRefineProjectPointNative(camera, p3, viewWidth, viewHeight, cx, cy)) continue;

            ax = FaceRefineClampCoordinateNative(ax);
            ay = FaceRefineClampCoordinateNative(ay);
            bx = FaceRefineClampCoordinateNative(bx);
            by = FaceRefineClampCoordinateNative(by);
            cx = FaceRefineClampCoordinateNative(cx);
            cy = FaceRefineClampCoordinateNative(cy);

            const double minX = FaceRefineMin3Native(ax, bx, cx);
            const double maxX = FaceRefineMax3Native(ax, bx, cx);
            const double minY = FaceRefineMin3Native(ay, by, cy);
            const double maxY = FaceRefineMax3Native(ay, by, cy);
            if (maxX < selX0 || minX > selX1 || maxY < selY0 || minY > selY1) continue;

            triangles.push_back(ax); triangles.push_back(ay);
            triangles.push_back(bx); triangles.push_back(by);
            triangles.push_back(cx); triangles.push_back(cy);

            if (minX < boxX0) boxX0 = minX;
            if (maxX > boxX1) boxX1 = maxX;
            if (minY < boxY0) boxY0 = minY;
            if (maxY > boxY1) boxY1 = maxY;
            ++g_faceRefineStats.ProjectedTriangles;
        }

        if (triangles.empty()) return 0;

        // 기본 해상도 픽셀 중심 커버리지 — 선택 사각형 ∩ 투영 bbox, 좌우/상하 1px 여유.
        int pixelX0 = FaceRefineFloorNative(boxX0) - 1;
        int pixelX1 = FaceRefineCeilNative(boxX1) + 1;
        int pixelY0 = FaceRefineFloorNative(boxY0) - 1;
        int pixelY1 = FaceRefineCeilNative(boxY1) + 1;
        if (pixelX0 < coreX0 - 1) pixelX0 = coreX0 - 1;
        if (pixelX1 > coreX1 + 1) pixelX1 = coreX1 + 1;
        if (pixelY0 < coreY0 - 1) pixelY0 = coreY0 - 1;
        if (pixelY1 > coreY1 + 1) pixelY1 = coreY1 + 1;
        if (pixelX0 < 0) pixelX0 = 0;
        if (pixelY0 < 0) pixelY0 = 0;
        if (pixelX1 > viewWidth - 1) pixelX1 = viewWidth - 1;
        if (pixelY1 > viewHeight - 1) pixelY1 = viewHeight - 1;
        if (pixelX0 > pixelX1 || pixelY0 > pixelY1) return 0;

        const int bitmapWidth = pixelX1 - pixelX0 + 1;
        const int bitmapHeight = pixelY1 - pixelY0 + 1;
        const long long bitmapArea = static_cast<long long>(bitmapWidth) * static_cast<long long>(bitmapHeight);
        if (bitmapArea <= 0) return 0;
        if (budgetUsed + bitmapArea > kFaceRefineMaxCandidatePixels) return -1;
        budgetUsed += bitmapArea;

        std::vector<unsigned char> coverage(static_cast<size_t>(bitmapArea), 0);
        for (size_t i = 0; i + 5 < triangles.size(); i += 6)
        {
            const double ax = triangles[i], ay = triangles[i + 1];
            const double bx = triangles[i + 2], by = triangles[i + 3];
            const double cx = triangles[i + 4], cy = triangles[i + 5];

            int localX0 = FaceRefineFloorNative(FaceRefineMin3Native(ax, bx, cx)) - pixelX0;
            int localX1 = FaceRefineCeilNative(FaceRefineMax3Native(ax, bx, cx)) - pixelX0;
            int localY0 = FaceRefineFloorNative(FaceRefineMin3Native(ay, by, cy)) - pixelY0;
            int localY1 = FaceRefineCeilNative(FaceRefineMax3Native(ay, by, cy)) - pixelY0;
            if (localX0 < 0) localX0 = 0;
            if (localY0 < 0) localY0 = 0;
            if (localX1 > bitmapWidth - 1) localX1 = bitmapWidth - 1;
            if (localY1 > bitmapHeight - 1) localY1 = bitmapHeight - 1;

            for (int y = localY0; y <= localY1; ++y)
            {
                unsigned char* row = &coverage[static_cast<size_t>(y) * bitmapWidth];
                const double sampleY = static_cast<double>(pixelY0 + y) + 0.5;
                for (int x = localX0; x <= localX1; ++x)
                {
                    if (row[x] != 0) continue;
                    const double sampleX = static_cast<double>(pixelX0 + x) + 0.5;
                    if (FaceRefineInsideTriangleNative(ax, ay, bx, by, cx, cy, sampleX, sampleY))
                        row[x] = 1;
                }
            }
        }

        // 4-이웃 침식: 위/아래/좌/우가 모두 덮인 픽셀만 "확실히 덮인 샘플"로 본다.
        // 비트맵 밖은 덮인 것으로 취급해 선택 사각형 경계에서 인위적으로 깎이지 않게 한다.
        const int supportX0 = coreX0 > pixelX0 ? coreX0 : pixelX0;
        const int supportX1 = coreX1 < pixelX1 ? coreX1 : pixelX1;
        const int supportY0 = coreY0 > pixelY0 ? coreY0 : pixelY0;
        const int supportY1 = coreY1 < pixelY1 ? coreY1 : pixelY1;

        bool supported = false;
        for (int y = supportY0; y <= supportY1 && !supported; ++y)
        {
            const int localY = y - pixelY0;
            const unsigned char* row = &coverage[static_cast<size_t>(localY) * bitmapWidth];
            const unsigned char* rowAbove = localY > 0
                ? &coverage[static_cast<size_t>(localY - 1) * bitmapWidth] : nullptr;
            const unsigned char* rowBelow = localY < bitmapHeight - 1
                ? &coverage[static_cast<size_t>(localY + 1) * bitmapWidth] : nullptr;
            for (int x = supportX0; x <= supportX1; ++x)
            {
                const int localX = x - pixelX0;
                if (row[localX] == 0) continue;
                if (localX > 0 && row[localX - 1] == 0) continue;
                if (localX < bitmapWidth - 1 && row[localX + 1] == 0) continue;
                if (rowAbove != nullptr && rowAbove[localX] == 0) continue;
                if (rowBelow != nullptr && rowBelow[localX] == 0) continue;
                supported = true;
                break;
            }
        }
        if (supported) return 0;

        // 후보 타일: 픽셀 중심이 아니라 삼각형-타일 교차로 표시한다.
        // 폭 0.15px 띠는 픽셀을 못 덮어도 타일 경계는 스칠 수 있기 때문이다.
        const int tilesX = (viewWidth + kFaceIdTileSize - 1) / kFaceIdTileSize;
        for (size_t i = 0; i + 5 < triangles.size(); i += 6)
        {
            const double ax = triangles[i], ay = triangles[i + 1];
            const double bx = triangles[i + 2], by = triangles[i + 3];
            const double cx = triangles[i + 4], cy = triangles[i + 5];

            int tileX0 = FaceRefineFloorNative(FaceRefineMin3Native(ax, bx, cx)) / kFaceIdTileSize;
            int tileX1 = FaceRefineFloorNative(FaceRefineMax3Native(ax, bx, cx)) / kFaceIdTileSize;
            int tileY0 = FaceRefineFloorNative(FaceRefineMin3Native(ay, by, cy)) / kFaceIdTileSize;
            int tileY1 = FaceRefineFloorNative(FaceRefineMax3Native(ay, by, cy)) / kFaceIdTileSize;
            if (tileX0 < coreX0 / kFaceIdTileSize) tileX0 = coreX0 / kFaceIdTileSize;
            if (tileX1 > coreX1 / kFaceIdTileSize) tileX1 = coreX1 / kFaceIdTileSize;
            if (tileY0 < coreY0 / kFaceIdTileSize) tileY0 = coreY0 / kFaceIdTileSize;
            if (tileY1 > coreY1 / kFaceIdTileSize) tileY1 = coreY1 / kFaceIdTileSize;

            for (int tileY = tileY0; tileY <= tileY1; ++tileY)
            {
                for (int tileX = tileX0; tileX <= tileX1; ++tileX)
                {
                    const double rx0 = static_cast<double>(tileX * kFaceIdTileSize);
                    const double ry0 = static_cast<double>(tileY * kFaceIdTileSize);
                    const double rx1 = rx0 + kFaceIdTileSize;
                    const double ry1 = ry0 + kFaceIdTileSize;
                    if (rx1 <= selX0 || rx0 >= selX1 || ry1 <= selY0 || ry0 >= selY1) continue;
                    if (!FaceRefineTriangleHitsRectNative(ax, ay, bx, by, cx, cy, rx0, ry0, rx1, ry1))
                        continue;
                    tileKeys.insert(static_cast<long long>(tileY) * tilesX + tileX);
                }
            }
        }
        return 1;
    }

    /// 보충 렌더 1패스. 선택 사각형 밖 샘플은 버리고, 실제로 찍힌 faceID만 돌려준다.
    bool RenderFaceIdRegionNative(int regionX0, int regionY0, int regionX1, int regionY1,
                                  int viewWidth, int viewHeight,
                                  int coreX0, int coreX1, int coreY0, int coreY1,
                                  std::set<int>& outFaceIds)
    {
        if (g_ctx.IsNull() || g_view.IsNull() || g_view->Window().IsNull()) return false;
        if (g_faceIdLayer == Graphic3d_ZLayerId_UNKNOWN) return false;
        if (!BuildFaceIdShapeNative()) return false;

        const int sourceWidth = regionX1 - regionX0;
        const int sourceHeight = regionY1 - regionY0;
        if (sourceWidth <= 0 || sourceHeight <= 0) return false;

        const int outputWidth = sourceWidth * kFaceRefineSupersample;
        const int outputHeight = sourceHeight * kFaceRefineSupersample;
        if (outputWidth <= 0 || outputHeight <= 0) return false;

        const Handle(Graphic3d_Camera) savedCamera = g_view->Camera();
        if (savedCamera.IsNull()) return false;
        // orthographic 전용: 확대는 scale/중심만 바꿔 원근 왜곡 없이 같은 투영을 얻는다.
        if (!savedCamera->IsOrthographic()) return false;

        FaceIdBackgroundGuardNative backgroundGuard;
        backgroundGuard.View = g_view;
        backgroundGuard.Base = g_view->View();
        const bool shapeWasDisplayed = g_ctx->IsDisplayed(g_faceIdShape);
        const Standard_Boolean savedAutoZFit = g_view->AutoZFitMode();
        Standard_Boolean savedImmediate = Standard_True;
        bool immediateCaptured = false;
        bool ok = false;

        try
        {
            // SetCamera는 즉시 갱신을 유발하므로 먼저 끈다. AutoZFit이 켜져 있으면
            // 더미 Redraw가 near/far를 바꾸므로 함께 끈다.
            savedImmediate = g_view->SetImmediateUpdate(Standard_False);
            immediateCaptured = true;
            g_view->SetAutoZFitMode(Standard_False);

            const gp_Pnt savedEye = savedCamera->Eye();
            const gp_Pnt savedCenter = savedCamera->Center();
            const gp_Pnt savedCenterNdc = savedCamera->Project(savedCenter);

            const double roiCenterX = (static_cast<double>(regionX0) + static_cast<double>(regionX1)) * 0.5;
            const double roiCenterY = (static_cast<double>(regionY0) + static_cast<double>(regionY1)) * 0.5;
            const double ndcX = 2.0 * roiCenterX / static_cast<double>(viewWidth) - 1.0;
            const double ndcY = 1.0 - 2.0 * roiCenterY / static_cast<double>(viewHeight);
            const gp_Pnt worldCenter = savedCamera->UnProject(gp_Pnt(ndcX, ndcY, savedCenterNdc.Z()));

            const gp_Pnt roiEye(worldCenter.X() + (savedEye.X() - savedCenter.X()),
                                worldCenter.Y() + (savedEye.Y() - savedCenter.Y()),
                                worldCenter.Z() + (savedEye.Z() - savedCenter.Z()));
            const double roiScale = savedCamera->Scale() *
                                    static_cast<double>(sourceHeight) / static_cast<double>(viewHeight);

            Handle(Graphic3d_Camera) roiCamera = new Graphic3d_Camera(savedCamera);
            roiCamera->SetTile(Graphic3d_CameraTile());
            roiCamera->SetEyeAndCenter(roiEye, worldCenter);
            roiCamera->SetScale(roiScale);
            g_view->SetCamera(roiCamera);

            backgroundGuard.Apply();
            g_ctx->SetZLayer(g_faceIdShape, g_faceIdLayer);
            if (!shapeWasDisplayed)
                g_ctx->Display(g_faceIdShape, AIS_Shaded, -1, Standard_False);

            Image_PixMap image;
            V3d_ImageDumpOptions options;
            options.Width = outputWidth;
            options.Height = outputHeight;
            options.BufferType = Graphic3d_BT_RGB;
            options.ToAdjustAspect = Standard_True;   // ROI 종횡비 = 출력 종횡비
            options.TargetZLayerId = g_faceIdLayer;
            options.IsSingleLayer = Standard_True;    // 원본 모델 깊이만, 오버레이는 제외
            const Standard_Boolean dumped = g_view->ToPixMap(image, options);

            if (!shapeWasDisplayed && g_ctx->IsDisplayed(g_faceIdShape))
                g_ctx->Erase(g_faceIdShape, Standard_False);

            if (dumped && !image.IsEmpty() && image.Format() == Image_Format_RGB &&
                static_cast<int>(image.SizeX()) == outputWidth &&
                static_cast<int>(image.SizeY()) == outputHeight)
            {
                if (!g_faceRefineDumpPath.empty())
                {
                    try
                    {
                        FaceRefineWriteBmpNative(image, g_faceRefineDumpPath.c_str());
                    }
                    catch (const Standard_Failure&) { }
                }
                const double inverseScale = 1.0 / static_cast<double>(kFaceRefineSupersample);
                for (int y = 0; y < outputHeight; ++y)
                {
                    const Standard_Byte* row = image.Row(static_cast<Standard_Size>(y));
                    if (row == nullptr) break;
                    const double sourceY = static_cast<double>(regionY0) +
                                           (static_cast<double>(y) + 0.5) * inverseScale;
                    if (sourceY < static_cast<double>(coreY0) ||
                        sourceY >= static_cast<double>(coreY1) + 1.0) continue;
                    for (int x = 0; x < outputWidth; ++x)
                    {
                        const double sourceX = static_cast<double>(regionX0) +
                                               (static_cast<double>(x) + 0.5) * inverseScale;
                        if (sourceX < static_cast<double>(coreX0) ||
                            sourceX >= static_cast<double>(coreX1) + 1.0) continue;
                        const Standard_Byte* pixel = row + static_cast<Standard_Size>(x) * 3;
                        const int code = FaceIdFromPixelNative(pixel[0], pixel[1], pixel[2]);
                        if (code > 0 && code <= static_cast<int>(g_faces.size()))
                            outFaceIds.insert(code - 1);
                    }
                }
                ok = true;
            }
        }
        catch (const Standard_Failure&)
        {
            ok = false;
        }

        try
        {
            if (!shapeWasDisplayed && g_ctx->IsDisplayed(g_faceIdShape))
                g_ctx->Erase(g_faceIdShape, Standard_False);
            g_view->SetCamera(savedCamera);
            g_view->SetAutoZFitMode(savedAutoZFit);
            if (immediateCaptured) g_view->SetImmediateUpdate(savedImmediate);
        }
        catch (const Standard_Failure&) { ok = false; }

        return ok;
    }

    /// 진단 전용: 지정한 기본 픽셀 영역을 8배로 다시 렌더해 실제로 디코드되는
    /// face id를 그대로 돌려준다. 선택 상태는 바꾸지 않는다.
    std::vector<int> ProbeFaceIdRegionNative(int x0, int y0, int x1, int y1)
    {
        std::vector<int> ids;
        if (g_view.IsNull() || g_view->Window().IsNull()) return ids;

        int px0 = x0 < x1 ? x0 : x1;
        int px1 = x0 < x1 ? x1 : x0;
        int py0 = y0 < y1 ? y0 : y1;
        int py1 = y0 < y1 ? y1 : y0;
        if (px0 < 0) px0 = 0;
        if (py0 < 0) py0 = 0;
        if (px1 >= g_faceIdWidth) px1 = g_faceIdWidth - 1;
        if (py1 >= g_faceIdHeight) py1 = g_faceIdHeight - 1;
        if (px0 > px1 || py0 > py1) return ids;

        std::set<int> found;
        RenderFaceIdRegionNative(px0, py0, px1 + 1, py1 + 1,
                                 g_faceIdWidth, g_faceIdHeight,
                                 px0, px1, py0, py1, found);
        ids.assign(found.begin(), found.end());
        return ids;
    }

    /// 마우스를 놓은 시점에만 호출된다. 기본 ID 목록에 없던 면 중, 기본 해상도
    /// 샘플이 닿지 않은 얇은 투영 조각을 국소 고해상도로 확인해 supplemental에 채운다.
    void RefineRectangleSelectionNative(int x0, int y0, int x1, int y1,
                                        const std::vector<int>& baseFaceIds,
                                        std::set<int>& supplemental)
    {
        supplemental.clear();
        g_faceRefineLastBaseIds.assign(baseFaceIds.begin(), baseFaceIds.end());
        g_faceRefineStats = FaceRefineStatsNative();
        const std::chrono::steady_clock::time_point started = std::chrono::steady_clock::now();

        if (g_ctx.IsNull() || g_view.IsNull() || g_faces.empty() ||
            g_faceIdWidth <= 0 || g_faceIdHeight <= 0 ||
            g_faceIdLayer == Graphic3d_ZLayerId_UNKNOWN)
        {
            g_faceRefineStats.Status = FaceRefineStatus_Unsupported;
            return;
        }

        const int viewWidth = g_faceIdWidth;
        const int viewHeight = g_faceIdHeight;

        int minX = x0 < x1 ? x0 : x1;
        int maxX = x0 < x1 ? x1 : x0;
        int minY = y0 < y1 ? y0 : y1;
        int maxY = y0 < y1 ? y1 : y0;
        if (minX < 0) minX = 0;
        if (minY < 0) minY = 0;
        if (maxX > viewWidth - 1) maxX = viewWidth - 1;
        if (maxY > viewHeight - 1) maxY = viewHeight - 1;
        if (minX > maxX || minY > maxY)
        {
            g_faceRefineStats.Status = FaceRefineStatus_NoCandidate;
            return;
        }

        const Handle(Graphic3d_Camera) camera = g_view->Camera();
        if (camera.IsNull() || !camera->IsOrthographic())
        {
            g_faceRefineStats.Status = FaceRefineStatus_Unsupported;
            return;
        }

        const bool faceIdBuildFailedSaved = g_faceIdBuildFailed;

        std::set<int> baseSet(baseFaceIds.begin(), baseFaceIds.end());
        std::set<long long> tileKeys;

        const std::chrono::steady_clock::time_point projectStarted = std::chrono::steady_clock::now();
        long long candidateBudget = 0;
        int candidateFaces = 0;
        bool capped = false;

        for (int faceId = 0; faceId < static_cast<int>(g_faces.size()); ++faceId)
        {
            if (baseSet.count(faceId) != 0) continue;
            const int collected = CollectFaceRefineCandidateNative(
                faceId, camera, viewWidth, viewHeight, minX, maxX, minY, maxY, tileKeys, candidateBudget);
            if (collected < 0) { capped = true; break; }
            if (collected == 0) continue;
            ++candidateFaces;
            if (candidateFaces > kFaceRefineMaxCandidates) { capped = true; break; }
        }

        g_faceRefineStats.CandidateFaces = candidateFaces;
        g_faceRefineStats.CandidatePixels = candidateBudget;
        g_faceRefineStats.ProjectMs = std::chrono::duration<double, std::milli>(
            std::chrono::steady_clock::now() - projectStarted).count();

        if (capped)
        {
            g_faceRefineStats.Status = FaceRefineStatus_Capped;
            return;   // 기본 결과만 쓴다 — 기존 동작보다 나빠지지 않는다
        }
        if (candidateFaces == 0 || tileKeys.empty())
        {
            g_faceRefineStats.Status = FaceRefineStatus_NoCandidate;
            return;
        }

        const int tilesX = (viewWidth + kFaceIdTileSize - 1) / kFaceIdTileSize;
        std::set<std::pair<int, int>> blocks;
        for (long long key : tileKeys)
        {
            const int tileX = static_cast<int>(key % tilesX);
            const int tileY = static_cast<int>(key / tilesX);
            blocks.insert(std::make_pair(tileX / kFaceRefineBlockTiles, tileY / kFaceRefineBlockTiles));
        }
        g_faceRefineStats.CandidateTiles = static_cast<int>(tileKeys.size());

        struct RegionNative { int X0; int Y0; int X1; int Y1; };
        std::vector<RegionNative> regions;
        {
            int runRow = -1;
            int runX0 = 0;
            int runX1 = -1;
            for (std::set<std::pair<int, int>>::const_iterator it = blocks.begin(); it != blocks.end(); ++it)
            {
                const int blockX = it->first;
                const int blockY = it->second;
                if (blockY != runRow || blockX != runX1 + 1)
                {
                    if (runRow >= 0)
                    {
                        RegionNative region;
                        region.X0 = runX0 * kFaceRefineBlockTiles * kFaceIdTileSize;
                        region.X1 = (runX1 + 1) * kFaceRefineBlockTiles * kFaceIdTileSize - 1;
                        region.Y0 = runRow * kFaceRefineBlockTiles * kFaceIdTileSize;
                        region.Y1 = (runRow + 1) * kFaceRefineBlockTiles * kFaceIdTileSize - 1;
                        regions.push_back(region);
                    }
                    runRow = blockY;
                    runX0 = blockX;
                    runX1 = blockX;
                }
                else
                {
                    runX1 = blockX;
                }
            }
            if (runRow >= 0)
            {
                RegionNative region;
                region.X0 = runX0 * kFaceRefineBlockTiles * kFaceIdTileSize;
                region.X1 = (runX1 + 1) * kFaceRefineBlockTiles * kFaceIdTileSize - 1;
                region.Y0 = runRow * kFaceRefineBlockTiles * kFaceIdTileSize;
                region.Y1 = (runRow + 1) * kFaceRefineBlockTiles * kFaceIdTileSize - 1;
                regions.push_back(region);
            }
        }

        long long outputPixels = 0;
        for (size_t i = 0; i < regions.size(); ++i)
        {
            RegionNative& region = regions[i];
            if (region.X0 < minX) region.X0 = minX;
            if (region.Y0 < minY) region.Y0 = minY;
            if (region.X1 > maxX) region.X1 = maxX;
            if (region.Y1 > maxY) region.Y1 = maxY;
            // 렌더 여유 1px — 사각형 밖 샘플은 판정에서 버린다.
            if (region.X0 > 0) --region.X0;
            if (region.Y0 > 0) --region.Y0;
            if (region.X1 < viewWidth - 1) ++region.X1;
            if (region.Y1 < viewHeight - 1) ++region.Y1;
            if (region.X0 > region.X1 || region.Y0 > region.Y1) continue;
            const long long width = static_cast<long long>(region.X1 - region.X0 + 1);
            const long long height = static_cast<long long>(region.Y1 - region.Y0 + 1);
            outputPixels += width * height *
                            static_cast<long long>(kFaceRefineSupersample) *
                            static_cast<long long>(kFaceRefineSupersample);
        }

        if (regions.empty())
        {
            g_faceRefineStats.Status = FaceRefineStatus_NoCandidate;
            return;
        }
        if (static_cast<int>(regions.size()) > kFaceRefineMaxPasses ||
            outputPixels > kFaceRefineMaxOutputPixels)
        {
            g_faceRefineStats.Status = FaceRefineStatus_Capped;
            return;
        }

        const std::chrono::steady_clock::time_point renderStarted = std::chrono::steady_clock::now();
        std::set<int> rendered;
        bool renderOk = true;
        for (size_t i = 0; i < regions.size(); ++i)
        {
            const RegionNative& region = regions[i];
            if (!RenderFaceIdRegionNative(region.X0, region.Y0, region.X1, region.Y1,
                                          viewWidth, viewHeight, minX, maxX, minY, maxY, rendered))
            {
                renderOk = false;
                break;
            }
            ++g_faceRefineStats.RenderPasses;
        }
        g_faceRefineStats.RenderMs = std::chrono::duration<double, std::milli>(
            std::chrono::steady_clock::now() - renderStarted).count();

        if (!renderOk || g_faceIdBuildFailed != faceIdBuildFailedSaved)
        {
            g_faceRefineStats.Status = FaceRefineStatus_RenderFailed;
            return;   // 보충 실패는 기본 결과 유지(하드 실패 아님)
        }

        for (std::set<int>::const_iterator it = rendered.begin(); it != rendered.end(); ++it)
            if (baseSet.count(*it) == 0) supplemental.insert(*it);

        g_faceRefineStats.OutputPixels = outputPixels;
        g_faceRefineStats.Status = supplemental.empty()
            ? FaceRefineStatus_NoCandidate : FaceRefineStatus_Ok;
        g_faceRefineStats.TotalMs = std::chrono::duration<double, std::milli>(
            std::chrono::steady_clock::now() - started).count();
    }

    /// 삼각분할이 바뀌면 face-ID 이미지와 보충 캐시는 모두 무효다.
    /// 모델 revision은 CAD 식별자로 쓰이므로 절대 올리지 않는다.
    void InvalidateFaceSelectionCachesNative()
    {
        ++g_meshEpoch;
        ReleaseFaceIdCacheNative();
        g_faceRefineStats = FaceRefineStatsNative();
    }
