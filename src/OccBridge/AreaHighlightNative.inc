// Included inside the native anonymous namespace (OccBridgeApi.cpp) after
// WorkingSideNative.inc so it can reuse g_faces / FindWorking / g_ctx helpers.
//
// 제외영역(2D 사각형) 차감 발열면 채움 표시.
// 확정 면의 "표시 삼각분할"을 작업 프레임 2D로 사영해 제외 사각형을 뺀 뒤,
// 원본 삼각형의 무게중심 좌표로 3D 조각을 복원한다. 곡면 드레이핑이나
// BRep 면 절단은 쓰지 않는다. 표시 정밀도(메시)만큼만 잘린다.

struct AreaVec2 { double x, y; };
struct AreaRect2 { double minX, minY, maxX, maxY; };
// 분할 영역 표시용 축 정렬 반평면. axis: 0=x, 1=y. keepLess: 좌표 <= limit 유지.
struct AreaHalfPlane { int axis = 0; double limit = 0.0; bool keepLess = true; };

struct AreaHighlightEntry
{
    long long modelRevision = 0;
    long long workingId = 0;
    std::vector<int> faceIds;
    Quantity_Color color{0.9, 0.16, 0.16, Quantity_TOC_RGB};
    // Stage는 pending 메시만 갱신한다. 기존 커밋 표시는 Commit 성공 전까지 유지된다.
    Handle(Poly_Triangulation) mesh;
    Handle(AIS_Triangulation) presentation;
    int triangleCount = 0;
    bool committed = false;
    // 빈 메시(null + 0 삼각형)와 "스테이징 안 됨"을 구분한다.
    bool hasPending = false;
};
std::map<int, AreaHighlightEntry> g_areaHighlights;

// 조각/삼각형 상한. 폭주하는 제외 조합에서 UI 스레드를 붙잡지 않는다.
const int kAreaHighlightTriangleCap = 500000;
const double kAreaHighlightAreaEps = 1e-12;

double AreaPolygonArea2D(const std::vector<AreaVec2>& polygon)
{
    if (polygon.size() < 3) return 0.0;
    double sum = 0.0;
    for (size_t i = 0, j = polygon.size() - 1; i < polygon.size(); j = i++)
        sum += polygon[j].x * polygon[i].y - polygon[i].x * polygon[j].y;
    return std::fabs(sum) * 0.5;
}

// Sutherland-Hodgman half-plane clip. axis: 0=x, 1=y. keepLess: 좌표 <= value 유지.
void ClipHalfPlane2D(const std::vector<AreaVec2>& in, int axis, double value, bool keepLess,
                     std::vector<AreaVec2>& out)
{
    out.clear();
    if (in.size() < 3 || !std::isfinite(value)) return;
    const double eps = 1e-9;
    auto coordinate = [axis](const AreaVec2& p) { return axis == 0 ? p.x : p.y; };
    auto inside = [&](double c) { return keepLess ? c <= value + eps : c >= value - eps; };
    AreaVec2 previous = in.back();
    double previousValue = coordinate(previous);
    bool previousInside = inside(previousValue);
    for (const AreaVec2& current : in)
    {
        const double currentValue = coordinate(current);
        const bool currentInside = inside(currentValue);
        if (currentInside != previousInside)
        {
            const double delta = currentValue - previousValue;
            if (std::fabs(delta) > 1e-15)
            {
                const double t = (value - previousValue) / delta;
                AreaVec2 crossing;
                if (axis == 0)
                {
                    crossing.x = value;
                    crossing.y = previous.y + (current.y - previous.y) * t;
                }
                else
                {
                    crossing.y = value;
                    crossing.x = previous.x + (current.x - previous.x) * t;
                }
                out.push_back(crossing);
            }
        }
        if (currentInside) out.push_back(current);
        previous = current; previousValue = currentValue; previousInside = currentInside;
    }
}

