// STEP document writer/verifier for piece-level heating route output.
//
// One free shape carries the model.  Each confirmed area becomes two
// assemblies ("<area> 발열사" and "<area> 발열경로"); every route piece is a
// single component inside them (one wire solid, one centreline edge).
// The written file is re-read with an independent reader and the tree, names,
// colours and per-piece topology are verified before the file is accepted.

#ifndef STEP_EXPORT_INCLUDES_DONE
#include <algorithm>
#include <cmath>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>

#include <OSD_OpenFile.hxx>
#include <Standard_Failure.hxx>
#include <TCollection_ExtendedString.hxx>
#include <TDataStd_Name.hxx>
#include <TDF_Label.hxx>
#include <TDF_LabelSequence.hxx>
#include <TDocStd_Document.hxx>
#include <XCAFApp_Application.hxx>
#include <XCAFDoc_ColorTool.hxx>
#include <XCAFDoc_DocumentTool.hxx>
#include <XCAFDoc_ColorType.hxx>
#include <XCAFDoc_ShapeTool.hxx>
#include <Quantity_Color.hxx>
#include <STEPCAFControl_Reader.hxx>
#include <STEPCAFControl_Writer.hxx>
#include <STEPControl_StepModelType.hxx>
#include <IFSelect_ReturnStatus.hxx>
#include <Bnd_Box.hxx>
#include <BRepBndLib.hxx>
#include <BRepBuilderAPI_Copy.hxx>
#include <BRepCheck_Analyzer.hxx>
#include <TopoDS_Shape.hxx>
#include <TopExp_Explorer.hxx>
#include <TopAbs.hxx>
#include <TopAbs_ShapeEnum.hxx>

#endif

namespace step_export_geometry
{
    inline bool ReadLabelName(const TDF_Label& label, TCollection_ExtendedString& name)
    {
        Handle(TDataStd_Name) attribute;
        if (!label.FindAttribute(TDataStd_Name::GetID(), attribute) ||
            attribute.IsNull())
        {
            return false;
        }
        name = attribute->Get();
        return true;
    }

    inline std::wstring ExtToWString(const TCollection_ExtendedString& value)
    {
        std::wstring result;
        const Standard_ExtString text = value.ToExtString();
        for (Standard_Integer i = 0; text[i] != 0; ++i)
        {
            result.push_back(static_cast<wchar_t>(text[i]));
        }
        return result;
    }

    inline bool LabelNameIs(const TDF_Label& label, const std::wstring& expected)
    {
        TCollection_ExtendedString name;
        if (!ReadLabelName(label, name))
        {
            return false;
        }
        return ExtToWString(name) == expected;
    }

    inline bool ReadComponentColor(const Handle(XCAFDoc_ColorTool)& colors,
                                   const TDF_Label& label,
                                   Quantity_Color& color)
    {
        if (colors.IsNull()) return false;
        if (colors->GetColor(label, XCAFDoc_ColorGen, color)) return true;
        if (colors->GetColor(label, XCAFDoc_ColorSurf, color)) return true;
        return colors->GetColor(label, XCAFDoc_ColorCurv, color);
    }

    inline bool ColorMatches(const Quantity_Color& actual,
                             const Standard_Integer r,
                             const Standard_Integer g,
                             const Standard_Integer b)
    {
        const Quantity_Color expected(r / 255.0, g / 255.0, b / 255.0,
                                      Quantity_TOC_sRGB);
        const Standard_Real tolerance = 0.02;
        return std::fabs(actual.Red() - expected.Red()) <= tolerance &&
               std::fabs(actual.Green() - expected.Green()) <= tolerance &&
               std::fabs(actual.Blue() - expected.Blue()) <= tolerance;
    }

    struct NativeStepVerification
    {
        Standard_Real xMin = 0, yMin = 0, zMin = 0;
        Standard_Real xMax = 0, yMax = 0, zMax = 0;
        Standard_Boolean valid = Standard_False;
    };

    inline NativeStepVerification BoundsOf(const TopoDS_Shape& shape)
    {
        NativeStepVerification result;
        if (shape.IsNull()) return result;
        Bnd_Box box;
        // Geometry only: the loaded model carries a cached working mesh, and
        // comparing a mesh-influenced box with a re-read exact box would report
        // a false change.
        BRepBndLib::Add(shape, box, Standard_False);
        if (box.IsVoid()) return result;
        box.Get(result.xMin, result.yMin, result.zMin,
                result.xMax, result.yMax, result.zMax);
        result.valid = Standard_True;
        return result;
    }

