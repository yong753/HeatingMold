using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using HeatingMold.App.Persistence;
using Xunit;

namespace HeatingMold.App.Tests
{
    public sealed class ProjectJsonStoreTests
    {
        [Fact]
        public void FingerprintIgnoresSaveTimeAndSourceLocation()
        {
            ProjectDocument first = CreateDocument();
            first.CreatedUtc = "2026-01-01T00:00:00.0000000Z";
            first.SavedUtc = "2026-01-01T00:00:00.0000000Z";
            first.Source.RelativePath = "old/mold.stp";
            first.Source.OriginalPathHint = @"C:\old\mold.stp";

            string firstFingerprint = ProjectJsonStore.ComputeFingerprint(first);

            first.CreatedUtc = "2026-02-02T00:00:00.0000000Z";
            first.SavedUtc = "2026-02-02T00:00:00.0000000Z";
            first.Source.RelativePath = "../new/mold.stp";
            first.Source.OriginalPathHint = @"D:\new\mold.stp";

            Assert.Equal(firstFingerprint, ProjectJsonStore.ComputeFingerprint(first));
        }

        [Fact]
        public void FingerprintChangesWhenAreaContentChanges()
        {
            ProjectDocument document = CreateDocument();
            string before = ProjectJsonStore.ComputeFingerprint(document);
            document.Areas[0].MarginText = "7.5";
            Assert.NotEqual(before, ProjectJsonStore.ComputeFingerprint(document));
        }

        [Fact]
        public void SaveWritesStrictJsonAndReturnsMatchingHash()
        {
            string directory = Path.Combine(Path.GetTempPath(), "HeatingMold.ProjectTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "work.json");
                ProjectDocument document = CreateDocument();

                ProjectSaveResult result = ProjectJsonStore.Save(document, path);

                Assert.True(File.Exists(path));
                Assert.Equal(ProjectJsonStore.ComputeFileSha256(path), result.Sha256);
                Assert.Empty(Directory.GetFiles(directory, "*.tmp"));

                using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(path));
                JsonElement root = json.RootElement;
                Assert.Equal("HeatingMold.Project", root.GetProperty("format").GetString());
                Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
                Assert.Equal(1, root.GetProperty("areas").GetArrayLength());
                Assert.Equal("A", root.GetProperty("areas")[0].GetProperty("name").GetString());
            }
            finally
            {
                try { Directory.Delete(directory, recursive: true); } catch { }
            }
        }

        [Fact]
        public void LoadReadsSavedDocument()
        {
            string directory = Path.Combine(Path.GetTempPath(), "HeatingMold.ProjectTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "work.json");
                ProjectDocument document = CreateDocument();
                ProjectJsonStore.Save(document, path);

                ProjectDocument loaded = ProjectJsonStore.Load(path);

                Assert.Equal(document.Format, loaded.Format);
                Assert.Equal(document.SchemaVersion, loaded.SchemaVersion);
                Assert.Equal(ProjectJsonStore.ComputeFingerprint(document),
                    ProjectJsonStore.ComputeFingerprint(loaded));
                Assert.Equal("A", loaded.Areas[0].Name);
            }
            finally
            {
                try { Directory.Delete(directory, recursive: true); } catch { }
            }
        }

        [Fact]
        public void LoadRejectsWrongFormat()
        {
            string directory = Path.Combine(Path.GetTempPath(), "HeatingMold.ProjectTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "work.json");
                ProjectDocument document = CreateDocument();
                document.Format = "Other.Project";
                File.WriteAllBytes(path, ProjectJsonStore.Serialize(document));

                Assert.Throws<InvalidDataException>(() => ProjectJsonStore.Load(path));
            }
            finally
            {
                try { Directory.Delete(directory, recursive: true); } catch { }
            }
        }

        [Fact]
        public void LoadRejectsFutureSchemaAndMalformedJson()
        {
            string directory = Path.Combine(Path.GetTempPath(), "HeatingMold.ProjectTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string futurePath = Path.Combine(directory, "future.json");
                File.WriteAllText(futurePath,
                    "{\"format\":\"HeatingMold.Project\",\"schemaVersion\":2}");
                Assert.Throws<InvalidDataException>(() => ProjectJsonStore.Load(futurePath));

                string malformedPath = Path.Combine(directory, "malformed.json");
                File.WriteAllText(malformedPath, "{ not json");
                Assert.Throws<InvalidDataException>(() => ProjectJsonStore.Load(malformedPath));
            }
            finally
            {
                try { Directory.Delete(directory, recursive: true); } catch { }
            }
        }

