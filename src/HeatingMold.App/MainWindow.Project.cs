using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using HeatingMold.App.Persistence;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;
using Microsoft.Win32;

namespace HeatingMold.App
{
    public partial class MainWindow
    {
        private string _projectPath = "";
        private string _savedProjectFingerprint;
        private string _modelContentHash;

        private void SaveProject_Click(object sender, RoutedEventArgs e) => SaveProject();

        private void SaveProjectAs_Click(object sender, RoutedEventArgs e) => SaveProjectAs();

        /// <summary>현재 프로젝트 경로로 저장한다. 경로가 없으면 다른 이름으로 저장을 연다.</summary>
        private bool SaveProject()
        {
            if (_isBusy)
            {
                StatusText.Text = "다른 작업이 진행 중입니다 — 완료 후 저장하세요";
                return false;
            }
            if (string.IsNullOrEmpty(_projectPath)) return SaveProjectAs();
            return SaveProjectTo(_projectPath);
        }

        private bool SaveProjectAs()
        {
            if (_isBusy)
            {
                StatusText.Text = "다른 작업이 진행 중입니다 — 완료 후 저장하세요";
                return false;
            }

            var dialog = new SaveFileDialog
            {
                Filter = "HeatingMold 프로젝트 (*.json)|*.json|모든 파일 (*.*)|*.*",
                DefaultExt = ProjectJsonStore.FileExtension,
                AddExtension = true,
                OverwritePrompt = true,
                FileName = string.IsNullOrEmpty(_modelFileName) ? "project.json" : _modelFileName + ".json"
            };
            if (dialog.ShowDialog(this) != true) return false;
            return SaveProjectTo(dialog.FileName);
        }

        /// <summary>
        /// 저장 시점의 문서를 만들고 같은 폴더의 임시 파일을 거쳐 원자적으로 교체한다.
        /// 실패하면 현재 작업과 기존 JSON을 모두 보존한다.
        /// </summary>
        private bool SaveProjectTo(string path)
        {
            if (string.IsNullOrEmpty(_modelPath) || _areas.Count == 0 && _workingSide.Confirmed == null)
            {
                StatusText.Text = "저장할 작업이 없습니다 — STEP 모델을 먼저 여세요";
                return false;
            }

            bool sourceMissing = !File.Exists(_modelPath);
            if (sourceMissing && string.IsNullOrEmpty(_modelContentHash))
            {
                MessageBoxResult answer = MessageBox.Show(this,
                    "원본 STEP 파일을 찾을 수 없습니다." + Environment.NewLine
                    + "지금 저장하면 나중에 같은 모델을 다시 열 때 STEP 파일을 다시 선택해야 합니다."
                    + Environment.NewLine + "그래도 저장할까요?",
                    "원본 STEP 없음", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes) return false;
            }

            try
            {
                ProjectJsonStore.EnsureNotSourceFile(path, _modelPath);
                ProjectDocument document = BuildProjectDocument(path);
                document.CreatedUtc = DateTime.UtcNow.ToString("O");
                document.SavedUtc = document.CreatedUtc;

                ProjectSaveResult result = ProjectJsonStore.Save(document, path);
                _projectPath = result.FullPath;
                _savedProjectFingerprint = ProjectJsonStore.ComputeFingerprint(document);
                _modelContentHash = document.Source?.Sha256 ?? _modelContentHash;
                UpdateProjectActionStates();
                StatusText.Text = "프로젝트 저장 완료 · " + Path.GetFileName(result.FullPath);
                return true;
            }
            catch (Exception ex)
            {
                StatusText.Text = "프로젝트 저장 실패 — 기존 작업과 파일은 유지됩니다";
                ErrorReporter.ShowError("프로젝트 저장 실패", ex);
                return false;
            }
        }

        /// <summary>저장된 기준선과 현재 문서 내용이 다른지 비교한다.</summary>
        private bool HasUnsavedProjectChanges()
        {
            if (string.IsNullOrEmpty(_modelPath) && _areas.Count == 0) return false;
            if (_areas.Count == 0 && string.IsNullOrEmpty(_projectPath)) return false;

            try
            {
                ProjectDocument document = BuildProjectDocument(_projectPath);
                string fingerprint = ProjectJsonStore.ComputeFingerprint(document);
                return !string.Equals(fingerprint, _savedProjectFingerprint, StringComparison.Ordinal);
            }
            catch
            {
                // 문서를 만들 수 없을 정도로 상태가 불완전하면 손실 가능성을 숨기지 않는다.
                return true;
            }
        }