void AppendConvexFragment2D(const std::vector<AreaVec2>& polygon, std::vector<std::vector<AreaVec2>>& out)
{
    if (polygon.size() < 3 || AreaPolygonArea2D(polygon) < kAreaHighlightAreaEps) return;
    out.push_back(polygon);
}

// 볼록 다각형 - 사각형 = 최대 4개의 볼록 조각.
//   x < minX | x > maxX | (y < minY 이고 x <= maxX) | (y > maxY 이고 x >= minX)
// 네 조각은 경계선만 공유하고 내부는 겹치지 않는다. 겹침이 없으므로 z-fighting도 없다.
void SubtractRectFromConvex(const std::vector<AreaVec2>& polygon, const AreaRect2& rect,
                            std::vector<std::vector<AreaVec2>>& out)
{
    out.clear();
    if (polygon.size() < 3) return;
    double minX = polygon[0].x, maxX = polygon[0].x, minY = polygon[0].y, maxY = polygon[0].y;
    for (const AreaVec2& p : polygon)
    {
        if (p.x < minX) minX = p.x;
        if (p.x > maxX) maxX = p.x;
        if (p.y < minY) minY = p.y;
        if (p.y > maxY) maxY = p.y;
    }
    if (maxX <= rect.minX || minX >= rect.maxX || maxY <= rect.minY || minY >= rect.maxY)
    {
        out.push_back(polygon);   // 교차 없음: 원본 그대로
        return;
    }
    std::vector<AreaVec2> part, clipped;
    ClipHalfPlane2D(polygon, 0, rect.minX, true, part);
    AppendConvexFragment2D(part, out);
    ClipHalfPlane2D(polygon, 0, rect.maxX, false, part);
    AppendConvexFragment2D(part, out);
    ClipHalfPlane2D(polygon, 1, rect.minY, true, part);
    ClipHalfPlane2D(part, 0, rect.maxX, true, clipped);
    AppendConvexFragment2D(clipped, out);
    ClipHalfPlane2D(polygon, 1, rect.maxY, false, part);
    ClipHalfPlane2D(part, 0, rect.minX, false, clipped);
    AppendConvexFragment2D(clipped, out);
}

void SubtractRectsFromConvex(const std::vector<AreaVec2>& polygon, const std::vector<AreaRect2>& rects,
                             std::vector<std::vector<AreaVec2>>& out)
{
    out.clear();
    if (polygon.size() < 3) return;
    out.push_back(polygon);
    for (const AreaRect2& rect : rects)
    {
        std::vector<std::vector<AreaVec2>> next;
        for (const auto& fragment : out)
        {
            std::vector<std::vector<AreaVec2>> pieces;
            SubtractRectFromConvex(fragment, rect, pieces);
            for (auto& piece : pieces) next.push_back(std::move(piece));
        }
        out.swap(next);
        if (out.empty()) return;
    }
}

// 원본 2D 삼각형의 무게중심 좌표로 3D 점을 복원한다. 조각 정점은 클리핑으로 생긴
// 삼각형 내부/경계 점이므로 가중치가 [0,1]을 크게 벗어나면 수치 오류로 본다.
bool AreaBarycentricTo3D(const AreaVec2& a, const AreaVec2& b, const AreaVec2& c,
    const gp_Pnt& pa, const gp_Pnt& pb, const gp_Pnt& pc, double det,
    const AreaVec2& p, gp_Pnt& out)
{
    if (!std::isfinite(det) || std::fabs(det) < 1e-12) return false;
    const double wb = ((p.x - a.x) * (c.y - a.y) - (c.x - a.x) * (p.y - a.y)) / det;
    const double wc = ((b.x - a.x) * (p.y - a.y) - (p.x - a.x) * (b.y - a.y)) / det;
    const double wa = 1.0 - wb - wc;
    if (!std::isfinite(wa) || !std::isfinite(wb) || !std::isfinite(wc)) return false;
    const double tolerance = 1e-2;   // 1e-9 클리핑 eps가 슬리버 삼각형에서 증폭될 수 있다
    if (wa < -tolerance || wb < -tolerance || wc < -tolerance ||
        wa > 1.0 + tolerance || wb > 1.0 + tolerance || wc > 1.0 + tolerance) return false;
    out = gp_Pnt(pa.X() * wa + pb.X() * wb + pc.X() * wc,
                 pa.Y() * wa + pb.Y() * wb + pc.Y() * wc,
                 pa.Z() * wa + pb.Z() * wb + pc.Z() * wc);
    return std::isfinite(out.X()) && std::isfinite(out.Y()) && std::isfinite(out.Z());
}

