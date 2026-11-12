#ifndef STEP_EXPORT_INCLUDES_DONE
#include <algorithm>
#include <cmath>
#include <limits>
#include <string>
#include <vector>

#include <BRepAdaptor_Curve.hxx>
#include <BRepAdaptor_Surface.hxx>
#include <BRepAlgoAPI_Section.hxx>
#include <BRepAlgoAPI_Common.hxx>
#include <BRepGProp.hxx>
#include <GProp_GProps.hxx>
#include <BRep_Builder.hxx>
#include <TopoDS_Compound.hxx>
#include <BRepBndLib.hxx>
#include <BRepBuilderAPI_MakeFace.hxx>
#include <BRepBuilderAPI_MakePolygon.hxx>
#include <BRepBuilderAPI_Transform.hxx>
#include <BRepFeat_SplitShape.hxx>
#include <BRepPrimAPI_MakePrism.hxx>
#include <BRepTools.hxx>
#include <BRep_Tool.hxx>
#include <Bnd_Box.hxx>
#include <TopAbs_ShapeEnum.hxx>
#include <TopExp_Explorer.hxx>
#include <TopTools_DataMapOfShapeInteger.hxx>
#include <TopTools_ListIteratorOfListOfShape.hxx>
#include <TopTools_SequenceOfShape.hxx>
#include <TopoDS.hxx>
#include <TopoDS_Face.hxx>
#include <TopoDS_Shape.hxx>
#include <TopoDS_Wire.hxx>
#include <gp_Pnt.hxx>
#include <gp_Trsf.hxx>
#include <gp_Vec.hxx>
#endif

namespace step_export_geometry
{
    // The area outline is the projected CAD boundary, so a face edge that lies
    // on the outline shares its 2D coordinates exactly. A small tolerance keeps
    // those shared points on the boundary instead of flipping them outside.
    const double AreaRegionBoundaryTolerance = 0.05;
    // The prism must cover the whole model even when the working plane floats in
    // front of it, so extrude well past the model diagonal in both directions.
    const double AreaRegionPrismDepthFactor = 1.5;
    const int AreaRegionEdgeSamples = 5;

    struct NativeStepAreaFaceColor
    {
        TopoDS_Face face;
        int areaIndex;
    };

    struct NativeStepAreaFrame
    {
        gp_Pnt origin;
        gp_Vec axisX;
        gp_Vec axisY;
        gp_Vec normal;
    };

    enum NativeStepAreaCoverage
    {
        NativeStepAreaOutside = 0,
        NativeStepAreaCovered = 1,
        NativeStepAreaPartial = 2
    };

    inline std::string NativeStepAreaIndexToken(size_t index)
    {
        return std::to_string(static_cast<int>(index));
    }

    inline bool ResolveAreaFrame(const NativeStepExportArea& area, NativeStepAreaFrame& frame)
    {
        if (area.frame.size() != 9) return false;
        frame.origin = gp_Pnt(area.frame[0], area.frame[1], area.frame[2]);
        frame.axisX = gp_Vec(area.frame[3], area.frame[4], area.frame[5]);
        frame.axisY = gp_Vec(area.frame[6], area.frame[7], area.frame[8]);
        if (frame.axisX.Magnitude() <= Precision::Confusion() ||
            frame.axisY.Magnitude() <= Precision::Confusion())
        {
            return false;
        }
        frame.axisX.Normalize();
        frame.axisY.Normalize();
        frame.normal = frame.axisX.Crossed(frame.axisY);
        if (frame.normal.Magnitude() <= Precision::Confusion()) return false;
        frame.normal.Normalize();
        return true;
    }