        private void ResetProjectBaselineAfterModelLoad(string path, bool success)
        {
            _projectPath = "";
            _savedProjectFingerprint = null;
            _modelContentHash = success ? ProjectJsonStore.TryComputeFileSha256(path) : null;
            UpdateProjectActionStates();
        }

        private void UpdateProjectActionStates()
        {
            bool hasModel = !string.IsNullOrEmpty(_modelPath);
            if (OpenProjectButton != null) OpenProjectButton.IsEnabled = !_isBusy;
            if (SaveProjectButton != null) SaveProjectButton.IsEnabled = hasModel && !_isBusy;
            if (SaveProjectAsButton != null) SaveProjectAsButton.IsEnabled = hasModel && !_isBusy;
        }

        private ProjectDocument BuildProjectDocument(string projectPath)
        {
            if (string.IsNullOrEmpty(_modelPath))
                throw new InvalidOperationException("원본 STEP 경로가 없습니다.");

            ProjectWorkingSideDocument workingSide = CaptureWorkingSide();
            if (_areas.Count > 0 && workingSide == null)
                throw new InvalidOperationException("확정된 작업평면이 없어 영역이 있는 프로젝트를 저장할 수 없습니다.");

            string fullProjectPath = string.IsNullOrEmpty(projectPath) ? null : Path.GetFullPath(projectPath);
            string projectDirectory = string.IsNullOrEmpty(fullProjectPath)
                ? null
                : Path.GetDirectoryName(fullProjectPath);
            string relativePath = null;
            if (!string.IsNullOrEmpty(projectDirectory))
            {
                relativePath = Path.GetRelativePath(projectDirectory, _modelPath);
            }

            string sha256 = _modelContentHash;
            if (string.IsNullOrEmpty(sha256) && File.Exists(_modelPath))
                sha256 = ProjectJsonStore.TryComputeFileSha256(_modelPath);

            List<ProjectFaceTopologySignature> faceSignatures =
                CaptureFaceTopologySignatures(out string topologyIdentityError);

            var document = new ProjectDocument
            {
                AppVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "",
                Source = new ProjectSourceDocument
                {
                    RelativePath = relativePath,
                    OriginalPathHint = _modelPath,
                    FileName = Path.GetFileName(_modelPath),
                    FileLength = File.Exists(_modelPath) ? new FileInfo(_modelPath).Length : 0,
                    Sha256 = sha256,
                    ModelRevision = _modelRevision,
                    ImportPolicyVersion = 2,
                    FaceTopologySignatures = faceSignatures,
                    TopologyIdentityError = topologyIdentityError
                },
                WorkingSide = workingSide,
                Electrical = CaptureElectricalInputs(),
                Review = new ProjectReviewDocument { CurrentAreaOnly = _reviewCurrentAreaOnly },
                ActiveAreaId = _activeArea?.AreaId ?? -1
            };

            foreach (WorkingArea area in _areas.OrderBy(a => a.AreaId))
                document.Areas.Add(CaptureArea(area));

            if (string.IsNullOrEmpty(document.Source.TopologyIdentityError) &&
                document.Areas.SelectMany(a => a.ExitEdgeRefs ?? new List<ProjectExitEdgeReference>())
                    .Any(r => string.IsNullOrWhiteSpace(r.EdgeSignature) ||
                              string.Equals(r.EdgeSignature, "error", StringComparison.Ordinal)))
            {
                document.Source.TopologyIdentityError =
                    "원본 Edge topology signature가 없어 인출 경계를 재결속할 수 없습니다.";
            }

            return document;
        }

