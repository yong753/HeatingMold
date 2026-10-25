// Generic coincident-face detector: finds faces (from different bodies) whose surfaces overlap.
#include <cstdio>
#include <cmath>
#include <string>
#include <vector>
#include <map>
#include <tuple>
#include <algorithm>

#include <STEPControl_Reader.hxx>
#include <TopoDS.hxx>
#include <TopoDS_Shape.hxx>
#include <TopExp.hxx>
#include <TopExp_Explorer.hxx>
#include <TopTools_IndexedMapOfShape.hxx>
#include <TopTools_IndexedDataMapOfShapeListOfShape.hxx>
#include <TopTools_ListIteratorOfListOfShape.hxx>
#include <BRepTools.hxx>
#include <BRepAdaptor_Surface.hxx>
#include <BRepGProp.hxx>
#include <GProp_GProps.hxx>

struct F
{
    TopoDS_Face face;
    int type = 0, solid = -1, shell = -1;
    double area = 0, u1 = 0, u2 = 0, v1 = 0, v2 = 0;
};

int main(int argc, char** argv)
{
    const char* path = argc > 1 ? argv[1] : "D:/0.Development/2.HeatingMold/resource/Mold.stp";
    STEPControl_Reader reader;
    if (reader.ReadFile(path) != IFSelect_RetDone) { printf("read failed: %s\n", path); return 1; }
    reader.TransferRoots();
    TopoDS_Shape shape = reader.OneShape();

    TopTools_IndexedMapOfShape solidMap, shellMap;
    TopExp::MapShapes(shape, TopAbs_SOLID, solidMap);
    TopExp::MapShapes(shape, TopAbs_SHELL, shellMap);
    TopTools_IndexedDataMapOfShapeListOfShape faceSolid, faceShell;
    TopExp::MapShapesAndAncestors(shape, TopAbs_FACE, TopAbs_SOLID, faceSolid);
    TopExp::MapShapesAndAncestors(shape, TopAbs_FACE, TopAbs_SHELL, faceShell);

    std::vector<F> fs;
    for (TopExp_Explorer ex(shape, TopAbs_FACE); ex.More(); ex.Next())
    {
        F f; f.face = TopoDS::Face(ex.Current());
        BRepAdaptor_Surface s(f.face, Standard_False);
        f.type = (int)s.GetType();
        BRepTools::UVBounds(f.face, f.u1, f.u2, f.v1, f.v2);
        GProp_GProps g; BRepGProp::SurfaceProperties(f.face, g); f.area = g.Mass();
        if (faceSolid.Contains(f.face))
        {
            TopTools_ListIteratorOfListOfShape it(faceSolid.FindFromKey(f.face));
            if (!faceSolid.FindFromKey(f.face).IsEmpty()) f.solid = solidMap.FindIndex(it.Value());
        }
        if (faceShell.Contains(f.face))
        {
            TopTools_ListIteratorOfListOfShape it(faceShell.FindFromKey(f.face));
            if (!faceShell.FindFromKey(f.face).IsEmpty()) f.shell = shellMap.FindIndex(it.Value());
        }
        fs.push_back(f);
    }

    // sample points on every face
    struct P { gp_Pnt p; int f; };
    std::vector<P> pts;
    std::vector<int> firstPt(fs.size()), ptCount(fs.size());
    for (size_t i = 0; i < fs.size(); ++i)
    {
        BRepAdaptor_Surface s(fs[i].face, Standard_False);
        firstPt[i] = (int)pts.size();
        int n = 0;
        for (int a = 1; a <= 3; ++a) for (int b = 1; b <= 3; ++b)
        {
            double u = fs[i].u1 + (fs[i].u2 - fs[i].u1) * a / 4.0;
            double v = fs[i].v1 + (fs[i].v2 - fs[i].v1) * b / 4.0;
            pts.push_back({ s.Value(u, v), (int)i });
            ++n;
        }
        ptCount[i] = n;
    }

    const double cell = 4.0, tol = 1e-4;
    std::map<std::tuple<int, int, int>, std::vector<int>> grid;
    auto key = [&](const gp_Pnt& p) {
        return std::make_tuple((int)std::floor(p.X() / cell), (int)std::floor(p.Y() / cell), (int)std::floor(p.Z() / cell));
    };
    for (int i = 0; i < (int)pts.size(); ++i) grid[key(pts[i].p)].push_back(i);

    std::map<std::pair<int, int>, int> hits;
    for (int i = 0; i < (int)pts.size(); ++i)
    {
        auto k = key(pts[i].p);
        for (int dx = -1; dx <= 1; ++dx) for (int dy = -1; dy <= 1; ++dy) for (int dz = -1; dz <= 1; ++dz)
        {
            auto it = grid.find(std::make_tuple(std::get<0>(k) + dx, std::get<1>(k) + dy, std::get<2>(k) + dz));
            if (it == grid.end()) continue;
            for (int j : it->second)
            {
                if (j <= i) continue;
                if (pts[i].f == pts[j].f) continue;
                if (pts[i].p.Distance(pts[j].p) > tol) continue;
                int a = std::min(pts[i].f, pts[j].f), b = std::max(pts[i].f, pts[j].f);
                hits[std::make_pair(a, b)]++;
            }
        }
    }

    int nPairs = 0;
    double dupAreaA = 0, dupAreaB = 0;
    std::vector<std::pair<int, std::pair<int, int>>> list;
    std::vector<char> faceDup(fs.size(), 0);
    for (auto& kv : hits)
    {
        if (kv.second < 3) continue;
        ++nPairs;
        list.push_back({ kv.second, kv.first });
        int a = kv.first.first, b = kv.first.second;
        dupAreaA += fs[a].area; dupAreaB += fs[b].area;
        faceDup[a] = 1; faceDup[b] = 1;
    }

    int solidSolid = 0, solidSurface = 0;
    for (auto& kv : hits)
    {
        if (kv.second < 3) continue;
        int a = kv.first.first, b = kv.first.second;
        bool sa = fs[a].solid > 0, sb = fs[b].solid > 0;
        if (sa && sb) ++solidSolid; else ++solidSurface;
    }

    printf("file: %s\n", path);
    printf("faces=%zu  coincident-face-pairs=%d  (solid<->solid=%d, solid<->surface=%d)\n",
        fs.size(), nPairs, solidSolid, solidSurface);
    printf("faces involved=%d (A-side area=%.1f, B-side area=%.1f)\n", (int)std::count(faceDup.begin(), faceDup.end(), 1), dupAreaA, dupAreaB);

    std::sort(list.begin(), list.end(), [](auto& x, auto& y) {
        return x.first > y.first;
    });
    const char* names[] = { "Plane", "Cylinder", "Cone", "Sphere", "Torus", "Bezier", "BSpline", "Revolution", "Extrusion", "Offset", "Other" };
    printf("\ntop pairs (by matched sample count):\n");
    for (int i = 0; i < (int)list.size() && i < 15; ++i)
    {
        int a = list[i].second.first, b = list[i].second.second;
        printf("  hits=%2d  #%-4d(%s solid=%2d A=%9.2f)  <->  #%-4d(%s solid=%2d A=%9.2f)\n",
            list[i].first, a, names[fs[a].type], fs[a].solid, fs[a].area,
            b, names[fs[b].type], fs[b].solid, fs[b].area);
    }
    return 0;
}
