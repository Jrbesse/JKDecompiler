using System.IO.Compression;
using System.Text;

namespace JKDecompiler.Core
{
    /// <summary>
    /// Minimal PNG encoder (8-bit RGB, no palette/alpha/interlace). Written
    /// to avoid an image-library dependency: this tool only WRITES PNGs
    /// (lightmap blocks, face crops) and never decodes untrusted images.
    /// </summary>
    public static class PngWriter
    {
        public static void SaveRgb(byte[] rgb, int width, int height, string path)
        {
            if (rgb.Length != width * height * 3)
                throw new ArgumentException($"rgb length {rgb.Length} != {width}x{height}x3");

            var raw = new byte[height * (1 + width * 3)];
            for (int y = 0; y < height; y++)
            {
                raw[y * (1 + width * 3)] = 0; // filter: none
                Array.Copy(rgb, y * width * 3, raw, y * (1 + width * 3) + 1, width * 3);
            }

            using var fs = File.Create(path);
            fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

            var ihdr = new byte[13];
            WriteBE32(ihdr, 0, width);
            WriteBE32(ihdr, 4, height);
            ihdr[8] = 8;  // bit depth
            ihdr[9] = 2;  // color type: truecolor
            ihdr[10] = 0; // compression
            ihdr[11] = 0; // filter
            ihdr[12] = 0; // interlace
            WriteChunk(fs, "IHDR", ihdr);

            using (var idat = new MemoryStream())
            {
                using (var z = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
                {
                    z.Write(raw, 0, raw.Length);
                }
                WriteChunk(fs, "IDAT", idat.ToArray());
            }
            WriteChunk(fs, "IEND", Array.Empty<byte>());
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4];
            WriteBE32(len, 0, data.Length);
            s.Write(len);
            var name = Encoding.ASCII.GetBytes(type);
            s.Write(name);
            s.Write(data);
            var crc = new byte[4];
            WriteBE32(crc, 0, Crc32(name, data));
            s.Write(crc);
        }

        private static void WriteBE32(byte[] b, int off, int v)
        {
            b[off] = (byte)(v >> 24);
            b[off + 1] = (byte)(v >> 16);
            b[off + 2] = (byte)(v >> 8);
            b[off + 3] = (byte)v;
        }

        private static int Crc32(byte[] name, byte[] data)
        {
            uint crc = 0xFFFFFFFFu;
            void Update(byte[] b)
            {
                foreach (var x in b)
                {
                    crc ^= x;
                    for (int i = 0; i < 8; i++)
                        crc = (crc >> 1) ^ (0xEDB88320u & (uint)(-(int)(crc & 1)));
                }
            }
            Update(name);
            Update(data);
            return (int)(~crc);
        }
    }
}
