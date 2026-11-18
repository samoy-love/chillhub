import importlib.util
import copy
import json
import tempfile
import unittest
from pathlib import Path

from PIL import Image, UnidentifiedImageError

MODULE_PATH = Path(__file__).with_name("prepare-bundled-media.py")
SPEC = importlib.util.spec_from_file_location("prepare_bundled_media", MODULE_PATH)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


COLORS = [(220, 30, 30), (30, 200, 50), (30, 60, 220), (220, 190, 20), (170, 40, 190)]


def image_file(path, color):
    path.parent.mkdir(parents=True, exist_ok=True)
    Image.new("RGB", (640, 360), color).save(path, format="JPEG", quality=100)
    return str(path)


def fixture_plan(root):
    source = root / "source"
    trailer = image_file(source / "trailer.jpg", COLORS[0])
    gameplay = image_file(source / "gameplay.jpg", COLORS[1])
    shots = [image_file(source / f"shot-{i}.jpg", COLORS[i + 1]) for i in range(1, 4)]
    return {"games": {"sample": {
        "poster": {"localPath": trailer, "url": "https://launcher.samoy.love/assets/trailer.jpg"},
        "trailer": {"url": "https://launcher.samoy.love/assets/trailer.mp4", "localPath": "trailer.mp4", "sha256": "a" * 64},
        "gameplay": {"role": "gameplay", "url": "https://launcher.samoy.love/assets/gameplay.mp4",
                     "posterUrl": "https://launcher.samoy.love/assets/gameplay.jpg", "localPath": "gameplay.mp4",
                     "poster": {"localPath": gameplay}, "variants": []},
        "screenshots": [{"name": f"screenshot-{i}.jpg", "localPath": shot,
                         "url": f"https://launcher.samoy.love/assets/shot-{i}.jpg"}
                        for i, shot in enumerate(shots, 1)],
    }}}


def tree_bytes(*roots):
    return {f"{root.name}/{item.relative_to(root).as_posix()}": item.read_bytes()
            for root in roots for item in root.rglob("*") if item.is_file()}


class BundleMediaTests(unittest.TestCase):
    def test_gameplay_adds_second_tile_without_shifting_screenshot_filenames(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            game_dir = root / "source"
            trailer_poster = image_file(game_dir / "trailer.jpg", COLORS[0])
            gameplay_poster = image_file(game_dir / "gameplay.jpg", COLORS[1])
            screenshots = [image_file(game_dir / f"shot-{i}.jpg", COLORS[i + 1]) for i in range(1, 4)]
            plan = {"games": {"sample": {
                "poster": {"localPath": trailer_poster, "url": "https://launcher.samoy.love/assets/trailer.jpg"},
                "trailer": {"url": "https://launcher.samoy.love/assets/trailer.mp4", "localPath": "trailer.mp4", "sha256": "a" * 64},
                "gameplay": {"role": "gameplay", "url": "https://launcher.samoy.love/assets/gameplay.mp4",
                             "posterUrl": "https://launcher.samoy.love/assets/gameplay.jpg", "localPath": "gameplay.mp4",
                             "poster": {"localPath": gameplay_poster}, "variants": []},
                "screenshots": [{"name": f"screenshot-{i}.jpg", "localPath": shot,
                                 "url": f"https://launcher.samoy.love/assets/shot-{i}.jpg"}
                                for i, shot in enumerate(screenshots, 1)],
            }}}
            native, cache = root / "native", root / "cache"
            report = MODULE.prepare_bundle_media(plan, native, cache)
            serialized = json.dumps(report)
            self.assertIn('"cachePath":', serialized)
            self.assertNotIn("stagedCache", serialized)
            art = native / "art/sample"
            self.assertEqual(sorted(p.name for p in art.glob("tile-*.webp")), [f"tile-{i}.webp" for i in range(5)])
            self.assertTrue((native / "Assets/Media/sample/gameplay-poster.jpg").is_file())
            self.assertEqual(sorted(p.name for p in (native / "Assets/Media/sample").glob("*.jpg")),
                             ["gameplay-poster.jpg", "screenshot-1.jpg", "screenshot-2.jpg", "screenshot-3.jpg", "trailer-poster.jpg"])
            self.assertEqual(len(report["images"]), 5)
            self.assertEqual([v["role"] for v in report["videos"]], ["trailer", "gameplay"])
            self.assertTrue(all(Path(item["cachePath"]).is_file() for item in report["images"]))
            for index, expected in enumerate(COLORS):
                with Image.open(art / f"tile-{index}.webp") as tile:
                    actual = tile.convert("RGB").getpixel((240, 135))
                self.assertLessEqual(max(abs(actual[channel] - expected[channel]) for channel in range(3)), 25,
                                     f"tile-{index} has the wrong source image: {actual}")

            # A later four-card rebuild removes only generated tile files beyond
            # the new contiguous set while preserving bundled source images.
            without_gameplay = {"games": {"sample": {key: value for key, value in plan["games"]["sample"].items()
                                                      if key != "gameplay"}}}
            MODULE.prepare_bundle_media(without_gameplay, native, cache)
            self.assertEqual(sorted(p.name for p in art.glob("tile-*.webp")), [f"tile-{i}.webp" for i in range(4)])
            self.assertFalse((art / "tile-4.webp").exists())
            self.assertFalse((native / "Assets/Media/sample/gameplay-poster.jpg").exists())

    def test_without_gameplay_keeps_four_tiles(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            game_dir = root / "source"
            poster = image_file(game_dir / "trailer.jpg", COLORS[0])
            shots = [image_file(game_dir / f"shot-{i}.jpg", COLORS[i]) for i in range(1, 4)]
            plan = {"games": {"sample": {
                "poster": {"localPath": poster, "url": "https://launcher.samoy.love/assets/trailer.jpg"},
                "trailer": {"url": "https://launcher.samoy.love/assets/trailer.mp4", "localPath": "trailer.mp4", "sha256": "a" * 64},
                "screenshots": [{"name": f"screenshot-{i}.jpg", "localPath": shot,
                                 "url": f"https://launcher.samoy.love/assets/shot-{i}.jpg"}
                                for i, shot in enumerate(shots, 1)],
            }}}
            native = root / "native"
            MODULE.prepare_bundle_media(plan, native, root / "cache")
            self.assertEqual(sorted(p.name for p in (native / "art/sample").glob("tile-*.webp")),
                             [f"tile-{i}.webp" for i in range(4)])

    def test_missing_or_corrupt_source_keeps_previous_posters_and_tiles(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            plan = fixture_plan(root)
            native, cache = root / "native", root / "cache"
            MODULE.prepare_bundle_media(plan, native, cache)
            assets, art = native / "Assets/Media/sample", native / "art/sample"
            before = tree_bytes(assets, art, cache)

            missing = copy.deepcopy(plan)
            missing["games"]["sample"]["screenshots"][1]["localPath"] = str(root / "missing.jpg")
            with self.assertRaises(FileNotFoundError):
                MODULE.prepare_bundle_media(missing, native, cache)
            self.assertEqual(tree_bytes(assets, art, cache), before)

            corrupt_path = root / "source/corrupt.jpg"
            corrupt_path.write_bytes(b"not a jpeg")
            corrupt = copy.deepcopy(plan)
            corrupt["games"]["sample"]["screenshots"][1]["localPath"] = str(corrupt_path)
            with self.assertRaises(UnidentifiedImageError):
                MODULE.prepare_bundle_media(corrupt, native, cache)
            self.assertEqual(tree_bytes(assets, art, cache), before)


if __name__ == "__main__":
    unittest.main()
