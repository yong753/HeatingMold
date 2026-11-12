using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using HeatingMold.App.VisualTesting;
using Xunit;

namespace HeatingMold.App.Tests
{
    public sealed class VisualScenarioTests
    {
        [Fact]
        public void Load_AcceptsValidScenarioAndResolvesModel()
        {
            using var temp = new TempDirectory();
            string model = temp.WriteFile("model.stp", "test-model");
            string scenarioPath = WriteScenario(temp, model, sha256: VisualScenario.ComputeSha256(model));

            VisualScenario scenario = VisualScenario.Load(scenarioPath, temp.Path, out string error);

            Assert.Null(error);
            Assert.NotNull(scenario);
            Assert.Equal(Path.GetFullPath(model), scenario.ResolvedModelPath);
            Assert.Equal(VisualScenario.ComputeSha256(model), scenario.ActualModelSha256);
        }

        [Fact]
        public void Load_RejectsHashMismatch()
        {
            using var temp = new TempDirectory();
            string model = temp.WriteFile("model.stp", "test-model");
            string scenarioPath = WriteScenario(temp, model, sha256: new string('0', 64));

            VisualScenario scenario = VisualScenario.Load(scenarioPath, temp.Path, out string error);

            Assert.Null(scenario);
            Assert.Contains("SHA-256", error);
        }

        [Fact]
        public void Load_RejectsDuplicateFaceIds()
        {
            using var temp = new TempDirectory();
            string model = temp.WriteFile("model.stp", "test-model");
            string scenarioPath = WriteScenario(temp, model, sha256: VisualScenario.ComputeSha256(model),
                faceIds: "[1, 1]");

            VisualScenario scenario = VisualScenario.Load(scenarioPath, temp.Path, out string error);

            Assert.Null(scenario);
            Assert.Contains("중복", error);
        }

        [Fact]
        public void Load_RejectsDirectInputPreset()
        {
            using var temp = new TempDirectory();
            string model = temp.WriteFile("model.stp", "test-model");
            string scenarioPath = WriteScenario(temp, model, sha256: VisualScenario.ComputeSha256(model),
                electrical: "\"presetIndex\": 2, \"voltageV\": 220, \"reviewScope\": 0");

            VisualScenario scenario = VisualScenario.Load(scenarioPath, temp.Path, out string error);

            Assert.Null(scenario);
            Assert.Contains("presetIndex", error);
        }

        [Fact]
        public void Load_AcceptsAllowedLimitsAndExpectedVerdict()
        {
            using var temp = new TempDirectory();
            string model = temp.WriteFile("model.stp", "test-model");
            string scenarioPath = WriteScenario(temp, model, sha256: VisualScenario.ComputeSha256(model),
                electrical: "\"presetIndex\": 0, \"voltageV\": 220, \"reviewScope\": 0, "
                    + "\"allowedCurrentA\": 12, \"allowedPowerKw\": 2.5, \"expectedWithinLimits\": true");

            VisualScenario scenario = VisualScenario.Load(scenarioPath, temp.Path, out string error);

            Assert.Null(error);
            VisualElectricalSpec electrical = Assert.Single(scenario.Steps).Electrical;
            Assert.Equal(12.0, electrical.AllowedCurrentA);
            Assert.Equal(2.5, electrical.AllowedPowerKw);
            Assert.True(electrical.ExpectedWithinLimits);
        }

        [Fact]
        public void Load_RejectsNonPositiveAllowedCurrent()
        {
            using var temp = new TempDirectory();
            string model = temp.WriteFile("model.stp", "test-model");
            string scenarioPath = WriteScenario(temp, model, sha256: VisualScenario.ComputeSha256(model),
                electrical: "\"presetIndex\": 0, \"voltageV\": 220, \"reviewScope\": 0, \"allowedCurrentA\": 0");

            VisualScenario scenario = VisualScenario.Load(scenarioPath, temp.Path, out string error);

            Assert.Null(scenario);
            Assert.Contains("allowedCurrentA", error);
        }

