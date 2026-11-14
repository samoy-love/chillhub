"""Refresh offline images and create an isolated preview cache; never uploads.

Run: uv run --with pillow --with blake3 python scripts/content/prepare-bundled-media.py
"""
import hashlib
import json
from pathlib import Path
import shutil

import blake3
from PIL import Image, ImageOps

DIRECTORY = Path(__file__).resolve().parent
NATIVE = DIRECTORY.parent.parent / "launcher/ChillHub.Native"
PLAN_PATH = DIRECTORY / ".cache/media-upload-plan.json"
CACHE_PATH = DIRECTORY / ".cache/preview-profile/cache/media"


def prepare_bundle_media(plan, native, cache):
    native = Path(native)
    cache = Path(cache)
    cache.mkdir(parents=True, exist_ok=True)
    report = {"serverWrites": 0, "cacheDirectory": str(cache), "images": [], "videos": []}

    def cached_image(url, source):
        key = blake3.blake3(url.encode("utf-8")).hexdigest()
        destination = cache / f"{key}.img"
        shutil.copyfile(source, destination)
        report["images"].append({"url": url, "source": str(source), "cachePath": str(destination),
                                 "sha256": hashlib.sha256(destination.read_bytes()).hexdigest()})

    for gid, media in plan["games"].items():
        if not gid or not all(character.islower() or character.isdigit() or character == "-" for character in gid):
            raise ValueError(f"Unsafe game id: {gid}")
        assets = native / "Assets/Media" / gid
        art = native / "art" / gid
        assets.mkdir(parents=True, exist_ok=True)
        art.mkdir(parents=True, exist_ok=True)
        # These names belong to this generator. Remove only stale generated tiles
        # so switching from five cards back to four cannot leave tile-4 behind.
        for old_tile in art.iterdir():
            if old_tile.name.startswith("tile-") and old_tile.name.endswith(".webp") and old_tile.name[5:-5].isdigit():
                old_tile.unlink()
        poster = assets / "trailer-poster.jpg"
        shutil.copyfile(media["poster"]["localPath"], poster)
        cached_image(media["poster"]["url"], poster)
        ordered = [poster]
        gameplay = media.get("gameplay")
        if gameplay:
            gameplay_poster = assets / "gameplay-poster.jpg"
            shutil.copyfile(gameplay["poster"]["localPath"], gameplay_poster)
            cached_image(gameplay["posterUrl"], gameplay_poster)
            ordered.append(gameplay_poster)
        else:
            (assets / "gameplay-poster.jpg").unlink(missing_ok=True)
        for shot in media["screenshots"]:
            destination = assets / shot["name"]
            shutil.copyfile(shot["localPath"], destination)
            cached_image(shot["url"], destination)
            ordered.append(destination)
        for index, source in enumerate(ordered):
            with Image.open(source) as image:
                image = ImageOps.fit(image.convert("RGB"), (480, 270), method=Image.Resampling.LANCZOS)
                image.save(art / f"tile-{index}.webp", quality=84, method=6)
        for video in [media["trailer"], *([gameplay] if gameplay else [])]:
            first_variant = next(iter(video.get("variants", [])), {})
            local_path = video.get("localPath") or first_variant.get("localPath", "")
            report["videos"].append({"gameId": gid, "role": video.get("role", "trailer"), "url": video["url"],
                                     "localPath": local_path,
                                     "sha256": video.get("sha256") or first_variant.get("sha256", "")})
        print(f"{gid}: {len(ordered)} offline images and tiles ready")
    return report


def main():
    plan = json.loads(PLAN_PATH.read_text("utf-8"))
    report = prepare_bundle_media(plan, NATIVE, CACHE_PATH)
    (DIRECTORY / ".cache/preview-media-map.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", "utf-8")


if __name__ == "__main__":
    main()
