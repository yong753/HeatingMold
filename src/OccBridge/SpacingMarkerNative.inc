// Included inside the native anonymous namespace (OccBridgeApi.cpp), after the OCCT includes.
// 화면 표시 전용 발열사 간격 마커. 확정 경로·길이·전기 판정에 절대 쓰지 않는다.
// 생성/교체는 전체 세트를 모두 만든 뒤 한 번에 swap하는 원자적 교체다.

void ClearSpacingMarkersNative()
{
    if (!g_ctx.IsNull())
    {
        for (const auto& entry : g_spacingMarkers)
        {
            const SpacingMarkerOverlay& overlay = entry.second;
            if (!overlay.tick.IsNull()) g_ctx->Remove(overlay.tick, Standard_False);
            if (!overlay.dimension.IsNull()) g_ctx->Remove(overlay.dimension, Standard_False);
            if (!overlay.label.IsNull()) g_ctx->Remove(overlay.label, Standard_False);
        }
    }
    g_spacingMarkers.clear();
}

static void LogSpacingMarkersNative(size_t count, const char* outcome)
{
    char path[MAX_PATH] = {};
    DWORD length = ::GetEnvironmentVariableA("HEATINGMOLD_OCC_LOG", path, MAX_PATH);
    if (length == 0 || length >= MAX_PATH) return;
    std::ofstream stream(path, std::ios::app);
    if (!stream.is_open()) return;
    stream << "spacing-markers: " << outcome << " count=" << count << "\n";
}

static void LogSpacingRejectNative(const char* reason)
{
    char path[MAX_PATH] = {};
    DWORD length = ::GetEnvironmentVariableA("HEATINGMOLD_OCC_LOG", path, MAX_PATH);
    if (length == 0 || length >= MAX_PATH) return;
    std::ofstream stream(path, std::ios::app);
    if (!stream.is_open()) return;
    stream << "spacing-markers: rejected " << reason << "\n";
}

static void DisplaySpacingMarkerObject(const Handle(AIS_InteractiveObject)& object)
{
    if (g_ctx.IsNull() || object.IsNull()) return;
    g_ctx->Display(object, Standard_False);
    g_ctx->SetZLayer(object, Graphic3d_ZLayerId_Topmost);
    g_ctx->Deactivate(object, Standard_False);
}

// anchors: 표시할 마커 위치 3개(x,y,z)/개, targets: 이웃 레인의 대응 위치 3개/개,
// texts: 마커 수치 문자열, rgb: 3개/개, height: 모델 공간 문자 높이(mm).
// 반환값 false는 표시 실패이며, 호출자는 기존 확정 기하를 유지해야 한다.
bool ReplaceSpacingMarkersNative(long long contextId, long long revision,
    const std::vector<double>& anchors, const std::vector<double>& targets,
    const std::vector<std::string>& texts, const std::vector<double>& rgb, double height)
{
    if (!std::isfinite(height) || height <= 0.0) return false;
    if (anchors.size() % 3 != 0) return false;
    const size_t count = texts.size();
    if (anchors.size() != count * 3 || targets.size() != count * 3 || rgb.size() != count * 3)
    {
        LogSpacingRejectNative("sizes");
        return false;
    }

    for (double value : anchors)
        if (!std::isfinite(value)) return false;
    for (double value : targets)
        if (!std::isfinite(value)) return false;
    for (double value : rgb)
        if (!std::isfinite(value) || value < 0.0 || value > 1.0) return false;

    if (FindWorking(contextId, revision) == nullptr)
    {
        LogSpacingRejectNative("working");
        return false;
    }
    if (count == 0)
    {
        ClearSpacingMarkersNative();
        LogSpacingMarkersNative(0, "cleared");
        return true;
    }
    if (g_ctx.IsNull())
    {
        LogSpacingRejectNative("ctx");
        return false;
    }

    std::map<int, SpacingMarkerOverlay> next;
    auto discard = [&next]()
    {
        if (!g_ctx.IsNull())
        {
            for (const auto& entry : next)
            {
                const SpacingMarkerOverlay& overlay = entry.second;
                if (!overlay.tick.IsNull()) g_ctx->Remove(overlay.tick, Standard_False);
                if (!overlay.dimension.IsNull()) g_ctx->Remove(overlay.dimension, Standard_False);
                if (!overlay.label.IsNull()) g_ctx->Remove(overlay.label, Standard_False);
            }
        }
        next.clear();
    };

    try
    {
        for (size_t i = 0; i < count; ++i)
        {
            const gp_Pnt anchor(anchors[i * 3], anchors[i * 3 + 1], anchors[i * 3 + 2]);
            const gp_Pnt target(targets[i * 3], targets[i * 3 + 1], targets[i * 3 + 2]);
            gp_Vec direction(anchor, target);
            if (direction.Magnitude() <= 1e-9)
            {
                LogSpacingRejectNative("direction");
                discard();
                return false;
            }

            gp_Vec normal(0.0, 0.0, 1.0);
            if (direction.IsParallel(normal, 1e-9)) normal = gp_Vec(1.0, 0.0, 0.0);
            gp_Vec perpendicular = direction.Crossed(normal);
            perpendicular.Normalize();
            perpendicular.Multiply(height * 0.5);

            const Quantity_Color color(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2],
                Quantity_TOC_RGB);

            const gp_Pnt tickA(anchor.X() - perpendicular.X(), anchor.Y() - perpendicular.Y(),
                anchor.Z() - perpendicular.Z());
            const gp_Pnt tickB(anchor.X() + perpendicular.X(), anchor.Y() + perpendicular.Y(),
                anchor.Z() + perpendicular.Z());

            Handle(AIS_Line) tick = new AIS_Line(new Geom_CartesianPoint(tickA),
                new Geom_CartesianPoint(tickB));
            tick->SetColor(color);
            tick->SetWidth(2.0);

            Handle(AIS_Line) dimension = new AIS_Line(new Geom_CartesianPoint(anchor),
                new Geom_CartesianPoint(target));
            dimension->SetColor(color);
            dimension->SetWidth(1.0);

            Handle(AIS_TextLabel) label = new AIS_TextLabel();
            label->SetText(TCollection_ExtendedString(texts[i].c_str()));
            const gp_Pnt textPosition(
                anchor.X() + direction.X() * 0.5 + perpendicular.X() * 0.75,
                anchor.Y() + direction.Y() * 0.5 + perpendicular.Y() * 0.75,
                anchor.Z() + direction.Z() * 0.5 + perpendicular.Z() * 0.75);
            label->SetPosition(textPosition);
            label->SetHJustification(Graphic3d_HTA_CENTER);
            label->SetVJustification(Graphic3d_VTA_CENTER);
            label->SetZoomable(Standard_True);
            label->SetHeight(height);
            label->SetColor(color);

            DisplaySpacingMarkerObject(tick);
            DisplaySpacingMarkerObject(dimension);
            DisplaySpacingMarkerObject(label);

            SpacingMarkerOverlay overlay;
            overlay.tick = tick;
            overlay.dimension = dimension;
            overlay.label = label;
            overlay.contextId = contextId;
            overlay.revision = revision;
            next[static_cast<int>(i)] = overlay;
        }
    }
    catch (const Standard_Failure&)
    {
        LogSpacingRejectNative("exception");
        discard();
        return false;
    }

    ClearSpacingMarkersNative();
    g_spacingMarkers.swap(next);
    LogSpacingMarkersNative(count, "replaced");
    return true;
}
