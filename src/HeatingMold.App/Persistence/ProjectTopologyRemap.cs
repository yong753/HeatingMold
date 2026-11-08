using System;
using System.Collections.Generic;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App.Persistence
{
    /// <summary>
    /// 저장된 topology signature를 새로 가져온 모델의 런타임 Face/Edge ID로 재결속한다.
    /// 원본 Edge는 signature·trim·방향이 모두 일치해야 하며, XY 최근접이나
    /// 런타임 인덱스 추정으로 대체하지 않는다.
    /// </summary>
    internal static class ProjectTopologyRemap
    {
        internal static bool TryMapFaceIds(
            IReadOnlyList<ProjectFaceTopologySignature> saved,
            IReadOnlyList<string> fresh,
            out int[] savedToFresh,
            out string error)
        {
            savedToFresh = Array.Empty<int>();
            error = null;

            if (saved == null || saved.Count == 0)
            {
                error = "저장된 Face topology signature가 없습니다.";
                return false;
            }
            if (fresh == null || fresh.Count == 0)
            {
                error = "새 모델의 Face topology signature가 없습니다.";
                return false;
            }
            if (saved.Count != fresh.Count)
            {
                error = "Face topology signature 수가 새 모델과 일치하지 않습니다.";
                return false;
            }

            var freshBySignature = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < fresh.Count; i++)
            {
                string signature = fresh[i];
                if (string.IsNullOrWhiteSpace(signature) ||
                    string.Equals(signature, "error", StringComparison.Ordinal))
                {
                    error = "새 모델 Face " + i + "의 topology signature가 올바르지 않습니다.";
                    return false;
                }
                if (!freshBySignature.TryAdd(signature, i))
                {
                    error = "새 모델의 Face topology signature가 중복됩니다.";
                    return false;
                }
            }

            var map = new int[saved.Count];
            Array.Fill(map, -1);
            var seenSavedIds = new HashSet<int>();
            foreach (ProjectFaceTopologySignature entry in saved)
            {
                if (entry == null || entry.FaceId < 0 || entry.FaceId >= saved.Count)
                {
                    error = "저장된 Face ID가 올바르지 않습니다.";
                    return false;
                }
                if (!seenSavedIds.Add(entry.FaceId))
                {
                    error = "저장된 Face ID가 중복됩니다.";
                    return false;
                }
                if (string.IsNullOrWhiteSpace(entry.Signature) ||
                    string.Equals(entry.Signature, "error", StringComparison.Ordinal))
                {
                    error = "저장된 Face " + entry.FaceId + "의 topology signature가 올바르지 않습니다.";
                    return false;
                }
                if (!freshBySignature.TryGetValue(entry.Signature, out int freshId))
                {
                    error = "저장된 Face " + entry.FaceId + "의 topology signature를 새 모델에서 찾지 못했습니다.";
                    return false;
                }
                map[entry.FaceId] = freshId;
            }

            for (int i = 0; i < map.Length; i++)
            {
                if (map[i] < 0)
                {
                    error = "저장된 Face " + i + "의 topology signature가 누락되었습니다.";
                    return false;
                }
            }

            savedToFresh = map;
            return true;
        }

        internal static bool TryMapExitEdgeReferences(
            IReadOnlyList<ProjectExitEdgeReference> saved,
            IReadOnlyList<ExitEdgeReference> fresh,
            int[] savedToFreshFace,
            out ExitEdgeReference[] mapped,
            out string error)
        {
            mapped = Array.Empty<ExitEdgeReference>();
            error = null;

            if (saved == null)
            {
                error = "저장된 exit edge reference가 없습니다.";
                return false;
            }
            if (saved.Count == 0)
            {
                mapped = Array.Empty<ExitEdgeReference>();
                return true;
            }
            if (fresh == null || fresh.Count == 0)
            {
                error = "새 모델의 exit edge reference가 없습니다.";
                return false;
            }

            var result = new ExitEdgeReference[saved.Count];
            var usedFresh = new HashSet<int>();
            for (int i = 0; i < saved.Count; i++)
            {
                ProjectExitEdgeReference reference = saved[i];
                if (reference == null)
                {
                    error = "저장된 exit edge reference가 null입니다.";
                    return false;
                }
                if (string.IsNullOrWhiteSpace(reference.EdgeSignature) ||
                    string.Equals(reference.EdgeSignature, "error", StringComparison.Ordinal))
                {
                    error = "저장된 exit edge " + i + "의 topology signature가 올바르지 않습니다.";
                    return false;
                }

                bool filterFace = reference.FaceId >= 0;
                int freshFace = -1;
                if (filterFace)
                {
                    if (savedToFreshFace == null || reference.FaceId >= savedToFreshFace.Length ||
                        savedToFreshFace[reference.FaceId] < 0)
                    {
                        error = "저장된 exit edge " + i + "의 Face를 새 모델에서 찾지 못했습니다.";
                        return false;
                    }
                    freshFace = savedToFreshFace[reference.FaceId];
                }

                int found = -1;
                for (int j = 0; j < fresh.Count; j++)
                {
                    ExitEdgeReference candidate = fresh[j];
                    if (candidate == null ||
                        !string.Equals(candidate.EdgeSignature, reference.EdgeSignature,
                            StringComparison.Ordinal))
                        continue;
                    if (filterFace && candidate.FaceId != freshFace) continue;
                    if (candidate.FirstParameter != reference.FirstParameter ||
                        candidate.LastParameter != reference.LastParameter ||
                        candidate.Reversed != reference.Reversed)
                        continue;
                    if (usedFresh.Contains(j))
                    {
                        error = "저장된 exit edge " + i + "가 같은 새 edge에 중복 결속됩니다.";
                        return false;
                    }
                    if (found >= 0)
                    {
                        error = "저장된 exit edge " + i + "에 대응하는 새 edge가 여러 개입니다.";
                        return false;
                    }
                    found = j;
                }

                if (found < 0)
                {
                    error = "저장된 exit edge " + i + "의 signature·trim·방향과 일치하는 새 edge가 없습니다.";
                    return false;
                }

                usedFresh.Add(found);
                result[i] = CadExitBoundary2D.CopyReference(fresh[found]);
            }

            mapped = result;
            return true;
        }

        internal static bool TryMapExitBinding(
            ProjectExitEdgeBinding saved,
            ProjectExitEdgeReference savedReference,
            ExitEdgeReference freshReference,
            long freshModelRevision,
            long contextId,
            long contextRevision,
            out ExitEdgeBinding mapped,
            out string error)
        {
            mapped = default;
            error = null;

            if (saved == null || savedReference == null || freshReference == null)
            {
                error = "저장된 exit binding 또는 edge reference가 없습니다.";
                return false;
            }
            if (saved.EdgeKey != savedReference.EdgeKey ||
                saved.FirstParameter != savedReference.FirstParameter ||
                saved.LastParameter != savedReference.LastParameter ||
                saved.Reversed != savedReference.Reversed)
            {
                error = "저장된 exit binding이 저장된 edge reference와 일치하지 않습니다.";
                return false;
            }
            if (!string.Equals(freshReference.EdgeSignature, savedReference.EdgeSignature,
                    StringComparison.Ordinal))
            {
                error = "새 edge의 topology signature가 저장된 값과 다릅니다.";
                return false;
            }
            if (freshReference.Revision != freshModelRevision)
            {
                error = "새 edge의 모델 revision이 현재 작업 컨텍스트와 다릅니다.";
                return false;
            }
            if (freshReference.EdgeKey <= 0 || !double.IsFinite(saved.Parameter))
            {
                error = "새 edge key 또는 저장된 edge parameter가 올바르지 않습니다.";
                return false;
            }

            mapped = new ExitEdgeBinding(
                freshModelRevision,
                contextId,
                contextRevision,
                freshReference.EdgeKey,
                freshReference.FirstParameter,
                freshReference.LastParameter,
                saved.Parameter,
                freshReference.Reversed);
            return true;
        }

        internal static bool TryMapExitBoundaryPoint(
            ProjectExitBoundaryPoint saved,
            IReadOnlyList<ProjectExitEdgeReference> savedReferences,
            IReadOnlyList<ExitEdgeReference> freshReferences,
            long freshModelRevision,
            long contextId,
            long contextRevision,
            out ExitBoundaryPoint mapped,
            out string error)
        {
            mapped = default;
            error = null;

            if (saved == null || saved.Point == null)
            {
                error = "저장된 exit boundary point가 없습니다.";
                return false;
            }
            if (!double.IsFinite(saved.Point.X) || !double.IsFinite(saved.Point.Y) ||
                !double.IsFinite(saved.SegmentParameter))
            {
                error = "저장된 exit boundary point에 유한하지 않은 값이 있습니다.";
                return false;
            }

            bool hasCad = saved.Binding != null;
            bool hasArtificial = saved.ArtificialBinding != null;
            if (hasCad == hasArtificial)
            {
                error = "저장된 exit boundary point에는 CAD 또는 artificial binding 중 하나만 있어야 합니다.";
                return false;
            }

            ExitEdgeBinding? cadBinding = null;
            ArtificialEdgeBinding? artificialBinding = null;
            if (hasCad)
            {
                if (savedReferences == null || freshReferences == null ||
                    savedReferences.Count != freshReferences.Count)
                {
                    error = "저장된 exit edge reference와 새 edge reference 수가 일치하지 않습니다.";
                    return false;
                }

                int index = -1;
                for (int i = 0; i < savedReferences.Count; i++)
                {
                    ProjectExitEdgeReference reference = savedReferences[i];
                    if (reference == null) continue;
                    if (reference.EdgeKey == saved.Binding.EdgeKey &&
                        reference.FirstParameter == saved.Binding.FirstParameter &&
                        reference.LastParameter == saved.Binding.LastParameter &&
                        reference.Reversed == saved.Binding.Reversed)
                    {
                        if (index >= 0)
                        {
                            error = "저장된 exit boundary point의 edge reference가 여러 개입니다.";
                            return false;
                        }
                        index = i;
                    }
                }
                if (index < 0)
                {
                    error = "저장된 exit boundary point의 edge reference를 찾지 못했습니다.";
                    return false;
                }
                if (!TryMapExitBinding(saved.Binding, savedReferences[index], freshReferences[index],
                        freshModelRevision, contextId, contextRevision,
                        out ExitEdgeBinding mappedCad, out error))
                    return false;
                cadBinding = mappedCad;
            }
            else
            {
                if (!TryMapArtificialBinding(saved.ArtificialBinding, contextId, contextRevision,
                        out ArtificialEdgeBinding mappedArtificial, out error))
                    return false;
                artificialBinding = mappedArtificial;
            }

            mapped = new ExitBoundaryPoint(
                new Vec2(saved.Point.X, saved.Point.Y),
                saved.SegmentIndex,
                saved.SegmentParameter)
            {
                Binding = cadBinding,
                ArtificialBinding = artificialBinding
            };
            return true;
        }

        internal static bool TryMapArtificialBinding(
            ProjectArtificialEdgeBinding saved,
            long contextId,
            long contextRevision,
            out ArtificialEdgeBinding mapped,
            out string error)
        {
            mapped = default;
            error = null;

            if (saved == null)
            {
                error = "저장된 artificial edge binding이 없습니다.";
                return false;
            }
            if (!double.IsFinite(saved.MinX) || !double.IsFinite(saved.MinY) ||
                !double.IsFinite(saved.MaxX) || !double.IsFinite(saved.MaxY) ||
                !double.IsFinite(saved.FirstParameter) ||
                !double.IsFinite(saved.LastParameter) ||
                !double.IsFinite(saved.Parameter))
            {
                error = "저장된 artificial edge binding에 유한하지 않은 값이 있습니다.";
                return false;
            }
            if (saved.MinX > saved.MaxX || saved.MinY > saved.MaxY || saved.Side < 0 || saved.Side > 3)
            {
                error = "저장된 artificial edge rectangle 또는 side가 올바르지 않습니다.";
                return false;
            }

            mapped = new ArtificialEdgeBinding(
                contextId,
                contextRevision,
                saved.MinX,
                saved.MinY,
                saved.MaxX,
                saved.MaxY,
                saved.Side,
                saved.FirstParameter,
                saved.LastParameter,
                saved.Parameter);
            return true;
        }
    }
}
