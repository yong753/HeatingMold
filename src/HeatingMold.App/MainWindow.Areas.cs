using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;

namespace HeatingMold.App;

public partial class MainWindow
{
    private void ResetCircuitEditingUi()
    {
        ExitVertexEdit();
        CancelPowerSetup();
        CancelExitFacePick();
        if (Viewport != null)
            Viewport.PowerLocationMode = false;
        if (CircuitLengthGrid != null) CircuitLengthGrid.ItemsSource = null;
    }

    private void RefreshAreaList()
    {
        _suppressAreaSelection = true;
        _syncingAreaSelector = true;
        try
        {
            if (ConfirmedAreasList != null)
            {
                ConfirmedAreasList.ItemsSource = null;
                ConfirmedAreasList.ItemsSource = _areas.ToList();
                ConfirmedAreasList.SelectedItem = _activeArea;
            }
            if (ActiveAreaSelectorBox != null)
            {
                ActiveAreaSelectorBox.ItemsSource = null;
                ActiveAreaSelectorBox.ItemsSource = _areas.ToList();
                ActiveAreaSelectorBox.SelectedItem = _activeArea;
                ActiveAreaSelectorBox.IsEnabled = _areas.Count > 0;
            }
        }
        finally
        {
            _syncingAreaSelector = false;
            _suppressAreaSelection = false;
        }
        RefreshElectricalTable();
    }

