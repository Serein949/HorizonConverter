"""Generates the launcher icons for the Alpha and Public Android builds."""

import os
from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
S = 1024

DENSITIES = {
    "mipmap-mdpi": 48,
    "mipmap-hdpi": 72,
    "mipmap-xhdpi": 96,
    "mipmap-xxhdpi": 144,
    "mipmap-xxxhdpi": 192,
}

SCRIPT_FONTS = [
    r"C:\Windows\Fonts\segoescb.ttf",
    r"C:\Windows\Fonts\segoesc.ttf",
    r"C:\Windows\Fonts\georgiab.ttf",
]


def font(size):
    for path in SCRIPT_FONTS:
        if os.path.exists(path):
            return ImageFont.truetype(path, size)
    return ImageFont.load_default()


def gradient(size, top, bottom):
    img = Image.new("RGB", (1, size))
    for y in range(size):
        t = y / max(1, size - 1)
        img.putpixel((0, y), tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)))
    return img.resize((size, size))


def rounded_mask(size, radius):
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    return mask


def draw_mark(canvas_size, scale, badge):
    """Draws the arrow mark + badge into a transparent layer, sized to `scale` of canvas."""
    layer = Image.new("RGBA", (canvas_size, canvas_size), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)

    cx = cy = canvas_size / 2
    span = canvas_size * scale          # width of the mark
    accent = (0, 184, 255, 255)

    bar = span * 0.115                   # arrow shaft thickness
    head = span * 0.20                   # arrow head size

    def arrow(y, direction):
        x0, x1 = cx - span / 2, cx + span / 2
        if direction > 0:
            d.rounded_rectangle([x0, y - bar / 2, x1 - head * 0.9, y + bar / 2], radius=bar / 2, fill=accent)
            d.polygon([
                (x1, y),
                (x1 - head * 1.05, y - head * 0.72),
                (x1 - head * 1.05, y + head * 0.72),
            ], fill=accent)
        else:
            d.rounded_rectangle([x0 + head * 0.9, y - bar / 2, x1, y + bar / 2], radius=bar / 2, fill=accent)
            d.polygon([
                (x0, y),
                (x0 + head * 1.05, y - head * 0.72),
                (x0 + head * 1.05, y + head * 0.72),
            ], fill=accent)

    arrow(cy - span * 0.16, +1)
    arrow(cy + span * 0.16, -1)

    # Badge letter in the corner of the safe zone.
    if badge:
        # Round badge inside the adaptive safe zone (radius <= 0.33 * canvas).
        br = canvas_size * 0.105
        bx = cx + canvas_size * 0.215
        by = cy - canvas_size * 0.215
        d.ellipse([bx - br, by - br, bx + br, by + br], fill=(11, 18, 28, 255))
        d.ellipse([bx - br, by - br, bx + br, by + br], outline=accent, width=max(2, int(canvas_size * 0.006)))
        f = font(int(br * 1.5))
        box = d.textbbox((0, 0), badge, font=f)
        tw, th = box[2] - box[0], box[3] - box[1]
        d.text((bx - tw / 2 - box[0], by - th / 2 - box[1]), badge, font=f, fill=(238, 248, 255, 255))
    return layer


def build(badge, out_dir):
    base = gradient(S, (13, 20, 32), (18, 34, 48)).convert("RGBA")

    # soft cyan glow top-left, like the app background
    glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    gd.ellipse([-S * 0.25, -S * 0.35, S * 0.75, S * 0.55], fill=(0, 150, 220, 70))
    glow = glow.filter(ImageFilter.GaussianBlur(S * 0.09))
    base = Image.alpha_composite(base, glow)

    # faint texture grid
    grid = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    gd = ImageDraw.Draw(grid)
    step = S / 12
    for i in range(13):
        p = i * step
        gd.line([(p, 0), (p, S)], fill=(255, 255, 255, 12), width=max(1, int(S / 512)))
        gd.line([(0, p), (S, p)], fill=(255, 255, 255, 12), width=max(1, int(S / 512)))
    base = Image.alpha_composite(base, grid)

    # adaptive icon: background is full-bleed, foreground keeps the mark inside the safe zone
    background = base.copy()
    foreground = draw_mark(S, 0.52, badge)

    # legacy icon: rounded square with the mark
    legacy = Image.alpha_composite(base, draw_mark(S, 0.56, badge))
    legacy.putalpha(rounded_mask(S, int(S * 0.22)))
    round_icon = Image.alpha_composite(base, draw_mark(S, 0.56, badge))
    round_icon.putalpha(rounded_mask(S, int(S * 0.5) - int(S * 0.02)))

    for folder, px in DENSITIES.items():
        target = os.path.join(out_dir, folder)
        os.makedirs(target, exist_ok=True)
        legacy.resize((px, px), Image.LANCZOS).save(os.path.join(target, "appicon.png"))
        round_icon.resize((px, px), Image.LANCZOS).save(os.path.join(target, "appicon_round.png"))
        background.resize((px * 2, px * 2), Image.LANCZOS).convert("RGB").save(
            os.path.join(target, "appicon_background.png"))
        foreground.resize((px * 2, px * 2), Image.LANCZOS).save(os.path.join(target, "appicon_foreground.png"))

    legacy.resize((S, S), Image.LANCZOS).save(os.path.join(out_dir, "_preview_legacy.png"))
    foreground.resize((S, S), Image.LANCZOS).save(os.path.join(out_dir, "_preview_foreground.png"))


if __name__ == "__main__":
    build("\u03b1", os.path.join(ROOT, "src", "AeroflyConv.Android", "Resources"))
    build("P", os.path.join(ROOT, "src", "AeroflyConv.Public", "Resources"))
    print("icons written")