    inline void NormalizeAreaRing(const std::vector<double>& source, std::vector<double>& ring)
    {
        ring.clear();
        if (source.size() < 6 || (source.size() % 2) != 0) return;
        const size_t count = source.size() / 2;
        for (size_t i = 0; i < count; ++i)
        {
            const double u = source[i * 2];
            const double v = source[i * 2 + 1];
            if (!std::isfinite(u) || !std::isfinite(v))
            {
                ring.clear();
                return;
            }
            if (!ring.empty())
            {
                const double du = u - ring[ring.size() - 2];
                const double dv = v - ring[ring.size() - 1];
                if (du * du + dv * dv <= 1e-16) continue;
            }
            ring.push_back(u);
            ring.push_back(v);
        }
        while (ring.size() >= 4)
        {
            const size_t last = ring.size() - 2;
            const double du = ring[last] - ring[0];
            const double dv = ring[last + 1] - ring[1];
            if (du * du + dv * dv > 1e-16) break;
            ring.resize(last);
        }
        if (ring.size() < 6) ring.clear();
    }

    inline double AreaRingSegmentDistance(double pu, double pv, double au, double av,
                                          double bu, double bv)
    {
        const double du = bu - au;
        const double dv = bv - av;
        const double lengthSquared = du * du + dv * dv;
        if (lengthSquared <= 1e-18) return std::hypot(pu - au, pv - av);
        double t = ((pu - au) * du + (pv - av) * dv) / lengthSquared;
        if (t < 0.0) t = 0.0;
        else if (t > 1.0) t = 1.0;
        return std::hypot(pu - (au + t * du), pv - (av + t * dv));
    }

    inline double AreaRingBoundaryDistance(const std::vector<double>& ring, double u, double v)
    {
        const size_t count = ring.size() / 2;
        if (count < 2) return std::numeric_limits<double>::max();
        double best = std::numeric_limits<double>::max();
        for (size_t i = 0; i < count; ++i)
        {
            const size_t j = (i + 1) % count;
            const double distance = AreaRingSegmentDistance(
                u, v, ring[i * 2], ring[i * 2 + 1], ring[j * 2], ring[j * 2 + 1]);
            if (distance < best) best = distance;
        }
        return best;
    }

    inline bool AreaRingContains(const std::vector<double>& ring, double u, double v)
    {
        const size_t count = ring.size() / 2;
        if (count < 3) return false;
        bool inside = false;
        for (size_t i = 0, j = count - 1; i < count; j = i++)
        {
            const double ui = ring[i * 2];
            const double vi = ring[i * 2 + 1];
            const double uj = ring[j * 2];
            const double vj = ring[j * 2 + 1];
            if ((vi > v) == (vj > v)) continue;
            const double denominator = vj - vi;
            if (std::fabs(denominator) <= 1e-18) continue;
            const double crossing = ui + (v - vi) * (uj - ui) / denominator;
            if (u < crossing) inside = !inside;
        }
        return inside;
    }

    inline void AppendAreaSample(const NativeStepAreaFrame& frame, const gp_Pnt& point,
                                 std::vector<double>& samples)
    {
        const gp_Vec delta(frame.origin, point);
        samples.push_back(delta.Dot(frame.axisX));
        samples.push_back(delta.Dot(frame.axisY));
    }

    inline void CollectAreaFaceSamples(const TopoDS_Face& face, const NativeStepAreaFrame& frame,
                                       std::vector<double>& boundary,
                                       std::vector<double>& interior)
    {
        boundary.clear();
        interior.clear();
        for (TopExp_Explorer explorer(face, TopAbs_EDGE); explorer.More(); explorer.Next())
        {
            const TopoDS_Edge edge = TopoDS::Edge(explorer.Current());
            if (BRep_Tool::Degenerated(edge)) continue;
            BRepAdaptor_Curve curve(edge);
            const double first = curve.FirstParameter();
            const double last = curve.LastParameter();
            if (!std::isfinite(first) || !std::isfinite(last) || last <= first) continue;
            for (int i = 0; i < AreaRegionEdgeSamples; ++i)
            {
                const double fraction =
                    static_cast<double>(i) / static_cast<double>(AreaRegionEdgeSamples - 1);
                AppendAreaSample(frame, curve.Value(first + (last - first) * fraction), boundary);
            }
        }
        Standard_Real uMin = 0.0, uMax = 0.0, vMin = 0.0, vMax = 0.0;
        BRepTools::UVBounds(face, uMin, uMax, vMin, vMax);
        if (std::isfinite(uMin) && std::isfinite(uMax) && std::isfinite(vMin) &&
            std::isfinite(vMax) && uMax > uMin && vMax > vMin)
        {
            const BRepAdaptor_Surface surface(face);
            AppendAreaSample(frame, surface.Value((uMin + uMax) * 0.5, (vMin + vMax) * 0.5),
                             interior);
        }
    }

