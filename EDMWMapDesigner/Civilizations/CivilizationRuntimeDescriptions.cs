using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace EDMWMapDesigner.Civilizations;

// Display metadata belongs to the immutable launch snapshot, not the installed
// tables. Older packages can recover authored names without rewriting backups.
internal static class CivilizationRuntimeDescriptions
{
    internal static IReadOnlyList<CivilizationRuntimeUnitRule> ForLaunch(
        CivilizationInstallerPackage package, IEnumerable<CivilizationRuntimeUnitRule> rules,
        IReadOnlyDictionary<int, string> targetNames)
    {
        var project = CivilizationEditProject.Deserialize(package.ProjectJson);
        var authored = project.Civilizations.SelectMany(civilization => civilization.Entities
            .SelectMany(entity => CivilizationRuntimeConfiguration.UnitRulesFor(civilization, entity)))
            .ToDictionary(rule => rule.Id, rule => rule.Name);
        return rules.Select(rule => rule with
        {
            Name = rule.Name.Length == 0 && authored.TryGetValue(rule.Id, out var name) ? name : rule.Name,
            TargetName = targetNames.GetValueOrDefault(rule.TargetObjectId, rule.TargetName),
            Actions = rule.Actions is null ? null : rule.Actions.Select(action => action with
                { ObjectName = targetNames.GetValueOrDefault(action.ObjectId, action.ObjectName) }).ToArray()
        }).ToArray();
    }

    internal static IReadOnlyDictionary<int, string> ReadTargetNames(string gamePath, IEnumerable<int> objects)
    {
        var targets = objects.Distinct().ToArray();
        var result = new Dictionary<int, string>();
        if (targets.Length == 0) return result;
        // LoadLibraryEx as a data file reads resources without running DLL code.
        var library = LoadLibraryExW(Path.Combine(gamePath, "Language2.dll"), IntPtr.Zero, 0x00000002);
        if (library == IntPtr.Zero) return result;
        try
        {
            using var stream = File.OpenRead(Path.Combine(gamePath, "Data", "db", "dbobjects.dat"));
            Span<byte> value = stackalloc byte[4];
            stream.ReadExactly(value);
            var count = BinaryPrimitives.ReadInt32LittleEndian(value);
            if (count is <= 0 or > 100_000 || stream.Length != 4L + count * 1936L)
                throw new InvalidDataException("The target-name object table has an invalid layout.");
            foreach (var target in targets)
            {
                if (target < 0 || target >= count) continue;
                // Same player-facing Language2 name field used by CivilizationGameData.
                stream.Position = 4L + target * 1936L + 296;
                stream.ReadExactly(value);
                var nameId = BinaryPrimitives.ReadUInt32LittleEndian(value);
                if (nameId is 0 or > 65535) continue;
                var buffer = new StringBuilder(4096);
                if (LoadStringW(library, nameId, buffer, buffer.Capacity) <= 0) continue;
                var name = string.Concat(buffer.ToString().Select(character =>
                    char.IsControl(character) || character is '\u2028' or '\u2029' ? ' ' : character)).Trim();
                if (name.Length > 100)
                    name = name[..(char.IsHighSurrogate(name[99]) ? 99 : 100)].TrimEnd();
                if (name.Length != 0) result[target] = name;
            }
        }
        finally { FreeLibrary(library); }
        return result;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int LoadStringW(IntPtr module, uint identifier, StringBuilder buffer, int capacity);
    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr module);
}
