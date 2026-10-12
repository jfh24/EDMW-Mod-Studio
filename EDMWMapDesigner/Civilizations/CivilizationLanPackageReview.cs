using static EDMWMapDesigner.Civilizations.CivilizationPackageContentComparer;

namespace EDMWMapDesigner.Civilizations;

public sealed record CivilizationLanReviewSnapshot(CivilizationInstallerPackage? Package, IReadOnlyList<string> Issues);

/// <summary>Rebuilds the reviewed edits from their source project instead of trusting network descriptions.</summary>
public static class CivilizationLanPackageReview
{
    /// <summary>Compares installed effects and authored intent, excluding only locally rebuilt presentation.</summary>
    internal static bool SameInstalledContent(CivilizationInstallerPackage received, CivilizationInstallerPackage installed) =>
        CivilizationPackageContentComparer.SameInstalledContent(received, installed);

    public static CivilizationLanReviewSnapshot Prepare(CivilizationInstallerPackage incoming, string gamePath)
    {
        try
        {
            var issues = CivilizationPackageManager.Review(incoming, gamePath);
            if (issues.Count != 0) return new(null, issues);
            var project = CivilizationEditProject.Deserialize(incoming.ProjectJson);
            var catalog = CivilizationGameCatalog.Load(gamePath);
            var gameData = new CivilizationGameData(gamePath, catalog);
            foreach (var civilization in project.Civilizations)
            {
                civilization.Name = gameData.GetValues(civilization).GetValueOrDefault("overview.displayName") ?? gameData.GetDisplayName(civilization);
                foreach (var entity in civilization.Entities)
                {
                    if (entity.SourceObjectId is int row && catalog.Items.FirstOrDefault(item => item.SourceId == row) is { } item)
                        entity.Name = item.Name;
                    else if (entity.SourcePowerId is int power) entity.Name = "Power " + power;
                    else if (entity.SourceTechnologyId is int technology)
                        entity.Name = gameData.GetTechnologyValues(technology, entity.Category).GetValueOrDefault(
                            entity.Category == CivilizationCategory.Abilities ? "ability.displayName" : "technology.displayName") ?? "Technology " + technology;
                    entity.Name = gameData.GetDisplayName(entity);
                }
            }
            var canonical = CivilizationInstallerPackage.Create(project, gamePath);
            PreserveReviewedLegacyFingerprints(incoming, canonical, gamePath);
            if (!SameFiles(incoming.Files, canonical.Files) ||
                !SameHashes(incoming.Dependencies, canonical.Dependencies) ||
                !SameHashes(incoming.AssetDependencies, canonical.AssetDependencies) ||
                !incoming.OwnerTargets.ToHashSet(StringComparer.Ordinal).SetEquals(canonical.OwnerTargets) ||
                !SameModels(incoming.ModelReferences, canonical.ModelReferences))
                return new(null, ["The received edits do not match their project. Installation blocked."]);
            canonical.PackageId = incoming.PackageId;
            return new(canonical, []);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or
            InvalidOperationException or UnauthorizedAccessException or NotSupportedException)
        {
            return new(null, [error.Message]);
        }
    }

    private static void PreserveReviewedLegacyFingerprints(CivilizationInstallerPackage received,
        CivilizationInstallerPackage canonical, string gamePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath));
        foreach (var file in received.Files.Where(file => !file.WholeFile &&
            file.RelativePath.Equals("Data\\db\\dbtechtree.dat", StringComparison.OrdinalIgnoreCase)))
        {
            var rebuilt = canonical.Files.SingleOrDefault(candidate => candidate.RelativePath.Equals(file.RelativePath, StringComparison.OrdinalIgnoreCase));
            if (rebuilt is null || rebuilt.WholeFile || SameHash(file.StructuralHash, rebuilt.StructuralHash)) continue;
            using var stream = File.Open(CivilizationPackageManager.ResolveTarget(root, file.RelativePath), FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 4 or > 32 * 1024 * 1024)
                throw new InvalidDataException("The reviewed technology table has unsupported bounds.");
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            // Only presentation of the already reviewed table fingerprint may
            // differ. SameFiles still compares all expected/replacement ranges,
            // source identities and file bounds exactly before returning it.
            // Review already checked the received legacy fingerprint, including
            // age bounds owned by other installed packages. Retaining it makes
            // the final installation and future reuse repeat that same check.
            if (!CivilizationInstallerPackage.HasTechnologyMembershipPatches(file, bytes) &&
                SameHash(rebuilt.StructuralHash, CivilizationInstallerPackage.StructuralHash(file.RelativePath, bytes)))
                rebuilt.StructuralHash = file.StructuralHash;
        }
    }
}
