using System;
using System.Buffers.Binary;
using System.Drawing;
using System.IO;
using System.IO.Compression;

// L4a: stand-in for System.Drawing.Bitmap on Linux (System.Drawing.Common is Windows-only since .NET 7).
// Only what the smoke test uses: load a PNG written by vlqt_test_window_grab (QImage::save), Width, Height, GetPixel.
// Supports 8-bit grey/RGB/grey+alpha/RGBA, non-interlaced (what Qt writes). Compiled only on non-Windows hosts.
sealed class Bitmap : IDisposable
{
    readonly byte[] _rgba; // 4 bytes per pixel, row-major

    public int Width { get; }
    public int Height { get; }

    public Bitmap(string path)
    {
        var data = File.ReadAllBytes(path);
        ReadOnlySpan<byte> sig = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        if (data.Length < 8 || !data.AsSpan(0, 8).SequenceEqual(sig))
            throw new InvalidDataException($"{path}: not a PNG");
        int bitDepth = 0, colorType = 0, interlace = 0;
        using var idat = new MemoryStream();
        for (int p = 8; p + 8 <= data.Length;)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(p));
            var type = System.Text.Encoding.ASCII.GetString(data, p + 4, 4);
            var chunk = data.AsSpan(p + 8, len);
            if (type == "IHDR")
            {
                Width = BinaryPrimitives.ReadInt32BigEndian(chunk);
                Height = BinaryPrimitives.ReadInt32BigEndian(chunk[4..]);
                bitDepth = chunk[8]; colorType = chunk[9]; interlace = chunk[12];
            }
            else if (type == "IDAT") idat.Write(chunk);
            else if (type == "IEND") break;
            p += 12 + len;
        }
        int channels = colorType switch { 0 => 1, 2 => 3, 4 => 2, 6 => 4, _ => 0 };
        if (bitDepth != 8 || channels == 0 || interlace != 0)
            throw new InvalidDataException($"{path}: unsupported PNG (depth {bitDepth}, color type {colorType}, interlace {interlace})");

        int stride = Width * channels;
        var raw = new byte[(stride + 1) * Height];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
            z.ReadExactly(raw);

        var cur = new byte[stride];
        var prev = new byte[stride];
        _rgba = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            int filter = raw[y * (stride + 1)];
            Array.Copy(raw, y * (stride + 1) + 1, cur, 0, stride);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= channels ? cur[i - channels] : 0, b = prev[i], c = i >= channels ? prev[i - channels] : 0;
                cur[i] = (byte)(cur[i] + filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException($"{path}: bad filter {filter}"),
                });
            }
            for (int x = 0; x < Width; x++)
            {
                int s = x * channels, d = (y * Width + x) * 4;
                switch (channels)
                {
                    case 1: _rgba[d] = _rgba[d + 1] = _rgba[d + 2] = cur[s]; _rgba[d + 3] = 255; break;
                    case 2: _rgba[d] = _rgba[d + 1] = _rgba[d + 2] = cur[s]; _rgba[d + 3] = cur[s + 1]; break;
                    case 3: _rgba[d] = cur[s]; _rgba[d + 1] = cur[s + 1]; _rgba[d + 2] = cur[s + 2]; _rgba[d + 3] = 255; break;
                    default: Array.Copy(cur, s, _rgba, d, 4); break;
                }
            }
            (cur, prev) = (prev, cur);
        }
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    public Color GetPixel(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            throw new ArgumentOutOfRangeException(nameof(x), $"({x},{y}) outside {Width}x{Height}");
        int d = (y * Width + x) * 4;
        return Color.FromArgb(_rgba[d + 3], _rgba[d], _rgba[d + 1], _rgba[d + 2]);
    }

    public void Dispose() { }
}