// faceIds의 표시 삼각분할을 프레임 2D에서 rects로 잘라 남은 조각을 하나의
// Poly_Triangulation으로 만든다. 삼각형마다 정점을 3개씩 복제해 평면 음영을 얻는다.
// 제외로 전부 사라지면 triangleCount=0, mesh=null (정상 결과).
bool BuildAreaHighlightMeshNative(const std::vector<int>& faceIds, const std::vector<AreaRect2>& rects,
    const std::vector<AreaHalfPlane>& planes,
    const gp_Pnt& origin, const gp_Dir& axisX, const gp_Dir& axisY,
    Handle(Poly_Triangulation)& mesh, int& triangleCount, std::string& diagnostic)
{
    diagnostic.clear();
    mesh.Nullify();
    triangleCount = 0;
    if (faceIds.empty() || (rects.empty() && planes.empty()))
    {
        diagnostic = "empty-input";
        return false;
    }
    const gp_Vec xAxis(axisX), yAxis(axisY);
    std::vector<gp_Pnt> nodes;
    std::vector<Poly_Triangle> triangles;
    size_t sourceTriangles = 0;
    int emitted = 0;
    try
    {
        for (int faceId : faceIds)
        {
            if (faceId < 0 || faceId >= static_cast<int>(g_faces.size()))
            {
                diagnostic = "face-index " + std::to_string(faceId);
                return false;
            }
            TopLoc_Location location;
            Handle(Poly_Triangulation) source = BRep_Tool::Triangulation(g_faces[faceId], location);
            if (source.IsNull()) continue;   // 삼각분할이 없는 면은 건너뛴다
            const gp_Trsf transform = location.Transformation();
            for (int i = 1; i <= source->NbTriangles(); ++i)
            {
                int n1 = 0, n2 = 0, n3 = 0;
                source->Triangle(i).Get(n1, n2, n3);
                const gp_Pnt pa = source->Node(n1).Transformed(transform);
                const gp_Pnt pb = source->Node(n2).Transformed(transform);
                const gp_Pnt pc = source->Node(n3).Transformed(transform);
                ++sourceTriangles;
                // 2D 사영은 ProjectionFrame과 같은 규약: dot(p-origin, axis).
                const gp_Vec da(origin, pa), db(origin, pb), dc(origin, pc);
                const AreaVec2 a{da.Dot(xAxis), da.Dot(yAxis)};
                const AreaVec2 b{db.Dot(xAxis), db.Dot(yAxis)};
                const AreaVec2 c{dc.Dot(xAxis), dc.Dot(yAxis)};
                if (!std::isfinite(a.x) || !std::isfinite(a.y) || !std::isfinite(b.x) ||
                    !std::isfinite(b.y) || !std::isfinite(c.x) || !std::isfinite(c.y)) continue;
                const double det = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
                if (!std::isfinite(det) || std::fabs(det) < 1e-12) continue;   // 프레임에 수직
                const gp_Vec normal = gp_Vec(pa, pb).Crossed(gp_Vec(pa, pc));
                if (!std::isfinite(normal.SquareMagnitude()) || normal.SquareMagnitude() < 1e-24) continue;
                // 삼각형 bbox와 겹치는 사각형만 이 삼각형에 적용한다.
                std::vector<AreaRect2> localRects;
                const double triMinX = (std::min)(a.x, (std::min)(b.x, c.x));
                const double triMaxX = (std::max)(a.x, (std::max)(b.x, c.x));
                const double triMinY = (std::min)(a.y, (std::min)(b.y, c.y));
                const double triMaxY = (std::max)(a.y, (std::max)(b.y, c.y));
                for (const AreaRect2& rect : rects)
                    if (triMaxX > rect.minX && triMinX < rect.maxX &&
                        triMaxY > rect.minY && triMinY < rect.maxY)
                        localRects.push_back(rect);
                // 분할 영역 마스크(축 정렬 반평면)로 먼저 자른다. 절단선은 작업평면에서
                // 축 정렬이므로 사각형 차감과 같은 볼록 조각 분해가 그대로 성립한다.
                std::vector<AreaVec2> clipped{a, b, c};
                for (const AreaHalfPlane& plane : planes)
                {
                    std::vector<AreaVec2> next;
                    ClipHalfPlane2D(clipped, plane.axis, plane.limit, plane.keepLess, next);
                    clipped.swap(next);
                    if (clipped.size() < 3) break;
                }
                if (clipped.size() < 3) continue;
                std::vector<std::vector<AreaVec2>> fragments;
                if (localRects.empty())
                    fragments.push_back(clipped);
                else
                    SubtractRectsFromConvex(clipped, localRects, fragments);
                for (const auto& fragment : fragments)
                {
                    if (fragment.size() < 3) continue;
                    ++emitted;
                    if (emitted > kAreaHighlightTriangleCap)
                    {
                        diagnostic = "fragment-cap";
                        return false;
                    }
                    // 볼록 조각 팬 삼각분할.
                    for (size_t k = 1; k + 1 < fragment.size(); ++k)
                    {
                        gp_Pnt q[3];
                        if (!AreaBarycentricTo3D(a, b, c, pa, pb, pc, det, fragment[0], q[0]) ||
                            !AreaBarycentricTo3D(a, b, c, pa, pb, pc, det, fragment[k], q[1]) ||
                            !AreaBarycentricTo3D(a, b, c, pa, pb, pc, det, fragment[k + 1], q[2]))
                            continue;
                        const gp_Vec pieceNormal = gp_Vec(q[0], q[1]).Crossed(gp_Vec(q[0], q[2]));
                        if (!std::isfinite(pieceNormal.SquareMagnitude()) ||
                            pieceNormal.SquareMagnitude() < 1e-24) continue;
                        // 원본 삼각형의 감김과 반대로 나온 조각만 뒤집는다.
                        if (pieceNormal.Dot(normal) < 0.0) std::swap(q[1], q[2]);
                        nodes.push_back(q[0]);
                        nodes.push_back(q[1]);
                        nodes.push_back(q[2]);
                        triangles.push_back(Poly_Triangle(
                            static_cast<Standard_Integer>(nodes.size() - 2),
                            static_cast<Standard_Integer>(nodes.size() - 1),
                            static_cast<Standard_Integer>(nodes.size())));
                        if (static_cast<int>(triangles.size()) > kAreaHighlightTriangleCap)
                        {
                            diagnostic = "triangle-cap";
                            return false;
                        }
                    }
                }
            }
        }
    }
    catch (const Standard_Failure& failure)
    {
        diagnostic = std::string("exception ") + (failure.GetMessageString() ? failure.GetMessageString() : "");
        return false;
    }
    catch (...)
    {
        diagnostic = "exception";
        return false;
    }
    if (sourceTriangles == 0)
    {
        diagnostic = "no-triangulation";   // 표시 메시가 없으면 잘라낼 대상도 없다
        return false;
    }
    triangleCount = static_cast<int>(triangles.size());
    if (triangles.empty()) return true;   // 전부 제외: 빈 결과도 정상
    mesh = new Poly_Triangulation(static_cast<Standard_Integer>(nodes.size()),
        static_cast<Standard_Integer>(triangles.size()), Standard_False);
    for (size_t i = 0; i < nodes.size(); ++i)
        mesh->SetNode(static_cast<Standard_Integer>(i + 1), nodes[i]);
    for (size_t i = 0; i < triangles.size(); ++i)
        mesh->SetTriangle(static_cast<Standard_Integer>(i + 1), triangles[i]);
    mesh->ComputeNormals();   // 정점을 삼각형마다 복제했으므로 평면 음영
    return true;
}