        [Fact]
        public void Load_RejectsLegacySchema()
        {
            using var temp = new TempDirectory();
            string model = temp.WriteFile("model.stp", "test-model");
            string scenarioPath = WriteScenario(temp, model, sha256: VisualScenario.ComputeSha256(model),
                schemaVersion: 1);

            VisualScenario scenario = VisualScenario.Load(scenarioPath, temp.Path, out string error);

            Assert.Null(scenario);
            Assert.Contains("schemaVersion", error);
        }

        [Fact]
        public void Load_RejectsUnsupportedSchema()
        {
            using var temp = new TempDirectory();
            string model = temp.WriteFile("model.stp", "test-model");
            string scenarioPath = WriteScenario(temp, model, sha256: VisualScenario.ComputeSha256(model),
                schemaVersion: 3);

            VisualScenario scenario = VisualScenario.Load(scenarioPath, temp.Path, out string error);

            Assert.Null(scenario);
            Assert.Contains("schemaVersion", error);
        }

        [Fact]
        public void Load_AcceptsV2AreasAndActions()
        {
            using var temp = new TempDirectory();
            string model = temp.WriteFile("model.stp", "test-model");
            string json = "{" +
                "\"schemaVersion\":2," +
                "\"name\":\"v2-test\"," +
                "\"model\":{\"path\":\"model.stp\",\"sha256\":\"" + VisualScenario.ComputeSha256(model) + "\",\"faceCount\":2,\"solidCount\":1}," +
                "\"areas\":[{\"alias\":\"A\",\"faceIds\":[1],\"marginMm\":5,\"spacingMm\":10,\"direction\":0," +
                "\"expect\":{\"pieces\":2}}]," +
                "\"steps\":[" +
                "{\"id\":\"1\",\"action\":\"selectFaces\",\"faces\":[1]}," +
                "{\"id\":\"2\",\"action\":\"confirmArea\",\"area\":\"A\"}," +
                "{\"id\":\"3\",\"action\":\"generate\",\"area\":\"A\"}]" +
                "}";
            string scenarioPath = temp.WriteFile("scenario.json", json);

            VisualScenario scenario = VisualScenario.Load(scenarioPath, temp.Path, out string error);

            Assert.Null(error);
            Assert.NotNull(scenario);
            Assert.Single(scenario.Areas);
            Assert.Equal(3, scenario.Steps.Length);
            Assert.Equal("A", scenario.Areas[0].Alias);
        }

        [Fact]
        public void Load_AcceptsZeroSolidModel()
        {
            using var temp = new TempDirectory();
            string model = temp.WriteFile("model.stp", "test-model");
            string json = "{" +
                "\"schemaVersion\":2," +
                "\"name\":\"zero-solid\"," +
                "\"model\":{\"path\":\"model.stp\",\"sha256\":\"" + VisualScenario.ComputeSha256(model) + "\",\"faceCount\":80,\"solidCount\":0}," +
                "\"areas\":[{\"alias\":\"A\",\"faceIds\":[1],\"marginMm\":5,\"spacingMm\":10,\"direction\":0}]," +
                "\"steps\":[{\"id\":\"1\",\"action\":\"selectFaces\",\"faces\":[1]}]" +
                "}";
            string scenarioPath = temp.WriteFile("scenario.json", json);

            VisualScenario scenario = VisualScenario.Load(scenarioPath, temp.Path, out string error);

            Assert.Null(error);
            Assert.NotNull(scenario);
            Assert.Equal(0, scenario.Model.SolidCount);
        }

