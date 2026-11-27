"""Check the static guide's stable routes and local links before publication."""

from html.parser import HTMLParser
from pathlib import Path
import sys
from urllib.parse import unquote, urlsplit


DEFAULT_ROOT = Path(__file__).resolve().parents[1] / "docs" / "user-guide"
REQUIRED_PAGES = ("index.html", "windows.html", "android.html")


class Page(HTMLParser):
    def __init__(self):
        super().__init__()
        self.links = []
        self.ids = set()
        self.errors = []
        self.language = False
        self.title = False
        self.viewport = False

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == "html":
            self.language = bool(attrs.get("lang"))
        if tag == "title":
            self.title = True
        if tag == "meta" and attrs.get("name") == "viewport":
            self.viewport = bool(attrs.get("content"))
        if identifier := attrs.get("id"):
            if identifier in self.ids:
                self.errors.append(f"duplicate id '{identifier}'")
            self.ids.add(identifier)
        for attribute in ("href", "src"):
            if attribute in attrs:
                self.links.append(attrs[attribute] or "")

    def handle_startendtag(self, tag, attrs):
        self.handle_starttag(tag, attrs)


def check(root=DEFAULT_ROOT):
    root = root.resolve()
    errors = []
    for name in REQUIRED_PAGES:
        if not (root / name).is_file():
            errors.append(f"missing required page: {name}")

    pages = {}
    for path in sorted(root.rglob("*.html")):
        page = Page()
        page.feed(path.read_text(encoding="utf-8"))
        pages[path] = page
        name = path.relative_to(root).as_posix()
        for error in page.errors:
            errors.append(f"{name}: {error}")
        for label, present in (("language", page.language), ("title", page.title),
                               ("viewport", page.viewport)):
            if not present:
                errors.append(f"{name}: missing {label}")

    for path, page in pages.items():
        name = path.relative_to(root).as_posix()
        for link in page.links:
            if not link:
                errors.append(f"{name}: empty link")
                continue
            url = urlsplit(link)
            if url.scheme or url.netloc:
                if url.scheme not in ("https", "mailto", "tel"):
                    errors.append(f"{name}: unsupported external link '{link}'")
                continue
            if url.path.startswith("/"):
                errors.append(f"{name}: root-relative link breaks the Pages base path: '{link}'")
                continue
            target = (path.parent / unquote(url.path)).resolve() if url.path else path
            if not target.is_relative_to(root):
                errors.append(f"{name}: link leaves the published guide: '{link}'")
                continue
            if target.is_dir():
                target /= "index.html"
            if not target.is_file():
                errors.append(f"{name}: missing local target '{link}'")
            elif url.fragment and target in pages and unquote(url.fragment) not in pages[target].ids:
                errors.append(f"{name}: missing fragment '{link}'")
    return errors, f"Checked {len(pages)} guide pages."


if __name__ == "__main__":
    errors, summary = check(Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_ROOT)
    print(summary)
    for error in errors:
        print(error, file=sys.stderr)
    sys.exit(bool(errors))
