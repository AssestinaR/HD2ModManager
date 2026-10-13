# -*- coding: utf-8 -*-
"""手部特写 + HD2 骨骼叠加：方案 B 的目视验收图。

`_probe_gainfit.py` 给的是数字（顶点云质心差），但"手指看起来正不正常"是
目视问题。这张图把**出货网格**的左肩->左手区域放大，并把 HD2 自己的手指骨
**画在网格上**：

  * 骨骼线段穿过手指几何  -> 绑定正确，手指会绕自己的关节弯；
  * 骨骼线段跑到手指外面  -> 就是用户看到的"手指不正常"（弯的时候会甩出去）。

三行 = 参考(Lunalice) / 方案B之前 / 方案B之后，三列 = 正视(X-Z) 侧视(Y-Z) 俯视(X-Y)。

usage: _probe_hand_zoom.py [out.png]
"""
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import export_obj as E
import _probe_hops as PH
import _probe_out_hands as OH

W, H = 470, 470
RUNS = [('Lunalice (ref)', 'out/blender'),
        ('Lilium  before B', 'out/lilium'),
        ('Lilium  after  B', 'out/lilium-b')]
VIEWS = [('front X-Z', 0, 2), ('side Y-Z', 1, 2), ('top X-Y', 0, 1)]
BONE_COL = {'thumb': (255, 190, 60), 'index': (110, 235, 130),
            'middle': (90, 200, 255), 'ring': (215, 130, 255),
            'pinky': (255, 110, 110), 'hand': (255, 255, 255)}
BG = (18, 19, 23)


def bone_segments(hd2):
    """-> list of (name, p0, p1) for the LEFT hand + finger bones.

    The direction comes from the CHILD's head, not from the bind matrix -- the
    matrix's 3x3 block is not the bone orientation in this rig (the first
    attempt read a 1.1 m "finger" out of it).
    """
    head = {}
    for h in hd2:
        head[h['index']] = PH.ST.origin_np(PH.ST.m_to_np(h['world']))
    kids = {}
    for h in hd2:
        p = h.get('parent')
        if p is not None and p >= 0 and p != h['index'] and p in head:
            kids.setdefault(p, []).append(h['index'])
    out = []
    for h in hd2:
        nm = h['name'] or ''
        if not nm.startswith('l_'):
            continue
        key = None
        for k in BONE_COL:
            if k in nm:
                key = k
                break
        if key is None:
            continue
        p0 = head[h['index']]
        ks = [c for c in kids.get(h['index'], []) if c in head]
        if ks:
            p1 = head[ks[0]]
        else:                       # a leaf (finger tip): extend the last step
            par = h.get('parent')
            p1 = p0 + (p0 - head[par]) if par in head else p0 + np.array(
                [0.01, 0.0, 0.0])
        out.append((key, p0, p1))
    return out


def roi(pos, tris, names, prim):
    """bbox of the vertices the LEFT hand + finger bones actually drive."""
    sel = np.array([bool(names[i]) and (names[i].startswith('l_')
                    and ('finger' in names[i] or names[i].endswith('_hand')))
                    for i in prim])
    pts = pos[sel]
    if not len(pts):
        pts = pos
    lo, hi = pts.min(0), pts.max(0)
    return lo - 0.035, hi + 0.035


def render(pos, tris, segs, ax, ay, lo, hi, bg=BG):
    px, py = pos[:, ax], pos[:, ay]
    pz = pos[:, 3 - ax - ay]
    span = np.array([hi[ax] - lo[ax], hi[ay] - lo[ay]])
    scale = min(W * 0.94 / span[0], H * 0.94 / span[1])
    ox = (W - span[0] * scale) / 2
    oy = (H - span[1] * scale) / 2
    sx = (px - lo[ax]) * scale + ox
    sy = H - ((py - lo[ay]) * scale + oy)

    img = np.zeros((H, W, 3), np.float64)
    img[:, :] = bg
    zbuf = np.full((H, W), -1e18)
    p0, p1, p2 = pos[tris[:, 0]], pos[tris[:, 1]], pos[tris[:, 2]]
    nrm = np.cross(p1 - p0, p2 - p0)
    nrm = nrm / np.maximum(np.linalg.norm(nrm, axis=1, keepdims=True), 1e-30)
    L = np.array([-0.4, 0.55, 0.73])
    L = L / np.linalg.norm(L)
    lam = np.abs(nrm @ L) * 0.72 + 0.28
    for i in range(len(tris)):
        xs, ys, zs = sx[tris[i]], sy[tris[i]], pz[tris[i]]
        x0, x1 = int(np.floor(xs.min())), int(np.ceil(xs.max()))
        y0, y1 = int(np.floor(ys.min())), int(np.ceil(ys.max()))
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
        sub[hit] = z[hit]
        s = float(lam[i]) * 255.0
        img[y0:y1 + 1, x0:x1 + 1][hit] = (s * 0.92, s * 0.94, s)

    im = Image.fromarray(np.clip(img, 0, 255).astype(np.uint8))
    dr = ImageDraw.Draw(im)
    for key, a, b in segs:
        col = BONE_COL[key]
        dr.line([(a[ax] - lo[ax]) * scale + ox,
                 H - ((a[ay] - lo[ay]) * scale + oy),
                 (b[ax] - lo[ax]) * scale + ox,
                 H - ((b[ay] - lo[ay]) * scale + oy)], fill=col, width=2)
        for p in (a, b):
            u = (p[ax] - lo[ax]) * scale + ox
            v = H - ((p[ay] - lo[ay]) * scale + oy)
            if 0 <= u < W and 0 <= v < H:
                dr.ellipse([u - 2, v - 2, u + 2, v + 2], fill=col)
    return im


def main(argv=None):
    argv = argv or []
    out = argv[0] if argv else os.path.join(HERE, 'out/hand-zoom.png')
    hd2, _bn, _bi, _ch, _pa = PH.rig()
    segs = bone_segments(hd2)
    print('HD2 左手/手指骨 %d 根，画在网格上' % len(segs))

    models = []
    for tag, d in RUNS:
        m = OH.load(d)
        z = np.load(os.path.join(HERE, d, 'model.npz'))
        tris = np.asarray(z['tris'], np.int64)
        models.append((tag, m['bind'], tris, m['names'], OH.primary(m)))
    lo, hi = roi(models[0][1], models[0][2], models[0][3], models[0][4])
    print('ROI X[%.3f,%.3f] Y[%.3f,%.3f] Z[%.3f,%.3f]'
          % (lo[0], hi[0], lo[1], hi[1], lo[2], hi[2]))

    cw, chh = W * len(VIEWS), H + 24
    canvas = Image.new('RGB', (cw, chh * len(models) + 22), (14, 15, 18))
    dr = ImageDraw.Draw(canvas)
    dr.text((10, 6), 'SHIPPED mesh, left hand zoom   coloured lines = HD2\'s own '
                     'hand/finger bones   (thumb amber / index green / middle blue '
                     '/ ring violet / pinky red)', fill=(255, 214, 120))
    y = 22
    for tag, pos, tris, _nm, _pr in models:
        for c, (lbl, ax, ay) in enumerate(VIEWS):
            canvas.paste(render(pos, tris, segs, ax, ay, lo, hi), (W * c, y))
            dr.text((W * c + 8, y + 4), '%s  %s' % (tag, lbl), fill=(215, 219, 226))
        y += chh
    canvas.save(out)
    print('[written] %s (%dx%d)' % (out, canvas.width, canvas.height))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