    // Boundary samples decide inside/outside; the single interior sample only
    // ever counts as "inside" evidence, so a curved face whose UV centre happens
    // to project outside its own outline cannot turn a covered face into a cut.
    // The boundary band is checked before the inside test: a cut edge lies on
    // the outline with a floating-point sign that flips between pieces
    // (-2.3e-13 on one side, +0 on the other), and counting a negative epsilon
    // as "inside" made both sides of one cut look like one spanning face.
    inline std::string AreaRingBoundsToken(const std::vector<double>& ring)
    {
        double minU = ring[0], maxU = ring[0], minV = ring[1], maxV = ring[1];
        for (size_t i = 0; i + 1 < ring.size(); i += 2)
        {
            minU = (std::min)(minU, ring[i]); maxU = (std::max)(maxU, ring[i]);
            minV = (std::min)(minV, ring[i + 1]); maxV = (std::max)(maxV, ring[i + 1]);
        }
        std::ostringstream out;
        out << "pts=" << ring.size() / 2
            << " u=[" << minU << "," << maxU << "] v=[" << minV << "," << maxV << "]";
        return out.str();
    }

    inline void LogStepAreaNative(const std::string& message)
    {
        char path[MAX_PATH] = { 0 };
        const DWORD length = ::GetEnvironmentVariableA("HEATINGMOLD_OCC_LOG", path, MAX_PATH);
        if (length == 0 || length >= MAX_PATH) return;
        std::ofstream log(path, std::ios::app);
        if (!log) return;
        log << "step-area: " << message << "\n";
    }

    inline std::string AreaSamplesBoundsToken(const std::vector<double>& boundary,
                                              const std::vector<double>& interior)
    {
        if (boundary.size() < 2 && interior.size() < 2) return "empty";
        double minU = 1e300, maxU = -1e300, minV = 1e300, maxV = -1e300;
        for (size_t i = 0; i + 1 < boundary.size(); i += 2)
        {
            minU = (std::min)(minU, boundary[i]); maxU = (std::max)(maxU, boundary[i]);
            minV = (std::min)(minV, boundary[i + 1]); maxV = (std::max)(maxV, boundary[i + 1]);
        }
        for (size_t i = 0; i + 1 < interior.size(); i += 2)
        {
            minU = (std::min)(minU, interior[i]); maxU = (std::max)(maxU, interior[i]);
            minV = (std::min)(minV, interior[i + 1]); maxV = (std::max)(maxV, interior[i + 1]);
        }
        std::ostringstream out;
        out << "u=[" << minU << "," << maxU << "] v=[" << minV << "," << maxV << "]";
        return out.str();
    }

