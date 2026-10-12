using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace EDMWMapDesigner.Civilizations;

/// <summary>Bounded top-level texture decoding for the game's SST/DDS/TGA assets.</summary>
public static class CivilizationTextureReader
{
    private const int MaximumPixels = 4 * 1024 * 1024;

    public static ModelTexture Read(byte[] data, string name)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length > CivilizationGameAssets.MaximumModelBytes) throw new InvalidDataException("The texture exceeds the input limit.");
        var payload = data.AsSpan();
        if (Path.GetExtension(name).Equals(".sst", StringComparison.OrdinalIgnoreCase))
        {
            // SST-tool's format documentation and stock EDMW files agree on
            // the 15-byte wrapper, followed by a DDS, TGA or JPEG image.
            if (payload.Length < 15 || payload[0] > 1 || payload[1] == 0 || payload[2] == 0)
                throw new InvalidDataException("The SST texture header is invalid.");
            var width = ReadInt(payload, 6);
            var height = ReadInt(payload, 10);
            ValidateDimensions(width, height);
            var revision = payload[0];
            var flags = payload[14];
            payload = payload[15..];
            if (revision == 1) return ReadDds(payload, name);
            if (flags == 0) return ReadTga(payload, name);
            if (flags == 3) return ReadImage(payload, name);
            throw new InvalidDataException("This SST texture representation is unsupported.");
        }
        if (payload.StartsWith("DDS "u8)) return ReadDds(payload, name);
        if (Path.GetExtension(name).Equals(".tga", StringComparison.OrdinalIgnoreCase)) return ReadTga(payload, name);
        return ReadImage(payload, name);
    }

    private static ModelTexture ReadDds(ReadOnlySpan<byte> data, string name)
    {
        if (data.Length < 128 || !data[..4].SequenceEqual("DDS "u8) || ReadInt(data, 4) != 124 || ReadInt(data, 76) != 32)
            throw new InvalidDataException("The DDS texture header is invalid.");
        var height = ReadInt(data, 12);
        var width = ReadInt(data, 16);
        ValidateDimensions(width, height);
        var pixels = new int[checked(width * height)];
        var flags = ReadInt(data, 80);
        if ((flags & 4) != 0)
        {
            var fourCc = BinaryPrimitives.ReadUInt32LittleEndian(data[84..]);
            var kind = fourCc switch
            {
                0x31545844 => 1, // DXT1 / BC1
                0x32545844 or 0x33545844 => 2, // DXT2/DXT3 / BC2
                0x34545844 or 0x35545844 => 3, // DXT4/DXT5 / BC3
                _ => throw new InvalidDataException("This DDS compression is unsupported.")
            };
            var premultiplied = fourCc is 0x32545844 or 0x34545844;
            DecodeBlocks(data[128..], width, height, pixels, kind, premultiplied);
        }
        else
        {
            var bits = ReadInt(data, 88);
            if (bits is not (16 or 24 or 32) || (flags & (0x40 | 0x20000)) == 0)
                throw new InvalidDataException("This DDS pixel format is unsupported.");
            var bytes = bits / 8;
            var pitch = (ReadInt(data, 8) & 8) != 0 ? ReadInt(data, 20) : checked(width * bytes);
            if (pitch < width * bytes || pitch > 65536 || data.Length - 128 < (long)pitch * height)
                throw new InvalidDataException("The DDS pixel data is truncated.");
            var red = ReadUInt(data, 92);
            var green = ReadUInt(data, 96);
            var blue = ReadUInt(data, 100);
            var alpha = (flags & 1) != 0 ? ReadUInt(data, 104) : 0u;
            var luminance = (flags & 0x20000) != 0;
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var offset = 128 + y * pitch + x * bytes;
                    uint raw = 0;
                    for (var b = 0; b < bytes; b++) raw |= (uint)data[offset + b] << (b * 8);
                    var r = ExtractChannel(raw, red, 0);
                    var g = luminance ? r : ExtractChannel(raw, green, 0);
                    var bValue = luminance ? r : ExtractChannel(raw, blue, 0);
                    pixels[y * width + x] = Pack(ExtractChannel(raw, alpha, 255), r, g, bValue);
                }
        }
        return new ModelTexture(name, width, height, pixels);
    }

    private static void DecodeBlocks(ReadOnlySpan<byte> data, int width, int height, int[] pixels, int kind, bool premultiplied)
    {
        var blockWidth = (width + 3) / 4;
        var blockHeight = (height + 3) / 4;
        var stride = kind == 1 ? 8 : 16;
        if ((long)blockWidth * blockHeight * stride > data.Length) throw new InvalidDataException("The DDS blocks are truncated.");
        Span<int> colors = stackalloc int[4];
        Span<int> alphas = stackalloc int[8];
        for (var blockY = 0; blockY < blockHeight; blockY++)
            for (var blockX = 0; blockX < blockWidth; blockX++)
            {
                var block = data.Slice((blockY * blockWidth + blockX) * stride, stride);
                var colorOffset = kind == 1 ? 0 : 8;
                var color0 = BinaryPrimitives.ReadUInt16LittleEndian(block[colorOffset..]);
                var color1 = BinaryPrimitives.ReadUInt16LittleEndian(block[(colorOffset + 2)..]);
                colors[0] = Rgb565(color0);
                colors[1] = Rgb565(color1);
                if (color0 > color1 || kind != 1)
                {
                    colors[2] = Mix(colors[0], colors[1], 2, 1, 3);
                    colors[3] = Mix(colors[0], colors[1], 1, 2, 3);
                }
                else
                {
                    colors[2] = Mix(colors[0], colors[1], 1, 1, 2);
                    colors[3] = 0;
                }
                var indices = BinaryPrimitives.ReadUInt32LittleEndian(block[(colorOffset + 4)..]);
                ulong alphaIndices = 0;
                if (kind == 3)
                {
                    alphas[0] = block[0]; alphas[1] = block[1];
                    if (alphas[0] > alphas[1])
                        for (var index = 2; index < 8; index++) alphas[index] = ((8 - index) * alphas[0] + (index - 1) * alphas[1]) / 7;
                    else
                    {
                        for (var index = 2; index < 6; index++) alphas[index] = ((6 - index) * alphas[0] + (index - 1) * alphas[1]) / 5;
                        alphas[6] = 0; alphas[7] = 255;
                    }
                    for (var index = 0; index < 6; index++) alphaIndices |= (ulong)block[index + 2] << (8 * index);
                }
                for (var index = 0; index < 16; index++)
                {
                    var x = blockX * 4 + index % 4;
                    var y = blockY * 4 + index / 4;
                    if (x >= width || y >= height) continue;
                    var color = colors[(int)((indices >> (index * 2)) & 3)];
                    var alpha = kind switch
                    {
                        2 => ((block[index / 2] >> ((index & 1) * 4)) & 15) * 17,
                        3 => alphas[(int)((alphaIndices >> (index * 3)) & 7)],
                        _ => (int)((uint)color >> 24)
                    };
                    var r = (color >> 16) & 255;
                    var g = (color >> 8) & 255;
                    var b = color & 255;
                    if (premultiplied && alpha > 0)
                    { r = Math.Min(255, r * 255 / alpha); g = Math.Min(255, g * 255 / alpha); b = Math.Min(255, b * 255 / alpha); }
                    pixels[y * width + x] = Pack(alpha, r, g, b);
                }
            }
    }

    private static ModelTexture ReadTga(ReadOnlySpan<byte> data, string name)
    {
        if (data.Length < 18 || data[1] != 0 || data[2] is not (2 or 3 or 10 or 11))
            throw new InvalidDataException("This TGA texture header is unsupported.");
        var width = BinaryPrimitives.ReadUInt16LittleEndian(data[12..]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]);
        ValidateDimensions(width, height);
        var bits = data[16];
        var gray = data[2] is 3 or 11;
        if ((gray && bits is not (8 or 16)) || (!gray && bits is not (16 or 24 or 32)))
            throw new InvalidDataException("This TGA pixel format is unsupported.");
        var stride = bits / 8;
        var cursor = 18 + data[0];
        var pixels = new int[width * height];
        var rle = data[2] is 10 or 11;
        var position = 0;
        var hasAlpha = (data[17] & 15) != 0;
        while (position < pixels.Length)
        {
            var packet = rle ? ReadByte(data, ref cursor) : (byte)0;
            var count = rle ? (packet & 127) + 1 : 1;
            if (count > pixels.Length - position) throw new InvalidDataException("A TGA packet exceeds its dimensions.");
            var repeated = (packet & 128) != 0;
            var color = 0;
            for (var index = 0; index < count; index++)
            {
                if (!repeated || index == 0)
                {
                    if (data.Length - cursor < stride) throw new InvalidDataException("The TGA pixels are truncated.");
                    if (gray) color = Pack(hasAlpha && stride == 2 ? data[cursor + 1] : 255, data[cursor], data[cursor], data[cursor]);
                    else if (stride == 2)
                    {
                        var raw = BinaryPrimitives.ReadUInt16LittleEndian(data[cursor..]);
                        color = Pack(hasAlpha && (raw & 0x8000) == 0 ? 0 : 255,
                            ((raw >> 10) & 31) * 255 / 31, ((raw >> 5) & 31) * 255 / 31, (raw & 31) * 255 / 31);
                    }
                    else color = Pack(hasAlpha && stride == 4 ? data[cursor + 3] : 255, data[cursor + 2], data[cursor + 1], data[cursor]);
                    cursor += stride;
                }
                var x = position % width;
                var y = position / width;
                if ((data[17] & 0x10) != 0) x = width - 1 - x;
                if ((data[17] & 0x20) == 0) y = height - 1 - y;
                pixels[y * width + x] = color;
                position++;
            }
        }
        return new ModelTexture(name, width, height, pixels);
    }

    private static ModelTexture ReadImage(ReadOnlySpan<byte> data, string name)
    {
        ValidateImageHeader(data);
        try
        {
            using var stream = new MemoryStream(data.ToArray(), writable: false);
            using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            ValidateDimensions(image.Width, image.Height);
            using var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap)) graphics.DrawImageUnscaled(image, 0, 0);
            var locked = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var pixels = new int[bitmap.Width * bitmap.Height];
                for (var y = 0; y < bitmap.Height; y++) Marshal.Copy(IntPtr.Add(locked.Scan0, y * locked.Stride), pixels, y * bitmap.Width, bitmap.Width);
                return new ModelTexture(name, bitmap.Width, bitmap.Height, pixels);
            }
            finally { bitmap.UnlockBits(locked); }
        }
        catch (Exception exception) when (exception is ArgumentException or ExternalException or OutOfMemoryException)
        { throw new InvalidDataException("The texture image cannot be decoded.", exception); }
    }

    private static void ValidateImageHeader(ReadOnlySpan<byte> data)
    {
        // Check encoded dimensions before allowing GDI+ to allocate a bitmap.
        if (data.Length >= 24 && data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            var width = BinaryPrimitives.ReadUInt32BigEndian(data[16..]);
            var height = BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
            if (width > int.MaxValue || height > int.MaxValue) throw new InvalidDataException("The image dimensions exceed their limit.");
            ValidateDimensions((int)width, (int)height);
            return;
        }
        if (data.Length < 4 || data[0] != 0xff || data[1] != 0xd8)
            throw new InvalidDataException("This texture image representation is unsupported.");
        var cursor = 2;
        while (cursor < data.Length)
        {
            if (data[cursor++] != 0xff) throw new InvalidDataException("The JPEG marker stream is invalid.");
            while (cursor < data.Length && data[cursor] == 0xff) cursor++;
            if (cursor >= data.Length) break;
            var marker = data[cursor++];
            if (marker is 0xd8 or 0x01 or >= 0xd0 and <= 0xd7) continue;
            if (marker is 0xd9 or 0xda || data.Length - cursor < 2) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[cursor..]);
            if (length < 2 || data.Length - cursor < length) throw new InvalidDataException("The JPEG segment is truncated.");
            if (marker is >= 0xc0 and <= 0xc3 or >= 0xc5 and <= 0xc7 or >= 0xc9 and <= 0xcb or >= 0xcd and <= 0xcf)
            {
                if (length < 8) throw new InvalidDataException("The JPEG frame header is truncated.");
                ValidateDimensions(BinaryPrimitives.ReadUInt16BigEndian(data[(cursor + 5)..]),
                    BinaryPrimitives.ReadUInt16BigEndian(data[(cursor + 3)..]));
                return;
            }
            cursor += length;
        }
        throw new InvalidDataException("The JPEG frame header is missing.");
    }

    private static int ReadInt(ReadOnlySpan<byte> data, int offset)
    {
        var value = ReadUInt(data, offset);
        return value <= int.MaxValue ? (int)value : throw new InvalidDataException("A texture header value exceeds its limit.");
    }
    private static uint ReadUInt(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || data.Length - offset < 4) throw new InvalidDataException("The texture header is truncated.");
        return BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    }
    private static byte ReadByte(ReadOnlySpan<byte> data, ref int cursor)
    {
        if (cursor < 0 || cursor >= data.Length) throw new InvalidDataException("The texture data is truncated.");
        return data[cursor++];
    }
    private static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 4096 || height > 4096 || (long)width * height > MaximumPixels)
            throw new InvalidDataException("The texture dimensions exceed the preview limit.");
    }
    private static int ExtractChannel(uint pixel, uint mask, int fallback)
    {
        if (mask == 0) return fallback;
        var shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        var maximum = mask >> shift;
        if ((maximum & (maximum + 1)) != 0) throw new InvalidDataException("A DDS channel mask is not contiguous.");
        return (int)((((ulong)pixel & mask) >> shift) * 255 / maximum);
    }
    private static int Rgb565(ushort color) => Pack(255, ((color >> 11) & 31) * 255 / 31, ((color >> 5) & 63) * 255 / 63, (color & 31) * 255 / 31);
    private static int Mix(int first, int second, int firstWeight, int secondWeight, int divisor) => Pack(255,
        (((first >> 16) & 255) * firstWeight + ((second >> 16) & 255) * secondWeight) / divisor,
        (((first >> 8) & 255) * firstWeight + ((second >> 8) & 255) * secondWeight) / divisor,
        ((first & 255) * firstWeight + (second & 255) * secondWeight) / divisor);
    private static int Pack(int alpha, int red, int green, int blue) => unchecked((int)((uint)alpha << 24 | (uint)red << 16 | (uint)green << 8 | (uint)blue));
}
