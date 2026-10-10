"""
Makes the texture pack work on another build of the game (macOS, another store, ...).

Sprite atlases are named "sactx-...-<hash>", and the hash differs between builds even when
the atlas itself is pixel-for-pixel the same. This compares the build in this game folder
with another one and writes textures/aliases.txt, which tells the plugin "a texture called
<other name> is the one <our file> was made for". Only atlases whose pictures really are
identical get an alias.

    python tools/build_texture_aliases.py "<other build>"

<other build> is the other game's folder, its .app, or its Data folder. Entries from earlier
runs (other builds) are kept. build_release.py ships aliases.txt with the textures.

Requires: pip install UnityPy Pillow
"""
import collections
import os
import re
import sys

import UnityPy
from PIL import Image, ImageChops

Image.MAX_IMAGE_PIXELS = None
sys.stdout.reconfigure(encoding="utf-8")

REPO_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GAME_DIR = os.path.dirname(REPO_DIR)
TEX_DIR = os.path.join(GAME_DIR, "BepInEx", "plugins", "TGYHTurkishPatch", "textures")
ALIASES = os.path.join(TEX_DIR, "aliases.txt")

# Share of pixels allowed to differ noticeably between "identical" atlases (compression noise).
MAX_DIFFERENT = 0.005


def find_data_dir(path):
    """Accepts a game folder, a .app bundle or a Data folder."""
    candidates = [path, os.path.join(path, "Contents", "Resources", "Data")]
    if os.path.isdir(path):
        for name in os.listdir(path):
            full = os.path.join(path, name)
            if name.endswith("_Data"):
                candidates.append(full)
            if name.endswith(".app"):
                candidates.append(os.path.join(full, "Contents", "Resources", "Data"))
    for c in candidates:
        if os.path.isfile(os.path.join(c, "globalgamemanagers")):
            return c
    sys.exit(f"No Unity data folder found in: {path}")


def asset_files(data_dir):
    files = [os.path.join(data_dir, f) for f in sorted(os.listdir(data_dir)) if f.endswith(".assets")]
    bundles = os.path.join(data_dir, "StreamingAssets", "CompBundles")
    if os.path.isdir(bundles):
        files += [os.path.join(bundles, f) for f in sorted(os.listdir(bundles))]
    return files


def file_key(texture_name):
    """A texture name the way it appears as a file name in the textures folder."""
    return re.sub(r'[<>:"/\\|?*]', "_", texture_name).strip().lower()


def stem(texture_name):
    return re.sub(r"-[0-9a-f]{8}$", "", texture_name)


def clean_name(file_stem):
    for token in ("-CAB-", "-sharedassets", "-resources.assets"):
        i = file_stem.rfind(token)
        if i > 0:
            return file_stem[:i]
    return file_stem


def collect(data_dir, wanted):
    """name -> first Texture2D reader with that name, for names accepted by `wanted`."""
    found = {}
    for path in asset_files(data_dir):
        try:
            env = UnityPy.load(path)
        except Exception:
            continue
        for obj in env.objects:
            if obj.type.name != "Texture2D":
                continue
            tex = obj.read()
            if tex.m_Name not in found and wanted(tex.m_Name):
                found[tex.m_Name] = tex
    return found


def same_picture(a, b):
    if a.size != b.size:
        return False
    diff = ImageChops.difference(a, b)
    worst = ImageChops.lighter(diff.convert("RGB").convert("L"), diff.getchannel("A"))
    visible = ImageChops.lighter(a.getchannel("A"), b.getchannel("A")).point(lambda v: 255 if v else 0)
    strong = ImageChops.multiply(worst, visible).point(lambda v: 255 if v > 40 else 0)
    return strong.histogram()[255] / (a.size[0] * a.size[1]) <= MAX_DIFFERENT


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    other_dir = find_data_dir(sys.argv[1])
    this_dir = find_data_dir(GAME_DIR)

    pack = set()
    for root, _, files in os.walk(TEX_DIR):
        for f in files:
            if f.lower().endswith((".png", ".tgyc")):
                pack.add(clean_name(os.path.splitext(f)[0]).lower())

    ours = collect(this_dir, lambda n: file_key(n) in pack)
    stems = {stem(n) for n in ours}
    theirs = collect(other_dir, lambda n: stem(n) in stems)
    by_stem = collections.defaultdict(list)
    for name in theirs:
        by_stem[stem(name)].append(name)

    aliases, same_name, unresolved = {}, 0, []
    for name, tex in sorted(ours.items()):
        if name in theirs:
            same_name += 1
            continue
        picture = tex.image.convert("RGBA")
        matches = [n for n in by_stem.get(stem(name), [])
                   if n not in ours and same_picture(picture, theirs[n].image.convert("RGBA"))]
        if not matches:
            unresolved.append(name)
        for n in matches:
            aliases[n] = file_key(name)
        print(f"\r{same_name + len(aliases) + len(unresolved)} / {len(ours)} compared...", end="", flush=True)
    print()

    existing = {}
    if os.path.isfile(ALIASES):
        with open(ALIASES, encoding="utf-8") as fh:
            for line in fh:
                if "\t" in line and not line.startswith("#"):
                    alias, target = line.rstrip("\n").split("\t", 1)
                    existing[alias] = target
    existing.update(aliases)
    with open(ALIASES, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("# <texture name in another build of the game> TAB <texture file here, without extension>\n")
        fh.write("# Generated by tools/build_texture_aliases.py\n")
        for alias, target in sorted(existing.items()):
            fh.write(f"{alias}\t{target}\n")

    print(f"Textures in the pack: {len(ours)}")
    print(f"  same name in the other build: {same_name}")
    print(f"  identical picture under another name: {len(ours) - same_name - len(unresolved)} ({len(aliases)} aliases)")
    print(f"  no identical counterpart: {len(unresolved)}")
    for name in unresolved:
        print("     ", name)
    print(f"Wrote {ALIASES} ({len(existing)} entries)")


if __name__ == "__main__":
    main()
