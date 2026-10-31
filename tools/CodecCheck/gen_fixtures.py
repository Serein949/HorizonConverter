#!/usr/bin/env python3
"""Generate deterministic texture fixtures for CodecCheck.

Writes compressed block data, the expected decode (produced by the reference decoders)
and RGBA source images used by the C# encoder round-trip tests.

Reference decoders used:
  * texture2ddecoder.decode_bc1 / decode_bc3 / decode_bc4 / decode_bc5 /
    decode_etc1 / decode_etc2 / decode_etc2a8  (Perfare's Texture2DDecoder)
  * the pure-python _decode_dxt3() from the reference ttx_converter.py for BC2
Compression for the decode fixtures comes from etcpak (BC1/BC3/BC4/BC5/ETC1/ETC2/ETC2-RGBA)
plus purely random block payloads to exercise every branch of the ETC decoders.

Usage: python gen_fixtures.py [fixtures_dir]
"""
from __future__ import annotations

import importlib.util
import json
import os
import random
import sys

from PIL import Image, ImageChops, ImageDraw

import etcpak
import texture2ddecoder as t2d


REF_CONVERTER = r"C:\Users\17353\AppData\Local\Temp\aero-ref\AeroflyFS-Native-Converter-main\ttx_converter.py"


def load_ttx():
    if not os.path.isfile(REF_CONVERTER):
        raise RuntimeError("reference converter not found: " + REF_CONVERTER)
    spec = importlib.util.spec_from_file_location("ttx_converter_ref", REF_CONVERTER)
    mod = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(mod)
    return mod


def bgra_to_rgba(bgra: bytes) -> bytes:
    out = bytearray(len(bgra))
    out[0::4] = bgra[2::4]
    out[1::4] = bgra[1::4]
    out[2::4] = bgra[0::4]
    out[3::4] = bgra[3::4]
    return bytes(out)


def ref_decode(fmt: str, data: bytes, w: int, h: int, ttx) -> bytes:
    if fmt == "bc1":
        return bgra_to_rgba(t2d.decode_bc1(data, w, h))
    if fmt == "bc2":
        return bytes(ttx._decode_dxt3(data, w, h))
    if fmt == "bc3":
        return bgra_to_rgba(t2d.decode_bc3(data, w, h))
    if fmt == "bc4":
        bgra = t2d.decode_bc4(data, w, h)
        return bytes(bgra[2::4])  # BC4 value ends up in the R byte of the BGRA result
    if fmt == "bc5":
        bgra = t2d.decode_bc5(data, w, h)
        out = bytearray(w * h * 2)
        out[0::2] = bgra[2::4]  # X block -> R
        out[1::2] = bgra[1::4]  # Y block -> G
        return bytes(out)
    if fmt == "etc1":
        return bgra_to_rgba(t2d.decode_etc1(data, w, h))
    if fmt == "etc2rgb":
        return bgra_to_rgba(t2d.decode_etc2(data, w, h))
    if fmt == "etc2rgba":
        return bgra_to_rgba(t2d.decode_etc2a8(data, w, h))
    raise ValueError(fmt)


def compress(fmt: str, rgba: bytes, w: int, h: int) -> bytes:
    if fmt == "bc1":
        return etcpak.compress_bc1(rgba, w, h)
    if fmt == "bc3":
        return etcpak.compress_bc3(rgba, w, h)
    if fmt == "bc4":
        return etcpak.compress_bc4(rgba, w, h)
    if fmt == "bc5":
        return etcpak.compress_bc5(rgba, w, h)
    if fmt == "etc1":
        return etcpak.compress_etc1_rgb(rgba, w, h)
    if fmt == "etc2rgb":
        return etcpak.compress_etc2_rgb(rgba, w, h)
    if fmt == "etc2rgba":
        return etcpak.compress_etc2_rgba(rgba, w, h)
    raise ValueError(fmt)


def block_bytes(fmt: str) -> int:
    return {"bc1": 8, "bc2": 16, "bc3": 16, "bc4": 8, "bc5": 16,
            "etc1": 8, "etc2rgb": 8, "etc2rgba": 16}[fmt]


