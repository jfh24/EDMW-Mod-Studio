using System.Text.RegularExpressions;
using System.Text.Json;
using System.IO.Compression;
using EDMWMapDesigner.Collaboration;
using EDMWMapDesigner.Generation;
using EDMWMapDesigner.Models;
using EDMWMapDesigner.UI;

namespace EDMWMapDesigner;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0].Equals("--verify-online-ui", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                ApplicationConfiguration.Initialize();
                OnlineDialogVerification.Verify(args[1]);
                Console.WriteLine("ONLINE UI PASS: Friends, recipient picker, and toolbar layout.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("ONLINE UI FAIL: " + error);
                return 1;
            }
        }
        if (args.Length is 2 or 3 && args[0].Equals("--verify-online", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Networking.OnlineVerification.RunAsync(args[1], args.Length == 3 ? args[2] : null).GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("ONLINE FAIL: " + error);
                return 1;
            }
        }
        if (args.Length == 1 && args[0].Equals("--verify-civilization-navigation", StringComparison.OrdinalIgnoreCase))
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            ApplicationConfiguration.Initialize();
            return CivilizationEditorVerification.RunNavigation();
        }
        if (args.Length == 3 && args[0].Equals("--verify-age-progression-layout", StringComparison.OrdinalIgnoreCase))
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            ApplicationConfiguration.Initialize();
            return AgeProgressionVerification.RunLayout(args[1], args[2]);
        }
        if (args.Length == 3 && args[0].Equals("--verify-age-progression", StringComparison.OrdinalIgnoreCase))
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            ApplicationConfiguration.Initialize();
            return AgeProgressionVerification.Run(args[1], args[2]);
        }
        if (args.Length == 3 && args[0].Equals("--verify-map-lan", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                ApplicationConfiguration.Initialize();
                var project = MapProject.CreateDefault(16);
                project.Name = "LAN verification map";
                for (var y = 3; y < 13; y++)
                    for (var x = 3; x < 13; x++)
                        project.SetCell(x, y, TerrainKind.Grassland);
                var package = MainForm.PrepareMapLanPackage(project, args[1], hidePreview: false);
                var hidden = MainForm.PrepareMapLanPackage(project, args[1], hidePreview: true);
                if (package.MapId != hidden.MapId || package.PreviewBytes.SequenceEqual(hidden.PreviewBytes))
                    throw new InvalidOperationException("Hiding the preview must redact the image while preserving map identity.");
                var checks = CivilizationLanDialogVerification.VerifyAutomaticSending(args[2])
                    .Concat(LanExportIntegrationVerification.Verify(args[2]))
                    .Concat(MapLanDialogVerification.Verify(package, args[1]))
                    .Concat(MapLanDialogVerification.VerifyApproval(package, args[2]))
                    .Concat(MapLanDialogVerification.VerifySender(package, args[2]))
                    .Concat(MapLanVerification.Run(args[2]).GetAwaiter().GetResult());
                Console.WriteLine("MAP LAN PASS: " + string.Join("; ", checks));
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("MAP LAN FAIL: " + error);
                return 1;
            }
        }
        if (args.Length == 3 && args[0].Equals("--verify-civilization-models", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                ApplicationConfiguration.Initialize();
                var checks = CivilizationModelVerification.Run(args[1], args[2]);
                Console.WriteLine("CIVILIZATION MODELS PASS: " + string.Join("; ", checks));
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("CIVILIZATION MODELS FAIL: " + error);
                return 1;
            }
        }
        if (args.Length == 3 && args[0].Equals("--verify-civilization-lan", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                ApplicationConfiguration.Initialize();
                var project = Civilizations.CivilizationEditProject.CreateDefault();
                var objects = File.ReadAllBytes(Path.Combine(args[1], "Data", "db", "dbobjects.dat"));
                project.Civilizations.Single(civ => civ.SourceRowIndex == 10).Entities.Add(new()
                {
                    Name = "Citizen", Category = Civilizations.CivilizationCategory.Units, SourceObjectId = 833,
                    Properties = new(StringComparer.Ordinal)
                    {
                        ["unit.health"] = checked(BitConverter.ToUInt32(objects, 4 + 833 * 1936 + 120) + 1)
                            .ToString(System.Globalization.CultureInfo.InvariantCulture)
                    }
                });
                var package = Civilizations.CivilizationInstallerPackage.Create(project, args[1]);
                var uiChecks = CivilizationLanDialogVerification.Verify(package, args[1]);
                var approvalChecks = CivilizationLanDialogVerification.VerifyApproval(args[1], Path.Combine(args[2], "ui-approval"));
                var checks = uiChecks.Concat(approvalChecks)
                    .Concat(Civilizations.CivilizationLanVerification.Run(args[1], args[2]).GetAwaiter().GetResult());
                Console.WriteLine("CIVILIZATION LAN PASS: " + string.Join("; ", checks));
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("CIVILIZATION LAN FAIL: " + error);
                return 1;
            }
        }
        if (args.Length == 3 && args[0].Equals("--verify-civilization-sources", StringComparison.OrdinalIgnoreCase))
            return Civilizations.CivilizationSourcesVerification.Run(args[1], args[2]);
        if (args.Length == 3 && args[0].Equals("--verify-civilization-packages", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var checks = Civilizations.CivilizationPackageVerification.Run(args[1], args[2])
                    .Concat(Civilizations.CivilizationPackageAdversarialVerification.Run(args[1], Path.Combine(args[2], "adversarial")));
                Console.WriteLine("CIVILIZATION PACKAGES PASS: " + string.Join("; ", checks));
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("CIVILIZATION PACKAGES FAIL: " + exception);
                return 1;
            }
        }
        if (args.Length == 3 && args[0].Equals("--verify-civilization-repaint", StringComparison.OrdinalIgnoreCase))
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            ApplicationConfiguration.Initialize();
            return CivilizationRepaintVerification.Run(args[1], args[2]);
        }
        if (args.Length == 3 && args[0].Equals("--verify-civilization-editor", StringComparison.OrdinalIgnoreCase))
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            ApplicationConfiguration.Initialize();
            return CivilizationEditorVerification.Run(args[1], args[2]);
        }
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
            return RunSelfTest();
        if (args.Length == 2 && args[0].Equals("--verify-stock-profiles", StringComparison.OrdinalIgnoreCase))
            return VerifyStockProfiles(args[1]);
        if (args.Length == 3 && args[0].Equals("--upgrade-resource-policy", StringComparison.OrdinalIgnoreCase))
            return UpgradeResourcePolicy(args[1], args[2]);
        if (args.Length == 3 && args[0].Equals("--build-fauna-evidence-probe", StringComparison.OrdinalIgnoreCase))
            return BuildFaunaEvidenceProbe(args[1], args[2]);
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    private static int BuildFaunaEvidenceProbe(string outputFolder, string stockScriptsFolder)
    {
        try
        {
            var artifacts = FaunaEvidenceProbeBuilder.Build(outputFolder, stockScriptsFolder);
            Console.WriteLine("FAUNA EVIDENCE PROBE READY");
            Console.WriteLine("Installer: " + artifacts.InstallerPath);
            Console.WriteLine("Manifest: " + artifacts.ManifestPath);
            Console.WriteLine("Instructions: " + artifacts.InstructionsPath);
            Console.WriteLine("Package: " + artifacts.PackagePath);
            Console.WriteLine("Project: " + artifacts.ProjectPath);
            Console.WriteLine("Script: " + artifacts.ScriptPath);
            Console.WriteLine("Payload: " + artifacts.PayloadPath);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAUNA EVIDENCE PROBE FAIL: " + ex);
            return 1;
        }
    }

    private static int RunSelfTest()
    {
        try
        {
            var project = MapProject.CreateDefault(16);
            for (var y = 3; y < 13; y++)
                for (var x = 3; x < 13; x++)
                    project.SetCell(x, y, x is >= 6 and <= 9 && y is >= 6 and <= 9
                        ? TerrainKind.Hills
                        : TerrainKind.Grassland);

            project.SetCell(7, 7, TerrainKind.Mountain);
            project.SetCell(8, 8, TerrainKind.Forest);
            var automaticElevationId = AnchorPackage.MapId(project);
            project.ManualResources.Add(new ManualResource
                { Kind = ManualResourceKind.Gold, X = 4, Y = 4 });
            if (AnchorPackage.MapId(project) == automaticElevationId)
                throw new InvalidOperationException("Manual resources and forests were omitted from map identity.");
            project.ManualResources.Clear();
            // Saved marker content participates in map identity; the transient
            // drawing-tool toggle is not persisted.
            project.ManualPlayerStarts.Add(new ManualPlayerStart
            {
                PlayerSlot = 1,
                X = 4,
                Y = 4
            });
            var manualStartId = AnchorPackage.MapId(project);
            if (manualStartId == automaticElevationId)
                throw new InvalidOperationException("Manual player starts were omitted from map identity.");
            project.ManualPlayerStarts.Clear();
            project.ElevationMask[7 * project.GridSize + 7] = 85;
            project.ElevationMask[8 * project.GridSize + 8] = 35;
            if (AnchorPackage.MapId(project) == automaticElevationId)
                throw new InvalidOperationException("Painted elevation was omitted from map identity.");
            var result = RmvGenerator.Generate(project);
            var required = new[]
            {
                "UseMapGenerator", "DeclareRandomMapTerrain", "TerrainRegion",
                "RandomMapTerrainRange", "PercentLand", "HeightMapChaos",
                "#include \"Common\\Common Initial Units\"", "TileDistanceBetweenPlayers",
                "RiverLandFill                   false"
            };
            foreach (var token in required)
                if (!result.Script.Contains(token, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Generated script is missing {token}.");

            if (!result.Script.Contains("UseMapGenerator                 \"EDMWAnchored\"", StringComparison.Ordinal) ||
                !result.Script.Contains("ConvergentSampleSize            32", StringComparison.Ordinal) ||
                !result.Script.Contains($"ConvergentThreshold             {AnchorPackage.TransportThreshold(project)}", StringComparison.Ordinal))
                throw new InvalidOperationException("Anchored generator parameters were not exported.");

            var encodedThreshold = float.Parse(AnchorPackage.TransportThreshold(project),
                System.Globalization.CultureInfo.InvariantCulture);
            var encodedBits = BitConverter.SingleToUInt32Bits(encodedThreshold);
            if ((encodedBits & 0x80000000) != 0 || (encodedBits & 0x7F800000) != 0x3F800000 ||
                (encodedBits & AnchorPackage.IdMask) != AnchorPackage.MapId(project))
                throw new InvalidOperationException("The positive-only map ID transport did not round-trip.");

            if (result.Analysis.LandFraction <= 0 || result.Analysis.LandComponents != 1)
                throw new InvalidOperationException("Map analysis produced invalid topology.");
            var selectedProfile = StockResourceProfileSelector.Select(
                project, result.Analysis, Analysis.SemanticAnchorAnalyzer.Analyze(project));
            var stockResourceTokens = new[]
            {
                $"#include \"{selectedProfile.IncludeStem}\\{selectedProfile.IncludeStem} Tiny\"",
                $"#include \"{selectedProfile.IncludeStem}\\{selectedProfile.IncludeStem} Small\"",
                $"#include \"{selectedProfile.IncludeStem}\\{selectedProfile.IncludeStem} Medium\"",
                $"#include \"{selectedProfile.IncludeStem}\\{selectedProfile.IncludeStem} Large\"",
                $"#include \"{selectedProfile.IncludeStem}\\{selectedProfile.IncludeStem} Huge\"",
                "MaxResourceElevation            2",
                "ResourcePerPlayer               (Berry, 1)",
                "ResourcePerNeutralLand          (Berry, 0)",
                "ResourcePerPlayer               (Gold, 5)",
                "ResourcePerNeutralLand          (Gold, Between(0, 10))",
                "ResourcePerPlayer               (Stone, 2)",
                "ResourcePerNeutralLand          (Stone, Between(0, 10))",
                "MaxLoopCount                    200"
            };
            if (stockResourceTokens.Any(token => !result.Script.Contains(token, StringComparison.Ordinal)) ||
                result.Script.Contains("PlaceResourceInRing             ", StringComparison.Ordinal) ||
                result.Script.Contains("ResourcePlacementLimit          ", StringComparison.Ordinal) ||
                result.Script.Contains("ResourceSeperation              ", StringComparison.Ordinal))
                throw new InvalidOperationException("Generated RMV does not delegate resource policy to its selected original profile.");

            VerifyStockProfileSelection();
            VerifyRandomLandGeneration();
            VerifySeededMapGeneration();
            VerifySeededMapVariety();
            VerifyGenerationSeedMetadata();
            VerifyCoastlinePolicyPersistence();
            VerifyUsStateBrushes();
            VerifyUsStateFeatures();
            VerifyCliffPersistence();
            VerifyForcedCliffPersistence();
            VerifyFaunaCapabilityAndEvidenceProbe();
            VerifyCollaborationCompatibility();
            VerifyLanCollaborationAsync().GetAwaiter().GetResult();

            if (result.Script != RmvGenerator.Generate(project).Script)
                throw new InvalidOperationException("Generation is not deterministic for an unchanged project.");

            var regions = Regex.Matches(result.Script, @"TerrainRegion\s+\((\d+),\s*(\d+),\s*\d+\)")
                .Select(match => (Start: int.Parse(match.Groups[1].Value), End: int.Parse(match.Groups[2].Value)))
                .ToArray();
            if (regions.Length == 0 || regions[0].Start != 0 || regions[^1].End != 100 ||
                regions.Zip(regions.Skip(1)).Any(pair => pair.First.End + 1 != pair.Second.Start))
                throw new InvalidOperationException("TerrainRegion ranges do not cover 0-100 continuously.");
            if (result.Script.Contains("NaN", StringComparison.OrdinalIgnoreCase) ||
                result.Script.Contains("Infinity", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Generated script contains a non-finite number.");

            var anchored = MapProject.CreateDefault(16);
            PaintComponent(anchored, 1, 1, 3, 3, SemanticAnchorRole.OuterNeutral);
            PaintComponent(anchored, 6, 6, 9, 9, SemanticAnchorRole.Center);
            PaintComponent(anchored, 11, 2, 14, 5, SemanticAnchorRole.InnerNeutral);
            PaintComponent(anchored, 11, 11, 14, 14, SemanticAnchorRole.PlayerLand);
            var anchorSummary = Analysis.SemanticAnchorAnalyzer.Analyze(anchored);
            var anchoredResult = RmvGenerator.Generate(anchored);
            if (anchorSummary.Count(SemanticAnchorRole.Center) != 1 ||
                anchorSummary.Count(SemanticAnchorRole.InnerNeutral) != 1 ||
                anchorSummary.Count(SemanticAnchorRole.OuterNeutral) != 1 ||
                anchorSummary.Count(SemanticAnchorRole.PlayerLand) != 1 ||
                !anchoredResult.Script.Contains("Semantic anchors: center 1, inner 1, outer 1, player 1", StringComparison.Ordinal) ||
                !anchoredResult.Script.Contains("TeamGrowth                      false", StringComparison.Ordinal) ||
                !anchoredResult.Script.Contains("PlayerInnerRadius               0.55", StringComparison.Ordinal) ||
                !anchoredResult.Script.Contains("PlayerOuterRadius               0.9", StringComparison.Ordinal) ||
                !anchoredResult.Script.Contains("NumInnerNeutrals                2", StringComparison.Ordinal) ||
                !anchoredResult.Script.Contains("NumOuterNeutrals                1", StringComparison.Ordinal))
                throw new InvalidOperationException("Semantic component anchors were not translated into stock RMV zones.");

            var noOuterNeutral = MapProject.CreateDefault(32);
            PaintComponent(noOuterNeutral, 2, 11, 12, 21, SemanticAnchorRole.PlayerLand);
            PaintComponent(noOuterNeutral, 19, 11, 29, 21, SemanticAnchorRole.PlayerLand);
            PaintComponent(noOuterNeutral, 11, 2, 21, 8, SemanticAnchorRole.InnerNeutral);
            PaintComponent(noOuterNeutral, 11, 24, 21, 29, SemanticAnchorRole.InnerNeutral);
            var noOuterResult = RmvGenerator.Generate(noOuterNeutral);
            if (!noOuterResult.Script.Contains("NumInnerNeutrals                2", StringComparison.Ordinal) ||
                !noOuterResult.Script.Contains("NumOuterNeutrals                0", StringComparison.Ordinal))
                throw new InvalidOperationException("Tagged maps generated phantom neutral regions.");

            var untaggedRegions = MapProject.CreateDefault(32);
            PaintComponent(untaggedRegions, 2, 10, 11, 21, SemanticAnchorRole.None);
            PaintComponent(untaggedRegions, 20, 10, 29, 21, SemanticAnchorRole.None);
            var untaggedResult = RmvGenerator.Generate(untaggedRegions);
            if (!untaggedResult.Script.Contains("NumInnerNeutrals                2", StringComparison.Ordinal) ||
                !untaggedResult.Script.Contains("NumOuterNeutrals                0", StringComparison.Ordinal))
                throw new InvalidOperationException("Untagged land generated more neutral resource regions than painted components.");

            var temporaryProject = Path.Combine(Path.GetTempPath(), $"edmw-self-test-{Guid.NewGuid():N}.edmwmap");
            try
            {
                anchored.ElevationMask[0] = -100;
                anchored.ElevationMask[1] = -25;
                anchored.ElevationMask[2] = 0;
                anchored.ElevationMask[3] = 65;
                anchored.ElevationMask[^1] = 100;
                anchored.RampMask[16 * 7 + 7] = 1;
                anchored.RampMask[16 * 8 + 8] = 1;
                anchored.ManualResources.Add(new ManualResource
                    { Kind = ManualResourceKind.Gold, X = 7, Y = 7 });
                anchored.ManualPlayerStarts.Add(new ManualPlayerStart
                {
                    PlayerSlot = 1,
                    X = 7,
                    Y = 7
                });
                anchored.ManualPlayerStarts.Add(new ManualPlayerStart
                {
                    PlayerSlot = 2,
                    X = 12,
                    Y = 12
                });
                var paintedAnchoredScript = RmvGenerator.Generate(anchored).Script;
                if (!paintedAnchoredScript.Contains(
                        "PlayerTreePercentage            1", StringComparison.Ordinal) ||
                    !paintedAnchoredScript.Contains(
                        "InnerNeutralTreePercentage      1", StringComparison.Ordinal) ||
                    !paintedAnchoredScript.Contains(
                        "OuterNeutralTreePercentage      1", StringComparison.Ordinal) ||
                    !paintedAnchoredScript.Contains(
                        "Manual player starts: P1, P2", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Exact tree or manual-start metadata was not generated correctly.");
                anchored.Save(temporaryProject);
                var loaded = MapProject.Load(temporaryProject);
                if (loaded.GridSize != anchored.GridSize || !loaded.Cells.SequenceEqual(anchored.Cells) ||
                    !loaded.ComponentRoles.SequenceEqual(anchored.ComponentRoles) ||
                    !loaded.ElevationMask.SequenceEqual(anchored.ElevationMask) ||
                    !loaded.RampMask.SequenceEqual(anchored.RampMask) ||
                    loaded.ManualResources.Count != 1 || loaded.ManualResources[0].Kind != ManualResourceKind.Gold ||
                    loaded.ManualPlayerStarts.Count != 2 ||
                    loaded.ManualPlayerStarts[0].PlayerSlot != 1 ||
                    loaded.ManualPlayerStarts[1].PlayerSlot != 2)
                    throw new InvalidOperationException("Project save/load did not round-trip.");

                var legacyJson = File.ReadAllText(temporaryProject);
                legacyJson = legacyJson.TrimEnd();
                legacyJson = legacyJson[..^1] +
                    ",\n  \"goldQuantity\": 10,\n  \"stoneQuantity\": 10,\n" +
                    "  \"worldGoldQuantity\": 20,\n  \"worldStoneQuantity\": 20\n}\n";
                File.WriteAllText(temporaryProject, legacyJson);
                _ = MapProject.Load(temporaryProject);
                if (RmvGenerator.Generate(MapProject.Load(temporaryProject)).Script != paintedAnchoredScript)
                    throw new InvalidOperationException("Legacy resource fields still influence RMV generation.");
            }
            finally
            {
                if (File.Exists(temporaryProject)) File.Delete(temporaryProject);
            }

            var temporaryScripts = Path.Combine(Path.GetTempPath(), $"edmw-package-test-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(temporaryScripts);
                var scriptPath = Path.Combine(temporaryScripts, "Self Test.rmv");
                var package = GeneratedMapPackage.WriteForSelfTest(project, scriptPath);
                if (!File.Exists(package.ScriptPath) || !File.Exists(package.PayloadPath) ||
                    new FileInfo(package.PayloadPath).Length != AnchorPackage.HeaderSize +
                        project.Cells.Length * 4L + sizeof(uint) * 2L)
                    throw new InvalidOperationException("The generated map package is incomplete.");
                AnchorPackage.ValidateFile(package.PayloadPath, project);

                var combinedResourceProject = project.Clone();
                combinedResourceProject.ManualResources =
                [
                    new ManualResource { Kind = ManualResourceKind.Gold, X = 4, Y = 4 }
                ];
                var combinedResourcePayload = AnchorPackage.Write(combinedResourceProject, temporaryScripts);
                AnchorPackage.ValidateFile(combinedResourcePayload, combinedResourceProject);
                using (var stream = File.OpenRead(combinedResourcePayload))
                using (var reader = new BinaryReader(stream))
                {
                    stream.Position = 24;
                    if ((reader.ReadUInt32() & 0x00000003u) != 0x00000003u)
                        throw new InvalidOperationException(
                            "Manual resources and exact forests were not permanently activated.");
                }

                var playerStartProject = project.Clone();
                playerStartProject.ManualPlayerStarts =
                [
                    new ManualPlayerStart
                    {
                        PlayerSlot = 1,
                        X = 4,
                        Y = 4
                    },
                    new ManualPlayerStart
                    {
                        PlayerSlot = 2,
                        X = 11,
                        Y = 11
                    }
                ];
                var playerStartPayload = AnchorPackage.Write(playerStartProject, temporaryScripts);
                AnchorPackage.ValidateFile(playerStartPayload, playerStartProject);
                if (new FileInfo(playerStartPayload).Length != AnchorPackage.HeaderSize +
                    project.Cells.Length * 4L + sizeof(uint) * 2L + 20L)
                    throw new InvalidOperationException("Manual player starts have an invalid payload size.");
                using (var stream = File.OpenRead(playerStartPayload))
                using (var reader = new BinaryReader(stream))
                {
                    stream.Position = 24;
                    if ((reader.ReadUInt32() & 0x00000004u) == 0)
                        throw new InvalidOperationException(
                            "Manual player starts were not permanently activated.");
                    stream.Position = AnchorPackage.HeaderSize + project.Cells.Length * 4L;
                    if (reader.ReadUInt32() != 0 || reader.ReadUInt32() != 2 ||
                        reader.ReadByte() != 1 || reader.ReadByte() != 0 ||
                        reader.ReadInt32() != 4 || reader.ReadInt32() != 4 ||
                        reader.ReadByte() != 2 || reader.ReadByte() != 0 ||
                        reader.ReadInt32() != 11 || reader.ReadInt32() != 11)
                        throw new InvalidOperationException("Manual player starts did not serialize by lobby slot.");
                }
                var synchronizedProject = project.Clone();
                var startDelta = Collaboration.MapDelta.Create(synchronizedProject, playerStartProject)
                    ?? throw new InvalidOperationException("Manual player starts did not create a collaboration delta.");
                startDelta.ApplyTo(synchronizedProject);
                if (synchronizedProject.ManualPlayerStarts.Count != 2 ||
                    synchronizedProject.ManualPlayerStarts[1].PlayerSlot != 2)
                    throw new InvalidOperationException("Manual player starts did not synchronize.");

                var archivePath = Path.Combine(temporaryScripts, "Self Test Installer.zip");
                MultiplayerPackageExporter.Export(project, package, archivePath);
                var executablePath = Path.Combine(temporaryScripts, "Self Test Installer.exe");
                MultiplayerInstallerBuilder.Create(archivePath, executablePath);
                if (!File.Exists(executablePath) || new FileInfo(executablePath).Length <= new FileInfo(archivePath).Length)
                    throw new InvalidOperationException("The self-contained executable map installer was not built correctly.");

                byte[] standardPreviewBytes;
                using (var standardArchive = ZipFile.OpenRead(archivePath))
                {
                    var standardPreview = standardArchive.GetEntry(
                        "EDMW Installer/map-preview.png")
                        ?? throw new InvalidOperationException(
                            "The standard installer preview is missing.");
                    using var standardPreviewInput = standardPreview.Open();
                    using var standardPreviewOutput = new MemoryStream();
                    standardPreviewInput.CopyTo(standardPreviewOutput);
                    standardPreviewBytes = standardPreviewOutput.ToArray();
                }

                var hiddenPreviewArchivePath = Path.Combine(
                    temporaryScripts, "Self Test Installer Redacted Preview.zip");
                MultiplayerPackageExporter.Export(
                    project, package, hiddenPreviewArchivePath, hideMapPreview: true);
                using (var hiddenPreviewArchive = ZipFile.OpenRead(hiddenPreviewArchivePath))
                {
                    var redactedPreview = hiddenPreviewArchive.GetEntry(
                        "EDMW Installer/map-preview.png")
                        ?? throw new InvalidOperationException(
                            "The redacted installer preview is missing.");
                    using var redactedPreviewInput = redactedPreview.Open();
                    using var redactedPreviewOutput = new MemoryStream();
                    redactedPreviewInput.CopyTo(redactedPreviewOutput);
                    var redactedPreviewBytes = redactedPreviewOutput.ToArray();
                    if (redactedPreviewBytes.AsSpan().SequenceEqual(standardPreviewBytes))
                        throw new InvalidOperationException(
                            "The hidden installer preview was not replaced.");
                    redactedPreviewOutput.Position = 0;
                    using var redactedPreviewBitmap = new Bitmap(redactedPreviewOutput);
                    var redPixelCount = 0;
                    for (var y = 0; y < redactedPreviewBitmap.Height; y += 2)
                        for (var x = 0; x < redactedPreviewBitmap.Width; x += 2)
                        {
                            var pixel = redactedPreviewBitmap.GetPixel(x, y);
                            if (pixel.R >= 170 && pixel.G <= 80 && pixel.B <= 80)
                                redPixelCount++;
                        }
                    if (redPixelCount < 500)
                        throw new InvalidOperationException(
                            "The redacted installer preview does not contain the red notice.");
                }
                var hiddenPreviewExecutablePath = Path.Combine(
                    temporaryScripts, "Self Test Installer Redacted Preview.exe");
                MultiplayerInstallerBuilder.Create(
                    hiddenPreviewArchivePath, hiddenPreviewExecutablePath);
                if (!File.Exists(hiddenPreviewExecutablePath) ||
                    new FileInfo(hiddenPreviewExecutablePath).Length <=
                    new FileInfo(hiddenPreviewArchivePath).Length)
                    throw new InvalidOperationException(
                        "The installer with a redacted map preview was not built correctly.");

                var bytes = File.ReadAllBytes(package.PayloadPath);
                bytes[^1] ^= 1;
                File.WriteAllBytes(package.PayloadPath, bytes);
                try
                {
                    AnchorPackage.ValidateFile(package.PayloadPath, project);
                    throw new InvalidOperationException("Payload corruption was not detected.");
                }
                catch (InvalidDataException) { }
                try
                {
                    AnchorPackage.Write(project, temporaryScripts);
                    throw new InvalidOperationException("A damaged or colliding payload was overwritten silently.");
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("already uses map ID", StringComparison.Ordinal))
                {
                }
            }
            finally
            {
                if (Directory.Exists(temporaryScripts)) Directory.Delete(temporaryScripts, true);
            }

            for (var index = 0; index < 256; index++)
            {
                var markerProject = project.Clone();
                markerProject.Variance = index % 51;
                markerProject.Cells[index % markerProject.Cells.Length] = index % TerrainCatalog.All.Length;
                var marker = float.Parse(AnchorPackage.TransportThreshold(markerProject),
                    System.Globalization.CultureInfo.InvariantCulture);
                if (marker < 1 || marker >= 2 || BitConverter.SingleToUInt32Bits(marker) >> 31 != 0)
                    throw new InvalidOperationException("A generated map ID used the legacy negative transport.");
            }

            _ = RmvGenerator.Generate(MapProject.CreateDefault(8));
            var allLand = MapProject.CreateDefault(8);
            Array.Fill(allLand.Cells, (int)TerrainKind.Desert);
            var plainsResult = RmvGenerator.Generate(allLand);
            if (!plainsResult.Script.Contains("#include \"Plains\\Plains Tiny\"", StringComparison.Ordinal) ||
                !plainsResult.Script.Contains("ResourcePerPlayer               (Animal, 0)", StringComparison.Ordinal) ||
                plainsResult.Script.Contains("ResourcePerPlayer               (Fish,", StringComparison.Ordinal) ||
                !plainsResult.Script.Contains("MaximumTreesPerClump            20", StringComparison.Ordinal) ||
                !plainsResult.Script.Contains("MaxTreeLoopCount                800", StringComparison.Ordinal) ||
                !plainsResult.Script.Contains("MaxLoopCount                    800", StringComparison.Ordinal))
                throw new InvalidOperationException("The original Plains resource/forest differences were not preserved.");

            var highlands = MapProject.CreateDefault(8);
            Array.Fill(highlands.Cells, (int)TerrainKind.Hills);
            var highlandsResult = RmvGenerator.Generate(highlands);
            if (!highlandsResult.Script.Contains("#include \"Highlands\\Highlands Tiny\"", StringComparison.Ordinal) ||
                !highlandsResult.Script.Contains("ResourcePerPlayer               (Animal, kAnimalPerPlayer)", StringComparison.Ordinal) ||
                highlandsResult.Script.Contains("ResourcePerPlayer               (Fish,", StringComparison.Ordinal))
                throw new InvalidOperationException("The original Highlands resource differences were not preserved.");

            var embeddedGenerator = EmbeddedGenerator.Read();
            EmbeddedGenerator.ValidateX86Dll(embeddedGenerator);
            var temporaryGame = Path.Combine(Path.GetTempPath(), $"edmw-location-test-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(temporaryGame);
                File.WriteAllBytes(Path.Combine(temporaryGame, "Default.dll"), [0]);
                File.WriteAllBytes(Path.Combine(temporaryGame, "Empires_DMW.exe"), [0]);
                if (!GameInstallationLocator.IsValid(temporaryGame) ||
                    !string.Equals(GameInstallationLocator.Locate(temporaryGame),
                        Path.GetFullPath(temporaryGame), StringComparison.OrdinalIgnoreCase) ||
                    GameInstallationLocator.IsValid(Path.Combine(temporaryGame, "missing")))
                    throw new InvalidOperationException(
                        "Selected game-installation path validation failed.");
                if (Directory.EnumerateFiles(temporaryGame).Count() != 2)
                    throw new InvalidOperationException("Game-installation discovery modified the selected folder.");

                var scripts = Path.Combine(temporaryGame, "Data", "Random Map Scripts");
                var payloads = Path.Combine(scripts, AnchorPackage.DataFolderName);
                Directory.CreateDirectory(payloads);
                File.WriteAllText(Path.Combine(scripts, "Dashboard Test.rmv"), "UseMapGenerator \"EDMW_123ABC\"");
                File.WriteAllBytes(Path.Combine(temporaryGame, "EDMW_123ABC.dll"), [0]);
                var traceMarker = Path.Combine(temporaryGame, "EDMW_123ABC.trace");
                var traceLog = Path.Combine(temporaryGame, "EDMW_123ABC.trace.log");
                var faunaLog = Path.Combine(temporaryGame, "EDMW_123ABC.fauna.42.jsonl");
                var treeSnapshot = Path.Combine(temporaryGame, "EDMW_123ABC.trees.bin");
                File.WriteAllBytes(traceMarker, [0]);
                File.WriteAllBytes(traceLog, [0]);
                File.WriteAllBytes(faunaLog, [0]);
                File.WriteAllBytes(treeSnapshot, [0]);
                File.WriteAllBytes(Path.Combine(payloads, "123ABC.edmwa"), [0]);
                var legacyPayload = Path.Combine(scripts, "Legacy Payloads", "123ABC.edmwa");
                Directory.CreateDirectory(Path.GetDirectoryName(legacyPayload)!);
                File.WriteAllBytes(legacyPayload, [0]);
                var customMaps = CustomMapInstallationCatalog.Discover(temporaryGame);
                if (customMaps.Count != 1 || customMaps[0].Name != "Dashboard Test" || !customMaps[0].IsComplete)
                    throw new InvalidOperationException("Custom-map discovery did not find the installed map.");
                CustomMapInstallationCatalog.Uninstall(customMaps[0]);
                if (CustomMapInstallationCatalog.Discover(temporaryGame).Count != 0 ||
                    File.Exists(Path.Combine(temporaryGame, "EDMW_123ABC.dll")) ||
                    File.Exists(Path.Combine(payloads, "123ABC.edmwa")) ||
                    File.Exists(legacyPayload) || File.Exists(traceMarker) || File.Exists(traceLog) ||
                    File.Exists(faunaLog) || File.Exists(treeSnapshot))
                    throw new InvalidOperationException("Custom-map removal left an installed package artifact.");

                // Reproduce an older partial uninstall: the RMV is already gone while its
                // exact-ID generator and payload remain. The dashboard must still expose it.
                var orphanGenerator = Path.Combine(temporaryGame, "EDMW_456DEF.dll");
                var orphanPayload = Path.Combine(payloads, "456DEF.edmwa");
                Directory.CreateDirectory(payloads);
                File.WriteAllBytes(orphanGenerator, [0]);
                File.WriteAllBytes(orphanPayload, [0]);
                var orphanedMaps = CustomMapInstallationCatalog.Discover(temporaryGame);
                if (orphanedMaps.Count != 1 || orphanedMaps[0].MapId != "456DEF" ||
                    orphanedMaps[0].ScriptPath is not null || orphanedMaps[0].IsComplete)
                    throw new InvalidOperationException("Orphaned map-package discovery failed.");
                CustomMapInstallationCatalog.Uninstall(orphanedMaps[0]);
                if (File.Exists(orphanGenerator) || File.Exists(orphanPayload) ||
                    CustomMapInstallationCatalog.Discover(temporaryGame).Count != 0)
                    throw new InvalidOperationException("Orphaned map-package removal failed.");
            }
            finally
            {
                if (Directory.Exists(temporaryGame)) Directory.Delete(temporaryGame, true);
            }

            Console.WriteLine($"SELF-TEST PASS ({result.Script.Length} chars, {result.Analysis.LandFraction:P1} land)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("SELF-TEST FAIL: " + ex);
            return 1;
        }
    }

    private static void VerifyUsStateBrushes()
    {
        var expectedNames = new[]
        {
            "Alabama", "Alaska", "Arizona", "Arkansas", "California", "Colorado", "Connecticut",
            "Delaware", "Florida", "Georgia", "Hawaii", "Idaho", "Illinois", "Indiana", "Iowa",
            "Kansas", "Kentucky", "Louisiana", "Maine", "Maryland", "Massachusetts", "Michigan",
            "Minnesota", "Mississippi", "Missouri", "Montana", "Nebraska", "Nevada", "New Hampshire",
            "New Jersey", "New Mexico", "New York", "North Carolina", "North Dakota", "Ohio",
            "Oklahoma", "Oregon", "Pennsylvania", "Rhode Island", "South Carolina", "South Dakota",
            "Tennessee", "Texas", "Utah", "Vermont", "Virginia", "Washington", "West Virginia",
            "Wisconsin", "Wyoming"
        };
        if (!UsStateBrushCatalog.All.Select(state => state.Name).SequenceEqual(expectedNames))
            throw new InvalidOperationException("The state brush menu must contain all 50 states once, in alphabetical order.");

        const int radius = 24;
        foreach (var state in UsStateBrushCatalog.All)
        {
            var occupied = 0;
            for (var y = -radius; y <= radius; y++)
                for (var x = -radius; x <= radius; x++)
                {
                    var unrotated = BrushShapeGeometry.ContainsRotated(
                        BrushShape.Round, x, y, radius, 0, 100, 100, state);
                    if (unrotated) occupied++;
                    if (unrotated != BrushShapeGeometry.ContainsRotated(
                            BrushShape.Round, -y, x, radius, 90, 100, 100, state) ||
                        unrotated != BrushShapeGeometry.ContainsRotated(
                            BrushShape.Round, x, y, radius, 360, 100, 100, state) ||
                        unrotated != BrushShapeGeometry.ContainsRotated(
                            BrushShape.Round, y, -x, radius, -90, 100, 100, state))
                        throw new InvalidOperationException($"{state.Name} brush rotation changed its footprint.");
                    if (unrotated != BrushShapeGeometry.ContainsRotated(
                            BrushShape.Round, x * 2, y, radius, 0, 200, 100, state) ||
                        unrotated != BrushShapeGeometry.ContainsRotated(
                            BrushShape.Round, x, y * 2, radius, 0, 100, 200, state))
                        throw new InvalidOperationException($"{state.Name} brush length or width scaling distorted its footprint.");
                }
            if (occupied < 8)
                throw new InvalidOperationException($"{state.Name} has an empty or unusable state brush.");
            if (state.Contains(-1.1, 0) || state.Contains(1.1, 0) ||
                state.Contains(0, -1.1) || state.Contains(0, 1.1))
                throw new InvalidOperationException($"{state.Name} brush extends outside its normalized bounds.");
            if (!BrushShapeGeometry.ContainsRotated(BrushShape.Round, 0, 0, 0, 73, 25, 200, state) ||
                BrushShapeGeometry.ContainsRotated(BrushShape.Round, 1, 0, 0, 73, 25, 200, state))
                throw new InvalidOperationException($"{state.Name} does not paint exactly one cell at the minimum brush scale.");

            // A transformed state must fit inside the iteration bounds used by both
            // the painted stroke and its hover preview, including narrow brushes.
            const int rotation = 37;
            const int length = 200;
            const int width = 25;
            var extent = BrushShapeGeometry.FootprintExtent(radius, rotation, length, width);
            for (var offset = -extent - 2; offset <= extent + 2; offset++)
                foreach (var edge in new[] { -extent - 1, extent + 1 })
                    if (BrushShapeGeometry.ContainsRotated(BrushShape.Round,
                            edge, offset, radius, rotation, length, width, state) ||
                        BrushShapeGeometry.ContainsRotated(BrushShape.Round,
                            offset, edge, radius, rotation, length, width, state))
                        throw new InvalidOperationException($"The preview bounds clip the transformed {state.Name} brush.");
        }
    }

    private static void VerifyUsStateFeatures()
    {
        var mountainSamples = 0;
        var waterSamples = 0;
        var forestSamples = 0;
        foreach (var state in UsStateBrushCatalog.All)
        {
            // Exercise all combinations against one set of coordinates, so
            // changing a toggle cannot change the other layer or the silhouette.
            for (var row = -32; row <= 32; row++)
                for (var column = -32; column <= 32; column++)
                {
                    var x = column / 32d;
                    var y = row / 32d;
                    var land = state.Contains(x, y);
                    var mountain = state.GetFeature(x, y, mountains: true, water: false);
                    var water = state.GetFeature(x, y, mountains: false, water: true);
                    var forest = state.GetFeature(x, y, mountains: false, water: false, forests: true);
                    var combined = state.GetFeature(x, y, mountains: true, water: true, forests: true);
                    if (state.GetFeature(x, y, mountains: false, water: false) is not null ||
                        mountain is not (null or TerrainKind.Mountain) ||
                        water is not (null or TerrainKind.Shallows) ||
                        forest is not (null or TerrainKind.Forest) ||
                        combined != (water ?? mountain ?? forest) ||
                        state.GetFeature(x, y, mountains: true, water: true) != (water ?? mountain) ||
                        state.GetFeature(x, y, mountains: true, water: false, forests: true) != (mountain ?? forest) ||
                        state.GetFeature(x, y, mountains: false, water: true, forests: true) != (water ?? forest) ||
                        state.Contains(x, y) != land)
                        throw new InvalidOperationException($"{state.Name} feature toggles are not independent, or water/mountain/forest priority changed.");
                    if (mountain is not null && !land)
                        throw new InvalidOperationException($"{state.Name} mountains extend beyond the land silhouette.");
                    if (forest is not null && !land)
                        throw new InvalidOperationException($"{state.Name} forests extend beyond the land silhouette.");
                    if (mountain is not null) mountainSamples++;
                    if (water is not null) waterSamples++;
                    if (forest is not null) forestSamples++;
                }
            foreach (var invalid in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity, -1.01, 1.01 })
                if (state.GetFeature(invalid, 0, mountains: true, water: true, forests: true) is not null ||
                    state.GetFeature(0, invalid, mountains: true, water: true, forests: true) is not null)
                    throw new InvalidOperationException($"{state.Name} features accepted coordinates outside the state mask.");
        }
        if (mountainSamples == 0 || waterSamples == 0 || forestSamples == 0)
            throw new InvalidOperationException("The state geography catalog is missing its mountain, water, or forest layer.");

        // Geographic landmarks transformed independently from longitude/latitude
        // into the exact state silhouette projection during data verification.
        var mountainFixtures = new[]
        {
            ("Colorado", "Mount Elbert", -0.250156519, -0.034562579, true),
            ("California", "Mount Whitney", 0.130022022, 0.155405830, true),
            ("Alaska", "Denali", 0.244040325, -0.065414866, true),
            ("Hawaii", "Mauna Kea", 0.756228716, 0.295792668, true),
            ("New Hampshire", "Mount Washington", 0.180112476, -0.206140015, true),
            ("Colorado", "Limon plains", 0.512284713, -0.090819661, false),
            ("California", "Fresno", -0.121189289, 0.122668556, false),
            ("Alaska", "Bethel", -0.057590143, 0.083569942, false),
            ("Florida", "Orlando", 0.627890498, -0.234416955, false)
        };
        foreach (var (stateName, landmark, x, y, mountainous) in mountainFixtures)
        {
            var state = UsStateBrushCatalog.All.Single(item => item.Name == stateName);
            if (!state.Contains(x, y) ||
                (state.GetFeature(x, y, mountains: true, water: false) == TerrainKind.Mountain) != mountainous)
                throw new InvalidOperationException($"The mountain overlay is misaligned at {landmark}, {stateName}.");
        }
        var waterFixtures = new[]
        {
            ("Utah", "Great Salt Lake", -0.2859293021, -0.6349954671, true),
            ("Florida", "Lake Okeechobee", 0.7897450364, 0.2418463593, true),
            ("California", "Lake Tahoe", -0.1580167935, -0.3702335432, true),
            ("Alaska", "Iliamna Lake", 0.1506281785, 0.1575264610, true),
            ("Hawaii", "Wailuku River", 0.8918975911, 0.3334940623, true),
            ("Utah", "Salt Lake City", -0.1032494725, -0.4981840758, false)
        };
        foreach (var (stateName, landmark, x, y, watery) in waterFixtures)
        {
            var state = UsStateBrushCatalog.All.Single(item => item.Name == stateName);
            if ((state.GetFeature(x, y, mountains: false, water: true) == TerrainKind.Shallows) != watery)
                throw new InvalidOperationException($"The water overlay is misaligned at {landmark}, {stateName}.");
        }
        var forestFixtures = new[]
        {
            ("Washington", "Hoh rainforest", -0.8488353044, -0.2219243442, true),
            ("Washington", "Willapa Hills", -0.7231386722, 0.3064494738, true),
            ("Minnesota", "Superior National Forest", 0.3465275388, -0.4954884876, true),
            ("Louisiana", "Kisatchie National Forest", -0.5948859012, -0.2222540423, true),
            ("Alaska", "Yukon interior forest", 0.3117456237, -0.1937738489, true),
            ("Washington", "Yakima", 0.0621532394, 0.2657816841, false),
            ("Minnesota", "Minneapolis", 0.0135162233, 0.4978313942, false)
        };
        foreach (var (stateName, landmark, x, y, forested) in forestFixtures)
        {
            var state = UsStateBrushCatalog.All.Single(item => item.Name == stateName);
            if ((state.GetFeature(x, y, mountains: false, water: false, forests: true) == TerrainKind.Forest) != forested)
                throw new InvalidOperationException($"The forest overlay is misaligned at {landmark}, {stateName}.");
        }
        VerifyOriginalMountainCoverage();
        VerifyStateForestCoverage();
    }

    private static void VerifyStateForestCoverage()
    {
        foreach (var state in UsStateBrushCatalog.All)
        {
            using var encoded = new MemoryStream(Convert.FromBase64String(UsStateFeatureCatalog.Forests(state.Name)));
            using var compressed = new GZipStream(encoded, CompressionMode.Decompress);
            var mask = new byte[UsStateBrush.MaskSize * UsStateBrush.MaskSize / 8];
            compressed.ReadExactly(mask);
            var forestCells = mask.Sum(value => System.Numerics.BitOperations.PopCount((uint)value));
            if (forestCells == 0)
                throw new InvalidOperationException($"{state.Name} has no selected forest cores.");
            var landCells = 0;
            for (var row = 0; row < UsStateBrush.MaskSize; row++)
                for (var column = 0; column < UsStateBrush.MaskSize; column++)
                    if (state.Contains(2d * (column + 0.5) / UsStateBrush.MaskSize - 1,
                                       2d * (row + 0.5) / UsStateBrush.MaskSize - 1))
                        landCells++;
            if (forestCells > landCells * 0.25 + 1)
                throw new InvalidOperationException($"{state.Name} selected forest cores cover {forestCells / (double)landCells:P1} of the state; the limit is 25%.");
        }
    }

    private static void VerifyOriginalMountainCoverage()
    {
        // Counts from the independently verified full GMBA polygon mapping.
        // Together with landmark checks, these preserve the selected source
        // coverage for every state.
        var originalCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Alabama"] = 584,
            ["Alaska"] = 49862,
            ["Arizona"] = 207857,
            ["Arkansas"] = 15210,
            ["California"] = 203325,
            ["Colorado"] = 295595,
            ["Connecticut"] = 1685,
            ["Delaware"] = 0,
            ["Florida"] = 0,
            ["Georgia"] = 14237,
            ["Hawaii"] = 37987,
            ["Idaho"] = 215274,
            ["Illinois"] = 0,
            ["Indiana"] = 0,
            ["Iowa"] = 0,
            ["Kansas"] = 0,
            ["Kentucky"] = 6333,
            ["Louisiana"] = 0,
            ["Maine"] = 32238,
            ["Maryland"] = 7228,
            ["Massachusetts"] = 11227,
            ["Michigan"] = 0,
            ["Minnesota"] = 0,
            ["Mississippi"] = 0,
            ["Missouri"] = 0,
            ["Montana"] = 145806,
            ["Nebraska"] = 0,
            ["Nevada"] = 224715,
            ["New Hampshire"] = 96201,
            ["New Jersey"] = 0,
            ["New Mexico"] = 130245,
            ["New York"] = 28669,
            ["North Carolina"] = 26076,
            ["North Dakota"] = 0,
            ["Ohio"] = 0,
            ["Oklahoma"] = 1695,
            ["Oregon"] = 317612,
            ["Pennsylvania"] = 42315,
            ["Rhode Island"] = 0,
            ["South Carolina"] = 3321,
            ["South Dakota"] = 3557,
            ["Tennessee"] = 15436,
            ["Texas"] = 6412,
            ["Utah"] = 347234,
            ["Vermont"] = 168314,
            ["Virginia"] = 37674,
            ["Washington"] = 291154,
            ["West Virginia"] = 90137,
            ["Wisconsin"] = 0,
            ["Wyoming"] = 182296,
        };
        foreach (var state in UsStateBrushCatalog.All)
        {
            using var encoded = new MemoryStream(Convert.FromBase64String(UsStateFeatureCatalog.Mountains(state.Name)));
            using var compressed = new GZipStream(encoded, CompressionMode.Decompress);
            var mask = new byte[UsStateBrush.MaskSize * UsStateBrush.MaskSize / 8];
            compressed.ReadExactly(mask);
            var mountainCells = mask.Sum(value => System.Numerics.BitOperations.PopCount((uint)value));
            if (mountainCells != originalCounts[state.Name])
                throw new InvalidOperationException($"{state.Name} no longer matches the original full mountain footprint.");
        }
    }

    private static void VerifyCliffPersistence()
    {
        var ordinary = MapProject.CreateDefault(16);
        Array.Fill(ordinary.Cells, (int)TerrainKind.Grassland);
        const int cliffIndex = 5 * 16 + 7;
        ordinary.Cells[cliffIndex] = (int)TerrainKind.Mountain;
        ordinary.ElevationMask[cliffIndex] = 100;
        ordinary.RampMask[cliffIndex] = 1;
        ordinary.Validate();
        var cliffs = ordinary.Clone();
        cliffs.ElevationMask[cliffIndex] = MapProject.CliffElevation;
        cliffs.RampMask[cliffIndex] = 0;
        cliffs.Validate();
        if (MapProject.CliffElevation != 126 || AnchorPackage.MapId(ordinary) == AnchorPackage.MapId(cliffs))
            throw new InvalidOperationException("Cliff metadata did not distinguish the map from ordinary mountain elevation 100.");

        var delta = MapDelta.Create(ordinary, cliffs)
            ?? throw new InvalidOperationException("A cliff metadata-only edit did not create a collaboration delta.");
        if (!delta.Indices.SequenceEqual(new[] { cliffIndex }) ||
            delta.Cells[0] != (int)TerrainKind.Mountain ||
            delta.ElevationMask[0] != MapProject.CliffElevation || delta.RampMask[0] != 0)
            throw new InvalidOperationException("Cliff marker or ramp clearing was omitted from the collaboration delta.");
        var synchronized = ordinary.Clone();
        delta.ApplyTo(synchronized);
        AssertEquivalentProjectState(cliffs, synchronized, "Cliff metadata failed to synchronize.");

        var invalid = cliffs.Clone();
        invalid.Cells[cliffIndex] = (int)TerrainKind.Grassland;
        try
        {
            invalid.Validate();
            throw new InvalidOperationException("The cliff elevation marker was accepted on non-mountain terrain.");
        }
        catch (InvalidDataException) { }
        foreach (var invalidElevation in new[] { 101, 125, 128, -101 })
        {
            var values = cliffs.SerializedElevationMask;
            values[cliffIndex] = invalidElevation;
            try
            {
                invalid.SerializedElevationMask = values;
                throw new InvalidOperationException("An unrecognized reserved elevation was accepted.");
            }
            catch (InvalidDataException) { }
        }

        // A later user-authored ramp can cross a cliff; both pieces of metadata
        // must survive save/load and export together.
        cliffs.RampMask[cliffIndex] = 1;
        var folder = Path.Combine(Path.GetTempPath(), $"edmw-cliff-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var projectPath = Path.Combine(folder, "Cliff test.edmwmap");
            cliffs.Save(projectPath);
            var loaded = MapProject.Load(projectPath);
            AssertEquivalentProjectState(cliffs, loaded, "Cliff terrain, height marker, or manual ramp failed to save/load.");
            var payloadPath = AnchorPackage.Write(loaded, folder);
            AnchorPackage.ValidateFile(payloadPath, loaded);
            var bytes = File.ReadAllBytes(payloadPath);
            if (bytes[AnchorPackage.HeaderSize + cliffIndex] != (byte)TerrainKind.Mountain ||
                bytes[AnchorPackage.HeaderSize + loaded.Cells.Length * 2 + cliffIndex] != 126 ||
                bytes[AnchorPackage.HeaderSize + loaded.Cells.Length * 3 + cliffIndex] != 1)
                throw new InvalidOperationException("The native payload lost the cliff marker or its explicit ramp.");
            var ordinaryPayload = AnchorPackage.Write(ordinary, folder);
            var ordinaryBytes = File.ReadAllBytes(ordinaryPayload);
            if (ordinaryBytes[AnchorPackage.HeaderSize + ordinary.Cells.Length * 2 + cliffIndex] != 100)
                throw new InvalidOperationException("Legacy explicit elevation 100 was changed into a cliff marker.");
            if (!RmvGenerator.Generate(cliffs).Script.Contains("CliffTerrain                    GrasslandsCliff", StringComparison.Ordinal))
                throw new InvalidOperationException("The generated map script omitted the cliff surface terrain.");
        }
        finally
        {
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var resolvedFolder = Path.GetFullPath(folder);
            if (!resolvedFolder.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolvedFolder).StartsWith("edmw-cliff-test-", StringComparison.Ordinal))
                throw new InvalidOperationException("Cliff test cleanup escaped its temporary directory.");
            Directory.Delete(resolvedFolder, recursive: true);
        }
    }

    private static void VerifyForcedCliffPersistence()
    {
        var ordinary = MapProject.CreateDefault(16);
        Array.Fill(ordinary.Cells, (int)TerrainKind.Grassland);
        const int cliffIndex = 5 * 16 + 7;
        const int waterIndex = 6 * 16 + 7;
        ordinary.ElevationMask[cliffIndex] = 35;
        ordinary.Cells[waterIndex] = (int)TerrainKind.Shallows;
        ordinary.ElevationMask[waterIndex] = -25;
        ordinary.Validate();
        var legacyId = AnchorPackage.MapId(ordinary);
        var cliffs = ordinary.Clone();
        cliffs.CliffMask[cliffIndex] = 1;
        cliffs.CliffMask[waterIndex] = 1;
        // Even subsequently painted ramps cannot relax the forced-cliff policy.
        cliffs.RampMask[cliffIndex] = 1;
        cliffs.Validate();
        if (AnchorPackage.MapId(cliffs) == legacyId ||
            (AnchorPackage.PlacementFlagsFor(cliffs) & AnchorPackage.ForcedCliffMaskFlag) == 0 ||
            cliffs.ElevationMask[cliffIndex] != 35 || cliffs.ElevationMask[waterIndex] != -25 ||
            cliffs.Cells[cliffIndex] != ordinary.Cells[cliffIndex] ||
            cliffs.Cells[waterIndex] != ordinary.Cells[waterIndex])
            throw new InvalidOperationException("Forced cliffs lost their numeric height, terrain, or map identity.");

        var clone = cliffs.Clone();
        clone.CliffMask[cliffIndex] = 0;
        if (cliffs.CliffMask[cliffIndex] != 1)
            throw new InvalidOperationException("Cloning shared the forced-cliff array.");
        var resized = cliffs.Clone();
        resized.Resize(32);
        if (resized.CliffMask[10 * 32 + 14] != 1 || resized.CliffMask[12 * 32 + 14] != 1 ||
            resized.ElevationMask[10 * 32 + 14] != 35 || resized.ElevationMask[12 * 32 + 14] != -25)
            throw new InvalidOperationException("Resizing lost forced cliff authorship or numeric height.");
        var automatic = cliffs.Clone();
        automatic.ElevationMask[cliffIndex] = MapProject.AutomaticElevation;
        automatic.Validate();
        if (automatic.CliffMask[cliffIndex] != 0 || automatic.CliffMask[waterIndex] != 1)
            throw new InvalidOperationException("Resetting elevation did not clear only its forced-cliff flag.");
        var changedTexture = cliffs.Clone();
        changedTexture.SetCell(7, 5, TerrainKind.Desert);
        changedTexture.Validate();
        if (changedTexture.CliffMask[cliffIndex] != 1 || changedTexture.ElevationMask[cliffIndex] != 35)
            throw new InvalidOperationException("Changing terrain cleared an authored elevation cliff.");

        var delta = MapDelta.Create(ordinary, cliffs)
            ?? throw new InvalidOperationException("A forced-cliff edit produced no delta.");
        var synchronized = ordinary.Clone();
        var received = JsonSerializer.Deserialize<MapDelta>(JsonSerializer.SerializeToUtf8Bytes(delta))
            ?? throw new InvalidOperationException("Forced cliffs did not round-trip in a delta.");
        received.ApplyTo(synchronized);
        AssertEquivalentProjectState(cliffs, synchronized, "Forced-cliff deltas lost height or ramp precedence.");
        var legacyEdit = cliffs.Clone();
        legacyEdit.ElevationMask[cliffIndex] = 45;
        var legacyDelta = MapDelta.Create(cliffs, legacyEdit)!;
        legacyDelta.CliffMask = [];
        var legacySynchronized = cliffs.Clone();
        legacyDelta.ApplyTo(legacySynchronized);
        if (legacySynchronized.CliffMask[cliffIndex] != 1 ||
            legacySynchronized.ElevationMask[cliffIndex] != 45)
            throw new InvalidOperationException("A legacy delta omitted and erased forced-cliff authorship.");

        // Disjoint strokes merge, while the latest completed stroke owns all metadata on a shared cell.
        var firstEditor = ordinary.Clone();
        firstEditor.CliffMask[cliffIndex] = 1;
        var secondEditor = ordinary.Clone();
        secondEditor.CliffMask[waterIndex] = 1;
        var merged = ordinary.Clone();
        MapDelta.Create(ordinary, firstEditor)!.ApplyTo(merged);
        MapDelta.Create(ordinary, secondEditor)!.ApplyTo(merged);
        if (merged.CliffMask[cliffIndex] != 1 || merged.CliffMask[waterIndex] != 1)
            throw new InvalidOperationException("Disjoint forced-cliff deltas did not merge.");
        secondEditor.ElevationMask[cliffIndex] = 60;
        secondEditor.CliffMask[cliffIndex] = 0;
        secondEditor.RampMask[cliffIndex] = 1;
        MapDelta.Create(ordinary, secondEditor)!.ApplyTo(merged);
        if (merged.CliffMask[cliffIndex] != 0 || merged.ElevationMask[cliffIndex] != 60 ||
            merged.RampMask[cliffIndex] != 1 || merged.CliffMask[waterIndex] != 1)
            throw new InvalidOperationException("The latest elevation stroke did not replace its cliff metadata.");

        var malformed = cliffs.Clone();
        malformed.CliffMask[cliffIndex] = 2;
        try
        {
            malformed.Validate();
            throw new InvalidOperationException("An invalid forced-cliff flag was accepted.");
        }
        catch (InvalidDataException) { }
        malformed = cliffs.Clone();
        malformed.CliffMask = new byte[3];
        try
        {
            malformed.Validate();
            throw new InvalidOperationException("An incorrectly sized forced-cliff mask was accepted.");
        }
        catch (InvalidDataException) { }
        received.CliffMask[0] = 2;
        try
        {
            received.ApplyTo(synchronized);
            throw new InvalidOperationException("An invalid remote forced-cliff flag was accepted.");
        }
        catch (InvalidDataException) { }
        AssertEquivalentProjectState(cliffs, synchronized, "An invalid cliff delta partially modified the map.");

        var folder = Path.Combine(Path.GetTempPath(), $"edmw-forced-cliff-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var projectPath = Path.Combine(folder, "Forced cliffs.edmwmap");
            cliffs.Save(projectPath);
            var loaded = MapProject.Load(projectPath);
            AssertEquivalentProjectState(cliffs, loaded, "Forced cliffs failed to save/load.");
            var legacyJson = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(ordinary,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }))!.AsObject();
            legacyJson.Remove("cliffMask");
            File.WriteAllText(projectPath, legacyJson.ToJsonString());
            var legacyLoaded = MapProject.Load(projectPath);
            AssertEquivalentProjectState(ordinary, legacyLoaded, "An older project did not default to zero cliff flags.");

            var payload = AnchorPackage.Write(loaded, folder);
            AnchorPackage.ValidateFile(payload, loaded);
            var bytes = File.ReadAllBytes(payload);
            if (bytes.Length != AnchorPackage.HeaderSize + loaded.Cells.Length * 5 + sizeof(uint) * 2 ||
                BitConverter.ToUInt32(bytes, 24) != AnchorPackage.PlacementFlagsFor(loaded) ||
                bytes[AnchorPackage.HeaderSize + loaded.Cells.Length * 2 + cliffIndex] != 35 ||
                bytes[AnchorPackage.HeaderSize + loaded.Cells.Length * 3 + cliffIndex] != 1 ||
                bytes[AnchorPackage.HeaderSize + loaded.Cells.Length * 4 + cliffIndex] != 1 ||
                bytes[AnchorPackage.HeaderSize + loaded.Cells.Length * 4 + waterIndex] != 1)
                throw new InvalidOperationException("The optional native cliff mask changed authored heights or ramps.");
            bytes[AnchorPackage.HeaderSize + loaded.Cells.Length * 4 + cliffIndex] = 0;
            var corrupt = Path.Combine(folder, "Corrupt cliffs.edmwa");
            File.WriteAllBytes(corrupt, bytes);
            try
            {
                AnchorPackage.ValidateFile(corrupt, loaded);
                throw new InvalidOperationException("A corrupted native cliff mask passed validation.");
            }
            catch (InvalidDataException) { }
            var legacyPayload = AnchorPackage.Write(ordinary, folder);
            if (new FileInfo(legacyPayload).Length != AnchorPackage.HeaderSize + ordinary.Cells.Length * 4 + sizeof(uint) * 2 ||
                (AnchorPackage.PlacementFlagsFor(ordinary) & AnchorPackage.ForcedCliffMaskFlag) != 0 ||
                AnchorPackage.MapId(ordinary) != legacyId)
                throw new InvalidOperationException("Zero cliff masks changed the legacy payload layout or identity.");
        }
        finally
        {
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var resolvedFolder = Path.GetFullPath(folder);
            if (!resolvedFolder.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolvedFolder).StartsWith("edmw-forced-cliff-test-", StringComparison.Ordinal))
                throw new InvalidOperationException("Forced-cliff test cleanup escaped its temporary directory.");
            Directory.Delete(resolvedFolder, recursive: true);
        }
    }

    private static void VerifyGenerationSeedMetadata()
    {
        var project = MapProject.CreateDefault(16);
        var originalId = AnchorPackage.MapId(project);
        foreach (int? seed in new int?[] { 0, int.MinValue, int.MaxValue, null })
        {
            var changed = project.Clone();
            changed.GenerationSeed = seed;
            if (changed.Clone().GenerationSeed != seed)
                throw new InvalidOperationException("Cloning omitted the map generation seed.");
            var delta = MapDelta.Create(project, changed)
                ?? throw new InvalidOperationException("A seed-only metadata edit did not create a delta.");
            if (delta.Indices.Length != 0)
                throw new InvalidOperationException("A seed-only edit unexpectedly changed terrain cells.");
            var wire = JsonSerializer.SerializeToUtf8Bytes(delta);
            var received = JsonSerializer.Deserialize<MapDelta>(wire)
                ?? throw new InvalidOperationException("The seed metadata delta did not round-trip on the wire.");
            received.ApplyTo(project);
            AssertEquivalentProjectState(changed, project, "The generation seed did not synchronize or clear.");
            if (AnchorPackage.MapId(project) != originalId || MapDelta.Create(project, project.Clone()) is not null)
                throw new InvalidOperationException("Generation metadata changed the terrain identity or created an empty edit.");

            var file = Path.Combine(Path.GetTempPath(), $"edmw-generation-seed-{Guid.NewGuid():N}.edmwmap");
            try
            {
                project.Save(file);
                AssertEquivalentProjectState(project, MapProject.Load(file), "The generation seed did not save/load.");
            }
            finally { File.Delete(file); }
        }
    }

    private static void VerifySeededMapGeneration()
    {
        foreach (var size in new[] { 8, 64, 512 })
            foreach (var seed in new[] { int.MinValue, 0, int.MaxValue })
            {
                var first = SeededMapGenerator.Generate(size, seed);
                first.Validate();
                var second = SeededMapGenerator.Generate(size, seed);
                AssertEquivalentProjectState(first, second,
                    $"Seeded generation is not deterministic at size {size}, seed {seed}.");
                if (first.GenerationSeed != seed || !first.PreserveCoastline || first.GridSize != size ||
                    ReferenceEquals(first.Cells, second.Cells) || ReferenceEquals(first.ElevationMask, second.ElevationMask) ||
                    ReferenceEquals(first.RampMask, second.RampMask) || ReferenceEquals(first.CliffMask, second.CliffMask) ||
                    ReferenceEquals(first.ComponentRoles, second.ComponentRoles))
                    throw new InvalidOperationException("Seeded generation did not return an independent map with its seed and size.");
                if (first.ManualPlayerStarts.Count != 0 || first.ManualResources.Count != 0 ||
                    first.ComponentRoles.Any(value => value != 0) || first.RampMask.Any(value => value != 0) ||
                    first.CliffMask.Any(value => value != 0))
                    throw new InvalidOperationException("A freshly generated map contains stale authored metadata.");
                for (var index = 0; index < first.Cells.Length; index++)
                    if (first.ElevationMask[index] != (first.Cells[index] == (int)TerrainKind.Mountain
                            ? MapProject.CliffElevation : MapProject.AutomaticElevation))
                        throw new InvalidOperationException("Generated cliff metadata is missing or attached to the wrong terrain.");

                var land = first.Cells.Count(value => ((TerrainKind)value).IsLand());
                var forest = first.Cells.Count(value => value == (int)TerrainKind.Forest);
                var mountain = first.Cells.Count(value => value == (int)TerrainKind.Mountain);
                var grass = first.Cells.Count(value => value == (int)TerrainKind.Grassland);
                if (land == 0 || land == first.Cells.Length || forest == 0 || mountain == 0 || grass == 0)
                    throw new InvalidOperationException($"Seed {seed} at size {size} omitted a complete-map terrain feature.");
                if (size >= 64 && (land < first.Cells.Length * .30 || land > first.Cells.Length * .85 ||
                    forest > land * .60 || mountain > land * .40 ||
                    grass < land * .15))
                    throw new InvalidOperationException($"Seed {seed} at size {size} lost balanced land, features, or coast terrain.");
            }

        var baseline = SeededMapGenerator.Generate(64, 314159);
        var different = SeededMapGenerator.Generate(64, 271828);
        if (baseline.Cells.SequenceEqual(different.Cells))
            throw new InvalidOperationException("Different complete-map seeds produced identical terrain.");
        AssertEquivalentProjectState(baseline, SeededMapGenerator.Generate(64, 314159),
            "Generating another seed changed the result of a reusable seed.");

        var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var folder = Path.GetFullPath(Path.Combine(temporaryRoot, $"edmw-seeded-export-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(folder);
        try
        {
            var package = GeneratedMapPackage.WriteForSelfTest(baseline, Path.Combine(folder, "Seeded complete map.rmv"));
            AnchorPackage.ValidateFile(package.PayloadPath, baseline);
        }
        finally
        {
            if (!folder.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(folder).StartsWith("edmw-seeded-export-", StringComparison.Ordinal))
                throw new InvalidOperationException("Seeded test cleanup escaped its temporary directory.");
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void VerifyCoastlinePolicyPersistence()
    {
        var project = MapProject.CreateDefault(16);
        const uint legacyId = 7779104;
        if (project.PreserveCoastline || AnchorPackage.MapId(project) != legacyId)
            throw new InvalidOperationException("The default coastline policy changed legacy map identity.");
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var folder = Path.GetFullPath(Path.Combine(temporaryRoot, $"edmw-coastline-test-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var preserve in new[] { true, false })
            {
                var changed = project.Clone();
                changed.PreserveCoastline = preserve;
                if (changed.Clone().PreserveCoastline != preserve)
                    throw new InvalidOperationException("Cloning omitted the coastline policy.");
                var delta = MapDelta.Create(project, changed)
                    ?? throw new InvalidOperationException("A coastline-only edit did not create a delta.");
                if (delta.Indices.Length != 0)
                    throw new InvalidOperationException("A coastline policy edit unexpectedly changed cells.");
                var received = JsonSerializer.Deserialize<MapDelta>(JsonSerializer.SerializeToUtf8Bytes(delta))
                    ?? throw new InvalidOperationException("Coastline policy did not round-trip on the wire.");
                received.ApplyTo(project);
                AssertEquivalentProjectState(changed, project, "Coastline policy did not synchronize or clear.");
                if ((AnchorPackage.MapId(project) != legacyId) != preserve ||
                    ((AnchorPackage.PlacementFlagsFor(project) & 0x10) != 0) != preserve)
                    throw new InvalidOperationException("Coastline policy did not affect native identity and flags correctly.");
                var file = Path.Combine(folder, $"coastline-{preserve}.edmwmap");
                project.Save(file);
                AssertEquivalentProjectState(project, MapProject.Load(file), "Coastline policy did not save/load.");
                var payload = AnchorPackage.Write(project, folder);
                AnchorPackage.ValidateFile(payload, project);
                if (((BitConverter.ToUInt32(File.ReadAllBytes(payload), 24) & 0x10) != 0) != preserve)
                    throw new InvalidOperationException("The native payload omitted the coastline policy.");
                var beachDirective = Regex.Match(RmvGenerator.Generate(project).Script, @"(?m)^PaintBeaches\s+(true|false)\s*$");
                if (!beachDirective.Success || beachDirective.Groups[1].Value != (preserve ? "false" : "true"))
                    throw new InvalidOperationException("Fallback export did not respect the authored coastline policy.");
            }
        }
        finally
        {
            if (!folder.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(folder).StartsWith("edmw-coastline-test-", StringComparison.Ordinal))
                throw new InvalidOperationException("Coastline test cleanup escaped its temporary directory.");
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void VerifySeededMapVariety()
    {
        const int size = 128;
        var seeds = new[] { 0, 1, 2, 3, 4, 5, 314159, int.MinValue, int.MaxValue };
        var edgeLand = false;
        var cornerLand = false;
        var offCenterLand = false;
        var mixedShore = false;
        var forestShore = false;
        var cliffShore = false;
        var deepShore = false;
        var waterDivision = false;
        var longForest = false;
        var longRidge = false;
        foreach (var seed in seeds)
        {
            var map = SeededMapGenerator.Generate(size, seed);
            var land = 0;
            var coast = 0;
            var nonBeachCoast = 0;
            var boundaryLand = 0;
            double sumX = 0, sumY = 0;
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var terrain = map[x, y];
                    if (!terrain.IsLand()) continue;
                    land++;
                    sumX += x;
                    sumY += y;
                    if (x == 0 || y == 0 || x == size - 1 || y == size - 1) boundaryLand++;
                    var adjacent = new List<TerrainKind>(4);
                    if (x > 0) adjacent.Add(map[x - 1, y]);
                    if (x + 1 < size) adjacent.Add(map[x + 1, y]);
                    if (y > 0) adjacent.Add(map[x, y - 1]);
                    if (y + 1 < size) adjacent.Add(map[x, y + 1]);
                    if (!adjacent.Any(value => value.IsWater())) continue;
                    coast++;
                    if (terrain != TerrainKind.Beach) nonBeachCoast++;
                    forestShore |= terrain == TerrainKind.Forest;
                    cliffShore |= terrain == TerrainKind.Mountain;
                    deepShore |= adjacent.Contains(TerrainKind.DeepWater);
                }
            edgeLand |= boundaryLand >= size / 5;
            cornerLand |= map[0, 0].IsLand() || map[size - 1, 0].IsLand() ||
                          map[0, size - 1].IsLand() || map[size - 1, size - 1].IsLand();
            offCenterLand |= Math.Max(Math.Abs(sumX / land / (size - 1) - .5),
                                     Math.Abs(sumY / land / (size - 1) - .5)) >= .075;
            mixedShore |= coast > 0 && nonBeachCoast >= coast * .10;
            waterDivision |= Components(map, value => value.IsLand()).Count(component => component.Count >= land * .05) >= 2;
            longRidge |= Components(map, value => value == TerrainKind.Mountain)
                .Any(component => component.Count >= size * size * .01 && component.Span >= size * .5);
            longForest |= Components(map, value => value == TerrainKind.Forest)
                .Any(component => component.Count >= size * size * .02 && component.Span >= size * .5);
        }
        var checks = new[]
        {
            (edgeLand, "land reaching map edges"), (cornerLand, "land reaching map corners"),
            (offCenterLand, "off-center land placement"), (mixedShore, "mixed shore terrain"),
            (forestShore, "forests reaching water"), (cliffShore, "cliffs reaching water"),
            (deepShore, "deep water meeting land"), (waterDivision, "water separating substantial land areas"),
            (longRidge, "large coherent mountain ranges"), (longForest, "large coherent forest belts")
        };
        var missing = checks.Where(check => !check.Item1).Select(check => check.Item2).ToArray();
        if (missing.Length != 0)
            throw new InvalidOperationException($"Seed sample set lost terrain variety: {string.Join(", ", missing)}.");

        static List<(int Count, int Span)> Components(MapProject map, Func<TerrainKind, bool> includes)
        {
            var seen = new bool[map.Cells.Length];
            var queue = new int[map.Cells.Length];
            var components = new List<(int Count, int Span)>();
            for (var start = 0; start < map.Cells.Length; start++)
            {
                if (seen[start] || !includes((TerrainKind)map.Cells[start])) continue;
                var head = 0;
                var tail = 0;
                var minX = map.GridSize;
                var minY = map.GridSize;
                var maxX = 0;
                var maxY = 0;
                Enqueue(start);
                while (head < tail)
                {
                    var index = queue[head++];
                    var x = index % map.GridSize;
                    var y = index / map.GridSize;
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                    if (x > 0) Enqueue(index - 1);
                    if (x + 1 < map.GridSize) Enqueue(index + 1);
                    if (y > 0) Enqueue(index - map.GridSize);
                    if (y + 1 < map.GridSize) Enqueue(index + map.GridSize);
                }
                components.Add((tail, Math.Max(maxX - minX + 1, maxY - minY + 1)));

                void Enqueue(int index)
                {
                    if (seen[index] || !includes((TerrainKind)map.Cells[index])) return;
                    seen[index] = true;
                    queue[tail++] = index;
                }
            }
            return components;
        }
    }

    private static void VerifyCollaborationCompatibility()
    {
        var baseline = CreateCollaborationBaseline();
        var placer = PopulateCollaborativeFeatures(baseline.Clone());
        var delta = MapDelta.Create(baseline, placer)
            ?? throw new InvalidOperationException("Collaborative resources did not create a map delta.");
        if (delta.ManualResourceChanges?.Count != ManualResourceKindExtensions.PaletteKinds.Count ||
            delta.ManualPlayerStartChanges?.Count != 2 || delta.Name is null || delta.Author is null ||
            !delta.Variance.HasValue)
            throw new InvalidOperationException("The collaboration delta omitted shared map systems.");

        var wire = JsonSerializer.SerializeToUtf8Bytes(delta,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var received = JsonSerializer.Deserialize<MapDelta>(wire,
                           new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
                       ?? throw new InvalidOperationException("The collaboration delta did not round-trip on the wire.");
        var exporter = baseline.Clone();
        received.ApplyTo(exporter);
        AssertEquivalentProjectState(placer, exporter,
            "A non-placing collaborator did not receive every exportable map feature.");

        // Independent concurrent placements must merge instead of replacing each
        // collaborator's complete resource list.
        var firstEditor = baseline.Clone();
        firstEditor.ManualResources.Add(new ManualResource
            { Kind = ManualResourceKind.Gold, X = 5, Y = 5 });
        var secondEditor = baseline.Clone();
        secondEditor.ManualResources.Add(new ManualResource
            { Kind = ManualResourceKind.Stone, X = 6, Y = 5 });
        var authoritative = baseline.Clone();
        (MapDelta.Create(baseline, firstEditor) ??
         throw new InvalidOperationException("The first concurrent edit produced no delta.")).ApplyTo(authoritative);
        (MapDelta.Create(baseline, secondEditor) ??
         throw new InvalidOperationException("The second concurrent edit produced no delta.")).ApplyTo(authoritative);
        if (authoritative.ManualResources.Count != 2 ||
            !authoritative.ManualResources.Any(resource => resource.Kind == ManualResourceKind.Gold) ||
            !authoritative.ManualResources.Any(resource => resource.Kind == ManualResourceKind.Stone))
            throw new InvalidOperationException("Concurrent LAN resource placements did not merge.");

        var wrongGrid = MapProject.CreateDefault(baseline.GridSize + 4);
        try
        {
            (MapDelta.Create(baseline, firstEditor) ??
             throw new InvalidOperationException("The grid-safety edit produced no delta.")).ApplyTo(wrongGrid);
            throw new InvalidOperationException("A stale collaboration delta was accepted on a resized grid.");
        }
        catch (InvalidDataException) { }

        var cleared = authoritative.Clone();
        cleared.ManualResources.Clear();
        (MapDelta.Create(authoritative, cleared) ??
         throw new InvalidOperationException("Clearing shared resources produced no delta.")).ApplyTo(authoritative);
        if (authoritative.ManualResources.Count != 0)
            throw new InvalidOperationException("Clearing shared resources did not synchronize.");

        var temporary = Path.Combine(Path.GetTempPath(), $"edmw-collaboration-export-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temporary);
            var package = GeneratedMapPackage.WriteForSelfTest(exporter,
                Path.Combine(temporary, "LAN Export.rmv"));
            AnchorPackage.ValidateFile(package.PayloadPath, exporter);
            using var stream = File.OpenRead(package.PayloadPath);
            using var reader = new BinaryReader(stream);
            stream.Position = AnchorPackage.HeaderSize + exporter.Cells.Length *
                (exporter.CliffMask.Any(value => value != 0) ? 5L : 4L);
            if (reader.ReadUInt32() != ManualResourceKindExtensions.PaletteKinds.Count ||
                exporter[4, 4] != TerrainKind.Forest)
                throw new InvalidOperationException(
                    "A collaborator's Forest or manual resources were omitted from another user's export.");
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        }

    }

    private static async Task VerifyLanCollaborationAsync()
    {
        var baseline = CreateCollaborationBaseline();
        var hostState = baseline.Clone();
        using var host = new LanCollaborationSession();
        using var placerSession = new LanCollaborationSession();
        using var exporterSession = new LanCollaborationSession();
        host.DeltaReceived += (_, delta) => delta.ApplyTo(hostState);
        host.ProjectReceived += (_, project) => hostState = project.Clone();

        var placerReady = new TaskCompletionSource<MapProject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exporterReady = new TaskCompletionSource<MapProject>(TaskCreationOptions.RunContinuationsAsynchronously);
        placerSession.ProjectReceived += (_, project) => placerReady.TrySetResult(project.Clone());
        exporterSession.ProjectReceived += (_, project) => exporterReady.TrySetResult(project.Clone());

        var portProbe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((System.Net.IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();

        await host.StartHostingAsync(() => hostState.Clone(), "Host", port, enableDiscovery: false);
        await placerSession.JoinAsync(System.Net.IPAddress.Loopback.ToString(), "Placer", port);
        var placerBaseline = await placerReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await exporterSession.JoinAsync(System.Net.IPAddress.Loopback.ToString(), "Exporter", port);
        _ = await exporterReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var placed = PopulateCollaborativeFeatures(placerBaseline.Clone());
        var placementDelta = MapDelta.Create(placerBaseline, placed)
            ?? throw new InvalidOperationException("The LAN placement edit produced no delta.");
        await placerSession.SendDeltaAsync(placementDelta);

        // The exporter asks the authoritative host for a barrier snapshot before
        // packaging, so it cannot export an update still waiting in its UI queue.
        var exporterSnapshot = await exporterSession.SynchronizeAsync();
        var hostSnapshot = await host.SynchronizeAsync();
        AssertEquivalentProjectState(hostSnapshot, exporterSnapshot,
            "The host and non-placing exporter did not converge after LAN synchronization.");
        if (exporterSnapshot.ManualResources.Count != ManualResourceKindExtensions.PaletteKinds.Count)
            throw new InvalidOperationException("The LAN exporter snapshot omitted placed resources.");

        // Two clients editing from the same revision retain both disjoint changes.
        var placerConcurrent = exporterSnapshot.Clone();
        placerConcurrent.ManualResources.Add(new ManualResource
            { Kind = ManualResourceKind.Gold, X = 18, Y = 17 });
        var exporterConcurrent = exporterSnapshot.Clone();
        exporterConcurrent.ManualResources.Add(new ManualResource
            { Kind = ManualResourceKind.Stone, X = 19, Y = 17 });
        var first = MapDelta.Create(exporterSnapshot, placerConcurrent)
            ?? throw new InvalidOperationException("The placer concurrency delta was empty.");
        var second = MapDelta.Create(exporterSnapshot, exporterConcurrent)
            ?? throw new InvalidOperationException("The exporter concurrency delta was empty.");
        await Task.WhenAll(placerSession.SendDeltaAsync(first), exporterSession.SendDeltaAsync(second));
        var mergedFromPlacer = await placerSession.SynchronizeAsync();
        var mergedFromExporter = await exporterSession.SynchronizeAsync();
        AssertEquivalentProjectState(mergedFromPlacer, mergedFromExporter,
            "Concurrent LAN edits did not converge on every peer.");
        if (!mergedFromExporter.ManualResources.Any(resource =>
                resource.Kind == ManualResourceKind.Gold && resource.X == 18 && resource.Y == 17) ||
            !mergedFromExporter.ManualResources.Any(resource =>
                resource.Kind == ManualResourceKind.Stone && resource.X == 19 && resource.Y == 17))
            throw new InvalidOperationException("A concurrent LAN resource placement was lost.");

        var clearedSeed = mergedFromExporter.Clone();
        clearedSeed.GenerationSeed = null;
        clearedSeed.PreserveCoastline = false;
        await exporterSession.SendDeltaAsync(MapDelta.Create(mergedFromExporter, clearedSeed)
            ?? throw new InvalidOperationException("Clearing the seed did not create a LAN metadata delta."));
        var clearedSnapshot = await placerSession.SynchronizeAsync();
        AssertEquivalentProjectState(clearedSeed, clearedSnapshot, "A remote generation seed could not be cleared.");

        var temporary = Path.Combine(Path.GetTempPath(), $"edmw-lan-export-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temporary);
            var package = GeneratedMapPackage.WriteForSelfTest(mergedFromExporter,
                Path.Combine(temporary, "Remote Placer Export.rmv"));
            AnchorPackage.ValidateFile(package.PayloadPath, mergedFromExporter);
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        }

        // A delta created on the old coordinate system must be rejected—not
        // remapped—if another collaborator resized/replaced the document first.
        var staleTarget = mergedFromExporter.Clone();
        staleTarget.SetCell(4, 4, TerrainKind.Grassland);
        var staleGridDelta = MapDelta.Create(mergedFromExporter, staleTarget)
            ?? throw new InvalidOperationException("The stale-grid LAN edit produced no delta.");
        var resized = MapProject.CreateDefault(28);
        await placerSession.SendProjectAsync(resized);
        try
        {
            await exporterSession.SendDeltaAsync(staleGridDelta);
            throw new InvalidOperationException("The host accepted a LAN delta from the pre-resize grid.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("changed size", StringComparison.Ordinal))
        {
            // Expected: the host also sends its authoritative replacement before
            // rejecting the stale update, keeping the collaborator connected.
        }
        var resizedExporter = await exporterSession.SynchronizeAsync();
        if (!exporterSession.IsConnected || resizedExporter.GridSize != resized.GridSize)
            throw new InvalidOperationException(
                "Stale-grid recovery disconnected or failed to resynchronize the editor.");
    }

    private static MapProject CreateCollaborationBaseline()
    {
        var project = MapProject.CreateDefault(24);
        for (var y = 3; y <= 20; y++)
            for (var x = 3; x <= 20; x++)
                project.SetCell(x, y, TerrainKind.Grassland);
        return project;
    }

    private static MapProject PopulateCollaborativeFeatures(MapProject project)
    {
        project.Name = "Shared Resource Export";
        project.Author = "LAN Team";
        project.Variance = 37;
        project.GenerationSeed = -123456789;
        project.PreserveCoastline = true;
        project.SetCell(4, 4, TerrainKind.Forest);
        project.ComponentRoles[7 * project.GridSize + 7] = (byte)SemanticAnchorRole.PlayerLand;
        project.ElevationMask[8 * project.GridSize + 8] = 60;
        project.CliffMask[8 * project.GridSize + 8] = 1;
        project.RampMask[8 * project.GridSize + 8] = 1;
        project.RampMask[9 * project.GridSize + 9] = 1;
        project.SetCell(19, 19, TerrainKind.Mountain);
        project.ElevationMask[19 * project.GridSize + 19] = MapProject.CliffElevation;
        project.ManualPlayerStarts =
        [
            new ManualPlayerStart { PlayerSlot = 1, X = 10, Y = 10 },
            new ManualPlayerStart { PlayerSlot = 2, X = 16, Y = 16 }
        ];

        var waterIndex = 0;
        var landIndex = 0;
        foreach (var kind in ManualResourceKindExtensions.PaletteKinds)
        {
            if (kind.RequiresWater())
            {
                project.ManualResources.Add(new ManualResource
                    { Kind = kind, X = 1 + waterIndex++, Y = 1 });
            }
            else
            {
                project.ManualResources.Add(new ManualResource
                {
                    Kind = kind,
                    X = 5 + landIndex % 8,
                    Y = 5 + landIndex++ / 8
                });
            }
        }
        project.ManualResources = project.ManualResources
            .OrderBy(resource => resource.Y)
            .ThenBy(resource => resource.X)
            .ThenBy(resource => (byte)resource.Kind)
            .ToList();
        project.Validate();
        return project;
    }

    private static void AssertEquivalentProjectState(MapProject expected, MapProject actual, string message)
    {
        var expectedResources = expected.ManualResources
            .Select(resource => ((byte)resource.Kind, resource.X, resource.Y))
            .OrderBy(resource => resource.Y).ThenBy(resource => resource.X).ThenBy(resource => resource.Item1);
        var actualResources = actual.ManualResources
            .Select(resource => ((byte)resource.Kind, resource.X, resource.Y))
            .OrderBy(resource => resource.Y).ThenBy(resource => resource.X).ThenBy(resource => resource.Item1);
        var expectedStarts = expected.ManualPlayerStarts
            .Select(start => (start.PlayerSlot, start.X, start.Y)).OrderBy(start => start.PlayerSlot);
        var actualStarts = actual.ManualPlayerStarts
            .Select(start => (start.PlayerSlot, start.X, start.Y)).OrderBy(start => start.PlayerSlot);
        if (expected.Name != actual.Name || expected.Author != actual.Author ||
            expected.GridSize != actual.GridSize || expected.Variance != actual.Variance ||
            expected.GenerationSeed != actual.GenerationSeed ||
            expected.PreserveCoastline != actual.PreserveCoastline ||
            !expected.Cells.SequenceEqual(actual.Cells) ||
            !expected.ComponentRoles.SequenceEqual(actual.ComponentRoles) ||
            !expected.RampMask.SequenceEqual(actual.RampMask) ||
            !expected.CliffMask.SequenceEqual(actual.CliffMask) ||
            !expected.ElevationMask.SequenceEqual(actual.ElevationMask) ||
            !expectedResources.SequenceEqual(actualResources) ||
            !expectedStarts.SequenceEqual(actualStarts) ||
            AnchorPackage.MapId(expected) != AnchorPackage.MapId(actual))
            throw new InvalidOperationException(message);
    }

    private static void PaintComponent(MapProject project, int x0, int y0, int x1, int y1, SemanticAnchorRole role)
    {
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                project.SetCell(x, y, TerrainKind.Grassland);
                project.ComponentRoles[y * project.GridSize + x] = (byte)role;
            }
    }

    private static void VerifyFaunaCapabilityAndEvidenceProbe()
    {
        var faunaSelectors = ManualResourceKindExtensions.PaletteKinds
            .Where(kind => kind.IsFauna())
            .ToArray();
        var expectedFaunaSelectors = new[]
        {
            ManualResourceKind.Fish, ManualResourceKind.Dolphin,
            ManualResourceKind.Deer, ManualResourceKind.BighornSheep,
            ManualResourceKind.Bear, ManualResourceKind.Squirrel,
            ManualResourceKind.Sheep, ManualResourceKind.Chicken,
            ManualResourceKind.WildPig, ManualResourceKind.Elk
        };
        if (!faunaSelectors.SequenceEqual(expectedFaunaSelectors) ||
            ManualResourceKindExtensions.PaletteKinds.Contains(ManualResourceKind.Wildlife) ||
            faunaSelectors.Any(kind => !kind.IsUserPlaceable()) ||
            !ManualResourceKind.Gold.IsUserPlaceable() ||
            !ManualResourceKind.Stone.IsUserPlaceable() ||
            !ManualResourceKind.BerryBush.IsUserPlaceable() ||
            faunaSelectors.Count(kind => kind.IsWaterFauna()) != 2 ||
            faunaSelectors.Count(kind => kind.IsLandFauna()) != 8)
            throw new InvalidOperationException(
                "Fauna classification or the active palette policy is incomplete.");

        var expectedTokens = new[]
        {
            "Res-Dolphin", "Res-Deer", "Res-BighornSheep", "Res-Bear",
            "Res-Squirrel", "Res-Sheep", "Res-Chicken",
            "Res-WildPig", "Res-Elk"
        };
        var namedFauna = faunaSelectors.Where(kind => kind.IsNamedFauna()).ToArray();
        var modelAliases = new[]
        {
            ManualResourceKind.Goat, ManualResourceKind.Tiger,
            ManualResourceKind.Shark, ManualResourceKind.BlueBird,
            ManualResourceKind.Walrus,
            ManualResourceKind.Wolf, ManualResourceKind.Horse
        };
        if (!namedFauna.Select(kind => kind.WorldUnitToken()).SequenceEqual(expectedTokens) ||
            namedFauna.Length != 9 || namedFauna.Any(kind => !kind.HasDistinctShippedModel()) ||
            modelAliases.Any(kind => !kind.IsLegacyUnsupportedFauna() || kind.IsUserPlaceable()) ||
            ManualResourceKind.Fish.IsNamedFauna() ||
            !ManualResourceKind.Wildlife.IsLegacyUnsupportedFauna() ||
            ManualResourceKind.Fish.IsLegacyUnsupportedFauna())
            throw new InvalidOperationException("A named fauna token or compatibility identity changed.");

        var wildlifeMap = MapProject.CreateDefault(16);
        for (var y = 4; y < wildlifeMap.GridSize; ++y)
            for (var x = 0; x < wildlifeMap.GridSize; ++x)
                wildlifeMap.SetCell(x, y, TerrainKind.Grassland);
        wildlifeMap.ManualResources.Add(new ManualResource
            { Kind = ManualResourceKind.Fish, X = 1, Y = 1 });
        wildlifeMap.ManualResources.Add(new ManualResource
            { Kind = ManualResourceKind.Deer, X = 2, Y = 5 });
        wildlifeMap.ManualResources.Add(new ManualResource
            { Kind = ManualResourceKind.Dolphin, X = 3, Y = 1 });
        wildlifeMap.Validate();

        if (AnchorPackage.PlacementFlagsFor(wildlifeMap) != 0x0000000Fu)
            throw new InvalidOperationException("Fauna intent did not activate only the dedicated capability bit.");
        var legacyWildlifeMap = wildlifeMap.Clone();
        legacyWildlifeMap.ManualResources =
        [
            new ManualResource { Kind = ManualResourceKind.Wildlife, X = 2, Y = 5 },
            new ManualResource { Kind = ManualResourceKind.Shark, X = 3, Y = 1 },
            new ManualResource { Kind = ManualResourceKind.Goat, X = 4, Y = 5 }
        ];
        legacyWildlifeMap.Validate();
        if (AnchorPackage.PlacementFlagsFor(legacyWildlifeMap) != 0x00000007u)
            throw new InvalidOperationException("A legacy model alias activated the fauna capability bit.");

        var script = RmvGenerator.Generate(wildlifeMap).Script;
        var faunaDirectivePattern = @"(?m)^Place(?:World)?Unit(?:InRing)?\s+\(Res-(?:Deer|Goat|BighornSheep|Tiger|Bear|Shark|Squirrel|Sheep|Eagle|Walrus|Wolf|Chicken|Horse|Dolphin|WildPig|Elk),";
        if (Regex.IsMatch(script, faunaDirectivePattern) ||
            script.Contains("-11861391", StringComparison.Ordinal) ||
            script.Contains("-1048576", StringComparison.Ordinal))
            throw new InvalidOperationException("Manual fauna leaked into the stock RMV parser.");

        const string exactInputScript = "// parser input must remain byte-for-byte unchanged\r\n";
        if (!string.Equals(
                ManualFaunaDirectiveRenderer.Append(exactInputScript, wildlifeMap),
                exactInputScript, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The manual-fauna renderer changed the stock RMV parser input.");

        var fishOnlyMap = wildlifeMap.Clone();
        fishOnlyMap.ManualResources =
        [
            new ManualResource { Kind = ManualResourceKind.Fish, X = 1, Y = 1 }
        ];
        fishOnlyMap.Validate();
        if (Regex.IsMatch(RmvGenerator.Generate(fishOnlyMap).Script, faunaDirectivePattern))
            throw new InvalidOperationException("Fish incorrectly emitted a parser unit directive.");

        var allNamedMap = wildlifeMap.Clone();
        allNamedMap.ManualResources = namedFauna.Select((kind, index) =>
            new ManualResource
            {
                Kind = kind,
                X = 1 + index % 14,
                Y = kind.IsWaterFauna() ? 1 : 5 + index % 10
            }).ToList();
        allNamedMap.Validate();
        if (Regex.IsMatch(RmvGenerator.Generate(allNamedMap).Script, faunaDirectivePattern))
            throw new InvalidOperationException("Named fauna leaked into the stock RMV parser.");

        var duplicateMap = wildlifeMap.Clone();
        duplicateMap.ManualResources =
        [
            new ManualResource { Kind = ManualResourceKind.Deer, X = 2, Y = 5 },
            new ManualResource { Kind = ManualResourceKind.Deer, X = 2, Y = 5 }
        ];
        duplicateMap.Validate();
        if (Regex.IsMatch(RmvGenerator.Generate(duplicateMap).Script, faunaDirectivePattern))
            throw new InvalidOperationException("Duplicate fauna leaked into the stock RMV parser.");

        var reorderedMap = allNamedMap.Clone();
        reorderedMap.ManualResources.Reverse();
        if (Regex.IsMatch(RmvGenerator.Generate(reorderedMap).Script, faunaDirectivePattern))
            throw new InvalidOperationException("Reordered fauna leaked into the stock RMV parser.");

        var notes = RmvGenerator.Generate(wildlifeMap).Notes;
        if (!notes.Any(note => note.Contains("transactionally replace only stock Fish", StringComparison.Ordinal)) ||
            !RmvGenerator.Generate(legacyWildlifeMap).Notes.Any(note =>
                note.Contains("inert compatibility data", StringComparison.Ordinal)))
            throw new InvalidOperationException("Fauna export warnings are not truthful.");

        var probeMap = FaunaEvidenceProbeBuilder.CreateProbeMap();
        var probeNotice = FaunaEvidenceProbeBuilder.BuildFaunaEvidenceNotice();
        if (probeMap.ManualResources.Count(resource => resource.Kind.IsFauna()) != 10 ||
            Regex.IsMatch(probeNotice, @"(?m)^Place(?:World)?Unit(?:InRing)?\s+\(") ||
            !probeNotice.Contains("native DLL", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The evidence notice did not describe the production fauna seam.");

        var temporaryScripts = Path.Combine(
            Path.GetTempPath(), $"edmw-fauna-capability-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temporaryScripts);
            var payload = AnchorPackage.Write(wildlifeMap, temporaryScripts);
            AnchorPackage.ValidateFile(payload, wildlifeMap);
            var expectedLength = AnchorPackage.HeaderSize + wildlifeMap.Cells.Length * 4L +
                                 sizeof(uint) * 2L + wildlifeMap.ManualResources.Count * 9L;
            if (new FileInfo(payload).Length != expectedLength)
                throw new InvalidOperationException("The Version 7 resource-record layout changed.");
            var bytes = File.ReadAllBytes(payload);
            if (BitConverter.ToUInt32(bytes, 24) != 0x0000000Fu)
                throw new InvalidOperationException("The flagged Version 7 payload did not round-trip.");

            var oldUnflagged = (byte[])bytes.Clone();
            BitConverter.GetBytes(0x00000007u).CopyTo(oldUnflagged, 24);
            if ((BitConverter.ToUInt32(oldUnflagged, 24) &
                    AnchorPackage.ManualFaunaPlacementFlag) != 0 ||
                !oldUnflagged.AsSpan(AnchorPackage.HeaderSize).SequenceEqual(
                    bytes.AsSpan(AnchorPackage.HeaderSize)))
                throw new InvalidOperationException("Old unflagged fauna compatibility data was altered.");

            var corruptPath = Path.Combine(temporaryScripts, "unknown-flag.edmwa");
            var unknownFlag = (byte[])bytes.Clone();
            BitConverter.GetBytes(0x0000002Fu).CopyTo(unknownFlag, 24);
            File.WriteAllBytes(corruptPath, unknownFlag);
            try
            {
                AnchorPackage.ValidateFile(corruptPath, wildlifeMap);
                throw new InvalidOperationException("An unknown payload flag was accepted.");
            }
            catch (InvalidDataException)
            {
                // Expected: the payload layout is unchanged and unknown flags fail closed.
            }

            var legacyPayload = AnchorPackage.Write(legacyWildlifeMap, temporaryScripts);
            AnchorPackage.ValidateFile(legacyPayload, legacyWildlifeMap);
            using (var stream = File.OpenRead(legacyPayload))
            using (var reader = new BinaryReader(stream))
            {
                stream.Position = 24;
                if (reader.ReadUInt32() != 0x00000007u)
                    throw new InvalidOperationException("Legacy Wildlife did not remain unflagged.");
            }

            var projectPath = Path.Combine(temporaryScripts, "fauna.edmwmap");
            wildlifeMap.Save(projectPath);
            var loaded = MapProject.Load(projectPath);
            var clone = loaded.Clone();
            if (!loaded.ManualResources.Select(resource => (resource.Kind, resource.X, resource.Y))
                    .SequenceEqual(wildlifeMap.ManualResources.Select(resource =>
                        (resource.Kind, resource.X, resource.Y))) ||
                AnchorPackage.MapId(clone) != AnchorPackage.MapId(wildlifeMap))
                throw new InvalidOperationException("Fauna project save/load or clone changed marker data.");

            clone.Resize(32);
            if (clone.ManualResources.Count != wildlifeMap.ManualResources.Count)
                throw new InvalidOperationException("A valid fauna marker was lost during resize.");
            var invalidated = wildlifeMap.Clone();
            invalidated.SetCell(1, 1, TerrainKind.Grassland);
            invalidated.SetCell(2, 5, TerrainKind.DeepWater);
            if (invalidated.ManualResources.Any(resource =>
                    resource.Kind is ManualResourceKind.Fish or ManualResourceKind.Deer))
                throw new InvalidOperationException("Terrain invalidation retained an incompatible fauna marker.");

            var expectedMapId = AnchorPackage.MapId(wildlifeMap);
            for (var iteration = 0; iteration < 100; ++iteration)
            {
                if (AnchorPackage.MapId(wildlifeMap) != expectedMapId ||
                    AnchorPackage.PlacementFlagsFor(wildlifeMap) != 0x0000000Fu ||
                    RmvGenerator.Generate(wildlifeMap).Script != script)
                    throw new InvalidOperationException("Fauna capability output is not repeatably deterministic.");
            }
        }
        finally
        {
            if (Directory.Exists(temporaryScripts)) Directory.Delete(temporaryScripts, true);
        }
    }

    private static void VerifyStockProfileSelection()
    {
        var cases = new (StockTopologySignature Signature, StockResourceProfile Expected)[]
        {
            (new(1.00, 0.05, 1, 1.00, true, false, 0.00, 0), StockResourceProfile.Plains),
            (new(1.00, 0.40, 1, 1.00, true, false, 0.00, 0), StockResourceProfile.Highlands),
            (new(0.72, 0.05, 1, 1.00, true, false, 0.80, 0), StockResourceProfile.CaspianSea),
            (new(0.90, 0.05, 2, 0.50, true, true, 0.10, 0), StockResourceProfile.Channels),
            (new(0.83, 0.05, 1, 1.00, true, true, 0.05, 0), StockResourceProfile.Coastal),
            (new(0.72, 0.05, 1, 1.00, false, true, 0.05, 0), StockResourceProfile.Continental),
            (new(0.55, 0.05, 3, 0.75, false, true, 0.05, 0), StockResourceProfile.LargeIslands),
            (new(0.75, 0.05, 3, 0.45, true, true, 0.20, 0), StockResourceProfile.Mediterranean),
            (new(0.25, 0.05, 8, 0.20, false, true, 0.05, 0), StockResourceProfile.Oceania),
            (new(0.75, 0.05, 2, 0.55, true, true, 0.10, 0), StockResourceProfile.Peloponnese),
            (new(0.93, 0.05, 1, 1.00, true, true, 0.05, 0), StockResourceProfile.Rivers),
            (new(0.48, 0.05, 6, 0.25, false, true, 0.05, 0), StockResourceProfile.SmallIslands),
            (new(0.55, 0.05, 3, 0.40, false, true, 0.05, 2), StockResourceProfile.TeamIslands)
        };

        foreach (var testCase in cases)
        {
            var selected = StockResourceProfileSelector.Select(testCase.Signature);
            if (selected.Profile != testCase.Expected)
                throw new InvalidOperationException(
                    $"Stock profile classifier selected {selected.Profile} instead of {testCase.Expected}.");
        }
    }

    private static void VerifyRandomLandGeneration()
    {
        const int size = 128;
        const int seed = 314159;
        var first = MapProject.CreateDefault(size);
        first.SetCell(64, 64, TerrainKind.Mountain);
        first.ComponentRoles[64 * size + 64] = (byte)SemanticAnchorRole.PlayerLand;
        var second = first.Clone();

        var firstResult = RandomLandGenerator.Add(first, TerrainKind.Grassland, 0, seed);
        var secondResult = RandomLandGenerator.Add(second, TerrainKind.Grassland, 0, seed);
        var landCells = first.Cells.Count(value => ((TerrainKind)value).IsLand());
        if (firstResult != secondResult ||
            firstResult.AddedCells < first.Cells.Length * 0.02 ||
            firstResult.AddedCells > first.Cells.Length * 0.55 ||
            firstResult.OverwrittenCells != 0 ||
            firstResult.OverwritePercentage != 0 ||
            firstResult.PatchesAdded is < 3 or > 6 ||
            !first.Cells.SequenceEqual(second.Cells) ||
            !first.ComponentRoles.SequenceEqual(second.ComponentRoles) ||
            landCells != firstResult.AddedCells + 1 ||
            first[64, 64] != TerrainKind.Mountain ||
            first.ComponentRoles[64 * size + 64] != (byte)SemanticAnchorRole.PlayerLand ||
            first.Cells.Any(value => (TerrainKind)value is not (
                TerrainKind.DeepWater or TerrainKind.Grassland or TerrainKind.Mountain)))
            throw new InvalidOperationException(
                "Random land generation is not additive, organic, or deterministic for a fixed seed.");

        var fullLand = MapProject.CreateDefault(64);
        Array.Fill(fullLand.Cells, (int)TerrainKind.Forest);
        var fullLandResult = RandomLandGenerator.Add(
            fullLand, TerrainKind.Grassland, 0, seed);
        if (fullLandResult.AddedCells != 0 || fullLandResult.PatchesAdded != 0)
            throw new InvalidOperationException("Random land generation replaced existing land.");

        var halfOverwrite = MapProject.CreateDefault(64);
        Array.Fill(halfOverwrite.Cells, (int)TerrainKind.Forest);
        Array.Fill(halfOverwrite.ComponentRoles, (byte)SemanticAnchorRole.PlayerLand);
        var fullOverwrite = halfOverwrite.Clone();
        var originalRoles = (byte[])halfOverwrite.ComponentRoles.Clone();
        var halfResult = RandomLandGenerator.Add(
            halfOverwrite, TerrainKind.Grassland, 50, seed);
        var fullResult = RandomLandGenerator.Add(
            fullOverwrite, TerrainKind.Grassland, 100, seed);
        var expectedHalf = (int)Math.Round(
            fullResult.OverwrittenCells * 0.5, MidpointRounding.AwayFromZero);
        if (halfResult.AddedCells != 0 || fullResult.AddedCells != 0 ||
            fullResult.OverwrittenCells <= 0 ||
            halfResult.OverwrittenCells != expectedHalf ||
            halfResult.PatchesAdded != fullResult.PatchesAdded ||
            halfOverwrite.Cells.Count(value => value == (int)TerrainKind.Grassland) !=
                halfResult.OverwrittenCells ||
            fullOverwrite.Cells.Count(value => value == (int)TerrainKind.Grassland) !=
                fullResult.OverwrittenCells ||
            !halfOverwrite.ComponentRoles.SequenceEqual(originalRoles) ||
            !fullOverwrite.ComponentRoles.SequenceEqual(originalRoles))
            throw new InvalidOperationException(
                "Random land did not apply the requested exact overwrite percentage.");

        var cliffRepaint = MapProject.CreateDefault(64);
        Array.Fill(cliffRepaint.Cells, (int)TerrainKind.Mountain);
        Array.Fill(cliffRepaint.ElevationMask, MapProject.CliffElevation);
        var cliffRepaintResult = RandomLandGenerator.Add(cliffRepaint, TerrainKind.Grassland, 100, seed);
        cliffRepaint.Validate();
        if (cliffRepaintResult.OverwrittenCells <= 0 ||
            cliffRepaint.ElevationMask.Where((value, index) =>
                value != (cliffRepaint.Cells[index] == (int)TerrainKind.Mountain
                    ? MapProject.CliffElevation : MapProject.AutomaticElevation)).Any())
            throw new InvalidOperationException("Random land repaint left cliff markers on overwritten terrain.");

        try
        {
            RandomLandGenerator.Add(
                MapProject.CreateDefault(64), TerrainKind.Shallows, 0, seed);
            throw new InvalidOperationException("Random land generation accepted water terrain.");
        }
        catch (ArgumentException)
        {
            // Expected: the UI substitutes Grassland when the active terrain is water.
        }
        try
        {
            RandomLandGenerator.Add(
                MapProject.CreateDefault(64), TerrainKind.Grassland, 101, seed);
            throw new InvalidOperationException(
                "Random land generation accepted an invalid overwrite percentage.");
        }
        catch (ArgumentOutOfRangeException)
        {
        }
    }

    private static int VerifyStockProfiles(string scriptsFolder)
    {
        try
        {
            var templateProject = MapProject.CreateDefault(16);
            var generatorName = AnchorPackage.GeneratorName(AnchorPackage.MapId(templateProject));
            foreach (var profile in Enum.GetValues<StockResourceProfile>())
            {
                var selection = StockResourceProfileSelector.Describe(profile);
                var expanded = StockResourceProfileImporter.Import(scriptsFolder, selection);
                var mainAttributes = StockResourceProfileImporter.ImportMainAttributes(scriptsFolder, selection);
                var sizeProfiles = Regex.Matches(expanded, "resource/forest policy", RegexOptions.IgnoreCase).Count;
                var twoPlayerBranches = Regex.Matches(expanded, @"(?m)^#if Is2Players\r?$").Count;
                var eightPlayerBranches = Regex.Matches(expanded, @"(?m)^#elif Is8Players\r?$").Count;
                var firstSizeBranch = Regex.Matches(expanded, @"(?m)^#if IsTinyMap\r?$").Count;
                var remainingSizeBranches = Regex.Matches(expanded,
                    @"(?m)^#elif Is(?:Small|Medium|Large|Huge)Map\r?$").Count;
                var ifCount = Regex.Matches(expanded, @"(?m)^\s*#if\b").Count;
                var endIfCount = Regex.Matches(expanded, @"(?m)^\s*#endif\b").Count;
                if (sizeProfiles != 5 || twoPlayerBranches != 5 || eightPlayerBranches != 5 ||
                    firstSizeBranch != 1 || remainingSizeBranches != 4 || ifCount != endIfCount ||
                    expanded.Contains("UseMapGenerator", StringComparison.Ordinal) ||
                    expanded.Contains("PlayerInnerRadius", StringComparison.Ordinal) ||
                    expanded.Contains("kPercentLand", StringComparison.Ordinal) ||
                    mainAttributes.Contains("UseMapGenerator", StringComparison.Ordinal) ||
                    mainAttributes.Contains("PercentLand", StringComparison.Ordinal) ||
                    mainAttributes.Contains("PlayerInnerRadius", StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"Expanded {selection.IncludeStem} policy is incomplete or contains topology settings " +
                        $"(sizes {sizeProfiles}, P2 {twoPlayerBranches}, P8 {eightPlayerBranches}, " +
                        $"size branches {firstSizeBranch}+{remainingSizeBranches}, if/endif {ifCount}/{endIfCount}).");

                if (profile == StockResourceProfile.SmallIslands)
                {
                    var gold = Regex.Matches(expanded,
                        @"#define\s*\(\s*kGoldPerPlayer\s*,\s*(\d+)", RegexOptions.IgnoreCase);
                    var stone = Regex.Matches(expanded,
                        @"#define\s*\(\s*kStonePerPlayer\s*,\s*(\d+)", RegexOptions.IgnoreCase);
                    if (gold.Count != 35 || stone.Count != 35 ||
                        gold.Cast<Match>().Any(match => int.Parse(match.Groups[1].Value) <= 0) ||
                        stone.Cast<Match>().Any(match => int.Parse(match.Groups[1].Value) <= 0))
                        throw new InvalidDataException(
                            "Small Islands does not guarantee nonzero player Gold and Stone for every size and 2-8 players.");
                }

                var sourcePath = Path.Combine(Path.GetFullPath(scriptsFolder),
                    $"{selection.IncludeStem}.rmv");
                var originalMain = File.ReadAllText(sourcePath);
                var renderedMain = StockMapTemplateRenderer.RenderTemplateBody(
                    scriptsFolder, selection, generatorName);
                var riverLandFillOverrides = Regex.Matches(renderedMain,
                    @"(?im)^\s*RiverLandFill\s+false(?:\s*//[^\r\n]*)?\r?$");
                if (!NormalizeStockTemplate(originalMain).Equals(
                        NormalizeStockTemplate(renderedMain), StringComparison.Ordinal) ||
                    riverLandFillOverrides.Count != 1 ||
                    !renderedMain.Contains("UseMapGenerator", StringComparison.Ordinal) ||
                    !renderedMain.Contains($"\"{generatorName}\"", StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"Rendered {selection.IncludeStem} template changed content outside generator, " +
                        "map-identity, and anchored RiverLandFill directives.");

                var legacyRendered = StockMapTemplateRenderer.Render(scriptsFolder, selection, templateProject);
                var preservedProject = templateProject.Clone();
                preservedProject.PreserveCoastline = true;
                var preservedRendered = StockMapTemplateRenderer.Render(scriptsFolder, selection, preservedProject);
                const string beachPattern = @"(?im)^\s*PaintBeaches\s+(?:true|false)[^\r\n]*";
                var sourceBeachDirectives = Regex.Matches(originalMain, beachPattern).Select(match => match.Value.Trim());
                var legacyBeachDirectives = Regex.Matches(legacyRendered, beachPattern).Select(match => match.Value.Trim());
                if (!sourceBeachDirectives.SequenceEqual(legacyBeachDirectives) ||
                    !Regex.IsMatch(preservedRendered, @"(?m)^PaintBeaches\s+false\s*\z") ||
                    preservedRendered.LastIndexOf("PaintBeaches", StringComparison.Ordinal) <=
                    preservedRendered.LastIndexOf("#include", StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"Rendered {selection.IncludeStem} template did not preserve legacy coasts or apply the final authored-shore override.");
            }

            Console.WriteLine("STOCK PROFILE VERIFICATION PASS (13 stock-derived templates, legacy and authored shoreline policies; 65 original size files; 455 player-count branches)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("STOCK PROFILE VERIFICATION FAIL: " + ex);
            return 1;
        }
    }

    private static string NormalizeStockTemplate(string script)
    {
        var normalized = Regex.Replace(script,
            "(?m)^(?<prefix>\\s*UseMapGenerator\\s+)\"[^\"\\r\\n]+\"(?<suffix>[^\\r\\n]*)(?=\\r?$)",
            "${prefix}\"<GENERATOR>\"${suffix}");
        normalized = Regex.Replace(normalized,
            @"(?m)^(?<prefix>\s*StringTableID\s+)\d+(?<suffix>[^\r\n]*)(?=\r?$)",
            "${prefix}<STRING_TABLE_ID>${suffix}");
        return Regex.Replace(normalized,
            @"(?m)^\s*RiverLandFill\s+\S+[^\r\n]*(?:\r?\n|$)",
            string.Empty);
    }

    private static int UpgradeResourcePolicy(string scriptPath, string scriptsFolder)
    {
        try
        {
            var backup = StockResourcePolicyUpgrader.Upgrade(scriptPath, scriptsFolder);
            Console.WriteLine($"RESOURCE POLICY UPGRADED: {Path.GetFullPath(scriptPath)}");
            Console.WriteLine($"BACKUP: {backup}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("RESOURCE POLICY UPGRADE FAIL: " + ex);
            return 1;
        }
    }

}
