using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace EDMWMapDesigner.Civilizations;

/// <summary>Nonvisual acceptance of actual source joins and read-only import.</summary>
internal static class CivilizationSourcesVerification
{
    public static int Run(string gamePath, string outputDirectory)
    {
        try
        {
            var catalog = CivilizationGameCatalog.Load(gamePath);
            Verify(gamePath, new CivilizationGameData(gamePath, catalog), catalog,
                CivilizationGameCatalog.ImportCivilizations(gamePath), outputDirectory);
            Console.WriteLine("CIVILIZATION SOURCES VERIFICATION PASS");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("CIVILIZATION SOURCES VERIFICATION FAIL: " + exception);
            return 1;
        }
    }

    public static IReadOnlyList<string> Verify(string gamePath, CivilizationGameData gameData,
        CivilizationGameCatalog catalog, IReadOnlyList<CivilizationDraft> civilizations,
        string outputDirectory)
    {
        var elapsed = Stopwatch.StartNew();
        Console.WriteLine("SOURCE TIMING start: source hash and table reads");
        Directory.CreateDirectory(outputDirectory);
        var hashes = SourceHashes(gamePath);
        var objects = File.ReadAllBytes(Path.Combine(gamePath, "Data", "db", "dbobjects.dat"));
        var techBytes = File.ReadAllBytes(Path.Combine(gamePath, "Data", "db", "dbtechtree.dat"));
        var events = File.ReadAllBytes(Path.Combine(gamePath, "Data", "db", "dbevents.dat"));
        var effects = File.ReadAllBytes(Path.Combine(gamePath, "Data", "db", "dbeffects.dat"));
        var calamities = File.ReadAllBytes(Path.Combine(gamePath, "Data", "db", "dbcalamity.dat"));
        var civBytes = File.ReadAllBytes(Path.Combine(gamePath, "Data", "db", "dbphoenixcivtable.dat"));
        var powerBytes = new CivilizationGameAssets(gamePath).ReadAssetFile(@"db\dbcivpowers.dat");
        var records = CivilizationTechnologyBindings.ReadRecords(techBytes);
        var eventRecords = CivilizationAbilityData.ReadEvents(events);
        var byId = records.ToDictionary(record => record.TechId);
        var details = new CivilizationAbilityData(objects, records, events, effects, calamities, civBytes, powerBytes);
        var checks = new List<string>();
        var report = new List<object>();
        var allIds = new HashSet<string>(StringComparer.Ordinal);

        Require(civilizations.Count == 9, "The active civilization identities did not import.");
        var pristine = new CivilizationEditProject { GamePath = gamePath, Civilizations = civilizations.Select(civ => civ.Clone()).ToList() }.Serialize();
        foreach (var civilization in civilizations)
        {
            var civilizationElapsed = Stopwatch.StartNew();
            var row = civilization.SourceRowIndex ?? throw new InvalidOperationException("Missing civilization source row.");
            Console.WriteLine($"SOURCE TIMING {civilization.Name}: current identity/supplemental values begin ({elapsed.ElapsedMilliseconds} ms total)");
            var civValues = gameData.GetValues(civilization);
            Console.WriteLine($"SOURCE TIMING {civilization.Name}: current identity/supplemental values complete ({civilizationElapsed.ElapsedMilliseconds} ms)");
            Require(civValues.GetValueOrDefault("overview.displayName") is { Length: > 0 } &&
                civValues.GetValueOrDefault("overview.internalName") is { Length: > 0 },
                $"{civilization.Name} has no current identity text.");
            var lower = int.Parse(civValues["age.initialAge"], CultureInfo.InvariantCulture);
            var upper = int.Parse(civValues["age.finalAge"], CultureInfo.InvariantCulture);
            var civilizationOffset = 4 + row * 344;
            // Loader 0x0051EA62/65 copies raw +300 to runtime +0x44;
            // raw +304/+308 become runtime +0x5C/+0x60 at EA8C..EA95.
            var initializerId = BinaryPrimitives.ReadInt32LittleEndian(civBytes.AsSpan(civilizationOffset + 300, 4));
            var leaderFirst = BinaryPrimitives.ReadUInt32LittleEndian(civBytes.AsSpan(civilizationOffset + 304, 4));
            var leaderLast = BinaryPrimitives.ReadUInt32LittleEndian(civBytes.AsSpan(civilizationOffset + 308, 4));
            Require(byId.TryGetValue(initializerId, out var initializer) && initializer.NodeKind == 12 &&
                initializer.CivilizationRowIndex == row && initializer.AdditionalTechnologyIds.Count > 0 &&
                civValues.GetValueOrDefault("overview.initializationTechnology", "").Contains(initializerId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
                civValues.GetValueOrDefault("overview.bonuses") is { Length: > 0 },
                $"{civilization.Name} did not join the actual +300 initializer and applied bonus technologies.");
            Require(leaderFirst is > 0 and <= 65535 && leaderLast >= leaderFirst && leaderLast <= 65535 &&
                civValues.GetValueOrDefault("assets.leaderNames") is { Length: > 0 },
                $"{civilization.Name} did not load the localized +304/+308 leader interval.");
            var availableObjects = new[] { CivilizationCategory.Units, CivilizationCategory.Buildings }
                .SelectMany(category => gameData.GetRoster(civilization, category))
                .Select(entity => entity.SourceObjectId!.Value).ToHashSet();
            var categoryCounts = new Dictionary<string, int>();
            var sourceFieldCounts = new Dictionary<string, int>();
            foreach (var category in new[] { CivilizationCategory.Units, CivilizationCategory.Buildings,
                CivilizationCategory.Technologies, CivilizationCategory.Abilities })
            {
                var categoryElapsed = Stopwatch.StartNew();
                Console.WriteLine($"SOURCE TIMING {civilization.Name}/{category}: roster begin");
                var roster = gameData.GetRoster(civilization, category);
                // Compute native membership once per category, rather than
                // rebuilding the complete producer closure for every entry.
                var expectedTechnologyIds = category switch
                {
                    CivilizationCategory.Technologies => CivilizationTechnologyBindings.GetCivilizationTechnologyRecords(
                        objects, records, row, lower, upper).Select(item => item.TechId).ToHashSet(),
                    CivilizationCategory.Abilities => details.GetRoster(row, lower, upper).Select(item => item.TechId).ToHashSet(),
                    _ => CivilizationTechnologyBindings.GetCivilizationObjectIds(objects, records, row, category, lower, upper)
                        .Select(id => checked((int)CivilizationObjectBindings.ReadTechnologyId(objects, id, category))).ToHashSet()
                };
                Console.WriteLine($"SOURCE TIMING {civilization.Name}/{category}: {roster.Count} entries/current values begin ({categoryElapsed.ElapsedMilliseconds} ms)");
                Require(roster.Count > 0, $"{civilization.Name}/{category} has no actual source entries.");
                Require(roster.Select(entity => entity.Id).Distinct(StringComparer.Ordinal).Count() == roster.Count,
                    $"{civilization.Name}/{category} duplicates source entries.");
                var fields = 0;
                var prefix = category switch { CivilizationCategory.Units => "unit", CivilizationCategory.Buildings => "building",
                    CivilizationCategory.Technologies => "technology", _ => "ability" };
                foreach (var entity in roster)
                {
                    Require(entity.Properties.Count == 0 && allIds.Add(entity.Id),
                        "Source navigation authored overrides or reused an identity across civilizations/categories.");
                    if (entity.SourcePowerId is int powerId)
                    {
                        Require(category == CivilizationCategory.Abilities && entity.SourceObjectId is null,
                            "A civilization power has an incorrect category or object identity.");
                        var power = details.GetPowerRecords(row).SingleOrDefault(record => record.Index == powerId);
                        Require(power is not null && power.ActivationTechnologyId == entity.SourceTechnologyId,
                            "A civilization power is not assigned by the actual Phoenix power slots.");
                        var powerValues = gameData.GetValues(entity);
                        Require(powerValues.GetValueOrDefault("ability.sourcePowerId") == powerId.ToString(CultureInfo.InvariantCulture) &&
                            powerValues.GetValueOrDefault("ability.displayName") is { Length: > 0 } &&
                            powerValues.GetValueOrDefault("ability.owners") is { Length: > 0 },
                            "A selected civilization power did not expose its actual identity/name/assignment.");
                        if (power!.ActivationTechnologyId is int activationId &&
                            byId[activationId].EventId < eventRecords.Count && eventRecords[(int)byId[activationId].EventId].EffectIds.Count > 0)
                            Require(powerValues.GetValueOrDefault("ability.effects") is { Length: > 0 } &&
                                powerValues.GetValueOrDefault("ability.effectOperations") is { Length: > 0 },
                                "A civilization power's native activation effect chain did not load.");
                        fields += powerValues.Count;
                        continue;
                    }
                    var technologyId = entity.SourceTechnologyId ?? checked((int)CivilizationObjectBindings.ReadTechnologyId(
                        objects, entity.SourceObjectId!.Value, category));
                    var source = byId[technologyId];
                    Require(source.AllowsCivilizationRow(row) && source.StartAge <= upper && source.EndAge >= lower && !source.EditorOnly,
                        $"{civilization.Name}/{entity.Name} fails the exact source mask or age bounds.");
                    Require(expectedTechnologyIds.Contains(technologyId),
                        $"{civilization.Name}/{entity.Name} lacks an available native producer/action assignment.");
                    if (source.CivilizationRowIndex != row && category is CivilizationCategory.Units or CivilizationCategory.Buildings)
                        Require(source.ProducerObjectIds.Any(availableObjects.Contains),
                            "A shared or legacy-tagged object is not linked to an available producer.");
                    if (category == CivilizationCategory.Technologies)
                        Require(CivilizationTechnologyBindings.IsResearchRecord(source), "A command/power leaked into research.");
                    if (category == CivilizationCategory.Abilities)
                        Require(CivilizationTechnologyBindings.IsAbilityRecord(source), "A production/initialization node leaked into abilities.");
                    var values = gameData.GetValues(entity);
                    Require(values.Count > 0 && values.GetValueOrDefault(prefix + ".internalName") is { Length: > 0 } &&
                        values.GetValueOrDefault(prefix + ".displayName") is { Length: > 0 },
                        $"{civilization.Name}/{entity.Name} lacks current source identity values.");
                    foreach (var cost in new[] { "foodCost", "woodCost", "goldCost", "stoneCost", "productionCoefficient" })
                        Require(values.TryGetValue(prefix + "." + cost, out var raw) && uint.TryParse(raw, out _),
                            $"{civilization.Name}/{entity.Name} has no actual {cost}.");
                    if (category is CivilizationCategory.Units or CivilizationCategory.Buildings)
                    {
                        Require(values.ContainsKey(prefix + ".health") && values.ContainsKey(prefix + ".graphicsId"),
                            "An actual object did not expose stats and assets.");
                        Require(values.Keys.Any(key => key == prefix + ".commands"),
                            "An actual object did not expose its source command references.");
                    }
                    else
                    {
                        Require(values.ContainsKey(prefix + ".sourceNodeKind") && values.ContainsKey(prefix + ".prerequisites"),
                            "A technology/ability did not expose subtype and prerequisite metadata.");
                        if (source.EventId < 1500 && eventRecords[(int)source.EventId].EffectIds.Count > 0)
                            Require(values.GetValueOrDefault(prefix + ".effects") is { Length: > 0 } &&
                                values.GetValueOrDefault(prefix + ".effectOperations") is { Length: > 0 },
                                "A linked event/effect chain was not exposed in current values.");
                        if (source.NodeKind == 5)
                            Require(values.GetValueOrDefault("ability.calamityReference") is { Length: > 0 },
                                "A native calamity source did not expose its referenced table row.");
                    }
                    fields += values.Count;
                }
                categoryCounts[category.ToString()] = roster.Count;
                sourceFieldCounts[category.ToString()] = fields;
                Console.WriteLine($"SOURCE TIMING {civilization.Name}/{category}: {fields} fields complete ({categoryElapsed.ElapsedMilliseconds} ms)");
                if (category == CivilizationCategory.Abilities)
                    Require(roster.Where(entity => entity.SourcePowerId is not null).Select(entity => entity.SourcePowerId!.Value)
                        .ToHashSet().SetEquals(details.GetPowerRecords(row).Select(power => power.Index)),
                        $"{civilization.Name} omitted a configured civilization power.");
            }
            foreach (var (category, prefix) in new[] { (CivilizationCategory.Overview, "overview."),
                (CivilizationCategory.Economy, "economy."), (CivilizationCategory.Ages, "age."),
                (CivilizationCategory.Assets, "assets."), (CivilizationCategory.AI, "ai.") })
            {
                var actual = civValues.Count(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal) && pair.Value.Length > 0);
                Require(actual > 0, $"{civilization.Name}/{category} has no actual supplemental source values.");
                sourceFieldCounts[category.ToString()] = actual;
            }
            report.Add(new { civilization.Name, Row = row, InitialAge = lower, FinalAge = upper,
                InitializerTechnologyId = initializerId, LeaderFirstStringId = leaderFirst,
                LeaderLastStringId = leaderLast, categoryCounts, sourceFieldCounts });
            checks.Add($"{civilization.Name}: all entity rosters and supplemental source categories populated without overrides");
            Console.WriteLine($"SOURCE TIMING {civilization.Name}: complete ({civilizationElapsed.ElapsedMilliseconds} ms; {elapsed.ElapsedMilliseconds} ms total)");
        }
        var retained = new CivilizationEditProject { GamePath = gamePath, Civilizations = civilizations.Select(civ => civ.Clone()).ToList() }.Serialize();
        Require(retained == pristine, "Read-only source queries mutated the imported civilization drafts.");

        // A bounded callback fails promptly if an invalid interval ever wraps
        // into a billions-of-IDs loop. Other tables may contain null string
        // sentinels, so the callback retains the normal lookup behavior.
        var malformedCivilizations = (byte[])civBytes.Clone();
        var malformedLeaderRows = new[] { (Row: 3, First: uint.MaxValue, Last: uint.MaxValue),
            (Row: 4, First: 8227u, Last: 8221u), (Row: 5, First: 8241u, Last: 65536u) };
        foreach (var malformed in malformedLeaderRows)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(malformedCivilizations.AsSpan(4 + malformed.Row * 344 + 304, 4), malformed.First);
            BinaryPrimitives.WriteUInt32LittleEndian(malformedCivilizations.AsSpan(4 + malformed.Row * 344 + 308, 4), malformed.Last);
        }
        var localizationCalls = 0;
        var malformedSupplement = new CivilizationSupplementalData(gamePath, objects, malformedCivilizations, records, catalog,
            (library, id) =>
            {
                if (++localizationCalls > 20_000)
                    throw new InvalidOperationException("An invalid localization interval escaped the bounded source loop.");
                return id is > 0 and <= 65535 ? $"{library} string {id}" : null;
            });
        foreach (var malformed in malformedLeaderRows)
        {
            var actual = malformedSupplement.GetValues(civilizations.Single(civilization => civilization.SourceRowIndex == malformed.Row));
            Require(!actual.ContainsKey("assets.leaderNames") && actual.GetValueOrDefault("overview.initializationTechnology") is { Length: > 0 },
                "A sentinel, reversed or out-of-range leader interval was enumerated or prevented remaining source values loading.");
        }
        checks.Add("All nine +300 initializer/bonus and +304/+308 leader joins verified; in-memory sentinel, reversed and out-of-range leader intervals skip safely");

