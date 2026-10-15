using System.Buffers.Binary;
using System.Globalization;

namespace EDMWMapDesigner.Civilizations;

public readonly record struct CivilizationRuntimeRule(int TechnologyId, int CivilizationId, int Resource, int Amount);

/// <summary>Versioned, data-only research bonuses. Resource IDs follow the game's cost array.</summary>
public static class CivilizationRuntimeEffects
{
    public const string DirectoryPath = "Data\\EDMWEffects\\";
    public const int MaximumRules = 1024;
    public static IReadOnlyList<(string Key, string Name, int Resource)> Fields { get; } =
        [("technology.bonusWood", "Wood", 0), ("technology.bonusStone", "Stone", 1),
         ("technology.bonusGold", "Gold", 2), ("technology.bonusFood", "Food", 4)];

    public static bool IsKey(string key) => Fields.Any(field => field.Key == key);
    public static string PathFor(CivilizationEntityDraft entity) => DirectoryPath + Guid.Parse(entity.Id).ToString("N") + ".edmwe";
    public static bool IsPath(string path) => path.StartsWith(DirectoryPath, StringComparison.OrdinalIgnoreCase) &&
        path.EndsWith(".edmwe", StringComparison.OrdinalIgnoreCase) &&
        Guid.TryParseExact(path[DirectoryPath.Length..^6], "N", out _);

    public static IReadOnlyList<CivilizationRuntimeRule> RulesFor(CivilizationDraft civilization, CivilizationEntityDraft entity)
    {
        var rules = new List<CivilizationRuntimeRule>();
        foreach (var field in Fields)
        {
            if (!entity.Properties.TryGetValue(field.Key, out var value) || string.IsNullOrWhiteSpace(value)) continue;
            if (entity.Category != CivilizationCategory.Technologies || entity.SourcePowerId is not null ||
                entity.SourceTechnologyId is not (>= 1000 and < 15000) ||
                civilization.SourceRowIndex is not (>= 3 and <= 11) ||
                civilization.SourceCivilizationId != civilization.SourceRowIndex + 1 ||
                !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var amount) || amount is < 0 or > 100000)
                throw new InvalidDataException("Research bonuses require an existing civilization technology and a whole amount from 0 to 100,000.");
            if (amount > 0) rules.Add(new(entity.SourceTechnologyId.Value, civilization.SourceCivilizationId.Value, field.Resource, amount));
        }
        return rules;
    }

    public static byte[] Encode(IEnumerable<CivilizationRuntimeRule> source)
    {
        var rules = source.OrderBy(rule => rule.CivilizationId).ThenBy(rule => rule.TechnologyId)
            .ThenBy(rule => rule.Resource).ThenBy(rule => rule.Amount).ToArray();
        if (rules.Length > MaximumRules) throw new InvalidDataException("Too many runtime effect rules.");
        var bytes = new byte[12 + rules.Length * 16];
        "EDMWEF01"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), rules.Length);
        for (var index = 0; index < rules.Length; index++)
        {
            var rule = rules[index];
            var record = bytes.AsSpan(12 + index * 16, 16);
            BinaryPrimitives.WriteInt32LittleEndian(record, rule.TechnologyId);
            BinaryPrimitives.WriteInt32LittleEndian(record[4..], rule.CivilizationId);
            BinaryPrimitives.WriteInt32LittleEndian(record[8..], rule.Resource);
            BinaryPrimitives.WriteInt32LittleEndian(record[12..], rule.Amount);
        }
        _ = Decode(bytes);
        return bytes;
    }

    public static IReadOnlyList<CivilizationRuntimeRule> Decode(byte[] bytes)
    {
        if (bytes.Length >= 8 && (bytes.AsSpan(0, 8).SequenceEqual("EDMWEF02"u8) || bytes.AsSpan(0, 8).SequenceEqual("EDMWEF03"u8) || bytes.AsSpan(0, 8).SequenceEqual("EDMWEF04"u8) || bytes.AsSpan(0, 8).SequenceEqual("EDMWEF05"u8)))
            return CivilizationRuntimeConfiguration.Decode(bytes).Research;
        if (bytes.Length < 12 || !bytes.AsSpan(0, 8).SequenceEqual("EDMWEF01"u8))
            throw new InvalidDataException("The runtime effect header is invalid.");
        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        if (count is < 0 or > MaximumRules || bytes.Length != 12 + count * 16)
            throw new InvalidDataException("The runtime effect framing is invalid.");
        var rules = new List<CivilizationRuntimeRule>();
        for (var index = 0; index < count; index++)
        {
            var record = bytes.AsSpan(12 + index * 16, 16);
            var rule = new CivilizationRuntimeRule(BinaryPrimitives.ReadInt32LittleEndian(record),
                BinaryPrimitives.ReadInt32LittleEndian(record[4..]), BinaryPrimitives.ReadInt32LittleEndian(record[8..]),
                BinaryPrimitives.ReadInt32LittleEndian(record[12..]));
            if (rule.TechnologyId is < 1000 or >= 15000 || rule.CivilizationId is < 4 or > 12 ||
                rule.Resource is not (0 or 1 or 2 or 4) || rule.Amount is < 1 or > 100000)
                throw new InvalidDataException("A runtime effect rule is invalid.");
            rules.Add(rule);
        }
        return rules;
    }

    internal static void ValidatePackageFile(CivilizationEditProject project, CivilizationPackageFile file)
    {
        var matches = project.Civilizations.SelectMany(civ => civ.Entities.Select(entity => (civ, entity)))
            .Where(item => PathFor(item.entity).Equals(file.RelativePath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || file.Replacement is null || file.ExpectedHash is not null ||
            CivilizationRuntimeConfiguration.DataFor(matches[0].civ, matches[0].entity) is not { } expected ||
            !EquivalentConfiguration(expected, file.Replacement) || CivilizationRuntimeConfiguration.Decode(file.Replacement).Count == 0)
            throw new InvalidDataException("Runtime effects must match the package's source project and own a new sidecar file.");
    }

    private static bool EquivalentConfiguration(byte[] expected, byte[] actual)
    {
        // Legacy packages retain their original hashes and sidecars. Compare their decoded
        // meaning so the current writer does not invalidate older sight-only exports.
        var left = CivilizationRuntimeConfiguration.Decode(expected);
        var right = CivilizationRuntimeConfiguration.Decode(actual);
        return left.Research.SequenceEqual(right.Research) && left.Units.Count == right.Units.Count &&
            left.Units.Zip(right.Units).All(pair => CivilizationRuntimeConfiguration.Equivalent(pair.First, pair.Second));
    }
}
