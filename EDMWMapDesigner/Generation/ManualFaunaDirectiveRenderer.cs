using EDMWMapDesigner.Models;

namespace EDMWMapDesigner.Generation;

/// <summary>
/// Keeps manual fauna out of the stock RMV parser. The native generator owns exact
/// descriptor validation and final placement after stock world generation.
/// </summary>
internal static class ManualFaunaDirectiveRenderer
{
    internal static string Append(string source, MapProject map)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(map);
        return source;
    }
}