        /// <summary>
        /// 모델 전체 Face의 재가져오기 안정 signature를 읽는다. 저장 자체를 막지는 않지만,
        /// 하나라도 비어 있거나 중복이면 불러오기 때 안전하게 재결속할 수 없으므로 진단을 남긴다.
        /// </summary>
        private List<ProjectFaceTopologySignature> CaptureFaceTopologySignatures(out string error)
        {
            error = null;
            var result = new List<ProjectFaceTopologySignature>();
            try
            {
                string[] signatures = Bridge.GetFaceTopologySignatures();
                if (signatures == null || signatures.Length == 0)
                {
                    error = "모델 Face topology signature를 읽지 못했습니다.";
                    return result;
                }

                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < signatures.Length; i++)
                {
                    string signature = signatures[i];
                    if (string.IsNullOrWhiteSpace(signature) ||
                        string.Equals(signature, "error", StringComparison.Ordinal) ||
                        !seen.Add(signature))
                    {
                        error = "Face topology signature가 비어 있거나 중복되어 재결속할 수 없습니다.";
                        result.Clear();
                        return result;
                    }
                    result.Add(new ProjectFaceTopologySignature { FaceId = i, Signature = signature });
                }
            }
            catch (Exception ex)
            {
                error = "Face topology signature를 읽지 못했습니다: " + ex.Message;
                result.Clear();
            }
            return result;
        }

        private ProjectWorkingSideDocument CaptureWorkingSide()
        {
            WorkingSideDefinition side = _workingSide.Confirmed;
            if (side == null || side.Id.Value <= 0 || side.Revision.Value != Bridge.GetModelRevision())
                return null;
            return new ProjectWorkingSideDocument
            {
                Frame = ToProjectFrame(side.Frame),
                ModelRevision = _modelRevision
            };
        }

        private ProjectAreaDocument CaptureArea(WorkingArea area)
        {
            var document = new ProjectAreaDocument
            {
                AreaId = area.AreaId,
                Name = area.Name,
                Color = ToProjectColor(area.Color),
                FaceIds = area.FaceIdSet.OrderBy(id => id).ToArray(),
                Region = area.Region.Select(loop => loop.Select(ToProjectVec2).ToList()).ToList(),
                Frame = ToProjectFrame(area.Frame),
                Exclusions = area.Exclusions.Select(rect => new ProjectRect
                {
                    MinX = rect.MinX,
                    MinY = rect.MinY,
                    MaxX = rect.MaxX,
                    MaxY = rect.MaxY
                }).ToList(),
                Direction = area.Direction.ToString(),
                MarginText = area.MarginText,
                SpacingText = area.SpacingText,
                TargetPowerText = area.TargetPowerText,
                ExitFaceId = area.ExitFaceId,
                ExitFaceRevision = area.ExitFaceRevision,
                ExitChain = area.ExitChain.Select(ToProjectVec2).ToList(),
                ExitChain3D = area.ExitChain3D.Select(ToProjectVec3).ToList(),
                ExitChainFaceIds = area.ExitChainFaceIds.ToList(),
                StartAnchor = area.StartAnchor.HasValue ? ToProjectVec2(area.StartAnchor.Value) : null,
                EndAnchor = area.EndAnchor.HasValue ? ToProjectVec2(area.EndAnchor.Value) : null,
                ExitEdgeRefs = area.ExitEdgeRefs.Select(ToProjectExitEdgeReference).ToList(),
                SelectedStartExit = area.SelectedStartExit.HasValue
                    ? ToProjectExitBoundaryPoint(area.SelectedStartExit.Value) : null,
                SelectedEndExit = area.SelectedEndExit.HasValue
                    ? ToProjectExitBoundaryPoint(area.SelectedEndExit.Value) : null,
                SelectedAnchorCandidate = area.SelectedAnchorCandidate == null
                    ? null : ToProjectAnchoredCandidate(area.SelectedAnchorCandidate),
                ExitBoundaryKind = area.ExitBoundary?.GetType().Name,
                RegionExitBoundaryKind = area.RegionExitBoundary?.GetType().Name,
                RegionCadBoundaryKind = area.RegionCadBoundary?.GetType().Name,
                SplitCutLines = area.SplitCutLines.Select(cut => new ProjectSplitCutLine
                {
                    AX = cut.A.X,
                    AY = cut.A.Y,
                    BX = cut.B.X,
                    BY = cut.B.Y
                }).ToList(),
                Circuits = area.Circuits.Select(ToProjectCircuit).ToList(),
                CircuitPieceIndices = area.CircuitPieceIndices.Select(indices => indices.ToArray()).ToList(),
                Pattern = area.Pattern == null ? null : ToProjectPattern(area.Pattern),
                ProjectedPieces = area.ProjectedPieces.Select(ToProjectRoutePieceResult).ToList(),
                NativePieceIds = area.NativePieceIds?.ToArray() ?? Array.Empty<int>(),
                RoutePieceLengths = area.RoutePieceLengths?.ToArray() ?? Array.Empty<double>(),
                Paths = area.Paths.Select(path => path.Select(ToProjectVec2).ToList()).ToList(),
                ExclusionPieces = area.ExclusionPieces?.Select(
                    pieces => pieces.Select(ToProjectRoutePiece).ToList()).ToList(),
                PatternDisplayed = area.PatternDisplayed,
                RouteEdited = area.RouteEdited,
                AppliedInputs = area.AppliedInputs == null ? null : new ProjectAppliedInputs
                {
                    Margin = area.AppliedInputs.Margin,
                    Spacing = area.AppliedInputs.Spacing,
                    Direction = area.AppliedInputs.Direction.ToString(),
                    RouteContextKey = area.AppliedInputs.RouteContextKey
                },
                CommittedMapping = area.CommittedMapping == null
                    ? null : ToProjectCommittedContext(area.CommittedMapping)
            };

            document.CommittedChains = CaptureCommittedChains(area, out bool available, out string error);
            document.ChainsAvailable = available;
            document.ChainError = error;
            return document;
        }

