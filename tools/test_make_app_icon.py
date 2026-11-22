"""Checks for reproducible, matching application icon assets."""

from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from PIL import Image

import make_app_icon


class ApplicationIconTests(unittest.TestCase):
    def test_generated_assets_match_their_rendered_frames(self):
        with tempfile.TemporaryDirectory() as directory:
            ico = Path(directory) / "windows" / "OBK.ico"
            png = Path(directory) / "android" / "obk.png"
            with patch.object(make_app_icon, "OUTPUT", ico), patch.object(make_app_icon, "ANDROID_OUTPUT", png):
                make_app_icon.main()
            with Image.open(ico) as windows:
                self.assertEqual({(size, size) for size in make_app_icon.SIZES}, windows.ico.sizes())
                for size in make_app_icon.SIZES:
                    with self.subTest(size=size):
                        self.assertEqual(make_app_icon.render(size).tobytes(),
                                         windows.ico.getimage((size, size)).convert("RGBA").tobytes())
            with Image.open(png) as android:
                self.assertEqual((512, 512), android.size)
                self.assertEqual(make_app_icon.render(512).tobytes(), android.convert("RGBA").tobytes())
            before = (ico.read_bytes(), png.read_bytes())
            with patch.object(make_app_icon, "OUTPUT", ico), patch.object(make_app_icon, "ANDROID_OUTPUT", png):
                make_app_icon.main()
            self.assertEqual(before, (ico.read_bytes(), png.read_bytes()))

    def test_android_box_is_not_clipped_by_a_circular_launcher_mask(self):
        image = make_app_icon.render(512)
        for y in range(image.height):
            for x in range(image.width):
                r, g, b, alpha = image.getpixel((x, y))
                if alpha > 200 and min(r, g, b) > 200:
                    self.assertLess((x - 255.5) ** 2 + (y - 255.5) ** 2, 256 ** 2)


if __name__ == "__main__":
    unittest.main()
