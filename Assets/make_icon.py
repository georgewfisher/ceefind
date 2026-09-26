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
    return draw_tile(size, size)


def draw_tile(width: int, height: int) -> Image.Image:
    """Draws the mark centred on a tile, which need not be square.

    The wide tile the Store asks for is 310x150, so the mark is sized against the
    shorter edge and centred, rather than stretched to fill.
    """
    w, h = width * SUPERSAMPLE, height * SUPERSAMPLE
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    short = min(w, h)
    pad = short * 0.04
    d.rounded_rectangle([pad, pad, w - pad, h - pad], radius=short * 0.18, fill=BG)

    font = ImageFont.truetype(FONT_PATH, int(short * 0.62))

    text = ">f"
    left, top, right, bottom = d.textbbox((0, 0), text, font=font)
    x = (w - (right - left)) / 2 - left
    y = (h - (bottom - top)) / 2 - top

    d.text((x, y), ">", font=font, fill=ACCENT)
    d.text((x + d.textlength(">", font=font), y), "f", font=font, fill=INK)

    return img.resize((width, height), Image.LANCZOS)


def main() -> None:
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)

    sizes = [16, 20, 24, 30, 32, 36, 40, 44, 48, 50, 60, 64, 71, 89, 96, 128, 150, 256, 310, 512]
    images = {n: draw(n) for n in sizes}
    for n, img in images.items():
        img.save(os.path.join(out, f"icon-{n}.png"))

    # The Store's wide tile is the one asset that is not square.
    draw_tile(310, 150).save(os.path.join(out, "wide-310x150.png"))

    ico_sizes = [16, 24, 32, 48, 64, 128, 256]
    images[256].save(
        os.path.join(out, "ceefind.ico"),
        format="ICO",
        sizes=[(n, n) for n in ico_sizes],
    )

    print(f"font: {os.path.basename(FONT_PATH)}")
    print(f"wrote {len(sizes)} PNGs, a wide tile and ceefind.ico to {out}")


if __name__ == "__main__":
    main()
