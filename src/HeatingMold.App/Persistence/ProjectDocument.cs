using System;
using System.Collections.Generic;

namespace HeatingMold.App.Persistence
{
    /// <summary>
    /// 프로젝트 JSON의 최상위 문서. Phase A는 저장 전용이며, 이 스키마는
    /// 이후 불러오기 단계에서도 그대로 사용한다. 네이티브 포인터, 임시 stage ID,
    /// AIS 객체, 조회 캐시는 이 문서에 포함하지 않는다.
    /// </summary>
    public sealed class ProjectDocument
    {
        public string Format { get; set; } = "HeatingMold.Project";
        public int SchemaVersion { get; set; } = 1;
        public string AppVersion { get; set; }
        public string CreatedUtc { get; set; }
        public string SavedUtc { get; set; }

        public ProjectSourceDocument Source { get; set; }
        public ProjectWorkingSideDocument WorkingSide { get; set; }
        public List<ProjectAreaDocument> Areas { get; set; } = new();
        public int ActiveAreaId { get; set; } = -1;
        public ProjectElectricalDocument Electrical { get; set; }
        public ProjectReviewDocument Review { get; set; }

        /// <summary>저장 위치/시각처럼 작업 내용이 아닌 값을 제외한 비교용 복사본.</summary>
        public ProjectDocument CloneForFingerprint()
        {
            var copy = (ProjectDocument)MemberwiseClone();
            copy.CreatedUtc = null;
            copy.SavedUtc = null;
            if (copy.Source != null) copy.Source = copy.Source.CloneForFingerprint();
            return copy;
        }
    }

    public sealed class ProjectSourceDocument
    {
        /// <summary>JSON 파일 위치를 기준으로 한 상대 경로. 불러오기 때 이 경로를 우선 사용한다.</summary>
        public string RelativePath { get; set; }
        /// <summary>저장 시 원본 STEP의 절대 경로(진단/재선택 안내용).</summary>
        public string OriginalPathHint { get; set; }
        public string FileName { get; set; }
        public long FileLength { get; set; }
        /// <summary>가져온 STEP 바이트의 SHA-256. 파일이 바뀌면 불러오기를 거부하기 위한 값이다.</summary>
        public string Sha256 { get; set; }
        public long ModelRevision { get; set; }
        public int ImportPolicyVersion { get; set; } = 1;
        /// <summary>모델 전체 Face의 재가져오기 안정 topology signature. FaceId는 현재 런타임 인덱스다.</summary>
        public List<ProjectFaceTopologySignature> FaceTopologySignatures { get; set; } = new();
        /// <summary>signature를 모두 읽지 못한 경우의 진단. 값이 있으면 이 프로젝트는 아직 불러오기 불가다.</summary>
        public string TopologyIdentityError { get; set; }

        public ProjectSourceDocument CloneForFingerprint()
        {
            var copy = (ProjectSourceDocument)MemberwiseClone();
            copy.RelativePath = null;
            copy.OriginalPathHint = null;
            return copy;
        }
    }

    public sealed class ProjectFaceTopologySignature
    {
        public int FaceId { get; set; }
        public string Signature { get; set; }
    }

    public sealed class ProjectWorkingSideDocument
    {
        public ProjectFrame Frame { get; set; }
        public long ModelRevision { get; set; }
    }

    public sealed class ProjectAreaDocument
    {
        public int AreaId { get; set; }
        public string Name { get; set; }
        public ProjectColor Color { get; set; }
        public int[] FaceIds { get; set; } = Array.Empty<int>();
        public List<List<ProjectVec2>> Region { get; set; } = new();
        public ProjectFrame Frame { get; set; }
        public List<ProjectRect> Exclusions { get; set; } = new();

        public string Direction { get; set; }
        public string MarginText { get; set; }
        public string SpacingText { get; set; }

        public int ExitFaceId { get; set; } = -1;
        public long ExitFaceRevision { get; set; }
        public List<ProjectVec2> ExitChain { get; set; } = new();
        public List<ProjectVec3> ExitChain3D { get; set; } = new();
        public List<int> ExitChainFaceIds { get; set; } = new();
        public ProjectVec2 StartAnchor { get; set; }
        public ProjectVec2 EndAnchor { get; set; }
        public List<ProjectExitEdgeReference> ExitEdgeRefs { get; set; } = new();
        public ProjectExitBoundaryPoint SelectedStartExit { get; set; }
        public ProjectExitBoundaryPoint SelectedEndExit { get; set; }
        public ProjectAnchoredCandidate SelectedAnchorCandidate { get; set; }

