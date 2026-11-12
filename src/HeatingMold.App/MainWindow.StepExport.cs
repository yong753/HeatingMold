using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HeatingMold.Core.Electrical;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;
using Microsoft.Win32;

namespace HeatingMold.App;

public partial class MainWindow
{
    private void StepExport_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!TryEnterBusy("STEP 내보내기")) return;
        try { ExportStepCore(null, out _); }
        finally { ExitBusy(); }
    }

    private enum ExportStepResult
    {
        Success,
        Canceled,
        Rejected,
        Failed
    }

    /// <summary>
    /// STEP 저장의 공통 경로. DXF와 달리 전기 사양표/전력 한도를 조건으로 삼지 않고,
    /// 확정된 3D 모델과 확정 발열경로·발열사만 검증한다.
    /// </summary>
    private ExportStepResult ExportStepCore(string explicitPath, out string error)
    {
        error = null;
        // 대화상자 전에 같은 스냅샷으로 경로 준비 상태를 확정한다.
        ReviewSnapshot preflight = BuildReviewSnapshot();
        List<WorkingArea> routeAreas = SelectStepExportAreas(preflight, out string scopeError);
        if (routeAreas == null)
        {
            error = scopeError;
            StatusText.Text = "STEP 내보내기 불가 — " + scopeError;
            return ExportStepResult.Rejected;
        }
        if (!ElectricalSettingsResolver.TryGetWire(WirePresetBox?.SelectedIndex ?? -1, out HeatingWireSpec wire))
        {
            error = "발열사 규격을 선택하세요.";
            StatusText.Text = "STEP 내보내기 불가 — 전기 검토 탭에서 발열사 규격을 선택하세요.";
            return ExportStepResult.Rejected;
        }

        WorkingSideDefinition side = _workingSide.Confirmed;
        List<RouteExportCircuit> circuits = CollectExportCircuits(routeAreas, side, out string collectError);
        if (circuits == null)
        {
            error = collectError;
            StatusText.Text = "STEP 내보내기 불가 — " + collectError;
            return ExportStepResult.Rejected;
        }
        long modelRevisionBeforeDialog = _modelRevision;

        string file;
        if (explicitPath != null)
        {
            file = explicitPath;
        }
        else
        {
            string suffix = _reviewCurrentAreaOnly && _activeArea != null
                ? "_" + _activeArea.Name
                : "";
            string defaultName = SanitizeFileName(
                (string.IsNullOrEmpty(_modelFileName) ? "heat_pattern" : _modelFileName) + suffix + "_발열경로") + ".step";
            var dialog = new SaveFileDialog
            {
                Title = "STEP 내보내기",
                Filter = "STEP 파일 (*.step)|*.step|STEP 파일 (*.stp)|*.stp|모든 파일 (*.*)|*.*",
                DefaultExt = ".step",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = defaultName
            };
            if (dialog.ShowDialog(this) != true)
            {
                error = "사용자가 저장을 취소했습니다";
                StatusText.Text = "STEP 내보내기를 취소했습니다";
                return ExportStepResult.Canceled;
            }
            file = dialog.FileName;
        }

        string temp = file + "." + Guid.NewGuid().ToString("N") + ".step.tmp";
        try
        {
            if (string.IsNullOrWhiteSpace(file))
                throw new IOException("저장 경로가 비어 있습니다. 다시 선택하세요.");
            string extension = Path.GetExtension(file);
            if (!string.Equals(extension, ".step", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(extension, ".stp", StringComparison.OrdinalIgnoreCase))
                throw new IOException("STEP 확장자(.step 또는 .stp)로 저장하세요.");
            if (!string.IsNullOrEmpty(_modelPath) &&
                string.Equals(Path.GetFullPath(file), Path.GetFullPath(_modelPath),
                    StringComparison.OrdinalIgnoreCase))
                throw new IOException("원본 STEP 파일과 같은 경로에는 저장할 수 없습니다. 다른 이름을 선택하세요.");
            if (_modelRevision != modelRevisionBeforeDialog)
                throw new IOException("모델이 변경되어 내보내기를 중단했습니다. 다시 검토한 뒤 저장하세요.");

            // 대화상자 동안 초안/영역이 바뀌었을 수 있으므로 다시 계산한다.
            ReviewSnapshot snapshot = BuildReviewSnapshot();
            List<WorkingArea> currentAreas = SelectStepExportAreas(snapshot, out string currentScopeError);
            if (currentAreas == null)
                throw new IOException("내보내기 조건이 바뀌었습니다 — " + currentScopeError);
            List<RouteExportCircuit> currentCircuits = CollectExportCircuits(currentAreas, side, out string currentError);
            if (currentCircuits == null)
                throw new IOException("내보내기 조건이 바뀌었습니다 — " + currentError);

            var payload = BuildStepPayload(currentCircuits);
            string modelName = string.IsNullOrEmpty(_modelFileName)
                ? "3D 모델"
                : Path.GetFileNameWithoutExtension(_modelFileName);

            // 대화상자에서 동의한 시점의 대상 상태를 기록한다. 쓰기 직후 이 상태가
            // 달라졌다면 사용자가 동의한 파일이 아니므로 덮어쓰지 않는다.
            bool destinationExisted = File.Exists(file);
            DateTime destinationWriteUtc = default;
            long destinationLength = 0;
            if (destinationExisted)
            {
                var before = new FileInfo(file);
                destinationWriteUtc = before.LastWriteTimeUtc;
                destinationLength = before.Length;
            }

            string directory = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // 최종 파일에 바로 쓰지 않는다. 네이티브가 임시 파일을 쓰고 별도 리더로 검증한다.
            if (!Bridge.ExportStepWithRoutes(temp, modelName, wire.DiameterMm, payload,
                    out string nativeFailure))
                throw new IOException(DescribeStepFailure(nativeFailure, currentCircuits));
            if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
                throw new IOException("STEP 임시 파일이 생성되지 않았습니다.");

            bool existsNow = File.Exists(file);
            if (destinationExisted)
            {
                if (!existsNow)
                    throw new IOException("선택한 대상 파일이 삭제되었습니다. 다시 내보내세요.");
                var now = new FileInfo(file);
                if (now.LastWriteTimeUtc != destinationWriteUtc || now.Length != destinationLength)
                    throw new IOException("선택한 대상 파일이 그 사이 변경되었습니다. 다시 내보내세요.");
                try
                {
                    File.Replace(temp, file, null);
                }
                catch (Exception replaceError) when (replaceError is IOException ||
                    replaceError is UnauthorizedAccessException ||
                    replaceError is PlatformNotSupportedException)
                {
                    throw new IOException(
                        "기존 파일을 안전하게 교체하지 못했습니다. 다른 저장 위치를 선택하세요.", replaceError);
                }
            }
            else
            {
                if (existsNow)
                    throw new IOException("선택한 경로에 다른 파일이 생성되었습니다. 다시 내보내세요.");
                File.Move(temp, file);
            }

            StatusText.Text = "STEP 저장 완료 — " + file + " ("
                + currentCircuits.Count.ToString(CultureInfo.InvariantCulture) + "개 발열경로 · Ø"
                + wire.DiameterMm.ToString("0.00", CultureInfo.InvariantCulture) + " mm)";
            return ExportStepResult.Success;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            StatusText.Text = "STEP 저장 실패: " + ex.Message;
            ErrorReporter.ShowError("STEP 저장 실패 — " + file, ex);
            return ExportStepResult.Failed;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch { /* 임시 파일 정리 실패는 저장 결과를 덮지 않는다. */ }
        }
    }

    /// <summary>
    /// STEP으로 내보낼 영역을 고른다. 경로가 없는 영역은 내보낼 것이 없으므로 건너뛰고,
    /// 경로가 있는데 준비되지 않은 영역은 조용히 빼지 않고 전체 실패로 알린다.
    /// </summary>
    private List<WorkingArea> SelectStepExportAreas(ReviewSnapshot snapshot, out string error)
    {
        error = null;
        var areas = new List<WorkingArea>();
        if (snapshot?.Entries == null) { error = "검토 범위에 작업 영역이 없습니다"; return null; }
        foreach (ReviewAreaEntry entry in snapshot.Entries)
        {
            if (entry?.Area == null) continue;
            if (entry.State == ReviewAreaState.Missing) continue;
            if (entry.State != ReviewAreaState.Ready)
            {
                error = "영역 " + entry.Area.Name + "의 경로 상태가 '" + ReviewStateLabel(entry.State)
                    + "'입니다(" + entry.Reason + "). 경로를 다시 생성한 뒤 내보내세요.";
                return null;
            }
            areas.Add(entry.Area);
        }
        if (areas.Count == 0)
        {
            error = "확정된 발열경로가 없습니다. 경로를 생성한 뒤 다시 내보내세요.";
            return null;
        }
        return areas;
    }

    private static StepExportArea[] BuildStepPayload(IReadOnlyList<RouteExportCircuit> circuits)
    {
        return StepExportPayload.Build(circuits);
    }

    private static string DescribeStepFailure(
        string token, IReadOnlyList<RouteExportCircuit> circuits)
    {
        if (string.IsNullOrEmpty(token)) return "STEP 내보내기에 실패했습니다.";
        if (token == "no-model") return "3D 모델이 로드되지 않았습니다.";
        if (token == "invalid-diameter") return "발열사 직경이 유효하지 않습니다.";
        if (token == "model-copy") return "3D 모델 사본을 만들지 못했습니다.";
        if (token == "step-transfer" || token == "step-write")
            return "STEP 파일을 쓰지 못했습니다.";
        if (token == "xde-application" || token == "xde-document" || token == "xde-tools")
            return "STEP 문서를 준비하지 못했습니다.";
        if (token.StartsWith("wire-build:", StringComparison.Ordinal))
        {
            if (int.TryParse(token.Substring("wire-build:".Length), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int index)
                && circuits != null && index >= 0 && index < circuits.Count)
            {
                return "영역 " + circuits[index].Pieces[0].AreaName
                    + "의 발열사·발열경로 형상을 만들지 못했습니다. 확정 경로 좌표를 확인하세요.";
            }
            return "발열사·발열경로 형상을 만들지 못했습니다.";
        }
        if (token.StartsWith("piece-build:", StringComparison.Ordinal))
        {
            // piece-build:<area>:<piece>:<reason>
            string[] parts = token.Split(':');
            if (parts.Length >= 4
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int areaIndex)
                && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pieceIndex)
                && circuits != null && areaIndex >= 0 && areaIndex < circuits.Count)
            {
                IReadOnlyList<RouteExportPiece> pieces = circuits[areaIndex].Pieces;
                string areaName = pieces[0].AreaName;
                string pieceLabel = pieceIndex >= 0 && pieceIndex < pieces.Count
                    ? (pieces[pieceIndex].PieceIndex + 1).ToString(CultureInfo.InvariantCulture)
                    : (pieceIndex + 1).ToString(CultureInfo.InvariantCulture);
                return "영역 " + areaName + " 조각 " + pieceLabel
                    + "의 발열사·발열경로 형상을 만들지 못했습니다(" + parts[3]
                    + "). 확정 경로 좌표를 확인하세요.";
            }
            return "발열사·발열경로 형상을 만들지 못했습니다.";
        }
        if (token.StartsWith("verify-", StringComparison.Ordinal))
            return "작성한 STEP 파일을 다시 읽어 검증하지 못했습니다. 다른 저장 위치에서 다시 시도하세요.";
        return "STEP 내보내기에 실패했습니다 (" + token + ").";
    }
}
