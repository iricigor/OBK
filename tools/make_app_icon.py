"""Generate matching OBK Windows and Android archive-box icons.

Development tooling only; requires Pillow. Run from the repository root:
    python .\\tools\\make_app_icon.py

Sizes up to 32 px use a larger envelope and thicker flap for small-size clarity.
"""
from pathlib import Path

from PIL import Image, ImageDraw

TILE = (15, 108, 189, 255)
PAPER = (255, 255, 255, 255)
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)
OUTPUT = Path(__file__).resolve().parents[1] / "src" / "OBK.App" / "Assets" / "OBK.ico"
ANDROID_OUTPUT = Path(__file__).resolve().parents[1] / "src" / "OBK.Android" / "Resources" / "AppIcon" / "obk.png"


def render(size: int) -> Image.Image:
    supersample = 8
    scale = size * supersample / 1024
    image = Image.new("RGBA", (size * supersample, size * supersample), (0, 0, 0, 0))
    d = ImageDraw.Draw(image)
    s = lambda *v: [round(x * scale) for x in v]
    d.rounded_rectangle(s(40, 40, 984, 984), radius=round(180 * scale), fill=TILE)
    d.rounded_rectangle(s(164, 204, 860, 344), radius=round(48 * scale), fill=PAPER)
    d.rounded_rectangle(s(164, 408, 860, 876), radius=round(64 * scale), fill=PAPER)
    if size <= 32:
        left, top, right, bottom, width = 260, 492, 764, 788, 64
    else:
        left, top, right, bottom, width = 296, 508, 728, 784, 36
    d.rounded_rectangle(s(left, top, right, bottom), radius=round(32 * scale), fill=TILE)
    d.line(s(left, top + 24, 512, top + 176, right, top + 24),
           fill=PAPER, width=max(1, round(width * scale)), joint="curve")
    return image.resize((size, size), Image.Resampling.LANCZOS)


def main() -> None:
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    images = [render(size) for size in SIZES]
    largest = images[-1]
    largest.save(OUTPUT, format="ICO", sizes=[(s, s) for s in SIZES], append_images=images[:-1])
    print(f"Wrote {OUTPUT}")
    ANDROID_OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    render(512).save(ANDROID_OUTPUT)
    print(f"Wrote {ANDROID_OUTPUT}")


if __name__ == "__main__":
    main()
