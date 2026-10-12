using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EDMWMapDesigner.Civilizations;

/// <summary>
/// Read-only civilization summaries joined to their actual object, technology,
/// localization and script sources. Match presets and shared scripts retain
/// their scope; they are never converted into guessed per-civilization defaults.
/// </summary>
public sealed class CivilizationSupplementalData
{
    private readonly byte[] _objects;
    private readonly byte[] _civilizations;
    private readonly IReadOnlyList<CivilizationTechnologyRecord> _technologies;
    private readonly Dictionary<int, CivilizationTechnologyRecord> _technologyById;
    private readonly Dictionary<int, CivilizationCatalogItem> _catalog;
    private readonly CivilizationGameAssets _assets;
    private readonly Func<string, uint, string?> _localizedText;
    private readonly Dictionary<int, IReadOnlyDictionary<string, string>> _values = [];
    private readonly Dictionary<int, string?> _modelNames = [];
    private readonly Dictionary<string, ScriptInfo> _scripts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _diagnostics = [];
    private readonly byte[]? _premade;
    private readonly byte[]? _roleDefinitions;
    private readonly byte[]? _resourceProfiles;
    private readonly string _startingScriptRoster;

    public IReadOnlyList<string> Diagnostics => _diagnostics.AsReadOnly();

    public CivilizationSupplementalData(string gamePath, byte[] objects,
        byte[] civilizations, IReadOnlyList<CivilizationTechnologyRecord> technologyRecords,
        CivilizationGameCatalog catalog, Func<string, uint, string?>? localizedText = null)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(civilizations);
        ArgumentNullException.ThrowIfNull(technologyRecords);
        ArgumentNullException.ThrowIfNull(catalog);
        _objects = objects;
        _civilizations = civilizations;
        _technologies = technologyRecords;
        _technologyById = technologyRecords.ToDictionary(record => record.TechId);
        _catalog = catalog.Items.ToDictionary(item => item.SourceId);
        _localizedText = (library, id) => id is > 0 and <= ushort.MaxValue ? localizedText?.Invoke(library, id) : null;
        _assets = new CivilizationGameAssets(gamePath);
        _premade = ReadOptionalTable("db\\dbpremadecivs.dat", 124);
        _roleDefinitions = ReadOptionalTable("db\\dbcivspecificunitdefines.dat", 216);
        _resourceProfiles = ReadOptionalTable("db\\dbstartingresources.dat", 136);
        _startingScriptRoster = ReadStartingScriptRoster();
    }

    public IReadOnlyDictionary<string, string> GetValues(CivilizationDraft civilization)
    {
        ArgumentNullException.ThrowIfNull(civilization);
        if (civilization.SourceRowIndex is not int row)
            return new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
        var sourceValues = CivilizationCivilizationBindings.ReadValues(_civilizations, row);
        var offset = checked(4 + row * CivilizationCivilizationBindings.RecordSize);
        if (civilization.SourceCivilizationId is int storedId && ReadInt(_civilizations, offset) != storedId)
            throw new InvalidDataException("The civilization's stored identity no longer matches its source row.");
        if (_values.TryGetValue(row, out var cached)) return cached;
        var tracing = Environment.GetEnvironmentVariable("EDMW_CIV_SOURCE_TRACE") == "1";
        void Stage(string name, Action action)
        {
            if (tracing) Console.WriteLine($"Supplemental {row}: {name} start");
            var timer = Stopwatch.StartNew();
            action();
            if (tracing) Console.WriteLine($"Supplemental {row}: {name} {timer.ElapsedMilliseconds} ms");
        }

        var lower = int.Parse(sourceValues["age.initialAge"], CultureInfo.InvariantCulture);
        var upper = int.Parse(sourceValues["age.finalAge"], CultureInfo.InvariantCulture);
        if (lower < 0 || upper < lower || upper - lower > 100)
            throw new InvalidDataException("The source civilization has an unsupported age interval.");
        IReadOnlyList<CivilizationCatalogItem> units = [];
        IReadOnlyList<CivilizationCatalogItem> buildings = [];
        Stage("ownership", () =>
        {
            units = OwnedObjects(row, CivilizationCategory.Units, lower, upper);
            buildings = OwnedObjects(row, CivilizationCategory.Buildings, lower, upper);
        });
        var owned = units.Concat(buildings).DistinctBy(item => item.SourceId).ToArray();
        var technologies = CivilizationTechnologyBindings.GetCivilizationTechnologyRecords(
            _objects, _technologies, row, lower, upper);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        values["overview.rosterSummary"] = $"{units.Count} units, {buildings.Count} buildings and {technologies.Count} research technologies across supported ages. " +
            "Availability comes from the game's civilization masks, technology ownership and age ranges.";
        Stage("initialization", () => AddInitialization(values, offset));
        Stage("premade profiles", () => AddPremadeProfiles(values, offset));
        Stage("leaders", () => AddLeadersAndTransitionText(values, offset));
        Stage("age rosters", () => AddRostersByAge(values, row, lower, upper));
        Stage("age advances", () => AddAgeAdvances(values, row, lower, upper, owned));
        Stage("models and icons", () => AddModelsAndIcons(values, owned));
        Stage("workers", () => AddWorkerValues(values, units));
        Stage("roles", () => AddRoleDefinitions(values, owned));
        Stage("behavior scripts", () => AddBehaviorScripts(values, owned));
        var resources = DescribeResourceProfiles();
        if (resources.Length > 0) values["economy.resourceProfiles"] = resources;
        if (_startingScriptRoster.Length > 0) values["economy.startingScriptRoster"] = _startingScriptRoster;
        values["assets.localization"] = "Civilization name: Language.dll / " + sourceValues["assets.nameStringId"] +
            ". Unit, building and research names use their individual string references.";
        var result = new ReadOnlyDictionary<string, string>(values);
        _values.Add(row, result);
        return result;
    }

    private void AddInitialization(Dictionary<string, string> values, int offset)
    {
        // Native loader 0x0051EA62 maps disk +300 to runtime +44. Direct
        // source values join Civilization - ... tech IDs for every active row.
        // Technology application 0x00673861..0x00673886 applies the second
        // trailing array; these are applied linked technologies, not prerequisites.
        var id = ReadInt(_civilizations, offset + 300);
        if (!_technologyById.TryGetValue(id, out var technology)) return;
        values["overview.initializationTechnology"] = TechName(technology);
        var linked = technology.AdditionalTechnologyIds.Select(DescribeTechnology).ToArray();
        if (linked.Length > 0)
            values["overview.appliedTechnologies"] = "Linked technologies applied by the civilization initializer:\r\n" + string.Join("\r\n", linked);
    }

    private void AddPremadeProfiles(Dictionary<string, string> values, int offset)
    {
        if (_premade is null) return;
        var initializer = ReadInt(_civilizations, offset + 300);
        var names = new List<string>();
        // The primary dbpremadecivs schema explicitly names +120 the civilization
        // technology ID. Exact ID joins avoid assigning obsolete names by similarity.
        foreach (var start in Records(_premade, 124))
        {
            if (ReadInt(_premade, start + 120) != initializer) continue;
            var identifier = ReadText(_premade.AsSpan(start + 8, 100));
            var display = _localizedText("Language.dll", ReadUInt(_premade, start + 112)) ?? identifier;
            var ages = _localizedText("Language.dll", ReadUInt(_premade, start + 116));
            names.Add(display + (string.IsNullOrWhiteSpace(ages) ? "" : " — " + CleanText(ages)));
        }
        if (names.Count > 0)
            values["overview.premadeProfiles"] = "Premade records linked by the same civilization technology:\r\n" + string.Join("\r\n", names);
    }

    private void AddLeadersAndTransitionText(Dictionary<string, string> values, int offset)
    {
        // Loader 0x0051EA8C/0x0051EA92 maps disk +304/+308 to runtime
        // +5C/+60. Direct source intervals join Language.dll leader names.
        // UINT_MAX is a sentinel and must never enter an inclusive uint loop.
        var first = ReadUInt(_civilizations, offset + 304);
        var last = ReadUInt(_civilizations, offset + 308);
        if (first is > 0 and <= ushort.MaxValue && last <= ushort.MaxValue && first <= last && last - first < 100)
        {
            var names = new List<string>();
            var count = checked((int)(last - first + 1));
            for (var index = 0; index < count; index++)
                if (_localizedText("Language.dll", checked(first + (uint)index)) is { Length: > 0 } name) names.Add(CleanText(name));
            if (names.Count > 0) values["assets.leaderNames"] = string.Join("\r\n", names);
        }
        // +336 maps to runtime +68 at 0x0051EAAE..B1. The four real entries
        // 22000..22003 describe the player's medieval-to-WWI civilization choice.
        var transitionId = ReadUInt(_civilizations, offset + 336);
        if (transitionId > 0 && _localizedText("Language.dll", transitionId) is { Length: > 0 } text)
            values["age.transitionDescription"] = CleanText(text);
    }

    private void AddRostersByAge(Dictionary<string, string> values, int row, int lower, int upper)
    {
        var all = new List<string>();
        var unlocks = new List<string>();
        HashSet<int> previousResearch = [];
        for (var age = lower; age <= upper; age++)
        {
            var units = OwnedObjects(row, CivilizationCategory.Units, age, age);
            var buildings = OwnedObjects(row, CivilizationCategory.Buildings, age, age);
            var research = CivilizationTechnologyBindings.GetCivilizationTechnologyRecords(
                _objects, _technologies, row, age, age);
            all.Add($"{CivilizationAgeNames.GetName(age)}:\r\nUnits: {string.Join(", ", units.Select(ObjectName))}\r\n" +
                $"Buildings: {string.Join(", ", buildings.Select(ObjectName))}\r\n" +
                $"Research: {string.Join(", ", research.Select(TechName))}");
            var added = research.Where(record => !previousResearch.Contains(record.TechId)).ToArray();
            if (added.Length > 0)
                unlocks.Add($"{CivilizationAgeNames.GetName(age)} — {(age == lower ? "available at first supported age" : "newly available")}:\r\n" +
                    string.Join("\r\n", added.Select(TechName)));
            previousResearch = research.Select(record => record.TechId).ToHashSet();
        }
        values["age.rostersByAge"] = string.Join("\r\n\r\n", all);
        values["economy.startingRosterByAge"] = "Buildable/training roster for each supported starting age; this is availability, not units spawned at game start.\r\n\r\n" + values["age.rostersByAge"];
        values["ai.allowedRostersByAge"] = "The game's configured production and research availability:\r\n\r\n" + values["age.rostersByAge"];
        if (unlocks.Count > 0) values["age.technologyUnlocks"] = string.Join("\r\n\r\n", unlocks);
    }

    private void AddAgeAdvances(Dictionary<string, string> values, int row, int lower, int upper,
        IReadOnlyList<CivilizationCatalogItem> owned)
    {
        // These are the actual non-epoch records named Epoch ... Up ... and
        // joined through owned producers/masks. Their names are retained; no
        // next-age/civilization target is guessed from a number in the name.
        var ownedIds = owned.Select(item => item.SourceId).ToHashSet();
        var advances = _technologies.Where(record => CivilizationTechnologyBindings.IsAgeAdvanceRecord(record) && !record.EditorOnly &&
            record.AllowsCivilizationRow(row) && record.StartAge <= upper && record.EndAge >= lower &&
            record.ProducerObjectIds.Any(ownedIds.Contains)).OrderBy(record => record.StartAge).ToArray();
        if (advances.Length == 0) return;
        values["age.transitionTechnology"] = string.Join("\r\n", advances.Select(TechName));
        values["age.advanceCosts"] = string.Join("\r\n", advances.Select(record =>
            $"{TechName(record)}: food {record.FoodCost}, wood {record.WoodCost}, gold {record.GoldCost}, stone {record.StoneCost}; production coefficient {record.ProductionCoefficient}"));
        values["age.transitionBuilding"] = string.Join("\r\n", advances.Select(record =>
            TechName(record) + ": " + string.Join(", ", record.ProducerObjectIds.Where(_catalog.ContainsKey).Select(id => ObjectName(_catalog[id])))));
        var prerequisites = advances.Where(record => record.PrerequisiteTechnologyIds.Count > 0).Select(record =>
            TechName(record) + ": " + string.Join(", ", record.PrerequisiteTechnologyIds.Select(DescribeTechnology))).ToArray();
        if (prerequisites.Length > 0) values["age.transitionPrerequisites"] = string.Join("\r\n", prerequisites);
    }

    private void AddModelsAndIcons(Dictionary<string, string> values, IReadOnlyList<CivilizationCatalogItem> owned)
    {
        var models = new List<string>();
        var icons = new List<string>();
        foreach (var item in owned.OrderBy(ObjectName, StringComparer.OrdinalIgnoreCase))
        {
            var offset = CivilizationObjectBindings.ValidateRecord(_objects, item.SourceId, item.Category);
            var graphics = ReadUInt(_objects, offset + 304);
            if (graphics <= int.MaxValue)
            {
                if (!_modelNames.TryGetValue((int)graphics, out var model))
                    _modelNames[(int)graphics] = model = _assets.FindModelName((int)graphics);
                if (model is not null) models.Add($"{ObjectName(item)} → {model} (graphics {graphics})");
            }
            var icon = ReadUInt(_objects, offset + 300);
            if (icon != uint.MaxValue) icons.Add($"{ObjectName(item)} → button {icon}");
        }
        if (models.Count > 0) values["assets.modelMappings"] = string.Join("\r\n", models);
        if (icons.Count > 0) values["assets.iconMappings"] = string.Join("\r\n", icons);
    }

    private void AddWorkerValues(Dictionary<string, string> values, IReadOnlyList<CivilizationCatalogItem> units)
    {
        var workers = new List<string>();
        foreach (var item in units)
        {
            var offset = CivilizationObjectBindings.ValidateRecord(_objects, item.SourceId, item.Category);
            if (!ReadText(_objects.AsSpan(offset + 240, 32)).Equals("Citizen", StringComparison.OrdinalIgnoreCase)) continue;
            var fields = CivilizationObjectBindings.ReadValues(_objects, item.SourceId, item.Category);
            var descriptions = new List<string>();
            foreach (var (key, label) in new[]
            {
                ("buildCoefficient", "building"), ("gatherFieldFoodCoefficient", "field food"),
                ("gatherAnimalFoodCoefficient", "animal/fish food"), ("gatherTreeFoodCoefficient", "tree food"),
                ("gatherWoodCoefficient", "wood"), ("gatherGoldCoefficient", "gold"), ("gatherStoneCoefficient", "stone")
            })
                if (fields.TryGetValue("unit." + key, out var value)) descriptions.Add(label + " " + value);
            if (descriptions.Count > 0) workers.Add(ObjectName(item) + ": " + string.Join(", ", descriptions));
        }
        if (workers.Count > 0)
            values["economy.workerCoefficients"] = "Current worker parameters from their unit records. These raw coefficients have not been converted to resources per second.\r\n" + string.Join("\r\n", workers);
    }

    private void AddRoleDefinitions(Dictionary<string, string> values, IReadOnlyList<CivilizationCatalogItem> owned)
    {
        if (_roleDefinitions is null) return;
        var ids = owned.Select(item => item.SourceId).ToHashSet();
        var rows = new List<string>();
        foreach (var offset in Records(_roleDefinitions, 216))
        {
            var role = ReadText(_roleDefinitions.AsSpan(offset + 8, 100));
            var matches = new List<string>();
            // Loader 0x00519D78 reads 216 bytes; +108/+116/.../+188
            // become prototype references at runtime +3C/+5C/.../+17C.
            // Slot order differs from Phoenix rows (England/USA), so ownership
            // is joined by object ID rather than an assumed slot-to-civ index.
            for (var slot = 0; slot < 11; slot++)
            {
                var id = ReadUInt(_roleDefinitions, offset + 108 + slot * 8);
                if (id <= int.MaxValue && ids.Contains((int)id) && _catalog.TryGetValue((int)id, out var item))
                    matches.Add(ObjectName(item));
            }
            if (matches.Count > 0) rows.Add(role + " → " + string.Join(", ", matches.Distinct()));
        }
        if (rows.Count > 0)
            values["ai.roleDefinitions"] = "Civilization-specific role definitions that reference this civilization's available objects:\r\n" + string.Join("\r\n", rows);
    }

    private void AddBehaviorScripts(Dictionary<string, string> values, IReadOnlyList<CivilizationCatalogItem> owned)
    {
        var available = _assets.AssetNames.Where(name => name.StartsWith("unit ai scripts\\", StringComparison.OrdinalIgnoreCase) &&
            name.EndsWith(".tai", StringComparison.OrdinalIgnoreCase)).ToDictionary(name => name, StringComparer.OrdinalIgnoreCase);
        var assignments = new List<string>();
        var sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in owned)
        {
            var offset = CivilizationObjectBindings.ValidateRecord(_objects, item.SourceId, item.Category);
            var type = ReadText(_objects.AsSpan(offset + 240, 32));
            var path = "unit ai scripts\\" + type + ".tai";
            if (!available.TryGetValue(path, out var actualPath)) continue;
            assignments.Add(ObjectName(item) + " → " + type + ".tai");
            sourcePaths.Add(actualPath);
        }
        if (assignments.Count == 0) return;
        // Native loader 0x006F84FF builds Unit AI Scripts\ + supplied name +
        // .tai and opens through the game's FSPath at 0x006F8556..8560.
        values["ai.script"] = "Unit behavior script matches for the current object definition types. Scripts are shared by objects of the same type.\r\n" +
            string.Join("\r\n", assignments.OrderBy(line => line, StringComparer.OrdinalIgnoreCase));
        var stateRows = new List<string>();
        var includeRows = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(sourcePaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
        while (pending.TryDequeue(out var path))
        {
            if (!visited.Add(path) || visited.Count > 256) continue;
            var script = ReadScript(path);
            if (script.States.Count > 0) stateRows.Add(Path.GetFileName(path) + ": " + string.Join(", ", script.States));
            if (script.Includes.Count == 0) continue;
            includeRows.Add(Path.GetFileName(path) + " → " + string.Join(", ", script.Includes));
            foreach (var include in script.Includes)
            {
                // TAI includes resolve within the same source directory; the
                // bounded asset reader rejects traversal and rooted filenames.
                var includePath = "unit ai scripts\\" + include.Replace('/', '\\');
                if (available.TryGetValue(includePath, out var actual)) pending.Enqueue(actual);
            }
        }
        if (stateRows.Count > 0) values["ai.behaviorStates"] = "States declared in the assigned scripts and their includes:\r\n" + string.Join("\r\n", stateRows);
        if (includeRows.Count > 0) values["ai.includes"] = string.Join("\r\n", includeRows);
    }

    private string DescribeResourceProfiles()
    {
        if (_resourceProfiles is null) return "";
        var rows = new List<string>();
        foreach (var offset in Records(_resourceProfiles, 136))
        {
            var identifier = ReadText(_resourceProfiles.AsSpan(offset, 100));
            var display = _localizedText("Language.dll", ReadUInt(_resourceProfiles, offset + 108));
            // Loader 0x0051F8FE..F928 copies +112/+116/+120/+124 to
            // runtime +4/+8/+C/+10. Getter 0x0051F9D6 uses resource type
            // 2/3/4/5, matching dbobjects resourceType food/wood/stone/gold.
            rows.Add(identifier + (string.IsNullOrWhiteSpace(display) ? "" : " / " + CleanText(display)) +
                $": food {ReadUInt(_resourceProfiles, offset + 112)}, wood {ReadUInt(_resourceProfiles, offset + 116)}, " +
                $"stone {ReadUInt(_resourceProfiles, offset + 120)}, gold {ReadUInt(_resourceProfiles, offset + 124)}");
        }
        return "Starting-resource table presets, shared match settings. The active preset and any civilization/game-mode adjustments are selected at game start.\r\n" + string.Join("\r\n", rows);
    }

    private string ReadStartingScriptRoster()
    {
        var summaries = new List<string>();
        foreach (var path in new[] { "Random Map Scripts\\Common\\Common Initial Units.rmv", "Random Map Scripts\\Common\\Tourney Common Initial Units.rmv" })
        {
            var text = ReadOptionalText(path);
            if (text is null) continue;
            text = Regex.Replace(text, @"(?m)//[^\r\n]*", "");
            var entries = Regex.Matches(text, @"\bPlaceUnit\s*\(\s*([^,\s)]+)", RegexOptions.CultureInvariant)
                .Select(match => match.Groups[1].Value).GroupBy(name => name, StringComparer.Ordinal)
                .Select(group => $"{group.Count()} × {group.Key}").ToArray();
            if (entries.Length > 0) summaries.Add(Path.GetFileName(path) + ": " + string.Join(", ", entries));
        }
        return summaries.Count == 0 ? "" : "Shared random-map script inputs; map choice, civilization conversion and citizen handicap determine the final spawned roster.\r\n" + string.Join("\r\n", summaries);
    }

    private ScriptInfo ReadScript(string path)
    {
        if (_scripts.TryGetValue(path, out var cached)) return cached;
        var text = ReadOptionalText(path);
        if (text is null) return _scripts[path] = new([], []);
        text = Regex.Replace(text, @"(?m)//[^\r\n]*", "");
        var includes = Regex.Matches(text, "#include[\\t ]*\\([\\t ]*\"([^\"\\r\\n]+)\"[\\t ]*\\)",
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)
            .Select(match => match.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Parse state headers as lines. Repeated \s* around newlines can spend
        // quadratic time redistributing comment-stripped blank lines.
        var states = new HashSet<string>(StringComparer.Ordinal);
        string? candidate = null;
        using (var reader = new StringReader(text))
            while (reader.ReadLine() is { } line)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                if (trimmed.StartsWith('{') && candidate is not null) states.Add(candidate);
                candidate = trimmed.Length > 0 && (char.IsAsciiLetter(trimmed[0]) || trimmed[0] == '_') &&
                    trimmed.All(character => char.IsAsciiLetterOrDigit(character) || character == '_') ? trimmed : null;
            }
        return _scripts[path] = new(Array.AsReadOnly(states.ToArray()), Array.AsReadOnly(includes));
    }

    private IReadOnlyList<CivilizationCatalogItem> OwnedObjects(int row, CivilizationCategory category, int lower, int upper)
    {
        var ids = CivilizationTechnologyBindings.GetCivilizationObjectIds(_objects, _technologies, row, category, lower, upper).ToHashSet();
        return _catalog.Values.Where(item => item.Category == category && ids.Contains(item.SourceId))
            .OrderBy(ObjectName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private string ObjectName(CivilizationCatalogItem item)
    {
        var offset = CivilizationObjectBindings.ValidateRecord(_objects, item.SourceId, item.Category);
        return _localizedText("Language2.dll", ReadUInt(_objects, offset + 296)) ?? item.Name;
    }
    private string TechName(CivilizationTechnologyRecord record) =>
        (_localizedText("Language2.dll", record.NameStringId) ?? record.Identifier) + $" [technology {record.TechId}]";
    private string DescribeTechnology(int id) => _technologyById.TryGetValue(id, out var record) ? TechName(record) : $"Technology {id}";

    private byte[]? ReadOptionalTable(string name, int stride)
    {
        try
        {
            var bytes = _assets.ReadAssetFile(name);
            if (bytes.Length < 4 || ReadUInt(bytes, 0) > 100_000 || bytes.LongLength != 4L + (long)ReadUInt(bytes, 0) * stride)
                throw new InvalidDataException("Unsupported record framing.");
            return bytes;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
        {
            _diagnostics.Add(name + ": " + error.Message);
            return null;
        }
    }
    private string? ReadOptionalText(string name)
    {
        try { return Encoding.UTF8.GetString(_assets.ReadAssetFile(name)); }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
        {
            _diagnostics.Add(name + ": " + error.Message);
            return null;
        }
    }
    private static IEnumerable<int> Records(byte[] bytes, int stride)
    {
        var count = ReadUInt(bytes, 0);
        for (var row = 0; row < count; row++) yield return checked(4 + row * stride);
    }
    private static string ReadText(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        if (end < 0) throw new InvalidDataException("A source identifier is not terminated.");
        foreach (var value in bytes[..end])
            if (value is < 32 or > 126) throw new InvalidDataException("A source identifier is not printable ASCII.");
        return Encoding.ASCII.GetString(bytes[..end]);
    }
    private static string CleanText(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
    private static uint ReadUInt(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static int ReadInt(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
    private sealed record ScriptInfo(IReadOnlyList<string> States, IReadOnlyList<string> Includes);
}
