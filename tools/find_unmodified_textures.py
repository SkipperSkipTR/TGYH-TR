"""
Finds replacement PNGs that are pixel-identical to the game's own textures.

Those PNGs change nothing in-game but still cost a decode, an upload and a cache entry
each, so they are worth keeping out of the plugin's textures folder.

    python find_unmodified_textures.py            # report only
    python find_unmodified_textures.py --move     # move them to mod_files/_old/textures_unmodified/

Requires: pip install UnityPy Pillow
"""
import collections
import os
import re
import shutil
import sys

import UnityPy
from PIL import Image, ImageChops

Image.MAX_IMAGE_PIXELS = None
sys.stdout.reconfigure(encoding="utf-8")

GAME_DIR = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DATA_DIR = os.path.join(GAME_DIR, "Thank Goodness You're Here!_Data")
BUNDLES_DIR = os.path.join(DATA_DIR, "StreamingAssets", "CompBundles")
PLUGIN_DIR = os.path.join(GAME_DIR, "BepInEx", "plugins", "TGYHTurkishPatch")
TEX_DIR = os.path.join(PLUGIN_DIR, "textures")
UNMODIFIED_DIR = os.path.join(GAME_DIR, "mod_files", "_old", "textures_unmodified")

# Largest per-channel difference still counted as "unmodified" (decoder rounding).
TOLERANCE = 4


def source_key(stem):
    """'<name>-CAB-<hash>-<pathid>' or '<name>-sharedassetsN.assets-<pathid>' -> (asset file, path id)."""
    m = re.search(r"-(CAB-[0-9a-f]{32})-(-?\d+)$", stem)
    if not m:
        m = re.search(r"-((?:sharedassets\d+|resources)\.assets)-(-?\d+)$", stem)
    return (m.group(1), int(m.group(2))) if m else None


def is_unmodified(png_path, original):
    png = Image.open(png_path).convert("RGBA")
    if png.size != original.size:
        return False
    diff = ImageChops.difference(png, original)
    if diff.getbbox() is None:
        return True
    # Colour under fully transparent pixels is invisible; ignore it.
    visible = ImageChops.lighter(png.getchannel("A"), original.getchannel("A")).point(lambda v: 255 if v else 0)
    worst = ImageChops.lighter(diff.convert("RGB").convert("L"), diff.getchannel("A"))
    return ImageChops.multiply(worst, visible).getextrema()[1] <= TOLERANCE


def main():
    move = "--move" in sys.argv

    wanted = collections.defaultdict(list)
    unknown = []
    for root, _, files in os.walk(TEX_DIR):
        for name in files:
            if not name.lower().endswith(".png"):
                continue
            key = source_key(name[:-4])
            (wanted[key] if key else unknown).append(os.path.join(root, name))

    sources = [os.path.join(BUNDLES_DIR, f) for f in sorted(os.listdir(BUNDLES_DIR))]
    sources += [os.path.join(DATA_DIR, f) for f in sorted(os.listdir(DATA_DIR)) if f.endswith(".assets")]

    unmodified, modified, checked = [], [], set()
    for path in sources:
        for obj in UnityPy.load(path).objects:
            if obj.type.name != "Texture2D":
                continue
            key = (os.path.basename(obj.assets_file.name), obj.path_id)
            if key not in wanted or key in checked:
                continue
            checked.add(key)
            original = obj.read().image.convert("RGBA")
            for png in wanted[key]:
                (unmodified if is_unmodified(png, original) else modified).append(png)
        print(f"\r{len(unmodified) + len(modified)} PNGs compared...", end="", flush=True)
    print()

    not_found = [p for key, paths in wanted.items() if key not in checked for p in paths]
    size_mb = sum(os.path.getsize(p) for p in unmodified) / 1048576
    print(f"Modified:   {len(modified)}")
    print(f"Unmodified: {len(unmodified)} ({size_mb:.0f} MB)")
    if unknown or not_found:
        print(f"Left alone: {len(unknown) + len(not_found)} (no matching original found)")

    if not move:
        for p in sorted(unmodified):
            print("  unmodified:", os.path.relpath(p, TEX_DIR))
        print("\nRun again with --move to move the unmodified PNGs out of the textures folder.")
        return

    for p in unmodified:
        target = os.path.join(UNMODIFIED_DIR, os.path.relpath(p, TEX_DIR))
        os.makedirs(os.path.dirname(target), exist_ok=True)
        shutil.move(p, target)
    print(f"Moved {len(unmodified)} PNGs to {UNMODIFIED_DIR}")


if __name__ == "__main__":
    main()
