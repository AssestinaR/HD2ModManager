# -*- coding: utf-8 -*-
"""Before/after of the chain-rule fix, on the SHIPPED bind mesh.

Red = the vertices whose dominant bone changed (the two ribbons).  The point of
looking at this is to check the ribbon does not end up inside the body or the
leg now that it is driven by `l_elbow` instead of `l_pinky_finger1`.

usage: _probe_round65_render.py [out.png]
"""
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import _probe_fingerbleed as FB
import _probe_out_hands as OH

RUNS = [('before', 'out/lilium-before-chainrule'), ('after ', 'out/lilium')]


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
        HERE, 'out/round65-before-after.png')
    old = OH.load('out/lilium-before-chainrule')
    new = OH.load('out/lilium')
    moved = np.linalg.norm(old['bind'] - new['bind'], axis=1) > 1e-3
    print('moved verts: %d  (max %.1f mm)' % (int(moved.sum()), 
                                              float(np.linalg.norm(
                                                  old['bind'] - new['bind'],
                                                  axis=1).max()) * 1000))

    rows = []
    for tag, d in RUNS:
        m = OH.load(d)
        z = np.load(os.path.join(HERE, d, 'model.npz'))
        tris = np.asarray(z['tris'], np.int64)
        hot = moved[tris].any(axis=1)
        rows.append((tag, m['bind'], tris, hot))

    panels = []
    for tag, pos, tris, hot in rows:
        for v in FB.VIEWS:
            panels.append((tag, v[0], FB.render(pos, tris, v, hot)))

    W, H = FB.W, FB.H
    cw, ch = W * len(FB.VIEWS), H + 24
    canvas = Image.new('RGB', (cw, ch * len(rows) + 22), (14, 15, 18))
    dr = ImageDraw.Draw(canvas)
    dr.text((10, 6), 'chain-rule fix   red = vertices whose dominant bone changed '
                     '(the two ribbons: pinky -> elbow)', fill=(255, 214, 120))
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