def _noise(w: int, h: int, rnd: random.Random, cell: int) -> Image.Image:
    nw = max(1, (w + cell - 1) // cell)
    nh = max(1, (h + cell - 1) // cell)
    data = bytes(rnd.randrange(256) for _ in range(nw * nh))
    return Image.frombytes("L", (nw, nh), data).resize((w, h), Image.BICUBIC)


def make_photo(w: int, h: int, seed: int, alpha_mode: str | None = None) -> Image.Image:
    """Photo-like deterministic image: smooth gradients, multi-octave noise, shapes."""
    rnd = random.Random(seed)
    gy = Image.linear_gradient("L").resize((w, h), Image.BICUBIC)
    gx = gy.rotate(90)
    n1 = _noise(w, h, rnd, 32)
    n2 = _noise(w, h, rnd, 8)
    n3 = _noise(w, h, rnd, 3)

    def channel(base_lo, base_hi, grad, seed_shift):
        r = ImageChops.add(grad.point(lambda v: base_lo + (base_hi - base_lo) * v // 255),
                           n1.point(lambda v: v // 3))
        r = ImageChops.add(r, n2.point(lambda v: v // 6))
        r = ImageChops.add(r, n3.point(lambda v: v // 10))
        if seed_shift:
            r = ImageChops.add(r, Image.new("L", (w, h), seed_shift))
        return r

    red = channel(30, 190, gy, 0)
    green = channel(40, 170, gx, 15)
    blue = channel(50, 210, gy.rotate(180), 30)
    img = Image.merge("RGB", (red, green, blue))

    draw = ImageDraw.Draw(img)
    for _ in range(5):
        x0 = rnd.randrange(0, max(1, w - 4))
        y0 = rnd.randrange(0, max(1, h - 4))
        x1 = min(w - 1, x0 + rnd.randrange(4, max(5, w // 2)))
        y1 = min(h - 1, y0 + rnd.randrange(4, max(5, h // 2)))
        colour = (rnd.randrange(256), rnd.randrange(256), rnd.randrange(256))
        if rnd.random() < 0.5:
            draw.ellipse([x0, y0, x1, y1], fill=colour)
        else:
            draw.rectangle([x0, y0, x1, y1], fill=colour)

    if alpha_mode is not None:
        if alpha_mode == "transparent":
            img.putalpha(Image.new("L", (w, h), 0))
        elif alpha_mode == "gradient":
            a = gy.point(lambda v: 40 + 215 * v // 255)
            a = ImageChops.add(a, n2.point(lambda v: v // 12))
            ad = ImageDraw.Draw(a)
            ad.ellipse([w // 8, h // 8, w // 2, h // 2], fill=0)
            ad.rectangle([w // 2, h // 2, 3 * w // 4, 3 * h // 4], fill=255)
            img.putalpha(a)
        else:
            raise ValueError(alpha_mode)
    return img.convert("RGBA")


def write_bytes(path: str, data: bytes) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(data)


def main() -> int:
    here = os.path.dirname(os.path.abspath(__file__))
    out_dir = os.path.abspath(sys.argv[1]) if len(sys.argv) > 1 else os.path.join(here, "fixtures")
    if os.path.isdir(out_dir):
        import shutil
        shutil.rmtree(out_dir)
    os.makedirs(out_dir)

    ttx = load_ttx()
    manifest = {"decode": [], "encode": []}

    sizes = [(4, 4), (8, 8), (16, 4), (64, 32), (256, 256)]
    formats = ["bc1", "bc2", "bc3", "bc4", "bc5", "etc1", "etc2rgb", "etc2rgba"]

    case_id = 0
    for fmt in formats:
        for (w, h) in sizes:
            # BC2 has no etcpak compressor; random bytes are valid DXT3 blocks.
            if fmt == "bc2":
                data = bytes(random.Random(hash((fmt, w, h)) & 0xFFFFFFFF).randrange(256)
                             for _ in range((w // 4) * (h // 4) * 16))
            else:
                rgba = make_photo(w, h, seed=1000 + case_id, alpha_mode=(None if fmt not in
                                    ("bc3", "bc4", "bc5", "etc2rgba") else "gradient")).tobytes()
                data = compress(fmt, rgba, w, h)

            expected = ref_decode(fmt, data, w, h, ttx)
            cid = f"dec_{fmt}_{w}x{h}_{case_id}"
            case_id += 1
            rel = os.path.join("cases", cid)
            write_bytes(os.path.join(out_dir, rel, "data.bin"), data)
            write_bytes(os.path.join(out_dir, rel, "expected.bin"), expected)
            manifest["decode"].append({
                "name": cid, "format": fmt, "width": w, "height": h,
                "data": os.path.join(rel, "data.bin").replace("\\", "/"),
                "expected": os.path.join(rel, "expected.bin").replace("\\", "/"),
            })

    # Random block payloads: exercise every mode/escape branch of the ETC decoders and the
    # 3-colour branch of BC1.  BC2 included as well.
    rnd = random.Random(424242)
    for fmt in formats:
        w, h = (64, 32) if fmt != "bc2" else (64, 32)
        n = (w // 4) * (h // 4) * block_bytes(fmt)
        data = bytes(rnd.randrange(256) for _ in range(n))
        expected = ref_decode(fmt, data, w, h, ttx)
        cid = f"dec_{fmt}_random_{w}x{h}"
        rel = os.path.join("cases", cid)
        write_bytes(os.path.join(out_dir, rel, "data.bin"), data)
        write_bytes(os.path.join(out_dir, rel, "expected.bin"), expected)
        manifest["decode"].append({
            "name": cid, "format": fmt, "width": w, "height": h,
            "data": os.path.join(rel, "data.bin").replace("\\", "/"),
            "expected": os.path.join(rel, "expected.bin").replace("\\", "/"),
        })

    # ---- encoder round-trip sources -----------------------------------------------------
    encode_specs = []
    eid = 0
    for fmt in ("bc1", "bc3", "etc2rgb", "etc2rgba"):
        for (w, h) in sizes:
            alpha_mode = "gradient" if fmt in ("bc3", "etc2rgba") else None
            img = make_photo(w, h, seed=7000 + eid, alpha_mode=alpha_mode)
            encode_specs.append((fmt, w, h, img, "photo", eid))
            eid += 1
        # Edge/alpha coverage.
        for (w, h, am, kind) in [(64, 32, "transparent", "transparent"),
                                 (64, 32, "gradient", "alpha")]:
            if fmt in ("bc3", "etc2rgba"):
                img = make_photo(w, h, seed=9000 + eid, alpha_mode=am)
                encode_specs.append((fmt, w, h, img, kind, eid))
                eid += 1

    for (fmt, w, h, img, kind, idx) in encode_specs:
        cid = f"enc_{fmt}_{w}x{h}_{kind}"
        rel = os.path.join("cases", cid)
        write_bytes(os.path.join(out_dir, rel, "source.bin"), img.tobytes())
        manifest["encode"].append({
            "name": cid, "format": fmt, "width": w, "height": h, "kind": kind,
            "source": os.path.join(rel, "source.bin").replace("\\", "/"),
            "encoded": os.path.join(rel, "encoded.bin").replace("\\", "/"),
        })

    with open(os.path.join(out_dir, "manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=1)

    print(f"fixtures: {len(manifest['decode'])} decode cases, "
          f"{len(manifest['encode'])} encode cases -> {out_dir}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
