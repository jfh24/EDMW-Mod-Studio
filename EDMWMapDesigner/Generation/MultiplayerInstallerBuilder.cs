using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace EDMWMapDesigner.Generation;

internal static class MultiplayerInstallerBuilder
{
    private const string StubResourceName = "EDMWMapDesigner.Tools.EDMWMultiplayerInstaller.exe";
    private static readonly byte[] FooterMagic = Encoding.ASCII.GetBytes("EDMWMAPINSTALL01");

    public static string Create(string zipPath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var zipBytes = File.ReadAllBytes(zipPath);
        ValidatePackage(zipBytes);

        using var stub = Assembly.GetExecutingAssembly().GetManifestResourceStream(StubResourceName)
            ?? throw new InvalidOperationException("The embedded multiplayer installer is missing from this build.");
        var resolvedOutput = Path.GetFullPath(outputPath);
        var parent = Path.GetDirectoryName(resolvedOutput);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        var staged = resolvedOutput + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stub.CopyTo(output);
                var packageOffset = output.Position;
                output.Write(zipBytes);
                output.Write(FooterMagic);
                output.Write(BitConverter.GetBytes(packageOffset));
                output.Write(BitConverter.GetBytes((long)zipBytes.Length));
                output.Write(SHA256.HashData(zipBytes));
                output.Flush(true);
            }
            File.Move(staged, resolvedOutput, true);
            return resolvedOutput;
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
    }

    private static void ValidatePackage(byte[] package)
    {
        using var memory = new MemoryStream(package, writable: false);
        using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
        var files = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToArray();
        var generator = files.SingleOrDefault(entry => !entry.FullName.Contains('/') &&
            entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        var traceMarkers = files.Where(entry => !entry.FullName.Contains('/') &&
            entry.Name.EndsWith(".trace", StringComparison.OrdinalIgnoreCase)).ToArray();
        var diagnosticLayout = files.Length == 5 && generator is not null &&
            traceMarkers.Length == 1 && traceMarkers[0].Length == 0 &&
            Path.GetFileNameWithoutExtension(traceMarkers[0].Name).Equals(
                Path.GetFileNameWithoutExtension(generator.Name), StringComparison.OrdinalIgnoreCase);
        if ((files.Length != 4 && !diagnosticLayout) || generator is null ||
            files.Count(entry => entry.FullName.StartsWith("Data/Random Map Scripts/", StringComparison.OrdinalIgnoreCase) && entry.Name.EndsWith(".rmv", StringComparison.OrdinalIgnoreCase)) != 1 ||
            files.Count(entry => entry.FullName.StartsWith("Data/Random Map Scripts/EDMW Anchors/", StringComparison.OrdinalIgnoreCase) && entry.Name.EndsWith(".edmwa", StringComparison.OrdinalIgnoreCase)) != 1 ||
            files.Count(entry => entry.FullName.Equals("EDMW Installer/map-preview.png", StringComparison.OrdinalIgnoreCase)) != 1)
            throw new InvalidDataException("The multiplayer package does not contain the expected generator, RMV, terrain payload, and map preview.");
    }
}
