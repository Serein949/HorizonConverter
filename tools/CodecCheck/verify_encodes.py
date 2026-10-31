#!/usr/bin/env python3
"""Verify CodecCheck encoder output against the reference decoders.

Reads fixtures/manifest.json, decodes every cases/.../encoded.bin with texture2ddecoder
(BC1/BC3/ETC2 RGB/ETC2 RGBA) and computes PSNR against the source.  Also computes the PSNR of
the naive BC1 reference encoder from ttx_converter.py so the optimised encoder can be compared
against it on the same image.

Writes fixtures/results.json and prints a short summary.
"""
from __future__ import annotations

import importlib.util
import json
import math
import os
import sys

from PIL import Image

import texture2ddecoder as t2d

from gen_fixtures import bgra_to_rgba, REF_CONVERTER


def psnr_channel(src: bytes, dec: bytes, n: int, offset: int, stride: int) -> float:
    total = 0
    for i in range(n):
        d = src[i * stride + offset] - dec[i * stride + offset]
        total += d * d
    if total == 0:
        return 99.0
    mse = total / n
    return 10.0 * math.log10(255.0 * 255.0 / mse)


def psnr_rgb(src: bytes, dec: bytes, w: int, h: int) -> float:
    n = w * h
    total = 0
    for i in range(n):
        o = i * 4
        dr = src[o] - dec[o]
        dg = src[o + 1] - dec[o + 1]
        db = src[o + 2] - dec[o + 2]
        total += dr * dr + dg * dg + db * db
    if total == 0:
        return 99.0
    mse = total / (n * 3)
    return 10.0 * math.log10(255.0 * 255.0 / mse)


def psnr_alpha(src: bytes, dec: bytes, w: int, h: int) -> float:
    return psnr_channel(src, dec, w * h, 3, 4)


def decode_encoded(fmt: str, data: bytes, w: int, h: int) -> bytes:
    if fmt == "bc1":
        return bgra_to_rgba(t2d.decode_bc1(data, w, h))
    if fmt == "bc3":
        return bgra_to_rgba(t2d.decode_bc3(data, w, h))
    if fmt == "etc2rgb":
        return bgra_to_rgba(t2d.decode_etc2(data, w, h))
    if fmt == "etc2rgba":
        return bgra_to_rgba(t2d.decode_etc2a8(data, w, h))
    raise ValueError(fmt)


def load_ttx():
    spec = importlib.util.spec_from_file_location("ttx_converter_ref", REF_CONVERTER)
    mod = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(mod)
    return mod


def main() -> int:
    here = os.path.dirname(os.path.abspath(__file__))
    fixtures = os.path.abspath(sys.argv[1]) if len(sys.argv) > 1 else os.path.join(here, "fixtures")
    with open(os.path.join(fixtures, "manifest.json"), encoding="utf-8") as f:
        manifest = json.load(f)

    results = {"cases": [], "naive_bc1": None}

    def rd(rel):
        with open(os.path.join(fixtures, rel), "rb") as f:
            return f.read()

    naive_case = None
    for c in manifest["encode"]:
        fmt = c["format"]
        w, h = c["width"], c["height"]
        src = rd(c["source"])
        enc = rd(c["encoded"])
        dec = decode_encoded(fmt, enc, w, h)
        rgb = psnr_rgb(src, dec, w, h)
        alpha = psnr_alpha(src, dec, w, h) if fmt in ("bc3", "etc2rgba") else 99.0

        threshold_rgb = 12.0
        threshold_alpha = 12.0
        if c["kind"] == "transparent":
            threshold_rgb = 12.0
            threshold_alpha = 99.0
        elif w * h >= 65536 and c["kind"] == "photo":
            threshold_rgb = 30.0
            threshold_alpha = 35.0
        elif c["kind"] == "alpha":
            threshold_alpha = 30.0

        ok = rgb >= threshold_rgb and alpha >= threshold_alpha
        results["cases"].append({
            "name": c["name"], "format": fmt, "width": w, "height": h,
            "kind": c["kind"], "psnr_rgb": rgb, "psnr_alpha": alpha,
            "threshold_rgb": threshold_rgb, "threshold_alpha": threshold_alpha, "ok": ok,
        })
        if fmt == "bc1" and w * h >= 65536 and c["kind"] == "photo" and naive_case is None:
            naive_case = (c["name"], src, w, h, rgb)

    # Naive reference BC1 encoder comparison on the same source image.
    if naive_case is not None:
        name, src, w, h, ours = naive_case
        ttx = load_ttx()
        img = Image.frombytes("RGBA", (w, h), src)
        naive = ttx._encode_bc1(img)
        naive_dec = bgra_to_rgba(t2d.decode_bc1(naive, w, h))
        naive_psnr = psnr_rgb(src, naive_dec, w, h)
        results["naive_bc1"] = {
            "name": name, "w": w, "h": h,
            "naive_psnr_rgb": naive_psnr, "ours_psnr_rgb": ours, "beats_naive": ours > naive_psnr,
        }

    with open(os.path.join(fixtures, "results.json"), "w", encoding="utf-8") as f:
        json.dump(results, f, indent=1)

    for c in results["cases"]:
        print(f"  {c['name']:<32} RGB={c['psnr_rgb']:6.2f}  A={c['psnr_alpha']:6.2f}  "
              f"{'ok' if c['ok'] else 'FAIL'}")
    if results["naive_bc1"]:
        n = results["naive_bc1"]
        print(f"  naive BC1 reference: {n['naive_psnr_rgb']:.2f} dB, ours {n['ours_psnr_rgb']:.2f} dB")
    print("verify done")
    return 0 if all(c["ok"] for c in results["cases"]) else 1


if __name__ == "__main__":
    raise SystemExit(main())