        /// <summary>
        /// 커밋된 네이티브 3D 체인을 읽는다. 읽기 실패는 저장 자체를 막지 않는다.
        /// 2D 패턴/초안/입력 조건은 그대로 저장되어 나중에 상태를 진단할 수 있다.
        /// </summary>
        private List<ProjectCommittedChain> CaptureCommittedChains(
            WorkingArea area, out bool available, out string error)
        {
            available = false;
            error = null;
            var result = new List<ProjectCommittedChain>();
            if (area?.NativePieceIds == null || area.NativePieceIds.Length == 0)
            {
                available = true;
                return result;
            }

            WorkingSideDefinition side = _workingSide.Confirmed;
            if (side == null || side.Id.Value <= 0 || side.Revision.Value != Bridge.GetModelRevision())
            {
                error = "유효한 작업평면 컨텍스트가 없어 확정 3D 경로를 읽지 못했습니다.";
                return result;
            }

            try
            {
                CommittedRouteChain[] chains = Bridge.GetCommittedRouteChainsDetailed(
                    side.Id.Value, side.Revision.Value, area.NativePieceIds);
                if (chains == null || chains.Length != area.NativePieceIds.Length)
                {
                    error = "확정 3D 경로 조각 수가 네이티브 ID 수와 일치하지 않습니다.";
                    return result;
                }

                for (int i = 0; i < chains.Length; i++)
                {
                    CommittedRouteChain chain = chains[i];
                    if (chain == null || chain.Points == null ||
                        chain.Points.Length < 6 || chain.Points.Length % 3 != 0)
                    {
                        error = "확정 3D 경로 조각 " + (i + 1) + "의 좌표가 올바르지 않습니다.";
                        result.Clear();
                        return result;
                    }

                    int pointCount = chain.Points.Length / 3;
                    int[] supportFaces = chain.SupportFaceIds ?? Array.Empty<int>();
                    if (supportFaces.Length != 0 && supportFaces.Length != pointCount)
                    {
                        error = "확정 3D 경로 조각 " + (i + 1) + "의 지지 Face 수가 좌표 점 수와 일치하지 않습니다.";
                        result.Clear();
                        return result;
                    }
                    for (int f = 0; f < supportFaces.Length; f++)
                    {
                        if (supportFaces[f] < 0)
                        {
                            error = "확정 3D 경로 조각 " + (i + 1) + "의 지지 Face ID가 올바르지 않습니다.";
                            result.Clear();
                            return result;
                        }
                    }

                    var points = new List<ProjectVec3>(pointCount);
                    for (int k = 0; k + 2 < chain.Points.Length; k += 3)
                    {
                        points.Add(new ProjectVec3
                        {
                            X = chain.Points[k],
                            Y = chain.Points[k + 1],
                            Z = chain.Points[k + 2]
                        });
                    }
                    result.Add(new ProjectCommittedChain
                    {
                        NativePieceId = area.NativePieceIds[i],
                        Kind = area.Pattern?.RoutePieces != null && i < area.Pattern.RoutePieces.Count
                            ? area.Pattern.RoutePieces[i].Kind.ToString()
                            : null,
                        Points = points,
                        SupportFaceIds = (int[])supportFaces.Clone()
                    });
                }

                available = true;
                return result;
            }
            catch (Exception ex)
            {
                error = "확정 3D 경로를 읽지 못했습니다: " + ex.Message;
                return result;
            }
        }

