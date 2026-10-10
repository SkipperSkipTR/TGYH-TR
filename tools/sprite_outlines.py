"""
Finds translated artwork that the game would not draw, and describes how to widen the sprites.

The game draws a sprite through a tight outline mesh around its ORIGINAL artwork. Text that
was repainted a little outside that outline is simply never drawn. For every replaced texture
this compares the PNG with the game's original, finds repainted pixels no sprite outline
covers, and writes extra rectangles for the sprite they belong to. The plugin adds those
rectangles to the sprite's mesh at runtime (see Plugin/SpriteOutlines.cs).

Used by build_texture_pack.py; not meant to be run on its own.

Limits, reported at the end of a build:
  - Pixels painted outside the sprite's own rectangle in the atlas cannot be recovered.
  - Pixels touching another sprite's artwork are left out rather than risk showing that sprite.
"""
import collections

from PIL import Image, ImageChops, ImageDraw
from UnityPy.helpers.MeshHelper import MeshHandler

# A repainted pixel counts when it differs this much from the original and is this opaque.
CHANGE_THRESHOLD = 40
ALPHA_THRESHOLD = 24
# Sprites with fewer uncovered pixels than this are left alone.
MIN_PIXELS = 12
# Extra rectangles are assembled from cells of this size, refined once where they would
# otherwise touch another sprite.
CELL = 8
FINE_CELL = 2


class SpriteInfo:
    __slots__ = ("name", "rect", "triangles")

    def __init__(self, name, rect, triangles):
        self.name = name
        self.rect = rect            # (left, top, right, bottom) in image coordinates
        self.triangles = triangles  # [((x, y), (x, y), (x, y)), ...] in image coordinates


def read_sprite(sprite, version):
    """Texture, and the sprite's place and outline on it, in image coordinates (y down)."""
    data = sprite.m_RD
    if sprite.m_SpriteAtlas:
        key = (sprite.m_RenderDataKey[0], sprite.m_RenderDataKey[1])
        for k, v in sprite.m_SpriteAtlas.read().m_RenderDataMap:
            if (k[0], k[1]) == key:
                data = v
                break
    texture = data.texture.read()
    width, height = texture.m_Width, texture.m_Height

    settings = data.settingsRaw
    packed, rotation = settings & 1, (settings >> 2) & 15
    rect, offset = data.textureRect, data.textureRectOffset
    scale = sprite.m_PixelsToUnits
    pivot_x, pivot_y = sprite.m_Pivot.x * sprite.m_Rect.width, sprite.m_Pivot.y * sprite.m_Rect.height

    mesh = MeshHandler(sprite.m_RD, version)
    mesh.process()
    points = []
    for x, y, _ in mesh.m_Vertices:
        tx, ty = x * scale + pivot_x - offset.x, y * scale + pivot_y - offset.y
        if packed and rotation in (1, 3):   # flipped horizontally / rotated 180
            tx = rect.width - tx
        if packed and rotation in (2, 3):   # flipped vertically / rotated 180
            ty = rect.height - ty
        points.append((rect.x + tx, height - (rect.y + ty)))
    triangles = [(points[a], points[b], points[c]) for sub in mesh.get_triangles() for a, b, c in sub]
    box = (rect.x, height - rect.y - rect.height, rect.x + rect.width, height - rect.y)
    return texture, SpriteInfo(sprite.m_Name, box, triangles)


def _coverage(size, sprites):
    mask = Image.new("L", size, 0)
    draw = ImageDraw.Draw(mask)
    for sprite in sprites:
        for triangle in sprite.triangles:
            draw.polygon(triangle, fill=255)
    return mask


def _cells(needed, forbidden, box, cell):
    """Cells of the box that hold needed pixels and no forbidden ones -> (kept cells, rejected cells)."""
    left, top, right, bottom = box
    keep, reject = [], []
    for y in range(top, bottom, cell):
        for x in range(left, right, cell):
            area = (x, y, min(x + cell, right), min(y + cell, bottom))
            if needed.crop(area).getbbox() is None:
                continue
            (keep if forbidden.crop(area).getbbox() is None else reject).append(area)
    return keep, reject


