"""Generate the OBK application icon (school cap over a message envelope).

Development tooling only; requires Pillow. Run from the repository root:
    python .\\tools\\make_app_icon.py

Sizes up to 32 px use a simplified design (bold envelope, small cap) because
fine details disappear at taskbar and title-bar sizes.
"""
from pathlib import Path

from PIL import Image, ImageDraw

TILE = (0, 95, 184, 255)          # accent blue
TILE_EDGE = (0, 72, 140, 255)
PAPER = (255, 255, 255, 255)
FLAP = (0, 95, 184, 255)
CAP = (32, 36, 44, 255)
TASSEL = (255, 196, 0, 255)
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)
OUTPUT = Path(__file__).resolve().parents[1] / "src" / "OBK.App" / "Assets" / "OBK.ico"


def detailed(scale: float) -> Image.Image:
    """Design on a 1024 grid; scale maps grid units to pixels."""
    size = round(1024 * scale)
    image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(image)
    s = lambda *v: [round(x * scale) for x in v]
    d.rounded_rectangle(s(40, 40, 984, 984), radius=round(210 * scale), fill=TILE_EDGE)
    d.rounded_rectangle(s(40, 40, 984, 960), radius=round(210 * scale), fill=TILE)
    left, top, right, bottom = 180, 420, 844, 850
    d.rounded_rectangle(s(left, top, right, bottom), radius=round(48 * scale), fill=PAPER)
    mid = (left + right) // 2
    width = max(1, round(34 * scale))
    d.line(s(left + 40, top + 40, mid, 660, right - 40, top + 40), fill=FLAP, width=width, joint="curve")
    cx, cy = mid, 330
    d.polygon(s(cx - 300, cy, cx, cy - 130, cx + 300, cy, cx, cy + 130), fill=CAP)
    d.rounded_rectangle(s(cx - 170, cy + 30, cx + 170, cy + 150), radius=round(40 * scale), fill=CAP)
    d.line(s(cx, cy, cx + 250, cy + 40, cx + 250, cy + 200), fill=TASSEL, width=max(1, round(22 * scale)), joint="curve")
    d.ellipse(s(cx + 222, cy + 190, cx + 278, cy + 260), fill=TASSEL)
    return image


def simple(scale: float) -> Image.Image:
    """Bold small-size design on a 64 grid: large envelope, strong flap, small cap."""
    size = round(64 * scale)
    image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(image)
    s = lambda *v: [round(x * scale) for x in v]
    d.rounded_rectangle(s(0, 0, 63, 63), radius=round(12 * scale), fill=TILE)
    d.rectangle(s(8, 24, 55, 54), fill=PAPER)
    d.line(s(9, 25, 32, 42, 54, 25), fill=FLAP, width=max(1, round(5 * scale)), joint="curve")
    d.polygon(s(14, 13, 32, 5, 50, 13, 32, 21), fill=CAP)
    d.rectangle(s(23, 15, 41, 22), fill=CAP)
    d.line(s(32, 13, 47, 15, 47, 23), fill=TASSEL, width=max(1, round(3 * scale)))
    return image


def render(size: int) -> Image.Image:
    supersample = 8
    if size <= 32:
        big = simple(size * supersample / 64)
    else:
        big = detailed(size * supersample / 1024)
    return big.resize((size, size), Image.LANCZOS)


def main() -> None:
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    images = [render(size) for size in SIZES]
    largest = images[-1]
    largest.save(OUTPUT, format="ICO", sizes=[(s, s) for s in SIZES], append_images=images[:-1])
    print(f"Wrote {OUTPUT}")


if __name__ == "__main__":
    main()