        private ProjectElectricalDocument CaptureElectricalInputs()
        {
            return new ProjectElectricalDocument
            {
                WirePresetIndex = WirePresetBox?.SelectedIndex ?? -1,
                WireDiameterText = WireDiameterBox?.Text ?? "",
                WireResistanceText = WireResistanceBox?.Text ?? "",
                AllowedVoltageText = AllowedVoltageBox?.Text ?? "",
                // 스키마 2 이하 호환용. 스키마 3에서는 허용 전압과 같은 값을 유지한다.
                AppliedVoltageText = AllowedVoltageBox?.Text ?? "",
                AllowedCurrentText = AllowedCurrentBox?.Text ?? "",
                AllowedPowerText = AllowedPowerBox?.Text ?? ""
            };
        }

        private static ProjectPatternDocument ToProjectPattern(SerpentineResult pattern)
        {
            return new ProjectPatternDocument
            {
                Path = pattern.Path == null ? new List<ProjectVec2>() : pattern.Path.Select(ToProjectVec2).ToList(),
                Segments = pattern.Segments == null ? new List<ProjectSerpentineSegment>() :
                    pattern.Segments.Select(segment => new ProjectSerpentineSegment
                    {
                        LaneIndex = segment.LaneIndex,
                        Start = ToProjectVec2(segment.Start),
                        End = ToProjectVec2(segment.End)
                    }).ToList(),
                LaneCount = pattern.LaneCount,
                AngleDeg = pattern.AngleDeg,
                TotalLength = pattern.TotalLength,
                NominalLaneCount = pattern.NominalLaneCount,
                RoutePieces = pattern.RoutePieces == null ? new List<ProjectRoutePiece>() :
                    pattern.RoutePieces.Select(ToProjectRoutePiece).ToList(),
                StartExit = pattern.StartExit == null ? null : ToProjectExitPoint(pattern.StartExit),
                EndExit = pattern.EndExit == null ? null : ToProjectExitPoint(pattern.EndExit),
                CandidateId = pattern.CandidateId
            };
        }

        private static ProjectCircuitDocument ToProjectCircuit(CircuitRoute circuit)
        {
            return new ProjectCircuitDocument
            {
                Pieces = circuit.Pieces == null ? new List<ProjectRoutePiece>() :
                    circuit.Pieces.Select(ToProjectRoutePiece).ToList(),
                StartExit = circuit.StartExit == null ? null : ToProjectExitPoint(circuit.StartExit),
                EndExit = circuit.EndExit == null ? null : ToProjectExitPoint(circuit.EndExit),
                AngleDeg = circuit.AngleDeg,
                LaneCount = circuit.LaneCount,
                NominalLaneCount = circuit.NominalLaneCount,
                CandidateId = circuit.CandidateId
            };
        }

        private static ProjectRoutePiece ToProjectRoutePiece(SerpentineRoutePiece piece)
        {
            return new ProjectRoutePiece
            {
                Kind = piece.Kind.ToString(),
                LaneIndex = piece.LaneIndex,
                Points = piece.Points == null ? new List<ProjectVec2>() :
                    piece.Points.Select(ToProjectVec2).ToList()
            };
        }

        private static ProjectRoutePieceResult ToProjectRoutePieceResult(RoutePieceResult piece)
        {
            return new ProjectRoutePieceResult
            {
                Length = piece.Length,
                Start = piece.Start?.ToArray(),
                End = piece.End?.ToArray()
            };
        }

