using System.Reflection;

namespace EDMWMapDesigner.Generation;

internal static class EmbeddedGenerator
{
    private const string ResourceName = "EDMWMapDesigner.Native.EDMWAnchored.dll";

    internal static byte[] Read()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The embedded generator DLL is missing from this build.");
        using var memory = new MemoryStream();
        resource.CopyTo(memory);
        return memory.ToArray();
    }

    internal static void ValidateX86Dll(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 512 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
            throw new InvalidDataException("The embedded generator is not a valid Windows DLL.");
        var peOffset = BitConverter.ToInt32(bytes[0x3C..0x40]);
        if (peOffset < 0 || peOffset + 6 > bytes.Length ||
            bytes[peOffset] != (byte)'P' || bytes[peOffset + 1] != (byte)'E' ||
            BitConverter.ToUInt16(bytes[(peOffset + 4)..(peOffset + 6)]) != 0x014C)
            throw new InvalidDataException("The embedded generator must be a 32-bit x86 DLL for Empires DMW.");
    }
}
