#pragma once

// 순수 위상 순회: 외곽 경계 Edge의 기존 정점 연결만 따라 순서와 진행 방향을
// 결정한다. Wire를 새로 만들거나 정점/Edge를 교체/병합하지 않는다.
// 원본 CAD 참조·파라미터 구간·소유 Face 판정은 호출자가 보관한다.

#include <TopAbs_Orientation.hxx>
#include <TopExp.hxx>
#include <TopTools_IndexedMapOfShape.hxx>
#include <TopoDS_Edge.hxx>
#include <TopoDS_Vertex.hxx>

#include <string>
#include <utility>
#include <vector>

#ifdef _MANAGED
#pragma managed(push, off)
#endif
namespace HeatingMoldBoundaryNative
{
    struct BoundaryStep
    {
        int inputIndex = -1;
        // false: 입력 Edge의 oriented first -> oriented last 방향으로 진행.
        // true : 그 반대 방향.
        bool reverse = false;
    };

    struct BoundaryComponent
    {
        std::vector<BoundaryStep> steps;
    };

    /// <summary>
    /// 입력 순서대로 주어진 외곽 Edge들을 실제 정점 identity로 연결해 루프별로
    /// 정렬한다. 반환값 false는 그래프 자체가 성립하지 않는 오류다. 개별 루프
    /// 순회 실패는 failedComponents로 보고하고 나머지 루프는 계속 처리한다.
    /// </summary>
    inline bool OrderBoundaryLoops(const std::vector<TopoDS_Edge>& edges,
                                   std::vector<BoundaryComponent>& components,
                                   int& failedComponents,
                                   std::string& failure)
    {
        components.clear();
        failedComponents = 0;
        failure.clear();
        if (edges.empty()) { failure = "empty boundary edge set"; return false; }

        const int count = static_cast<int>(edges.size());
        if (count > 100000) { failure = "boundary edge count exceeds limit"; return false; }

        TopTools_IndexedMapOfShape edgeSet;
        for (int i = 0; i < count; ++i)
        {
            const TopoDS_Edge& edge = edges[i];
            if (edge.IsNull()) { failure = "boundary contains a null edge"; return false; }
            if (edge.Orientation() != TopAbs_FORWARD && edge.Orientation() != TopAbs_REVERSED)
            {
                failure = "boundary edge orientation is neither FORWARD nor REVERSED";
                return false;
            }
            if (edgeSet.Contains(edge))
            {
                failure = "boundary contains the same edge more than once";
                return false;
            }
            edgeSet.Add(edge);
        }

        struct EdgeEnds { TopoDS_Vertex vertex[2]; };
        std::vector<EdgeEnds> ends(static_cast<size_t>(count));
        TopTools_IndexedMapOfShape vertexMap;
        // 각 정점에 닿는 (edgeIndex, endpointSlot) 목록. 닫힌 Edge 하나는
        // 같은 정점에 두 슬롯을 모두 등록해 그래프 차수 2를 만든다.
        std::vector<std::vector<int>> incident;
        auto addIncidence = [&](const TopoDS_Vertex& vertex, int edgeIndex, int slot)
        {
            int index = vertexMap.FindIndex(vertex);
            if (index == 0)
            {
                vertexMap.Add(vertex);
                incident.emplace_back();
                index = vertexMap.Extent();
            }
            incident[static_cast<size_t>(index - 1)].push_back(edgeIndex * 2 + slot);
        };

        for (int i = 0; i < count; ++i)
        {
            TopoDS_Vertex first, last;
            // orient=true: Edge의 실제 방향을 반영한 시작/끝 정점.
            TopExp::Vertices(edges[static_cast<size_t>(i)], first, last, Standard_True);
            if (first.IsNull() || last.IsNull())
            {
                failure = "boundary edge is missing an endpoint vertex";
                return false;
            }
            ends[static_cast<size_t>(i)].vertex[0] = first;
            ends[static_cast<size_t>(i)].vertex[1] = last;
            addIncidence(first, i, 0);
            addIncidence(last, i, 1);
        }

        for (size_t v = 0; v < incident.size(); ++v)
        {
            if (incident[v].size() != 2)
            {
                failure = "boundary vertex is not shared by exactly two edge ends";
                return false;
            }
        }

        std::vector<bool> seen(static_cast<size_t>(count), false);
        for (int seed = 0; seed < count; ++seed)
        {
            if (seen[static_cast<size_t>(seed)]) continue;
            std::vector<int> component;
            std::vector<int> pending{seed};
            seen[static_cast<size_t>(seed)] = true;
            while (!pending.empty())
            {
                const int edgeIndex = pending.back();
                pending.pop_back();
                component.push_back(edgeIndex);
                for (int slot = 0; slot < 2; ++slot)
                {
                    const int vi = vertexMap.FindIndex(ends[static_cast<size_t>(edgeIndex)].vertex[slot]);
                    if (vi <= 0) continue;
                    for (int code : incident[static_cast<size_t>(vi - 1)])
                    {
                        const int adjacent = code / 2;
                        if (seen[static_cast<size_t>(adjacent)]) continue;
                        seen[static_cast<size_t>(adjacent)] = true;
                        pending.push_back(adjacent);
                    }
                }
            }

            // 시작 슬롯(oriented first)에서 나가 oriented last로 들어가는
            // 방향을 초기 방향으로 고정한다. 입력 순서가 같으면 결과도 같다.
            BoundaryComponent loop;
            std::vector<bool> visited(static_cast<size_t>(count), false);
            visited[static_cast<size_t>(seed)] = true;
            loop.steps.push_back({seed, false});
            int current = seed;
            bool reverse = false;
            bool closed = false;
            const int limit = static_cast<int>(component.size());
            for (int step = 0; step < limit; ++step)
            {
                const int exitSlot = reverse ? 0 : 1;
                const int exitVertex = vertexMap.FindIndex(ends[static_cast<size_t>(current)].vertex[exitSlot]);
                if (exitVertex <= 0) break;
                int nextEdge = -1;
                int nextSlot = -1;
                for (int code : incident[static_cast<size_t>(exitVertex - 1)])
                {
                    const int edgeIndex = code / 2;
                    const int slot = code % 2;
                    if (edgeIndex == current && slot == exitSlot) continue;
                    nextEdge = edgeIndex;
                    nextSlot = slot;
                    break;
                }
                if (nextEdge < 0) break;

                if (nextEdge == seed)
                {
                    // 모든 Edge를 소비한 뒤 시작 슬롯으로 돌아와야 폐합이다.
                    if (nextSlot != 0) break;
                    if (static_cast<int>(loop.steps.size()) != limit) break;
                    closed = true;
                    break;
                }
                if (visited[static_cast<size_t>(nextEdge)]) break;
                visited[static_cast<size_t>(nextEdge)] = true;
                const bool nextReverse = nextSlot == 1;
                loop.steps.push_back({nextEdge, nextReverse});
                current = nextEdge;
                reverse = nextReverse;
            }
            if (!closed)
            {
                ++failedComponents;
                failure += "boundary component did not close by its own vertex chain\n";
                continue;
            }
            components.push_back(std::move(loop));
        }
        return true;
    }
}
#ifdef _MANAGED
#pragma managed(pop)
#endif
