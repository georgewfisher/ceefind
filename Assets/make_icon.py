"""Generates the CeeFind icon: a terminal prompt reading '>f'.

Drawn as type rather than as hand-built strokes. An earlier attempt built the 'f'
from an arc and two lines and never stopped looking like a dagger - a font
already solves the problem of what a letter should look like.

Rendered at 8x and reduced, which keeps the edges clean at every size.
"""

import os
import sys
from PIL import Image, ImageDraw, ImageFont

INK = (230, 237, 243, 255)      # near-white, the letter
ACCENT = (78, 201, 176, 255)    # teal, the prompt
BG = (30, 30, 30, 255)          # terminal background

SUPERSAMPLE = 8
FONT_CANDIDATES = ["consolab.ttf", "CascadiaMono.ttf", "cour.ttf", "segoeuib.ttf"]


def find_font() -> str:
    fonts = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts")
    for name in FONT_CANDIDATES:
        path = os.path.join(fonts, name)
        if os.path.exists(path):
            return path
    raise SystemExit("no suitable font found")


FONT_PATH = find_font()


def draw(size: int) -> Image.Image:
    s = size * SUPERSAMPLE
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    pad = s * 0.04
    d.rounded_rectangle([pad, pad, s - pad, s - pad], radius=s * 0.18, fill=BG)

    # Sized so '>f' fills the tile without crowding its edges.
    font = ImageFont.truetype(FONT_PATH, int(s * 0.62))

    # Measured and centred as a unit, so the pair sits true in the tile whatever
    # the font's own side bearings happen to be.
    text = ">f"
    left, top, right, bottom = d.textbbox((0, 0), text, font=font)
    x = (s - (right - left)) / 2 - left
    y = (s - (bottom - top)) / 2 - top

    # Drawn in two passes so the prompt and the letter can differ in colour, with
    # the 'f' placed by advance width to preserve the font's own spacing.
    d.text((x, y), ">", font=font, fill=ACCENT)
    d.text((x + d.textlength(">", font=font), y), "f", font=font, fill=INK)

    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)

    sizes = [16, 20, 24, 32, 40, 44, 48, 64, 128, 150, 256, 512]
    images = {n: draw(n) for n in sizes}
    for n, img in images.items():
        img.save(os.path.join(out, f"icon-{n}.png"))

    ico_sizes = [16, 24, 32, 48, 64, 128, 256]
    images[256].save(
        os.path.join(out, "ceefind.ico"),
        format="ICO",
        sizes=[(n, n) for n in ico_sizes],
    )

    print(f"font: {os.path.basename(FONT_PATH)}")
    print(f"wrote {len(sizes)} PNGs and ceefind.ico to {out}")


if __name__ == "__main__":
    main()
