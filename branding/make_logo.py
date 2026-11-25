"""Draws the Tunefin logo: an eighth note whose flag is a fin.

Outputs (run from the repo root: python3 branding/make_logo.py):
  branding/icon.png    512x512 square icon (channel image)
  branding/banner.png  1280x720 plugin-catalog image (icon + wordmark)
Needs Pillow. The wordmark uses Avenir Next (macOS); any bold sans works.
"""
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

OUT = Path(__file__).resolve().parent
S = 4  # supersampling factor

DEEP = (14, 22, 48)       # night navy
TOP = (31, 60, 140)       # indigo
BOTTOM = (10, 132, 150)   # sea teal
WHITE = (255, 255, 255)
FOAM = (166, 236, 240)


def bezier(p0, p1, p2, p3, n=64):
    pts = []
    for i in range(n + 1):
        t = i / n
        a, b, c, d = (1 - t) ** 3, 3 * (1 - t) ** 2 * t, 3 * (1 - t) * t ** 2, t ** 3
        pts.append((a * p0[0] + b * p1[0] + c * p2[0] + d * p3[0],
                    a * p0[1] + b * p1[1] + c * p2[1] + d * p3[1]))
    return pts


def gradient(size, top, bottom):
    w, h = size
    g = Image.new("RGB", (1, 256))
    for y in range(256):
        t = y / 255
        g.putpixel((0, y), tuple(round(top[i] + (bottom[i] - top[i]) * t) for i in range(3)))
    return g.resize((w, h), Image.BICUBIC)


def note_mask(size, scale=1.0, ox=0, oy=0):
    """The mark on a 512 grid: tilted note head, stem, and a fin-shaped flag."""
    w, h = size
    m = Image.new("L", (w, h), 0)
    d = ImageDraw.Draw(m)
    k = scale * S

    def P(x, y):
        return (ox * S + x * k, oy * S + y * k)

    # note head: a tilted ellipse, drawn upright then rotated
    hw, hh = 150 * k, 108 * k
    head = Image.new("L", (int(hw * 1.6), int(hw * 1.6)), 0)
    hd = ImageDraw.Draw(head)
    cx, cy = head.width / 2, head.height / 2
    hd.ellipse((cx - hw / 2, cy - hh / 2, cx + hw / 2, cy + hh / 2), fill=255)
    head = head.rotate(24, resample=Image.BICUBIC)
    hx, hy = P(196, 378)
    m.paste(255, (int(hx - head.width / 2), int(hy - head.height / 2)), head)

    # stem
    sx0, sy0 = P(244, 118)
    sx1, sy1 = P(270, 372)
    d.rectangle((sx0, sy0, sx1, sy1), fill=255)

    # flag as a dorsal fin: convex leading edge out to the tip, concave trailing edge back
    lead = bezier(P(244, 112), P(300, 104), P(392, 150), P(404, 292))
    trail = bezier(P(404, 292), P(372, 236), P(316, 214), P(258, 222))
    d.polygon(lead + trail[1:] + [P(244, 230)], fill=255)
    return m


def icon(px=512):
    big = (px * S, px * S)
    bg = gradient(big, TOP, BOTTOM)
    # rounded-square tile
    tile = Image.new("L", big, 0)
    r = int(px * 0.22 * S)
    ImageDraw.Draw(tile).rounded_rectangle((0, 0, big[0] - 1, big[1] - 1), radius=r, fill=255)
    img = Image.new("RGBA", big, (0, 0, 0, 0))
    img.paste(bg, (0, 0), tile)

    # a faint wave line low in the tile, like a waterline
    wave = Image.new("L", big, 0)
    wd = ImageDraw.Draw(wave)
    k = px / 512 * S
    pts = bezier((40 * k, 446 * k), (160 * k, 410 * k), (300 * k, 482 * k), (472 * k, 436 * k))
    wd.line(pts, fill=70, width=int(10 * k), joint="curve")
    img.paste(FOAM, (0, 0), Image.composite(wave, Image.new("L", big, 0), tile))

    mask = note_mask(big, scale=px / 512 * 0.92, ox=px * 0.045, oy=px * 0.0)
    shadow = mask.filter(ImageFilter.GaussianBlur(10 * S * px / 512)).point(lambda v: v * 0.35)
    img.paste(DEEP, (int(6 * k), int(10 * k)), shadow)
    img.paste(WHITE, (0, 0), mask)
    return img.resize((px, px), Image.LANCZOS)


def banner(w=1280, h=720):
    big = (w * S, h * S)
    img = gradient(big, DEEP, (16, 40, 78)).convert("RGBA")
    ic = icon(360).resize((360 * S, 360 * S), Image.LANCZOS)
    img.paste(ic, (120 * S, (h - 360) // 2 * S), ic)
    d = ImageDraw.Draw(img)
    try:
        title = ImageFont.truetype("/System/Library/Fonts/Avenir Next.ttc", 150 * S, index=0)  # Bold
        sub = ImageFont.truetype("/System/Library/Fonts/Avenir Next.ttc", 44 * S, index=5)    # Medium
    except OSError:
        title = ImageFont.load_default(150 * S)
        sub = ImageFont.load_default(44 * S)
    tx = 540 * S
    d.text((tx, 230 * S), "Tunefin", font=title, fill=WHITE)
    d.text((tx + 6 * S, 430 * S), "YouTube Music in Jellyfin", font=sub, fill=FOAM)
    return img.resize((w, h), Image.LANCZOS).convert("RGB")


if __name__ == "__main__":
    icon().save(OUT / "icon.png")
    banner().save(OUT / "banner.png")
    print("wrote", OUT / "icon.png", OUT / "banner.png")
