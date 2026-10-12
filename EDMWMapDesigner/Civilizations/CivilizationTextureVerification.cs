using System.Buffers.Binary;

namespace EDMWMapDesigner.Civilizations;

/// <summary>Numeric decoder checks; no images are displayed or saved.</summary>
internal static class CivilizationTextureVerification
{
    public static IReadOnlyList<string> Verify()
    {
        var checks = new List<string>();
        var bc1 = Dds("DXT1"u8, 8);
        Put16(bc1, 128, 0xf800); Put16(bc1, 130, 0x07e0);
        Put32(bc1, 132, 0x000000e4);
        var opaque = CivilizationTextureReader.Read(bc1, "opaque.dds");
        Require(opaque.Pixels[0] == unchecked((int)0xffff0000) && opaque.Pixels[1] == unchecked((int)0xff00ff00) &&
            opaque.Pixels[2] == unchecked((int)0xffaa5500) && opaque.Pixels[3] == unchecked((int)0xff55aa00), "BC1 color interpolation");
        Put16(bc1, 128, 0); Put16(bc1, 130, 0xffff); Put32(bc1, 132, uint.MaxValue);
        Require(CivilizationTextureReader.Read(bc1, "alpha.dds").Pixels.All(pixel => pixel == 0), "BC1 cutout alpha");
        var wrapped = new byte[15 + bc1.Length];
        wrapped[0] = 1; wrapped[1] = 1; wrapped[2] = 1; Put32(wrapped, 6, 4); Put32(wrapped, 10, 4);
        bc1.CopyTo(wrapped, 15);
        Require(CivilizationTextureReader.Read(wrapped, "alpha.sst").Pixels.All(pixel => pixel == 0), "SST DDS framing");
        checks.Add("BC1 color, cutout alpha and SST framing");

        var bc2 = Dds("DXT3"u8, 16);
        Array.Fill(bc2, (byte)0x88, 128, 8);
        Put16(bc2, 136, 0xf800); Put16(bc2, 138, 0x07e0);
        Require(CivilizationTextureReader.Read(bc2, "alpha.dds").Pixels.All(pixel => pixel == unchecked((int)0x88ff0000)), "BC2 four-bit alpha");
        checks.Add("BC2 alpha");

        var bc3 = Dds("DXT5"u8, 16);
        bc3[128] = 0; bc3[129] = 255; bc3[130] = 7 | 6 << 3;
        Put16(bc3, 136, 0xf800); Put16(bc3, 138, 0x07e0);
        var alpha = CivilizationTextureReader.Read(bc3, "alpha.dds");
        Require(alpha.Pixels[0] == unchecked((int)0xffff0000) && alpha.Pixels[1] == 0x00ff0000, "BC3 explicit alpha endpoints");
        bc3[128] = 255; bc3[129] = 0; bc3[130] = 2;
        Require((uint)CivilizationTextureReader.Read(bc3, "alpha.dds").Pixels[0] >> 24 == 218, "BC3 interpolated alpha");
        checks.Add("BC3 alpha palettes");

        var tga = Tga(2, 24, 0, 12);
        new byte[] { 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255 }.CopyTo(tga, 18);
        Require(CivilizationTextureReader.Read(tga, "origin.tga").Pixels.SequenceEqual(new[]
        { unchecked((int)0xff0000ff), unchecked((int)0xffffffff), unchecked((int)0xffff0000), unchecked((int)0xff00ff00) }), "TGA bottom-left origin");
        tga[17] = 0x10;
        Require(CivilizationTextureReader.Read(tga, "origin.tga").Pixels[0] == unchecked((int)0xffffffff), "TGA right origin");
        var rle = Tga(10, 32, 0x28, 5);
        new byte[] { 0x83, 30, 20, 10, 90 }.CopyTo(rle, 18);
        Require(CivilizationTextureReader.Read(rle, "rle.tga").Pixels.All(pixel => pixel == 0x5a0a141e), "TGA RLE alpha");
        checks.Add("TGA origin, RLE and alpha");

        Reject(() => CivilizationTextureReader.Read(bc2[..^1], "truncated.dds"), "truncated DDS block");
        Reject(() => CivilizationTextureReader.Read(rle[..^1], "truncated.tga"), "truncated TGA packet");
        rle[18] = 0xff;
        Reject(() => CivilizationTextureReader.Read(rle, "oversized.tga"), "oversized TGA packet");
        Put32(bc1, 16, uint.MaxValue);
        Reject(() => CivilizationTextureReader.Read(bc1, "oversized.dds"), "overflowing DDS width");
        wrapped[1] = 0;
        Reject(() => CivilizationTextureReader.Read(wrapped, "invalid.sst"), "invalid SST resolutions");
        checks.Add("Truncation, packet limits and dimensions");
        return checks.AsReadOnly();
    }

    private static byte[] Dds(ReadOnlySpan<byte> compression, int blockBytes)
    {
        var data = new byte[128 + blockBytes];
        "DDS "u8.CopyTo(data); Put32(data, 4, 124); Put32(data, 8, 0x1007); Put32(data, 12, 4); Put32(data, 16, 4);
        Put32(data, 76, 32); Put32(data, 80, 4); compression.CopyTo(data.AsSpan(84));
        return data;
    }
    private static byte[] Tga(byte type, byte bits, byte descriptor, int pixelBytes)
    {
        var data = new byte[18 + pixelBytes]; data[2] = type; Put16(data, 12, 2); Put16(data, 14, 2); data[16] = bits; data[17] = descriptor; return data;
    }
    private static void Put16(byte[] data, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value);
    private static void Put32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    private static void Require(bool condition, string name) { if (!condition) throw new InvalidDataException("Texture verification failed: " + name); }
    private static void Reject(Action action, string name)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new InvalidDataException("Texture verification accepted " + name);
    }
}
