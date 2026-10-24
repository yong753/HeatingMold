using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HeatingMold.Core.Contracts.Geometry;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Core.Regions;

namespace HeatingMold.App.ViewModels;

/// <summary>발열 영역의 안정 Face 참조와 선택 topology 표시 상태를 관리합니다.</summary>
internal sealed class RegionSelectionViewModel
{
    private readonly List<FaceRefId> _selectedFaceReferences = new();
    private readonly HashSet<FaceRefId> _selectedFaceReferenceSet = new();
    private readonly ReadOnlyCollection<FaceRefId> _readOnlySelectedFaceReferences;
    private readonly List<BoundaryLoop> _boundaryLoops = new();
    private readonly ReadOnlyCollection<BoundaryLoop> _readOnlyBoundaryLoops;

    /// <summary>Native Face 번호와 분리된 현재 선택 Face 참조를 선택 순서대로 제공합니다.</summary>
    public IReadOnlyList<FaceRefId> SelectedFaceReferences => _readOnlySelectedFaceReferences;

    /// <summary>현재 선택한 Face 수를 제공합니다.</summary>
    public int Count => _selectedFaceReferences.Count;

    /// <summary>현재 선택에서 질의한 외곽·홀 경계를 읽기 전용으로 제공합니다.</summary>
    public IReadOnlyList<BoundaryLoop> BoundaryLoops => _readOnlyBoundaryLoops;

    /// <summary>현재 Face 선택으로 질의한 연결 성분과 경계 topology입니다.</summary>
    public GeometryQueryResult<SelectedTopology> TopologyResult { get; private set; }

    /// <summary>선택 topology와 작업 좌표를 해석할 때 쓰는 투영 좌표계입니다.</summary>
    public ProjectionFrame ProjectionFrame { get; set; }

    /// <summary>선택 topology를 해석하거나 표시할 수 없을 때 화면에 알릴 진단입니다.</summary>
    public string FailureMessage { get; set; }

    /// <summary>선택 목록의 읽기 전용 보기를 초기화합니다.</summary>
    public RegionSelectionViewModel()
    {
        _readOnlySelectedFaceReferences = _selectedFaceReferences.AsReadOnly();
        _readOnlyBoundaryLoops = _boundaryLoops.AsReadOnly();
    }

    /// <summary>안정 참조 ID가 현재 선택에 포함되어 있는지 확인합니다.</summary>
    public bool Contains(FaceRefId faceReferenceId) =>
        _selectedFaceReferenceSet.Contains(faceReferenceId);

    /// <summary>입력한 안정 참조 목록으로 현재 선택을 교체하고 중복 ID를 하나로 합칩니다.</summary>
    public void ReplaceSelection(IEnumerable<FaceRefId> faceReferenceIds)
    {
        ArgumentNullException.ThrowIfNull(faceReferenceIds);
        FaceRefId[] nextSelection = faceReferenceIds.ToArray();
        foreach (FaceRefId faceReferenceId in nextSelection)
            ValidateFaceReferenceId(faceReferenceId);

        Clear();
        foreach (FaceRefId faceReferenceId in nextSelection)
            Add(faceReferenceId);
    }

    /// <summary>안정 참조 ID를 선택에 추가하고 이미 있으면 변경하지 않습니다.</summary>
    public bool Add(FaceRefId faceReferenceId)
    {
        ValidateFaceReferenceId(faceReferenceId);
        if (!_selectedFaceReferenceSet.Add(faceReferenceId))
            return false;

        _selectedFaceReferences.Add(faceReferenceId);
        return true;
    }

    /// <summary>선택된 안정 참조 ID를 제거하고 제거 여부를 반환합니다.</summary>
    public bool Remove(FaceRefId faceReferenceId)
    {
        if (!_selectedFaceReferenceSet.Remove(faceReferenceId))
            return false;

        _selectedFaceReferences.Remove(faceReferenceId);
        return true;
    }

    /// <summary>안정 참조 ID의 선택 여부를 반전하고 변경 후 선택 상태를 반환합니다.</summary>
    public bool Toggle(FaceRefId faceReferenceId)
    {
        ValidateFaceReferenceId(faceReferenceId);
        if (Remove(faceReferenceId))
            return false;

        Add(faceReferenceId);
        return true;
    }

    /// <summary>성공한 topology 질의와 표시 경계·작업 투영 frame을 함께 저장합니다.</summary>
    public void SetTopology(
        GeometryQueryResult<SelectedTopology> topologyResult,
        ProjectionFrame projectionFrame)
    {
        ArgumentNullException.ThrowIfNull(topologyResult);
        ArgumentNullException.ThrowIfNull(projectionFrame);
        if (!topologyResult.HasValue || topologyResult.Value is null)
            throw new ArgumentException("성공한 선택 topology가 필요합니다.", nameof(topologyResult));

        ClearTopology();
        TopologyResult = topologyResult;
        ProjectionFrame = projectionFrame;
        foreach (BoundaryLoop loop in topologyResult.Value.Components.SelectMany(component => component.BoundaryLoops))
            _boundaryLoops.Add(loop);
    }

    /// <summary>현재 Face 선택은 두고 topology·경계·투영 frame·진단만 초기화합니다.</summary>
    public void ClearTopology()
    {
        _boundaryLoops.Clear();
        TopologyResult = null;
        ProjectionFrame = null;
        FailureMessage = null;
    }

    /// <summary>현재 선택 Face와 그 선택에서 파생된 topology 상태를 모두 지웁니다.</summary>
    public void Clear()
    {
        _selectedFaceReferences.Clear();
        _selectedFaceReferenceSet.Clear();
        ClearTopology();
    }

    private static void ValidateFaceReferenceId(FaceRefId faceReferenceId)
    {
        if (faceReferenceId.IsEmpty)
            throw new ArgumentException("빈 Face 참조 ID는 선택할 수 없습니다.", nameof(faceReferenceId));
    }
}
