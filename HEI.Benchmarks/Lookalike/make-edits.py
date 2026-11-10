"""Makes the look-alike set's files from its originals (docs/LOOKALIKE-BAR.md).

usage: python -I make-edits.py <commons-set.json> <originals folder> <out folder>

<originals folder> holds each original as <series>-<shot>.jpg, as downloaded. <out folder> gets files\ (every
original copied byte for byte, EXIF and all, and its 16 edits, saved as JPEG without metadata), each under a
neutral name, and truth.json, which says what each file is. Needs Pillow; the seed is fixed, so the same
originals always give the same files.
"""
import hashlib
import json
import math
import os
import random
import shutil
import sys
from multiprocessing import Pool

from PIL import Image, ImageEnhance, ImageFilter

Image.MAX_IMAGE_PIXELS = 200_000_000

KINDS = [
    "crop-90", "crop-75", "crop-50", "crop-square",
    "resave-q85", "resave-q60", "resave-q35",
    "resize-50", "resize-1080",
    "filter-bw", "filter-warm", "filter-vivid", "filter-bright", "filter-blur",
    "rotate-2", "rotate-5",
]


def name(series, shot, kind):
    return hashlib.sha256(f"heiward-lookalike|{series}|{shot}|{kind}".encode()).hexdigest()[:12] + ".jpg"


def corner_crop(img, side, rng):
    """A crop keeping `side` of each side, towards a corner the seed picks."""
    w, h = img.size
    cw, ch = round(w * side), round(h * side)
    x = rng.choice([0, w - cw])
    y = rng.choice([0, h - ch])
    return img.crop((x, y, x + cw, y + ch))


def rotate(img, degrees):
    """Turned by `degrees`, cut to the largest upright rectangle of the picture's shape inside it."""
    w, h = img.size
    t = math.radians(abs(degrees))
    s = min(w / (w * math.cos(t) + h * math.sin(t)), h / (w * math.sin(t) + h * math.cos(t)))
    turned = img.rotate(degrees, resample=Image.BICUBIC, expand=False)
    cw, ch = int(w * s), int(h * s)
    x, y = (w - cw) // 2, (h - ch) // 2
    return turned.crop((x, y, x + cw, y + ch))


def edit(img, kind, rng):
    """The edited picture and the JPEG quality to save it at."""
    w, h = img.size
    if kind == "crop-90":
        cw, ch = round(w * 0.95), round(h * 0.95)
        x, y = (w - cw) // 2, (h - ch) // 2
        return img.crop((x, y, x + cw, y + ch)), 92
    if kind == "crop-75":
        return corner_crop(img, 0.87, rng), 92
    if kind == "crop-50":
        return corner_crop(img, 0.71, rng), 92
    if kind == "crop-square":
        s = min(w, h)
        x, y = (w - s) // 2, (h - s) // 2
        return img.crop((x, y, x + s, y + s)), 92
    if kind.startswith("resave-q"):
        return img, int(kind[len("resave-q"):])
    if kind == "resize-50":
        return img.resize((w // 2, h // 2), Image.LANCZOS), 85
    if kind == "resize-1080":
        f = 1080 / max(w, h)
        return img.resize((round(w * f), round(h * f)), Image.LANCZOS), 75
    if kind == "filter-bw":
        return img.convert("L").convert("RGB"), 92
    if kind == "filter-warm":
        r, g, b = img.split()
        r = r.point(lambda v: min(255, round(v * 1.08)))
        b = b.point(lambda v: round(v * 0.88))
        return Image.merge("RGB", (r, g, b)), 92
    if kind == "filter-vivid":
        return ImageEnhance.Contrast(ImageEnhance.Color(img).enhance(1.4)).enhance(1.15), 92
    if kind == "filter-bright":
        return ImageEnhance.Brightness(img).enhance(1.2), 92
    if kind == "filter-blur":
        return img.filter(ImageFilter.GaussianBlur(radius=0.002 * max(w, h))), 92
    if kind == "rotate-2":
        return rotate(img, 2), 92
    if kind == "rotate-5":
        return rotate(img, -5), 92
    raise ValueError(kind)


def one(job):
    entry, originals, files = job
    series, shot = entry["series"], entry["shot"]
    src = os.path.join(originals, f"{series}-{shot}.jpg")
    rows = []
    orig_name = name(series, shot, "original")
    shutil.copyfile(src, os.path.join(files, orig_name))
    base = {"series": series, "shot": shot, "split": entry["split"]}
    with Image.open(src) as opened:
        img = opened.convert("RGB")
    rows.append({**base, "file": orig_name, "kind": "original", "width": img.size[0], "height": img.size[1],
                 "bytes": os.path.getsize(src)})
    rng = random.Random(f"{series}-{shot}")
    for kind in KINDS:
        out, quality = edit(img, kind, rng)
        path = os.path.join(files, name(series, shot, kind))
        out.save(path, "JPEG", quality=quality)
        rows.append({**base, "file": os.path.basename(path), "kind": kind, "width": out.size[0], "height": out.size[1],
                     "bytes": os.path.getsize(path)})
    return rows


def main():
    manifest, originals, out = sys.argv[1], sys.argv[2], sys.argv[3]
    files = os.path.join(out, "files")
    os.makedirs(files, exist_ok=True)
    with open(manifest, encoding="utf-8") as f:
        entries = json.load(f)
    jobs = [(e, originals, files) for e in entries]
    with Pool(max(1, (os.cpu_count() or 2) // 2)) as pool:
        rows = [r for part in pool.imap(one, jobs) for r in part]
    with open(os.path.join(out, "truth.json"), "w", encoding="utf-8") as f:
        json.dump(rows, f, indent=1)
    print(len(rows), "files")


if __name__ == "__main__":
    main()