        /// <summary>런타임 경계 구현의 종류. 객체 자체는 저장하지 않고, 불러오기 때 재구성한다.</summary>
        public string ExitBoundaryKind { get; set; }
        public string RegionExitBoundaryKind { get; set; }
        public string RegionCadBoundaryKind { get; set; }

        public List<ProjectCircuitDocument> Circuits { get; set; } = new();
        public List<int[]> CircuitPieceIndices { get; set; } = new();
        public ProjectPatternDocument Pattern { get; set; }
        public List<ProjectRoutePieceResult> ProjectedPieces { get; set; } = new();
        public int[] NativePieceIds { get; set; } = Array.Empty<int>();
        public double[] RoutePieceLengths { get; set; } = Array.Empty<double>();
        public List<List<ProjectVec2>> Paths { get; set; } = new();
        public List<List<ProjectRoutePiece>> ExclusionPieces { get; set; }

        public bool PatternDisplayed { get; set; }
        public bool RouteEdited { get; set; }
        public ProjectAppliedInputs AppliedInputs { get; set; }
        public ProjectCommittedContextDocument CommittedMapping { get; set; }

        /// <summary>커밋된 네이티브 3D 체인. 읽을 수 없으면 ChainsAvailable=false와 사유를 남긴다.</summary>
        public List<ProjectCommittedChain> CommittedChains { get; set; } = new();
        public bool ChainsAvailable { get; set; }
        public string ChainError { get; set; }
    }

    public sealed class ProjectElectricalDocument
    {
        public int WirePresetIndex { get; set; }
        public string WireDiameterText { get; set; }
        public string WireResistanceText { get; set; }
        public string AppliedVoltageText { get; set; }
    }

    public sealed class ProjectReviewDocument
    {
        public bool CurrentAreaOnly { get; set; }
    }

    public sealed class ProjectAppliedInputs
    {
        public double Margin { get; set; }
        public double Spacing { get; set; }
        public string Direction { get; set; }
        public string RouteContextKey { get; set; }
    }

    public sealed class ProjectPatternDocument
    {
        public List<ProjectVec2> Path { get; set; } = new();
        public List<ProjectSerpentineSegment> Segments { get; set; } = new();
        public int LaneCount { get; set; }
        public double AngleDeg { get; set; }
        public double TotalLength { get; set; }
        public int NominalLaneCount { get; set; }
        public List<ProjectRoutePiece> RoutePieces { get; set; } = new();
        public ProjectExitPoint StartExit { get; set; }
        public ProjectExitPoint EndExit { get; set; }
        public string CandidateId { get; set; }
    }

    public sealed class ProjectCircuitDocument
    {
        public List<ProjectRoutePiece> Pieces { get; set; } = new();
        public ProjectExitPoint StartExit { get; set; }
        public ProjectExitPoint EndExit { get; set; }
        public double AngleDeg { get; set; }
        public int LaneCount { get; set; }
        public int NominalLaneCount { get; set; }
        public string CandidateId { get; set; }
    }

    public sealed class ProjectRoutePiece
    {
        public string Kind { get; set; }
        public int LaneIndex { get; set; }
        public List<ProjectVec2> Points { get; set; } = new();
    }

    public sealed class ProjectSerpentineSegment
    {
        public int LaneIndex { get; set; }
        public ProjectVec2 Start { get; set; }
        public ProjectVec2 End { get; set; }
    }

    public sealed class ProjectRoutePieceResult
    {
        public double Length { get; set; }
        public double[] Start { get; set; }
        public double[] End { get; set; }
    }

    public sealed class ProjectCommittedChain
    {
        public int NativePieceId { get; set; }
        /// <summary>표시 스타일 복원용 경로 조각 종류. 이전 저장 파일에는 없을 수 있다.</summary>
        public string Kind { get; set; }
        public List<ProjectVec3> Points { get; set; } = new();
        public int[] SupportFaceIds { get; set; } = Array.Empty<int>();
    }