        private static ProjectExitPoint ToProjectExitPoint(CircuitExitPoint exit)
        {
            return new ProjectExitPoint
            {
                Role = exit.Role.ToString(),
                Point = ToProjectVec2(exit.Point),
                BoundarySegmentIndex = exit.BoundarySegmentIndex,
                BoundaryParameter = exit.BoundaryParameter,
                Binding = exit.Binding.HasValue ? ToProjectExitEdgeBinding(exit.Binding.Value) : null,
                ArtificialBinding = exit.ArtificialBinding.HasValue
                    ? ToProjectArtificialEdgeBinding(exit.ArtificialBinding.Value) : null,
                SplitBinding = exit.SplitBinding.HasValue
                    ? ToProjectSplitLineBinding(exit.SplitBinding.Value) : null
            };
        }

        private static ProjectExitBoundaryPoint ToProjectExitBoundaryPoint(ExitBoundaryPoint exit)
        {
            return new ProjectExitBoundaryPoint
            {
                Point = ToProjectVec2(exit.Point),
                SegmentIndex = exit.SegmentIndex,
                SegmentParameter = exit.SegmentParameter,
                Binding = exit.Binding.HasValue ? ToProjectExitEdgeBinding(exit.Binding.Value) : null,
                ArtificialBinding = exit.ArtificialBinding.HasValue
                    ? ToProjectArtificialEdgeBinding(exit.ArtificialBinding.Value) : null,
                SplitBinding = exit.SplitBinding.HasValue
                    ? ToProjectSplitLineBinding(exit.SplitBinding.Value) : null
            };
        }

        private static ProjectSplitLineBinding ToProjectSplitLineBinding(SplitLineBinding binding)
        {
            return new ProjectSplitLineBinding
            {
                WorkingContextId = binding.WorkingContextId,
                WorkingContextRevision = binding.WorkingContextRevision,
                AX = binding.AX,
                AY = binding.AY,
                BX = binding.BX,
                BY = binding.BY,
                FirstParameter = binding.FirstParameter,
                LastParameter = binding.LastParameter,
                Parameter = binding.Parameter
            };
        }

        private static ProjectExitEdgeBinding ToProjectExitEdgeBinding(ExitEdgeBinding binding)
        {
            return new ProjectExitEdgeBinding
            {
                ModelRevision = binding.ModelRevision,
                WorkingContextId = binding.WorkingContextId,
                WorkingContextRevision = binding.WorkingContextRevision,
                EdgeKey = binding.EdgeKey,
                FirstParameter = binding.FirstParameter,
                LastParameter = binding.LastParameter,
                Parameter = binding.Parameter,
                Reversed = binding.Reversed
            };
        }

        private static ProjectArtificialEdgeBinding ToProjectArtificialEdgeBinding(ArtificialEdgeBinding binding)
        {
            return new ProjectArtificialEdgeBinding
            {
                WorkingContextId = binding.WorkingContextId,
                WorkingContextRevision = binding.WorkingContextRevision,
                MinX = binding.MinX,
                MinY = binding.MinY,
                MaxX = binding.MaxX,
                MaxY = binding.MaxY,
                Side = binding.Side,
                FirstParameter = binding.FirstParameter,
                LastParameter = binding.LastParameter,
                Parameter = binding.Parameter
            };
        }

        private static ProjectExitEdgeReference ToProjectExitEdgeReference(ExitEdgeReference reference)
        {
            return new ProjectExitEdgeReference
            {
                Revision = reference.Revision,
                EdgeKey = reference.EdgeKey,
                EdgeSignature = reference.EdgeSignature,
                FaceId = reference.FaceId,
                FirstParameter = reference.FirstParameter,
                LastParameter = reference.LastParameter,
                Reversed = reference.Reversed,
                StartIndex = reference.StartIndex,
                EndIndex = reference.EndIndex
            };
        }

