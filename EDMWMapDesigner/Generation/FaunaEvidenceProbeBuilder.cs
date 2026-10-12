using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EDMWMapDesigner.Analysis;
using EDMWMapDesigner.Models;

namespace EDMWMapDesigner.Generation;

internal sealed record FaunaEvidenceProbeArtifacts(
    string InstallerPath,
    string ManifestPath,
    string InstructionsPath,
    string PackagePath,
    string ProjectPath,
    string ScriptPath,
    string PayloadPath);

internal static class FaunaEvidenceProbeBuilder
{
    internal const string ProbeName = "EDMW Fauna Evidence Probe";

    internal static FaunaEvidenceProbeArtifacts Build(
        string outputFolder, string stockScriptsFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(stockScriptsFolder);
        var output = Path.GetFullPath(outputFolder);
        var stockScripts = Path.GetFullPath(stockScriptsFolder);
        Directory.CreateDirectory(output);
        var embeddedGenerator = EmbeddedGenerator.Read();
        if (embeddedGenerator.AsSpan().IndexOf(
                Encoding.ASCII.GetBytes("fauna_payload")) < 0)
            throw new InvalidOperationException(
                "The acceptance package requires a diagnostic native build. " +
                "Build with EDMW_DIAGNOSTICS=1 before invoking this command.");

        var map = CreateProbeMap();
        var profile = StockResourceProfileSelector.Select(
            map, MapAnalyzer.Analyze(map), SemanticAnchorAnalyzer.Analyze(map));
        if (!profile.IncludesFish || !profile.UsesAnimalSymbols)
            throw new InvalidOperationException(
                "The evidence map must select a stock profile containing both Fish and Animal categories.");

        var scriptPath = Path.Combine(output, ProbeName + ".rmv");
        var package = GeneratedMapPackage.Write(map, scriptPath, stockScripts);
        File.AppendAllText(scriptPath, BuildFaunaEvidenceNotice(), new UTF8Encoding(false));
        var generatedScript = File.ReadAllText(scriptPath);
        const string namedFaunaDirectivePattern =
            @"(?m)^\s*Place(?:World)?Unit(?:InRing)?\s+\(Res-(?:Deer|Goat|BighornSheep|Tiger|Bear|Shark|Squirrel|Sheep|Eagle|Walrus|Wolf|Chicken|Horse|Dolphin|WildPig|Elk),";
        if (Regex.IsMatch(generatedScript, namedFaunaDirectivePattern) ||
            generatedScript.Contains("-11861391", StringComparison.Ordinal) ||
            generatedScript.Contains("-1048576", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The acceptance RMV contains a forbidden manual-fauna parser directive or legacy sentinel coordinate.");
        AnchorPackage.ValidateFile(package.PayloadPath, map);
        if ((AnchorPackage.PlacementFlagsFor(map) & AnchorPackage.ManualFaunaPlacementFlag) == 0)
            throw new InvalidOperationException("The evidence payload is missing its fauna capability bit.");

        var projectPath = Path.Combine(output, ProbeName + ".edmwmap");
        map.Save(projectPath);
        var zipPath = Path.Combine(output, ProbeName + ".diagnostic-package.zip");
        MultiplayerPackageExporter.ExportDiagnosticProbe(map, package, zipPath);
        var installerPath = Path.Combine(output, ProbeName + " Installer.exe");
        MultiplayerInstallerBuilder.Create(zipPath, installerPath);

        var manifestPath = Path.Combine(output, "fauna-evidence-manifest.json");
        var instructionsPath = Path.Combine(output, "ONE-RUN-INSTRUCTIONS.txt");
        WriteManifest(map, profile, installerPath, zipPath, package, projectPath, manifestPath);
        WriteInstructions(map, installerPath, manifestPath, instructionsPath);
        return new FaunaEvidenceProbeArtifacts(
            installerPath, manifestPath, instructionsPath, zipPath, projectPath,
            package.ScriptPath, package.PayloadPath);
    }

    internal static MapProject CreateProbeMap()
    {
        const int size = 128;
        var map = MapProject.CreateDefault(size);
        map.Name = ProbeName;
        map.Author = "EDMW Mod Studio evidence gate";
        map.Variance = 0;

        for (var y = 20; y <= 107; ++y)
        {
            for (var x = 20; x <= 107; ++x)
            {
                var shore = x is 20 or 107 || y is 20 or 107;
                map.SetCell(x, y, shore ? TerrainKind.Beach : TerrainKind.Grassland);
            }
        }
        for (var y = 28; y <= 35; ++y)
            for (var x = 50; x <= 61; ++x)
                map.SetCell(x, y, TerrainKind.Hills);
        for (var y = 28; y <= 35; ++y)
            for (var x = 66; x <= 77; ++x)
                map.SetCell(x, y, TerrainKind.Mountain);
        for (var y = 84; y <= 91; ++y)
            for (var x = 42; x <= 53; ++x)
                map.SetCell(x, y, TerrainKind.Forest);
        for (var y = 58; y <= 69; ++y)
            for (var x = 20; x <= 24; ++x)
                map.SetCell(x, y, TerrainKind.Beach);

        map.ManualPlayerStarts =
        [
            new ManualPlayerStart { PlayerSlot = 1, X = 32, Y = 96 },
            new ManualPlayerStart { PlayerSlot = 2, X = 96, Y = 96 }
        ];

        map.ManualResources =
        [
            new ManualResource { Kind = ManualResourceKind.Gold, X = 56, Y = 92 },
            new ManualResource { Kind = ManualResourceKind.Stone, X = 64, Y = 92 },
            new ManualResource { Kind = ManualResourceKind.BerryBush, X = 72, Y = 92 },

            new ManualResource { Kind = ManualResourceKind.Fish, X = 12, Y = 16 },
            new ManualResource { Kind = ManualResourceKind.Dolphin, X = 112, Y = 16 },

            new ManualResource { Kind = ManualResourceKind.Deer, X = 32, Y = 32 },
            new ManualResource { Kind = ManualResourceKind.BighornSheep, X = 68, Y = 32 },
            new ManualResource { Kind = ManualResourceKind.Bear, X = 100, Y = 32 },
            new ManualResource { Kind = ManualResourceKind.Squirrel, X = 32, Y = 56 },
            new ManualResource { Kind = ManualResourceKind.Sheep, X = 48, Y = 56 },
            new ManualResource { Kind = ManualResourceKind.Chicken, X = 32, Y = 76 },
            new ManualResource { Kind = ManualResourceKind.WildPig, X = 76, Y = 76 },
            new ManualResource { Kind = ManualResourceKind.Elk, X = 95, Y = 96 }
        ];

        map.ElevationMask[32 * size + 52] = 35;
        map.ElevationMask[32 * size + 68] = 70;
        map.ElevationMask[56 * size + 64] = 45;
        map.ElevationMask[16 * size + 12] = -45;
        map.RampMask[36 * size + 68] = 1;
        map.Validate();
        return map;
    }

    internal static string BuildFaunaEvidenceNotice()
    {
        var output = new StringBuilder();
        output.AppendLine();
        output.AppendLine("//////////////////////////////////////////////////////////////////////");
        output.AppendLine("// MANUAL FAUNA ACCEPTANCE MAP.");
        output.AppendLine("// The native DLL places named species directly after stock world generation.");
        output.AppendLine("// No manual fauna directives are sent through the stock RMV parser.");
        output.AppendLine("// Fish uses the engine's validated resource kind 5 global consumer.");
        output.AppendLine("//////////////////////////////////////////////////////////////////////");
        return output.ToString();
    }

    private static IEnumerable<ManualResource> OrderedFauna(MapProject map) =>
        map.ManualResources
            .Select((resource, sequence) => (Resource: resource, Sequence: sequence))
            .Where(item => item.Resource.Kind.HasDistinctShippedModel())
            .OrderBy(item => item.Resource.Kind.FaunaCategory())
            .ThenBy(item => (byte)item.Resource.Kind)
            .ThenBy(item => item.Resource.Y)
            .ThenBy(item => item.Resource.X)
            .ThenBy(item => item.Sequence)
            .Select(item => item.Resource);

    private static void WriteManifest(
        MapProject map,
        StockResourceProfileSelection profile,
        string installerPath,
        string zipPath,
        MapPackageResult package,
        string projectPath,
        string manifestPath)
    {
        var fauna = OrderedFauna(map).Select((resource, sortedIndex) => new
        {
            sortedIndex,
            kindByte = (byte)resource.Kind,
            kind = resource.Kind.ToString(),
            displayName = resource.Kind.DisplayName(),
            category = resource.Kind.FaunaCategory().ToString().ToLowerInvariant(),
            exactToken = resource.Kind.IsNamedFauna()
                ? resource.Kind.WorldUnitToken()
                : "Stock-Fish-Category",
            authoredCell = new { x = resource.X, y = resource.Y },
            nativeDirectPlacement = resource.Kind.IsNamedFauna()
        }).ToArray();
        var generatorName = AnchorPackage.GeneratorName(AnchorPackage.MapId(map));
        var manifest = new
        {
            schema = "edmw-fauna-acceptance-v8",
            purpose = "Diagnostic acceptance package for deterministic manual Fish and distinct-model fauna placement.",
            map = new
            {
                name = map.Name,
                gridSize = map.GridSize,
                variance = map.Variance,
                mapId = $"{AnchorPackage.MapId(map):X6}",
                generatorName,
                payloadMagic = "EDMWA07",
                payloadFlags = $"0x{AnchorPackage.PlacementFlagsFor(map):X8}",
                stockProfile = profile.IncludeStem,
                waterCategoryPolicy = "Authored Fish/Dolphin transactionally replace stock Fish; rollback preserves stock.",
                landCategoryPolicy = "Authored named land fauna transactionally replace stock Animal; rollback preserves stock."
            },
            runtimeSeamEvidence = new
            {
                directive = "none; manual fauna is absent from the RMV parser",
                descriptorIdentity = "Exact build-specific descriptor IDs are compiled into the native DLL and each is validated against its expected installed global object before use.",
                unitVectorContextOffset = "0x2BC; begin/end/capacity range starts at +0x04",
                unitRecordBytes = 24,
                resourceVectorContextOffset = "0x248",
                globalUnitConsumer = "Empires_DMW.exe 0x007994E3, after Resources and Trees",
                resourceConsumer = "Empires_DMW.exe 0x0079DCA9; kind 5 hard-codes Fish global ID 315",
                ownership = "The native DLL plans from the payload after Trees, stages any required MSVCRT-backed vector growth, then commits validated named-unit and Fish records without changing parser coordinates or cached bounds.",
                orientation = "Terrain and direct named-unit records mirror the editor's Y axis exactly once. Fish, forests, Gold, Stone, and Berry retain the separate resource-record transpose. The established player-start path is unchanged.",
                finalCellGuard = "World height is checked only for finiteness because it has a different scale from generator height. Final cell kinds mirror the consumers: Dolphin requires kind 1, Fish accepts 1 or 10, and land uses the finalized generator topology without a positive world-kind requirement.",
                status = "Implemented with category-level rollback, stock preservation, exact build/descriptor gates, and deterministic placement."
            },
            fauna,
            preservationMarkers = map.ManualResources
                .Where(resource => !resource.Kind.IsFauna())
                .Select(resource => new
                {
                    kindByte = (byte)resource.Kind,
                    kind = resource.Kind.ToString(),
                    cell = new { x = resource.X, y = resource.Y }
                }).ToArray(),
            manualPlayerStarts = map.ManualPlayerStarts
                .OrderBy(start => start.PlayerSlot)
                .Select(start => new { start.PlayerSlot, start.X, start.Y }).ToArray(),
            expectedDiagnostics = new
            {
                trace = generatorName + ".trace.log",
                faunaJsonLinesGlob = generatorName + ".fauna.*.jsonl",
                location = "The EDMW game folder beside the installed generator DLL.",
                events = new[]
                {
                    "candidate_vector_header", "unified_unit_placement_vector",
                    "unified_unit_placement_record", "place_wall_vector",
                    "fauna_payload", "fauna_marker", "placement_digest",
                    "resource_vector", "resource_record", "manual_fauna_placement"
                }
            },
            evidenceGate = new[]
            {
                "The generated RMV contains no manual-fauna PlaceUnit or PlaceWorldUnit directives and leaves parser cached bounds untouched.",
                "Generation reaches Resources and Trees without changing manual player-start records.",
                "Each authored Fish marker produces one kind-5 Fish record at the deterministic nearest validated deep-water cell.",
                "Each authored named marker resolves to its exact descriptor and global database identity before conversion.",
                "Terrain, Dolphin, and named land fauna match the editor top-to-bottom; player starts, Fish, forests, Gold, Stone, and Berry retain their already-correct coordinates.",
                "Water and land categories commit independently; a failed category preserves all of its stock behavior.",
                "The same payload produces byte-identical plans and digests on every peer."
            },
            artifacts = new
            {
                installer = Path.GetFileName(installerPath),
                installerSha256 = Sha256(installerPath),
                package = Path.GetFileName(zipPath),
                packageSha256 = Sha256(zipPath),
                project = Path.GetFileName(projectPath),
                projectSha256 = Sha256(projectPath),
                script = Path.GetFileName(package.ScriptPath),
                scriptSha256 = Sha256(package.ScriptPath),
                payload = Path.GetFileName(package.PayloadPath),
                payloadSha256 = Sha256(package.PayloadPath)
            }
        };
        File.WriteAllText(manifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    private static void WriteInstructions(
        MapProject map, string installerPath, string manifestPath, string instructionsPath)
    {
        var generatorName = AnchorPackage.GeneratorName(AnchorPackage.MapId(map));
        var text = $"""
            EDMW MANUAL FAUNA — ACCEPTANCE PACKAGE

            The offline implementation and automated verification are complete. This package is the
            focused live acceptance case for manual Fish and the nine user-approved named
            fauna types whose shipped database entries have matching models.

            It preserves the same proven manual-player-start path. Manual fauna is absent from the
            stock RMV parser. After Trees, the native DLL validates the installed executable, the exact
            descriptor-to-global-object identity, finalized topology, resource reservations, and vector
            ownership before transactionally appending named species. Fish uses the separately proved
            resource kind 5 consumer. No parser coordinates or cached bounds are changed.

            This focused acceptance package was built with diagnostics enabled. The normal production
            application and its exported maps keep diagnostics disabled. Expected files from this test are:
              {generatorName}.trace.log
              {generatorName}.fauna.<process-id>.jsonl

            Exact identities, hashes, and acceptance criteria are in:
              {Path.GetFileName(manifestPath)}

            Run this package only as an explicitly authorized acceptance test. Confirm the map loads,
            player starts remain correct; terrain, Dolphin, and named land fauna match the editor
            top-to-bottom; and Fish, forests, Gold, Stone, and Berry remain unchanged.
            """;
        File.WriteAllText(instructionsPath, text, new UTF8Encoding(false));
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