// 커밋된 채움 메시가 점유한 면. 이 면은 호버/픽에서 제외한다.
bool IsFaceInAreaHighlightNative(int faceId)
{
    if (faceId < 0) return false;
    for (const auto& entry : g_areaHighlights)
    {
        if (!entry.second.committed) continue;
        if (std::find(entry.second.faceIds.begin(), entry.second.faceIds.end(), faceId) !=
            entry.second.faceIds.end())
            return true;
    }
    return false;
}

// AIS_Triangulation 노드 색: Alpha<<24 | Blue<<16 | Green<<8 | Red.
int PackAreaColor(const Quantity_Color& color, int alpha)
{
    auto channel = [](double value)
    {
        int v = static_cast<int>(std::lround(value * 255.0));
        return v < 0 ? 0 : (v > 255 ? 255 : v);
    };
    return ((alpha & 0xFF) << 24) | (channel(color.Blue()) << 16) |
           (channel(color.Green()) << 8) | channel(color.Red());
}

void RedrawAreaHighlights()
{
    if (!g_view.IsNull()) { try { g_view->Redraw(); } catch (...) {} }
}

bool StageAreaHighlightMeshCore(int areaId, long long modelRevision, long long workingId,
    const std::vector<int>& faceIds, const std::vector<double>& rects,
    const std::vector<double>& planeValues,
    const std::vector<double>& frame, const double* color)
{
    if (areaId <= 0 || color == nullptr) return false;
    if (faceIds.empty() || rects.size() % 4 != 0 || planeValues.size() % 3 != 0) return false;
    if (frame.size() != 9) return false;
    if (rects.empty() && planeValues.empty()) return false;
    if (modelRevision != g_modelRevision || !FindWorking(workingId, modelRevision)) return false;
    for (double value : frame)
        if (!std::isfinite(value)) return false;
    for (double value : rects)
        if (!std::isfinite(value)) return false;
    for (double value : planeValues)
        if (!std::isfinite(value)) return false;
    if (!std::isfinite(color[0]) || !std::isfinite(color[1]) || !std::isfinite(color[2])) return false;

    std::vector<AreaRect2> nativeRects;
    nativeRects.reserve(rects.size() / 4);
    for (size_t i = 0; i + 3 < rects.size(); i += 4)
    {
        AreaRect2 rect{rects[i], rects[i + 1], rects[i + 2], rects[i + 3]};
        if (rect.maxX < rect.minX || rect.maxY < rect.minY) return false;
        if (rect.maxX - rect.minX < 1e-6 || rect.maxY - rect.minY < 1e-6) continue;   // 퇴화 사각형
        nativeRects.push_back(rect);
    }

    std::vector<AreaHalfPlane> nativePlanes;
    nativePlanes.reserve(planeValues.size() / 3);
    for (size_t i = 0; i + 2 < planeValues.size(); i += 3)
    {
        const double axisValue = planeValues[i];
        const int axis = static_cast<int>(std::lround(axisValue));
        if (axis != 0 && axis != 1) return false;
        if (std::fabs(axisValue - axis) > 1e-9) return false;
        nativePlanes.push_back(AreaHalfPlane{axis, planeValues[i + 1], planeValues[i + 2] != 0.0});
    }
    if (nativeRects.empty() && nativePlanes.empty()) return false;

    gp_Pnt origin;
    gp_Dir axisX, axisY;
    try
    {
        origin = gp_Pnt(frame[0], frame[1], frame[2]);
        gp_Vec xVector(frame[3], frame[4], frame[5]), yVector(frame[6], frame[7], frame[8]);
        if (xVector.SquareMagnitude() < 1e-18 || yVector.SquareMagnitude() < 1e-18) return false;
        axisX = gp_Dir(xVector);
        axisY = gp_Dir(yVector);
    }
    catch (const Standard_Failure&) { return false; }

    Handle(Poly_Triangulation) mesh;
    int triangleCount = 0;
    std::string diagnostic;
    if (!BuildAreaHighlightMeshNative(faceIds, nativeRects, nativePlanes, origin, axisX, axisY,
            mesh, triangleCount, diagnostic))
        return false;

    // 기존 엔트리가 있으면 스테이징 필드만 교체한다. 커밋 표시는 Commit 성공까지 유지된다.
    AreaHighlightEntry& entry = g_areaHighlights[areaId];
    entry.modelRevision = modelRevision;
    entry.workingId = workingId;
    entry.faceIds = faceIds;
    entry.color = Quantity_Color(color[0], color[1], color[2], Quantity_TOC_RGB);
    entry.mesh = mesh;
    entry.triangleCount = triangleCount;
    entry.hasPending = true;
    return true;
}

