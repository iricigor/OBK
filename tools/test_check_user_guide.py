"""Focused regression tests for the guide's publication checks."""

from pathlib import Path
import tempfile
import unittest

from check_user_guide import check, REQUIRED_PAGES


def page(content=""):
    return (
        '<!doctype html><html lang="en"><head><title>Guide</title>'
        '<meta name="viewport" content="width=device-width, initial-scale=1">'
        '</head><body>' + content + '</body></html>'
    )


class CheckUserGuideTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        for name in REQUIRED_PAGES:
            self.write(name, page())

    def write(self, name, content):
        (self.root / name).write_text(content, encoding="utf-8")

    def errors(self):
        return check(self.root)[0]

    def test_relative_navigation_assets_and_fragments_pass(self):
        self.write("index.html", page(
            '<a href="windows.html#install">Windows</a>'
            '<link rel="stylesheet" href="styles.css">'
            '<a href="https://github.com/iricigor/OBK">Source</a>'))
        self.write("windows.html", page('<h1 id="install">Install</h1>'))
        self.write("styles.css", "body { margin: 0; }")
        self.assertEqual([], self.errors())

    def test_deleted_platform_destination_fails(self):
        (self.root / "android.html").unlink()
        self.assertIn("missing required page: android.html", self.errors())

    def test_broken_page_asset_and_fragment_fail(self):
        self.write("index.html", page(
            '<a href="missing.html">Missing page</a><img src="missing.png" alt="">'
            '<a href="windows.html#missing">Missing section</a>'))
        errors = "\n".join(self.errors())
        self.assertIn("missing local target 'missing.html'", errors)
        self.assertIn("missing local target 'missing.png'", errors)
        self.assertIn("missing fragment 'windows.html#missing'", errors)

    def test_links_outside_pages_artifact_fail(self):
        self.write("index.html", page(
            '<a href="/windows.html">Wrong base path</a>'
            '<a href="../TECHNICAL.md">Unpublished document</a>'))
        errors = "\n".join(self.errors())
        self.assertIn("root-relative link", errors)
        self.assertIn("link leaves the published guide", errors)

    def test_duplicate_fragment_and_missing_page_metadata_fail(self):
        self.write("index.html", '<html><body><p id="same"></p><p id="same"></p></body></html>')
        errors = "\n".join(self.errors())
        self.assertIn("duplicate id 'same'", errors)
        for field in ("language", "title", "viewport"):
            self.assertIn(f"missing {field}", errors)


if __name__ == "__main__":
    unittest.main()
