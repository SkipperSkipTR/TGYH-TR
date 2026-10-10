using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace TGYHTurkishPatch
{
    /// <summary>
    /// Recognises a sprite atlas by its picture instead of its name.
    ///
    /// Atlases are named "sactx-...-&lt;8 hex digits&gt;", and that hash differs between builds of
    /// the game (Windows, macOS, other stores, later versions) even when the atlas is identical.
    /// signatures.txt carries a small fingerprint of the original atlas each replacement was
    /// made for. When the game loads an atlas whose name matches a replacement except for the
    /// hash, the same fingerprint is taken from the loaded texture, and the replacement is used
    /// only if the two agree — so a repacked atlas with a different layout is left alone.
    /// The fingerprint format is defined in tools/build_texture_pack.py.
    /// </summary>
    internal static class TextureFingerprint
    {
        private const int Grid = 32;
        private const int Length = Grid * Grid * 2;     // alpha plane, then green plane
        // Mean per-sample difference (0-255) still counted as the same picture. Identical atlases
        // measure well under 1; different layouts of the same atlas measure 20 and up.
        private const double MaxDistance = 4.0;

        private sealed class Candidate
        {
            public string Key;
            public byte[] Signature;
        }

        private static readonly Dictionary<string, List<Candidate>> _byStem =
            new Dictionary<string, List<Candidate>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, byte[]> _byKey =
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        public static int Count => _byKey.Count;

        /// <summary>"sactx-…-Name-1a2b3c4d" -> "sactx-…-Name"; null for anything that isn't an atlas name.</summary>
        private static string Stem(string name)
        {
            if (name.Length < 16 || !name.StartsWith("sactx-", StringComparison.OrdinalIgnoreCase)) return null;
            int dash = name.Length - 9;
            if (name[dash] != '-') return null;
            for (int i = dash + 1; i < name.Length; i++)
            {
                char c = name[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return null;
            }
            return name.Substring(0, dash);
        }

        /// <summary>Reads signatures.txt; <paramref name="isKnownKey"/> filters out entries without a texture file.</summary>
        public static void Load(string file, Func<string, bool> isKnownKey)
        {
            _byStem.Clear();
            _byKey.Clear();
            if (!File.Exists(file)) return;

            try
            {
                foreach (var raw in File.ReadAllLines(file))
                {
                    int tab = raw.IndexOf('\t');
                    if (raw.Length == 0 || raw[0] == '#' || tab <= 0) continue;

                    string key = TextureManager.NormalizeName(raw.Substring(0, tab));
                    string stem = Stem(key);
                    if (stem == null || !isKnownKey(key)) continue;

                    byte[] signature = Convert.FromBase64String(raw.Substring(tab + 1).Trim());
                    if (signature.Length != Length) continue;

                    _byKey[key] = signature;
                    if (!_byStem.TryGetValue(stem, out var list))
                        _byStem[stem] = list = new List<Candidate>();
                    list.Add(new Candidate { Key = key, Signature = signature });
                }
            }
            catch (Exception ex)
            {
                LocalizationPlugin.Log.LogWarning($"[TEXTURE] Could not read signatures.txt: {ex.Message}");
            }
        }

        /// <summary>
        /// For a texture with no replacement under its own name: the key of the replacement made
        /// for the same atlas under another hash, or null. Must be called before the texture's
        /// pixels are replaced. Main thread only.
        /// </summary>
        public static string FindMatch(Texture2D tex, string normalizedName, out double distance)
        {
            distance = double.NaN;
            string stem = Stem(normalizedName);
            if (stem == null || !_byStem.TryGetValue(stem, out var candidates)) return null;

            byte[] actual = Compute(tex);
            if (actual == null) return null;

            string best = null;
            double bestDistance = double.MaxValue;
            foreach (var candidate in candidates)
            {
                double d = Distance(actual, candidate.Signature);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = candidate.Key;
                }
            }
            distance = bestDistance;
            return bestDistance <= MaxDistance ? best : null;
        }

        /// <summary>Distance between a loaded texture and the fingerprint stored for <paramref name="key"/> (diagnostics).</summary>
        public static bool TryMeasure(Texture2D tex, string key, out double distance)
        {
            distance = double.NaN;
            if (!_byKey.TryGetValue(key, out var expected)) return false;
            byte[] actual = Compute(tex);
            if (actual == null) return false;
            distance = Distance(actual, expected);
            return true;
        }

        /// <summary>
        /// Mean absolute difference. Which way up a GPU read-back arrives depends on the graphics
        /// API, so the upside-down comparison is accepted as well.
        /// </summary>
        private static double Distance(byte[] actual, byte[] expected)
        {
            long upright = 0, flipped = 0;
            for (int plane = 0; plane < 2; plane++)
            {
                int offset = plane * Grid * Grid;
                for (int y = 0; y < Grid; y++)
                {
                    int row = offset + y * Grid;
                    int mirrored = offset + (Grid - 1 - y) * Grid;
                    for (int x = 0; x < Grid; x++)
                    {
                        int value = actual[row + x];
                        upright += Math.Abs(value - expected[row + x]);
                        flipped += Math.Abs(value - expected[mirrored + x]);
                    }
                }
            }
            return Math.Min(upright, flipped) / (double)Length;
        }

        /// <summary>
        /// Shrinks the texture to 32x32 on the GPU and reads that back. Works for non-readable
        /// and compressed textures, which is what the game's atlases are.
        /// </summary>
        private static byte[] Compute(Texture2D tex)
        {
            int width = tex.width, height = tex.height;
            if (width % Grid != 0 || height % Grid != 0 || width < 2 * Grid || height < 2 * Grid) return null;

            RenderTexture target = null;
            Texture2D small = null;
            var previous = RenderTexture.active;
            try
            {
                target = RenderTexture.GetTemporary(Grid, Grid, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(tex, target);
                RenderTexture.active = target;

                small = new Texture2D(Grid, Grid, TextureFormat.RGBA32, false);
                small.ReadPixels(new Rect(0, 0, Grid, Grid), 0, 0, false);
                var pixels = small.GetPixels32();
                if (pixels == null || pixels.Length != Grid * Grid) return null;

                var signature = new byte[Length];
                for (int i = 0; i < Grid * Grid; i++)
                {
                    var pixel = pixels[i];
                    signature[i] = pixel.a;
                    signature[Grid * Grid + i] = pixel.g;
                }
                return signature;
            }
            catch (Exception ex)
            {
                LocalizationPlugin.Log.LogWarning($"[TEXTURE] Fingerprint failed for '{tex.name}': {ex.Message}");
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                if (target != null) RenderTexture.ReleaseTemporary(target);
                if (small != null) UnityEngine.Object.Destroy(small);
            }
        }
    }
}
