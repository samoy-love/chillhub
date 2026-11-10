"""Refresh offline images and create an isolated preview cache; never uploads.

Run: uv run --with pillow --with blake3 python scripts/content/prepare-bundled-media.py
Creator video bytes are never requested. Only their public oEmbed thumbnails
are cached alongside the external link, as in the launcher itself.
"""
import hashlib
import json
from pathlib import Path
import shutil
import urllib.request

import blake3
from PIL import Image, ImageOps

DIRECTORY = Path(__file__).resolve().parent
NATIVE = DIRECTORY.parent.parent / "launcher/ChillHub.Native"
PLAN = json.loads((DIRECTORY / ".cache/media-upload-plan.json").read_text("utf-8"))
GAMEPLAY = json.loads((DIRECTORY / "gameplay-verification.json").read_text("utf-8"))
CACHE = DIRECTORY / ".cache/preview-profile/cache/media"
CACHE.mkdir(parents=True, exist_ok=True)
report = {"serverWrites": 0, "cacheDirectory": str(CACHE), "images": [], "videos": []}


def cached_image(url, source):
    key = blake3.blake3(url.encode("utf-8")).hexdigest()
    destination = CACHE / f"{key}.img"
    shutil.copyfile(source, destination)
    report["images"].append({"url": url, "source": str(source), "cachePath": str(destination),
                             "sha256": hashlib.sha256(destination.read_bytes()).hexdigest()})


for gid, media in PLAN["games"].items():
    assets = NATIVE / "Assets/Media" / gid
    art = NATIVE / "art" / gid
    assets.mkdir(parents=True, exist_ok=True)
    art.mkdir(parents=True, exist_ok=True)
    poster = assets / "trailer-poster.jpg"
    shutil.copyfile(media["poster"]["localPath"], poster)
    cached_image(media["poster"]["url"], poster)
    creator = GAMEPLAY["games"][gid]
    creator_poster = assets / "gameplay-poster.jpg"
    request = urllib.request.Request(creator["posterUrl"], headers={"User-Agent": "ChillHub-content-review/1.0"})
    with urllib.request.urlopen(request, timeout=30) as response:
        data = response.read(2 * 1024 * 1024 + 1)
    assert len(data) <= 2 * 1024 * 1024
    creator_poster.write_bytes(data)
    cached_image(creator["posterUrl"], creator_poster)
    ordered = [poster, creator_poster]
    for shot in media["screenshots"]:
        destination = assets / shot["name"]
        shutil.copyfile(shot["localPath"], destination)
        cached_image(shot["url"], destination)
        ordered.append(destination)
    for index, source in enumerate(ordered):
        with Image.open(source) as image:
            # UI cards are 16:9. Preserve full photographs in Assets for gallery.
            image = ImageOps.fit(image.convert("RGB"), (480, 270), method=Image.Resampling.LANCZOS)
            image.save(art / f"tile-{index}.webp", quality=84, method=6)
    trailer = media["trailer"]
    report["videos"].append({"gameId": gid, "url": trailer["url"], "localPath": trailer["localPath"],
                              "sha256": trailer["sha256"]})
    print(f"{gid}: 5 offline images and tiles ready")

(DIRECTORY / ".cache/preview-media-map.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", "utf-8")