    inline NativeStepAreaCoverage EvaluateAreaCoverage(const std::vector<double>& ring,
                                                       const std::vector<double>& boundary,
                                                       const std::vector<double>& interior)
    {
        if (boundary.empty()) return NativeStepAreaOutside;
        bool anyInside = false;
        bool anyOutside = false;
        for (size_t i = 0; i + 1 < boundary.size(); i += 2)
        {
            const double u = boundary[i];
            const double v = boundary[i + 1];
            if (AreaRingBoundaryDistance(ring, u, v) <= AreaRegionBoundaryTolerance) continue;
            if (AreaRingContains(ring, u, v)) anyInside = true;
            else anyOutside = true;
        }
        if (anyOutside) return anyInside ? NativeStepAreaPartial : NativeStepAreaOutside;
        if (anyInside) return NativeStepAreaCovered;
        for (size_t i = 0; i + 1 < interior.size(); i += 2)
        {
            if (AreaRingContains(ring, interior[i], interior[i + 1])) return NativeStepAreaCovered;
        }
        // Every boundary sample sits on the area outline itself: this face is the
        // area (or lies exactly inside it).
        return NativeStepAreaCovered;
    }

    inline bool BuildAreaRegionPrism(const std::vector<double>& ring,
                                     const NativeStepAreaFrame& frame,
                                     double halfDepth,
                                     TopoDS_Shape& prism)
    {
        BRepBuilderAPI_MakePolygon polygon;
        const size_t count = ring.size() / 2;
        for (size_t i = 0; i < count; ++i)
        {
            const double u = ring[i * 2];
            const double v = ring[i * 2 + 1];
            polygon.Add(gp_Pnt(frame.origin.X() + u * frame.axisX.X() + v * frame.axisY.X(),
                               frame.origin.Y() + u * frame.axisX.Y() + v * frame.axisY.Y(),
                               frame.origin.Z() + u * frame.axisX.Z() + v * frame.axisY.Z()));
        }
        polygon.Close();
        if (!polygon.IsDone()) return false;
        BRepBuilderAPI_MakeFace makeFace(polygon.Wire(), Standard_True);
        if (!makeFace.IsDone()) return false;
        gp_Trsf shift;
        shift.SetTranslation(gp_Vec(-frame.normal.X() * halfDepth,
                                    -frame.normal.Y() * halfDepth,
                                    -frame.normal.Z() * halfDepth));
        const TopoDS_Shape base =
            BRepBuilderAPI_Transform(makeFace.Face(), shift, Standard_True).Shape();
        if (base.IsNull()) return false;
        BRepPrimAPI_MakePrism maker(base, gp_Vec(frame.normal.X() * halfDepth * 2.0,
                                                 frame.normal.Y() * halfDepth * 2.0,
                                                 frame.normal.Z() * halfDepth * 2.0));
        maker.Build();
        if (!maker.IsDone() || maker.Shape().IsNull()) return false;
        prism = maker.Shape();
        return true;
    }

