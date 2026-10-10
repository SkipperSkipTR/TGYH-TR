using System;
using System.IO;
using System.IO.Compression;

namespace TGYHTurkishPatch
{
    /// <summary>
    /// Streaming PNG reader: hands out one RGBA32 row at a time (top row first), so a
    /// 4096x4096 PNG never needs more than two scanlines of scratch memory. That matters
    /// in this 32-bit process, where decoding whole images at once runs out of address space.
    /// Handles 8-bit non-interlaced PNGs; anything else reports <see cref="Supported"/> = false.
    /// </summary>
    internal sealed unsafe class PngRowReader : IDisposable
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public bool Supported { get; private set; }

        private readonly FileStream _file;
        private Stream _inflate;
        private byte _colorType;
        private int _bytesPerPixel;
        private int _stride;
        private byte[] _current;    // [filter byte][scanline]
        private byte[] _previous;
        private byte[] _palette;    // RGBA per entry
        private int _idatRemaining;
        private bool _idatEnded;

        public PngRowReader(string path)
        {
            _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            try
            {
                ReadHeader();
            }
            catch
            {
                _file.Dispose();
                throw;
            }
        }

        private void ReadHeader()
        {
            var head = new byte[8];
            ReadExact(head, 8);
            if (head[0] != 0x89 || head[1] != 'P' || head[2] != 'N' || head[3] != 'G')
                throw new InvalidDataException("not a PNG file");

            byte bitDepth = 0, interlace = 0;
            bool gotHeader = false;
            byte[] transparency = null;
            byte[] paletteRgb = null;

            while (true)
            {
                ReadExact(head, 8);
                int length = ReadInt32BigEndian(head, 0);
                if (length < 0) throw new InvalidDataException("corrupt PNG chunk");
                uint type = (uint)ReadInt32BigEndian(head, 4);

                if (type == 0x49484452) // IHDR
                {
                    var ihdr = new byte[13];
                    ReadExact(ihdr, 13);
                    Width = ReadInt32BigEndian(ihdr, 0);
                    Height = ReadInt32BigEndian(ihdr, 4);
                    bitDepth = ihdr[8];
                    _colorType = ihdr[9];
                    interlace = ihdr[12];
                    gotHeader = true;
                    _file.Seek(length - 13 + 4, SeekOrigin.Current);
                }
                else if (type == 0x504C5445) // PLTE
                {
                    paletteRgb = new byte[length];
                    ReadExact(paletteRgb, length);
                    _file.Seek(4, SeekOrigin.Current);
                }
                else if (type == 0x74524E53) // tRNS
                {
                    transparency = new byte[length];
                    ReadExact(transparency, length);
                    _file.Seek(4, SeekOrigin.Current);
                }
                else if (type == 0x49444154) // IDAT
                {
                    _idatRemaining = length;
                    break;
                }
                else if (type == 0x49454E44) // IEND
                {
                    throw new InvalidDataException("PNG has no image data");
                }
                else
                {
                    _file.Seek((long)length + 4, SeekOrigin.Current);
                }
            }

            if (!gotHeader || Width <= 0 || Height <= 0)
                throw new InvalidDataException("PNG header missing");

            switch (_colorType)
            {
                case 0: _bytesPerPixel = 1; break;
                case 2: _bytesPerPixel = 3; break;
                case 3: _bytesPerPixel = 1; break;
                case 4: _bytesPerPixel = 2; break;
                case 6: _bytesPerPixel = 4; break;
                default: return;
            }

            // Colour-keyed transparency on grey/RGB images is rare enough to leave to the fallback.
            if (bitDepth != 8 || interlace != 0) return;
            if (transparency != null && _colorType != 3) return;
            if (_colorType == 3)
            {
                if (paletteRgb == null) return;
                _palette = new byte[256 * 4];
                for (int i = 0; i < 256; i++)
                {
                    bool has = i * 3 + 2 < paletteRgb.Length;
                    _palette[i * 4 + 0] = has ? paletteRgb[i * 3 + 0] : (byte)0;
                    _palette[i * 4 + 1] = has ? paletteRgb[i * 3 + 1] : (byte)0;
                    _palette[i * 4 + 2] = has ? paletteRgb[i * 3 + 2] : (byte)0;
                    _palette[i * 4 + 3] = (transparency != null && i < transparency.Length) ? transparency[i] : (byte)255;
                }
            }

            _stride = Width * _bytesPerPixel;
            _current = new byte[_stride + 1];
            _previous = new byte[_stride + 1];

            var idat = new IdatStream(this);
            // Skip the 2-byte zlib header; the rest is a raw deflate stream.
            if (idat.ReadByte() < 0 || idat.ReadByte() < 0)
                throw new InvalidDataException("PNG image data truncated");
            _inflate = new DeflateStream(idat, CompressionMode.Decompress);
            Supported = true;
        }