bool StageAreaHighlightMeshNative(int areaId, long long modelRevision, long long workingId,
    const std::vector<int>& faceIds, const std::vector<double>& rects,
    const std::vector<double>& frame, const double* color)
{
    return StageAreaHighlightMeshCore(areaId, modelRevision, workingId, faceIds, rects,
        std::vector<double>(), frame, color);
}

// 분할 영역 부분 표시: 축 정렬 절단 반평면(axis, limit, keepLess 3개씩)으로 원본 면
// 삼각분할을 먼저 자른 뒤 제외 사각형을 뺀다. rects는 비어 있어도 된다.
bool StageAreaHighlightMeshClippedNative(int areaId, long long modelRevision, long long workingId,
    const std::vector<int>& faceIds, const std::vector<double>& rects,
    const std::vector<double>& planes, const std::vector<double>& frame, const double* color)
{
    return StageAreaHighlightMeshCore(areaId, modelRevision, workingId, faceIds, rects,
        planes, frame, color);
}

bool CommitAreaHighlightNative(int areaId)
{
    auto found = g_areaHighlights.find(areaId);
    if (found == g_areaHighlights.end()) return false;
    if (g_ctx.IsNull()) return false;
    AreaHighlightEntry& entry = found->second;
    // 스테이징 결과가 없으면(예: Discard 후 Stage 없이 Commit) 아무것도 바꾸지 않는다.
    if (!entry.hasPending) return false;
    // 스테이징 시점의 작업 문맥/모델 리비전이 그대로인지 다시 확인한다. 그 사이 모델이
    // 바뀌었으면 면 삼각분할이 다른 모델의 것이므로 표시하지 않는다.
    if (entry.modelRevision != g_modelRevision || !FindWorking(entry.workingId, entry.modelRevision))
        return false;
    const bool hasStagedMesh = !entry.mesh.IsNull() && entry.triangleCount > 0;
    const bool stagedEmpty = entry.mesh.IsNull() && entry.triangleCount == 0;
    if (!hasStagedMesh && !stagedEmpty) return false;
    Handle(AIS_Triangulation) presentation;
    if (hasStagedMesh)
    {
        try
        {
            presentation = new AIS_Triangulation(entry.mesh);
            const Standard_Integer packed = PackAreaColor(entry.color, 0xFF);
            Handle(TColStd_HArray1OfInteger) colors =
                new TColStd_HArray1OfInteger(1, entry.mesh->NbNodes());
            for (Standard_Integer i = 1; i <= entry.mesh->NbNodes(); ++i) colors->SetValue(i, packed);
            presentation->SetColors(colors);
            presentation->SetTransparency(kAreaFillTransparency);
            // 면 색 오버라이드(호버/선택/확정)는 -2로 당긴다. 채움 메시가 같은 -2를 쓰면
            // 서로 다른 삼각분할의 보간 깊이 차이로 컷 경계에서 깊이 승패가 갈려 깜빡인다.
            // 채움을 한 단계 더 당겨(-4) 항상 면 색 위에 오도록 순서를 고정한다.
            presentation->SetPolygonOffsets(Aspect_POM_Fill, -4.0f, -4.0f);
            presentation->Attributes()->SetupOwnShadingAspect();
            presentation->Attributes()->ShadingAspect()->Aspect()->SetPolygonOffsets(Aspect_POM_Fill, -4.0f, -4.0f);
            // AIS_Triangulation::Compute는 모드 0(AIS_WireFrame)에서만 프리미티브를 만든다.
            // AIS_Shaded(1)로 표시하면 Compute가 즉시 반환해 빈 presentation이 된다.
            // 모드 0에서도 채움 삼각형 배열 + ShadingAspect를 사용하므로 음영으로 그려진다.
            g_ctx->SetDisplayMode(presentation, AIS_WireFrame, Standard_False);
            // 새 표시를 먼저 올린다(즉시 redraw 없이). 기존 표시 제거를 먼저 하면 새 표시
            // 생성이 실패했을 때 형상은 그대로인데 채움만 사라진다.
            g_ctx->Display(presentation, Standard_False);
            g_ctx->Deactivate(presentation, Standard_False);
        }
        catch (const Standard_Failure&)
        {
            if (!presentation.IsNull()) { try { g_ctx->Remove(presentation, Standard_False); } catch (...) {} }
            return false;
        }
        catch (...)
        {
            if (!presentation.IsNull()) { try { g_ctx->Remove(presentation, Standard_False); } catch (...) {} }
            return false;
        }
    }
    // 새 표시가 준비된 뒤에만 기존 표시를 제거한다.
    if (!entry.presentation.IsNull())
    {
        try { g_ctx->Remove(entry.presentation, Standard_False); } catch (...) {}
    }
    // 표시가 바뀐 뒤에만 상태를 확정한다. 실패하면 기존 표시/색이 그대로 남는다.
    entry.presentation = presentation;
    entry.committed = true;
    entry.mesh.Nullify();
    entry.hasPending = false;
    // 이 영역의 Face는 더 이상 통짜 색칠을 쓰지 않는다. 클리핑 메시가 대신 표시된다.
    for (int faceId : entry.faceIds)
    {
        g_confirmedFaceColors.erase(faceId);
        ClearFaceColorOverride(faceId);
    }
    if (!g_aisShape.IsNull()) g_ctx->Redisplay(g_aisShape, Standard_False);
    RedrawAreaHighlights();
    return true;
}

