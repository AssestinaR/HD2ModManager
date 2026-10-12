"""Round 45e step 3b: layered centre line, end to end.

For each build:
  1. split the mesh into shells (connected components) and pick the one that IS the
     arm tube (spans the elbow-to-wrist range, most vertices),
  2. render ONLY that shell, flat white on black,
  3. take its centre line with the shared tool and report devmax / flips.

usage: python probe_layered_centreline.py
"""
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, r'C:\Users\Administrator\.workbuddy-ai\skills'
                   r'\mesh-deform-diagnosis\scripts')
import hd2_style as H
import patch_view as PV
import silhouette_centreline as SC
from probe_layered_render import components, run, BOX

PW, PH = 620, 560


def arm_shell(P, tris, t):
    """the connected component that spans the forearm range and has the most verts."""
    lab = components(len(P), tris)
    t3 = np.asarray([z[:3] for z in tris])
    best, bk = -1, None
    for k in np.unique(lab):
        sel = lab == k
        nv = int(sel.sum())
        if nv < 100:
            continue
        tt = t[sel]
        if tt.min() < -0.05 or tt.min() > 0.32 or tt.max() < 0.50:
            continue
        if nv > best:
            best, bk = nv, int(k)
    return lab, bk


def main():
    builds = [('no arm_rot', dict(arm_fit=1.0, arm_rot=None)),
              ('ALL (current)', dict(arm_fit=1.0, arm_rot=(10.46, -18.55, 29.63),
                                     arm_rot_mode='axis')),
              ('no elbow rot', dict(arm_fit=1.0, arm_rot=(10.46, 0.0, 29.63),
                                    arm_rot_mode='axis'))]
    W = 12 + len(builds) * (PW + 12)
    Hh = 92 + PH + 60
    img = Image.new('RGB', (W, Hh), (0, 0, 0))
    dr = ImageDraw.Draw(img)
    dr.text((12, 8), 'round 45e step 3b: render ONLY the arm shell, then take its '
            'centre line', fill=(232, 232, 244))
    dr.text((12, 26), 'the arm is one connected component; the cuff / band / frill are '
            'others.  isolating it is what the plain white render could not do.',
            fill=(150, 160, 180))
    dr.text((12, 44), 'box x[-0.735,-0.415] z[1.135,1.400]   red = centre line   '
            'blue = straight line through its two ends', fill=(150, 160, 180))

    stats = []
    for ci, (tag, kw) in enumerate(builds):
        r = run(**kw)
        P = np.asarray(r['out'], float)
        tris = r['tris']
        idx = {h['name']: h['index'] for h in r['hd2'] if h['name']}
        rest = r['rest_shipped']
        a = rest[idx['l_shoulder']][:3, 3].astype(float)
        b = rest[idx['l_hand']][:3, 3].astype(float)
        u = (b - a) / np.linalg.norm(b - a)
        t = (P - a) @ u
        lab, k = arm_shell(P, tris, t)
        sel = lab == k
        print('%-16s arm shell #%d: %d verts, t %.3f..%.3f'
              % (tag, k, int(sel.sum()), float(t[sel].min()), float(t[sel].max())))
        cx = 12 + ci * (PW + 12)
        cy = 68
        dr.text((cx + 4, cy - 14), tag, fill=(205, 214, 228))
        p = PV.Panel(img, dr, (cx, cy, cx + PW, cy + PH), BOX, 0, 2, 1, False)
        sub = [z for z in tris if int(lab[int(z[0])]) == k]
        p.mesh([tuple(q) for q in P], sub, (1.0, 1.0, 1.0), 1)

        # ---- the shared tool, straight on this panel's pixels -------------
        arr = np.asarray(img).astype(np.float32)
        fg, bg = SC.foreground(arr, 40.0)
        roi = (cx, cy, cx + PW, cy + PH)
        x0, y0, x1, y1 = roi
        m = SC.close_along(fg[y0:y1, x0:x1], 0, 1)
        e = SC.evaluate(SC.centre_line(m, 'v', 3), 9)
        seq = [(x0 + i, y0 + e['sm'][i]) for i in range(len(e['sm'])) if e['ok'][i]]
        for i in range(len(seq) - 1):
            dr.line([seq[i], seq[i + 1]], fill=(255, 60, 60), width=3)
        dr.line([(x0 + e['i0'], y0 + e['sm'][e['i0']]),
                 (x0 + e['i1'], y0 + e['sm'][e['i1']])], fill=(80, 170, 255), width=2)
        stats.append((tag, e['devmax'], e['flips'], e['n'], int(sel.sum())))

    print('\n%-16s %10s %8s %8s %10s' % ('build', 'devmax(px)', 'flips', 'slices',
                                         'shell verts'))
    for tag, dm, fl, n, nv in stats:
        print('%-16s %10.1f %8d %8d %10d' % (tag, dm, fl, n, nv))
    ly = 68 + PH + 8
    for i, (tag, dm, fl, n, nv) in enumerate(stats):
        dr.text((12 + i * (PW + 12), ly), '%-16s devmax %5.1f px   flips %d'
                % (tag, dm, fl), fill=(255, 200, 120))
    dr.text((12, ly + 20), 'flips 0 = the forearm is straight.  flips >= 1 = "a stick '
            'bent at both ends".', fill=(170, 190, 215))
    out = os.path.abspath('out/centreline-shell.png')
    img.save(out)
    print('[written] %s' % out)


if __name__ == '__main__':
    main()
