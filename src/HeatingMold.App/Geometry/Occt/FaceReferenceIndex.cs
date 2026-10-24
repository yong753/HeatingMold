using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using HeatingMold.Core.Primitives;

namespace HeatingMold.App.Geometry.Occt;

/// <summary>현재 Native 면 번호와 영속 Face 참조 ID 사이의 변환을 관리합니다.</summary>
internal sealed class FaceReferenceIndex
{
    private Dictionary<int, FaceRefId> _referencesByNativeIndex = new();
    private Dictionary<FaceRefId, int> _nativeIndicesByReference = new();
    private IReadOnlyDictionary<int, FaceRefId> _readOnlyReferencesByNativeIndex;
    private IReadOnlyDictionary<FaceRefId, int> _readOnlyNativeIndicesByReference;

    /// <summary>현재 모델에 대응된 Native 면 번호별 영속 Face 참조를 제공합니다.</summary>
    public IReadOnlyDictionary<int, FaceRefId> ReferencesByNativeIndex => _readOnlyReferencesByNativeIndex;

    /// <summary>현재 모델에 대응된 영속 Face 참조별 Native 면 번호를 제공합니다.</summary>
    public IReadOnlyDictionary<FaceRefId, int> NativeIndicesByReference => _readOnlyNativeIndicesByReference;

    /// <summary>현재 모델의 양방향 Face 대응을 비어 있는 읽기 전용 맵으로 초기화합니다.</summary>
    public FaceReferenceIndex()
    {
        UpdateReadOnlyViews();
    }

    /// <summary>Native 면 번호에 대응하는 영속 Face 참조를 찾습니다.</summary>
    public bool TryGetReferenceId(int nativeFaceIndex, out FaceRefId faceReferenceId) =>
        _referencesByNativeIndex.TryGetValue(nativeFaceIndex, out faceReferenceId);

    /// <summary>영속 Face 참조에 대응하는 현재 Native 면 번호를 찾습니다.</summary>
    public bool TryGetNativeIndex(FaceRefId faceReferenceId, out int nativeFaceIndex) =>
        _nativeIndicesByReference.TryGetValue(faceReferenceId, out nativeFaceIndex);

    /// <summary>검증된 대응 전체를 교체하며, 실패하면 현재 맵을 그대로 보존합니다.</summary>
    public bool TryReplace(
        IReadOnlyDictionary<FaceRefId, int> nativeIndicesByReference,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(nativeIndicesByReference);

        var nextNativeIndicesByReference = new Dictionary<FaceRefId, int>();
        var nextReferencesByNativeIndex = new Dictionary<int, FaceRefId>();
        foreach (KeyValuePair<FaceRefId, int> mapping in nativeIndicesByReference)
        {
            if (mapping.Key.IsEmpty)
            {
                diagnostic = "빈 Face 참조 ID는 Native 면 번호에 연결할 수 없습니다.";
                return false;
            }

            if (mapping.Value < 0)
            {
                diagnostic = "Native 면 번호는 음수일 수 없습니다.";
                return false;
            }

            if (nextReferencesByNativeIndex.ContainsKey(mapping.Value))
            {
                diagnostic = string.Concat("Native 면 번호가 중복되었습니다: ", mapping.Value);
                return false;
            }

            nextNativeIndicesByReference.Add(mapping.Key, mapping.Value);
            nextReferencesByNativeIndex.Add(mapping.Value, mapping.Key);
        }

        _nativeIndicesByReference = nextNativeIndicesByReference;
        _referencesByNativeIndex = nextReferencesByNativeIndex;
        UpdateReadOnlyViews();
        diagnostic = null;
        return true;
    }

    /// <summary>현재 모델에 종속된 양방향 Face 대응을 모두 지웁니다.</summary>
    public void Clear()
    {
        _nativeIndicesByReference = new Dictionary<FaceRefId, int>();
        _referencesByNativeIndex = new Dictionary<int, FaceRefId>();
        UpdateReadOnlyViews();
    }

    private void UpdateReadOnlyViews()
    {
        _readOnlyReferencesByNativeIndex = new ReadOnlyDictionary<int, FaceRefId>(_referencesByNativeIndex);
        _readOnlyNativeIndicesByReference = new ReadOnlyDictionary<FaceRefId, int>(_nativeIndicesByReference);
    }
}
