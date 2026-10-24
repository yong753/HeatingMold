#include <BRepBuilderAPI_MakeFace.hxx>
#include <BRepBuilderAPI_MakePolygon.hxx>
#include <BRepBuilderAPI_Sewing.hxx>
#include <BRepPrimAPI_MakeCylinder.hxx>
#include <BRepPrimAPI_MakeSphere.hxx>
#include <BRep_Builder.hxx>
#include <Geom_BSplineSurface.hxx>
#include <STEPControl_Writer.hxx>
#include <TColgp_Array2OfPnt.hxx>
#include <TColStd_Array1OfInteger.hxx>
#include <TColStd_Array1OfReal.hxx>
#include <TopoDS_Compound.hxx>
#include <TopoDS_Face.hxx>
#include <TopoDS_Shape.hxx>
#include <TopoDS_Wire.hxx>
#include <gp_Ax2.hxx>
#include <gp_Dir.hxx>

#include <cmath>
#include <fstream>
#include <iterator>
#include <limits>
#include <string>

#include "../../src/OccBridge/NativeGeometryMath.h"

static TopoDS_Wire MakeRectangleWire(
    double minimumX,
    double minimumY,
    double maximumX,
    double maximumY,
    double z)
{
    BRepBuilderAPI_MakePolygon polygon;
    polygon.Add(gp_Pnt(minimumX, minimumY, z));
    polygon.Add(gp_Pnt(maximumX, minimumY, z));
    polygon.Add(gp_Pnt(maximumX, maximumY, z));
    polygon.Add(gp_Pnt(minimumX, maximumY, z));
    polygon.Close();
    return polygon.Wire();
}

static TopoDS_Face MakeFlatSheet(
    double minimumX,
    double minimumY,
    double maximumX,
    double maximumY,
    double z)
{
    return BRepBuilderAPI_MakeFace(
        MakeRectangleWire(minimumX, minimumY, maximumX, maximumY, z)).Face();
}

static TopoDS_Face MakeSlopedSheet(double angleDegrees)
{
    const double span = 100.0;
    const double angleRadians = angleDegrees * std::acos(-1.0) / 180.0;
    const double height = span * std::tan(angleRadians);

    BRepBuilderAPI_MakePolygon polygon;
    polygon.Add(gp_Pnt(-span / 2.0, -30.0, 0.0));
    polygon.Add(gp_Pnt(span / 2.0, -30.0, height));
    polygon.Add(gp_Pnt(span / 2.0, 30.0, height));
    polygon.Add(gp_Pnt(-span / 2.0, 30.0, 0.0));
    polygon.Close();
    return BRepBuilderAPI_MakeFace(polygon.Wire()).Face();
}

static TopoDS_Face MakeBSplineSheet()
{
    TColgp_Array2OfPnt poles(1, 4, 1, 4);
    for (int uIndex = 1; uIndex <= 4; ++uIndex)
    {
        const double u = static_cast<double>(uIndex - 1) / 3.0;
        for (int vIndex = 1; vIndex <= 4; ++vIndex)
        {
            const double v = static_cast<double>(vIndex - 1) / 3.0;
            const double x = -50.0 + 100.0 * u;
            const double y = -30.0 + 60.0 * v;
            const double z = 30.0 * u * (1.0 - u) + 16.0 * v * (1.0 - v);
            poles.SetValue(uIndex, vIndex, gp_Pnt(x, y, z));
        }
    }

    TColStd_Array1OfReal uKnots(1, 2);
    TColStd_Array1OfReal vKnots(1, 2);
    uKnots.SetValue(1, 0.0);
    uKnots.SetValue(2, 1.0);
    vKnots.SetValue(1, 0.0);
    vKnots.SetValue(2, 1.0);

    TColStd_Array1OfInteger uMultiplicities(1, 2);
    TColStd_Array1OfInteger vMultiplicities(1, 2);
    uMultiplicities.SetValue(1, 4);
    uMultiplicities.SetValue(2, 4);
    vMultiplicities.SetValue(1, 4);
    vMultiplicities.SetValue(2, 4);

    Handle(Geom_BSplineSurface) surface = new Geom_BSplineSurface(
        poles,
        uKnots,
        vKnots,
        uMultiplicities,
        vMultiplicities,
        3,
        3,
        Standard_False,
        Standard_False);
    return BRepBuilderAPI_MakeFace(surface, 1e-7).Face();
}

static TopoDS_Shape MakeHalfCylinder()
{
    // +Z에서 반원통 외피가 드러나도록 축과 시작 방향을 고정한다.
    const gp_Ax2 axis(gp_Pnt(0.0, 0.0, 0.0), gp_Dir(0.0, 1.0, 0.0), gp_Dir(-1.0, 0.0, 0.0));
    return BRepPrimAPI_MakeCylinder(axis, 50.0, 100.0, std::acos(-1.0)).Shape();
}