    inline Standard_Integer CountOf(const TopoDS_Shape& shape, TopAbs_ShapeEnum type)
    {
        Standard_Integer count = 0;
        if (shape.IsNull()) return 0;
        for (TopExp_Explorer explorer(shape, type); explorer.More(); explorer.Next())
        {
            ++count;
        }
        return count;
    }

    inline bool FindFreeShapeByName(const Handle(XCAFDoc_ShapeTool)& shapes,
                                    const TDF_LabelSequence& freeShapes,
                                    const std::wstring& name,
                                    TDF_Label& found)
    {
        Standard_Boolean any = Standard_False;
        for (Standard_Integer i = 1; i <= freeShapes.Length(); ++i)
        {
            const TDF_Label label = freeShapes.Value(i);
            if (!LabelNameIs(label, name)) continue;
            if (any) return false; // duplicate name is not an identity
            found = label;
            any = Standard_True;
        }
        return any == Standard_True;
    }

    inline Standard_Integer FindComponentByName(const TDF_LabelSequence& components,
                                                const std::wstring& name,
                                                TDF_Label& found)
    {
        Standard_Integer count = 0;
        for (Standard_Integer c = 1; c <= components.Length(); ++c)
        {
            const TDF_Label label = components.Value(c);
            if (!LabelNameIs(label, name)) continue;
            found = label;
            ++count;
        }
        return count;
    }

    inline bool VerifyWireAssembly(const Handle(XCAFDoc_ShapeTool)& shapes,
                                   const Handle(XCAFDoc_ColorTool)& colors,
                                   const TDF_Label& assembly,
                                   const step_export_geometry::NativeStepExportArea& area,
                                   std::string& failure)
    {
        TDF_LabelSequence components;
        shapes->GetComponents(assembly, components);
        if (components.Length() != static_cast<Standard_Integer>(area.pieces.size()))
        {
            failure = "verify-parts";
            return false;
        }

        for (const step_export_geometry::NativeStepExportPiece& piece : area.pieces)
        {
            TDF_Label label;
            if (FindComponentByName(components, piece.name, label) != 1)
            {
                failure = "verify-name";
                return false;
            }
            TopoDS_Shape shape;
            if (!shapes->GetShape(label, shape) || shape.IsNull())
            {
                failure = "verify-shape";
                return false;
            }
            if (CountOf(shape, TopAbs_SOLID) != 1)
            {
                failure = "verify-solid";
                return false;
            }
            if (!BRepCheck_Analyzer(shape, Standard_False).IsValid())
            {
                failure = "verify-solid";
                return false;
            }
            if (!(SolidVolume(shape) > 0.0))
            {
                failure = "verify-solid";
                return false;
            }
            Quantity_Color color;
            if (!ReadComponentColor(colors, label, color) ||
                !ColorMatches(color, area.r, area.g, area.b))
            {
                failure = "verify-color";
                return false;
            }
            // FreeCAD resolves the part colour from the referred shape label
            // after removing the assembly location, so this must also be set.
            TDF_Label referred;
            if (!XCAFDoc_ShapeTool::GetReferredShape(label, referred) ||
                referred.IsNull())
            {
                failure = "verify-color";
                return false;
            }
            Quantity_Color partColor;
            if ((!colors->GetColor(referred, XCAFDoc_ColorSurf, partColor) &&
                 !colors->GetColor(referred, XCAFDoc_ColorGen, partColor)) ||
                !ColorMatches(partColor, area.r, area.g, area.b))
            {
                failure = "verify-color";
                return false;
            }
        }
        return true;
    }