def _merge(cells):
    """Joins cells into fewer rectangles: runs along a row, then equal runs down the rows."""
    rows = collections.defaultdict(list)
    for left, top, right, bottom in sorted(cells):
        rows[(top, bottom)].append([left, right])
    runs = []
    for (top, bottom), spans in sorted(rows.items()):
        spans.sort()
        merged = [spans[0]]
        for left, right in spans[1:]:
            if left <= merged[-1][1]:
                merged[-1][1] = max(merged[-1][1], right)
            else:
                merged.append([left, right])
        runs += [[left, top, right, bottom] for left, right in merged]
    out = []
    for run in sorted(runs, key=lambda r: (r[0], r[2], r[1])):
        last = out[-1] if out else None
        if last and last[0] == run[0] and last[2] == run[2] and last[3] == run[1]:
            last[3] = run[3]
        else:
            out.append(run)
    return out


def find_fixes(original, replacement, sprites):
    """
    -> (fixes, uncovered, recovered). fixes: [(sprite, [rectangles in image coordinates])].
    """
    size = original.size
    diff = ImageChops.difference(replacement, original)
    changed = ImageChops.lighter(diff.convert("RGB").convert("L"), diff.getchannel("A"))
    changed = changed.point(lambda v: 255 if v > CHANGE_THRESHOLD else 0)
    opaque = replacement.getchannel("A").point(lambda v: 255 if v > ALPHA_THRESHOLD else 0)
    painted = ImageChops.multiply(changed, opaque)

    covered = _coverage(size, sprites)
    uncovered = ImageChops.multiply(painted, ImageChops.invert(covered))
    total = uncovered.histogram()[255]
    if total == 0:
        return [], 0, 0

    # Sprites sharing one rectangle are the same picture under several names.
    by_rect = collections.defaultdict(list)
    for sprite in sprites:
        by_rect[tuple(round(v) for v in sprite.rect)].append(sprite)

    fixes = []
    gained = Image.new("L", size, 0)   # everything the extra rectangles add, over all sprites
    gain = ImageDraw.Draw(gained)
    for box, group in by_rect.items():
        left, top, right, bottom = max(0, box[0]), max(0, box[1]), min(size[0], box[2]), min(size[1], box[3])
        if right <= left or bottom <= top:
            continue
        box = (left, top, right, bottom)
        needed = Image.new("L", size, 0)
        needed.paste(uncovered.crop(box), box)
        if needed.histogram()[255] < MIN_PIXELS:
            continue

        others = _coverage(size, [s for s in sprites if s not in group])
        keep, reject = _cells(needed, others, box, CELL)
        for area in reject:
            fine, _ = _cells(needed, others, area, FINE_CELL)
            keep += fine
        if not keep:
            continue

        rectangles = _merge(keep)
        for r in rectangles:
            gain.rectangle((r[0], r[1], r[2] - 1, r[3] - 1), fill=255)
        for sprite in group:
            fixes.append((sprite, rectangles))
    recovered = ImageChops.multiply(uncovered, gained).histogram()[255]
    return fixes, total, recovered


def format_line(texture_key, texture_height, sprite, rectangles):
    """One sprites.txt line; coordinates are texture pixels with the origin at the bottom left."""
    xs = [p[0] for t in sprite.triangles for p in t]
    ys = [texture_height - p[1] for t in sprite.triangles for p in t]
    bounds = "%d,%d,%d,%d" % (round(min(xs)), round(min(ys)), round(max(xs)), round(max(ys)))
    rects = ";".join("%d,%d,%d,%d" % (r[0], texture_height - r[3], r[2], texture_height - r[1]) for r in rectangles)
    return "\t".join([texture_key, sprite.name, bounds, rects])
