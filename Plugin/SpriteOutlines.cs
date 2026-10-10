using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace TGYHTurkishPatch
{
    /// <summary>
    /// Widens sprite meshes so repainted artwork is not cut off.
    ///
    /// The game draws each sprite through a tight outline mesh around its original artwork.
    /// Translated text painted slightly outside that outline is in the texture but never
    /// drawn. sprites.txt (written by tools/build_texture_pack.py) lists, per sprite, extra
    /// rectangles of the texture that hold such pixels and belong to no other sprite; they
    /// are appended to the sprite's mesh here and taken out again when Turkish is switched off.
    /// </summary>
    internal static class SpriteOutlines
    {
        private sealed class Fix
        {
            public string Sprite;
            public float MinX, MinY, MaxX, MaxY;    // the sprite's mesh bounds on the texture
            public float[] Rectangles;              // x0, y0, x1, y1 per rectangle, texture pixels
        }

        private sealed class Applied
        {
            public Sprite Sprite;
            public Il2CppStructArray<Vector2> Vertices;     // original, in sprite rect space
            public Il2CppStructArray<ushort> Triangles;
        }

        // Texture key -> sprites on it that need widening.
        private static readonly Dictionary<string, List<Fix>> _fixes =
            new Dictionary<string, List<Fix>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<IntPtr, Applied> _applied = new Dictionary<IntPtr, Applied>();
        private static readonly HashSet<IntPtr> _seen = new HashSet<IntPtr>();
        private static Il2CppSystem.Type _spriteType;
        private static int _widened, _failed;
        private static bool _reportPending;

        public static int Count { get; private set; }

        public static bool HasFixes(string textureKey) => textureKey != null && _fixes.ContainsKey(textureKey);

        public static void Load(string file)
        {
            _fixes.Clear();
            Count = 0;
            if (!File.Exists(file)) return;

            try
            {
                foreach (var raw in File.ReadAllLines(file))
                {
                    if (raw.Length == 0 || raw[0] == '#') continue;
                    string[] parts = raw.Split('\t');
                    if (parts.Length != 4) continue;

                    float[] bounds = ParseNumbers(parts[2]);
                    float[] rectangles = ParseNumbers(parts[3].Replace(';', ','));
                    if (bounds.Length != 4 || rectangles.Length == 0 || rectangles.Length % 4 != 0) continue;

                    string key = TextureManager.NormalizeName(parts[0]);
                    if (!_fixes.TryGetValue(key, out var list)) _fixes[key] = list = new List<Fix>();
                    list.Add(new Fix
                    {
                        Sprite = parts[1],
                        MinX = bounds[0], MinY = bounds[1], MaxX = bounds[2], MaxY = bounds[3],
                        Rectangles = rectangles,
                    });
                    Count++;
                }
            }
            catch (Exception ex)
            {
                LocalizationPlugin.Log.LogWarning($"[SPRITE] Could not read sprites.txt: {ex.Message}");
            }
        }

        private static float[] ParseNumbers(string text)
        {
            string[] parts = text.Split(',');
            var values = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                values[i] = float.Parse(parts[i], CultureInfo.InvariantCulture);
            return values;
        }

        /// <summary>Lets the next <see cref="Scan"/> look at every loaded sprite again.</summary>
        public static void Rescan() => _seen.Clear();

        /// <summary>
        /// Looks for sprites that appeared since the last call and widens the ones listed in
        /// sprites.txt. <paramref name="keyOfTexture"/> maps a texture object to the key of the
        /// replacement applied to it, or null.
        /// </summary>
        public static unsafe void Scan(Func<IntPtr, string> keyOfTexture)
        {
            if (_fixes.Count == 0) return;

            if (_spriteType == null) _spriteType = Il2CppType.Of<Sprite>();
            var all = Resources.FindObjectsOfTypeAll(_spriteType);
            if (all == null) return;

            int count = all.Length;
            IntPtr* elements = (IntPtr*)((byte*)all.Pointer + 4 * IntPtr.Size);
            for (int i = 0; i < count; i++)
            {
                IntPtr ptr = elements[i];
                if (ptr == IntPtr.Zero || !_seen.Add(ptr) || _applied.ContainsKey(ptr)) continue;

                try
                {
                    var sprite = new Sprite(ptr);
                    var texture = sprite.texture;
                    if (texture == null) continue;

                    string key = keyOfTexture(texture.Pointer);
                    if (key == null || !_fixes.TryGetValue(key, out var fixes)) continue;

                    string name = sprite.name;
                    foreach (var fix in fixes)
                    {
                        if (!string.Equals(fix.Sprite, name, StringComparison.Ordinal)) continue;
                        if (Widen(sprite, texture, fix)) break;
                    }
                }
                catch (Exception ex)
                {
                    _failed++;
                    _reportPending = true;
                    if (LocalizationPlugin.VerboseLogging.Value)
                        LocalizationPlugin.Log.LogWarning($"[SPRITE] Could not widen a sprite: {ex.Message}");
                }
            }

            if (_reportPending)
            {
                _reportPending = false;
                LocalizationPlugin.Log.LogInfo($"[SPRITE] {_widened} sprite meshes widened so far, {_failed} failed.");
            }
        }

        private static bool Widen(Sprite sprite, Texture2D texture, Fix fix)
        {
            var vertices = sprite.vertices;
            var uv = sprite.uv;
            var triangles = sprite.triangles;
            int n = vertices.Length;
            if (n < 3 || uv.Length != n) return false;

            float width = texture.width, height = texture.height;
            float unit = sprite.pixelsPerUnit;
            Vector2 pivot = sprite.pivot;
            Rect rect = sprite.rect;

            // Vertices in sprite-rect pixels and where each sits on the texture.
            var local = new Vector2[n];
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                Vector2 v = vertices[i];
                // Rounding can leave a corner at 216.00002 of 216; Unity rejects the whole mesh for that.
                local[i] = new Vector2(
                    Mathf.Clamp(v.x * unit + pivot.x, 0f, rect.width),
                    Mathf.Clamp(v.y * unit + pivot.y, 0f, rect.height));
                Vector2 t = uv[i];
                float tx = t.x * width, ty = t.y * height;
                if (tx < minX) minX = tx; if (tx > maxX) maxX = tx;
                if (ty < minY) minY = ty; if (ty > maxY) maxY = ty;
            }

            // Several sprites can share a name; the mesh bounds tell which one this is.
            const float tolerance = 2.5f;
            if (Math.Abs(minX - fix.MinX) > tolerance || Math.Abs(maxX - fix.MaxX) > tolerance ||
                Math.Abs(minY - fix.MinY) > tolerance || Math.Abs(maxY - fix.MaxY) > tolerance)
                return false;

            // texture = scale * local + offset on each axis (scale is -1 for a flipped sprite).
            // Reading it off the mesh avoids textureRect, which Unity refuses for tightly packed sprites.
            if (!FitAxis(local, uv, n, width, true, out float scaleX, out float offsetX) ||
                !FitAxis(local, uv, n, height, false, out float scaleY, out float offsetY))
                return false;

            var extra = new List<Vector2>();
            for (int r = 0; r + 3 < fix.Rectangles.Length; r += 4)
            {
                float ax = (fix.Rectangles[r] - offsetX) / scaleX, bx = (fix.Rectangles[r + 2] - offsetX) / scaleX;
                float ay = (fix.Rectangles[r + 1] - offsetY) / scaleY, by = (fix.Rectangles[r + 3] - offsetY) / scaleY;
                float x0 = Mathf.Clamp(Math.Min(ax, bx), 0f, rect.width), x1 = Mathf.Clamp(Math.Max(ax, bx), 0f, rect.width);
                float y0 = Mathf.Clamp(Math.Min(ay, by), 0f, rect.height), y1 = Mathf.Clamp(Math.Max(ay, by), 0f, rect.height);
                if (x1 - x0 < 0.5f || y1 - y0 < 0.5f) continue;   // outside the sprite's rectangle

                extra.Add(new Vector2(x0, y0));
                extra.Add(new Vector2(x1, y0));
                extra.Add(new Vector2(x1, y1));
                extra.Add(new Vector2(x0, y1));
            }
            if (extra.Count == 0 || n + extra.Count > ushort.MaxValue) return false;

            var originalVertices = new Il2CppStructArray<Vector2>(n);
            for (int i = 0; i < n; i++) originalVertices[i] = local[i];

            var newVertices = new Il2CppStructArray<Vector2>(n + extra.Count);
            for (int i = 0; i < n; i++) newVertices[i] = local[i];
            for (int i = 0; i < extra.Count; i++) newVertices[n + i] = extra[i];

            int oldTriangles = triangles.Length;
            var newTriangles = new Il2CppStructArray<ushort>(oldTriangles + extra.Count / 4 * 6);
            for (int i = 0; i < oldTriangles; i++) newTriangles[i] = triangles[i];
            for (int q = 0, t = oldTriangles; q < extra.Count; q += 4)
            {
                int b = n + q;
                newTriangles[t++] = (ushort)b; newTriangles[t++] = (ushort)(b + 2); newTriangles[t++] = (ushort)(b + 1);
                newTriangles[t++] = (ushort)b; newTriangles[t++] = (ushort)(b + 3); newTriangles[t++] = (ushort)(b + 2);
            }

            sprite.OverrideGeometry(newVertices, newTriangles);

            // Unity rejects a mesh it doesn't like by logging an error and keeping the old one.
            if (sprite.vertices.Length != newVertices.Length)
            {
                _failed++;
                _reportPending = true;
                if (LocalizationPlugin.VerboseLogging.Value)
                {
                    float mx = 0, my = 0;
                    foreach (var e in extra) { mx = Math.Max(mx, e.x); my = Math.Max(my, e.y); }
                    LocalizationPlugin.Log.LogWarning(
                        $"[SPRITE] Unity rejected the wider mesh for '{sprite.name}': rect {rect.width}x{rect.height}, " +
                        $"extra vertices reach {mx},{my}, scale {scaleX},{scaleY}, bounds {sprite.bounds.size.x * unit}x{sprite.bounds.size.y * unit}");
                }
                return true;
            }

            _applied[sprite.Pointer] = new Applied { Sprite = sprite, Vertices = originalVertices, Triangles = triangles };
            _widened++;
            _reportPending = true;
            if (LocalizationPlugin.VerboseLogging.Value)
                LocalizationPlugin.Log.LogInfo($"[SPRITE] Widened '{sprite.name}' with {extra.Count / 4} rectangles ({n} -> {sprite.vertices.Length} vertices).");
            return true;
        }

        /// <summary>Least-squares fit of texture = scale * local + offset along one axis.</summary>
        private static bool FitAxis(Vector2[] local, Il2CppStructArray<Vector2> uv, int n, float size, bool xAxis,
            out float scale, out float offset)
        {
            double sumL = 0, sumT = 0, sumLL = 0, sumLT = 0;
            for (int i = 0; i < n; i++)
            {
                double l = xAxis ? local[i].x : local[i].y;
                Vector2 t2 = uv[i];
                double t = (xAxis ? t2.x : t2.y) * size;
                sumL += l; sumT += t; sumLL += l * l; sumLT += l * t;
            }
            double variance = n * sumLL - sumL * sumL;
            scale = 0f;
            offset = 0f;
            if (variance < 1e-3) return false;

            double s = (n * sumLT - sumL * sumT) / variance;
            // Anything but 1:1 (either way round) means this isn't the layout the data was made for.
            if (Math.Abs(Math.Abs(s) - 1.0) > 0.02) return false;
            scale = (float)s;
            offset = (float)((sumT - s * sumL) / n);
            return true;
        }

        /// <summary>Puts every widened sprite back to its original mesh.</summary>
        public static void RestoreAll()
        {
            int restored = 0;
            foreach (var entry in _applied.Values)
            {
                try
                {
                    if (entry.Sprite == null) continue;
                    entry.Sprite.OverrideGeometry(entry.Vertices, entry.Triangles);
                    restored++;
                }
                catch { }
            }
            _applied.Clear();
            _seen.Clear();
            if (restored > 0) LocalizationPlugin.Log.LogInfo($"[SPRITE] Restored {restored} sprite meshes.");
        }

        /// <summary>Forgets sprites that were destroyed with their scene or bundle.</summary>
        public static void Prune()
        {
            if (_applied.Count == 0) return;
            var dead = new List<IntPtr>();
            foreach (var kvp in _applied)
                if (kvp.Value.Sprite == null) dead.Add(kvp.Key);
            foreach (var ptr in dead) _applied.Remove(ptr);
        }
    }
}
