using System.Text;
using EDMWMapDesigner.Models;

namespace EDMWMapDesigner.Generation;

internal sealed record MapPackageResult(GenerationResult Generation, string ScriptPath, string PayloadPath);

internal static class GeneratedMapPackage
{
    internal static MapPackageResult Write(
        MapProject map,
        string scriptPath,
        string? stockScriptsFolder = null)
        => WriteCore(map, scriptPath, stockScriptsFolder, requireStockTemplate: true);

    internal static MapPackageResult WriteForSelfTest(MapProject map, string scriptPath) =>
        WriteCore(map, scriptPath, null, requireStockTemplate: false);

    private static MapPackageResult WriteCore(
        MapProject map,
        string scriptPath,
        string? stockScriptsFolder,
        bool requireStockTemplate)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        map.Validate();

        var resolvedScriptPath = Path.GetFullPath(scriptPath);
        var scriptsFolder = Path.GetDirectoryName(resolvedScriptPath)
            ?? throw new ArgumentException("The RMV path does not have a parent folder.", nameof(scriptPath));
        Directory.CreateDirectory(scriptsFolder);

        var resolvedStockScriptsFolder = !string.IsNullOrWhiteSpace(stockScriptsFolder)
            ? Path.GetFullPath(stockScriptsFolder)
            : StockResourceProfileImporter.IsStockScriptsFolder(scriptsFolder)
                ? scriptsFolder
                : null;
        if (resolvedStockScriptsFolder is not null &&
            !StockResourceProfileImporter.IsStockScriptsFolder(resolvedStockScriptsFolder))
            throw new InvalidOperationException(
                $"'{resolvedStockScriptsFolder}' is not an Empires DMW Random Map Scripts folder.");
        if (requireStockTemplate && resolvedStockScriptsFolder is null)
            throw new InvalidOperationException(
                "The original Empires DMW Random Map Scripts folder is required. " +
                "Select a valid EDMW installation so its original map templates can be read.");
        var generation = RmvGenerator.Generate(map, resolvedStockScriptsFolder);
        var stagedScript = resolvedScriptPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(stagedScript, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       64 * 1024, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(generation.Script);
                writer.Flush();
                stream.Flush(true);
            }

            var payloadPath = AnchorPackage.Write(map, scriptsFolder);
            File.Move(stagedScript, resolvedScriptPath, true);
            return new MapPackageResult(generation, resolvedScriptPath, payloadPath);
        }
        finally
        {
            if (File.Exists(stagedScript)) File.Delete(stagedScript);
        }
    }
}
