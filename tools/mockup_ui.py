"""Render an approximate preview of the Android UI (design mockup, not a real screenshot).

Mirrors the dp values in src/AeroflyConv.Android/Resources/layout/activity_main.xml at 3x,
re-using the same colours and glass recipe so the direction can be reviewed without a device.

Usage: python mockup_ui.py <out_dir>
"""

from __future__ import annotations

import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

S = 3  # density scale: dp -> px
W, H = 360 * S, 800 * S

FONT_CJK = r"C:\Windows\Fonts\msyh.ttc"
FONT_CJK_BOLD = r"C:\Windows\Fonts\msyhbd.ttc"
FONT_SCRIPT = r"C:\Windows\Fonts\segoescb.ttf"
FONT_MONO = r"C:\Windows\Fonts\consola.ttf"

ACCENT = (79, 216, 255)
ACCENT_DIM = (143, 228, 255)
TEXT = (245, 248, 253)
TEXT_SOFT = (220, 230, 242)
TEXT_MUTED = (139, 152, 169)
TEXT_FAINT = (126, 139, 156)


def font(path: str, sp: float) -> ImageFont.FreeTypeFont:
    return ImageFont.truetype(path, int(round(sp * S)))


def dp(v: float) -> int:
    return int(round(v * S))


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(len(a)))


def rounded_mask(size, radius):
    mask = Image.new("L", size, 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [0, 0, max(1, size[0] - 1), max(1, size[1] - 1)], radius=radius, fill=255
    )
    return mask


def vertical_gradient(size, top, bottom):
    w, h = size
    grad = Image.new("RGB", (1, max(1, h)))
    gd = ImageDraw.Draw(grad)
    for i in range(max(1, h)):
        gd.point((0, i), lerp(top, bottom, i / max(1, h - 1)))
    return grad.resize((max(1, w), max(1, h)))


def wallpaper():
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    img = np.zeros((H, W, 3), dtype=np.float32)
    img[:] = (5, 7, 12)

    def glow(cx, cy, radius, colour, strength):
        r = np.sqrt((xx - cx * W) ** 2 + (yy - cy * H) ** 2)
        falloff = np.clip(1.0 - r / radius, 0.0, 1.0) ** 2.2
        return np.array(colour, dtype=np.float32) * (falloff * strength)[..., None]

    img += glow(0.16, 0.06, 620 * S, (0, 184, 255), 0.34)
    img += glow(0.92, 0.74, 560 * S, (62, 123, 255), 0.26)
    img = np.clip(img, 0, 255).astype(np.uint8)
    return Image.fromarray(img, "RGB")


def frost(base: Image.Image, box, radius):
    x0, y0, x1, y1 = box
    w, h = int(x1 - x0), int(y1 - y0)
    back = base.crop((int(x0), int(y0), int(x1), int(y1))).convert("RGB").filter(
        ImageFilter.GaussianBlur(radius=14 * S / 3)
    )
    pane = Image.blend(back, Image.new("RGB", (w, h), (255, 255, 255)), 0.10)

    sheen = Image.new("L", (w, h), 0)
    sd = ImageDraw.Draw(sheen)
    for i in range(h):
        t = i / max(1, h - 1)
        sd.line([(0, i), (w, i)], fill=int(255 * max(0.0, 0.26 * (1 - t / 0.5))))
    pane = Image.composite(Image.new("RGB", (w, h), (255, 255, 255)), pane, sheen)
    return pane, rounded_mask((w, h), radius)


