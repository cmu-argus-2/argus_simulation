using System;
using System.IO;
using System.IO.Compression;

namespace Argus.Simulation.Core
{
    // Minimal PNG encoder for the recorder, with no Unity reference: 8-bit sRGB, RGB or RGBA, rows top
    // to bottom as given, no filtering, one IDAT chunk.
    internal static class PngEncoder
    {
        private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        private static readonly uint[] CrcTable = BuildCrcTable();

        // pixels holds height rows of width x channels bytes; channels is 3 (RGB) or 4 (RGBA).
        public static byte[] Encode(int width, int height, int channels, byte[] pixels)
        {
            if (width <= 0 || height <= 0 || (channels != 3 && channels != 4))
            {
                throw new ArgumentException("A positive size and 3 or 4 channels are required.");
            }

            if (pixels == null || pixels.Length != (long)width * height * channels)
            {
                throw new ArgumentException(
                    $"Expected {(long)width * height * channels} bytes for {width}x{height}x{channels}, got {pixels?.Length ?? 0}.",
                    nameof(pixels));
            }

            byte[] header = new byte[13];
            WriteBigEndian(header, 0, (uint)width);
            WriteBigEndian(header, 4, (uint)height);
            header[8] = 8;
            header[9] = channels == 4 ? (byte)6 : (byte)2;

            using MemoryStream png = new MemoryStream();
            png.Write(Signature, 0, Signature.Length);
            WriteChunk(png, "IHDR", header);
            WriteChunk(png, "sRGB", new byte[] { 0 });
            WriteChunk(png, "IDAT", Compress(pixels, height, width * channels));
            WriteChunk(png, "IEND", Array.Empty<byte>());
            return png.ToArray();
        }

        // zlib stream (RFC 1950) of the filtered rows: a filter-type byte of 0 before each row.
        private static byte[] Compress(byte[] pixels, int height, int stride)
        {
            using MemoryStream output = new MemoryStream();
            output.WriteByte(0x78);
            output.WriteByte(0x9C);
            uint a = 1;
            uint b = 0;
            using (DeflateStream deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                for (int row = 0; row < height; row++)
                {
                    deflate.WriteByte(0);
                    b = (b + a) % 65521;
                    int offset = row * stride;
                    deflate.Write(pixels, offset, stride);
                    for (int i = offset; i < offset + stride; i++)
                    {
                        a = (a + pixels[i]) % 65521;
                        b = (b + a) % 65521;
                    }
                }
            }

            byte[] adler = new byte[4];
            WriteBigEndian(adler, 0, (b << 16) | a);
            output.Write(adler, 0, adler.Length);
            return output.ToArray();
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            byte[] buffer = new byte[12 + data.Length];
            WriteBigEndian(buffer, 0, (uint)data.Length);
            for (int i = 0; i < 4; i++)
            {
                buffer[4 + i] = (byte)type[i];
            }

            Buffer.BlockCopy(data, 0, buffer, 8, data.Length);
            uint crc = 0xFFFFFFFFu;
            for (int i = 4; i < 8 + data.Length; i++)
            {
                crc = CrcTable[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
            }

            WriteBigEndian(buffer, 8 + data.Length, crc ^ 0xFFFFFFFFu);
            stream.Write(buffer, 0, buffer.Length);
        }

        private static void WriteBigEndian(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private static uint[] BuildCrcTable()
        {
            uint[] table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }
    }
}
