"""Tiny OBJ preview renderer used to sanity-check the TMB exporter.

Orthographic, z-buffered, nearest-neighbour texturing, flat-ish shading.
Usage: python render_obj.py <model.obj> <out.png> [width] [height]
"""
import math
import os
import sys

import numpy as np
from PIL import Image


def parse_obj(path):
    verts, uvs, normals = [], [], []
    faces = []          # (v0,v1,v2, t0,t1,t2, material)
    material = None
    materials = {}
    for raw in open(path, encoding="utf-8", errors="replace"):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        if line.startswith("v "):
            _, x, y, z = line.split()[:4]
            verts.append((float(x), float(y), float(z)))
        elif line.startswith("vt "):
            parts = line.split()
            uvs.append((float(parts[1]), float(parts[2]) if len(parts) > 2 else 0.0))
        elif line.startswith("vn "):
            parts = line.split()
            normals.append((float(parts[1]), float(parts[2]), float(parts[3])))
        elif line.startswith("usemtl "):
            material = line.split(None, 1)[1].strip()
        elif line.startswith("f "):
            idx = []
            for token in line.split()[1:]:
                bits = token.split("/")
                v = int(bits[0]) - 1
                t = int(bits[1]) - 1 if len(bits) > 1 and bits[1] else -1
                idx.append((v, t))
            for i in range(1, len(idx) - 1):
                a, b, c = idx[0], idx[i], idx[i + 1]
                faces.append((a[0], b[0], c[0], a[1], b[1], c[1], material))
    mtl = {}
    mtl_path = os.path.splitext(path)[0] + ".mtl"
    if os.path.exists(mtl_path):
        name = None
        for raw in open(mtl_path, encoding="utf-8", errors="replace"):
            line = raw.strip()
            if line.startswith("newmtl "):
                name = line.split(None, 1)[1].strip()
                mtl[name] = {}
            elif line.startswith("map_Kd ") and name:
                mtl[name]["map"] = line.split(None, 1)[1].strip()
    return verts, uvs, faces, mtl


def main():
    src = sys.argv[1]
    out = sys.argv[2]
    width = int(sys.argv[3]) if len(sys.argv) > 3 else 900
    height = int(sys.argv[4]) if len(sys.argv) > 4 else 620

    verts, uvs, faces, mtl = parse_obj(src)
    v = np.array(verts, dtype=np.float64)
    print(f"{len(v)} vertices, {len(faces)} triangles")

    # Z-up model: view from front-right-above, orthographic.
    center = (v.min(axis=0) + v.max(axis=0)) / 2
    extent = float(np.max(v.max(axis=0) - v.min(axis=0))) or 1.0
    yaw = math.radians(-35.0)
    pitch = math.radians(18.0)
    fwd = np.array([math.cos(pitch) * math.cos(yaw), math.cos(pitch) * math.sin(yaw), -math.sin(pitch)])
    fwd /= np.linalg.norm(fwd)
    up_hint = np.array([0.0, 0.0, 1.0])
    right = np.cross(up_hint, fwd)
    right /= np.linalg.norm(right)
    up = np.cross(fwd, right)
    light = np.array([-0.4, -0.6, 0.7])
    light /= np.linalg.norm(light)

    scale = (min(width, height) * 0.92) / extent
    rel = (v - center)
    px = rel @ right * scale + width / 2
    py = -(rel @ up) * scale + height / 2
    depth = rel @ fwd

    color = np.zeros((height, width, 3), dtype=np.float32)
    color[:] = (0.05, 0.06, 0.08)
    zbuf = np.full((height, width), np.inf, dtype=np.float64)

    textures = {}
    order = np.argsort([1 if (f[6] and "map" in mtl.get(f[6], {})) else 0 for f in faces])
    for fi in order:
        v0, v1, v2, t0, t1, t2, mat = faces[fi]
        x = np.array([px[v0], px[v1], px[v2]])
        y = np.array([py[v0], py[v1], py[v2]])
        z = np.array([depth[v0], depth[v1], depth[v2]])
        minx, maxx = int(max(0, math.floor(x.min()))), int(min(width - 1, math.ceil(x.max())))
        miny, maxy = int(max(0, math.floor(y.min()))), int(min(height - 1, math.ceil(y.max())))
        if minx > maxx or miny > maxy:
            continue
        area = (x[1] - x[0]) * (y[2] - y[0]) - (x[2] - x[0]) * (y[1] - y[0])
        if abs(area) < 1e-9:
            continue
        gx, gy = np.meshgrid(np.arange(minx, maxx + 1) + 0.5, np.arange(miny, maxy + 1) + 0.5)
        w0 = ((x[1] - gx) * (y[2] - gy) - (x[2] - gx) * (y[1] - gy)) / area
        w1 = ((x[2] - gx) * (y[0] - gy) - (x[0] - gx) * (y[2] - gy)) / area
        w2 = 1.0 - w0 - w1
        inside = (w0 >= 0) & (w1 >= 0) & (w2 >= 0)
        if not inside.any():
            continue
        zz = w0 * z[0] + w1 * z[1] + w2 * z[2]
        sub = zbuf[miny:maxy + 1, minx:maxx + 1]
        closer = inside & (zz < sub)
        if not closer.any():
            continue
        info = mtl.get(mat or "", {})
        tex = None
        if t0 >= 0 and t1 >= 0 and t2 >= 0 and info.get("map"):
            key = info["map"]
            if key not in textures:
                p = os.path.join(os.path.dirname(src), key.replace("/", os.sep))
                textures[key] = np.asarray(Image.open(p).convert("RGB"), dtype=np.float32) / 255.0 if os.path.exists(p) else None
            tex = textures[key]
        shade = 0.35 + 0.65 * max(0.0, abs(float(light[2])))
        if tex is not None:
            uv = np.array([uvs[t0], uvs[t1], uvs[t2]], dtype=np.float64) if max(t0, t1, t2) < len(uvs) else None
            if uv is not None:
                u = w0 * uv[0, 0] + w1 * uv[1, 0] + w2 * uv[2, 0]
                vv = w0 * uv[0, 1] + w1 * uv[1, 1] + w2 * uv[2, 1]
                th, tw = tex.shape[:2]
                ti = np.clip((vv % 1.0) * th, 0, th - 1).astype(np.int32)
                tj = np.clip((u % 1.0) * tw, 0, tw - 1).astype(np.int32)
                sampled = tex[ti, tj]
                block = color[miny:maxy + 1, minx:maxx + 1]
                block[closer] = sampled[closer] * shade
                color[miny:maxy + 1, minx:maxx + 1] = block
            else:
                color[miny:maxy + 1, minx:maxx + 1][closer] = np.array([0.7, 0.7, 0.72]) * shade
        else:
            block = color[miny:maxy + 1, minx:maxx + 1]
            block[closer] = np.array([0.62, 0.66, 0.72]) * shade
            color[miny:maxy + 1, minx:maxx + 1] = block
        sub[closer] = zz[closer]

    img = Image.fromarray((np.clip(color, 0, 1) * 255).astype(np.uint8))
    img.save(out)
    print("saved", out)


if __name__ == "__main__":
    main()
