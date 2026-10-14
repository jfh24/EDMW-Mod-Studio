using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EDMWMapDesigner.Civilizations;

public sealed record CivilizationProgressionProducer(int ObjectId, string DisplayName);

public sealed record CivilizationAgeProgressionItem(
    CivilizationEntityDraft Entity, string DisplayName, string Kind, string Scope)
{
    public bool IncludedByDefault { get; init; } = true;
    public int TechnologyId { get; init; }
    public IReadOnlyList<CivilizationProgressionProducer> ProducerChoices { get; init; } = [];
    public IReadOnlyList<int> AddedProducerIds { get; init; } = [];
    public string ProducerKey => MembershipKey.Replace("progressionIncluded", "progressionProducers", StringComparison.Ordinal);
    public string MembershipKey => Entity.Category switch
    {
        CivilizationCategory.Units => "unit.progressionIncluded",
        CivilizationCategory.Buildings => "building.progressionIncluded",
        _ => "technology.progressionIncluded"
    };
}

/// <summary>Verified existing production, research and age-advance definitions.</summary>
public static class CivilizationAgeProgression
{
    internal static CivilizationEntityDraft? FindEntity(CivilizationDraft civilization, CivilizationEntityDraft source) =>
        civilization.Entities.FirstOrDefault(entity => entity.Id == source.Id) ?? civilization.Entities.FirstOrDefault(entity =>
            entity.Category == source.Category && (source.SourceObjectId is int objectId ? entity.SourceObjectId == objectId :
                source.SourceTechnologyId is int technologyId && entity.SourceObjectId is null && entity.SourceTechnologyId == technologyId));

    internal static string? MembershipOverride(CivilizationDraft civilization,
        IReadOnlyList<CivilizationAgeProgressionItem> items, CivilizationAgeProgressionItem item) =>
        FindEntity(civilization, item.Entity)?.Properties.GetValueOrDefault(item.MembershipKey) ??
        items.Where(alias => alias.TechnologyId == item.TechnologyId)
            .Select(alias => FindEntity(civilization, alias.Entity)?.Properties.GetValueOrDefault(alias.MembershipKey))
            .FirstOrDefault(value => value is not null);

    // Identity and progression edit the same per-civilization mask. Keep aliases
    // of a shared production/research record consistent and preserve other edits.
    internal static void SetMembership(CivilizationDraft civilization,
        IReadOnlyList<CivilizationAgeProgressionItem> items, CivilizationAgeProgressionItem item,
        bool included, bool inherit, int? producerId = null)
    {
        var aliases = items.Where(alias => alias.TechnologyId == item.TechnologyId).ToArray();
        foreach (var alias in aliases)
        {
            var authored = FindEntity(civilization, alias.Entity);
            authored?.Properties.Remove(alias.MembershipKey);
            if (inherit || !included) authored?.Properties.Remove(alias.ProducerKey);
        }
        var entity = FindEntity(civilization, item.Entity);
        if (entity is null)
        {
            entity = item.Entity.Clone();
            civilization.Entities.Add(entity);
        }
        var originallyIncluded = aliases.Any(alias => alias.IncludedByDefault);
        var retainedEdits = aliases.Any(alias => FindEntity(civilization, alias.Entity) is { } authored &&
            (authored.Properties.Count > 0 || authored.Name != alias.Entity.Name));
        if (inherit || included && item.IncludedByDefault || !included && !originallyIncluded && !retainedEdits)
            entity.Properties.Remove(item.MembershipKey);
        else entity.Properties[item.MembershipKey] = included ? "true" : "false";
        if (!included && !originallyIncluded && retainedEdits) entity.Properties[item.MembershipKey] = "false";
        if (inherit) entity.Properties.Remove(item.ProducerKey);
        else if (included && item.AddedProducerIds.Count > 0)
            entity.Properties[item.ProducerKey] = (producerId ?? item.AddedProducerIds[0]).ToString(CultureInfo.InvariantCulture);
        if (entity.Properties.Count == 0 && entity.Name == item.Entity.Name) civilization.Entities.Remove(entity);
        civilization.Entities.RemoveAll(entry => entry.Properties.Count == 0 &&
            items.Any(source => source.Entity.Id == entry.Id && source.Entity.Name == entry.Name));
    }

    public static IReadOnlyList<CivilizationAgeProgressionItem> GetItems(
        byte[] objects, byte[] civilizations, IReadOnlyList<CivilizationTechnologyRecord> records,
        CivilizationGameCatalog catalog, CivilizationDraft civilization,
        Func<uint, string?> localizedTechnologyName, Func<uint, string?> localizedObjectName)
        => GetItems(objects, civilizations, records, catalog, civilization, localizedTechnologyName, localizedObjectName, false);

    public static IReadOnlyList<CivilizationAgeProgressionItem> GetCandidates(
        byte[] objects, byte[] civilizations, IReadOnlyList<CivilizationTechnologyRecord> records,
        CivilizationGameCatalog catalog, CivilizationDraft civilization,
        Func<uint, string?> localizedTechnologyName, Func<uint, string?> localizedObjectName)
        => GetItems(objects, civilizations, records, catalog, civilization, localizedTechnologyName, localizedObjectName, true);