    inline bool VerifyRouteAssembly(const Handle(XCAFDoc_ShapeTool)& shapes,
                                    const Handle(XCAFDoc_ColorTool)& colors,
                                    const TDF_Label& assembly,
                                    const step_export_geometry::NativeStepExportArea& area,
                                    std::string& failure)
    {
        TDF_LabelSequence components;
        shapes->GetComponents(assembly, components);
        if (components.Length() != static_cast<Standard_Integer>(area.pieces.size()))
        {
            failure = "verify-parts";
            return false;
        }

        for (const step_export_geometry::NativeStepExportPiece& piece : area.pieces)
        {
            TDF_Label label;
            if (FindComponentByName(components, piece.name + L" 경로", label) != 1)
            {
                failure = "verify-name";
                return false;
            }
            TopoDS_Shape shape;
            if (!shapes->GetShape(label, shape) || shape.IsNull())
            {
                failure = "verify-shape";
                return false;
            }
            if (CountOf(shape, TopAbs_EDGE) != 1 ||
                CountOf(shape, TopAbs_VERTEX) != 2)
            {
                failure = "verify-centreline";
                return false;
            }
            Quantity_Color color;
            if (!ReadComponentColor(colors, label, color) ||
                !ColorMatches(color, area.r, area.g, area.b))
            {
                failure = "verify-color";
                return false;
            }
            // Edge-only parts need a curve colour on the referred shape label;
            // otherwise FreeCAD paints them with its dark default line colour.
            TDF_Label referred;
            if (!XCAFDoc_ShapeTool::GetReferredShape(label, referred) ||
                referred.IsNull())
            {
                failure = "verify-color";
                return false;
            }
            Quantity_Color partColor;
            if (!colors->GetColor(referred, XCAFDoc_ColorCurv, partColor) ||
                !ColorMatches(partColor, area.r, area.g, area.b))
            {
                failure = "verify-color";
                return false;
            }
        }
        return true;
    }

