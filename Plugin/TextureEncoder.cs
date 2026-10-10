using System;

namespace TGYHTurkishPatch
{
    /// <summary>GPU layouts the encoder can produce (values are stored in cache files — don't renumber).</summary>
    internal enum RawFormat
    {
        None = 0,
        RGBA32 = 1,
        ARGB32 = 2,
        BGRA32 = 3,
        RGB24 = 4,
        RGB565 = 5,
        Alpha8 = 6,
        BC1 = 7,
        BC3 = 8,
    }

    /// <summary>
    /// Turns a PNG on disk into the exact bytes Unity expects from LoadRawTextureData:
    /// bottom row first, single mip, in the target texture's own format. No Unity types here,
    /// so it is safe to call from worker threads.
    /// </summary>
    internal static unsafe class TextureEncoder
    {
        public static int GetDataSize(RawFormat format, int width, int height)
        {
            switch (format)
            {
                case RawFormat.RGBA32:
                case RawFormat.ARGB32:
                case RawFormat.BGRA32: return width * height * 4;
                case RawFormat.RGB24: return width * height * 3;
                case RawFormat.RGB565: return width * height * 2;
                case RawFormat.Alpha8: return width * height;
                case RawFormat.BC1: return (width / 4) * (height / 4) * 8;
                case RawFormat.BC3: return (width / 4) * (height / 4) * 16;
                default: return 0;
            }
        }

        /// <returns>false (with a reason) when the PNG has to go through Unity's own loader instead.</returns>
        public static bool TryEncode(string path, int width, int height, RawFormat format, out byte[] data, out string reason)
        {
            data = null;
            reason = null;

            using (var png = new PngRowReader(path))
            {
                if (!png.Supported)
                {
                    reason = "PNG is not 8-bit non-interlaced";
                    return false;
                }
                if (png.Width != width || png.Height != height)
                {
                    reason = $"PNG is {png.Width}x{png.Height} but the texture is {width}x{height}";
                    return false;
                }

                bool blocks = format == RawFormat.BC1 || format == RawFormat.BC3;
                if (blocks && ((width & 3) != 0 || (height & 3) != 0))
                {
                    reason = "block-compressed texture size is not a multiple of 4";
                    return false;
                }

                int size = GetDataSize(format, width, height);
                if (size <= 0)
                {
                    reason = "unsupported texture format";
                    return false;
                }

                var output = new byte[size];
                int rowBytes = width * 4;
                // Block formats buffer four scanlines; everything else converts one at a time.
                var scratch = new byte[blocks ? rowBytes * 4 : rowBytes];

                fixed (byte* dst = output, rows = scratch)
                {
                    if (blocks)
                    {
                        bool bc3 = format == RawFormat.BC3;
                        int blockRowBytes = (width / 4) * (bc3 ? 16 : 8);
                        int blockRows = height / 4;
                        for (int y = 0; y < height; y++)
                        {
                            // PNG rows arrive top-down, Unity stores bottom-up: flip inside the
                            // block as well as the order of the block rows.
                            png.ReadRow(rows + (3 - (y & 3)) * rowBytes);
                            if ((y & 3) == 3)
                                BlockCompression.EncodeBlockRow(rows, width, dst + (blockRows - 1 - (y >> 2)) * blockRowBytes, bc3);
                        }
                    }
                    else if (format == RawFormat.RGBA32)
                    {
                        for (int y = 0; y < height; y++)
                            png.ReadRow(dst + (height - 1 - y) * rowBytes);
                    }
                    else
                    {
                        int outRowBytes = size / height;
                        for (int y = 0; y < height; y++)
                        {
                            png.ReadRow(rows);
                            ConvertRow(rows, dst + (height - 1 - y) * outRowBytes, width, format);
                        }
                    }
                }

                data = output;
                return true;
            }
        }

        private static void ConvertRow(byte* src, byte* dst, int width, RawFormat format)
        {
            switch (format)
            {
                case RawFormat.ARGB32:
                    for (int x = 0; x < width; x++, src += 4, dst += 4)
                    {
                        dst[0] = src[3]; dst[1] = src[0]; dst[2] = src[1]; dst[3] = src[2];
                    }
                    break;
                case RawFormat.BGRA32:
                    for (int x = 0; x < width; x++, src += 4, dst += 4)
                    {
                        dst[0] = src[2]; dst[1] = src[1]; dst[2] = src[0]; dst[3] = src[3];
                    }
                    break;
                case RawFormat.RGB24:
                    for (int x = 0; x < width; x++, src += 4, dst += 3)
                    {
                        dst[0] = src[0]; dst[1] = src[1]; dst[2] = src[2];
                    }
                    break;
                case RawFormat.RGB565:
                    for (int x = 0; x < width; x++, src += 4, dst += 2)
                    {
                        int v = ((src[0] * 31 + 127) / 255 << 11) | ((src[1] * 63 + 127) / 255 << 5) | ((src[2] * 31 + 127) / 255);
                        dst[0] = (byte)v;
                        dst[1] = (byte)(v >> 8);
                    }
                    break;
                case RawFormat.Alpha8:
                    for (int x = 0; x < width; x++, src += 4, dst += 1)
                        dst[0] = src[3];
                    break;
            }
        }
    }
}
