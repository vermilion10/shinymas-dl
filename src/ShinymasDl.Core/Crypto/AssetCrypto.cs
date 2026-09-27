using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ShinymasDl.Core.Crypto;

/// <summary> native port of the client's <c>resource_hash</c> wasm: CDN filenames and the text-asset cipher </summary>
public static class AssetCrypto
{
    // The wasm embeds this as one string; the whole thing is the XOR key.
    private static readonly byte[] Key = Encoding.ASCII.GetBytes(
        "B'KYWL[DI\\vqUIyw_we_are_hiring_https://knocknote.co.jp");

    /// <summary> media keeps its extension on the CDN, everything else is a bare hash </summary>
    private static readonly HashSet<string> KeepExtension = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".mp4", ".m4a" };

    /// <summary> the hashed CDN name for a path under the asset root, e.g. <c>images/bg/001.jpg</c> </summary>
    public static string HashName(string assetPath)
    {
        var fullPath = "/assets/" + assetPath;
        var extension = Path.GetExtension(assetPath);
        var stem = Path.GetFileNameWithoutExtension(assetPath);

        // sha256(first char of stem + last char of stem + full path)
        var input = Encoding.UTF8.GetBytes(string.Concat(stem[0], stem[^1], fullPath));
        var name = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();

        return KeepExtension.Contains(extension) ? name + extension : name;
    }

    /// <summary> only the text assets are encrypted; images, audio, video and fonts are served as-is </summary>
    public static bool IsEncrypted(string assetPath) =>
        Path.GetExtension(assetPath).ToLowerInvariant() is ".json" or ".atlas";

    public static byte[] Decrypt(ReadOnlySpan<byte> data)
    {
        var buffer = data.ToArray();
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] ^= Key[i % Key.Length];
        }

        var offset = GzipHeaderLength(buffer);

        // The gzip trailer is cut off, so inflate the raw deflate body instead.
        using var input = new MemoryStream(buffer, offset, buffer.Length - offset);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(buffer.Length * 4);
        deflate.CopyTo(output);
        return output.ToArray();
    }

    public static string DecryptText(ReadOnlySpan<byte> data) => Encoding.UTF8.GetString(Decrypt(data));

    private static int GzipHeaderLength(byte[] data)
    {
        if (data.Length < 10 || data[0] != 0x1f || data[1] != 0x8b || data[2] != 8)
        {
            throw new InvalidDataException("Not an encrypted asset: the XOR-decoded header is not gzip.");
        }

        var flags = data[3];
        var offset = 10;

        if ((flags & 0x04) != 0)
        {
            offset += 2 + (data[offset] | data[offset + 1] << 8);
        }

        if ((flags & 0x08) != 0)
        {
            offset = Array.IndexOf(data, (byte)0, offset) + 1;
        }

        if ((flags & 0x10) != 0)
        {
            offset = Array.IndexOf(data, (byte)0, offset) + 1;
        }

        if ((flags & 0x02) != 0)
        {
            offset += 2;
        }

        return offset;
    }
}