    inline bool VerifyStepDocumentNative(const std::string& path,
                                         const std::wstring& modelName,
                                         const TopoDS_Shape& model,
                                         const TopoDS_Shape& referenceModel,
                                         const std::vector<step_export_geometry::NativeStepExportArea>& areas,
                                         const std::vector<step_export_geometry::NativeStepAreaFaceColor>& faceColors,
                                         std::string& failure)
    {
        // Non-empty file probe: OCCT treats char* paths as UTF-8, while MSVC's
        // std::ifstream(char*) uses the ANSI code page, so use OSD_OpenStream.
        {
            std::ifstream probe;
            OSD_OpenStream(probe, path.c_str(), std::ios::binary | std::ios::ate);
            if (!probe.is_open() || probe.tellg() <= 0)
            {
                failure = "verify-empty";
                return false;
            }
        }

        Handle(TDocStd_Document) document;
        const Handle(XCAFApp_Application) app =
            XCAFApp_Application::GetApplication();
        if (app.IsNull())
        {
            failure = "verify-read";
            return false;
        }
        app->NewDocument("MDTV-XCAF", document);
        if (document.IsNull())
        {
            failure = "verify-read";
            return false;
        }

        STEPCAFControl_Reader reader;
        reader.SetNameMode(Standard_True);
        reader.SetColorMode(Standard_True);
        if (reader.ReadFile(path.c_str()) != IFSelect_RetDone)
        {
            failure = "verify-read";
            return false;
        }
        if (!reader.Transfer(document))
        {
            failure = "verify-read";
            return false;
        }

        const Handle(XCAFDoc_ShapeTool) shapes =
            XCAFDoc_DocumentTool::ShapeTool(document->Main());
        const Handle(XCAFDoc_ColorTool) colors =
            XCAFDoc_DocumentTool::ColorTool(document->Main());
        if (shapes.IsNull() || colors.IsNull())
        {
            failure = "verify-parts";
            return false;
        }

        TDF_LabelSequence freeShapes;
        shapes->GetFreeShapes(freeShapes);
        const Standard_Integer expectedRoots =
            1 + static_cast<Standard_Integer>(areas.size()) * 2;
        if (freeShapes.Length() != expectedRoots)
        {
            failure = "verify-parts";
            return false;
        }

        TDF_Label modelLabel;
        if (!FindFreeShapeByName(shapes, freeShapes, modelName, modelLabel))
        {
            failure = "verify-parts";
            return false;
        }

        // Bounding box: the exported model must keep the original model extent.
        // Comparing against the split shape itself reports a false change: the
        // section edges introduced by BRepFeat_SplitShape carry conservative
        // BSpline pole bounds, so the in-memory box is a few hundredths of a
        // millimetre wider than the trimmed geometry that STEP actually writes.
        // Cuts never add material, so the pre-split shape is the reference.
        const NativeStepVerification expected =
            BoundsOf(referenceModel.IsNull() ? model : referenceModel);
        TopoDS_Shape readModel;
        if (!expected.valid || !shapes->GetShape(modelLabel, readModel) ||
            readModel.IsNull())
        {
            failure = "verify-shape";
            return false;
        }
        const NativeStepVerification actual = BoundsOf(readModel);
        if (!actual.valid)
        {
            failure = "verify-bounds";
            return false;
        }
        const Standard_Real diagonal =
            std::sqrt((expected.xMax - expected.xMin) * (expected.xMax - expected.xMin) +
                      (expected.yMax - expected.yMin) * (expected.yMax - expected.yMin) +
                      (expected.zMax - expected.zMin) * (expected.zMax - expected.zMin));
        const Standard_Real tolerance = std::max<Standard_Real>(0.05, diagonal * 1e-6);
        if (std::fabs(actual.xMin - expected.xMin) > tolerance ||
            std::fabs(actual.yMin - expected.yMin) > tolerance ||
            std::fabs(actual.zMin - expected.zMin) > tolerance ||
            std::fabs(actual.xMax - expected.xMax) > tolerance ||
            std::fabs(actual.yMax - expected.yMax) > tolerance ||
            std::fabs(actual.zMax - expected.zMax) > tolerance)
        {
            failure = "verify-bounds";
            return false;
        }

        if (!faceColors.empty())
        {
            Standard_Integer painted = 0;
            std::vector<Standard_Integer> perArea(areas.size(), 0);
            for (TopExp_Explorer explorer(readModel, TopAbs_FACE); explorer.More();
                 explorer.Next())
            {
                Quantity_Color faceColor;
                if (!colors->GetColor(explorer.Current(), XCAFDoc_ColorSurf,
                                      faceColor))
                {
                    continue;
                }
                ++painted;
                bool matched = false;
                for (size_t a = 0; a < areas.size(); ++a)
                {
                    if (ColorMatches(faceColor, areas[a].r, areas[a].g, areas[a].b))
                    {
                        ++perArea[a];
                        matched = true;
                        break;
                    }
                }
                if (!matched)
                {
                    failure = "verify-facecolor";
                    return false;
                }
            }
            if (painted != static_cast<Standard_Integer>(faceColors.size()))
            {
                failure = "verify-facecolor";
                return false;
            }
            for (size_t a = 0; a < areas.size(); ++a)
            {
                if (areas[a].HasRegion() && perArea[a] <= 0)
                {
                    failure = "verify-facecolor";
                    return false;
                }
            }
        }

        for (const step_export_geometry::NativeStepExportArea& area : areas)
        {
            TDF_Label wireAssembly;
            if (!FindFreeShapeByName(shapes, freeShapes, area.name + L" 발열사",
                                     wireAssembly))
            {
                failure = "verify-parts";
                return false;
            }
            TDF_Label routeAssembly;
            if (!FindFreeShapeByName(shapes, freeShapes, area.name + L" 발열경로",
                                     routeAssembly))
            {
                failure = "verify-parts";
                return false;
            }
            if (!VerifyWireAssembly(shapes, colors, wireAssembly, area, failure) ||
                !VerifyRouteAssembly(shapes, colors, routeAssembly, area, failure))
            {
                return false;
            }
        }
        return true;
    }

