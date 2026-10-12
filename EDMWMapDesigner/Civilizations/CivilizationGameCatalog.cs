using System.Buffers.Binary;
using System.Text;

namespace EDMWMapDesigner.Civilizations;

/// <summary>A named source object. SourceId is its database row index.</summary>
public sealed record CivilizationCatalogItem(
    int SourceId,
    string Name,
    CivilizationCategory Category,
    int? GraphicsId = null);

/// <summary>
/// Reads known database layouts for selecting source objects in an editor draft.
/// It does not infer civilization membership or read unverified gameplay stats.
/// </summary>
public sealed class CivilizationGameCatalog
{
    private const int ObjectRecordSize = 0x790;
    private const int ObjectNameSize = 100;
    private const int ObjectDescriptorOffset = 232;
    private const int ObjectGraphicsOffset = 0x130;
    private const int CivilizationRecordSize = 344;
    private const int MaximumRecordCount = 100_000;

    private static readonly Dictionary<string, string> SupportedCivilizations =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["China"] = "China",
            ["Korea"] = "Korea",
            ["Franks"] = "Franks",
            ["UK"] = "United Kingdom",
            ["Germany"] = "Germany",
            ["Russia"] = "Russia",
            ["France"] = "France",
            ["England"] = "England",
            ["USA"] = "USA"
        };

    public string GamePath { get; }
    public IReadOnlyList<CivilizationCatalogItem> Items { get; }

    private CivilizationGameCatalog(string gamePath, List<CivilizationCatalogItem> items)
    {
        GamePath = gamePath;
        Items = items.AsReadOnly();
    }

    public static CivilizationGameCatalog Load(string gamePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gamePath);
        var resolvedPath = Path.GetFullPath(gamePath);
        var databasePath = Path.Combine(resolvedPath, "Data", "db", "dbobjects.dat");
        using var stream = File.Open(databasePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var count = ReadRecordCount(stream, ObjectRecordSize, databasePath);
        var record = new byte[ObjectRecordSize];
        var items = new List<CivilizationCatalogItem>();

        for (var rowIndex = 0; rowIndex < count; rowIndex++)
        {
            stream.ReadExactly(record);
            // Existing descriptor discovery consumes this byte. Other flag meanings
            // are deliberately not inferred from adjacent fields.
            if (record[ObjectDescriptorOffset] == 0) continue;
            var name = ReadAsciiName(record.AsSpan(0, ObjectNameSize));
            if (name.Length == 0 || HasExplicitDiscardMarker(name)) continue;
            var category = name.StartsWith("b ", StringComparison.OrdinalIgnoreCase)
                ? CivilizationCategory.Buildings
                : CivilizationCategory.Units;
            var graphicsId = BinaryPrimitives.ReadInt32LittleEndian(
                record.AsSpan(ObjectGraphicsOffset, sizeof(int)));
            items.Add(new CivilizationCatalogItem(rowIndex, name, category,
                graphicsId >= 0 ? graphicsId : null));
        }

        return new CivilizationGameCatalog(resolvedPath, items);
    }

    /// <summary>
    /// Returns country-suffix matches as suggestions only. A matching name does
    /// not establish production availability, ownership, or age availability.
    /// </summary>
    public IReadOnlyList<CivilizationCatalogItem> GetSuggestedItems(
        string civilizationName, CivilizationCategory category)
    {
        if (string.IsNullOrWhiteSpace(civilizationName))
            return Array.Empty<CivilizationCatalogItem>();
        var aliases = GetCountryAliases(civilizationName.Trim());
        return Items.Where(item => item.Category == category &&
            aliases.Any(alias => item.Name.EndsWith($"({alias})",
                StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    /// <summary>
    /// Imports identities for the nine supported civilization slots. Dormant
    /// Spain/Portugal rows are not exposed as working game civilizations.
    /// </summary>
    public static IReadOnlyList<CivilizationDraft> ImportCivilizations(string gamePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gamePath);
        var databasePath = Path.Combine(Path.GetFullPath(gamePath), "Data", "db",
            "dbphoenixcivtable.dat");
        using var stream = File.Open(databasePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var count = ReadRecordCount(stream, CivilizationRecordSize, databasePath);
        var record = new byte[CivilizationRecordSize];
        var civilizations = new List<CivilizationDraft>();
        var sourceIds = new HashSet<int>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var rowIndex = 0; rowIndex < count; rowIndex++)
        {
            stream.ReadExactly(record);
            var sourceId = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(0, 4));
            var storedIndex = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(4, 4));
            if (storedIndex != rowIndex || !sourceIds.Add(sourceId))
                throw new InvalidDataException("The civilization table has an unsupported identity layout.");
            var sourceName = ReadAsciiName(record.AsSpan(8, 200));
            if (!SupportedCivilizations.TryGetValue(sourceName, out var displayName)) continue;
            if (!names.Add(displayName))
                throw new InvalidDataException("The civilization table contains duplicate supported names.");
            civilizations.Add(new CivilizationDraft
            {
                Name = displayName,
                SourceCivilizationId = sourceId,
                SourceRowIndex = storedIndex
            });
        }

        if (civilizations.Count != SupportedCivilizations.Count)
            throw new InvalidDataException("The civilization table does not contain all nine supported slots.");
        return civilizations.AsReadOnly();
    }

    private static int ReadRecordCount(FileStream stream, int recordSize, string databasePath)
    {
        if (stream.Length < sizeof(int))
            throw new InvalidDataException($"The database is missing its record count: {databasePath}");
        Span<byte> header = stackalloc byte[sizeof(int)];
        stream.ReadExactly(header);
        var count = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (count <= 0 || count > MaximumRecordCount ||
            stream.Length != sizeof(int) + (long)count * recordSize)
            throw new InvalidDataException($"The database has an unsupported record layout: {databasePath}");
        return count;
    }

    private static string ReadAsciiName(ReadOnlySpan<byte> bytes)
    {
        var terminator = bytes.IndexOf((byte)0);
        if (terminator >= 0) bytes = bytes[..terminator];
        foreach (var value in bytes)
        {
            if (value < 32 || value > 126) return string.Empty;
        }
        return Encoding.ASCII.GetString(bytes).Trim();
    }

    private static bool HasExplicitDiscardMarker(string name) =>
        name.StartsWith("XXQ", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("DELETE", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("[unused]", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("EDITOR ", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("EE ", StringComparison.OrdinalIgnoreCase);

    private static string[] GetCountryAliases(string name) => name.ToUpperInvariant() switch
    {
        "CHINA" => ["China", "CHI"],
        "KOREA" => ["Korea", "KOR"],
        "FRANKS" => ["Franks"],
        "ENGLAND" => ["England", "English", "ENG"],
        "UK" or "UNITED KINGDOM" or "GREAT BRITAIN" => ["UK", "United Kingdom", "Great Britain"],
        "GERMANY" => ["Germany", "German", "GER"],
        "RUSSIA" => ["Russia", "Russian", "RUS"],
        "FRANCE" => ["France", "French", "FRE"],
        "USA" or "UNITED STATES" => ["USA", "United States", "US"],
        _ => [name]
    };
}