        [Fact]
        public void Load_AcceptsRepresentativeEndToEndActions()
        {
            using var temp = new TempDirectory();
            string model = temp.WriteFile("model.stp", "test-model");
            string json = "{" +
                "\"schemaVersion\":2," +
                "\"name\":\"e2e-contract\"," +
                "\"model\":{\"path\":\"model.stp\",\"sha256\":\"" + VisualScenario.ComputeSha256(model) + "\",\"faceCount\":80,\"solidCount\":0}," +
                "\"areas\":[{\"alias\":\"A\",\"faceIds\":[44,56,69],\"marginMm\":5,\"spacingMm\":15,\"direction\":0}]," +
                "\"steps\":[" +
                "{\"id\":\"1\",\"action\":\"selectFaces\",\"faces\":[44,56,69]}," +
                "{\"id\":\"2\",\"action\":\"confirmArea\",\"area\":\"A\"}," +
                "{\"id\":\"3\",\"action\":\"pickExitPoint\",\"area\":\"A\",\"exitPoint\":{\"role\":\"start\",\"uv\":[1,2],\"toleranceMm\":0.5}}," +
                "{\"id\":\"4\",\"action\":\"checkElectrical\",\"electrical\":{\"presetIndex\":0,\"voltageV\":220,\"allowedCurrentA\":16,\"allowedPowerKw\":3.5,\"reviewScope\":0,\"expectedWithinLimits\":false}}," +
                "{\"id\":\"5\",\"action\":\"splitArea\",\"area\":\"A\",\"partCount\":3,\"childAliases\":[\"A1\",\"A2\",\"A3\"],\"confirm\":\"yes\",\"confirmAction\":\"영역 분할\"}," +
                "{\"id\":\"6\",\"action\":\"checkArea\",\"area\":\"A1\"}," +
                "{\"id\":\"7\",\"action\":\"exportStep\",\"fileName\":\"out.step\",\"scope\":0}," +
                "{\"id\":\"8\",\"action\":\"saveProject\",\"fileName\":\"out.json\"}," +
                "{\"id\":\"9\",\"action\":\"loadProject\",\"fileName\":\"out.json\",\"compareProject\":true,\"confirm\":\"yes\",\"confirmAction\":\"프로젝트 열기\"}" +
                "]}";
            string scenarioPath = temp.WriteFile("scenario.json", json);

            VisualScenario scenario = VisualScenario.Load(scenarioPath, temp.Path, out string error);

            Assert.Null(error);
            Assert.NotNull(scenario);
            Assert.Equal(9, scenario.Steps.Length);
        }

        [Fact]
        public void Load_RejectsSplitChildAliasCollision()
        {
            using var temp = new TempDirectory();
            string model = temp.WriteFile("model.stp", "test-model");
            string json = "{" +
                "\"schemaVersion\":2," +
                "\"name\":\"split-alias\"," +
                "\"model\":{\"path\":\"model.stp\",\"sha256\":\"" + VisualScenario.ComputeSha256(model) + "\",\"faceCount\":2,\"solidCount\":1}," +
                "\"areas\":[" +
                "{\"alias\":\"A\",\"faceIds\":[1]}," +
                "{\"alias\":\"A1\",\"faceIds\":[2]}]," +
                "\"steps\":[{\"id\":\"1\",\"action\":\"splitArea\",\"area\":\"A\",\"childAliases\":[\"A1\"]}]" +
                "}";
            string scenarioPath = temp.WriteFile("scenario.json", json);

            VisualScenario scenario = VisualScenario.Load(scenarioPath, temp.Path, out string error);

            Assert.Null(scenario);
            Assert.Contains("childAliases", error);
        }

        [Fact]
        public void Load_RejectsArtifactPathOutsideRunDirectory()
        {
            using var temp = new TempDirectory();
            string model = temp.WriteFile("model.stp", "test-model");
            string json = "{" +
                "\"schemaVersion\":2," +
                "\"name\":\"path\"," +
                "\"model\":{\"path\":\"model.stp\",\"sha256\":\"" + VisualScenario.ComputeSha256(model) + "\",\"faceCount\":2,\"solidCount\":1}," +
                "\"areas\":[{\"alias\":\"A\",\"faceIds\":[1]}]," +
                "\"steps\":[{\"id\":\"1\",\"action\":\"saveProject\",\"fileName\":\"../outside.json\"}]" +
                "}";
            string scenarioPath = temp.WriteFile("scenario.json", json);

            VisualScenario scenario = VisualScenario.Load(scenarioPath, temp.Path, out string error);

            Assert.Null(scenario);
            Assert.Contains("순수 파일명", error);
        }

