namespace EDMWMapDesigner.Civilizations;

public sealed record CivilizationAgeName(int Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Names keyed by the native epoch ID used in civilization and progression intervals.</summary>
public static class CivilizationAgeNames
{
    public const int UnboundedAge = 14;

    private static readonly string[] FallbackNames =
    [
        "Prehistoric Age", "Stone Age", "Copper Age", "Bronze Age", "Dark Age",
        "Middle Ages", "Gunpowder Age", "Imperial Age", "WWI", "WWII",
        "Industrial Age", "Modern Age", "Digital Age", "Nano Age"
    ];

    public static IReadOnlyList<CivilizationAgeName> ReadNames(
        IReadOnlyList<CivilizationTechnologyRecord> records, Func<uint, string?> localizedName)
    {
        var names = records.Where(record => record.IsEpochDefinition)
            .Select(record => new CivilizationAgeName(record.TechId,
                record.TechId is 8 or 9 ? GetName(record.TechId) :
                CleanName(localizedName(record.NameStringId)) ?? GetName(record.TechId)))
            .DistinctBy(age => age.Id).OrderBy(age => age.Id).ToList();
        if (names.All(age => age.Id != UnboundedAge)) names.Add(new(UnboundedAge, GetName(UnboundedAge)));
        return names.AsReadOnly();
    }

    public static string GetName(int id) => id == UnboundedAge ? "No upper limit" :
        id >= 0 && id < FallbackNames.Length ? FallbackNames[id] : $"Age {id}";

    public static string FormatRange(int first, int last, Func<int, string>? name = null)
    {
        name ??= GetName;
        return first == last ? name(first) : last == UnboundedAge ? $"{name(first)} onward" : $"{name(first)}–{name(last)}";
    }

    private static string? CleanName(string? name)
    {
        var clean = name?.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        if (clean == "Age Medieval") return "Middle Ages";
        if (clean?.StartsWith("Age ", StringComparison.Ordinal) == true) clean = clean[4..] + " Age";
        return string.IsNullOrWhiteSpace(clean) ? null : clean;
    }
}