    public sealed class ProjectCommittedContextDocument
    {
        public int[] FaceIds { get; set; } = Array.Empty<int>();
        public int ExitFaceId { get; set; } = -1;
        public List<ProjectVec2> ExitChain { get; set; } = new();
        public List<ProjectVec3> ExitChain3D { get; set; } = new();
        public List<int> ExitChainFaceIds { get; set; } = new();
        public ProjectVec2 StartAnchor { get; set; }
        public ProjectVec2 EndAnchor { get; set; }
        public List<ProjectExitEdgeReference> ExitEdgeRefs { get; set; } = new();
        public ProjectExitBoundaryPoint SelectedStartExit { get; set; }
        public ProjectExitBoundaryPoint SelectedEndExit { get; set; }
        public ProjectAnchoredCandidate SelectedAnchorCandidate { get; set; }
        public List<ProjectExitPoint> StartExits { get; set; } = new();
        public List<ProjectExitPoint> EndExits { get; set; } = new();
        public List<ProjectVec2> CircuitStarts { get; set; } = new();
        public List<ProjectVec2> CircuitEnds { get; set; } = new();
        public string BoundaryKind { get; set; }
        public string RegionBoundaryKind { get; set; }
    }

    public sealed class ProjectExitPoint
    {
        public string Role { get; set; }
        public ProjectVec2 Point { get; set; }
        public int BoundarySegmentIndex { get; set; }
        public double BoundaryParameter { get; set; }
        public ProjectExitEdgeBinding Binding { get; set; }
        public ProjectArtificialEdgeBinding ArtificialBinding { get; set; }
    }

    public sealed class ProjectExitBoundaryPoint
    {
        public ProjectVec2 Point { get; set; }
        public int SegmentIndex { get; set; }
        public double SegmentParameter { get; set; }
        public ProjectExitEdgeBinding Binding { get; set; }
        public ProjectArtificialEdgeBinding ArtificialBinding { get; set; }
    }

    public sealed class ProjectExitEdgeBinding
    {
        public long ModelRevision { get; set; }
        public long WorkingContextId { get; set; }
        public long WorkingContextRevision { get; set; }
        public long EdgeKey { get; set; }
        public double FirstParameter { get; set; }
        public double LastParameter { get; set; }
        public double Parameter { get; set; }
        public bool Reversed { get; set; }
    }

    public sealed class ProjectArtificialEdgeBinding
    {
        public long WorkingContextId { get; set; }
        public long WorkingContextRevision { get; set; }
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }
        public int Side { get; set; }
        public double FirstParameter { get; set; }
        public double LastParameter { get; set; }
        public double Parameter { get; set; }
    }

    public sealed class ProjectExitEdgeReference
    {
        public long Revision { get; set; }
        public long EdgeKey { get; set; }
        /// <summary>재가져오기 안정 원본 Edge topology signature. 비어 있으면 불러오기에서 재결속할 수 없다.</summary>
        public string EdgeSignature { get; set; }
        public int FaceId { get; set; }
        public double FirstParameter { get; set; }
        public double LastParameter { get; set; }
        public bool Reversed { get; set; }
        public int StartIndex { get; set; }
        public int EndIndex { get; set; }
    }

    public sealed class ProjectAnchoredCandidate
    {
        public ProjectVec2 Start { get; set; }
        public ProjectVec2 End { get; set; }
        public double AngleDeg { get; set; }
        public string CandidateId { get; set; }
        public ProjectExitEdgeBinding StartBinding { get; set; }
        public ProjectExitEdgeBinding EndBinding { get; set; }
        public bool? StartLeftToRight { get; set; }
    }

    public sealed class ProjectVec2
    {
        public double X { get; set; }
        public double Y { get; set; }
    }

    public sealed class ProjectVec3
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
    }

    public sealed class ProjectFrame
    {
        public ProjectVec3 Origin { get; set; }
        public ProjectVec3 AxisX { get; set; }
        public ProjectVec3 AxisY { get; set; }
        public ProjectVec3 Normal { get; set; }
    }

    public sealed class ProjectRect
    {
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }
    }

    public sealed class ProjectColor
    {
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }
        public byte A { get; set; }
    }
}