        [Fact]
        public void Load_AcceptsReadOnlyElectricalCheckWithoutSettings()
        {
            using var temp = new TempDirectory();
            string model = temp.WriteFile("model.stp", "test-model");
            string json = "{" +
                "\"schemaVersion\":2," +
                "\"name\":\"electrical\"," +
                "\"model\":{\"path\":\"model.stp\",\"sha256\":\"" + VisualScenario.ComputeSha256(model) + "\",\"faceCount\":2,\"solidCount\":1}," +
                "\"areas\":[{\"alias\":\"A\",\"faceIds\":[1]}]," +
                "\"steps\":[{\"id\":\"1\",\"action\":\"checkElectrical\"}]" +
                "}";
            string scenarioPath = temp.WriteFile("scenario.json", json);

            VisualScenario scenario = VisualScenario.Load(scenarioPath, temp.Path, out string error);

            Assert.NotNull(scenario);
            Assert.Null(error);
        }

        [Fact]
        public void Load_RejectsSetElectricalWithoutSettings()
        {
            using var temp = new TempDirectory();
            string model = temp.WriteFile("model.stp", "test-model");
            string json = "{" +
                "\"schemaVersion\":2," +
                "\"name\":\"electrical\"," +
                "\"model\":{\"path\":\"model.stp\",\"sha256\":\"" + VisualScenario.ComputeSha256(model) + "\",\"faceCount\":2,\"solidCount\":1}," +
                "\"areas\":[{\"alias\":\"A\",\"faceIds\":[1]}]," +
                "\"steps\":[{\"id\":\"1\",\"action\":\"setElectrical\"}]" +
                "}";
            string scenarioPath = temp.WriteFile("scenario.json", json);

            VisualScenario scenario = VisualScenario.Load(scenarioPath, temp.Path, out string error);

            Assert.Null(scenario);
            Assert.Contains("electrical", error);
        }

        [Fact]
        public void Load_RejectsCircuitVerdictForUnknownArea()
        {
            using var temp = new TempDirectory();
            string model = temp.WriteFile("model.stp", "test-model");
            string json = "{" +
                "\"schemaVersion\":2," +
                "\"name\":\"electrical\"," +
                "\"model\":{\"path\":\"model.stp\",\"sha256\":\"" + VisualScenario.ComputeSha256(model) + "\",\"faceCount\":2,\"solidCount\":1}," +
                "\"areas\":[{\"alias\":\"A\",\"faceIds\":[1]}]," +
                "\"steps\":[{\"id\":\"1\",\"action\":\"checkElectrical\",\"electrical\":{\"presetIndex\":0,\"voltageV\":220,\"apply\":false,\"circuitVerdicts\":[{\"area\":\"B\",\"verdict\":\"OK\"}]}}]" +
                "}";
            string scenarioPath = temp.WriteFile("scenario.json", json);

            VisualScenario scenario = VisualScenario.Load(scenarioPath, temp.Path, out string error);

            Assert.Null(scenario);
            Assert.Contains("circuitVerdicts", error);
        }

        [Fact]
        public void Load_RejectsInvalidCircuitVerdict()
        {
            using var temp = new TempDirectory();
            string model = temp.WriteFile("model.stp", "test-model");
            string json = "{" +
                "\"schemaVersion\":2," +
                "\"name\":\"electrical\"," +
                "\"model\":{\"path\":\"model.stp\",\"sha256\":\"" + VisualScenario.ComputeSha256(model) + "\",\"faceCount\":2,\"solidCount\":1}," +
                "\"areas\":[{\"alias\":\"A\",\"faceIds\":[1]}]," +
                "\"steps\":[{\"id\":\"1\",\"action\":\"checkElectrical\",\"electrical\":{\"presetIndex\":0,\"voltageV\":220,\"apply\":false,\"circuitVerdicts\":[{\"area\":\"A\",\"verdict\":\"MAYBE\"}]}}]" +
                "}";
            string scenarioPath = temp.WriteFile("scenario.json", json);

            VisualScenario scenario = VisualScenario.Load(scenarioPath, temp.Path, out string error);

            Assert.Null(scenario);
            Assert.Contains("OK 또는 NG", error);
        }

