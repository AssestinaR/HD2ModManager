# -*- coding: utf-8 -*-
"""正交 z-buffer 着色渲染 npz 里的 `ours_bind` 网格，四视图拼图。只读。

用途：转 patch 之前先自己肉眼看一眼几何是否正常（撕裂 / 拉长 / 部件飞出）。

usage:
    _probe_render_npz.py out/lilium/model.npz out/lilium-render.png [标题]
    _probe_render_npz.py A.npz B.npz --pair out/pair.png
"""
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

W, H = 460, 760
# (标签, 屏幕 x 轴, 屏幕 y 轴, 深度轴, 是否左右镜像)
# HD2 约定：+X 角色右 / +Y 前 / +Z 上
VIEWS = [('front (+Y)', 0, 2, 1, False),
         ('back  (-Y)', 0, 2, 1, True),
         ('left  (-X)', 1, 2, 0, False),
         ('top   (+Z)', 0, 1, 2, False)]


def render(pos, tris, view, bg=(24, 26, 30)):
    _tag, ax, ay, depth_axis, mirror = view
    # 相机沿 depth_axis 看，屏幕 x = ax 轴，y = ay 轴（翻转成图像坐标）
    px, py = pos[:, ax], pos[:, ay]
    if mirror:
        px = -px
    pz = pos[:, depth_axis]
    lo = np.array([px.min(), py.min()])
    hi = np.array([px.max(), py.max()])
    span = max(hi - lo)
    pad = 0.06 * span
    scale = (min(W, H) - 2 * pad * 0) / span * 0.92
    ox = (W - (hi[0] - lo[0]) * scale) / 2
    oy = (H - (hi[1] - lo[1]) * scale) / 2
    sx = (px - lo[0]) * scale + ox
    sy = H - ((py - lo[1]) * scale + oy)

    img = np.zeros((H, W, 3), np.float64)
    img[:, :] = bg
    zbuf = np.full((H, W), -1e18)

    # 面法线（用 3D 坐标算），光源从左上前方
    p0, p1, p2 = pos[tris[:, 0]], pos[tris[:, 1]], pos[tris[:, 2]]
    nrm = np.cross(p1 - p0, p2 - p0)
    ln = np.linalg.norm(nrm, axis=1, keepdims=True)
    nrm = nrm / np.maximum(ln, 1e-30)
    L = np.array([-0.4, 0.55, 0.73])
    L = L / np.linalg.norm(L)
    lam = np.abs(nrm @ L) * 0.72 + 0.28

    for i in range(len(tris)):
        xs = sx[tris[i]]
        ys = sy[tris[i]]
        zs = pz[tris[i]]
        x0, x1 = int(np.floor(xs.min())), int(np.ceil(xs.max()))
        y0, y1 = int(np.floor(ys.min())), int(np.ceil(ys.max()))
        if x1 < 0 or y1 < 0 or x0 >= W or y0 >= H:
            continue
        x0, x1 = max(x0, 0), min(x1, W - 1)
        y0, y1 = max(y0, 0), min(y1, H - 1)
        if x0 > x1 or y0 > y1:
            continue
        gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + 0.5,
                             np.arange(y0, y1 + 1) + 0.5)
        d = ((xs[1] - xs[0]) * (ys[2] - ys[0])
             - (xs[2] - xs[0]) * (ys[1] - ys[0]))
        if abs(d) < 1e-12:
            continue
        w0 = ((xs[1] - gx) * (ys[2] - gy) - (xs[2] - gx) * (ys[1] - gy)) / d
        w1 = ((xs[2] - gx) * (ys[0] - gy) - (xs[0] - gx) * (ys[2] - gy)) / d
        w2 = 1.0 - w0 - w1
        m = (w0 >= -1e-9) & (w1 >= -1e-9) & (w2 >= -1e-9)
        if not m.any():
            continue
        z = w0 * zs[0] + w1 * zs[1] + w2 * zs[2]
        sub = zbuf[y0:y1 + 1, x0:x1 + 1]
        hit = m & (z > sub)
        if not hit.any():
            continue
        s = float(lam[i]) * 255.0
        sub[hit] = z[hit]
        img[y0:y1 + 1, x0:x1 + 1][hit] = (s * 0.92, s * 0.94, s)
    return Image.fromarray(np.clip(img, 0, 255).astype(np.uint8))


def sheet(npz, title):
    d = np.load(npz)
    pos = np.asarray(d['ours_bind'], np.float64)
    tris = np.asarray(d['tris'], np.int64)
    canvas = Image.new('RGB', (W * 4, H + 34), (16, 17, 20))
    dr = ImageDraw.Draw(canvas)
    for i, v in enumerate(VIEWS):
        canvas.paste(render(pos, tris, v), (W * i, 34))
        dr.text((W * i + 10, 11), v[0], fill=(210, 214, 220))
    dr.text((10, 11), '%s   %d verts / %d tris' % (title, len(pos), len(tris)),
            fill=(255, 214, 120))
    return canvas


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    if '--pair' in sys.argv:
        out = sys.argv[sys.argv.index('--pair') + 1]
        npzs = [a for a in sys.argv[1:] if not a.startswith('--') and a != out]
        imgs = [sheet(p, os.path.basename(os.path.dirname(p)) or p) for p in npzs]
        wmax = max(i.width for i in imgs)
        canvas = Image.new('RGB', (wmax, sum(i.height for i in imgs)),
                           (16, 17, 20))
        y = 0
        for im in imgs:
            canvas.paste(im, (0, y))
            y += im.height
        canvas.save(out)
        print('[written] %s  (%dx%d)' % (out, canvas.width, canvas.height))
        return 0
    npz, out = args[0], args[1]
    title = args[2] if len(args) > 2 else os.path.basename(os.path.dirname(npz))
    im = sheet(npz, title)
    im.save(out)
    print('[written] %s  (%dx%d)' % (out, im.width, im.height))
    return 0


if __name__ == '__main__':
    sys.exit(main())