    // Splits the export copy along every area outline and reports one colour per
    // resulting face. The split keeps the shape type (shell stays a shell, solid
    // stays a solid) and only adds the area outline as new face boundaries; no
    // geometry is moved and the outline itself never becomes a visible part.
    inline bool BuildAreaColoredModelNative(const TopoDS_Shape& model,
                                            const std::vector<TopoDS_Face>& modelFaces,
                                            const std::vector<NativeStepExportArea>& areas,
                                            TopoDS_Shape& result,
                                            std::vector<NativeStepAreaFaceColor>& faceColors,
                                            std::string& failure)
    {
        result = model;
        faceColors.clear();

        const size_t areaCount = areas.size();
        std::vector<NativeStepAreaFrame> frames(areaCount);
        std::vector<bool> usable(areaCount, false);
        for (size_t a = 0; a < areaCount; ++a)
        {
            const NativeStepExportArea& area = areas[a];
            if (area.faceIds.empty() || area.frame.empty() || area.ring.empty()) continue;
            if (!ResolveAreaFrame(area, frames[a]))
            {
                failure = "area-frame:" + NativeStepAreaIndexToken(a);
                return false;
            }
            usable[a] = true;
        }
        bool anyRegion = false;
        for (size_t a = 0; a < areaCount; ++a)
        {
            if (usable[a]) anyRegion = true;
        }
        if (!anyRegion) return true;

        std::vector<std::vector<double>> rings(areaCount);
        for (size_t a = 0; a < areaCount; ++a)
        {
            if (!usable[a]) continue;
            NormalizeAreaRing(areas[a].ring, rings[a]);
            LogStepAreaNative("area " + NativeStepAreaIndexToken(a) + " ring " + AreaRingBoundsToken(rings[a]));
            if (rings[a].size() < 6)
            {
                failure = "area-ring:" + NativeStepAreaIndexToken(a);
                return false;
            }
        }

        std::vector<std::vector<int>> faceAreas(modelFaces.size());
        for (size_t a = 0; a < areaCount; ++a)
        {
            if (!usable[a]) continue;
            for (int faceId : areas[a].faceIds)
            {
                if (faceId < 0 || static_cast<size_t>(faceId) >= modelFaces.size())
                {
                    failure = "area-face:" + NativeStepAreaIndexToken(a);
                    return false;
                }
                faceAreas[faceId].push_back(static_cast<int>(a));
            }
        }

        Bnd_Box box;
        BRepBndLib::Add(model, box, Standard_False);
        if (box.IsVoid())
        {
            failure = "area-bounds";
            return false;
        }
        Standard_Real xMin = 0.0, yMin = 0.0, zMin = 0.0, xMax = 0.0, yMax = 0.0, zMax = 0.0;
        box.Get(xMin, yMin, zMin, xMax, yMax, zMax);
        const double diagonal = std::sqrt((xMax - xMin) * (xMax - xMin) +
                                          (yMax - yMin) * (yMax - yMin) +
                                          (zMax - zMin) * (zMax - zMin));
        const double halfDepth = (diagonal > 0.0 ? diagonal : 1.0) * AreaRegionPrismDepthFactor;

        std::vector<TopoDS_Shape> prisms(areaCount);
        for (size_t a = 0; a < areaCount; ++a)
        {
            if (!usable[a]) continue;
            if (!BuildAreaRegionPrism(rings[a], frames[a], halfDepth, prisms[a]))
            {
                failure = "area-prism:" + NativeStepAreaIndexToken(a);
                return false;
            }
        }

        // 평면 절단(BRepFeat_SplitShape)은 곡면이 접히거나 주기적인 면에서
        // 절단선이 면을 완전히 분리하지 못한다(분할 영역 A-1 area-nofaces 재현).
        // 대신 선택 면을 영역 프리즘과 3D 공통 연산해 정확한 조각으로 교체한다.
        // 선택되지 않은 면은 원본 그대로 유지하고, 조각 합의 면적이 원본과
        // 일치하지 않으면 색을 생략하지 않고 명시적으로 실패한다.
        TopoDS_Compound compound;
        BRep_Builder compoundBuilder;
        compoundBuilder.MakeCompound(compound);
        std::vector<int> covered(areaCount, 0);
        for (size_t id = 0; id < modelFaces.size(); ++id)
        {
            if (faceAreas[id].empty())
            {
                compoundBuilder.Add(compound, modelFaces[id]);
                continue;
            }
            GProp_GProps originalProps;
            BRepGProp::SurfaceProperties(modelFaces[id], originalProps);
            const double originalArea = originalProps.Mass();
            double pieceArea = 0.0;
            int pieceCount = 0;
            // 제외영역이 면을 가로지르면 면의 일부는 어느 영역에도 속하지 않으므로
            // 조각 합이 원본 면적보다 작은 것이 정상이다. 이때만 동등성 검사를
            // 완화하고, 영역들이 면을 나눠 갖는 경우(분할 영역)에는 합집합이 면
            // 전체를 덮어야 하므로 엄격한 동등성을 그대로 유지한다.
            // 표본은 이 면을 공유하는 영역의 작업 프레임으로 투영한다. 같은 STEP
            // 내보내기의 영역은 하나의 확정 작업면을 공유한다.
            bool clipped = false;
            {
                std::vector<double> faceSamples;
                std::vector<double> faceInterior;
                CollectAreaFaceSamples(modelFaces[id], frames[faceAreas[id].front()],
                                       faceSamples, faceInterior);
                for (size_t s = 0; s + 1 < faceSamples.size() && !clipped; s += 2)
                {
                    const double sampleU = faceSamples[s];
                    const double sampleV = faceSamples[s + 1];
                    bool coveredByAnyArea = false;
                    for (int a : faceAreas[id])
                    {
                        if (AreaRingBoundaryDistance(rings[a], sampleU, sampleV) <= AreaRegionBoundaryTolerance ||
                            AreaRingContains(rings[a], sampleU, sampleV))
                        {
                            coveredByAnyArea = true;
                            break;
                        }
                    }
                    if (!coveredByAnyArea) clipped = true;
                }
            }
            for (int a : faceAreas[id])
            {
                BRepAlgoAPI_Common common(modelFaces[id], prisms[a]);
                common.Build();
                if (!common.IsDone() || common.Shape().IsNull())
                {
                    failure = "area-common:" + NativeStepAreaIndexToken(a);
                    return false;
                }
                for (TopExp_Explorer explorer(common.Shape(), TopAbs_FACE); explorer.More();
                     explorer.Next())
                {
                    const TopoDS_Face piece = TopoDS::Face(explorer.Current());
                    GProp_GProps props;
                    BRepGProp::SurfaceProperties(piece, props);
                    const double area = props.Mass();
                    if (!(area > 0.0)) continue;
                    compoundBuilder.Add(compound, piece);
                    NativeStepAreaFaceColor entry;
                    entry.face = piece;
                    entry.areaIndex = a;
                    faceColors.push_back(entry);
                    ++covered[a];
                    ++pieceCount;
                    pieceArea += area;
                }
            }
            if (pieceCount == 0)
            {
                failure = "area-nofaces:" + NativeStepAreaIndexToken(faceAreas[id].front());
                return false;
            }
            const double areaTolerance = (std::max)(1.0, originalArea * 1.0e-4);
            if (clipped)
            {
                // 제외영역으로 잘린 면: 조각이 원본 면적을 넘을 수는 없다.
                // (조각이 없으면 위에서 이미 실패했고, 영역별 조각 유무는 아래에서 검사한다.)
                if (pieceArea > originalArea + areaTolerance)
                {
                    LogStepAreaNative("face-excess " + std::to_string(id) +
                                      " pieces=" + std::to_string(pieceCount) +
                                      " pieceArea=" + std::to_string(pieceArea) +
                                      " originalArea=" + std::to_string(originalArea));
                    failure = "area-face-area:" + NativeStepAreaIndexToken(faceAreas[id].front()) +
                              ":" + std::to_string(id);
                    return false;
                }
                LogStepAreaNative("face-clip " + std::to_string(id) +
                                  " pieces=" + std::to_string(pieceCount) +
                                  " pieceArea=" + std::to_string(pieceArea) +
                                  " originalArea=" + std::to_string(originalArea));
            }
            else if (std::abs(pieceArea - originalArea) > areaTolerance)
            {
                LogStepAreaNative("face-area " + std::to_string(id) + " pieces=" + std::to_string(pieceCount) +
                                  " pieceArea=" + std::to_string(pieceArea) +
                                  " originalArea=" + std::to_string(originalArea) +
                                  " area=" + NativeStepAreaIndexToken(faceAreas[id].front()));
                failure = "area-face-area:" + NativeStepAreaIndexToken(faceAreas[id].front()) +
                          ":" + std::to_string(id);
                return false;
            }
        }
        for (size_t a = 0; a < areaCount; ++a)
        {
            if (!usable[a]) continue;
            LogStepAreaNative("area " + NativeStepAreaIndexToken(a) +
                              " covered=" + std::to_string(covered[a]));
            if (covered[a] == 0)
            {
                failure = "area-nofaces:" + NativeStepAreaIndexToken(a);
                return false;
            }
        }
        result = compound;
        return true;
    }
}