        [Fact]
        public void Load_RejectsElectricalDirectInputPresetInV2Step()
        {
            using var temp = new TempDirectory();
            string model = temp.WriteFile("model.stp", "test-model");
            string json = "{" +
                "\"schemaVersion\":2," +
                "\"name\":\"electrical\"," +
                "\"model\":{\"path\":\"model.stp\",\"sha256\":\"" + VisualScenario.ComputeSha256(model) + "\",\"faceCount\":2,\"solidCount\":1}," +
                "\"areas\":[{\"alias\":\"A\",\"faceIds\":[1]}]," +
                "\"steps\":[{\"id\":\"1\",\"action\":\"checkElectrical\",\"electrical\":{\"presetIndex\":2,\"voltageV\":220,\"reviewScope\":0}}]" +
                "}";
            string scenarioPath = temp.WriteFile("scenario.json", json);

            VisualScenario scenario = VisualScenario.Load(scenarioPath, temp.Path, out string error);

            Assert.Null(scenario);
            Assert.Contains("presetIndex", error);
        }

        [Fact]
        public void Load_RejectsDuplicateV2Alias()
        {
            using var temp = new TempDirectory();
            string model = temp.WriteFile("model.stp", "test-model");
            string json = "{" +
                "\"schemaVersion\":2," +
                "\"name\":\"v2-test\"," +
                "\"model\":{\"path\":\"model.stp\",\"sha256\":\"" + VisualScenario.ComputeSha256(model) + "\",\"faceCount\":2,\"solidCount\":1}," +
                "\"areas\":[" +
                "{\"alias\":\"A\",\"faceIds\":[1]}," +
                "{\"alias\":\"A\",\"faceIds\":[2]}]," +
                "\"steps\":[{\"id\":\"1\",\"action\":\"selectFaces\",\"faces\":[1]}]" +
                "}";
            string scenarioPath = temp.WriteFile("scenario.json", json);

            VisualScenario scenario = VisualScenario.Load(scenarioPath, temp.Path, out string error);

            Assert.Null(scenario);
            Assert.Contains("alias", error);
        }

        private static string WriteScenario(TempDirectory temp, string model, string sha256,
            int schemaVersion = 2, string faceIds = "[1, 2]",
            string electrical = "\"presetIndex\": 0, \"voltageV\": 220, \"reviewScope\": 0")
        {
            string json = "{" +
                "\"schemaVersion\":" + schemaVersion + "," +
                "\"name\":\"test\"," +
                "\"model\":{\"path\":\"model.stp\",\"sha256\":\"" + sha256 + "\",\"faceCount\":2,\"solidCount\":1}," +
                "\"areas\":[{\"alias\":\"A\",\"faceIds\":" + faceIds + ",\"marginMm\":5,\"spacingMm\":10,\"direction\":0}]," +
                "\"steps\":[{\"id\":\"1\",\"action\":\"checkElectrical\",\"electrical\":{" + electrical + "}}]" +
                "}";
            return temp.WriteFile("scenario.json", json);
        }
    }

    public sealed class VisualTestOptionsTests
    {
        [Fact]
        public void Parse_WithoutVisualOption_LeavesNormalAppUntouched()
        {
            VisualTestOptions options = VisualTestOptions.Parse(new[] { "file.stp" });

            Assert.False(options.Enabled);
            Assert.Null(options.Error);
        }

        [Fact]
        public void Parse_AcceptsAbsoluteRunDirectoryAndDefaults()
        {
            using var temp = new TempDirectory();
            string scenario = temp.WriteFile("scenario.json", "{}");
            string run = Path.Combine(temp.Path, "run");

            VisualTestOptions options = VisualTestOptions.Parse(new[]
            {
                "--visual-test", scenario, "--visual-run-dir", run
            });

            Assert.True(options.Enabled);
            Assert.Null(options.Error);
            Assert.Equal(Path.GetFullPath(run), options.RunDirectory);
            Assert.Equal(1, options.HoldSeconds);
            Assert.False(options.StepMode);
        }

