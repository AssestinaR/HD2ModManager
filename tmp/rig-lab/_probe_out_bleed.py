# -*- coding: utf-8 -*-
"""Same red-mask view, but on the SHIPPED mesh (`out/*/model.npz`).

The source-side probe says the sleeve is bound to the pinky.  This one shows the
consequence in the geometry that actually goes into the patch: red = vertices
whose dominant weight is an HD2 finger bone.

usage: _probe_out_bleed.py [out.png]
"""
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import _probe_fingerbleed as FB
import _probe_out_hands as OH

RUNS = [('Lunalice', 'out/blender'), ('Lilium  ', 'out/lilium')]


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, 'out/out-fingerbleed.png')
    rows = []
    for tag, d in RUNS:
        m = OH.load(d)
        pos = m['bind']
        p = OH.primary(m)
        hotv = np.array([('finger' in (m['names'][i] or '')) for i in p])
        names = np.asarray(m['names'], dtype=object)
        # the offending bones, and how many verts each one owns
        feed = {}
        for k, i in enumerate(p):
            if hotv[k]:
                feed[names[i]] = feed.get(names[i], 0) + 1
        # tris are not in the npz under this key -- rebuild from src_tpose order
        z = np.load(os.path.join(HERE, d, 'model.npz'))
        tris = np.asarray(z['tris'], np.int64) if 'tris' in z.files else None
        if tris is None:
            raise SystemExit('no tris in %s' % d)
        hot = hotv[tris].any(axis=1)
        print('=' * 78)
        print('%s  shipped bind: %d verts / %d tris   red(primary=finger) = %d (%.2f%%)'
              % (tag, len(pos), len(tris), int(hotv.sum()),
                 100.0 * hotv.sum() / len(hotv)))
        for nm, n in sorted(feed.items(), key=lambda kv: -kv[1])[:8]:
            print('     %5d  %s' % (n, nm))
        rows.append((tag, pos, tris, hot))

    panels = []
    for tag, pos, tris, hot in rows:
        for v in FB.VIEWS:
            panels.append((tag, v[0], FB.render(pos, tris, v, hot)))

    W, H = FB.W, FB.H
    cw, ch = W * len(FB.VIEWS), H + 24
    canvas = Image.new('RGB', (cw, ch * len(rows) + 22), (14, 15, 18))
    dr = ImageDraw.Draw(canvas)
    dr.text((10, 6), 'SHIPPED mesh   red = dominant weight is an HD2 FINGER bone',
            fill=(255, 214, 120))
    y = 22
    for r in range(len(rows)):
        for c in range(len(FB.VIEWS)):
            tag, lbl, im = panels[r * len(FB.VIEWS) + c]
            canvas.paste(im, (W * c, y))
            dr.text((W * c + 8, y + 4), '%s  %s' % (tag, lbl), fill=(215, 219, 226))
        y += ch
    canvas.save(out)
    print('[written] %s (%dx%d)' % (out, canvas.width, canvas.height))
    return 0


if __name__ == '__main__':
    sys.exit(main())
