using System;
using HeatingMold.App.VisualTesting;
using HeatingMold.Core.Geometry;
using HeatingMold.OccBridge;
using Xunit;

namespace HeatingMold.App.Tests
{
    public sealed class VisualRouteSnapshotTests
    {
        private static VisualRouteSnapshot CreateSnapshot()
        {
            return new VisualRouteSnapshot(
                AreaId: 7,
                Name: "A",
                ColorArgb: unchecked((int)0xFF3366CC),
                FaceIds: new[] { 44, 56, 69 },
                Region: new[] { new[] { new Vec2(0, 0), new Vec2(10, 0), new Vec2(10, 10), new Vec2(0, 10) } },
                Exclusions: Array.Empty<VisualExclusionSnapshot>(),
                Direction: 1,
                AppliedOuterGapMm: 5,
                AppliedWireDiameterMm: 15,
                AppliedDirection: 1,
                AppliedRouteContextKey: "key-1",
                CircuitCount: 1,
                CircuitPieceIndices: new[] { new[] { 0, 1 } },
                Pieces: new[]
                {
                    new VisualRoutePieceSnapshot(
                        Index: 0,
                        Kind: SerpentineRoutePieceKind.HeatingRun,
                        LaneIndex: 0,
                        PlaneLengthMm: 100,
                        NativePieceId: 501,
                        ActualLengthMm: 101.5,
                        PlanePoints: new[] { new Vec2(1, 1), new Vec2(1, 9) },
                        SurfacePoints: new[] { new Vec3(1, 1, 2), new Vec3(1, 9, 2) },
                        SupportFaceIds: new[] { 44 }),
                    new VisualRoutePieceSnapshot(
                        Index: 1,
                        Kind: SerpentineRoutePieceKind.TurnConnector,
                        LaneIndex: 0,
                        PlaneLengthMm: 10,
                        NativePieceId: 502,
                        ActualLengthMm: 10.25,
                        PlanePoints: new[] { new Vec2(1, 9), new Vec2(3, 9) },
                        SurfacePoints: new[] { new Vec3(1, 9, 2), new Vec3(3, 9, 2) },
                        SupportFaceIds: new[] { 44 })
                },
                StartExit: CreateExit(0, 1.5),
                EndExit: CreateExit(1, -2.5),
                ExitChain: new[] { new Vec2(0, 1), new Vec2(10, 1) },
                ExitChain3D: new[] { new Vec3(0, 1, 2), new Vec3(10, 1, 2) },
                SplitCutLines: Array.Empty<Vec2[]>(),
                PatternDisplayed: true,
                RouteEdited: false);
        }

        private static VisualExitSnapshot CreateExit(int role, double u)
        {
            return new VisualExitSnapshot(
                Role: role,
                Point: new Vec2(u, 3),
                SegmentIndex: 0,
                SegmentParameter: 0.25,
                Binding: new VisualBindingSnapshot(
                    Kind: 1,
                    ModelRevision: 11,
                    ContextId: 3,
                    ContextRevision: 4,
                    EdgeKey: 19,
                    FirstParameter: 0,
                    LastParameter: 1,
                    Parameter: 0.25,
                    Reversed: false,
                    MinX: 0,
                    MinY: 0,
                    MaxX: 0,
                    MaxY: 0,
                    Side: 0,
                    AX: 0,
                    AY: 0,
                    BX: 0,
                    BY: 0));
        }

        [Fact]
        public void IsUnchanged_AcceptsAnIdenticalSnapshot()
        {
            Assert.True(VisualRouteSnapshotComparer.IsUnchanged(CreateSnapshot(), CreateSnapshot(), out string difference));
            Assert.Equal(string.Empty, difference);
        }

        [Fact]
        public void IsUnchanged_DetectsAMovedSurfacePoint()
        {
            VisualRouteSnapshot before = CreateSnapshot();
            VisualRouteSnapshot after = CreateSnapshot() with
            {
                Pieces = new[]
                {
                    before.Pieces[0] with { SurfacePoints = new[] { new Vec3(1, 1, 2.5), new Vec3(1, 9, 2) } },
                    before.Pieces[1]
                }
            };

            Assert.False(VisualRouteSnapshotComparer.IsUnchanged(before, after, out string difference));
            Assert.Contains("3D 좌표", difference);
        }

        [Fact]
        public void IsUnchanged_DetectsAChangedNativePieceId()
        {
            VisualRouteSnapshot before = CreateSnapshot();
            VisualRouteSnapshot after = CreateSnapshot() with
            {
                Pieces = new[] { before.Pieces[0] with { NativePieceId = 999 }, before.Pieces[1] }
            };

            Assert.False(VisualRouteSnapshotComparer.IsUnchanged(before, after, out string difference));
            Assert.Contains("native", difference);
        }

