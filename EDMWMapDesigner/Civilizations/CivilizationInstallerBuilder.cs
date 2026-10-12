using System.Reflection;

namespace EDMWMapDesigner.Civilizations;

public static class CivilizationInstallerBuilder
{
    public static string SuggestedFileName(string projectName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(projectName.Trim().Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray());
        name = name.Trim(' ', '.');
        if (name.Length == 0) name = "Civilization";
        if (name.Length > 80) name = name[..80].TrimEnd(' ', '.');
        return "EDMW_" + name + "civ_installer.exe";
    }

    public static string Export(CivilizationEditProject project, string gamePath, string path)
    {
        var package = CivilizationInstallerPackage.Create(project, gamePath);
        var payload = CivilizationInstallerPackageCodec.Encode(package);
        var destination = Path.GetFullPath(path);
        if (!destination.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The shared civilization installer must use the .exe extension.");
        var gameRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath));
        if (destination.Equals(Path.Combine(gameRoot, "Empires_DMW.exe"), StringComparison.OrdinalIgnoreCase) ||
            destination.StartsWith(Path.Combine(gameRoot, "Data") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Save the installer outside the game's data and executable files.");
        using var template = Assembly.GetExecutingAssembly().GetManifestResourceStream("EDMWMapDesigner.Tools.EDMWCivilizationInstaller.exe")
            ?? throw new InvalidOperationException("The standalone civilization installer template is missing. Rebuild with build.ps1.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                template.CopyTo(output);
                CivilizationInstallerPackageCodec.Append(output, payload);
                output.Flush(true);
            }
            // Reopen through the same codec the recipient runs before publishing.
            var verified = CivilizationInstallerPackageCodec.ReadExecutable(temporary);
            if (verified.PackageId != package.PackageId) throw new InvalidDataException("The exported package identity could not be verified.");
            File.Move(temporary, destination, true);
            return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
