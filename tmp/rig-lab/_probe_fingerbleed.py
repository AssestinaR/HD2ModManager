# -*- coding: utf-8 -*-
"""Show, per model, WHICH source geometry ends up riding an HD2 FINGER bone.

The round-65 output-side probe found `l_pinky_finger1` / `r_pinky_finger1`
driving ~1500 verts each, at a mean distance of ~1.2 m from the bone -- i.e. a
handful of stray source bones are dumping far-away cloth onto the pinky.  This
probe paints exactly that geometry red, in both models, so the two can be
compared by eye.

Left column = Lunalice (the reference the pose constants were tuned against),
right column = Lilium.  Grey = everything else.

usage: _probe_fingerbleed.py <out.png>
"""
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import _probe_hops as PH          # rig(), alignment(), RUNS -- single source of truth

W, H = 420, 700
VIEWS = [('front', 0, 2, 1, False),
         ('left ', 1, 2, 0, False),
         ('top  ', 0, 1, 2, False)]
IN2M = 0.0254


def to_display(pos_src):
    """Valve space (X = left, Y = ?, Z = up, INCHES) -> HD2-ish.

    Not used any more: the source's own axes are not what you would guess from
    the file (bones come out in METRES with X = left, vertices come out in
    INCHES), so the only convention-proof mapping is the pipeline's own
    alignment.  `analyse()` now calls `align_pts` + `t` directly.  Kept because
    `_probe_zoom_arm.py` imports it."""
    p = np.asarray(pos_src, np.float64) * IN2M
    return np.stack([-p[:, 1], p[:, 0], p[:, 2]], axis=1)


def render(pos, tris, view, hot, bg=(22, 24, 28)):
    _tag, ax, ay, da, mirror = view
    px, py = pos[:, ax].copy(), pos[:, ay].copy()
    if mirror:
        px = -px
    pz = pos[:, da]
    lo = np.array([px.min(), py.min()])
    hi = np.array([px.max(), py.max()])
    span = max(hi - lo)
    scale = (min(W, H) * 0.92) / span
    ox = (W - (hi[0] - lo[0]) * scale) / 2
    oy = (H - (hi[1] - lo[1]) * scale) / 2
    sx = (px - lo[0]) * scale + ox
    sy = H - ((py - lo[1]) * scale + oy)

    img = np.zeros((H, W, 3), np.float64)
    img[:, :] = bg
    zbuf = np.full((H, W), -1e18)

    p0, p1, p2 = pos[tris[:, 0]], pos[tris[:, 1]], pos[tris[:, 2]]
    nrm = np.cross(p1 - p0, p2 - p0)
    nrm = nrm / np.maximum(np.linalg.norm(nrm, axis=1, keepdims=True), 1e-30)
    L = np.array([-0.4, 0.55, 0.73])
    L = L / np.linalg.norm(L)
    lam = np.abs(nrm @ L) * 0.72 + 0.28

    # draw cold first so hot always wins the depth test on a tie
    for hotpass in (False, True):
        for i in range(len(tris)):
            if bool(hot[i]) != hotpass:
                continue
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
            if hotpass:
                col = (232, 74, 74)
            else:
                s = float(lam[i]) * 255.0
                col = (s * 0.92, s * 0.94, s)
            img[y0:y1 + 1, x0:x1 + 1][hit] = col
    return Image.fromarray(np.clip(img, 0, 255).astype(np.uint8))


def analyse(tag, root, vpk, hd2, by_idx_name, by_idx, children, parents):
    body = PH.ST.snap_body(PH.SPINE_MAP)
    hby = {h['name']: h for h in hd2 if h['name']}
    al = PH.alignment(os.path.join(root, vpk), PH.MODEL, hby, body)
    src, short, verts, tris = al['src'], al['short'], al['verts'], al['tris']

    m = {}
    for sn, hn in PH.ST.snap_pairs(PH.SPINE_MAP) + PH.ST.SNAP_TOES:
        sb, hb = short.get(sn), hby.get(hn)
        if sb is not None and hb is not None:
            m[sb['index']] = hb['index']

    def ajoint(b):
        return al['align_pts'](PH.ST.origin_np(
            PH.ST.m_to_np(b['world_m'])).reshape(1, 3))[0] + al['t']

    resolved, hops, by_dist = PH.ST.resolve_bone_map(
        src, m, 'region', ajoint, by_idx_name, children, parents)

    # per-vertex primary source bone -> resolved HD2 bone name
    names = []
    for v in verts:
        j = int(np.argmax(v['w']))
        names.append(by_idx_name.get(resolved.get(v['bones'][j])))
    names = np.array(names, dtype=object)
    hotv = np.array([bool(n) and 'finger' in n for n in names])

    tris = np.asarray(tris, np.int64)
    hot = hotv[tris].any(axis=1)

    # which source bones feed the hot set, by name
    feed = {}
    for k, v in enumerate(verts):
        if not hotv[k]:
            continue
        j = int(np.argmax(v['w']))
        sb = v['bones'][j]
        feed.setdefault(sb, 0)
        feed[sb] += 1
    srcn = {b['index']: b['name'] for b in src}

    pos = al['align_pts'](np.array([v['pos'] for v in verts], np.float64)
                          * IN2M) + al['t']
    print('=' * 78)
    print('%s   %d verts / %d tris' % (tag, len(verts), len(tris)))
    print('  骑在 finger 骨上的顶点: %d (%.2f%%)  三角形: %d'
          % (int(hotv.sum()), 100.0 * hotv.sum() / max(len(hotv), 1),
             int(hot.sum())))
    print('  喂给它们的源骨 (顶点数 / 骨名 / hops / -> HD2):')
    dmap = {nm: (h, d) for nm, h, d in by_dist}
    for sb, n in sorted(feed.items(), key=lambda kv: -kv[1]):
        hn, dd = dmap.get(srcn.get(sb), (by_idx_name.get(resolved.get(sb)), -1.0))
        print('     %5d  %-22s hops=%s -> %s (%.3f m)'
              % (n, srcn.get(sb), hops.get(sb), hn, dd))
    print()
    return pos, tris, hot


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, 'out/lilium-fingerbleed.png')
    hd2, by_idx_name, by_idx, children, parents = PH.rig()
    panels = []
    for tag, root, vpk in PH.RUNS:
        pos, tris, hot = analyse(tag, root, vpk, hd2, by_idx_name, by_idx,
                                 children, parents)
        ims = [render(pos, tris, v, hot) for v in VIEWS]
        panels.append((tag, ims))

    cw = W * len(VIEWS)
    ch = H + 30
    canvas = Image.new('RGB', (cw, ch * len(panels) + 26), (14, 15, 18))
    dr = ImageDraw.Draw(canvas)
    dr.text((10, 7), 'red = source geometry that the region fallback binds to an '
                     'HD2 FINGER bone', fill=(255, 214, 120))
    y = 26
    for tag, ims in panels:
        for i, im in enumerate(ims):
            canvas.paste(im, (W * i, y + 26))
            dr.text((W * i + 8, y + 5), '%s  %s' % (tag, VIEWS[i][0]),
                    fill=(215, 219, 226))
        y += ch
    canvas.save(out)
    print('[written] %s  (%dx%d)' % (out, canvas.width, canvas.height))
    return 0


if __name__ == '__main__':
    sys.exit(main())