    inline bool WriteStepDocumentNative(
        const std::string& path,
        const std::wstring& modelName,
        const TopoDS_Shape& model,
        const TopoDS_Shape& referenceModel,
        const std::vector<step_export_geometry::NativeStepExportBuiltPiece>& wirePieces,
        const std::vector<step_export_geometry::NativeStepExportBuiltPiece>& routePieces,
        const std::vector<step_export_geometry::NativeStepExportArea>& areas,
        const std::vector<step_export_geometry::NativeStepAreaFaceColor>& faceColors,
        std::string& failure)
    {
        const Handle(XCAFApp_Application) app =
            XCAFApp_Application::GetApplication();
        if (app.IsNull())
        {
            failure = "xde-application";
            return false;
        }
        Handle(TDocStd_Document) document;
        app->NewDocument("MDTV-XCAF", document);
        if (document.IsNull())
        {
            failure = "xde-document";
            return false;
        }
        const Handle(XCAFDoc_ShapeTool) shapes =
            XCAFDoc_DocumentTool::ShapeTool(document->Main());
        const Handle(XCAFDoc_ColorTool) colors =
            XCAFDoc_DocumentTool::ColorTool(document->Main());
        if (shapes.IsNull() || colors.IsNull())
        {
            failure = "xde-tools";
            return false;
        }

        const TDF_Label modelLabel = shapes->AddShape(model, Standard_False,
                                                      Standard_False);
        TDataStd_Name::Set(modelLabel, TCollection_ExtendedString(modelName.c_str()));

        // Heating-area colour: the painted faces are labelled sub-shapes of the
        // single model free shape, so the area fill belongs to the model part
        // instead of being a second, coincident copy of the surface.
        for (const step_export_geometry::NativeStepAreaFaceColor& entry : faceColors)
        {
            if (entry.areaIndex < 0 ||
                static_cast<size_t>(entry.areaIndex) >= areas.size() ||
                entry.face.IsNull())
            {
                failure = "xde-face";
                return false;
            }
            TDF_Label faceLabel;
            shapes->AddSubShape(modelLabel, entry.face, faceLabel);
            if (faceLabel.IsNull())
            {
                failure = "xde-face";
                return false;
            }
            const step_export_geometry::NativeStepExportArea& area =
                areas[static_cast<size_t>(entry.areaIndex)];
            const Quantity_Color faceColor(area.r / 255.0, area.g / 255.0,
                                           area.b / 255.0, Quantity_TOC_sRGB);
            colors->SetColor(faceLabel, faceColor, XCAFDoc_ColorSurf);
        }

        size_t cursor = 0;
        for (const step_export_geometry::NativeStepExportArea& area : areas)
        {
            const TDF_Label wireAssembly = shapes->NewShape();
            TDataStd_Name::Set(wireAssembly,
                               TCollection_ExtendedString((area.name + L" 발열사").c_str()));
            const TDF_Label routeAssembly = shapes->NewShape();
            TDataStd_Name::Set(routeAssembly,
                               TCollection_ExtendedString((area.name + L" 발열경로").c_str()));
            const Quantity_Color color(area.r / 255.0, area.g / 255.0,
                                       area.b / 255.0, Quantity_TOC_sRGB);
            for (size_t i = 0; i < area.pieces.size(); ++i)
            {
                if (cursor >= wirePieces.size() || cursor >= routePieces.size())
                {
                    failure = "xde-document";
                    return false;
                }
                const TDF_Label wireLabel =
                    shapes->AddComponent(wireAssembly, wirePieces[cursor].wire,
                                         Standard_False);
                const TCollection_ExtendedString wireName(
                    wirePieces[cursor].name.c_str());
                TDataStd_Name::Set(wireLabel, wireName);
                colors->SetColor(wireLabel, color, XCAFDoc_ColorGen);
                // FreeCAD resolves a part's colour through the referred shape
                // label after removing the assembly location, not through the
                // occurrence label. Put the colour on both, and give the
                // edge-only centreline a curve colour so FreeCAD does not
                // fall back to its dark default line colour.
                TDF_Label wireShape;
                if (XCAFDoc_ShapeTool::GetReferredShape(wireLabel, wireShape) &&
                    !wireShape.IsNull())
                {
                    TDataStd_Name::Set(wireShape, wireName);
                    colors->SetColor(wireShape, color, XCAFDoc_ColorGen);
                    colors->SetColor(wireShape, color, XCAFDoc_ColorSurf);
                    colors->SetColor(wireShape, color, XCAFDoc_ColorCurv);
                }

                const TDF_Label routeLabel =
                    shapes->AddComponent(routeAssembly, routePieces[cursor].centreline,
                                         Standard_False);
                const TCollection_ExtendedString routeName(
                    (routePieces[cursor].name + L" 경로").c_str());
                TDataStd_Name::Set(routeLabel, routeName);
                colors->SetColor(routeLabel, color, XCAFDoc_ColorGen);
                TDF_Label routeShape;
                if (XCAFDoc_ShapeTool::GetReferredShape(routeLabel, routeShape) &&
                    !routeShape.IsNull())
                {
                    TDataStd_Name::Set(routeShape, routeName);
                    colors->SetColor(routeShape, color, XCAFDoc_ColorGen);
                    colors->SetColor(routeShape, color, XCAFDoc_ColorCurv);
                }
                ++cursor;
            }
        }
        shapes->UpdateAssemblies();

        STEPCAFControl_Writer writer;
        writer.SetNameMode(Standard_True);
        writer.SetColorMode(Standard_True);
        writer.SetLayerMode(Standard_True);
        writer.SetPropsMode(Standard_True);
        if (!writer.Transfer(document, STEPControl_AsIs))
        {
            failure = "step-transfer";
            return false;
        }
        if (writer.Write(path.c_str()) != IFSelect_RetDone)
        {
            failure = "step-write";
            return false;
        }

        return VerifyStepDocumentNative(path, modelName, model, referenceModel, areas,
                                        faceColors, failure);
    }