        var france = civilizations.Single(civ => civ.SourceRowIndex == 9);
        var franks = civilizations.Single(civ => civ.SourceRowIndex == 5);
        Require(gameData.GetRoster(france, CivilizationCategory.Units).Any(entity => entity.SourceObjectId == 935),
            "French Citizen935 was dropped because technology9237 has legacy Franks tag5.");
        Require(gameData.GetRoster(franks, CivilizationCategory.Units).Any(entity => entity.SourceObjectId == 851),
            "Franks Citizen851 was dropped because technology6157 has legacy French tag9.");
        Require(gameData.GetRoster(france, CivilizationCategory.Buildings).Any(entity => entity.SourceObjectId == 1635),
            "The French citizen's actual granary production chain was not imported.");
        Require(!gameData.GetRoster(france, CivilizationCategory.Buildings).Any(entity => entity.SourceObjectId == 1489),
            "The obsolete French house with a zero availability mask entered the roster.");
        Require(!gameData.GetRoster(france, CivilizationCategory.Abilities).Any(entity => entity.SourceTechnologyId == 1958),
            "The UK-only Infiltration mask was ignored for France.");
        checks.Add("Legacy Franks/French tags follow native mask+producer ownership; mask-disabled house and foreign power excluded");
        Require(details.GetPowerRecords(9).Any(power => power.Index == 11 && power.ActivationTechnologyId == 1501) &&
            details.GetPowerRecords(9).Any(power => power.Index == 37 && power.ActivationTechnologyId is null),
            "France's linked and native-only civilization power definitions did not resolve.");
        checks.Add("Phoenix power slots join the exact logical/engine IDs, including linked activation nodes and native-only definitions");