    private static IReadOnlyList<CivilizationAgeProgressionItem> GetItems(
        byte[] objects, byte[] civilizations, IReadOnlyList<CivilizationTechnologyRecord> records,
        CivilizationGameCatalog catalog, CivilizationDraft civilization,
        Func<uint, string?> localizedTechnologyName, Func<uint, string?> localizedObjectName, bool includeCandidates)
    {
        if (civilization.SourceRowIndex is not int row) return [];
        var effectiveCivilizations = (byte[])civilizations.Clone();
        foreach (var property in civilization.Properties.Where(pair => !string.IsNullOrWhiteSpace(pair.Value) &&
            pair.Key is "age.initialAge" or "age.finalAge" or "age.supportedAges" or "overview.allowedAges"))
            CivilizationCivilizationBindings.WriteValue(effectiveCivilizations, row, property.Key, property.Value);
        CivilizationCivilizationBindings.ValidateRow(effectiveCivilizations, row);
        var values = CivilizationCivilizationBindings.ReadValues(effectiveCivilizations, row);
        var lower = int.Parse(values["age.initialAge"], CultureInfo.InvariantCulture);
        var upper = int.Parse(values["age.finalAge"], CultureInfo.InvariantCulture);
        var producerChoices = CivilizationTechnologyBindings.GetProgressionProducerChoicesByTechnology(objects, records, row, lower, upper);
        var items = new List<CivilizationAgeProgressionItem>();
        foreach (var category in new[] { CivilizationCategory.Units, CivilizationCategory.Buildings })
        {
            var originals = CivilizationTechnologyBindings.GetCivilizationObjectIds(objects, records, row,
                category, lower, upper).ToHashSet();
            var ids = CivilizationTechnologyBindings.GetCivilizationProgressionObjectIds(objects, records, row,
                category, lower, upper, includeCandidates).ToHashSet();
            foreach (var source in catalog.Items.Where(item => item.Category == category && ids.Contains(item.SourceId)))
            {
                var nameId = BinaryPrimitives.ReadUInt32LittleEndian(objects.AsSpan(4 + source.SourceId * 1936 + 296, 4));
                var name = localizedObjectName(nameId) ?? source.Name;
                var technologyId = CivilizationObjectBindings.ReadTechnologyId(objects, source.SourceId, category);
                var technology = records.Single(record => record.TechId == technologyId);
                var choices = GetProducerChoices(technology);
                items.Add(new(new CivilizationEntityDraft
                {
                    Id = CatalogIdentity(civilization.Id, category, source.SourceId),
                    Name = name, Category = category, SourceObjectId = source.SourceId
                }, name, category == CivilizationCategory.Units ? "Unit" : "Building",
                    $"Inclusion is specific to {civilization.Name}. Age availability and costs use shared production technology {technologyId}; edits affect every definition using that record.")
                {
                    IncludedByDefault = originals.Contains(source.SourceId), TechnologyId = technology.TechId,
                    ProducerChoices = choices,
                    AddedProducerIds = AddedProducers(technology, choices)
                });
            }
        }
        var technologies = CivilizationTechnologyBindings.GetCivilizationProgressionTechnologyRecords(objects, records,
            row, lower, upper, includeCandidates);
        var originalTechnologyIds = CivilizationTechnologyBindings.GetCivilizationProgressionTechnologyRecords(objects, records,
            row, lower, upper, false).Select(record => record.TechId).ToHashSet();
        foreach (var technology in technologies)
        {
            var name = localizedTechnologyName(technology.NameStringId) ?? technology.Identifier;
            var kind = CivilizationTechnologyBindings.IsAgeAdvanceRecord(technology) ? "Age advance" : "Technology";
            var choices = GetProducerChoices(technology);
            items.Add(new(new CivilizationEntityDraft
            {
                Id = CatalogIdentity(civilization.Id, CivilizationCategory.Technologies, technology.TechId),
                Name = name, Category = CivilizationCategory.Technologies, SourceTechnologyId = technology.TechId
            }, name, kind,
                $"Inclusion is specific to {civilization.Name}. Age availability, costs and effects use shared technology {technology.TechId}; edits affect every civilization or action using that record.")
            {
                IncludedByDefault = originalTechnologyIds.Contains(technology.TechId), TechnologyId = technology.TechId,
                ProducerChoices = choices,
                AddedProducerIds = AddedProducers(technology, choices)
            });
        }
        return items.OrderBy(item => item.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();

        IReadOnlyList<CivilizationProgressionProducer> GetProducerChoices(CivilizationTechnologyRecord technology) =>
            producerChoices.GetValueOrDefault(technology.TechId, [])
                .Select(id => new CivilizationProgressionProducer(id,
                    localizedObjectName(BinaryPrimitives.ReadUInt32LittleEndian(objects.AsSpan(4 + id * 1936 + 296, 4))) ??
                    catalog.Items.FirstOrDefault(item => item.SourceId == id)?.Name ?? $"Object {id}"))
                .ToArray();

        static IReadOnlyList<int> AddedProducers(CivilizationTechnologyRecord technology,
            IReadOnlyList<CivilizationProgressionProducer> choices) =>
            choices.Count == 0 || choices.Any(choice => technology.ProducerObjectIds.Contains(choice.ObjectId))
                ? [] : [choices[0].ObjectId];
    }

    private static string CatalogIdentity(string civilizationId, CivilizationCategory category, int row)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"EDMW catalog/{civilizationId}/{category}/{row.ToString(CultureInfo.InvariantCulture)}"));
        return new Guid(bytes.AsSpan(0, 16)).ToString("D");
    }
}