        [Fact]
        public void IsUnchanged_DetectsAPieceLengthChangeThatKeepsTheTotal()
        {
            VisualRouteSnapshot before = CreateSnapshot();
            VisualRouteSnapshot after = CreateSnapshot() with
            {
                Pieces = new[]
                {
                    before.Pieces[0] with { ActualLengthMm = 102.5 },
                    before.Pieces[1] with { ActualLengthMm = 9.25 }
                }
            };

            double beforeTotal = before.Pieces[0].ActualLengthMm + before.Pieces[1].ActualLengthMm;
            double afterTotal = after.Pieces[0].ActualLengthMm + after.Pieces[1].ActualLengthMm;
            Assert.Equal(beforeTotal, afterTotal, 6);

            Assert.False(VisualRouteSnapshotComparer.IsUnchanged(before, after, out string difference));
            Assert.Contains("확정 3D 길이", difference);
        }

        [Fact]
        public void IsUnchanged_DetectsAChangedBindingParameter()
        {
            VisualRouteSnapshot before = CreateSnapshot();
            VisualExitSnapshot start = before.StartExit!;
            VisualRouteSnapshot after = CreateSnapshot() with
            {
                StartExit = start with
                {
                    Binding = start.Binding! with { Parameter = 0.75 }
                }
            };

            Assert.False(VisualRouteSnapshotComparer.IsUnchanged(before, after, out string difference));
            Assert.Contains("시작 인출점", difference);
        }

        [Fact]
        public void IsUnchanged_DetectsAnAddedExclusion()
        {
            VisualRouteSnapshot before = CreateSnapshot();
            VisualRouteSnapshot after = CreateSnapshot() with
            {
                Exclusions = new[] { new VisualExclusionSnapshot(0, 0, 1, 1) }
            };

            Assert.False(VisualRouteSnapshotComparer.IsUnchanged(before, after, out string difference));
            Assert.Contains("제외영역", difference);
        }

        [Fact]
        public void IsUnchanged_RequiresBothSnapshots()
        {
            Assert.False(VisualRouteSnapshotComparer.IsUnchanged(null!, CreateSnapshot(), out string difference));
            Assert.False(string.IsNullOrEmpty(difference));
        }

        [Fact]
        public void Load_AcceptsHeatingOnlyExpectations()
        {
            using var temp = new TempDirectory();
            string model = temp.WriteFile("model.stp", "test-model");
            string json = "{ \"schemaVersion\": 3, \"name\": \"t\", \"model\": { \"path\": \"" +
                          model.Replace("\\", "\\\\") + "\", \"sha256\": \"" +
                          VisualScenario.ComputeSha256(model) +
                          "\", \"faceCount\": 80, \"solidCount\": 0 }, " +
                          "\"areas\": [ { \"alias\": \"A\", \"faceIds\": [44], " +
                          "\"expect\": { \"heatingPieces\": 30, \"heatingLengthMm\": 17323.549 } } ], " +
                          "\"steps\": [ { \"id\": \"s1\", \"action\": \"checkArea\", \"area\": \"A\" } ] }";
            string scenarioPath = temp.WriteFile("scenario.json", json);

            VisualScenario scenario = VisualScenario.Load(scenarioPath, temp.Path, out string error);

            Assert.Null(error);
            Assert.NotNull(scenario);
            Assert.Equal(30, scenario!.Areas[0].Expect!.HeatingPieces);
            Assert.Equal(17323.549, scenario.Areas[0].Expect!.HeatingLengthMm);
        }

        [Fact]
        public void Load_RejectsNegativeHeatingExpectations()
        {
            using var temp = new TempDirectory();
            string model = temp.WriteFile("model.stp", "test-model");
            string json = "{ \"schemaVersion\": 3, \"name\": \"t\", \"model\": { \"path\": \"" +
                          model.Replace("\\", "\\\\") + "\", \"sha256\": \"" +
                          VisualScenario.ComputeSha256(model) +
                          "\", \"faceCount\": 80, \"solidCount\": 0 }, " +
                          "\"areas\": [ { \"alias\": \"A\", \"faceIds\": [44], " +
                          "\"expect\": { \"heatingPieces\": -1 } } ], " +
                          "\"steps\": [ { \"id\": \"s1\", \"action\": \"checkArea\", \"area\": \"A\" } ] }";
            string scenarioPath = temp.WriteFile("scenario.json", json);

            VisualScenario scenario = VisualScenario.Load(scenarioPath, temp.Path, out string error);

            Assert.Null(scenario);
            Assert.Contains("heating", error);
        }
    }
}
