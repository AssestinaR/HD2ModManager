"""Round 45e-2: the left arm, ours vs the reference patch, same view.

The skill's rule #3 is that a shape judgement must be made against the
reference in the same view -- our own numbers alone cannot say whether 30 mm of
deviation is "bent" or "normal".

columns: ours (rigid, transition band 1.5 / 3.0 / 5.0 cm) then the reference
patch's own left arm.  red = pixel-domain centre line, blue = straight line
through its two ends, so `flips 0` means that column is straight.

usage: python probe_arm_compare.py
"""
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, r'C:\Users\Administrator\.workbuddy-ai\skills'
                   r'\mesh-deform-diagnosis\scripts')
import patch_view as PV
import silhouette_centreline as SC
from probe_layered_render import run, BOX

PW, PH = 620, 560
ROT = (10.46, -18.55, 29.63)
REF_IDS = ('91eb7be84016ceab', '7336c6b662522f0e')


def ours(gap):
    os.environ['HD2_ARM_GAP'] = gap
    r = run(arm_fit=1.0, arm_rot=ROT, arm_rot_mode='rigid')
    return np.asarray(r['out'], float), [tuple(z) for z in r['tris']]


def ref():
    pts, trs, off = [], [], 0
    for uid in REF_IDS:
        p, t = PV.read_obj('out/patchdump/unit-%s.obj' % uid)
        pts.extend(p)
        trs.extend((a + off, b + off, c + off, m) for a, b, c, m in t)
        off += len(p)
    return pts, trs


def main():
    cols = [('ours rigid gap 1.5cm',) + ours('0.015'),
            ('ours rigid gap 3.0cm',) + ours('0.030'),
            ('ours rigid gap 5.0cm',) + ours('0.050'),
            ('REFERENCE patch (L+R arm)',) + ref()]
    W = 12 + len(cols) * (PW + 12)
    img = Image.new('RGB', (W, 92 + PH + 60), (0, 0, 0))
    dr = ImageDraw.Draw(img)
    dr.text((12, 8), 'round 45e-2: the left arm, ours (rigid, three transition '
            'widths) vs the REFERENCE patch, same view', fill=(232, 232, 244))
    dr.text((12, 26), 'BOX x[-0.735,-0.415] z[1.135,1.400].  red = pixel-domain '
            'centre line   blue = straight line through its two ends.',
            fill=(150, 160, 180))
    dr.text((12, 44), 'flips 0 means that column is straight.  the reference is '
            'the calibration: if IT is not straight, the measurement is wrong.',
            fill=(150, 160, 180))
    for ci, (tag, P, T) in enumerate(cols):
        cx, cy = 12 + ci * (PW + 12), 68
        dr.text((cx + 4, cy - 14), '%s   %dv %dt' % (tag, len(P), len(T)),
                fill=(205, 214, 228))
        p = PV.Panel(img, dr, (cx, cy, cx + PW, cy + PH), BOX, 0, 2, 1, False)
        p.mesh([tuple(q) for q in P], [tuple(z) for z in T], (1.0, 1.0, 1.0), 1)
        arr = np.asarray(img).astype(np.float32)
        fg, bg = SC.foreground(arr, 40.0)
        m = SC.close_along(fg[cy:cy + PH, cx:cx + PW], 0, 1)
        e = SC.evaluate(SC.centre_line(m, 'v', 3), 9)
        if e is not None:
            seq = [(cx + i, cy + e['sm'][i])
                   for i in range(len(e['sm'])) if e['ok'][i]]
            for i in range(len(seq) - 1):
                dr.line([seq[i], seq[i + 1]], fill=(255, 60, 60), width=3)
            dr.line([(cx + e['i0'], cy + e['sm'][e['i0']]),
                     (cx + e['i1'], cy + e['sm'][e['i1']])],
                    fill=(80, 170, 255), width=2)
            dr.text((cx + 4, cy + PH + 6),
                    'pixel: devmax %.1f px   flips %d' % (e['devmax'], e['flips']),
                    fill=(255, 200, 120))
        else:
            dr.text((cx + 4, cy + PH + 6), 'pixel: n/a', fill=(255, 200, 120))
    out = os.path.abspath('out/arm-compare.png')
    img.save(out)
    print('[written] %s' % out)


if __name__ == '__main__':
    main()