        [Fact]
        public void FaceTopologySignaturesAreSerialized()
        {
            ProjectDocument document = CreateDocument();
            document.Source.FaceTopologySignatures = new List<ProjectFaceTopologySignature>
            {
                new() { FaceId = 0, Signature = "face-a" },
                new() { FaceId = 3, Signature = "face-b" }
            };
            document.Source.TopologyIdentityError = null;

            byte[] json = ProjectJsonStore.Serialize(document);
            using JsonDocument parsed = JsonDocument.Parse(json);
            JsonElement signatures = parsed.RootElement
                .GetProperty("source").GetProperty("faceTopologySignatures");
            Assert.Equal(2, signatures.GetArrayLength());
            Assert.Equal(0, signatures[0].GetProperty("faceId").GetInt32());
            Assert.Equal("face-a", signatures[0].GetProperty("signature").GetString());
            Assert.Equal(3, signatures[1].GetProperty("faceId").GetInt32());
            Assert.Equal("face-b", signatures[1].GetProperty("signature").GetString());
        }

        [Fact]
        public void SaveReplacesExistingFileWithoutLeavingTemporaryFile()
        {
            string directory = Path.Combine(Path.GetTempPath(), "HeatingMold.ProjectTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "work.json");
                File.WriteAllText(path, "old");

                ProjectDocument document = CreateDocument();
                ProjectJsonStore.Save(document, path);

                Assert.Contains("HeatingMold.Project", File.ReadAllText(path));
                Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            }
            finally
            {
                try { Directory.Delete(directory, recursive: true); } catch { }
            }
        }

        [Fact]
        public void SaveRejectsNonJsonExtension()
        {
            ProjectDocument document = CreateDocument();
            Assert.Throws<InvalidOperationException>(
                () => ProjectJsonStore.Save(document, Path.Combine(Path.GetTempPath(), "work.stp")));
        }

        [Fact]
        public void SaveRejectsSourceStepPath()
        {
            Assert.Throws<InvalidOperationException>(
                () => ProjectJsonStore.EnsureNotSourceFile(@"C:\work\mold.stp", @"C:\work\mold.stp"));
        }

        [Fact]
        public void SerializeRejectsNonFiniteNumbers()
        {
            ProjectDocument document = CreateDocument();
            document.Areas[0].RoutePieceLengths = new[] { double.NaN };
            Assert.Throws<InvalidOperationException>(() => ProjectJsonStore.Serialize(document));
        }

        [Fact]
        public void SaveRejectsNonFiniteNumbersWithoutTouchingExistingFile()
        {
            string directory = Path.Combine(Path.GetTempPath(), "HeatingMold.ProjectTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "work.json");
                File.WriteAllText(path, "previous-project");

                ProjectDocument document = CreateDocument();
                document.Areas[0].RoutePieceLengths = new[] { double.PositiveInfinity };

                Assert.Throws<InvalidOperationException>(() => ProjectJsonStore.Save(document, path));
                Assert.Equal("previous-project", File.ReadAllText(path));
                Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            }
            finally
            {
                try { Directory.Delete(directory, recursive: true); } catch { }
            }
        }

        private static ProjectDocument CreateDocument()
        {
            return new ProjectDocument
            {
                AppVersion = "test",
                Source = new ProjectSourceDocument
                {
                    FileName = "mold.stp",
                    Sha256 = "abc123",
                    ModelRevision = 7
                },
                WorkingSide = new ProjectWorkingSideDocument
                {
                    Frame = Frame(),
                    ModelRevision = 7
                },
                Areas =
                {
                    new ProjectAreaDocument
                    {
                        AreaId = 1,
                        Name = "A",
                        Color = new ProjectColor { R = 255, G = 0, B = 0, A = 255 },
                        FaceIds = new[] { 3, 4 },
                        Region = { new System.Collections.Generic.List<ProjectVec2>
                        {
                            new ProjectVec2 { X = 0, Y = 0 },
                            new ProjectVec2 { X = 10, Y = 0 },
                            new ProjectVec2 { X = 10, Y = 10 }
                        } },
                        Frame = Frame(),
                        Direction = "auto",
                        MarginText = "5",
                        SpacingText = "50",
                        CommittedChains =
                        {
                            new ProjectCommittedChain
                            {
                                NativePieceId = -100,
                                Points =
                                {
                                    new ProjectVec3 { X = 0, Y = 0, Z = 0 },
                                    new ProjectVec3 { X = 10, Y = 0, Z = 0 }
                                }
                            }
                        },
                        ChainsAvailable = true
                    }
                },
                Electrical = new ProjectElectricalDocument
                {
                    WirePresetIndex = 0,
                    WireDiameterText = "1.0",
                    WireResistanceText = "0.1",
                    AppliedVoltageText = "220"
                },
                Review = new ProjectReviewDocument { CurrentAreaOnly = false }
            };
        }

        private static ProjectFrame Frame()
        {
            return new ProjectFrame
            {
                Origin = new ProjectVec3 { X = 0, Y = 0, Z = 0 },
                AxisX = new ProjectVec3 { X = 1, Y = 0, Z = 0 },
                AxisY = new ProjectVec3 { X = 0, Y = 1, Z = 0 },
                Normal = new ProjectVec3 { X = 0, Y = 0, Z = 1 }
            };
        }
    }
}