void DiscardAreaHighlightNative(int areaId)
{
    auto found = g_areaHighlights.find(areaId);
    if (found == g_areaHighlights.end()) return;
    if (!found->second.committed)
    {
        g_areaHighlights.erase(found);
        return;
    }
    // 커밋된 표시는 유지한다. 스테이징된 메시만 폐기한다.
    found->second.mesh.Nullify();
    found->second.hasPending = false;
}

void ClearAreaHighlightNative(int areaId)
{
    auto found = g_areaHighlights.find(areaId);
    if (found == g_areaHighlights.end()) return;
    if (!g_ctx.IsNull() && !found->second.presentation.IsNull())
    {
        try { g_ctx->Remove(found->second.presentation, Standard_False); } catch (...) {}
    }
    g_areaHighlights.erase(found);
    RedrawAreaHighlights();
}

void ClearAllAreaHighlightsNative()
{
    if (!g_ctx.IsNull())
        for (const auto& entry : g_areaHighlights)
            if (!entry.second.presentation.IsNull())
            {
                try { g_ctx->Remove(entry.second.presentation, Standard_False); } catch (...) {}
            }
    g_areaHighlights.clear();
}

int GetAreaHighlightTriangleCountNative(int areaId)
{
    auto found = g_areaHighlights.find(areaId);
    if (found == g_areaHighlights.end()) return -1;
    if (!found->second.committed) return found->second.triangleCount;
    if (found->second.presentation.IsNull()) return 0;   // 전부 제외된 결과
    const Handle(Poly_Triangulation) committed = found->second.presentation->GetTriangulation();
    return committed.IsNull() ? 0 : committed->NbTriangles();
}