    inline bool ExportStepWithRoutesNative(
        const std::string& path,
        const std::wstring& modelName,
        const Standard_Real diameterMm,
        const std::vector<step_export_geometry::NativeStepExportArea>& areas,
        std::string& failure)
    {
        failure.clear();
        if (g_shape.IsNull())
        {
            failure = "no-model";
            return false;
        }
        if (!(diameterMm > 0.0) || !std::isfinite(diameterMm))
        {
            failure = "invalid-diameter";
            return false;
        }
        if (areas.empty())
        {
            failure = "no-circuits";
            return false;
        }

        bool paintAreas = false;
        for (size_t a = 0; a < areas.size(); ++a)
        {
            if (areas[a].HasRegion())
            {
                paintAreas = true;
                break;
            }
        }

        // Heating-area colour needs the exact face identity that the area's face
        // ids refer to, so the painted path works on the loaded model itself.
        // BRepBuilderAPI_Copy is not guaranteed to keep the face order, and a
        // silent reordering would paint the wrong surface. The split/section
        // tools do not mutate their input, and the XDE document only references
        // the shapes it is given, so g_shape stays unchanged.
        TopoDS_Shape model;
        std::vector<step_export_geometry::NativeStepAreaFaceColor> faceColors;
        if (paintAreas)
        {
            std::vector<TopoDS_Face> modelFaces = g_faces;
            TopoDS_Shape painted;
            if (!step_export_geometry::BuildAreaColoredModelNative(
                    g_shape, modelFaces, areas, painted, faceColors, failure))
            {
                return false;
            }
            model = painted;
        }
        else
        {
            BRepBuilderAPI_Copy copy(g_shape, Standard_True, Standard_False);
            if (!copy.IsDone() || copy.Shape().IsNull())
            {
                failure = "model-copy";
                return false;
            }
            model = copy.Shape();
        }

        const Standard_Real radius = diameterMm * 0.5;

        std::vector<step_export_geometry::NativeStepExportBuiltPiece> wirePieces;
        std::vector<step_export_geometry::NativeStepExportBuiltPiece> routePieces;
        for (size_t a = 0; a < areas.size(); ++a)
        {
            const step_export_geometry::NativeStepExportArea& area = areas[a];
            if (area.name.empty() || area.pieces.empty())
            {
                failure = "no-circuits";
                return false;
            }
            for (size_t p = 0; p < area.pieces.size(); ++p)
            {
                const step_export_geometry::NativeStepExportPiece& piece =
                    area.pieces[p];
                step_export_geometry::NativeStepExportBuiltPiece built;
                built.name = piece.name.empty() ? area.name : piece.name;
                std::string reason;
                if (!step_export_geometry::BuildPieceAxisNative(
                        piece.points, radius, built.centreline, built.wire, reason))
                {
                    std::ostringstream token;
                    token << "piece-build:" << a << ":" << p << ":"
                          << (reason.empty() ? "fit" : reason);
                    failure = token.str();
                    return false;
                }
                wirePieces.push_back(built);

                step_export_geometry::NativeStepExportBuiltPiece route;
                route.name = built.name;
                route.centreline = built.centreline;
                routePieces.push_back(route);
            }
        }

        return WriteStepDocumentNative(path, modelName, model, g_shape, wirePieces,
                                       routePieces, areas, faceColors, failure);
    }
}
