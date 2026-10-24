using System;
using System.Collections.Generic;
using HeatingMold.App.Geometry.Occt;
using HeatingMold.Core.Primitives;

namespace HeatingMold.App.Tests;

/// <summary>Native 면 번호와 안정 Face ID 변환 경계를 검사합니다.</summary>
public sealed class FaceReferenceIndexTests
{
    /// <summary>성공적으로 교체한 맵을 양방향으로 조회하고 읽기 전용으로 노출합니다.</summary>
    [Fact]
    public void TryReplace_StoresBothDirectionsWithoutExposingMutableMaps()
    {
        FaceRefId firstReference = FaceRefId.New();
        FaceRefId secondReference = FaceRefId.New();
        var index = new FaceReferenceIndex();

        bool replaced = index.TryReplace(
            new Dictionary<FaceRefId, int>
            {
                [firstReference] = 4,
                [secondReference] = 9
            },
            out string diagnostic);

        Assert.True(replaced);
        Assert.Null(diagnostic);
        Assert.Equal(firstReference, index.ReferencesByNativeIndex[4]);
        Assert.Equal(secondReference, index.ReferencesByNativeIndex[9]);
        Assert.Equal(4, index.NativeIndicesByReference[firstReference]);
        Assert.Equal(9, index.NativeIndicesByReference[secondReference]);
        Assert.True(index.TryGetReferenceId(4, out FaceRefId resolvedReference));
        Assert.Equal(firstReference, resolvedReference);
        Assert.True(index.TryGetNativeIndex(secondReference, out int resolvedNativeIndex));
        Assert.Equal(9, resolvedNativeIndex);

        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<int, FaceRefId>)index.ReferencesByNativeIndex).Add(12, FaceRefId.New()));
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<FaceRefId, int>)index.NativeIndicesByReference).Add(FaceRefId.New(), 12));
    }

    /// <summary>유효하지 않은 새 맵은 거부하고 기존 모델의 맵을 보존합니다.</summary>
    [Fact]
    public void TryReplace_RejectsDuplicateNativeIndexAndPreservesExistingMap()
    {
        FaceRefId existingReference = FaceRefId.New();
        var index = new FaceReferenceIndex();
        Assert.True(index.TryReplace(
            new Dictionary<FaceRefId, int> { [existingReference] = 2 },
            out _));

        bool replaced = index.TryReplace(
            new Dictionary<FaceRefId, int>
            {
                [FaceRefId.New()] = 5,
                [FaceRefId.New()] = 5
            },
            out string diagnostic);

        Assert.False(replaced);
        Assert.Contains("중복", diagnostic);
        Assert.Single(index.NativeIndicesByReference);
        Assert.Equal(2, index.NativeIndicesByReference[existingReference]);
        Assert.True(index.TryGetNativeIndex(existingReference, out _));
    }

    /// <summary>빈 Face ID와 음수 Native 면 번호를 대응에 넣지 않습니다.</summary>
    [Fact]
    public void TryReplace_RejectsEmptyReferenceAndNegativeNativeIndex()
    {
        var index = new FaceReferenceIndex();

        Assert.False(index.TryReplace(
            new Dictionary<FaceRefId, int> { [default] = 0 },
            out string emptyIdDiagnostic));
        Assert.Contains("빈 Face 참조", emptyIdDiagnostic);

        Assert.False(index.TryReplace(
            new Dictionary<FaceRefId, int> { [FaceRefId.New()] = -1 },
            out string negativeIndexDiagnostic));
        Assert.Contains("음수", negativeIndexDiagnostic);
        Assert.Empty(index.NativeIndicesByReference);
        Assert.Empty(index.ReferencesByNativeIndex);
    }

    /// <summary>모델을 바꾸기 전에 저장한 Native 번호 대응을 모두 비웁니다.</summary>
    [Fact]
    public void Clear_RemovesBothDirections()
    {
        var index = new FaceReferenceIndex();
        FaceRefId reference = FaceRefId.New();
        Assert.True(index.TryReplace(
            new Dictionary<FaceRefId, int> { [reference] = 3 },
            out _));

        index.Clear();

        Assert.Empty(index.NativeIndicesByReference);
        Assert.Empty(index.ReferencesByNativeIndex);
        Assert.False(index.TryGetReferenceId(3, out _));
        Assert.False(index.TryGetNativeIndex(reference, out _));
    }
}
