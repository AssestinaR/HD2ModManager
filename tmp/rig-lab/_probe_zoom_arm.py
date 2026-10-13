# -*- coding: utf-8 -*-
"""Zoom in on the arm region, source side, so the red geometry can be named.

`_probe_fingerbleed.py` shows a big red wing in Lilium and only fingertips in
Lunalice.  This crops to the shoulder/arm box and adds a bone-joint overlay, so
the shape can be identified and its skeleton read off.

usage: _probe_zoom_arm.py [out.png]
"""
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import _probe_hops as PH
import _probe_fingerbleed as FB

W, H = 560, 620
# HD2-ish display box: X right, Y forward, Z up (metres)
BOX = dict(x=(-1.75, 0.15), y=(-1.05, 1.05), z=(0.75, 1.75))


def render(pos, tris, ax, ay, hot, joints, bg=(22, 24, 28)):
    px, py = pos[:, ax], pos[:, ay]
    pz = pos[:, 3 - ax - ay] if ax != ay else pos[:, 2]
    lo = np.array([BOX['xyz'[ax]][0] if False else 0, 0])
    span = np.array([BOX['x' if ax == 0 else ('y' if ax == 1 else 'z')],
                     BOX['x' if ay == 0 else ('y' if ay == 1 else 'z')]],
                    np.float64)
    scale = min(W / (span[0][1] - span[0][0]), H / (span[1][1] - span[1][0]))

    def sx_(v):
        return (v - span[0][0]) * scale

    def sy_(v):
        return H - (v - span[1][0]) * scale

    sx, sy = sx_(px), sy_(py)

    img = np.zeros((H, W, 3), np.float64)
    img[:, :] = bg
    zbuf = np.full((H, W), -1e18)
    p0, p1, p2 = pos[tris[:, 0]], pos[tris[:, 1]], pos[tris[:, 2]]
    nrm = np.cross(p1 - p0, p2 - p0)
    nrm = nrm / np.maximum(np.linalg.norm(nrm, axis=1, keepdims=True), 1e-30)
    L = np.array([-0.4, 0.55, 0.73])
    L = L / np.linalg.norm(L)
    lam = np.abs(nrm @ L) * 0.72 + 0.28

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

    im = Image.fromarray(np.clip(img, 0, 255).astype(np.uint8))
    dr = ImageDraw.Draw(im)
    for nm, p, col in joints:
        u, v = sx_(p[ax]), sy_(p[ay])
        if 0 <= u < W and 0 <= v < H:
            dr.ellipse([u - 3, v - 3, u + 3, v + 3], fill=col)
            dr.text((u + 5, v - 6), nm, fill=col)
    return im


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, 'out/zoom-arm.png')
    hd2, by_idx_name, by_idx, children, parents = PH.rig()
    hby = {h['name']: h for h in hd2 if h['name']}

    rows = []
    for tag, root, vpk in PH.RUNS:
        body = PH.ST.snap_body(PH.SPINE_MAP)
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
        nmv = np.array([by_idx_name.get(resolved.get(v['bones'][int(np.argmax(v['w']))]))
                        for v in verts], dtype=object)
        hotv = np.array([bool(n) and 'finger' in n for n in nmv])
        tris = np.asarray(tris, np.int64)
        hot = hotv[tris].any(axis=1)
        pos = al['align_pts'](np.array([v['pos'] for v in verts], np.float64)
                              * FB.IN2M) + al['t']

        # joint markers: the source extra bones, drawn where they really are
        joints = []
        for b in src:
            n = b['name'] or ''
            if 'extra' in n and b['index'] in hops and hops[b['index']] >= 4:
                p = ajoint(b)
                joints.append((n.replace('source_extra_', 'x'), p,
                               (120, 220, 140)))
        rows.append((tag, pos, tris, hot, joints))

    panels = []
    for tag, pos, tris, hot, joints in rows:
        for ax, ay, lbl in ((0, 2, 'front'), (0, 1, 'top')):
            panels.append((tag, lbl, render(pos, tris, ax, ay, hot, joints)))

    cw, ch = W, H + 24
    canvas = Image.new('RGB', (cw * 2, ch * len(rows) + 22), (14, 15, 18))
    dr = ImageDraw.Draw(canvas)
    dr.text((10, 6), 'arm box zoom   red = geometry bound to an HD2 finger bone   '
                     'green dot = source_extra bone with hops>=4', fill=(255, 214, 120))
    y = 22
    for r in range(len(rows)):
        for c in range(2):
            tag, lbl, im = panels[r * 2 + c]
            canvas.paste(im, (W * c, y))
            dr.text((W * c + 8, y + 4), '%s  %s' % (tag, lbl), fill=(215, 219, 226))
        y += ch
    canvas.save(out)
    print('[written] %s (%dx%d)' % (out, canvas.width, canvas.height))
    return 0


if __name__ == '__main__':
    sys.exit(main())