        Require(eventRecords.Count == 1500 && CivilizationAbilityData.ReadEffects(effects).Count == 2000 &&
            byId[9404].EventId == 737 && eventRecords[737].EffectIds.Contains(1660),
            "The stock French stone-gather research event/effect link did not resolve.");
        var stoneFields = details.GetEffectFields(9404, CivilizationCategory.Technologies);
        var operand = stoneFields.Single(field => field.Key == "technology.effect.1660.relative");
        var patched = (byte[])effects.Clone();
        details.WriteEffectValue(patched, 9404, CivilizationCategory.Technologies, operand.Key, "-0.25");
        var offset = 1660 * 88 + 16;
        Require(BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(patched.AsSpan(offset, 4))) == -0.25f &&
            patched.AsSpan(0, offset).SequenceEqual(effects.AsSpan(0, offset)) &&
            patched.AsSpan(offset + 4).SequenceEqual(effects.AsSpan(offset + 4)),
            "A modifier operand changed bytes outside its proven four-byte field.");
        ExpectInvalid(() => details.WriteEffectValue(patched, 9404, CivilizationCategory.Technologies, operand.Key, "NaN"));
        ExpectInvalid(() => details.WriteEffectValue(patched, 9404, CivilizationCategory.Technologies, "technology.effect.1698.relative", "0.5"));
        Require(details.GetEffectFields(9664, CivilizationCategory.Technologies).Count == 0,
            "The architecture integer consumer was exposed through the generic float write adapter.");
        ExpectInvalid(() => CivilizationAbilityData.ReadEvents(events[..^1]));
        ExpectInvalid(() => CivilizationAbilityData.ReadEffects(effects[..^1]));
        checks.Add("Exact events/effects framing, signed scalar operand isolation, unsupported opcode/link rejection and truncation checks");

        var after = SourceHashes(gamePath);
        Require(hashes.Count == after.Count && hashes.All(pair => after.GetValueOrDefault(pair.Key) == pair.Value),
            "Source import or in-memory acceptance checks changed installed game files.");
        checks.Add($"{hashes.Count} installed source files retained identical SHA-256 hashes");
        File.WriteAllText(Path.Combine(outputDirectory, "civilization-sources.json"), JsonSerializer.Serialize(
            new { Checks = checks, Civilizations = report, SourceHashes = hashes }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var check in checks) Console.WriteLine("SOURCE " + check);
        return checks.AsReadOnly();
    }

    private static Dictionary<string, string> SourceHashes(string gamePath)
    {
        var files = Directory.EnumerateFiles(Path.Combine(gamePath, "Data", "db"), "*.dat")
            .Concat(new[] { "Empires_DMW.exe", "Language.dll", "Language2.dll" }.Select(name => Path.Combine(gamePath, name)))
            .Where(File.Exists).Order(StringComparer.OrdinalIgnoreCase);
        return files.ToDictionary(path => Path.GetRelativePath(gamePath, path), path =>
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }, StringComparer.OrdinalIgnoreCase);
    }

    private static void ExpectInvalid(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid source data or modifier edit was accepted.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