static TopoDS_Compound MakeThreePanels()
{
    BRepBuilderAPI_Sewing sewing(1e-6);
    auto makePanel = [](double minimumX, double minimumZ, double maximumX, double maximumZ)
    {
        BRepBuilderAPI_MakePolygon polygon;
        polygon.Add(gp_Pnt(minimumX, 0.0, minimumZ));
        polygon.Add(gp_Pnt(maximumX, 0.0, maximumZ));
        polygon.Add(gp_Pnt(maximumX, 1.0, maximumZ));
        polygon.Add(gp_Pnt(minimumX, 1.0, minimumZ));
        polygon.Close();
        return BRepBuilderAPI_MakeFace(polygon.Wire()).Face();
    };

    sewing.Add(makePanel(0.0, 0.0, 1.0, 0.5));
    sewing.Add(makePanel(1.0, 0.5, 2.0, 0.0));
    sewing.Add(makePanel(2.0, 0.0, 3.0, 0.5));
    sewing.Perform();

    TopoDS_Compound shape;
    BRep_Builder builder;
    builder.MakeCompound(shape);
    builder.Add(shape, sewing.SewedShape());
    return shape;
}

static bool WriteStepFixture(const char* directory, const char* name, const TopoDS_Shape& shape)
{
    STEPControl_Writer writer;
    if (writer.Transfer(shape, STEPControl_AsIs) != IFSelect_RetDone)
        return false;

    const std::string path = std::string(directory) + "/" + name + ".stp";
    if (writer.Write(path.c_str()) != IFSelect_RetDone)
        return false;

    std::ifstream input(path);
    if (!input)
        return false;

    std::string contents(
        (std::istreambuf_iterator<char>(input)),
        std::istreambuf_iterator<char>());
    input.close();
    const std::string representation = "REPRESENTATION('',(";
    const std::size_t representationOffset = contents.find(representation);
    if (representationOffset == std::string::npos)
        return false;

    contents.replace(
        representationOffset,
        representation.size(),
        "REPRESENTATION('supplemental geometry',(");

    std::ofstream output(path, std::ios::trunc);
    output << contents;
    output.flush();
    return output.good();
}

static bool StableMidpointChecksPass()
{
    using HeatingMoldNativeGeometry::StableMidpoint;

    const double maximum = std::numeric_limits<double>::max();
    return StableMidpoint(maximum, maximum) == maximum &&
        StableMidpoint(-maximum, -maximum) == -maximum &&
        StableMidpoint(maximum, -maximum) == 0.0 &&
        StableMidpoint(1.0, 3.0) == 2.0;
}

int main(int argumentCount, char** arguments)
{
    if (argumentCount != 2)
        return 1;
    if (!StableMidpointChecksPass())
        return 3;

    const char* fixtureDirectory = arguments[1];
    BRep_Builder builder;

    TopoDS_Compound layered;
    builder.MakeCompound(layered);
    builder.Add(layered, MakeFlatSheet(-50.0, -30.0, 50.0, 30.0, 0.0));
    builder.Add(layered, MakeFlatSheet(-20.0, -10.0, 20.0, 10.0, 20.0));

    TopoDS_Compound ring;
    builder.MakeCompound(ring);
    builder.Add(ring, MakeFlatSheet(0.0, 0.0, 100.0, 20.0, 0.0));
    builder.Add(ring, MakeFlatSheet(0.0, 80.0, 100.0, 100.0, 0.0));
    builder.Add(ring, MakeFlatSheet(0.0, 20.0, 20.0, 80.0, 0.0));
    builder.Add(ring, MakeFlatSheet(80.0, 20.0, 100.0, 80.0, 0.0));

    TopoDS_Compound tangentBack;
    builder.MakeCompound(tangentBack);
    builder.Add(tangentBack, BRepPrimAPI_MakeSphere(gp_Pnt(0.0, 0.0, 0.0), 20.0).Shape());
    builder.Add(tangentBack, MakeFlatSheet(-40.0, -40.0, 40.0, 40.0, -30.0));

    TopoDS_Compound trimmedVisibility;
    builder.MakeCompound(trimmedVisibility);
    BRepBuilderAPI_MakeFace trimmedTop(
        MakeRectangleWire(-50.0, -30.0, 50.0, 30.0, 20.0));
    TopoDS_Wire hole = MakeRectangleWire(-10.0, -10.0, 10.0, 10.0, 20.0);
    hole.Reverse();
    trimmedTop.Add(hole);
    builder.Add(trimmedVisibility, trimmedTop.Face());
    builder.Add(trimmedVisibility, MakeFlatSheet(-70.0, -50.0, 70.0, 50.0, 0.0));

    const bool fixturesWritten =
        WriteStepFixture(fixtureDirectory, "flat", MakeFlatSheet(-50.0, -30.0, 50.0, 30.0, 0.0)) &&
        WriteStepFixture(fixtureDirectory, "slope30", MakeSlopedSheet(30.0)) &&
        WriteStepFixture(fixtureDirectory, "slope60", MakeSlopedSheet(60.0)) &&
        WriteStepFixture(fixtureDirectory, "bsplineSurface", MakeBSplineSheet()) &&
        WriteStepFixture(fixtureDirectory, "layers", layered) &&
        WriteStepFixture(fixtureDirectory, "ring", ring) &&
        WriteStepFixture(fixtureDirectory, "trimmedVisibility", trimmedVisibility) &&
        WriteStepFixture(fixtureDirectory, "tangentBack", tangentBack) &&
        WriteStepFixture(fixtureDirectory, "threePanels", MakeThreePanels()) &&
        WriteStepFixture(fixtureDirectory, "halfCylinder", MakeHalfCylinder()) &&
        WriteStepFixture(
            fixtureDirectory,
            "sphere",
            BRepPrimAPI_MakeSphere(gp_Pnt(0.0, 0.0, 0.0), 20.0).Shape());

    return fixturesWritten ? 0 : 2;
}
