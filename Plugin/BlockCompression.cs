using System;

namespace TGYHTurkishPatch
{
    /// <summary>
    /// BC1 (DXT1) / BC3 (DXT5) block encoder, modelled on stb_dxt: principal-axis endpoints
    /// plus one least-squares refinement step. Runs on worker threads only.
    /// </summary>
    internal static unsafe class BlockCompression
    {
        private static readonly byte[] Expand5 = new byte[32];
        private static readonly byte[] Expand6 = new byte[64];
        // For each 8-bit value: the (max, min) endpoint pair whose 2/3 blend lands closest to it.
        private static readonly byte[] SingleMatch5 = new byte[512];
        private static readonly byte[] SingleMatch6 = new byte[512];

        static BlockCompression()
        {
            for (int i = 0; i < 32; i++) Expand5[i] = (byte)((i << 3) | (i >> 2));
            for (int i = 0; i < 64; i++) Expand6[i] = (byte)((i << 2) | (i >> 4));
            BuildSingleMatchTable(SingleMatch5, Expand5);
            BuildSingleMatchTable(SingleMatch6, Expand6);
        }

        private static void BuildSingleMatchTable(byte[] table, byte[] expand)
        {
            for (int value = 0; value < 256; value++)
            {
                int bestError = int.MaxValue;
                for (int min = 0; min < expand.Length; min++)
                {
                    for (int max = 0; max < expand.Length; max++)
                    {
                        int minE = expand[min], maxE = expand[max];
                        int error = Math.Abs((2 * maxE + minE) / 3 - value) * 100 + Math.Abs(maxE - minE) * 3;
                        if (error < bestError)
                        {
                            table[value * 2] = (byte)max;
                            table[value * 2 + 1] = (byte)min;
                            bestError = error;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Encodes one row of 4x4 blocks. <paramref name="rows"/> holds four RGBA scanlines of
        /// <paramref name="width"/> pixels each, in texture order; <paramref name="dst"/> receives
        /// width/4 blocks of 16 bytes (BC3) or 8 bytes (BC1).
        /// </summary>
        public static void EncodeBlockRow(byte* rows, int width, byte* dst, bool bc3)
        {
            uint* block = stackalloc uint[16];
            int rowBytes = width * 4;
            for (int x = 0; x < width; x += 4)
            {
                uint* src = (uint*)(rows + x * 4);
                for (int y = 0; y < 4; y++)
                {
                    block[y * 4 + 0] = src[0];
                    block[y * 4 + 1] = src[1];
                    block[y * 4 + 2] = src[2];
                    block[y * 4 + 3] = src[3];
                    src = (uint*)((byte*)src + rowBytes);
                }

                if (bc3)
                {
                    EncodeAlpha((byte*)block, dst);
                    dst += 8;
                }
                EncodeColor(block, dst);
                dst += 8;
            }
        }

        private static void EncodeAlpha(byte* pixels, byte* dst)
        {
            int min = 255, max = 0;
            for (int i = 0; i < 16; i++)
            {
                int a = pixels[i * 4 + 3];
                if (a < min) min = a;
                if (a > max) max = a;
            }

            dst[0] = (byte)max;
            dst[1] = (byte)min;
            ulong bits = 0;
            if (max != min)
            {
                // alpha0 > alpha1 selects the 8-step ramp: indices 0, 2..7, 1 run from max to min.
                int range = max - min;
                for (int i = 0; i < 16; i++)
                {
                    int step = ((max - pixels[i * 4 + 3]) * 7 + (range >> 1)) / range;
                    int index = step == 0 ? 0 : (step == 7 ? 1 : step + 1);
                    bits |= (ulong)index << (3 * i);
                }
            }
            for (int i = 0; i < 6; i++) dst[2 + i] = (byte)(bits >> (8 * i));
        }

        private static void EncodeColor(uint* block, byte* dst)
        {
            byte* pixels = (byte*)block;
            int c0, c1;
            uint mask;

            uint first = block[0] & 0x00FFFFFF;
            bool uniform = true;
            for (int i = 1; i < 16; i++)
            {
                if ((block[i] & 0x00FFFFFF) != first) { uniform = false; break; }
            }

            if (uniform)
            {
                int r = pixels[0], g = pixels[1], b = pixels[2];
                c0 = (SingleMatch5[r * 2] << 11) | (SingleMatch6[g * 2] << 5) | SingleMatch5[b * 2];
                c1 = (SingleMatch5[r * 2 + 1] << 11) | (SingleMatch6[g * 2 + 1] << 5) | SingleMatch5[b * 2 + 1];
                mask = 0xAAAAAAAA;
            }
            else
            {
                PickEndpoints(pixels, out c0, out c1);
                if (c0 != c1)
                {
                    mask = MatchColors(pixels, c0, c1, out int error);
                    if (error != 0 && Refine(pixels, mask, out int r0, out int r1) && r0 != r1)
                    {
                        uint refinedMask = MatchColors(pixels, r0, r1, out int refinedError);
                        if (refinedError < error)
                        {
                            c0 = r0;
                            c1 = r1;
                            mask = refinedMask;
                        }
                    }
                }
                else
                {
                    mask = 0;
                }
            }

            // Keep colour0 >= colour1 so the block is always in four-colour mode (required for BC1).
            if (c0 < c1)
            {
                int t = c0; c0 = c1; c1 = t;
                mask ^= 0x55555555;
            }

            dst[0] = (byte)c0;
            dst[1] = (byte)(c0 >> 8);
            dst[2] = (byte)c1;
            dst[3] = (byte)(c1 >> 8);
            dst[4] = (byte)mask;
            dst[5] = (byte)(mask >> 8);
            dst[6] = (byte)(mask >> 16);
            dst[7] = (byte)(mask >> 24);
        }

        private static int Scale8(int value, int max)
        {
            int t = value * max + 128;
            return (t + (t >> 8)) >> 8;
        }

        private static int To565(int r, int g, int b)
        {
            return (Scale8(r, 31) << 11) | (Scale8(g, 63) << 5) | Scale8(b, 31);
        }

        /// <summary>Endpoints = the two pixels furthest apart along the block's principal axis.</summary>
        private static void PickEndpoints(byte* p, out int max565, out int min565)
        {
            int sumR = 0, sumG = 0, sumB = 0;
            int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
            for (int i = 0; i < 16; i++)
            {
                int r = p[i * 4], g = p[i * 4 + 1], b = p[i * 4 + 2];
                sumR += r; sumG += g; sumB += b;
                if (r < minR) minR = r; if (r > maxR) maxR = r;
                if (g < minG) minG = g; if (g > maxG) maxG = g;
                if (b < minB) minB = b; if (b > maxB) maxB = b;
            }
            int muR = (sumR + 8) >> 4, muG = (sumG + 8) >> 4, muB = (sumB + 8) >> 4;

            int covRR = 0, covRG = 0, covRB = 0, covGG = 0, covGB = 0, covBB = 0;
            for (int i = 0; i < 16; i++)
            {
                int r = p[i * 4] - muR, g = p[i * 4 + 1] - muG, b = p[i * 4 + 2] - muB;
                covRR += r * r; covRG += r * g; covRB += r * b;
                covGG += g * g; covGB += g * b; covBB += b * b;
            }

            float fRR = covRR / 255f, fRG = covRG / 255f, fRB = covRB / 255f;
            float fGG = covGG / 255f, fGB = covGB / 255f, fBB = covBB / 255f;
            float vr = maxR - minR, vg = maxG - minG, vb = maxB - minB;
            for (int iter = 0; iter < 4; iter++)
            {
                float r = vr * fRR + vg * fRG + vb * fRB;
                float g = vr * fRG + vg * fGG + vb * fGB;
                float b = vr * fRB + vg * fGB + vb * fBB;
                vr = r; vg = g; vb = b;
            }

            float magnitude = Math.Max(Math.Abs(vr), Math.Max(Math.Abs(vg), Math.Abs(vb)));
            int axisR, axisG, axisB;
            if (magnitude < 4f)
            {
                axisR = 299; axisG = 587; axisB = 114;
            }
            else
            {
                float scale = 512f / magnitude;
                axisR = (int)(vr * scale);
                axisG = (int)(vg * scale);
                axisB = (int)(vb * scale);
            }

            int minDot = int.MaxValue, maxDot = int.MinValue, minIndex = 0, maxIndex = 0;
            for (int i = 0; i < 16; i++)
            {
                int dot = p[i * 4] * axisR + p[i * 4 + 1] * axisG + p[i * 4 + 2] * axisB;
                if (dot < minDot) { minDot = dot; minIndex = i; }
                if (dot > maxDot) { maxDot = dot; maxIndex = i; }
            }

            max565 = To565(p[maxIndex * 4], p[maxIndex * 4 + 1], p[maxIndex * 4 + 2]);
            min565 = To565(p[minIndex * 4], p[minIndex * 4 + 1], p[minIndex * 4 + 2]);
        }

        /// <summary>Assigns each pixel its nearest palette entry; returns the 2-bit index mask.</summary>
        private static uint MatchColors(byte* p, int c0, int c1, out int error)
        {
            int r0 = Expand5[c0 >> 11], g0 = Expand6[(c0 >> 5) & 63], b0 = Expand5[c0 & 31];
            int r1 = Expand5[c1 >> 11], g1 = Expand6[(c1 >> 5) & 63], b1 = Expand5[c1 & 31];
            int r2 = (2 * r0 + r1) / 3, g2 = (2 * g0 + g1) / 3, b2 = (2 * b0 + b1) / 3;
            int r3 = (r0 + 2 * r1) / 3, g3 = (g0 + 2 * g1) / 3, b3 = (b0 + 2 * b1) / 3;

            uint mask = 0;
            int total = 0;
            for (int i = 0; i < 16; i++)
            {
                int r = p[i * 4], g = p[i * 4 + 1], b = p[i * 4 + 2];
                int d0 = (r - r0) * (r - r0) + (g - g0) * (g - g0) + (b - b0) * (b - b0);
                int d1 = (r - r1) * (r - r1) + (g - g1) * (g - g1) + (b - b1) * (b - b1);
                int d2 = (r - r2) * (r - r2) + (g - g2) * (g - g2) + (b - b2) * (b - b2);
                int d3 = (r - r3) * (r - r3) + (g - g3) * (g - g3) + (b - b3) * (b - b3);

                int best = d0;
                uint index = 0;
                if (d1 < best) { best = d1; index = 1; }
                if (d2 < best) { best = d2; index = 2; }
                if (d3 < best) { best = d3; index = 3; }
                mask |= index << (2 * i);
                total += best;
            }
            error = total;
            return mask;
        }

        /// <summary>Least-squares fit of the endpoints for a fixed index assignment.</summary>
        private static bool Refine(byte* p, uint mask, out int max565, out int min565)
        {
            max565 = 0;
            min565 = 0;

            int akku = 0;
            int at1R = 0, at1G = 0, at1B = 0, at2R = 0, at2G = 0, at2B = 0;
            for (int i = 0; i < 16; i++)
            {
                int step = (int)(mask & 3);
                mask >>= 2;
                int w1;
                switch (step)
                {
                    case 0: w1 = 3; akku += 0x090000; break;
                    case 1: w1 = 0; akku += 0x000900; break;
                    case 2: w1 = 2; akku += 0x040102; break;
                    default: w1 = 1; akku += 0x010402; break;
                }
                int r = p[i * 4], g = p[i * 4 + 1], b = p[i * 4 + 2];
                at1R += w1 * r; at1G += w1 * g; at1B += w1 * b;
                at2R += r; at2G += g; at2B += b;
            }
            at2R = 3 * at2R - at1R;
            at2G = 3 * at2G - at1G;
            at2B = 3 * at2B - at1B;

            int xx = akku >> 16, yy = (akku >> 8) & 0xFF, xy = akku & 0xFF;
            int det = xx * yy - xy * xy;
            if (det == 0) return false;

            float f5 = 3f * 31f / 255f / det;
            float f6 = 3f * 63f / 255f / det;
            max565 = (Clamp((at1R * yy - at2R * xy) * f5, 31) << 11)
                   | (Clamp((at1G * yy - at2G * xy) * f6, 63) << 5)
                   | Clamp((at1B * yy - at2B * xy) * f5, 31);
            min565 = (Clamp((at2R * xx - at1R * xy) * f5, 31) << 11)
                   | (Clamp((at2G * xx - at1G * xy) * f6, 63) << 5)
                   | Clamp((at2B * xx - at1B * xy) * f5, 31);
            return true;
        }

        private static int Clamp(float value, int max)
        {
            int v = (int)(value + 0.5f);
            return v < 0 ? 0 : (v > max ? max : v);
        }
    }
}
