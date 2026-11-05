// =====================================================================
// Face-ID 선택 캐시 — 사각형 선택에서 "화면에 실제로 보이는 면"만 고른다.
//
// 배경(OCCT 기본 동작):
//   AIS_InteractiveContext::SelectRectangle()은 selection frustum과
//   sensitive entity의 교집합만 계산한다. 렌더링 파이프라인과 독립이므로
//   화면에서 가려진 뒤쪽 면도 후보에 들어온다(OCCT 공식 설명).
//
// 해법:
//   표시 장면과 분리된 선택 전용 Z-layer에 면별 고유색(unlit) AIS_ColoredShape를
//   표시하고, V3d_View::ToPixMap()으로 FBO 렌더 결과를 받아 픽셀 색을 faceId로
//   되돌린다. 같은 Z-layer만 덤프(IsSingleLayer)하므로 모델/오버레이/러버밴드는
//   이미지에 섞이지 않고, 깊이 버퍼가 그대로 적용되어 가림이 정확하다.
//
// 캐시:
//   이미지는 카메라/모델/뷰포트가 바뀔 때만 다시 만든다. 색상·선택 변경은
//   재생성 사유가 아니다. 드래그 중에는 32x32 타일 요약과 경계 픽셀 스캔만
//   수행하므로 ray picking이나 ToPixMap 호출이 없다.
// =====================================================================

    struct FaceIdCameraKeyNative
    {
        double Eye[3];
        double Center[3];
        double Up[3];
        double Scale;
        double Aspect;
        int    Projection;
        int    Width;
        int    Height;
    };

    // 픽셀 코드 = faceId + 1 (0 = 배경). 채널당 8 간격/32단계 → 최대 32767면.
    // 8 간격이면 ±3 수준의 색 반올림에도 코드가 섞이지 않는다.
    const int   kFaceIdChannelStep = 8;
    const int   kFaceIdChannelMask = 31;
    const int   kFaceIdMaxCode = 32767;
    const int   kFaceIdTileSize = 32;

    Handle(AIS_ColoredShape)  g_faceIdShape;
    Handle(V3d_Viewer)        g_faceIdLayerOwner;   // 레이어 ID가 유효한 뷰어
    Graphic3d_ZLayerId        g_faceIdLayer = Graphic3d_ZLayerId_UNKNOWN;
    std::vector<unsigned int> g_faceIdCodes;              // top-down, 0 = 배경
    std::vector<std::vector<int>> g_faceIdTiles;          // 타일별 등장 faceId
    int                       g_faceIdWidth = 0;
    int                       g_faceIdHeight = 0;
    long long                 g_faceIdModelRevision = -1;
    bool                      g_faceIdCacheValid = false;
    FaceIdCameraKeyNative     g_faceIdCameraKey;
    long long                 g_faceIdBuildCount = 0;
    long long                 g_faceIdQueryCount = 0;
    bool                      g_faceIdBuildFailed = false;

    // 사각형 선택 프리뷰 세션 — 취소 시 되돌릴 기준 선택.
    // owner handle은 Redisplay마다 재계산되어 stale해질 수 있으므로 face ID로 보관한다.
    bool g_rectPreviewActive = false;
    std::vector<int> g_rectBaselineFaceIds;
    std::map<int, Handle(SelectMgr_EntityOwner)> g_rectOwnerMap;
    bool g_rectOwnerMapValid = false;
    std::vector<int> g_rectLastRequestedFaceIds;
    std::vector<int> g_rectLastAppliedFaceIds;
    bool g_rectHasApplied = false;

    Quantity_Color FaceIdColorNative(int code)
    {
        const int r = (code & kFaceIdChannelMask) * kFaceIdChannelStep;
        const int g = ((code >> 5) & kFaceIdChannelMask) * kFaceIdChannelStep;
        const int b = ((code >> 10) & kFaceIdChannelMask) * kFaceIdChannelStep;
        return Quantity_Color(r / 255.0, g / 255.0, b / 255.0, Quantity_TOC_sRGB);
    }

    int FaceIdFromPixelNative(unsigned char r, unsigned char g, unsigned char b)
    {
        const int lr = (static_cast<int>(r) + kFaceIdChannelStep / 2) / kFaceIdChannelStep;
        const int lg = (static_cast<int>(g) + kFaceIdChannelStep / 2) / kFaceIdChannelStep;
        const int lb = (static_cast<int>(b) + kFaceIdChannelStep / 2) / kFaceIdChannelStep;
        if (lr > kFaceIdChannelMask || lg > kFaceIdChannelMask || lb > kFaceIdChannelMask) return 0;
        return lr | (lg << 5) | (lb << 10);
    }

    void ReleaseFaceIdCacheNative()
    {
        if (!g_ctx.IsNull() && !g_faceIdShape.IsNull())
            g_ctx->Remove(g_faceIdShape, Standard_False);   // Erase된 객체도 context에는 남아 있다
        g_faceIdShape.Nullify();
        if (!g_viewer.IsNull() && !g_faceIdLayerOwner.IsNull() &&
            g_faceIdLayerOwner == g_viewer && g_faceIdLayer != Graphic3d_ZLayerId_UNKNOWN)
            g_viewer->RemoveZLayer(g_faceIdLayer);
        g_faceIdLayer = Graphic3d_ZLayerId_UNKNOWN;
        g_faceIdLayerOwner.Nullify();
        g_faceIdCodes.clear();
        g_faceIdCodes.shrink_to_fit();
        g_faceIdTiles.clear();
        g_faceIdTiles.shrink_to_fit();
        g_faceIdWidth = 0;
        g_faceIdHeight = 0;
        g_faceIdModelRevision = -1;
        g_faceIdCacheValid = false;
        g_faceIdBuildFailed = false;
    }

    void AbortRectanglePreviewNative()
    {
        g_rectPreviewActive = false;
        g_rectBaselineFaceIds.clear();
        g_rectOwnerMap.clear();
        g_rectOwnerMapValid = false;
        g_rectLastRequestedFaceIds.clear();
        g_rectLastAppliedFaceIds.clear();
        g_rectHasApplied = false;
    }

    /// <summary>지금 카메라/뷰포트 상태를 캐시 키로 기록한다.</summary>
    void CaptureFaceIdCameraKeyNative(Standard_Integer width, Standard_Integer height)
    {
        const Handle(Graphic3d_Camera) camera = g_view->Camera();
        const gp_Pnt eye = camera->Eye();
        const gp_Pnt center = camera->Center();
        const gp_Dir up = camera->Up();
        g_faceIdCameraKey.Eye[0] = eye.X();
        g_faceIdCameraKey.Eye[1] = eye.Y();
        g_faceIdCameraKey.Eye[2] = eye.Z();
        g_faceIdCameraKey.Center[0] = center.X();
        g_faceIdCameraKey.Center[1] = center.Y();
        g_faceIdCameraKey.Center[2] = center.Z();
        g_faceIdCameraKey.Up[0] = up.X();
        g_faceIdCameraKey.Up[1] = up.Y();
        g_faceIdCameraKey.Up[2] = up.Z();
        g_faceIdCameraKey.Scale = camera->Scale();
        g_faceIdCameraKey.Aspect = camera->Aspect();
        g_faceIdCameraKey.Projection = static_cast<int>(camera->ProjectionType());
        g_faceIdCameraKey.Width = width;
        g_faceIdCameraKey.Height = height;
    }

    bool SameFaceIdCameraNative(Standard_Integer width, Standard_Integer height)
    {
        const FaceIdCameraKeyNative current = g_faceIdCameraKey;
        if (current.Width != width || current.Height != height) return false;
        const Handle(Graphic3d_Camera) camera = g_view->Camera();
        const gp_Pnt eye = camera->Eye();
        const gp_Pnt center = camera->Center();
        const gp_Dir up = camera->Up();
        if (current.Eye[0] != eye.X() || current.Eye[1] != eye.Y() || current.Eye[2] != eye.Z()) return false;
        if (current.Center[0] != center.X() || current.Center[1] != center.Y() || current.Center[2] != center.Z()) return false;
        if (current.Up[0] != up.X() || current.Up[1] != up.Y() || current.Up[2] != up.Z()) return false;
        if (current.Scale != camera->Scale()) return false;
        if (current.Aspect != camera->Aspect()) return false;
        if (current.Projection != static_cast<int>(camera->ProjectionType())) return false;
        return true;
    }

    /// <summary>면별 고유색(unlit) 선택 전용 표시 객체. 프레젠테이션은 1회 계산 후 재사용한다.</summary>
    bool BuildFaceIdShapeNative()
    {
        if (!g_faceIdShape.IsNull()) return true;
        if (g_shape.IsNull() || g_faces.empty()) return false;
        if (g_faces.size() > static_cast<size_t>(kFaceIdMaxCode)) return false;

        Handle(AIS_ColoredShape) idShape = new AIS_ColoredShape(g_shape);
        idShape->SetDisplayMode(AIS_Shaded);
        idShape->Attributes()->SetFaceBoundaryDraw(Standard_False);
        for (size_t i = 0; i < g_faces.size(); ++i)
        {
            const int code = static_cast<int>(i) + 1;
            const Quantity_Color color = FaceIdColorNative(code);
            idShape->SetCustomColor(g_faces[i], color);
            const Handle(AIS_ColoredDrawer) style = idShape->CustomAspects(g_faces[i]);
            if (style.IsNull()) continue;
            style->SetupOwnShadingAspect();
            style->SetDisplayMode(AIS_Shaded);
            style->ShadingAspect()->SetColor(color);
            style->ShadingAspect()->SetTransparency(0.0);
            const Handle(Graphic3d_AspectFillArea3d) aspect = style->ShadingAspect()->Aspect();
            aspect->SetShadingModel(Graphic3d_TOSM_UNLIT);   // 조명을 곱하지 않아 색이 그대로 나온다
            aspect->SetInteriorStyle(Aspect_IS_SOLID);
            aspect->SetDrawEdges(Standard_False);            // 경계선이 ID 색을 덮지 않게
        }
        // 계산 비용이 큰 프레젠테이션은 여기서 한 번만 만들고 캐시한다.
        g_faceIdShape = idShape;
        return true;
    }

    /// <summary>ID 덤프 동안만 뷰 배경을 불투명 검정으로 바꾼다. 배경 회색이 유효 코드로
    /// 해석되면 빈 영역이 면으로 선택되므로, 성공/예외 모두 원래 배경으로 복원한다.</summary>
    struct FaceIdBackgroundGuardNative
    {
        Handle(V3d_View) View;
        Handle(Graphic3d_CView) Base;
        Quantity_Color Color;
        Graphic3d_TypeOfBackground Type;
        bool Active;

        FaceIdBackgroundGuardNative()
            : Color(0.0, 0.0, 0.0, Quantity_TOC_RGB), Type(Graphic3d_TOB_NONE), Active(false) {}

        void Apply()
        {
            if (View.IsNull() || Base.IsNull()) return;
            Color = View->BackgroundColor();
            Type = Base->BackgroundType();
            View->SetBackgroundColor(Quantity_Color(0.0, 0.0, 0.0, Quantity_TOC_RGB));
            Base->SetBackgroundType(Graphic3d_TOB_NONE);
            Active = true;
        }

        ~FaceIdBackgroundGuardNative()
        {
            if (!Active || View.IsNull()) return;
            try
            {
                View->SetBackgroundColor(Color);
                if (!Base.IsNull()) Base->SetBackgroundType(Type);
            }
            catch (const Standard_Failure&) { }
        }
    };

    bool EnsureFaceIdCacheNative()
    {
        if (g_ctx.IsNull() || g_view.IsNull() || g_viewer.IsNull() || g_shape.IsNull()) return false;
        if (g_faces.empty()) return false;
        if (g_faceIdBuildFailed) return false;

        Standard_Integer width = 0, height = 0;
        g_view->Window()->Size(width, height);
        if (width <= 0 || height <= 0) return false;

        if (g_faceIdCacheValid &&
            g_faceIdModelRevision == g_modelRevision &&
            g_faceIdWidth == width && g_faceIdHeight == height &&
            SameFaceIdCameraNative(width, height))
            return true;

        g_faceIdCacheValid = false;
        try
        {
            if (!BuildFaceIdShapeNative()) return false;

            if (g_faceIdLayer == Graphic3d_ZLayerId_UNKNOWN ||
                g_faceIdLayerOwner.IsNull() || g_faceIdLayerOwner != g_viewer)
            {
                g_faceIdLayer = Graphic3d_ZLayerId_UNKNOWN;
                if (!g_viewer->AddZLayer(g_faceIdLayer)) return false;
                g_faceIdLayerOwner = g_viewer;   // 뷰어가 바뀌면 레이어 ID도 다시 만든다
            }

            FaceIdBackgroundGuardNative backgroundGuard;
            backgroundGuard.View = g_view;
            backgroundGuard.Base = g_view->View();
            backgroundGuard.Apply();

            g_ctx->SetZLayer(g_faceIdShape, g_faceIdLayer);
            // 선택 모드 -1: 표시만 하고 선택 대상에는 넣지 않는다.
            g_ctx->Display(g_faceIdShape, AIS_Shaded, -1, Standard_False);

            Image_PixMap image;
            V3d_ImageDumpOptions options;
            options.Width = width;
            options.Height = height;
            options.BufferType = Graphic3d_BT_RGB;
            options.ToAdjustAspect = Standard_True;   // 창과 같은 w/h이므로 종횡비 왜곡 없음
            options.TargetZLayerId = g_faceIdLayer;
            options.IsSingleLayer = Standard_True;    // 이 레이어만 덤프 → 오버레이/러버밴드 배제
            const Standard_Boolean dumped = g_view->ToPixMap(image, options);

            if (g_ctx->IsDisplayed(g_faceIdShape))
                g_ctx->Erase(g_faceIdShape, Standard_False);   // 창 Redraw 없이 즉시 원복

            if (!dumped) return false;
            if (image.IsEmpty() || image.Format() != Image_Format_RGB) return false;
            if (static_cast<int>(image.SizeX()) != width || static_cast<int>(image.SizeY()) != height) return false;

            g_faceIdCodes.assign(static_cast<size_t>(width) * static_cast<size_t>(height), 0u);
            // Image_PixMap::Row()는 IsTopDown 플래그를 내부에서 정규화한다.
            // 여기서 height-1-y를 다시 적용하면 상하가 뒤집힌다.
            for (int y = 0; y < height; ++y)
            {
                const Standard_Byte* row = image.Row(static_cast<Standard_Size>(y));
                if (row == nullptr) return false;
                unsigned int* dst = &g_faceIdCodes[static_cast<size_t>(y) * static_cast<size_t>(width)];
                for (int x = 0; x < width; ++x)
                {
                    const Standard_Byte* pixel = row + static_cast<Standard_Size>(x) * 3;
                    const int code = FaceIdFromPixelNative(pixel[0], pixel[1], pixel[2]);
                    dst[x] = (code > 0 && code <= static_cast<int>(g_faces.size()))
                                 ? static_cast<unsigned int>(code)
                                 : 0u;
                }
            }

            const int tilesX = (width + kFaceIdTileSize - 1) / kFaceIdTileSize;
            const int tilesY = (height + kFaceIdTileSize - 1) / kFaceIdTileSize;
            g_faceIdTiles.assign(static_cast<size_t>(tilesX) * static_cast<size_t>(tilesY), std::vector<int>());
            for (int ty = 0; ty < tilesY; ++ty)
            {
                for (int tx = 0; tx < tilesX; ++tx)
                {
                    std::vector<int>& tile = g_faceIdTiles[static_cast<size_t>(ty) * tilesX + tx];
                    const int y0 = ty * kFaceIdTileSize;
                    const int y1 = std::min(y0 + kFaceIdTileSize, height);
                    const int x0 = tx * kFaceIdTileSize;
                    const int x1 = std::min(x0 + kFaceIdTileSize, width);
                    for (int y = y0; y < y1; ++y)
                    {
                        const unsigned int* src = &g_faceIdCodes[static_cast<size_t>(y) * static_cast<size_t>(width)];
                        for (int x = x0; x < x1; ++x)
                            if (src[x] != 0u) tile.push_back(static_cast<int>(src[x]) - 1);
                    }
                    if (tile.size() > 1)
                    {
                        std::sort(tile.begin(), tile.end());
                        tile.erase(std::unique(tile.begin(), tile.end()), tile.end());
                    }
                }
            }

            g_faceIdWidth = width;
            g_faceIdHeight = height;
            g_faceIdModelRevision = g_modelRevision;
            CaptureFaceIdCameraKeyNative(width, height);
            g_faceIdCacheValid = true;
            ++g_faceIdBuildCount;
            return true;
        }
        catch (const Standard_Failure&)
        {
            if (!g_faceIdShape.IsNull() && !g_ctx.IsNull() && g_ctx->IsDisplayed(g_faceIdShape))
                g_ctx->Erase(g_faceIdShape, Standard_False);
            g_faceIdBuildFailed = true;   // 같은 상태에서 무한 재시도하지 않는다
            return false;
        }
    }

    /// <summary>사각형 안에서 화면에 보이는 면 ID만 모은다. false = 캐시/렌더 실패.</summary>
    bool QueryVisibleFaceIdsNative(int x0, int y0, int x1, int y1, std::vector<int>& out)
    {
        out.clear();
        if (!EnsureFaceIdCacheNative()) return false;
        ++g_faceIdQueryCount;

        int minX = std::min(x0, x1), maxX = std::max(x0, x1);
        int minY = std::min(y0, y1), maxY = std::max(y0, y1);
        minX = std::max(0, minX);
        minY = std::max(0, minY);
        maxX = std::min(g_faceIdWidth - 1, maxX);
        maxY = std::min(g_faceIdHeight - 1, maxY);
        if (minX > maxX || minY > maxY) return true;

        std::set<int> unique;
        const int tilesX = (g_faceIdWidth + kFaceIdTileSize - 1) / kFaceIdTileSize;
        const int tileX0 = minX / kFaceIdTileSize, tileX1 = maxX / kFaceIdTileSize;
        const int tileY0 = minY / kFaceIdTileSize, tileY1 = maxY / kFaceIdTileSize;
        for (int ty = tileY0; ty <= tileY1; ++ty)
        {
            for (int tx = tileX0; tx <= tileX1; ++tx)
            {
                const int px0 = tx * kFaceIdTileSize;
                const int py0 = ty * kFaceIdTileSize;
                const int px1 = std::min(px0 + kFaceIdTileSize - 1, g_faceIdWidth - 1);
                const int py1 = std::min(py0 + kFaceIdTileSize - 1, g_faceIdHeight - 1);
                if (px0 >= minX && px1 <= maxX && py0 >= minY && py1 <= maxY)
                {
                    const std::vector<int>& tile =
                        g_faceIdTiles[static_cast<size_t>(ty) * tilesX + tx];
                    unique.insert(tile.begin(), tile.end());
                    continue;
                }
                const int sx0 = std::max(px0, minX), sx1 = std::min(px1, maxX);
                const int sy0 = std::max(py0, minY), sy1 = std::min(py1, maxY);
                for (int y = sy0; y <= sy1; ++y)
                {
                    const unsigned int* row =
                        &g_faceIdCodes[static_cast<size_t>(y) * static_cast<size_t>(g_faceIdWidth)];
                    for (int x = sx0; x <= sx1; ++x)
                        if (row[x] != 0u) unique.insert(static_cast<int>(row[x]) - 1);
                }
            }
        }
        out.assign(unique.begin(), unique.end());
        return true;
    }

    /// <summary>프리뷰 세션 동안 owner handle을 재사용한다. Begin의 Redisplay 이후
    /// 첫 적용에서 구축하고, 선택 변경(Select)만으로는 재계산되지 않는다.</summary>
    bool EnsureRectOwnerMapNative()
    {
        if (g_rectOwnerMapValid) return true;
        g_rectOwnerMap.clear();
        g_rectOwnerMapValid = BuildFaceOwnerMapNative(g_rectOwnerMap);
        return g_rectOwnerMapValid;
    }

    /// <summary>선택 owner 배열을 Replace로 적용한다. 같은 집합을 반복 적용하지 않고,
    /// applied는 실제 AIS 선택에서 다시 수집한 face id다. true = 선택 상태 변경됨.</summary>
    bool ApplySelectedFaceIdsNative(const std::vector<int>& faceIds, std::vector<int>& applied)
    {
        applied.clear();
        if (g_ctx.IsNull()) return false;
        if (g_rectHasApplied && faceIds == g_rectLastRequestedFaceIds)
        {
            applied = g_rectLastAppliedFaceIds;
            return false;
        }

        const bool mapReady = EnsureRectOwnerMapNative();
        if (!mapReady && !faceIds.empty()) return false;   // 매핑 실패 시 기존 선택을 건드리지 않는다

        std::vector<Handle(SelectMgr_EntityOwner)> list;
        list.reserve(faceIds.size());
        for (int faceId : faceIds)
        {
            const auto it = g_rectOwnerMap.find(faceId);
            if (it == g_rectOwnerMap.end()) continue;
            list.push_back(it->second);
        }

        if (list.empty())
        {
            g_ctx->ClearSelected(Standard_False);
        }
        else
        {
            NCollection_Array1<Handle(SelectMgr_EntityOwner)> array(1, static_cast<Standard_Integer>(list.size()));
            for (size_t i = 0; i < list.size(); ++i)
                array[static_cast<Standard_Integer>(i) + 1] = list[i];
            g_ctx->Select(array, AIS_SelectionScheme_Replace);
        }

        const std::set<int> actual = CollectSelectedFaceIdsNative();
        applied.assign(actual.begin(), actual.end());
        g_rectLastRequestedFaceIds = faceIds;
        g_rectLastAppliedFaceIds = applied;
        g_rectHasApplied = true;
        return true;
    }

    /// <summary>프리뷰 하이라이트(LocalSelected) 표시 여부. 스타일 변경은 기존 프레젠테이션에
    /// 자동 반영되지 않으므로 호출 측이 Unhilight/Hilight로 재구성한다.</summary>
    void SetPreviewHighlightVisibleNative(bool visible)
    {
        if (g_ctx.IsNull()) return;
        const Handle(Prs3d_Drawer) style = g_ctx->HighlightStyle(Prs3d_TypeOfHighlight_LocalSelected);
        if (style.IsNull()) return;
        const Standard_Real transparency = visible ? 0.0 : 1.0;
        style->SetTransparency(static_cast<Standard_ShortReal>(transparency));
        style->ShadingAspect()->SetTransparency(transparency);
    }

    /// <summary>선택 색상 오버라이드를 지운다(확정 영역 색은 보존). 실제 변경이 있을 때만 재계산한다.</summary>
    void ClearSelectionColorOverridesNative()
    {
        Handle(AIS_ColoredShape) colored = Handle(AIS_ColoredShape)::DownCast(g_aisShape);
        if (colored.IsNull()) return;
        bool changed = false;
        for (int faceId : g_selectedFaceIds)
            if (faceId >= 0 && faceId < static_cast<int>(g_faces.size()) &&
                g_confirmedFaceColors.count(faceId) == 0)
            {
                colored->UnsetCustomAspects(g_faces[faceId], Standard_True);
                changed = true;
            }
        g_selectedFaceIds.clear();
        if (changed) g_ctx->Redisplay(g_aisShape, Standard_False);
    }

    bool IsRectanglePreviewActiveNative()
    {
        return g_rectPreviewActive;
    }

    void BeginRectanglePreviewNative()
    {
        if (g_ctx.IsNull() || g_aisShape.IsNull()) return;
        if (g_rectPreviewActive) return;
        g_rectPreviewActive = true;

        // 기준 선택은 face ID로 보관한다. Begin 안의 Redisplay가 owner handle을 교체할 수 있다.
        const std::set<int> baseline = CollectSelectedFaceIdsNative();
        g_rectBaselineFaceIds.assign(baseline.begin(), baseline.end());
        g_rectOwnerMap.clear();
        g_rectOwnerMapValid = false;
        g_rectLastRequestedFaceIds.clear();
        g_rectLastAppliedFaceIds.clear();
        g_rectHasApplied = false;

        // 드래그 중에는 네이티브 하이라이트가 현재 프리뷰 집합을 표시한다.
        SetPreviewHighlightVisibleNative(true);
        g_ctx->UnhilightSelected(Standard_False);
        g_ctx->HilightSelected(Standard_False);   // 기준 선택도 보이는 스타일로 재구성
        ClearSelectionColorOverridesNative();     // 초록 오버라이드는 프리뷰와 겹치므로 제거
    }

    void PreviewFacesRectangleNative(int x0, int y0, int x1, int y1)
    {
        if (g_ctx.IsNull() || g_view.IsNull()) return;
        BeginRectanglePreviewNative();
        std::vector<int> visible;
        if (!QueryVisibleFaceIdsNative(x0, y0, x1, y1, visible)) return;   // 이전 프리뷰 유지
        std::vector<int> applied;
        if (ApplySelectedFaceIdsNative(visible, applied))
            g_view->Redraw();           // 선택 하이라이트가 바뀌면 전체 장면 갱신
        else
            g_view->RedrawImmediate();  // 면 집합은 그대로, 테두리만 이동
    }

    /// <summary>프리뷰 종료. cancel=true면 드래그 시작 시점의 선택으로 되돌린다.</summary>
    void EndRectanglePreviewNative(bool cancel)
    {
        if (g_ctx.IsNull()) return;
        if (!g_rectPreviewActive) return;
        if (cancel)
        {
            if (EnsureRectOwnerMapNative())
            {
                std::vector<Handle(SelectMgr_EntityOwner)> list;
                list.reserve(g_rectBaselineFaceIds.size());
                for (int faceId : g_rectBaselineFaceIds)
                {
                    const auto it = g_rectOwnerMap.find(faceId);
                    if (it != g_rectOwnerMap.end()) list.push_back(it->second);
                }
                if (list.empty())
                {
                    g_ctx->ClearSelected(Standard_False);
                }
                else
                {
                    NCollection_Array1<Handle(SelectMgr_EntityOwner)> array(
                        1, static_cast<Standard_Integer>(list.size()));
                    for (size_t i = 0; i < list.size(); ++i)
                        array[static_cast<Standard_Integer>(i) + 1] = list[i];
                    g_ctx->Select(array, AIS_SelectionScheme_Replace);
                }
            }
            else if (!g_rectBaselineFaceIds.empty())
            {
                // owner를 복원할 수 없으면 프리뷰 선택을 남기지 않는다.
                g_ctx->ClearSelected(Standard_False);
            }
        }
        SetPreviewHighlightVisibleNative(false);
        g_ctx->UnhilightSelected(Standard_False);   // 프리뷰 하이라이트 프레젠테이션 제거
        g_rectPreviewActive = false;
        g_rectBaselineFaceIds.clear();
        g_rectOwnerMap.clear();
        g_rectOwnerMapValid = false;
        g_rectLastRequestedFaceIds.clear();
        g_rectLastAppliedFaceIds.clear();
        g_rectHasApplied = false;
        UpdateSelectedFaceColors();                 // 실제 선택 기준으로 초록/확정색 재적용
        g_ctx->ClearDetected(Standard_False);
        g_lastHoverFace = -1;
        if (!g_view.IsNull()) g_view->Redraw();
    }