        [Fact]
        public void Parse_RejectsRelativeRunDirectory()
        {
            using var temp = new TempDirectory();
            string scenario = temp.WriteFile("scenario.json", "{}");

            VisualTestOptions options = VisualTestOptions.Parse(new[]
            {
                "--visual-test", scenario, "--visual-run-dir", "relative/run"
            });

            Assert.True(options.Enabled);
            Assert.Contains("절대 경로", options.Error);
        }

        [Fact]
        public void Parse_KeepsRequestedRunDirectoryWhenScenarioIsMissing()
        {
            using var temp = new TempDirectory();
            string missing = Path.Combine(temp.Path, "missing.json");
            string run = Path.Combine(temp.Path, "run");

            VisualTestOptions options = VisualTestOptions.Parse(new[]
            {
                "--visual-test", missing, "--visual-run-dir", run
            });

            Assert.True(options.Enabled);
            Assert.Contains("찾을 수 없습니다", options.Error);
            Assert.Equal(Path.GetFullPath(run), options.RunDirectory);
        }

        [Fact]
        public void Parse_RejectsDuplicateOption()
        {
            using var temp = new TempDirectory();
            string scenario = temp.WriteFile("scenario.json", "{}");

            VisualTestOptions options = VisualTestOptions.Parse(new[]
            {
                "--visual-test", scenario, "--visual-test", scenario
            });

            Assert.True(options.Enabled);
            Assert.Contains("중복", options.Error);
        }

        [Fact]
        public void Parse_RejectsHoldOutsideRange()
        {
            using var temp = new TempDirectory();
            string scenario = temp.WriteFile("scenario.json", "{}");

            VisualTestOptions options = VisualTestOptions.Parse(new[]
            {
                "--visual-test", scenario, "--visual-hold-seconds", "61"
            });

            Assert.True(options.Enabled);
            Assert.Contains("0~60", options.Error);
        }
    }

    public sealed class VisualTestReportTests
    {
        [Fact]
        public void Finish_WritesAtomicResultWithSteps()
        {
            using var temp = new TempDirectory();
            var report = new VisualTestReport(temp.Path, new VisualScenario
            {
                Name = "test",
                Model = new VisualModelSpec { FaceCount = 1, SolidCount = 1 },
                ResolvedModelPath = "model.stp",
                ActualModelSha256 = new string('a', 64)
            });
            report.WriteRun();
            report.StartStep(0, "first");
            report.EndStep(new VisualStepRecord
            {
                Index = 0,
                Name = "first",
                Success = true,
                Detail = "ok"
            });

            report.Finish(new VisualTestRunResult { Status = "Completed", Automation = "Completed" });

            string path = Path.Combine(temp.Path, "result.json");
            Assert.True(File.Exists(path));
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("Completed", document.RootElement.GetProperty("Status").GetString());
            Assert.Equal(1, document.RootElement.GetProperty("Steps").GetArrayLength());
            Assert.True(File.Exists(Path.Combine(temp.Path, "steps.jsonl")));
        }

        [Fact]
        public void Finish_RefusesToOverwriteExistingResult()
        {
            using var temp = new TempDirectory();
            File.WriteAllText(Path.Combine(temp.Path, "result.json"), "{}");
            var report = new VisualTestReport(temp.Path, new VisualScenario { Name = "test" });

            Assert.Throws<IOException>(() =>
                report.Finish(new VisualTestRunResult { Status = "Completed" }));
        }
    }

    public sealed class VisualTestRunnerTests
    {
        [Fact]
        public async Task Run_StopsAfterFailureAndDoesNotExecuteLaterSteps()
        {
            using var temp = new TempDirectory();
            var report = new VisualTestReport(temp.Path, new VisualScenario { Name = "test" });
            int executed = 0;
            var runner = new VisualTestRunner(
                new[] { "first", "second", "third" },
                (index, token) =>
                {
                    executed++;
                    return Task.FromResult(index == 0
                        ? VisualStepOutcome.Ok("first ok")
                        : VisualStepOutcome.Fail("intentional failure"));
                },
                report, holdSeconds: 0, stepMode: false, takeReportedError: () => null);

            runner.Start();
            await runner.Completion;

            Assert.Equal(2, executed);
            string json = File.ReadAllText(Path.Combine(temp.Path, "result.json"));
            using JsonDocument document = JsonDocument.Parse(json);
            Assert.Equal("Failed", document.RootElement.GetProperty("Status").GetString());
            Assert.Equal(2, document.RootElement.GetProperty("Steps").GetArrayLength());
            Assert.Contains("intentional failure", document.RootElement.GetProperty("Error").GetString());
        }

