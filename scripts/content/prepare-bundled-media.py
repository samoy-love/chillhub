"""Refresh offline images and create an isolated preview cache; never uploads.

Run: uv run --with pillow --with blake3 python scripts/content/prepare-bundled-media.py
"""
import hashlib
import json
import os
from pathlib import Path
import shutil
import tempfile

import blake3
from PIL import Image, ImageOps

DIRECTORY = Path(__file__).resolve().parent
NATIVE = DIRECTORY.parent.parent / "launcher/ChillHub.Native"
PLAN_PATH = DIRECTORY / ".cache/media-upload-plan.json"
CACHE_PATH = DIRECTORY / ".cache/preview-profile/cache/media"


def prepare_bundle_media(plan, native, cache):
    native = Path(native)
    cache = Path(cache)
    native.mkdir(parents=True, exist_ok=True)
    cache.mkdir(parents=True, exist_ok=True)
    report = {"serverWrites": 0, "cacheDirectory": str(cache), "images": [], "videos": []}

    for gid, media in plan["games"].items():
        if not gid or not all(character.islower() or character.isdigit() or character == "-" for character in gid):
            raise ValueError(f"Unsafe game id: {gid}")
        assets = native / "Assets/Media" / gid
        art = native / "art" / gid
        gameplay = media.get("gameplay")

        # Prepare and decode every source for this game's gallery before touching
        # its current posters, tiles, or preview cache.
        image_sources = [(media["poster"], "trailer-poster.jpg")]
        if gameplay:
            image_sources.append(({**gameplay["poster"], "url": gameplay["posterUrl"]}, "gameplay-poster.jpg"))
        for shot in media["screenshots"]:
            image_sources.append((shot, shot["name"]))

        with tempfile.TemporaryDirectory(prefix=".bundle-stage-", dir=native) as temporary, \
                tempfile.TemporaryDirectory(prefix=".bundle-cache-stage-", dir=cache) as cache_temporary:
            staging = Path(temporary)
            staged_assets = staging / "assets"
            staged_tiles = staging / "tiles"
            staged_cache = Path(cache_temporary)
            staged_assets.mkdir()
            staged_tiles.mkdir()
            image_reports = []
            for index, (entry, filename) in enumerate(image_sources):
                staged_image = staged_assets / filename
                shutil.copyfile(entry["localPath"], staged_image)
                with Image.open(staged_image) as source_image:
                    source_image.load()
                    tile = ImageOps.fit(source_image.convert("RGB"), (480, 270), method=Image.Resampling.LANCZOS)
                    tile.save(staged_tiles / f"tile-{index}.webp", quality=84, method=6)
                key = blake3.blake3(entry["url"].encode("utf-8")).hexdigest()
                cached = staged_cache / f"{key}.img"
                shutil.copyfile(staged_image, cached)
                image_reports.append({"url": entry["url"], "source": str(assets / filename),
                                      "cachePath": str(cache / cached.name),
                                      "sha256": hashlib.sha256(cached.read_bytes()).hexdigest()})

            assets.mkdir(parents=True, exist_ok=True)
            art.mkdir(parents=True, exist_ok=True)
            for staged in staged_assets.iterdir():
                os.replace(staged, assets / staged.name)
            for staged in staged_tiles.iterdir():
                os.replace(staged, art / staged.name)
            # These names belong to this generator. Delete stale generated tiles
            # only after every new source image has been decoded and staged.
            for old_tile in art.iterdir():
                suffix = old_tile.name[5:-5] if old_tile.name.startswith("tile-") and old_tile.name.endswith(".webp") else ""
                if suffix.isdigit() and int(suffix) >= len(image_sources):
                    old_tile.unlink()
            if not gameplay:
                (assets / "gameplay-poster.jpg").unlink(missing_ok=True)
            for staged in staged_cache.iterdir():
                os.replace(staged, cache / staged.name)
            report["images"].extend(image_reports)

        for video in [media["trailer"], *([gameplay] if gameplay else [])]:
            first_variant = next(iter(video.get("variants", [])), {})
            local_path = video.get("localPath") or first_variant.get("localPath", "")
            report["videos"].append({"gameId": gid, "role": video.get("role", "trailer"), "url": video["url"],
                                     "localPath": local_path,
                                     "sha256": video.get("sha256") or first_variant.get("sha256", "")})
        print(f"{gid}: {len(image_sources)} offline images and tiles ready")
    return report


def main():
    plan = json.loads(PLAN_PATH.read_text("utf-8"))
    report = prepare_bundle_media(plan, NATIVE, CACHE_PATH)
    (DIRECTORY / ".cache/preview-media-map.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", "utf-8")


if __name__ == "__main__":
    main()
