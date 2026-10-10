using System;
using System.IO;
using System.IO.Compression;

namespace TGYHTurkishPatch
{
    /// <summary>
    /// The .tgyc container: a 40-byte header followed by texture bytes ready for
    /// LoadRawTextureData, either raw (local cache) or deflate-compressed (prebuilt packs).
    /// </summary>
    internal static class TextureCacheFile
    {
        public const string Extension = ".tgyc";

        private const int Magic = 0x43594754; // "TGYC"
        private const int Version = 1;
        private const int HeaderSize = 40;

        /// <summary>
        /// Loads a file if it holds exactly the requested size and format. For cache entries
        /// pass the source PNG's length and timestamp; prebuilt files (<paramref name="checkSource"/>
        /// = false) have no source to compare against.
        /// </summary>
        /// <param name="mismatch">Set when the file is valid but describes a different texture.</param>
        public static bool TryRead(string file, bool checkSource, long sourceLength, long sourceTicks,
            int width, int height, RawFormat format, out byte[] data, out string mismatch)
        {
            data = null;
            mismatch = null;

            int size = TextureEncoder.GetDataSize(format, width, height);
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
            {
                var header = new byte[HeaderSize];
                if (!ReadExact(fs, header, HeaderSize)) return false;
                if (BitConverter.ToInt32(header, 0) != Magic || BitConverter.ToInt32(header, 4) != Version) return false;
                if (checkSource &&
                    (BitConverter.ToInt64(header, 8) != sourceLength || BitConverter.ToInt64(header, 16) != sourceTicks))
                    return false;

                int fileWidth = BitConverter.ToInt32(header, 24), fileHeight = BitConverter.ToInt32(header, 28);
                var fileFormat = (RawFormat)BitConverter.ToInt32(header, 32);
                if (fileWidth != width || fileHeight != height || fileFormat != format ||
                    BitConverter.ToInt32(header, 36) != size)
                {
                    mismatch = $"file holds {fileWidth}x{fileHeight} {fileFormat}, texture needs {width}x{height} {format}";
                    return false;
                }

                var buffer = new byte[size];
                if (fs.Length == HeaderSize + size)
                {
                    if (!ReadExact(fs, buffer, size)) return false;
                }
                else
                {
                    using (var inflate = new DeflateStream(fs, CompressionMode.Decompress))
                        if (!ReadExact(inflate, buffer, size)) return false;
                }
                data = buffer;
                return true;
            }
        }

        public static void Write(string file, long sourceLength, long sourceTicks,
            int width, int height, RawFormat format, byte[] raw, bool compress)
        {
            using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var header = new byte[HeaderSize];
                WriteInt(header, 0, Magic);
                WriteInt(header, 4, Version);
                WriteLong(header, 8, sourceLength);
                WriteLong(header, 16, sourceTicks);
                WriteInt(header, 24, width);
                WriteInt(header, 28, height);
                WriteInt(header, 32, (int)format);
                WriteInt(header, 36, raw.Length);
                fs.Write(header, 0, header.Length);

                if (compress)
                {
                    using (var deflate = new DeflateStream(fs, CompressionLevel.Optimal, true))
                        deflate.Write(raw, 0, raw.Length);
                }
                else
                {
                    fs.Write(raw, 0, raw.Length);
                }
            }
        }

        private static bool ReadExact(Stream stream, byte[] buffer, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = stream.Read(buffer, got, count - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        private static void WriteInt(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteLong(byte[] buffer, int offset, long value)
        {
            WriteInt(buffer, offset, (int)value);
            WriteInt(buffer, offset + 4, (int)(value >> 32));
        }
    }
}
