"""Generate the OBK application icon (school cap over a message envelope).

Development tooling only; requires Pillow. Run from the repository root:
    python .\\tools\\make_app_icon.py
"""
from pathlib import Path

from PIL import Image, ImageDraw

SIZE = 1024
TILE = (0, 95, 184, 255)          # accent blue
TILE_EDGE = (0, 72, 140, 255)
PAPER = (255, 255, 255, 255)
FOLD = (205, 222, 240, 255)
CAP = (32, 36, 44, 255)
TASSEL = (255, 196, 0, 255)
OUTPUT = Path(__file__).resolve().parents[1] / "src" / "OBK.App" / "Assets" / "OBK.ico"


def draw() -> Image.Image:
    image = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    d = ImageDraw.Draw(image)
    d.rounded_rectangle((40, 40, 984, 984), radius=210, fill=TILE_EDGE)
    d.rounded_rectangle((40, 40, 984, 960), radius=210, fill=TILE)

    # Envelope: body, lower folds and top flap.
    left, top, right, bottom = 196, 430, 828, 840
    d.rounded_rectangle((left, top, right, bottom), radius=48, fill=PAPER)
    mid_x, mid_y = (left + right) // 2, 660
    d.line([(left + 30, bottom - 24), (mid_x, mid_y), (right - 30, bottom - 24)], fill=FOLD, width=30, joint="curve")
    d.polygon([(left + 8, top + 18), (mid_x, mid_y), (right - 8, top + 18)], fill=FOLD)
    d.polygon([(left + 40, top + 6), (mid_x, mid_y - 40), (right - 40, top + 6)], fill=PAPER)

    # Graduation cap resting on the envelope.
    cx, cy = mid_x, 330
    d.polygon([(cx - 300, cy), (cx, cy - 130), (cx + 300, cy), (cx, cy + 130)], fill=CAP)
    d.rounded_rectangle((cx - 170, cy + 30, cx + 170, cy + 150), radius=40, fill=CAP)
    d.line([(cx, cy), (cx + 250, cy + 40), (cx + 250, cy + 200)], fill=TASSEL, width=22, joint="curve")
    d.ellipse((cx + 222, cy + 190, cx + 278, cy + 260), fill=TASSEL)
    d.ellipse((cx - 26, cy - 26, cx + 26, cy + 26), fill=TASSEL)
    return image


def main() -> None:
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    image = draw()
    sizes = [(s, s) for s in (16, 20, 24, 32, 40, 48, 64, 128, 256)]
    image.save(OUTPUT, format="ICO", sizes=sizes)
    print(f"Wrote {OUTPUT}")


if __name__ == "__main__":
    main()
