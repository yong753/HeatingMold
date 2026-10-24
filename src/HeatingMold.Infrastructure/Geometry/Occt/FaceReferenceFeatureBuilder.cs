using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Model;
using HeatingMold.Core.Primitives;

namespace HeatingMold.Infrastructure.Geometry.Occt;

/// <summary>Face 경계 와이어 하나의 3차원 표본과 경계 종류입니다.</summary>
public sealed class FaceBoundaryLoopSnapshot
{
    /// <summary>외곽 경계가 아니라 홀 경계인지 나타냅니다.</summary>
    public bool IsHole { get; }

    /// <summary>Native가 닫힌 와이어로 확인했는지 나타냅니다.</summary>
    public bool IsClosed { get; }

    /// <summary>경계를 따라 순서대로 표본화한 모델 좌표입니다.</summary>
    public IReadOnlyList<Vec3> Points { get; }

    /// <summary>경계 종류와 3차원 표본을 방어 복사해 보관합니다.</summary>
    public FaceBoundaryLoopSnapshot(bool isHole, bool isClosed, IEnumerable<Vec3> points)
    {
        IsHole = isHole;
        IsClosed = isClosed;
        Points = points?.ToArray() ?? throw new ArgumentNullException(nameof(points));
    }
}

/// <summary>하나의 현재 Face에서 수집한 특징 원본입니다.</summary>
public sealed class NativeFaceFeatureSnapshot
{
    /// <summary>현재 OCCT 문서에서 사용하는 0 기반 Face 번호입니다.</summary>
    public int NativeFaceIndex { get; }

    /// <summary>면의 기하 종류입니다.</summary>
    public FaceGeometryKind GeometryKind { get; }

    /// <summary>Native가 계산한 면적(mm²)입니다.</summary>
    public AreaMm2 SurfaceArea { get; }

    /// <summary>Native가 계산한 면 중심(mm)입니다.</summary>
    public Vec3 Center { get; }

    /// <summary>공유 Edge가 있는 이웃 Face의 현재 번호입니다.</summary>
    public IReadOnlyList<int> AdjacentNativeFaceIndices { get; }

    /// <summary>면 경계의 외곽·홀 표본입니다.</summary>
    public IReadOnlyList<FaceBoundaryLoopSnapshot> BoundaryLoops { get; }

    /// <summary>Native 값과 경계 자료를 변경에 안전하게 복사합니다.</summary>
    public NativeFaceFeatureSnapshot(
        int nativeFaceIndex,
        FaceGeometryKind geometryKind,
        AreaMm2 surfaceArea,
        Vec3 center,
        IEnumerable<int> adjacentNativeFaceIndices,
        IEnumerable<FaceBoundaryLoopSnapshot> boundaryLoops)
    {
        NativeFaceIndex = nativeFaceIndex;
        GeometryKind = geometryKind;
        SurfaceArea = surfaceArea;
        Center = center;
        AdjacentNativeFaceIndices = adjacentNativeFaceIndices?.ToArray()
            ?? throw new ArgumentNullException(nameof(adjacentNativeFaceIndices));
        BoundaryLoops = boundaryLoops?.ToArray()
            ?? throw new ArgumentNullException(nameof(boundaryLoops));
    }
}

/// <summary>Native 면 특징을 영속 Face 참조에 사용할 결정적 fingerprint로 변환합니다.</summary>
public sealed class FaceReferenceFeatureBuilder
{
    /// <summary>경계 표본 fingerprint의 형식 식별자입니다.</summary>
    public const string FingerprintVersion = "occ-face-boundary-samples-v1";

    /// <summary>현재 Face 집합의 면 특징과 이웃 경계 특징을 함께 만듭니다.</summary>
    public IReadOnlyList<NativeFaceFeature> Build(IEnumerable<NativeFaceFeatureSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        NativeFaceFeatureSnapshot[] faces = snapshots.ToArray();
        if (faces.Length == 0)
            throw new ArgumentException("Face 특징을 만들 입력이 비어 있습니다.", nameof(snapshots));
        if (faces.Any(face => face is null))
            throw new ArgumentException("Face 특징 입력에 null 항목이 있습니다.", nameof(snapshots));
        if (faces.Any(face => face.NativeFaceIndex < 0) ||
            faces.GroupBy(face => face.NativeFaceIndex).Any(group => group.Count() != 1))
        {
            throw new ArgumentException("Face 번호는 음수가 아니며 중복될 수 없습니다.", nameof(snapshots));
        }

        Dictionary<int, NativeFaceFeatureSnapshot> facesByIndex =
            faces.ToDictionary(face => face.NativeFaceIndex);
        Dictionary<int, string> boundaryByIndex = faces.ToDictionary(
            face => face.NativeFaceIndex,
            CreateBoundaryFingerprint);

        ValidateAdjacency(faces, facesByIndex);

        return faces
            .OrderBy(face => face.NativeFaceIndex)
            .Select(face => new NativeFaceFeature(
                face.NativeFaceIndex,
                face.GeometryKind,
                face.SurfaceArea,
                face.Center,
                boundaryByIndex[face.NativeFaceIndex],
                face.AdjacentNativeFaceIndices.Select(index => boundaryByIndex[index])))
            .ToArray();
    }

