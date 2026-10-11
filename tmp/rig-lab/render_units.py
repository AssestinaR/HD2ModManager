"""Contact sheet: every geometry-bearing unit of the reference patch, on its own.

Purpose: identify which unit is the RE-2310 *helmet* (the mod's head + hair) and
which are the body pieces, without ever touching the game's own files.
"""
import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import patch_view as PV

DUMP = 'out/patchdump'
OUT = sys.argv[1] if len(sys.argv) > 1 else 'out/ref-units.png'
PW, PH = 230, 300
COLS = 6


def main():
    meta = json.load(open(os.path.join(DUMP, 'units.json'), encoding='utf-8'))
    items = []
    for u in meta:
        fid = u['unitFileId'][2:].lower()
        p, t = PV.read_obj(os.path.join(DUMP, 'unit-%s.obj' % fid))
        if not t:
            continue
        A = np.asarray(p)
        items.append((u['tag'], fid, p, t,
                      (A[:, 0].min(), A[:, 1].min(), A[:, 2].min(),
                       A[:, 0].max(), A[:, 1].max(), A[:, 2].max())))
    print('%d units with geometry' % len(items))

    # one shared box so relative size is honest
    gb = (min(i[4][0] for i in items), min(i[4][1] for i in items),
          min(i[4][2] for i in items), max(i[4][3] for i in items),
          max(i[4][4] for i in items), max(i[4][5] for i in items))
    gsc = min(PW, PH) * 0.86 / max(gb[3] - gb[0], gb[5] - gb[2], 0.001)

    rows = (len(items) + COLS - 1) // COLS
    W, H = 12 + COLS * (PW + 8), 50 + rows * (PH + 40)
    img = Image.new('RGB', (W, H), (28, 30, 36))
    d = ImageDraw.Draw(img)
    d.text((12, 8), 'reference patch: every unit with geometry (shared scale, front view)',
           fill=(232, 232, 242))
    d.text((12, 26), 'x[-%.2f,%.2f] y[-%.2f,%.2f] z[%.2f,%.2f]' % (
        -gb[0], gb[3], -gb[1], gb[4], gb[2], gb[5]), fill=(150, 160, 180))

    for k, (tag, fid, p, t, b) in enumerate(items):
        r, c = divmod(k, COLS)
        cx = 12 + c * (PW + 8)
        cy = 50 + r * (PH + 40)
        d.text((cx + 2, cy), '%s' % fid, fill=(210, 220, 235))
        d.text((cx + 2, cy + 12), '%s' % tag[:32], fill=(160, 175, 195))
        d.text((cx + 2, cy + 24), 'z[%.2f,%.2f] %dv %dt' % (b[2], b[5], len(p), len(t)),
               fill=(140, 155, 175))
        pn = PV.Panel(img, d, (cx, cy + 38, cx + PW, cy + 38 + PH), gb, 0, 2, 1, False, gsc)
        pn.mesh(p, t, (0.96, 0.96, 1.0), 1)
    img.save(OUT)
    print('[written] %s' % OUT)


if __name__ == '__main__':
    main()
