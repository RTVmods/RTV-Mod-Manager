// Gets the original font file back out of a font Godot imported.
//
// A mod ships its fonts as the editor imported them: a .ttf/.otf becomes
// res://.godot/imported/<name>-<hash>.fontdata, a FontFile resource. The
// resource keeps the source file verbatim in its `data` byte array, so
// the font is the bytes after that array's length prefix. The resource
// is usually compressed ("RSCC": blocks of zstd, deflate or gzip, the
// way Godot's FileAccessCompressed writes them).

using System.IO.Compression;
using System.Text;

namespace VostokModManager.Api;

public static class GodotFontData
{
    /// <summary>The .ttf/.otf inside an imported font resource (compressed
    /// or not), or null when there is none.</summary>
    public static byte[]? ExtractFont(byte[] resource)
    {
        var data = IsTag(resource, 0, "RSCC") ? Decompress(resource) : resource;
        return data == null ? null : FindSfnt(data);
    }

    /// <summary>Inflates an RSCC file: magic, mode, block size, total size,
    /// then each block's compressed size and the blocks. Null for a mode
    /// it can't read (FastLZ) or a damaged file.</summary>
    public static byte[]? Decompress(byte[] file)
    {
        try
        {
            var mode = BitConverter.ToUInt32(file, 4);
            var blockSize = (int)BitConverter.ToUInt32(file, 8);
            var total = (int)BitConverter.ToUInt32(file, 12);
            if (blockSize <= 0 || total < 0) return null;
            var blocks = total / blockSize + 1;
            var offset = 16 + 4 * blocks;
            var output = new byte[total];
            var written = 0;
            for (var i = 0; i < blocks; i++)
            {
                var size = (int)BitConverter.ToUInt32(file, 16 + 4 * i);
                var expected = i < blocks - 1 ? blockSize : total - blockSize * (blocks - 1);
                if (expected > 0)
                {
                    var block = InflateBlock(mode, file.AsSpan(offset, size).ToArray(), expected);
                    if (block == null || block.Length != expected) return null;
                    block.CopyTo(output, written);
                    written += expected;
                }
                offset += size;
            }
            return written == total ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? InflateBlock(uint mode, byte[] block, int expected)
    {
        switch (mode)
        {
            case 2:  // Zstandard
                using (var z = new ZstdSharp.Decompressor())
                    return z.Unwrap(block, expected).ToArray();
            case 1:  // Deflate, with zlib's header
                return ReadAll(new ZLibStream(new MemoryStream(block), CompressionMode.Decompress), expected);
            case 3:  // Gzip
                return ReadAll(new GZipStream(new MemoryStream(block), CompressionMode.Decompress), expected);
            default: // FastLZ: not worth a decoder for fonts
                return null;
        }
    }

    private static byte[] ReadAll(Stream s, int expected)
    {
        using (s)
        {
            var buffer = new byte[expected];
            var read = 0;
            int n;
            while (read < expected && (n = s.Read(buffer, read, expected - read)) > 0) read += n;
            return read == expected ? buffer : buffer[..read];
        }
    }

    /// <summary>The first TrueType/OpenType file in `data` that sits right
    /// after its own length (a byte array in the resource), with a sane
    /// table count.</summary>
    internal static byte[]? FindSfnt(byte[] data)
    {
        for (var i = 4; i + 12 <= data.Length; i++)
        {
            if (!(IsTag(data, i, "OTTO") || IsTag(data, i, "true")
                  || (data[i] == 0 && data[i + 1] == 1 && data[i + 2] == 0 && data[i + 3] == 0)))
                continue;
            var length = BitConverter.ToUInt32(data, i - 4);
            if (length < 1024 || i + length > data.Length) continue;
            var tables = (data[i + 4] << 8) | data[i + 5];
            if (tables is < 4 or > 64) continue;
            return data.AsSpan(i, (int)length).ToArray();
        }
        return null;
    }

    private static bool IsTag(byte[] data, int at, string tag)
        => at + 4 <= data.Length && Encoding.ASCII.GetString(data, at, 4) == tag;
}
