using System.Buffers.Binary;
using EDMWMapDesigner.Civilizations;

namespace EDMWMapDesigner.Networking;

/// <summary>Creates a small, structurally valid codec fixture without reading or modifying a game installation.</summary>
internal static class OnlineCivilizationFixture
{
    public static CivilizationInstallerPackage CreateCodecFixture(string name = "Online civilization transport fixture")
    {
        const string path = @"Data\db\dbobjects.dat";
        const int identityOffset = 4 + 116;
        const int healthOffset = 4 + 120;
        var original = new byte[4 + 1936];
        BinaryPrimitives.WriteInt32LittleEndian(original, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(original.AsSpan(healthOffset, 4), 100);
        var replacement = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(replacement, 117);

        var project = CivilizationEditProject.CreateDefault();
        project.Name = name;
        project.Civilizations.Single(civilization => civilization.Name == "England").Entities.Add(new()
        {
            Name = "Synthetic transport unit", Category = CivilizationCategory.Units, SourceObjectId = 0,
            Properties = new(StringComparer.Ordinal) { ["unit.health"] = "117" }
        });

        var package = new CivilizationInstallerPackage
        {
            ProjectName = project.Name,
            ProjectJson = project.Serialize(),
            Changes = [new("England", "Synthetic transport unit", "unit.health", "Health", "100", "117", "Global object 0", path)],
            Files = [new()
            {
                RelativePath = path,
                ExpectedLength = original.Length,
                StructuralHash = CivilizationInstallerPackage.StructuralHash(path, original),
                Patches = [new()
                {
                    SemanticTarget = $"{path.ToLowerInvariant()}:{healthOffset}:4",
                    Offset = healthOffset, Length = 4,
                    Expected = original.AsSpan(healthOffset, 4).ToArray(), Replacement = replacement,
                    IdentityOffset = identityOffset, IdentityBytes = original.AsSpan(identityOffset, 4).ToArray()
                }]
            }]
        };
        // This verifies manifest/project/framing/patch bounds for transport and codec tests.
        // Stock-game review and installation require real matching game data and are not claimed here.
        CivilizationInstallerPackage.Validate(package);
        return package;
    }
}