    private static void ValidateAdjacency(
        IReadOnlyList<NativeFaceFeatureSnapshot> faces,
        IReadOnlyDictionary<int, NativeFaceFeatureSnapshot> facesByIndex)
    {
        foreach (NativeFaceFeatureSnapshot face in faces)
        {
            if (face.AdjacentNativeFaceIndices.Distinct().Count() != face.AdjacentNativeFaceIndices.Count)
                throw new ArgumentException("한 Face에 중복된 이웃 번호가 있습니다.", nameof(faces));

            foreach (int adjacentIndex in face.AdjacentNativeFaceIndices)
            {
                if (adjacentIndex == face.NativeFaceIndex ||
                    !facesByIndex.TryGetValue(adjacentIndex, out NativeFaceFeatureSnapshot? adjacent) ||
                    adjacent is null)
                    throw new ArgumentException("이웃 Face 번호가 현재 Face 목록과 일치하지 않습니다.", nameof(faces));
                if (!adjacent.AdjacentNativeFaceIndices.Contains(face.NativeFaceIndex))
                    throw new ArgumentException("Face 인접 목록이 서로 대칭이 아닙니다.", nameof(faces));
            }
        }
    }

    private static string CreateBoundaryFingerprint(NativeFaceFeatureSnapshot face)
    {
        if (!Enum.IsDefined(face.GeometryKind))
            throw new ArgumentException("Face 기하 종류가 유효하지 않습니다.", nameof(face));
        if (!double.IsFinite(face.SurfaceArea.Value) || face.SurfaceArea.Value <= 0)
            throw new ArgumentException("Face 면적은 양의 유한값이어야 합니다.", nameof(face));

        FaceBoundaryLoopSnapshot[] loops = face.BoundaryLoops.ToArray();
        if (loops.Length == 0)
            return HashBoundaryPayload(["closed-surface"]);
        if (loops.Any(loop => loop is null || !loop.IsClosed))
            throw new ArgumentException("Face에는 닫힌 경계 와이어가 필요합니다.", nameof(face));
        if (!loops.Any(loop => !loop.IsHole))
            throw new ArgumentException("Face 외곽 경계가 없습니다.", nameof(face));

        string[] canonicalLoops = loops
            .Select(loop => string.Concat(loop.IsHole ? "H:" : "O:", CanonicalizeLoop(loop.Points)))
            .OrderBy(loop => loop, StringComparer.Ordinal)
            .ToArray();
        return HashBoundaryPayload(canonicalLoops);
    }

    private static string HashBoundaryPayload(IEnumerable<string> canonicalLoops)
    {
        byte[] payload = Encoding.UTF8.GetBytes(string.Join("|", canonicalLoops));
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    private static string CanonicalizeLoop(IReadOnlyList<Vec3> points)
    {
        string[] coordinates = points.Select(FormatPoint).ToArray();
        var distinctCoordinates = new List<string>(coordinates.Length);

        foreach (string coordinate in coordinates)
        {
            if (distinctCoordinates.Count == 0 ||
                !string.Equals(distinctCoordinates[^1], coordinate, StringComparison.Ordinal))
            {
                distinctCoordinates.Add(coordinate);
            }
        }

        if (distinctCoordinates.Count > 1 &&
            string.Equals(distinctCoordinates[0], distinctCoordinates[^1], StringComparison.Ordinal))
        {
            distinctCoordinates.RemoveAt(distinctCoordinates.Count - 1);
        }
        if (distinctCoordinates.Count < 3)
            throw new ArgumentException("닫힌 Face 경계에는 서로 다른 세 점 이상이 필요합니다.", nameof(points));

        string forward = FindSmallestRotation(distinctCoordinates);
        distinctCoordinates.Reverse();
        string backward = FindSmallestRotation(distinctCoordinates);
        return string.CompareOrdinal(forward, backward) <= 0 ? forward : backward;
    }

    private static string FindSmallestRotation(IReadOnlyList<string> points)
    {
        if (points.Count == 0)
            throw new ArgumentException("회전 정규화에 사용할 경계점이 없습니다.", nameof(points));

        int smallestPointIndex = 0;
        for (int index = 1; index < points.Count; index++)
        {
            if (string.CompareOrdinal(points[index], points[smallestPointIndex]) < 0)
                smallestPointIndex = index;
        }

        int bestStartIndex = smallestPointIndex;
        for (int candidateStartIndex = 0; candidateStartIndex < points.Count; candidateStartIndex++)
        {
            if (!string.Equals(
                    points[candidateStartIndex],
                    points[smallestPointIndex],
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (CompareRotations(points, candidateStartIndex, bestStartIndex) < 0)
                bestStartIndex = candidateStartIndex;
        }

        var smallestRotation = new string[points.Count];
        for (int offset = 0; offset < points.Count; offset++)
            smallestRotation[offset] = points[(bestStartIndex + offset) % points.Count];

        return string.Join(";", smallestRotation);
    }

    private static int CompareRotations(
        IReadOnlyList<string> points,
        int firstStartIndex,
        int secondStartIndex)
    {
        for (int offset = 0; offset < points.Count; offset++)
        {
            string first = points[(firstStartIndex + offset) % points.Count];
            string second = points[(secondStartIndex + offset) % points.Count];
            int comparison = string.CompareOrdinal(first, second);
            if (comparison != 0)
                return comparison;
        }

        return 0;
    }

    private static string FormatPoint(Vec3 point) => string.Join(",",
        point.X.ToString("R", CultureInfo.InvariantCulture),
        point.Y.ToString("R", CultureInfo.InvariantCulture),
        point.Z.ToString("R", CultureInfo.InvariantCulture));
}
