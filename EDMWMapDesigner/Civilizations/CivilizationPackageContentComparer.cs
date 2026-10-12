using System.Security.Cryptography;

namespace EDMWMapDesigner.Civilizations;

/// <summary>Compares package effects and authored intent without requiring editor catalog or LAN review dependencies.</summary>
internal static class CivilizationPackageContentComparer
{
    /// <summary>Compares installed effects and authored intent, excluding only locally rebuilt presentation.</summary>
    internal static bool SameInstalledContent(CivilizationInstallerPackage received, CivilizationInstallerPackage installed) =>
        received.FormatVersion == installed.FormatVersion && received.PackageId == installed.PackageId &&
        received.ProjectName == installed.ProjectName && SameHash(received.ExecutableHash, installed.ExecutableHash) &&
        SameFiles(received.Files, installed.Files) && SameHashes(received.Dependencies, installed.Dependencies) &&
        SameHashes(received.AssetDependencies, installed.AssetDependencies) &&
        received.OwnerTargets.ToHashSet(StringComparer.Ordinal).SetEquals(installed.OwnerTargets) &&
        SameModels(received.ModelReferences, installed.ModelReferences) &&
        ProjectIntent(received.ProjectJson) == ProjectIntent(installed.ProjectJson);

    private static string ProjectIntent(string json)
    {
        var project = CivilizationEditProject.Deserialize(json);
        project.GamePath = "";
        foreach (var civilization in project.Civilizations)
        {
            // IDs supply stable, unique placeholders. Actual authored properties,
            // record references, asset bytes, ordering, and IDs remain in the comparison.
            civilization.Name = "Civilization " + civilization.Id;
            foreach (var entity in civilization.Entities) entity.Name = "Entity " + entity.Id;
        }
        return project.Serialize();
    }

    internal static bool SameFiles(IReadOnlyList<CivilizationPackageFile> received, IReadOnlyList<CivilizationPackageFile> rebuilt)
    {
        if (received.Count != rebuilt.Count) return false;
        foreach (var file in received)
        {
            var expected = rebuilt.SingleOrDefault(candidate => candidate.RelativePath.Equals(file.RelativePath, StringComparison.OrdinalIgnoreCase));
            if (expected is null || file.WholeFile != expected.WholeFile) return false;
            if (file.WholeFile)
            {
                if (!SameHash(file.ExpectedHash, expected.ExpectedHash) || !SameBytes(file.Replacement, expected.Replacement)) return false;
                continue;
            }
            if (file.ExpectedLength != expected.ExpectedLength || !SameHash(file.StructuralHash, expected.StructuralHash) ||
                file.Patches.Count != expected.Patches.Count) return false;
            foreach (var patch in file.Patches)
            {
                var target = expected.Patches.SingleOrDefault(candidate => candidate.Offset == patch.Offset && candidate.Length == patch.Length);
                if (target is null || patch.IdentityOffset != target.IdentityOffset ||
                    !SameBytes(patch.Expected, target.Expected) || !SameBytes(patch.Replacement, target.Replacement) ||
                    !SameBytes(patch.IdentityBytes, target.IdentityBytes)) return false;
            }
        }
        return true;
    }

    internal static bool SameModels(IReadOnlyList<CivilizationPackageModelReference> received, IReadOnlyList<CivilizationPackageModelReference> rebuilt)
    {
        if (received.Count != rebuilt.Count) return false;
        var left = received.OrderBy(model => model.GraphicsId).ThenBy(model => model.ModelName, StringComparer.OrdinalIgnoreCase).ThenBy(model => model.Hash, StringComparer.OrdinalIgnoreCase);
        var right = rebuilt.OrderBy(model => model.GraphicsId).ThenBy(model => model.ModelName, StringComparer.OrdinalIgnoreCase).ThenBy(model => model.Hash, StringComparer.OrdinalIgnoreCase);
        return left.Zip(right).All(pair => pair.First.GraphicsId == pair.Second.GraphicsId &&
            pair.First.ModelName.Equals(pair.Second.ModelName, StringComparison.OrdinalIgnoreCase) && SameHash(pair.First.Hash, pair.Second.Hash));
    }

    internal static bool SameHashes<T>(IReadOnlyDictionary<string, T> received, IReadOnlyDictionary<string, T> rebuilt) where T : class? =>
        received.Count == rebuilt.Count && received.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == received.Count &&
        rebuilt.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == rebuilt.Count && received.All(pair => rebuilt.Any(candidate =>
            candidate.Key.Equals(pair.Key, StringComparison.OrdinalIgnoreCase) && SameHash(pair.Value as string, candidate.Value as string)));

    internal static bool SameHash(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool SameBytes(byte[]? left, byte[]? right) => left is null || right is null
        ? left is null && right is null : left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
}