    private void ActiveAreaSelector_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingAreaSelector) return;
        if (ActiveAreaSelectorBox?.SelectedItem is not WorkingArea area) return;
        if (ReferenceEquals(area, _activeArea)) return;
        ClearFaceSelectionOnly();
        ActivateArea(area);
        RefreshAreaList();
        StatusText.Text = string.Format("{0} 작업영역 활성 — Face {1}개", area.Name, area.FaceIdSet.Count);
    }

    private void ConfirmedAreasList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAreaSelection) return;
        if (ConfirmedAreasList.SelectedItem is not WorkingArea area) return;
        if (ReferenceEquals(area, _activeArea)) return;
        ClearFaceSelectionOnly();
        ActivateArea(area);
        RefreshAreaList();
        StatusText.Text = string.Format("{0} 작업영역 활성 — Face {1}개", area.Name, area.FaceIdSet.Count);
    }

    private void ActivateArea(WorkingArea area)
    {
        // 영역이 바뀌면 이전 영역의 2D 제외 도구를 새 영역에 이어 쓰지 않는다.
        ExitExclusionSketch();
        if (ReferenceEquals(_activeArea, area)) return;
        ResetCircuitEditingUi();
        _activeArea = area;
        SyncPatternInputs(area);

        if (area == null)
        {
            if (Viewport != null) Viewport.CircuitHoverMode = false;
        }
        else
        {
            if (Viewport != null)
                Viewport.CircuitHoverMode = area.Pattern != null && area.Pattern.RoutePieces.Count > 0;
            if (area.Pattern != null)
                UpdateCircuitTable();
        }

        UpdateActiveAreaHighlight();
        SyncExitFaceUi(area);
        ApplyActiveExitEdgeHighlight();
        UpdateExportScopeText();
        if (area != null && IsPatternInputStale(area))
            StatusText.Text = string.Format("{0} 영역 — 입력 조건이 변경되었습니다 (이전 결과 표시 중)", area.Name);
        UpdateActionStates();
    }

    /// <summary>활성 영역 전환 시 그 영역의 배치 조건 초안을 화면에 복원한다.</summary>
    private void SyncPatternInputs(WorkingArea area)
    {
        if (area == null) return;
        // 두 값을 먼저 캡처해 두고, 복원 중 발생하는 TextChanged가 상대 필드의
        // 아직 화면에 남은 값을 새 영역 초안에 덮어쓰지 않게 한다.
        string marginText = area.MarginText ?? "";
        string spacingText = area.SpacingText ?? "";
        string targetPowerText = area.TargetPowerText ?? "";
        _suppressPatternInputWriteBack = true;
        try
        {
            if (MarginBox != null && MarginBox.Text != marginText) MarginBox.Text = marginText;
            if (SpacingBox != null && SpacingBox.Text != spacingText) SpacingBox.Text = spacingText;
            if (TargetPowerBox != null && TargetPowerBox.Text != targetPowerText)
                TargetPowerBox.Text = targetPowerText;
        }
        finally
        {
            _suppressPatternInputWriteBack = false;
        }
    }

    /// <summary>제외영역 변경이나 재생성 실패 시 복원할 경로 상태를 캡처한다.</summary>
    private static AreaRouteSnapshot CaptureAreaRouteState(WorkingArea area) => new(
        area.Exclusions.ToList(),
        area.Pattern,
        area.Paths,
        area.ExclusionPieces,
        area.NativePieceIds,
        area.RoutePieceLengths,
        area.ProjectedPieces,
        area.PatternDisplayed,
        area.LastExitFailure,
        area.StartAnchor,
        area.EndAnchor,
        area.AnchorCandidates,
        area.ExcludedCandidates,
        area.ExcludedPlan,
        area.ExcludedCandidateFailure,
        area.ExcludedPlanKey,
        area.Circuits,
        area.CircuitPieceIndices,
        area.ExitEdgeRefs,
        area.AppliedInputs,
        area.CommittedMapping,
        area.ExitBoundary,
        area.RegionExitBoundary,
        area.RegionCadBoundary,
        area.SelectedStartExit,
        area.SelectedEndExit,
        area.SelectedAnchorCandidate,
        area.RouteEdited);

    /// <summary>캡처한 경로 상태를 영역에 되돌린다. 제외 목록은 필요할 때만 복원한다.</summary>
    private static void RestoreAreaRouteState(WorkingArea area, AreaRouteSnapshot snapshot, bool restoreExclusions)
    {
        if (restoreExclusions)
        {
            area.Exclusions.Clear();
            area.Exclusions.AddRange(snapshot.Exclusions);
        }
        area.Pattern = snapshot.Pattern;
        area.Paths = snapshot.Paths;
        area.ExclusionPieces = snapshot.ExclusionPieces;
        area.NativePieceIds = snapshot.NativePieceIds;
        area.RoutePieceLengths = snapshot.RoutePieceLengths;
        area.ProjectedPieces = snapshot.ProjectedPieces;
        area.PatternDisplayed = snapshot.PatternDisplayed;
        area.LastExitFailure = snapshot.LastExitFailure;
        area.StartAnchor = snapshot.StartAnchor;
        area.EndAnchor = snapshot.EndAnchor;
        area.AnchorCandidates = snapshot.AnchorCandidates;
        area.ExcludedCandidates = snapshot.ExcludedCandidates;
        area.ExcludedPlan = snapshot.ExcludedPlan;
        area.ExcludedCandidateFailure = snapshot.ExcludedCandidateFailure;
        area.ExcludedPlanKey = snapshot.ExcludedPlanKey;
        area.Circuits = snapshot.Circuits;
        area.CircuitPieceIndices = snapshot.CircuitPieceIndices;
        area.ExitEdgeRefs = snapshot.ExitEdgeRefs;
        area.AppliedInputs = snapshot.AppliedInputs;
        area.CommittedMapping = snapshot.CommittedMapping;
        area.ExitBoundary = snapshot.ExitBoundary;
        area.RegionExitBoundary = snapshot.RegionExitBoundary;
        area.RegionCadBoundary = snapshot.RegionCadBoundary;
        area.SelectedStartExit = snapshot.SelectedStartExit;
        area.SelectedEndExit = snapshot.SelectedEndExit;
        area.SelectedAnchorCandidate = snapshot.SelectedAnchorCandidate;
        area.RouteEdited = snapshot.RouteEdited;
    }

    private void ClearFaceSelectionOnly()
    {
        _selectedFaceIds.Clear();
        if (SelectedFacesList != null) SelectedFacesList.Items.Clear();
        Bridge.ClearFaceSelection();
        Bridge.ClearHover();
        if (HoverText != null) HoverText.Text = "";
    }

    private void DeleteSelectedArea_Click(object sender, RoutedEventArgs e)
    {
        var area = ConfirmedAreasList?.SelectedItem as WorkingArea ?? _activeArea;
        if (area == null)
        {
            StatusText.Text = "삭제할 작업영역이 없습니다";
            return;
        }

        if (!ConfirmDiscardRouteEdits(area, "작업영역 삭제")) return;
        ExitVertexEdit();
        bool wasActive = ReferenceEquals(area, _activeArea);
        WorkingArea previousActive = _activeArea;
        var remaining = _areas.Where(a => !ReferenceEquals(a, area)).ToList();
        WorkingArea nextActive = wasActive ? remaining.FirstOrDefault() : previousActive;

        // 후보 목록으로 네이티브 재구성을 먼저 검증한다. 성공 전에는
        // _areas/활성 영역/하이라이트/표를 바꾸지 않으므로 실패 시 복원 코드가 필요 없다.
        if (!RebuildNativePatternGeometry(remaining, null, out _, out _))
        {
            StatusText.Text = string.Format(
                "{0} 영역을 삭제하지 못했습니다 — 기존 영역과 경로를 유지합니다", area.Name);
            UpdateActionStates();
            return;
        }

        if (area.AreaId > 0) Bridge.ClearAreaHighlight(area.AreaId);
        DetachFromSplitFamily(area);
        _areas.Clear();
        _areas.AddRange(remaining);
        ApplyAreaColors();
        ActivateArea(nextActive);
        RefreshAreaList();
        UpdateCircuitTable();
        ClearFaceSelectionOnly();
        StatusText.Text = string.Format("{0} 작업영역을 삭제했습니다 — 남은 영역 {1}개", area.Name, _areas.Count);
        if (_routePresentationWarning != null)
        {
            // 커밋 이후 표시 갱신 실패는 삭제를 되돌리지 않는다. 완료 문구가 경고를 덮지 않게 남긴다.
            StatusText.Text += " — 표시 갱신 경고: " + _routePresentationWarning;
            _routePresentationWarning = null;
        }
        UpdateActionStates();
    }

    /// <summary>
    /// 확정된 모든 영역의 발열경로를 그 영역의 원색(선택 외곽선과 같은 무광 색)으로 맞춘다.
    /// 표시 전용이며 형상/길이/ID를 바꾸지 않는다. 하나라도 실패하면 아무 색도 바꾸지 않는다.
    /// 경로 트랜잭션은 이 함수를 쓰지 않고 커밋에 참여한 영역 기준으로 준비한 대상을 넘긴다.
    /// </summary>
    private bool RefreshRouteColors()
    {
        WorkingSideDefinition side = _workingSide?.Confirmed;
        if (side == null) return true;
        var ids = new List<int>();
        var rgb = new List<double>();
        foreach (WorkingArea area in _areas)
        {
            if (!area.PatternDisplayed || area.NativePieceIds.Length == 0) continue;
            if (!AppendRouteColors(area, ids, rgb)) return false;
        }
        return ApplyRouteColors(side, ids.ToArray(), rgb.ToArray());
    }

    /// <summary>영역 하나의 경로 조각 ID와 원색을 준비 목록에 추가한다. 음수 ID는 실패다.</summary>
    private static bool AppendRouteColors(WorkingArea area, List<int> ids, List<double> rgb)
    {
        double r = area.Color.R / 255.0, g = area.Color.G / 255.0, b = area.Color.B / 255.0;
        foreach (int pieceId in area.NativePieceIds)
        {
            if (pieceId < 0) return false;
            ids.Add(pieceId);
            rgb.Add(r); rgb.Add(g); rgb.Add(b);
        }
        return true;
    }

    /// <summary>한 번의 네이티브 호출로 경로 조각 색을 바꾼다. 부분 적용은 없다.</summary>
    private static bool ApplyRouteColors(WorkingSideDefinition side, int[] pieceIds, double[] rgb)
    {
        if (pieceIds.Length == 0) return true;
        if (side == null) return false;
        try
        {
            return Bridge.SetRoutePieceColors(side.Id.Value, side.Revision.Value, pieceIds, rgb, false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 모든 영역의 발열면 표시를 다시 적용한다. 제외가 없는 영역은 기존처럼 Face 색칠을
    /// 쓰고, 제외가 있는 영역은 클리핑된 채움 메시만 표시한다(통짜 Face 색칠 금지).
    /// </summary>
    private void ApplyAreaColors()
    {
        // 영역 외곽선과 같은 원색으로 경로도 맞춘다. Face 색칠 조기 반환과 무관하게 항상 실행한다.
        RefreshRouteColors();
        // 같은 Face를 둘 이상 영역이 참조하면 어느 영역의 색도 그 Face 전체를 대표하지
        // 않는다. 마지막 영역 색으로 전체 Face를 덮지 않고, 영역 메시가 있는 경우에만
        // 부분 표시를 쓴다.
        var owners = new Dictionary<int, int>();
        foreach (var area in _areas)
            foreach (int faceId in area.FaceIdSet)
                owners[faceId] = owners.TryGetValue(faceId, out int owned) ? owned + 1 : 1;

        var faceIds = new List<int>();
        var rgb = new List<double>();
        var clipped = new List<WorkingArea>();
        foreach (var area in _areas)
        {
            // 분할 영역은 같은 Face 집합을 공유하고, 네이티브 채움은 영역 폴리곤으로
            // 클립할 수 없다. 통짜 색칠/부분 메시 대신 3D 경계선만 표시한다.
            if (area.HasSplitBoundary) continue;
            if (area.Exclusions.Count > 0) { clipped.Add(area); continue; }
            double r = area.Color.R / 255.0, g = area.Color.G / 255.0, b = area.Color.B / 255.0;
            foreach (int faceId in area.FaceIdSet)
            {
                if (owners[faceId] > 1) continue;
                faceIds.Add(faceId);
                rgb.Add(r); rgb.Add(g); rgb.Add(b);
            }
        }

        // 메시를 먼저 확정해 두면 Face 색칠 갱신이 그 영역의 색을 덮어쓰지 않는다.
        foreach (var area in clipped)
            if (!ApplyAreaHighlightMesh(area))
                StatusText.Text = string.Format("{0} 영역의 제외영역 표시를 갱신하지 못했습니다 — 이전 표시를 유지합니다", area.Name);

        if (faceIds.Count == 0)
        {
            Bridge.ClearConfirmedFaceHighlights();
            return;
        }
        Bridge.SetConfirmedFaceHighlightColors(faceIds.ToArray(), rgb.ToArray());
    }

    /// <summary>
    /// 활성 영역의 발열면 표시를 현재 제외 상태에 맞춘다.
    /// 제외 없음 → Face 색칠, 제외 있음 → 클리핑된 채움 메시. 실패 시 이전 표시를 유지한다.
    /// </summary>
    private void UpdateActiveAreaHighlight()
    {
        var area = _activeArea;
        if (area == null) return;
        try
        {
            if (area.Frame == null || area.FaceIdSet == null || area.FaceIdSet.Count == 0)
            {
                if (area.AreaId > 0) Bridge.ClearAreaHighlight(area.AreaId);
                return;
            }
            if (area.Exclusions.Count == 0 || area.HasSplitBoundary)
            {
                // Face 색칠로 되돌린다. 여러 영역이 공존하므로 전체 색칠을 다시 적용한다.
                // 분할 영역은 부분 채움을 지원하지 않으므로 경계선 표시만 유지한다.
                if (area.AreaId > 0) Bridge.ClearAreaHighlight(area.AreaId);
                ApplyAreaColors();
                return;
            }
            // Stage가 실패하면 이전 표시(이전 커밋 메시 또는 Face 색칠)가 그대로 남는다.
            if (!ApplyAreaHighlightMesh(area))
            {
                StatusText.Text = "제외영역 표시를 갱신하지 못했습니다 — 이전 표시를 유지합니다";
                return;
            }
            Bridge.RedrawView();
        }
        catch (Exception ex)
        {
            StatusText.Text = "제외영역 표시 갱신 실패: " + ex.Message;
        }
    }

    /// <summary>영역의 2D 제외 사각형을 4개 double(minX,minY,maxX,maxY)씩 펼친다.</summary>
    private static double[] ExclusionRectValues(WorkingArea area)
    {
        var rects = new double[area.Exclusions.Count * 4];
        for (int i = 0; i < area.Exclusions.Count; i++)
        {
            var rect = area.Exclusions[i];
            rects[i * 4] = rect.MinX;
            rects[i * 4 + 1] = rect.MinY;
            rects[i * 4 + 2] = rect.MaxX;
            rects[i * 4 + 3] = rect.MaxY;
        }
        return rects;
    }

    /// <summary>StageAreaHighlightMesh에 넘길 프레임 값 9개(origin, axisX, axisY).</summary>
    private static double[] FrameValues(WorkingArea area)
    {
        ProjectionFrame frame = area.Frame;
        return new[]
        {
            frame.Origin.X, frame.Origin.Y, frame.Origin.Z,
            frame.AxisX.X, frame.AxisX.Y, frame.AxisX.Z,
            frame.AxisY.X, frame.AxisY.Y, frame.AxisY.Z
        };
    }

    /// <summary>제외 사각형을 뺀 채움 메시를 스테이징한다(표시는 아직 바뀌지 않는다).</summary>
    private bool StageAreaHighlightMesh(WorkingArea area)
    {
        var side = _workingSide?.Confirmed;
        if (area == null || area.AreaId <= 0 || area.Frame == null || area.FaceIdSet == null ||
            area.FaceIdSet.Count == 0 || area.Exclusions.Count == 0 ||
            side == null) return false;
        try
        {
            int[] faceIds = SortedFaceIds(area);
            return Bridge.StageAreaHighlightMesh(area.AreaId, side.Revision.Value, side.Id.Value,
                faceIds, ExclusionRectValues(area), FrameValues(area),
                new[] { area.Color.R / 255.0, area.Color.G / 255.0, area.Color.B / 255.0 });
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>스테이징된 채움 메시를 확정 표시로 바꾼다.</summary>
    private bool CommitAreaHighlight(WorkingArea area)
    {
        if (area == null || area.AreaId <= 0) return false;
        try { return Bridge.CommitAreaHighlight(area.AreaId); }
        catch (Exception) { return false; }
    }

    private bool ApplyAreaHighlightMesh(WorkingArea area) =>
        StageAreaHighlightMesh(area) && CommitAreaHighlight(area);

    private string NextAreaName()
    {
        for (int i = 0; i < 26; i++)
        {
            string name = ((char)('A' + i)).ToString();
            if (_areas.All(a => a.Name != name)) return name;
        }
        return "A" + (_areas.Count + 1);
    }

    private Color NextAreaColor()
    {
        foreach (var color in AreaColors)
            if (_areas.All(a => a.Color != color)) return color;
        return AreaColors[_areas.Count % AreaColors.Length];
    }

    private int[] AllocateNativePieceIds(int count)
    {
        var ids = new int[count];
        for (int i = 0; i < count; i++) ids[i] = _nextNativePieceId++;
        return ids;
    }

    private int[] SortedFaceIds(WorkingArea area) => area.FaceIdSet.OrderBy(id => id).ToArray();

    private string AreaNameForFace(int faceId) =>
        _areas.FirstOrDefault(area => area.FaceIdSet.Contains(faceId))?.Name;

    private static double[] Flatten(ProjectionFrame frame, IReadOnlyList<Vec2> points)
    {
        var xyz = new double[points.Count * 3];
        for (int i = 0; i < points.Count; i++)
        {
            Vec3 p = frame.Unproject(points[i]);
            xyz[i * 3] = p.X; xyz[i * 3 + 1] = p.Y; xyz[i * 3 + 2] = p.Z;
        }
        return xyz;
    }

    private string FaceTypesText(WorkingArea area) =>
        string.Join(", ", SortedFaceIds(area).Select(id => "Face " + id + "=" + Bridge.GetFaceType(id)));

    /// <summary>
    /// 곡면 영역의 조각 하나를 데이텀 점 단위로 매핑해 3D 폴리라인으로 스테이징한다.
    /// 2D 프레임 → 3D 평면 되돌리기와 BRepProj 재투영을 모두 쓰지 않는다.
    /// </summary>
    /// <param name="allowCachedSamples">완전한 형상/컨텍스트/인출 binding/엄격 검증 키가
    /// 일치하는 표본만 재사용한다. 편집 세션의 (영역, 인덱스) 캐시는 사용하지 않는다.</param>
    /// <param name="exclusionMode">true면 제외영역이 잘라낸 연결선을 "ExcludedBridge" 종류로 넘긴다.</param>
    private RoutePieceResult StageCurvedRoutePiece(WorkingArea area, WorkingSideDefinition side,
        SerpentineRoutePiece piece, int index, int count, int stageId,
        bool allowCachedSamples = true, bool exclusionMode = false,
        CommittedRouteContext mappingContext = null,
        IReadOnlyDictionary<int, SurfaceMappedSample> vertexOverrides = null,
        Dictionary<ValidatedRouteSampleKey, SurfaceMappedSample[]> validatedSamples = null,
        ValidatedRouteSampleKeyPrefix sampleKeyPrefix = null)
    {
        mappingContext ??= CommittedRouteContext.Capture(area);
        ValidatedRouteSampleKey key = allowCachedSamples
            ? (sampleKeyPrefix ?? BuildValidatedRouteSampleKeyPrefix(area, side, mappingContext))
                .Build(piece, vertexOverrides)
            : null;
        SurfaceMappedSample[] cachedSamples = null;
        bool cached = key != null && validatedSamples != null &&
            validatedSamples.TryGetValue(key, out cachedSamples);
        IReadOnlyList<SurfaceMappedSample> samples = cachedSamples;
        if (!cached)
        {
            var hit = BuildSurfaceMapDelegate(area, side, context: mappingContext);
            if (!SurfacePathMapper.TryMapRouteStrict(piece.Points, hit, vertexOverrides,
                    out List<SurfaceMappedSample> mapped, out string failure, gapSpacing: 0.1))
                throw new InvalidOperationException(
                    "영역 " + area.Name + " 경로 조각 " + (index + 1) + "/" + count + " (" + piece.Kind + ") 매핑 실패 — "
                    + failure + " [" + FaceTypesText(area) + "]");
            samples = mapped.ToArray();
            // 성공 전에는 후보 사전만 쓴다. 실패한 편집/생성이 확정 캐시를 바꾸지 않는다.
            if (key != null && validatedSamples != null)
                validatedSamples[key] = (SurfaceMappedSample[])samples;
        }

        var xyz = new double[samples.Count * 3];
        var supportFaces = new int[samples.Count];
        for (int i = 0; i < samples.Count; i++)
        {
            xyz[i * 3] = samples[i].Point.X;
            xyz[i * 3 + 1] = samples[i].Point.Y;
            xyz[i * 3 + 2] = samples[i].Point.Z;
            supportFaces[i] = samples[i].FaceId;
        }
        var shown = Bridge.StageSurfaceMappedRoutePieceWithFaces(
            side.Id.Value, side.Revision.Value, stageId, StageKindText(piece, exclusionMode), xyz, supportFaces);
        if (shown == null)
            throw new InvalidOperationException(
                "영역 " + area.Name + " 경로 조각 " + (index + 1) + "/" + count + " (" + piece.Kind + ") 3D 스테이징 실패 ["
                + FaceTypesText(area) + " | " + Bridge.GetWorkingStageDiagnostic() + "]");
        return shown;
    }

    /// <summary>네이티브가 색/선종류를 고르는 데 쓰는 kind 문자열. 제외 모드의 턴·고정 연결선은
    /// 발열선이 아니므로 전부 ExcludedBridge(연결선 스타일)로 넘긴다.</summary>
    private static string StageKindText(SerpentineRoutePiece piece, bool exclusionMode) =>
        exclusionMode && piece.Kind is SerpentineRoutePieceKind.TurnConnector
            or SerpentineRoutePieceKind.FixedConnector
            ? "ExcludedBridge"
            : piece.Kind.ToString();

    /// <summary>해당 회로 끝 occurrence의 저장된 원본 parameter만 직접 평가한다.</summary>
    private void AddExitOverride(ref Dictionary<int, SurfaceMappedSample> overrides,
        int pointIndex, CircuitExitPoint exit, Vec2 piecePoint, WorkingSideDefinition side,
        CommittedRouteContext context)
    {
        if (exit == null || !double.IsFinite((exit.Point - piecePoint).Length) ||
            (exit.Point - piecePoint).Length > ExitBoundaryConstraint.AcceptanceTolerance)
            throw new InvalidOperationException("회로 인출점과 대상 경로 끝점이 일치하지 않습니다");
        SurfaceMappedSample? sample = BuildExitVertexOverride(side, context, exit);
        if (!sample.HasValue)
            throw new InvalidOperationException("인출점을 인출 경계 위에서 평가하지 못했습니다");
        double datumError = (sample.Value.DatumPoint - piecePoint).Length;
        if (!double.IsFinite(datumError) || datumError > ExitBoundaryConstraint.AcceptanceTolerance)
            throw new InvalidOperationException("인출 경계의 작업평면 좌표와 경로 끝점이 일치하지 않습니다 (오차 "
                + datumError + "mm)");
        overrides ??= new Dictionary<int, SurfaceMappedSample>();
        overrides[pointIndex] = sample.Value;
    }

    /// <summary>2D 인출 체인에서 가장 가까운 구간을 찾아 같은 구간 파라미터로 3D 점을 보간한다.</summary>
    private static Vec3 PointOnExitChain3D(WorkingArea area, Vec2 datum, out int segmentIndex) =>
        PointOnExitChain3D(area.ExitChain, area.ExitChain3D, datum, out segmentIndex);

    private static Vec3 PointOnExitChain3D(IReadOnlyList<Vec2> chain, IReadOnlyList<Vec3> chain3D,
        Vec2 datum, out int segmentIndex)
    {
        double bestDistance = double.MaxValue;
        int bestSegment = 1;
        double bestT = 0;
        for (int i = 1; i < chain.Count; i++)
        {
            Vec2 a = chain[i - 1], b = chain[i];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double lengthSquared = dx * dx + dy * dy;
            double t = lengthSquared <= 1e-18
                ? 0
                : Math.Clamp(((datum.X - a.X) * dx + (datum.Y - a.Y) * dy) / lengthSquared, 0, 1);
            double px = a.X + dx * t - datum.X, py = a.Y + dy * t - datum.Y;
            double distance = px * px + py * py;
            if (distance < bestDistance) { bestDistance = distance; bestSegment = i; bestT = t; }
        }
        segmentIndex = bestSegment;
        Vec3 p0 = chain3D[bestSegment - 1], p1 = chain3D[bestSegment];
        return new Vec3(p0.X + (p1.X - p0.X) * bestT,
                        p0.Y + (p1.Y - p0.Y) * bestT,
                        p0.Z + (p1.Z - p0.Z) * bestT);
    }

    /// <summary>既存 API: 確定済み領域は retain し、未確定の互換経路だけを候補にする。</summary>
    private bool RebuildNativePatternGeometry(out double activeLength, out int activeCount) =>
        RebuildNativePatternGeometry(_areas, null, out activeLength, out activeCount);

    /// <summary>
    /// 기존 호출자는 원본의 현재 상태를 분리 복사해 변경 후보로 사용한다.
    /// 다른 영역의 초안은 회로 재계산이나 표면 재매핑에 사용하지 않는다.
    /// </summary>
    private bool RebuildNativePatternGeometry(WorkingArea changedArea,
        out double activeLength, out int activeCount) =>
        RebuildNativePatternGeometry(_areas, changedArea, out activeLength, out activeCount);

    private bool RebuildNativePatternGeometry(IReadOnlyList<WorkingArea> areas, WorkingArea changedArea,
        out double activeLength, out int activeCount)
    {
        var candidates = new Dictionary<WorkingArea, WorkingArea>();
        if (changedArea != null)
            candidates.Add(changedArea, ForkAreaRoute(changedArea));
        else if (!_areas.Any(area => !areas.Contains(area)))
        {
            // 최초 스테이징을 호출하는 기존 테스트/호환 경로만 허용한다.
            // 실제 삭제에서는 남은 영역의 미확정 초안을 생성하지 않는다.
            foreach (WorkingArea area in areas)
                if (area.CommittedMapping == null && HasUncommittedRoute(area))
                    candidates.Add(area, ForkAreaRoute(area));
        }
        return TryCommitAreaRouteCandidates(areas, candidates, out activeLength, out activeCount);
    }

    private void ClearAllAreas(bool clearNative)
    {
        ExitExclusionSketch();
        ResetCircuitEditingUi();
        // 외부에서 이전 컬렉션 참조를 들고 있어도 빈 상태가 되도록 in-place로 비운다.
        foreach (WorkingArea area in _areas)
        {
            area.FaceIdSet.Clear();
            area.Region.Clear();
            area.Exclusions.Clear();
            area.ProjectedPieces.Clear();
        }
        _areas.Clear();
        _activeArea = null;
        _nextNativePieceId = 0;
        _selectedFaceIds.Clear();
        if (SelectedFacesList != null) SelectedFacesList.Items.Clear();
        _leadLengths = Array.Empty<double>();
        if (Viewport != null) Viewport.CircuitHoverMode = false;
        RefreshAreaList();
        SyncExitFaceUi(null);
        if (clearNative)
        {
            Bridge.ClearRoutePieces();
            Bridge.ClearExclusionOverlays();
            Bridge.ClearAllAreaHighlights();
            Bridge.ClearConfirmedFaceHighlights();
            Bridge.ClearExitEdgeHighlight();
        }
    }
}