        private static ProjectAnchoredCandidate ToProjectAnchoredCandidate(AnchoredCandidate candidate)
        {
            return new ProjectAnchoredCandidate
            {
                Start = ToProjectVec2(candidate.Start),
                End = ToProjectVec2(candidate.End),
                AngleDeg = candidate.AngleDeg,
                CandidateId = candidate.CandidateId,
                StartBinding = candidate.StartBinding.HasValue
                    ? ToProjectExitEdgeBinding(candidate.StartBinding.Value) : null,
                EndBinding = candidate.EndBinding.HasValue
                    ? ToProjectExitEdgeBinding(candidate.EndBinding.Value) : null,
                StartLeftToRight = candidate.StartLeftToRight
            };
        }

        private static ProjectCommittedContextDocument ToProjectCommittedContext(CommittedRouteContext context)
        {
            return new ProjectCommittedContextDocument
            {
                FaceIds = context.FaceIds?.ToArray() ?? Array.Empty<int>(),
                ExitFaceId = context.ExitFaceId,
                ExitChain = context.ExitChain == null
                    ? new List<ProjectVec2>() : context.ExitChain.Select(ToProjectVec2).ToList(),
                ExitChain3D = context.ExitChain3D == null
                    ? new List<ProjectVec3>() : context.ExitChain3D.Select(ToProjectVec3).ToList(),
                ExitChainFaceIds = context.ExitChainFaceIds?.ToList() ?? new List<int>(),
                StartAnchor = context.StartAnchor.HasValue ? ToProjectVec2(context.StartAnchor.Value) : null,
                EndAnchor = context.EndAnchor.HasValue ? ToProjectVec2(context.EndAnchor.Value) : null,
                ExitEdgeRefs = context.ExitEdgeRefs == null
                    ? new List<ProjectExitEdgeReference>()
                    : context.ExitEdgeRefs.Select(ToProjectExitEdgeReference).ToList(),
                SelectedStartExit = context.SelectedStartExit.HasValue
                    ? ToProjectExitBoundaryPoint(context.SelectedStartExit.Value) : null,
                SelectedEndExit = context.SelectedEndExit.HasValue
                    ? ToProjectExitBoundaryPoint(context.SelectedEndExit.Value) : null,
                SelectedAnchorCandidate = context.SelectedAnchorCandidate == null
                    ? null : ToProjectAnchoredCandidate(context.SelectedAnchorCandidate),
                StartExits = context.StartExits == null
                    ? new List<ProjectExitPoint>() : context.StartExits.Select(ToProjectExitPoint).ToList(),
                EndExits = context.EndExits == null
                    ? new List<ProjectExitPoint>() : context.EndExits.Select(ToProjectExitPoint).ToList(),
                CircuitStarts = context.CircuitStarts == null
                    ? new List<ProjectVec2>() : context.CircuitStarts.Select(ToProjectVec2).ToList(),
                CircuitEnds = context.CircuitEnds == null
                    ? new List<ProjectVec2>() : context.CircuitEnds.Select(ToProjectVec2).ToList(),
                BoundaryKind = context.Boundary?.GetType().Name,
                RegionBoundaryKind = context.RegionBoundary?.GetType().Name,
                // 확정 경로가 실제로 사용한 경계를 저장한다. 다음 로드에서 현재 초안
                // 선택점 상태로 모드를 다시 추론하면 save-load-save-load에서 뒤집힌다.
                RegionMode = context.Boundary != null &&
                    ReferenceEquals(context.Boundary, context.RegionBoundary)
            };
        }

        private static ProjectFrame ToProjectFrame(ProjectionFrame frame)
        {
            if (frame == null) return null;
            return new ProjectFrame
            {
                Origin = ToProjectVec3(frame.Origin),
                AxisX = ToProjectVec3(frame.AxisX),
                AxisY = ToProjectVec3(frame.AxisY),
                Normal = ToProjectVec3(frame.Normal)
            };
        }

        private static ProjectVec2 ToProjectVec2(Vec2 point) => new() { X = point.X, Y = point.Y };

        private static ProjectVec3 ToProjectVec3(Vec3 point) => new() { X = point.X, Y = point.Y, Z = point.Z };

        private static ProjectColor ToProjectColor(System.Windows.Media.Color color) => new()
        {
            R = color.R,
            G = color.G,
            B = color.B,
            A = color.A
        };
    }
}