        /// <summary>Decodes the next scanline into <paramref name="rgba"/> (Width * 4 bytes).</summary>
        public void ReadRow(byte* rgba)
        {
            int need = _stride + 1, got = 0;
            while (got < need)
            {
                int n = _inflate.Read(_current, got, need - got);
                if (n <= 0) throw new InvalidDataException("PNG image data truncated");
                got += n;
            }

            fixed (byte* curBase = _current, prevBase = _previous)
            {
                byte* cur = curBase + 1, prev = prevBase + 1;
                int bpp = _bytesPerPixel, stride = _stride;
                switch (curBase[0])
                {
                    case 0:
                        break;
                    case 1:
                        for (int i = bpp; i < stride; i++) cur[i] = (byte)(cur[i] + cur[i - bpp]);
                        break;
                    case 2:
                        for (int i = 0; i < stride; i++) cur[i] = (byte)(cur[i] + prev[i]);
                        break;
                    case 3:
                        for (int i = 0; i < bpp; i++) cur[i] = (byte)(cur[i] + (prev[i] >> 1));
                        for (int i = bpp; i < stride; i++) cur[i] = (byte)(cur[i] + ((cur[i - bpp] + prev[i]) >> 1));
                        break;
                    case 4:
                        for (int i = 0; i < bpp; i++) cur[i] = (byte)(cur[i] + prev[i]);
                        for (int i = bpp; i < stride; i++)
                        {
                            int a = cur[i - bpp], b = prev[i], c = prev[i - bpp];
                            int p = a + b - c;
                            int pa = p > a ? p - a : a - p;
                            int pb = p > b ? p - b : b - p;
                            int pc = p > c ? p - c : c - p;
                            int predictor = (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c);
                            cur[i] = (byte)(cur[i] + predictor);
                        }
                        break;
                    default:
                        throw new InvalidDataException("PNG uses an unknown row filter");
                }

                int w = Width;
                switch (_colorType)
                {
                    case 6:
                        Buffer.MemoryCopy(cur, rgba, stride, stride);
                        break;
                    case 2:
                        for (int x = 0; x < w; x++)
                        {
                            rgba[0] = cur[0]; rgba[1] = cur[1]; rgba[2] = cur[2]; rgba[3] = 255;
                            rgba += 4; cur += 3;
                        }
                        break;
                    case 0:
                        for (int x = 0; x < w; x++)
                        {
                            byte v = cur[x];
                            rgba[0] = v; rgba[1] = v; rgba[2] = v; rgba[3] = 255;
                            rgba += 4;
                        }
                        break;
                    case 4:
                        for (int x = 0; x < w; x++)
                        {
                            byte v = cur[x * 2];
                            rgba[0] = v; rgba[1] = v; rgba[2] = v; rgba[3] = cur[x * 2 + 1];
                            rgba += 4;
                        }
                        break;
                    case 3:
                        fixed (byte* pal = _palette)
                        {
                            for (int x = 0; x < w; x++)
                            {
                                *(uint*)rgba = *(uint*)(pal + cur[x] * 4);
                                rgba += 4;
                            }
                        }
                        break;
                }
            }

            var swap = _previous;
            _previous = _current;
            _current = swap;
        }

        private void ReadExact(byte[] buffer, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = _file.Read(buffer, got, count - got);
                if (n <= 0) throw new InvalidDataException("PNG file truncated");
                got += n;
            }
        }

        private static int ReadInt32BigEndian(byte[] b, int o)
        {
            return (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
        }

        public void Dispose()
        {
            _inflate?.Dispose();
            _file.Dispose();
        }

        /// <summary>Presents the payloads of consecutive IDAT chunks as one stream.</summary>
        private sealed class IdatStream : Stream
        {
            private readonly PngRowReader _owner;
            private readonly byte[] _head = new byte[12];

            public IdatStream(PngRowReader owner) { _owner = owner; }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var o = _owner;
                if (o._idatEnded || count == 0) return 0;

                while (o._idatRemaining == 0)
                {
                    // CRC of the finished chunk, then the next chunk's length + type.
                    int got = 0;
                    while (got < 12)
                    {
                        int n = o._file.Read(_head, got, 12 - got);
                        if (n <= 0) { o._idatEnded = true; return 0; }
                        got += n;
                    }
                    if (_head[8] != 'I' || _head[9] != 'D' || _head[10] != 'A' || _head[11] != 'T')
                    {
                        o._idatEnded = true;
                        return 0;
                    }
                    o._idatRemaining = ReadInt32BigEndian(_head, 4);
                    if (o._idatRemaining < 0) { o._idatEnded = true; return 0; }
                }

                int read = o._file.Read(buffer, offset, Math.Min(count, o._idatRemaining));
                if (read <= 0) { o._idatEnded = true; return 0; }
                o._idatRemaining -= read;
                return read;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
