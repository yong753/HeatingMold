using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HeatingMold.App.VisualTesting;

internal sealed class VisualScenario
{
    public int SchemaVersion { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public VisualModelSpec Model { get; set; }

    // ---- schemaVersion 1: 고정 7단계 ----
    public VisualAreaSpec Area { get; set; }
    public VisualExitSpec Exit { get; set; }
    public VisualAnchorSpec Anchor { get; set; }
    /// <summary>구 schemaVersion 1의 회로 분할 단계. 더 이상 지원하지 않으며,
    /// 기존 시나리오 파일이 조용히 분할 없이 실행되지 않도록 명시적으로 거부한다.</summary>
    public JsonElement? Split { get; set; }
    public VisualElectricalSpec Electrical { get; set; }
    public VisualExpectSpec Expect { get; set; }

    // ---- schemaVersion 2: 영역 목록 + 순차 행동 ----
    public VisualAreaDefinition[] Areas { get; set; }
    public VisualActionSpec[] Steps { get; set; }

    [JsonIgnore] public string ScenarioPath { get; set; } = "";
    [JsonIgnore] public string ResolvedModelPath { get; set; } = "";
    [JsonIgnore] public string ActualModelSha256 { get; set; } = "";

    public static VisualScenario Load(string path, string repositoryRoot, out string error)
    {
        error = null;
        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            VisualScenario scenario = JsonSerializer.Deserialize<VisualScenario>(File.ReadAllText(path), options);
            if (scenario == null) throw new ArgumentException("시나리오 JSON이 비어 있습니다.");
            scenario.ScenarioPath = Path.GetFullPath(path);

            if (scenario.SchemaVersion != 1 && scenario.SchemaVersion != 2)
                throw new ArgumentException("지원하지 않는 schemaVersion입니다. 현재 지원 값은 1, 2입니다.");
            if (scenario.Model == null) throw new ArgumentException("model 섹션이 없습니다.");

            if (string.IsNullOrWhiteSpace(scenario.Model.Path))
                throw new ArgumentException("model.path가 비어 있습니다.");
            scenario.ResolvedModelPath = Path.IsPathRooted(scenario.Model.Path)
                ? Path.GetFullPath(scenario.Model.Path)
                : Path.GetFullPath(Path.Combine(repositoryRoot, scenario.Model.Path));
            if (!File.Exists(scenario.ResolvedModelPath))
                throw new FileNotFoundException("모델 파일을 찾을 수 없습니다.", scenario.ResolvedModelPath);
            if (string.IsNullOrWhiteSpace(scenario.Model.Sha256)
                || scenario.Model.Sha256.Length != 64
                || scenario.Model.Sha256.Any(c => !Uri.IsHexDigit(c)))
                throw new ArgumentException("model.sha256은 64자리 16진수여야 합니다.");

            scenario.ActualModelSha256 = ComputeSha256(scenario.ResolvedModelPath);
            if (!string.Equals(scenario.ActualModelSha256, scenario.Model.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("모델 SHA-256이 시나리오와 일치하지 않습니다. 승인된 모델인지 확인하세요.");
            if (scenario.Model.FaceCount <= 0 || scenario.Model.SolidCount <= 0)
                throw new ArgumentException("model.faceCount/solidCount는 0보다 커야 합니다.");

            if (scenario.SchemaVersion == 1)
                scenario.ValidateLegacy();
            else
                scenario.ValidateActions();

            return scenario;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private void ValidateLegacy()
    {
        if (Area == null) throw new ArgumentException("area 섹션이 없습니다.");
        if (Exit == null) throw new ArgumentException("exit 섹션이 없습니다.");
        if (Split.HasValue && Split.Value.ValueKind != JsonValueKind.Null)
            throw new ArgumentException("schemaVersion 1의 split 단계는 더 이상 지원하지 않습니다. "
                + "회로 분할 없이 영역별 단일 회로를 사용하는 schemaVersion 2 시나리오로 변환하세요.");
        if (Electrical == null) throw new ArgumentException("electrical 섹션이 없습니다.");

        ValidateFaceIds(Area.FaceIds, "area.faceIds");
        ValidatePatternInputs(Area.MarginMm, Area.SpacingMm, Area.Direction, "area");
        ValidateExit(Exit, "exit");

        if (Anchor != null && Anchor.Index < 0)
            throw new ArgumentException("anchor.index는 0 이상이어야 합니다.");
        if (Electrical.PresetIndex < 0 || Electrical.PresetIndex > 2)
            throw new ArgumentException("electrical.presetIndex는 0, 1, 2 중 하나여야 합니다.");
        if (!double.IsFinite(Electrical.VoltageV) || Electrical.VoltageV < 0)
            throw new ArgumentException("electrical.voltageV는 0 이상의 숫자여야 합니다.");
        if (Electrical.ReviewScope < 0 || Electrical.ReviewScope > 1)
            throw new ArgumentException("electrical.reviewScope는 0(전체) 또는 1(현재 영역)이어야 합니다.");
        if (Electrical.PresetIndex == 2
            && (!double.IsFinite(Electrical.DiameterMm) || !(Electrical.DiameterMm > 0)
                || !double.IsFinite(Electrical.ResistanceOhmPerMeter)
                || !(Electrical.ResistanceOhmPerMeter > 0)))
            throw new ArgumentException("presetIndex=2일 때 diameterMm/resistanceOhmPerMeter가 필요합니다.");
        if (!double.IsFinite(Electrical.AllowedCurrentA) || !(Electrical.AllowedCurrentA > 0))
            throw new ArgumentException("electrical.allowedCurrentA는 0보다 큰 숫자여야 합니다.");
        if (!double.IsFinite(Electrical.AllowedPowerKw) || !(Electrical.AllowedPowerKw > 0))
            throw new ArgumentException("electrical.allowedPowerKw는 0보다 큰 숫자여야 합니다.");
    }

    private void ValidateActions()
    {
        if (Areas == null || Areas.Length == 0)
            throw new ArgumentException("schemaVersion=2에는 areas가 필요합니다.");
        if (Steps == null || Steps.Length == 0)
            throw new ArgumentException("schemaVersion=2에는 steps가 필요합니다.");

        var aliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (VisualAreaDefinition area in Areas)
        {
            if (area == null) throw new ArgumentException("areas에 null 항목이 있습니다.");
            if (string.IsNullOrWhiteSpace(area.Alias))
                throw new ArgumentException("area.alias가 비어 있습니다.");
            if (!aliases.Add(area.Alias))
                throw new ArgumentException("area.alias가 중복되었습니다: " + area.Alias);
            ValidateFaceIds(area.FaceIds, "area[" + area.Alias + "].faceIds");
            ValidatePatternInputs(area.MarginMm, area.SpacingMm, area.Direction, "area[" + area.Alias + "]");
            if (area.Exit != null) ValidateExit(area.Exit, "area[" + area.Alias + "].exit");
            if (area.Anchor != null && area.Anchor.Index < 0)
                throw new ArgumentException("area[" + area.Alias + "].anchor.index는 0 이상이어야 합니다.");
            ValidateExpect(area.Expect, "area[" + area.Alias + "].expect");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < Steps.Length; i++)
        {
            VisualActionSpec step = Steps[i];
            if (step == null) throw new ArgumentException("steps에 null 항목이 있습니다.");
            if (string.IsNullOrWhiteSpace(step.Action))
                throw new ArgumentException("steps[" + i + "].action이 비어 있습니다.");
            if (!string.IsNullOrWhiteSpace(step.Id) && !ids.Add(step.Id))
                throw new ArgumentException("steps.id가 중복되었습니다: " + step.Id);

            string action = step.Action;
            switch (action)
            {
                case "selectFaces":
                case "toggleFaces":
                    if (step.Faces == null || step.Faces.Length == 0)
                        throw new ArgumentException("steps[" + i + "].faces가 필요합니다.");
                    ValidateFaceIds(step.Faces, "steps[" + i + "].faces");
                    break;
                case "expectSelection":
                    if (step.Faces == null)
                        throw new ArgumentException("steps[" + i + "].faces가 필요합니다(빈 배열 허용).");
                    ValidateFaceIds(step.Faces, "steps[" + i + "].faces", allowEmpty: true);
                    break;
                case "clearSelection":
                case "checkAllAreas":
                    break;
                case "confirmArea":
                case "activateArea":
                case "deleteArea":
                case "pickExit":
                case "cancelExitPick":
                case "generate":
                case "checkArea":
                    RequireAlias(step.Area, aliases, i);
                    if (action == "generate" || action == "checkArea")
                        ValidateExpect(step.Expect, "steps[" + i + "].expect");
                    if (action == "pickExit")
                    {
                        VisualAreaDefinition target = Areas.First(a => a.Alias == step.Area);
                        if (target.Exit == null && step.Exit == null)
                            throw new ArgumentException("steps[" + i + "]가 참조하는 영역에 exit 정의가 없습니다: " + step.Area);
                        if (step.Exit != null) ValidateExit(step.Exit, "steps[" + i + "].exit");
                        if (step.Anchor != null && step.Anchor.Index < 0)
                            throw new ArgumentException("steps[" + i + "].anchor.index는 0 이상이어야 합니다.");
                    }
                    break;
                case "setAreaInputs":
                    RequireAlias(step.Area, aliases, i);
                    if (step.MarginMm == null && step.SpacingMm == null && step.Direction == null)
                        throw new ArgumentException("steps[" + i + "].setAreaInputs에는 변경할 값이 필요합니다.");
                    if (step.MarginMm != null && (!double.IsFinite(step.MarginMm.Value) || !(step.MarginMm.Value > 0)))
                        throw new ArgumentException("steps[" + i + "].marginMm는 0보다 커야 합니다.");
                    if (step.SpacingMm != null && (!double.IsFinite(step.SpacingMm.Value) || !(step.SpacingMm.Value > 0)))
                        throw new ArgumentException("steps[" + i + "].spacingMm는 0보다 커야 합니다.");
                    if (step.Direction != null && (step.Direction.Value < 0 || step.Direction.Value > 2))
                        throw new ArgumentException("steps[" + i + "].direction은 0..2여야 합니다.");
                    break;
                case "expectAreaInputs":
                    RequireAlias(step.Area, aliases, i);
                    if (step.MarginMm == null && step.SpacingMm == null && step.Direction == null)
                        throw new ArgumentException("steps[" + i + "].expectAreaInputs에는 기대할 값이 필요합니다.");
                    if (step.MarginMm != null && (!double.IsFinite(step.MarginMm.Value) || !(step.MarginMm.Value > 0)))
                        throw new ArgumentException("steps[" + i + "].marginMm는 0보다 커야 합니다.");
                    if (step.SpacingMm != null && (!double.IsFinite(step.SpacingMm.Value) || !(step.SpacingMm.Value > 0)))
                        throw new ArgumentException("steps[" + i + "].spacingMm는 0보다 커야 합니다.");
                    if (step.Direction != null && (step.Direction.Value < 0 || step.Direction.Value > 2))
                        throw new ArgumentException("steps[" + i + "].direction은 0..2여야 합니다.");
                    break;
                case "addExclusion":
                    RequireAlias(step.Area, aliases, i);
                    ValidateRect(step.Rect, i);
                    ValidateConfirm(step, i);
                    break;
                case "removeExclusion":
                    RequireAlias(step.Area, aliases, i);
                    ValidateConfirm(step, i);
                    break;
                case "expectNoPattern":
                    RequireAlias(step.Area, aliases, i);
                    break;
                case "checkExclusion":
                    RequireAlias(step.Area, aliases, i);
                    if (step.Rect != null) ValidateRect(step.Rect, i);
                    break;
                case "moveVertex":
                    RequireAlias(step.Area, aliases, i);
                    if (step.VertexIndex == null || step.VertexIndex.Value < 0)
                        throw new ArgumentException("steps[" + i + "].vertexIndex는 0 이상이어야 합니다.");
                    if (step.OffsetMm == null || step.OffsetMm.Length != 2
                        || !double.IsFinite(step.OffsetMm[0]) || !double.IsFinite(step.OffsetMm[1])
                        || (Math.Abs(step.OffsetMm[0]) < 1e-9 && Math.Abs(step.OffsetMm[1]) < 1e-9))
                        throw new ArgumentException("steps[" + i + "].offsetMm는 0이 아닌 [dx, dy]여야 합니다.");
                    break;
                case "exportDxf":
                    if (step.Scope != null && (step.Scope.Value < 0 || step.Scope.Value > 1))
                        throw new ArgumentException("steps[" + i + "].scope는 0 또는 1이어야 합니다.");
                    ValidateOutcome(step, i);
                    break;
                default:
                    throw new ArgumentException("알 수 없는 action입니다: " + action);
            }

            if (action == "generate")
            {
                ValidateOutcome(step, i);
                ValidateConfirm(step, i);
            }
        }
    }

    private static void ValidateConfirm(VisualActionSpec step, int index)
    {
        if (step.Confirm == null) return;
        if (step.Confirm != "yes" && step.Confirm != "no")
            throw new ArgumentException("steps[" + index + "].confirm은 yes 또는 no여야 합니다.");
        if (string.IsNullOrWhiteSpace(step.ConfirmAction))
            throw new ArgumentException("steps[" + index + "].confirmAction이 필요합니다.");
    }

    private static void ValidateRect(double[] rect, int index)
    {
        if (rect == null || rect.Length != 4 || !rect.All(double.IsFinite))
            throw new ArgumentException("steps[" + index + "].rect는 [minX,minY,maxX,maxY] 4개 실수여야 합니다.");
        if (!(rect[2] - rect[0] >= 0.5) || !(rect[3] - rect[1] >= 0.5))
            throw new ArgumentException("steps[" + index + "].rect는 폭과 높이가 0.5mm 이상이어야 합니다.");
    }

    private static void RequireAlias(string alias, HashSet<string> aliases, int index)
    {
        if (string.IsNullOrWhiteSpace(alias))
            throw new ArgumentException("steps[" + index + "].area가 비어 있습니다.");
        if (!aliases.Contains(alias))
            throw new ArgumentException("steps[" + index + "]가 알 수 없는 area를 참조합니다: " + alias);
    }

    private static void ValidateOutcome(VisualActionSpec step, int index)
    {
        if (string.IsNullOrWhiteSpace(step.Outcome)) step.Outcome = "success";
        if (step.Outcome != "success" && step.Outcome != "rejected")
            throw new ArgumentException("steps[" + index + "].outcome은 success 또는 rejected여야 합니다.");
    }

    private static void ValidateExpect(VisualAreaExpect expect, string section)
    {
        if (expect == null) return;
        if (expect.Pieces != null && expect.Pieces.Value < 0)
            throw new ArgumentException(section + ".pieces는 0 이상이어야 합니다.");
        if (expect.Turns != null && expect.Turns.Value < 0)
            throw new ArgumentException(section + ".turns는 0 이상이어야 합니다.");
        if (expect.Circuits != null && expect.Circuits.Value < 0)
            throw new ArgumentException(section + ".circuits는 0 이상이어야 합니다.");
        if (expect.MarginMm != null && (!double.IsFinite(expect.MarginMm.Value) || expect.MarginMm.Value <= 0))
            throw new ArgumentException(section + ".marginMm는 0보다 큰 유한 숫자여야 합니다.");
        if (expect.SpacingMm != null && (!double.IsFinite(expect.SpacingMm.Value) || expect.SpacingMm.Value <= 0))
            throw new ArgumentException(section + ".spacingMm는 0보다 큰 유한 숫자여야 합니다.");
        if (expect.LengthMm != null && (!double.IsFinite(expect.LengthMm.Value) || expect.LengthMm.Value < 0))
            throw new ArgumentException(section + ".lengthMm는 0 이상의 유한 숫자여야 합니다.");
        if (!double.IsFinite(expect.LengthToleranceMm) || expect.LengthToleranceMm < 0)
            throw new ArgumentException(section + ".lengthToleranceMm는 0 이상이어야 합니다.");
    }

    private static void ValidateFaceIds(int[] faceIds, string section, bool allowEmpty = false)
    {
        if (faceIds == null || (faceIds.Length == 0 && !allowEmpty))
            throw new ArgumentException(section + "가 비어 있습니다.");
        if (faceIds.Any(id => id < 0))
            throw new ArgumentException(section + "에는 0 이상의 Face ID만 사용할 수 있습니다.");
        if (faceIds.Distinct().Count() != faceIds.Length)
            throw new ArgumentException(section + "에 중복된 Face ID가 있습니다.");
    }

    private static void ValidatePatternInputs(double margin, double spacing, int direction, string section)
    {
        if (!double.IsFinite(margin) || !(margin > 0)
            || !double.IsFinite(spacing) || !(spacing > 0))
            throw new ArgumentException(section + ".marginMm/spacingMm은 0보다 큰 유한 숫자여야 합니다.");
        if (direction < 0 || direction > 2)
            throw new ArgumentException(section + ".direction은 0(자동), 1(Frame X), 2(Frame Y) 중 하나여야 합니다.");
    }

    private static void ValidateExit(VisualExitSpec exit, string section)
    {
        if (exit.FaceId < 0)
            throw new ArgumentException(section + ".faceId는 0 이상이어야 합니다.");
        if (exit.ToleranceMm <= 0) exit.ToleranceMm = 1.0;
        if (exit.StartPoint3D != null && exit.StartPoint3D.Length != 3)
            throw new ArgumentException(section + ".startPoint3D는 [x,y,z] 3개 값이어야 합니다.");
        if (exit.EndPoint3D != null && exit.EndPoint3D.Length != 3)
            throw new ArgumentException(section + ".endPoint3D는 [x,y,z] 3개 값이어야 합니다.");
        if ((exit.StartPoint3D == null) != (exit.EndPoint3D == null))
            throw new ArgumentException(section + ".startPoint3D와 endPoint3D는 함께 지정해야 합니다.");
    }

    internal static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }
}

internal sealed class VisualModelSpec
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public int FaceCount { get; set; }
    public int SolidCount { get; set; }
}

internal sealed class VisualAreaSpec
{
    public int[] FaceIds { get; set; } = Array.Empty<int>();
    public double MarginMm { get; set; } = 5;
    public double SpacingMm { get; set; } = 50;
    public int Direction { get; set; }
}

internal sealed class VisualAreaDefinition
{
    public string Alias { get; set; } = "";
    public int[] FaceIds { get; set; } = Array.Empty<int>();
    public double MarginMm { get; set; } = 5;
    public double SpacingMm { get; set; } = 50;
    public int Direction { get; set; }
    public VisualExitSpec Exit { get; set; }
    public VisualAnchorSpec Anchor { get; set; }
    public VisualAreaExpect Expect { get; set; }
}

internal sealed class VisualExitSpec
{
    public int FaceId { get; set; } = -1;
    public double[] StartPoint3D { get; set; }
    public double[] EndPoint3D { get; set; }
    public double ToleranceMm { get; set; } = 1.0;
}

internal sealed class VisualAnchorSpec
{
    public int Index { get; set; }
    /// <summary>작업평면 2D 시작점 지문. 후보 인덱스가 바뀌어도 동일 앵커를 찾는다.</summary>
    public double[] StartUv { get; set; }
    public double[] EndUv { get; set; }
    public double ToleranceMm { get; set; } = 0.5;
}

internal sealed class VisualElectricalSpec
{
    public int PresetIndex { get; set; }
    /// <summary>회로별 허용 전압 [V]. 계산 입력이 아니라 판정 한도다.</summary>
    public double VoltageV { get; set; } = 220;
    public int ReviewScope { get; set; }
    public double DiameterMm { get; set; }
    public double ResistanceOhmPerMeter { get; set; }
    /// <summary>회로별 허용 전류 [A]. 생략하면 제품 기본값을 쓴다.</summary>
    public double AllowedCurrentA { get; set; } = 16;
    /// <summary>전체가 나누어 쓰는 총 발열전력 [kW]. 생략하면 제품 기본값을 쓴다.</summary>
    public double AllowedPowerKw { get; set; } = 3.5;
    /// <summary>지정하면 모든 회로의 판정이 이 값과 일치해야 한다. 생략하면 판정 자체를 검사하지 않는다.</summary>
    public bool? ExpectedWithinLimits { get; set; }
}

internal sealed class VisualExpectSpec
{
    public int? AreaCount { get; set; }
    public int? PieceCount { get; set; }
    public int? TurnCount { get; set; }
}

/// <summary>영역 하나의 최종 기대 상태. 선언된 값만 검사한다.</summary>
internal sealed class VisualAreaExpect
{
    public int? Pieces { get; set; }
    public int? Turns { get; set; }
    public int? Circuits { get; set; }
    public int? ExitFaceId { get; set; }
    public double? LengthMm { get; set; }
    public double LengthToleranceMm { get; set; } = 0.5;
    /// <summary>생성 당시 적용된 마진. 선언한 경우에만 검사한다.</summary>
    public double? MarginMm { get; set; }
    /// <summary>생성 당시 적용된 간격. 선언한 경우에만 검사한다.</summary>
    public double? SpacingMm { get; set; }
    /// <summary>수동 편집 흔적. 선언한 경우에만 검사한다.</summary>
    public bool? RouteEdited { get; set; }
}

/// <summary>schemaVersion 2의 순차 행동 하나.</summary>
internal sealed class VisualActionSpec
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Action { get; set; } = "";
    public string Area { get; set; } = "";
    public int[] Faces { get; set; }
    public double? MarginMm { get; set; }
    public double? SpacingMm { get; set; }
    public int? Direction { get; set; }
    public int? Scope { get; set; }
    public double[] Rect { get; set; }
    public int? VertexIndex { get; set; }
    public double[] OffsetMm { get; set; }
    public string Confirm { get; set; }
    public string ConfirmAction { get; set; }
    public string FileName { get; set; }
    public string Outcome { get; set; } = "success";
    public string FailureContains { get; set; }
    public VisualExitSpec Exit { get; set; }
    public VisualAnchorSpec Anchor { get; set; }
    public VisualAreaExpect Expect { get; set; }
}
