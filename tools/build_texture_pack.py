"""
Builds a ready-to-ship texture pack from the PNGs in TGYHTurkishPatch/textures.

Each PNG is encoded into the exact GPU format of the game texture it replaces and written
as a compressed .tgyc file to mod_files/dist/textures/. Ship those files in
BepInEx/plugins/TGYHTurkishPatch/textures/ instead of the PNGs: players' machines then only
read and upload them, with no PNG decoding or DXT encoding.

    python build_texture_pack.py

build_release.py runs this as part of making a release.

Requires: pip install UnityPy, and the .NET SDK (for the TextureBuilder project).
"""
import os
import re
import shutil
import subprocess
import sys
import tempfile

import UnityPy

sys.stdout.reconfigure(encoding="utf-8")

MOD_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GAME_DIR = os.path.dirname(MOD_DIR)
DATA_DIR = os.path.join(GAME_DIR, "Thank Goodness You're Here!_Data")
BUNDLES_DIR = os.path.join(DATA_DIR, "StreamingAssets", "CompBundles")
TEX_DIR = os.path.join(GAME_DIR, "BepInEx", "plugins", "TGYHTurkishPatch", "textures")
OUT_DIR = os.path.join(MOD_DIR, "dist", "textures")
BUILDER = os.path.join(MOD_DIR, "TextureBuilder", "TextureBuilder.csproj")

# Unity TextureFormat -> the plugin's RawFormat (see TextureEncoder.cs)
RAW_FORMAT = {4: 1, 5: 2, 14: 3, 3: 4, 7: 5, 1: 6, 10: 7, 28: 7, 12: 8, 29: 8}


def source_key(stem):
    m = re.search(r"-(CAB-[0-9a-f]{32})-(-?\d+)$", stem)
    if not m:
        m = re.search(r"-((?:sharedassets\d+|resources)\.assets)-(-?\d+)$", stem)
    return (m.group(1), int(m.group(2))) if m else None


def clean_name(stem):
    """Mirrors TextureManager.ExtractCleanTextureName for dump-style file names."""
    for token in ("-CAB-", "-sharedassets", "-resources.assets"):
        i = stem.rfind(token)
        if i > 0:
            return stem[:i]
    return stem


def folder_priority(path):
    rel = os.path.relpath(path, TEX_DIR).replace("\\", "/").lower()
    if "/" not in rel:
        return 3
    if rel.startswith("sharedassets/"):
        return 2
    if rel.startswith("compbundles/"):
        return 1
    return 0


def build(out_dir=OUT_DIR):
    """Encodes the pack into out_dir; returns the builder's exit code (0 = everything built)."""
    pngs = [os.path.join(root, f) for root, _, files in os.walk(TEX_DIR) for f in files if f.lower().endswith(".png")]
    # Same order as the plugin's index: the last PNG for a name is the one the game shows.
    pngs.sort(key=lambda p: (folder_priority(p), p))
    winners = {}
    for p in pngs:
        winners[clean_name(os.path.basename(p)[:-4]).lower()] = p

    by_key, skipped = {}, []
    for p in winners.values():
        key = source_key(os.path.basename(p)[:-4])
        if key:
            by_key[key] = p
        else:
            skipped.append((p, "file name has no -CAB-/-sharedassets source suffix"))

    sources = [os.path.join(BUNDLES_DIR, f) for f in sorted(os.listdir(BUNDLES_DIR))]
    sources += [os.path.join(DATA_DIR, f) for f in sorted(os.listdir(DATA_DIR)) if f.endswith(".assets")]

    manifest, seen = [], set()
    for path in sources:
        for obj in UnityPy.load(path).objects:
            if obj.type.name != "Texture2D":
                continue
            key = (os.path.basename(obj.assets_file.name), obj.path_id)
            if key not in by_key or key in seen:
                continue
            seen.add(key)
            tex = obj.read()
            png = by_key[key]
            raw = RAW_FORMAT.get(int(tex.m_TextureFormat))
            if raw is None or tex.m_MipCount != 1:
                skipped.append((png, f"format {tex.m_TextureFormat} / {tex.m_MipCount} mips needs the PNG"))
                continue
            out_name = clean_name(os.path.basename(png)[:-4]) + ".tgyc"
            manifest.append("\t".join([png, str(tex.m_Width), str(tex.m_Height), str(raw), out_name]))
    skipped += [(p, "original texture not found in the game data") for k, p in by_key.items() if k not in seen]

    print(f"{len(pngs)} PNGs -> {len(winners)} texture names -> {len(manifest)} to build")
    if os.path.isdir(out_dir):
        shutil.rmtree(out_dir)
    os.makedirs(out_dir)

    with tempfile.NamedTemporaryFile("w", suffix=".tsv", delete=False, encoding="utf-8") as fh:
        fh.write("\n".join(manifest))
    try:
        result = subprocess.run(["dotnet", "run", "-c", "Release", "--project", BUILDER, "--", fh.name, out_dir])
    finally:
        os.unlink(fh.name)

    # Anything that could not be prebuilt ships as the PNG itself.
    for png, why in skipped:
        print(f"shipping PNG as-is ({why}): {os.path.basename(png)}")
        shutil.copy2(png, out_dir)

    print(f"Pack written to {out_dir}")
    return result.returncode


if __name__ == "__main__":
    sys.exit(build())