def draw_glass(base: Image.Image, box, radius=26 * S):
    pane, mask = frost(base, box, radius)
    x0, y0 = int(box[0]), int(box[1])
    base.paste(pane, (x0, y0), mask)
    shadow = Image.new("RGBA", base.size, (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle(
        [x0, y0 + dp(4), int(box[2]), int(box[3]) + dp(4)], radius=radius, fill=(0, 0, 0, 90)
    )
    shadow = shadow.filter(ImageFilter.GaussianBlur(dp(7)))
    base.alpha_composite(shadow) if base.mode == "RGBA" else base.paste(
        Image.alpha_composite(base.convert("RGBA"), shadow).convert("RGB"), (0, 0)
    )
    ImageDraw.Draw(base).rounded_rectangle(
        box, radius=radius, outline=(255, 255, 255, 62), width=max(1, int(S / 3))
    )


def draw_track(layer: Image.Image, d: ImageDraw.ImageDraw, box, radius=22 * S):
    d.rounded_rectangle(box, radius=radius, fill=(255, 255, 255, 20))
    d.rounded_rectangle(box, radius=radius, outline=(255, 255, 255, 48), width=max(1, int(S / 3)))


def paste_glass_pill(layer: Image.Image, box, radius, top, bottom, outline):
    x0, y0, x1, y1 = [int(v) for v in box]
    grad = vertical_gradient((x1 - x0, y1 - y0), top, bottom)
    layer.paste(grad, (x0, y0), rounded_mask((x1 - x0, y1 - y0), radius))
    ImageDraw.Draw(layer).rounded_rectangle(
        (x0, y0, x1, y1), radius=radius, outline=outline, width=max(1, int(S / 3))
    )


def centre_text(d, box, text, fnt, colour):
    x0, y0, x1, y1 = box
    tw = d.textlength(text, font=fnt)
    d.text(((x0 + x1 - tw) / 2, (y0 + y1) / 2), text, font=fnt, fill=colour, anchor="lm")


def draw_switch(d, x, cy, on=True):
    w, h = dp(42), dp(24)
    box = [x, cy - h // 2, x + w, cy + h // 2]
    d.rounded_rectangle(box, radius=h // 2, fill=(0, 184, 255) if on else (60, 66, 78))
    r = dp(9)
    cx = x + w - dp(12) if on else x + dp(12)
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=(245, 250, 255))


def build(demo_files: bool) -> Image.Image:
    base = wallpaper().convert("RGBA")
    content = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(content, "RGBA")
    cards: list[tuple] = []

    pad = dp(20)
    x0, x1 = pad, W - pad
    y = dp(28)

    title_f = font(FONT_CJK_BOLD, 25)
    script_f = font(FONT_SCRIPT, 42)
    d.text((pad, y + dp(34)), "Aerofly 转换器", font=title_f, fill=TEXT, anchor="ls")
    tw = d.textlength("Aerofly 转换器", font=title_f)
    d.text((pad + tw + dp(10), y + dp(40)), "Alpha", font=script_f, fill=ACCENT, anchor="ls")
    y += dp(62)

    # segmented mode control
    y += dp(16)
    draw_track(content, d, (pad, y, x1, y + dp(46)))
    pf = font(FONT_CJK, 12.5)
    modes = ["自动识别", "TTX → PNG", "PNG → TTX"]
    x = pad + dp(5)
    for i, label in enumerate(modes):
        w = d.textlength(label, font=pf) + dp(28)
        box = (x, y + dp(5), x + w, y + dp(41))
        if i == 0:
            paste_glass_pill(content, box, dp(18), (0, 184, 255), (0, 132, 210), (120, 226, 255, 170))
            d = ImageDraw.Draw(content, "RGBA")
            centre_text(d, box, label, pf, (12, 26, 34))
        else:
            centre_text(d, box, label, pf, (198, 210, 224))
        x += w + dp(6)
    y += dp(46) + dp(10)
    d.text((pad, y), "根据文件扩展名自动判断转换方向。", font=font(FONT_CJK, 11.5), fill=TEXT_FAINT, anchor="la")
    y += dp(24)

    # ---- files card ----
    y += dp(16)
    files_top = y
    fpad = dp(16)
    iy = files_top + dp(16)
    d.text((x0 + fpad, iy), "文件", font=font(FONT_CJK, 10), fill=TEXT_MUTED, anchor="la")
    count = "3 个" if demo_files else "0 个"
    cw = d.textlength(count, font=font(FONT_CJK, 10.5))
    d.text((x1 - fpad - cw, iy), count, font=font(FONT_CJK, 10.5), fill=ACCENT, anchor="la")

    ry = iy + dp(24)
    if demo_files:
        row_f = font(FONT_CJK, 12)
        for name, size in [("fuselage1_color.png", "2.1 MB"), ("preview.png", "640 KB"), ("test.ttx", "22 KB")]:
            row = (x0 + fpad, ry, x1 - fpad, ry + dp(46))
            d.rounded_rectangle(row, radius=dp(16), fill=(255, 255, 255, 22))
            d.rounded_rectangle(row, radius=dp(16), outline=(255, 255, 255, 38), width=max(1, int(S / 3)))
            d.text((row[0] + dp(12), row[1] + dp(11)), name, font=row_f, fill=TEXT_SOFT, anchor="la")
            d.text((row[0] + dp(12), row[1] + dp(28)), size, font=font(FONT_CJK, 10), fill=TEXT_FAINT, anchor="la")
            d.text((row[2] - dp(16), row[1] + dp(23)), "✕", font=font(FONT_CJK, 13), fill=(120, 132, 148), anchor="mm")
            ry += dp(52)
    else:
        d.text(((x0 + x1) / 2, ry + dp(16)), "还没有文件", font=font(FONT_CJK, 12), fill=(107, 119, 136), anchor="mm")
        d.text(((x0 + x1) / 2, ry + dp(38)), "添加 .ttx / .tsb / .ttc / .tm / .png / .wav",
               font=font(FONT_CJK, 10.5), fill=(96, 108, 124), anchor="mm")
        ry += dp(66)

    ry += dp(4)
    bw = (x1 - fpad - (x0 + fpad) - dp(16)) / 3
    bf = font(FONT_CJK, 13)
    boxes = [
        (x0 + fpad, ry, x0 + fpad + bw, ry + dp(44)),
        (x0 + fpad + bw + dp(8), ry, x0 + fpad + 2 * bw + dp(8), ry + dp(44)),
        (x0 + fpad + 2 * (bw + dp(8)), ry, x1 - fpad, ry + dp(44)),
    ]
    for box in boxes:
        paste_glass_pill(content, box, dp(22), (74, 78, 88), (34, 36, 42), (255, 255, 255, 46))
    d = ImageDraw.Draw(content, "RGBA")
    centre_text(d, boxes[0], "添加文件", bf, TEXT)
    centre_text(d, boxes[1], "文件夹", bf, TEXT_SOFT)
    centre_text(d, boxes[2], "清空", bf, TEXT_MUTED)
    ry += dp(44) + dp(14)

    d.text((x0 + fpad, ry + dp(7)), "输出", font=font(FONT_CJK, 10), fill=TEXT_MUTED, anchor="la")
    d.text((x0 + fpad + dp(32), ry + dp(4)), "Download/AeroflyConv", font=font(FONT_MONO, 11),
           fill=(175, 188, 203), anchor="la")
    chip = (x1 - fpad - dp(56), ry - dp(2), x1 - fpad, ry + dp(28))
    d.rounded_rectangle(chip, radius=dp(14), fill=(255, 255, 255, 34), outline=(255, 255, 255, 48),
                        width=max(1, int(S / 3)))
    centre_text(d, chip, "更改", font(FONT_CJK, 11), ACCENT_DIM)
    ry += dp(40)
    cards.append((x0, files_top, x1, ry))

    # ---- options card ----
    y = ry + dp(18)
    opt_top = y
    oy = opt_top + dp(16)
    d.text((x0 + fpad, oy), "选项", font=font(FONT_CJK, 10), fill=TEXT_MUTED, anchor="la")
    oy += dp(28)
    label_f = font(FONT_CJK, 13)

    def pill_row(caption, items, selected, y_pos):
        nonlocal d
        d.text((x0 + fpad, y_pos), caption, font=label_f, fill=TEXT_SOFT, anchor="la")
        y_pos += dp(26)
        track = (x0 + fpad, y_pos, x1 - fpad, y_pos + dp(42))
        draw_track(content, d, track)
        px = track[0] + dp(4)
        for i, label in enumerate(items):
            w = d.textlength(label, font=pf) + dp(26)
            pbox = (px, y_pos + dp(4), px + w, y_pos + dp(38))
            if i == selected:
                paste_glass_pill(content, pbox, dp(18), (0, 184, 255), (0, 132, 210), (120, 226, 255, 170))
                d = ImageDraw.Draw(content, "RGBA")
                centre_text(d, pbox, label, pf, (12, 26, 34))
            else:
                centre_text(d, pbox, label, pf, (198, 210, 224))
            px += w + dp(6)
        return y_pos + dp(56)

    oy = pill_row("格式", ["自动", "DXT1", "DXT5", "ETC2", "R8"], 0, oy)
    oy = pill_row("贴图尺寸", ["匹配原尺寸", "4096", "2048"], 0, oy)
    d.line([x0 + fpad, oy, x1 - fpad, oy], fill=(255, 255, 255, 34), width=max(1, int(S / 3)))
    oy += dp(14)

    for title, sub, on in [("垂直翻转", "Aerofly 贴图行序自下而上", True),
                           ("压缩 TTX（LZHAM）", "与游戏内文件相同的容器", True)]:
        d.text((x0 + fpad, oy), title, font=label_f, fill=TEXT_SOFT, anchor="la")
        d.text((x0 + fpad, oy + dp(19)), sub, font=font(FONT_CJK, 11), fill=(122, 135, 152), anchor="la")
        draw_switch(d, x1 - fpad - dp(42), oy + dp(12), on)
        oy += dp(48)
    cards.append((x0, opt_top, x1, oy + dp(4)))

    # ---- convert + status ----
    y = oy + dp(22)
    btn = (x0, y, x1, y + dp(56))
    paste_glass_pill(content, btn, dp(28), (79, 216, 255), (10, 132, 255), (255, 255, 255, 140))
    d = ImageDraw.Draw(content, "RGBA")
    centre_text(d, btn, "开始转换", font(FONT_CJK_BOLD, 16.5), (6, 20, 28))
    y += dp(56) + dp(14)
    d.text(((x0 + x1) / 2, y), "还没有文件" if not demo_files else "已转换 3 个文件",
           font=font(FONT_CJK, 12), fill=(148, 161, 178), anchor="ma")
    y += dp(22)

    # ---- log card (collapsed) ----
    log_top = y + dp(6)
    lh = dp(46)
    d.text((x0 + fpad, log_top + dp(15)), "›", font=font(FONT_CJK, 15), fill=ACCENT_DIM, anchor="la")
    d.text((x0 + fpad + dp(18), log_top + dp(15)), "日志", font=font(FONT_CJK, 10), fill=TEXT_MUTED, anchor="la")
    d.text((x0 + fpad + dp(54), log_top + dp(15)), "7 条" if demo_files else "1 条",
           font=font(FONT_MONO, 10), fill=(92, 103, 121), anchor="la")
    for i, label in enumerate(["复制", "清空"]):
        w = d.textlength(label, font=font(FONT_CJK, 11)) + dp(20)
        right = x1 - fpad - i * (w + dp(6))
        cbox = (right - w, log_top + dp(10), right, log_top + dp(36))
        d.rounded_rectangle(cbox, radius=dp(13), fill=(255, 255, 255, 34), outline=(255, 255, 255, 46),
                            width=max(1, int(S / 3)))
        centre_text(d, cbox, label, font(FONT_CJK, 11), (198, 206, 218))
    cards.append((x0, log_top, x1, log_top + lh))

    for box in cards:
        draw_glass(base, box)

    base.alpha_composite(content)
    return base.convert("RGB")


if __name__ == "__main__":
    out_dir = sys.argv[1] if len(sys.argv) > 1 else "."
    os.makedirs(out_dir, exist_ok=True)
    build(False).save(os.path.join(out_dir, "preview_empty.png"))
    build(True).save(os.path.join(out_dir, "preview_files.png"))
    print("ok")