        [Fact]
        public async Task RequestNext_IsIgnoredWhilePaused()
        {
            using var temp = new TempDirectory();
            var report = new VisualTestReport(temp.Path, new VisualScenario { Name = "test" });
            int executed = 0;
            var runner = new VisualTestRunner(
                new[] { "first", "second" },
                (index, token) =>
                {
                    executed++;
                    return Task.FromResult(VisualStepOutcome.Ok("ok " + index));
                },
                report, holdSeconds: 0, stepMode: true, takeReportedError: () => null);

            runner.Start();
            for (int i = 0; i < 200 && runner.State != VisualTestRunnerState.WaitingForNext; i++)
                await Task.Delay(10);
            Assert.Equal(VisualTestRunnerState.WaitingForNext, runner.State);

            runner.Pause();
            runner.RequestNext();
            await Task.Delay(150);
            Assert.Equal(1, executed);

            runner.Resume();
            runner.RequestNext();
            for (int i = 0; i < 200 && (executed < 2 || runner.State != VisualTestRunnerState.WaitingForNext); i++)
                await Task.Delay(10);
            Assert.Equal(2, executed);
            Assert.Equal(VisualTestRunnerState.WaitingForNext, runner.State);

            runner.RequestNext();
            await runner.Completion;

            Assert.Equal(2, executed);
            Assert.Equal(VisualTestRunnerState.Completed, runner.State);
        }

        [Fact]
        public async Task Run_CompletesAllSteps()
        {
            using var temp = new TempDirectory();
            var report = new VisualTestReport(temp.Path, new VisualScenario { Name = "test" });
            int executed = 0;
            var runner = new VisualTestRunner(
                new[] { "first", "second" },
                (index, token) =>
                {
                    executed++;
                    return Task.FromResult(VisualStepOutcome.Ok("ok " + index));
                },
                report, holdSeconds: 0, stepMode: false, takeReportedError: () => null);

            runner.Start();
            await runner.Completion;

            Assert.Equal(2, executed);
            string json = File.ReadAllText(Path.Combine(temp.Path, "result.json"));
            using JsonDocument document = JsonDocument.Parse(json);
            Assert.Equal("Completed", document.RootElement.GetProperty("Status").GetString());
            Assert.Equal("second", document.RootElement.GetProperty("LastCompletedStep").GetString());
        }

        [Fact]
        public async Task ConfirmationPolicy_UsesQueueOnlyWhileTheRunIsActive()
        {
            using var temp = new TempDirectory();
            var report = new VisualTestReport(temp.Path, new VisualScenario { Name = "test" });
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var runner = new VisualTestRunner(
                new[] { "first" },
                async (index, token) =>
                {
                    await gate.Task;
                    return VisualStepOutcome.Ok("ok");
                },
                report, holdSeconds: 0, stepMode: false, takeReportedError: () => null);

            // 세션이 없거나 실행기가 아직 만들어지지 않았으면 자동 응답을 쓰지 않는다.
            Assert.False(VisualTestConfirmationPolicy.UsesQueuedResponses(false, runner));
            Assert.False(VisualTestConfirmationPolicy.UsesQueuedResponses(true, null));

            runner.Start();
            Assert.True(VisualTestConfirmationPolicy.UsesQueuedResponses(true, runner));

            gate.SetResult(true);
            await runner.Completion;

            // 실행이 끝나면 큐는 비어 있으므로 제품 대화상자로 돌아가야 한다.
            Assert.Equal(VisualTestRunnerState.Completed, runner.State);
            Assert.False(VisualTestConfirmationPolicy.UsesQueuedResponses(true, runner));
        }
    }

    internal sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HeatingMoldTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string WriteFile(string name, string content)
        {
            string path = System.IO.Path.Combine(Path, name);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (Exception) { }
        }
    }
}
