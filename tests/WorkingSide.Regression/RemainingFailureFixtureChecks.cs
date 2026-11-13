using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

/// <summary>
/// 2026-11-13에 남아 있던 두 실패(B 세로 4분할 AnchorNotAtLaneEnd, B 하단 제외영역
/// 추가 후 표면 도메인 이탈)의 재현 입력을 고정한다. 실제 재현 실행은
/// repro-b-four-split-anchor.json / repro-b-exclusion-surface.json이 담당하고,
/// 이 그룹은 fixture와 시나리오의 입력이 나중에 조용히 바뀌지 않았는지 대조한다.
/// </summary>
static class RemainingFailureFixtureChecks
{
    static int count;
    static void Check(bool value, string label)
    { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); ++count; }

    static JsonElement Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    static string Action(JsonElement step) =>
        step.TryGetProperty("action", out var value) ? value.GetString() ?? "" : "";
    static string Area(JsonElement step) =>
        step.TryGetProperty("area", out var value) ? value.GetString() ?? "" : "";
    static string Outcome(JsonElement step) =>
        step.TryGetProperty("outcome", out var value) ? value.GetString() ?? "" : "success";

    static void Number(JsonElement parent, string property, double expected, string label)
    {
        Check(parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number,
            label + " 값이 있습니다");
        Check(Math.Abs(value.GetDouble() - expected) <= 1e-9, label + " 값이 일치합니다");
    }

    static void Text(JsonElement parent, string property, string expected, string label)
    {
        Check(parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String,
            label + " 값이 있습니다");
        Check(string.Equals(value.GetString(), expected, StringComparison.Ordinal),
            label + " 값이 일치합니다");
    }

    static double[] NumbersOf(JsonElement parent, string property)
    {
        var array = parent.GetProperty(property);
        var values = new double[array.GetArrayLength()];
        int index = 0;
        foreach (var item in array.EnumerateArray()) values[index++] = item.GetDouble();
        return values;
    }

    static void Numbers(JsonElement parent, string property, double[] expected, string label)
    {
        double[] actual = NumbersOf(parent, property);
        Check(actual.Length == expected.Length, label + " 개수가 일치합니다");
        for (int i = 0; i < expected.Length; ++i)
            Check(Math.Abs(actual[i] - expected[i]) <= 1e-9, label + "[" + i + "] 값이 일치합니다");
    }

    static void Texts(JsonElement parent, string property, string[] expected, string label)
    {
        var array = parent.GetProperty(property);
        Check(array.GetArrayLength() == expected.Length, label + " 개수가 일치합니다");
        int index = 0;
        foreach (var item in array.EnumerateArray())
            Check(string.Equals(item.GetString(), expected[index], StringComparison.Ordinal),
                label + "[" + index++ + "] 값이 일치합니다");
    }

    static JsonElement FindArea(JsonElement scenario, string alias)
    {
        foreach (var area in scenario.GetProperty("areas").EnumerateArray())
            if (string.Equals(area.GetProperty("alias").GetString(), alias, StringComparison.Ordinal))
                return area;
        throw new Exception("시나리오에서 영역을 찾지 못했습니다: " + alias);
    }

    static int FindStep(List<JsonElement> steps, string action, string area, string outcome = null)
    {
        for (int i = 0; i < steps.Count; ++i)
        {
            if (!string.Equals(Action(steps[i]), action, StringComparison.Ordinal)) continue;
            if (area != null && !string.Equals(Area(steps[i]), area, StringComparison.Ordinal)) continue;
            if (outcome != null && !string.Equals(Outcome(steps[i]), outcome, StringComparison.Ordinal)) continue;
            return i;
        }
        return -1;
    }

    static bool ExpectMatches(JsonElement step, JsonElement expected)
    {
        if (!step.TryGetProperty("expect", out var actual)) return false;
        foreach (var property in expected.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Number) return false;
            if (!actual.TryGetProperty(property.Name, out var value)) return false;
            if (value.ValueKind != JsonValueKind.Number) return false;
            if (Math.Abs(value.GetDouble() - property.Value.GetDouble()) > 1e-9) return false;
        }
        return true;
    }

    static void CompareExpectation(JsonElement step, JsonElement expected, string label)
    {
        Check(step.TryGetProperty("expect", out var actual), label + " expect가 있습니다");
        foreach (var property in expected.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Number)
                Number(actual, property.Name, property.Value.GetDouble(), label + "." + property.Name);
            else if (property.Value.ValueKind == JsonValueKind.String)
                Text(actual, property.Name, property.Value.GetString() ?? "", label + "." + property.Name);
            else
                Check(false, label + "." + property.Name + " 지원하지 않는 expect 형식입니다");
        }
    }

    static void CompareElectrical(JsonElement step, JsonElement expected, string label)
    {
        Check(step.TryGetProperty("electrical", out var actual), label + " 전기 설정이 있습니다");
        Number(actual, "presetIndex", expected.GetProperty("presetIndex").GetDouble(), label + ".presetIndex");
        Number(actual, "voltageV", expected.GetProperty("voltageV").GetDouble(), label + ".voltageV");
        Number(actual, "allowedCurrentA", expected.GetProperty("allowedCurrentA").GetDouble(), label + ".allowedCurrentA");
        Number(actual, "allowedPowerKw", expected.GetProperty("allowedPowerKw").GetDouble(), label + ".allowedPowerKw");
        Number(actual, "reviewScope", expected.GetProperty("reviewScope").GetDouble(), label + ".reviewScope");
    }

    static void VerifyFailure(string root, JsonElement failure, JsonElement fixtureModel)
    {
        string scenarioRelative = failure.GetProperty("scenario").GetString() ?? "";
        string scenarioName = Path.GetFileName(scenarioRelative);
        Check(scenarioName.StartsWith("repro-", StringComparison.Ordinal),
            "실패 재현은 별도 repro 시나리오로 분리되어 있습니다: " + scenarioName);

        string scenarioPath = Path.Combine(root, scenarioRelative.Replace('/', Path.DirectorySeparatorChar));
        Check(File.Exists(scenarioPath), "재현 시나리오 파일이 있습니다: " + scenarioRelative);
        JsonElement scenario = Load(scenarioPath);

        Check(scenario.GetProperty("schemaVersion").GetInt32() == 3, scenarioName + " 스키마는 3입니다");
        JsonElement model = scenario.GetProperty("model");
        Text(model, "sha256", fixtureModel.GetProperty("sha256").GetString() ?? "", scenarioName + ".model.sha256");
        Number(model, "faceCount", fixtureModel.GetProperty("faceCount").GetDouble(), scenarioName + ".model.faceCount");
        Number(model, "solidCount", fixtureModel.GetProperty("solidCount").GetDouble(), scenarioName + ".model.solidCount");

        var steps = scenario.GetProperty("steps").EnumerateArray().ToList();
        Check(steps.Count == failure.GetProperty("actionCount").GetInt32(),
            scenarioName + " 액션 수가 기록과 일치합니다");

        foreach (var area in failure.GetProperty("areas").EnumerateArray())
        {
            string alias = area.GetProperty("alias").GetString() ?? "";
            JsonElement actual = FindArea(scenario, alias);
            Numbers(actual, "faceIds", NumbersOf(area, "faceIds"), scenarioName + "." + alias + ".faceIds");
            Number(actual, "spacingMm", area.GetProperty("spacingMm").GetDouble(), scenarioName + "." + alias + ".spacingMm");
            Number(actual, "direction", area.GetProperty("direction").GetDouble(), scenarioName + "." + alias + ".direction");

            foreach (var pick in area.GetProperty("picks").EnumerateArray())
            {
                string role = pick.GetProperty("role").GetString() ?? "";
                int pickIndex = -1;
                for (int i = 0; i < steps.Count; ++i)
                {
                    if (!string.Equals(Action(steps[i]), "pickExitPoint", StringComparison.Ordinal)) continue;
                    if (!string.Equals(Area(steps[i]), alias, StringComparison.Ordinal)) continue;
                    JsonElement exitPoint = steps[i].GetProperty("exitPoint");
                    if (string.Equals(exitPoint.GetProperty("role").GetString(), role, StringComparison.Ordinal))
                    { pickIndex = i; break; }
                }
                Check(pickIndex >= 0, scenarioName + "." + alias + "." + role + " 픽 단계가 있습니다");
                JsonElement exitPointStep = steps[pickIndex].GetProperty("exitPoint");
                Numbers(exitPointStep, "uv", NumbersOf(pick, "uv"), scenarioName + "." + alias + "." + role + ".uv");
                Number(exitPointStep, "toleranceMm", pick.GetProperty("toleranceMm").GetDouble(),
                    scenarioName + "." + alias + "." + role + ".toleranceMm");
                if (!pick.GetProperty("toleranceConfirmed").GetBoolean())
                    Check(pick.TryGetProperty("toleranceNote", out var note) &&
                        (note.GetString() ?? "").Contains("미확정", StringComparison.Ordinal),
                        scenarioName + "." + alias + "." + role + " 미확정 tolerance가 기록되어 있습니다");
                Check(pick.TryGetProperty("resolvedBinding", out _),
                    scenarioName + "." + alias + "." + role + " 해석 binding이 기록되어 있습니다");
            }

            var generated = area.GetProperty("generatedExpect");
            int generateIndex = FindStep(steps, "generate", alias);
            Check(generateIndex >= 0, scenarioName + "." + alias + " 초기 생성 단계가 있습니다");
            CompareExpectation(steps[generateIndex], generated, scenarioName + "." + alias + ".generate");
        }

        JsonElement initialElectrical = failure.GetProperty("initialElectrical");
        int electricalIndex = FindStep(steps, "setElectrical", null);
        Check(electricalIndex >= 0, scenarioName + " 초기 전기 설정 단계가 있습니다");
        CompareElectrical(steps[electricalIndex], initialElectrical, scenarioName + ".initialElectrical");

        if (failure.TryGetProperty("splitElectrical", out var splitElectrical))
        {
            int splitElectricalIndex = -1;
            for (int i = 0; i < steps.Count; ++i)
            {
                if (!string.Equals(Action(steps[i]), "setElectrical", StringComparison.Ordinal)) continue;
                if (Math.Abs(steps[i].GetProperty("electrical").GetProperty("voltageV").GetDouble() -
                    splitElectrical.GetProperty("voltageV").GetDouble()) <= 1e-9)
                { splitElectricalIndex = i; break; }
            }
            Check(splitElectricalIndex >= 0, scenarioName + " 분할 직전 전기 설정 단계가 있습니다");
            CompareElectrical(steps[splitElectricalIndex], splitElectrical, scenarioName + ".splitElectrical");
        }

        if (failure.TryGetProperty("exclusion", out var exclusion))
        {
            string area = exclusion.GetProperty("area").GetString() ?? "";
            int exclusionIndex = FindStep(steps, "addExclusion", area);
            Check(exclusionIndex >= 0, scenarioName + " 제외영역 추가 단계가 있습니다");
            Numbers(steps[exclusionIndex], "rect", NumbersOf(exclusion, "rect"),
                scenarioName + ".exclusion.rect");
        }

        JsonElement failureSpec = failure.GetProperty("failure");
        string failureAction = failureSpec.GetProperty("action").GetString() ?? "";
        string failureArea = failureSpec.GetProperty("area").GetString() ?? "";
        bool resolved = failureSpec.TryGetProperty("resolved", out var resolvedValue) && resolvedValue.GetBoolean();
        int failureIndex;
        if (resolved)
        {
            failureIndex = -1;
            for (int i = steps.Count - 1; i >= 0; --i)
            {
                if (!string.Equals(Action(steps[i]), failureAction, StringComparison.Ordinal)) continue;
                if (!string.Equals(Area(steps[i]), failureArea, StringComparison.Ordinal)) continue;
                if (failureSpec.TryGetProperty("expect", out var expected) && !ExpectMatches(steps[i], expected))
                    continue;
                failureIndex = i;
                break;
            }
            Check(failureIndex >= 0, scenarioName + " 해결된 단계가 있습니다: " + failureAction + " " + failureArea);
            if (failureIndex >= 0 && failureSpec.TryGetProperty("expect", out var resolvedExpect))
                CompareExpectation(steps[failureIndex], resolvedExpect, scenarioName + ".resolvedExpect");
        }
        else
        {
            failureIndex = FindStep(steps, failureAction, failureArea, "rejected");
            Check(failureIndex >= 0, scenarioName + " 예상 거부 단계가 outcome=rejected로 있습니다");
            Text(steps[failureIndex], "failureContains", failureSpec.GetProperty("failureContains").GetString() ?? "",
                scenarioName + ".failureContains");
            if (failureAction == "splitArea")
            {
                Number(steps[failureIndex], "partCount", failureSpec.GetProperty("partCount").GetDouble(),
                    scenarioName + ".partCount");
                Texts(steps[failureIndex], "childAliases",
                    failureSpec.GetProperty("childAliases").EnumerateArray().Select(item => item.GetString() ?? "").ToArray(),
                    scenarioName + ".childAliases");
                Text(steps[failureIndex], "confirm", failureSpec.GetProperty("confirm").GetString() ?? "",
                    scenarioName + ".confirm");
                Text(steps[failureIndex], "confirmAction", failureSpec.GetProperty("confirmAction").GetString() ?? "",
                    scenarioName + ".confirmAction");
            }
        }

        foreach (var preservation in failure.GetProperty("preservation").EnumerateArray())
        {
            string when = preservation.GetProperty("when").GetString() ?? "";
            string action = preservation.GetProperty("action").GetString() ?? "";
            string area = preservation.GetProperty("area").GetString() ?? "";
            int found = -1;
            for (int i = 0; i < steps.Count; ++i)
            {
                if (when == "before" && i >= failureIndex) continue;
                if (when == "after" && i <= failureIndex) continue;
                if (!string.Equals(Action(steps[i]), action, StringComparison.Ordinal)) continue;
                if (!string.Equals(Area(steps[i]), area, StringComparison.Ordinal)) continue;
                if (preservation.TryGetProperty("expect", out var expected) && !ExpectMatches(steps[i], expected))
                    continue;
                found = i;
                break;
            }
            Check(found >= 0, scenarioName + " " + when + " 보존 검사가 있습니다: " + action + " " + area);
            if (preservation.TryGetProperty("expect", out var expectedExpect))
                CompareExpectation(steps[found], expectedExpect,
                    scenarioName + ".preservation." + when + "." + action + "." + area);
        }
    }

    public static void Run(string fixtureDir)
    {
        string root = Path.GetFullPath(Path.Combine(fixtureDir, "..", ".."));
        string fixturePath = Path.Combine(root, "tests", "WorkingSide.Regression", "Fixtures", "remaining-failures.json");
        Check(File.Exists(fixturePath), "남은 실패 fixture가 있습니다: " + fixturePath);
        JsonElement fixture = Load(fixturePath);
        JsonElement model = fixture.GetProperty("model");

        string modelPath = Path.Combine(root, model.GetProperty("path").GetString() ?? "");
        Check(File.Exists(modelPath), "fixture 모델 파일이 있습니다");
        string actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(modelPath)));
        Check(string.Equals(actualHash, model.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase),
            "fixture 모델 SHA-256이 실제 파일과 일치합니다");

        JsonElement execution = fixture.GetProperty("execution");
        Check(execution.GetProperty("runs").GetArrayLength() == 2, "두 재현 실행 기록이 있습니다");
        Check(execution.GetProperty("assemblies").GetArrayLength() == 3,
            "실행 DLL 3종(HM App/Core, OccBridge) 증빙이 있습니다");
        foreach (var assembly in execution.GetProperty("assemblies").EnumerateArray())
        {
            Check((assembly.GetProperty("sha256").GetString() ?? "").Length == 64,
                "DLL 증빙 해시 형식이 올바릅니다: " + assembly.GetProperty("file").GetString());
            Check(!string.IsNullOrWhiteSpace(assembly.GetProperty("path").GetString()),
                "DLL 증빙 경로가 기록되어 있습니다: " + assembly.GetProperty("file").GetString());
        }
        Check((execution.GetProperty("historicalArtifactAssemblies").GetString() ?? "")
            .Contains("미확정", StringComparison.Ordinal),
            "과거 artifact의 DLL 출처는 미확정으로 표시되어 있습니다");

        var failures = fixture.GetProperty("failures");
        Check(failures.GetArrayLength() == 2, "두 실패가 각각 보존되어 있습니다");
        foreach (var failure in failures.EnumerateArray())
            VerifyFailure(root, failure, model);

        Console.WriteLine(count + " remaining failure fixture checks passed.");
    }
}
