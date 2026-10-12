"""Round 45e step 3b: layered centre line, end to end.

  1. split the mesh into shells (connected components),
  2. list every shell that spans the forearm, with its own centre-line devmax/flips,
  3. render the tightest one (smallest distance from the bone axis) flat white on
     black and take its centre line with the shared pixel tool, as a cross-check.

usage: python probe_layers_report.py
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


def frame(r):
    idx = {h['name']: h['index'] for h in r['hd2'] if h['name']}
    rest = r['rest_shipped']
    a = rest[idx['l_shoulder']][:3, 3].astype(float)
    b = rest[idx['l_hand']][:3, 3].astype(float)
    u = (b - a) / np.linalg.norm(b - a)
    return a, u


def vtx_centreline(P, sel, a, u, t):
    """centre line of ONE shell in the vertex domain: slice along t, take the
    (x,z) centroid of each slice; return the lateral offset series."""
    u2 = np.array([u[0], u[2]]); u2 /= np.linalg.norm(u2)
    n2 = np.array([-u2[1], u2[0]])
    e2 = np.stack([P[:, 0], P[:, 2]], 1) - np.array([a[0], a[2]])
    q = e2 @ n2
    out = []
    edges = np.arange(0.10, 0.62 + 1e-9, 0.02)
    for i in range(len(edges) - 1):
        m = sel & (t >= edges[i]) & (t < edges[i + 1])
        out.append(np.nan if m.sum() < 8 else float(q[m].mean()))
    return np.array(out)


def main():
    ROT = (10.46, -18.55, 29.63)
    builds = [('no arm_rot', dict(arm_fit=1.0, arm_rot=None), {}),
              ('rigid gap1.5cm', dict(arm_fit=1.0, arm_rot=ROT,
                                      arm_rot_mode='rigid'),
               {'HD2_ARM_GAP': '0.015'}),
              ('rigid gap5cm', dict(arm_fit=1.0, arm_rot=ROT,
                                    arm_rot_mode='rigid'),
               {'HD2_ARM_GAP': '0.050'}),
              ('axis 18cm(45d)', dict(arm_fit=1.0, arm_rot=ROT,
                                      arm_rot_mode='axis'), {})]
    cand = {}
    for tag, kw, env in builds:
        os.environ.pop('HD2_ARM_GAP', None)
        for k, v in env.items():
            os.environ[k] = v
        r = run(**kw)
        P = np.asarray(r['out'], float)
        tris = r['tris']
        a, u = frame(r)
        t = (P - a) @ u
        perp = np.linalg.norm((P - a) - np.outer(t, u), axis=1)
        lab = components(len(P), tris)
        rows = []
        for k in np.unique(lab):
            sel = lab == k
            nv = int(sel.sum())
            if nv < 100:
                continue
            tt = t[sel]
            if tt.min() < -0.05 or tt.min() > 0.32 or tt.max() < 0.50:
                continue
            q = vtx_centreline(P, sel, a, u, t)
            ok = np.isfinite(q)
            d = np.diff(q[ok])
            flips = int((np.sign(d[1:]) * np.sign(d[:-1]) < 0).sum()) if len(d) > 2 else 0
            base = np.linspace(q[ok][0], q[ok][-1], int(ok.sum()))
            rows.append(dict(k=int(k), nv=nv, perp=float(perp[sel].mean()),
                             tmin=float(tt.min()), tmax=float(tt.max()),
                             devmax=float(np.max(np.abs(q[ok] - base))), flips=flips))
        rows.sort(key=lambda d: d['perp'])
        cand[tag] = (r, P, tris, lab, rows)
        print('\n== %s ==  shells that span the forearm:' % tag)
        print('%-8s %7s %7s %8s %8s %11s %6s' %
              ('shell', 'verts', 'perp', 't_min', 't_max', 'devmax(mm)', 'flips'))
        for d in rows:
            print('%-8d %7d %7.3f %8.3f %8.3f %11.1f %6d'
                  % (d['k'], d['nv'], d['perp'], d['tmin'], d['tmax'],
                     1000 * d['devmax'], d['flips']))

    W = 12 + len(builds) * (PW + 12)
    img = Image.new('RGB', (W, 92 + PH + 60), (0, 0, 0))
    dr = ImageDraw.Draw(img)
    dr.text((12, 8), 'round 45e step 3b: render ONLY the tightest arm shell, then '
            'take its centre line', fill=(232, 232, 244))
    dr.text((12, 26), 'the arm is one connected component; cuff / band / frill are '
            'others.  isolating it is what the plain white render could not do.',
            fill=(150, 160, 180))
    dr.text((12, 44), 'box x[-0.735,-0.415] z[1.135,1.400]   red = centre line   '
            'blue = straight line through its two ends', fill=(150, 160, 180))
    stats = []
    for ci, (tag, kw, env) in enumerate(builds):
        r, P, tris, lab, rows = cand[tag]
        k = rows[0]['k']
        sel = lab == k
        cx, cy = 12 + ci * (PW + 12), 68
        dr.text((cx + 4, cy - 14), '%s   shell #%d  %dv  perp %.3f'
                % (tag, k, int(sel.sum()), rows[0]['perp']), fill=(205, 214, 228))
        p = PV.Panel(img, dr, (cx, cy, cx + PW, cy + PH), BOX, 0, 2, 1, False)
        sub = [z for z in tris if int(lab[int(z[0])]) == k]
        p.mesh([tuple(z) for z in P], sub, (1.0, 1.0, 1.0), 1)
        arr = np.asarray(img).astype(np.float32)
        fg, bg = SC.foreground(arr, 40.0)
        x0, y0, x1, y1 = cx, cy, cx + PW, cy + PH
        m = SC.close_along(fg[y0:y1, x0:x1], 0, 1)
        e = SC.evaluate(SC.centre_line(m, 'v', 3), 9)
        if e is not None:
            seq = [(x0 + i, y0 + e['sm'][i]) for i in range(len(e['sm'])) if e['ok'][i]]
            for i in range(len(seq) - 1):
                dr.line([seq[i], seq[i + 1]], fill=(255, 60, 60), width=3)
            dr.line([(x0 + e['i0'], y0 + e['sm'][e['i0']]),
                     (x0 + e['i1'], y0 + e['sm'][e['i1']])], fill=(80, 170, 255), width=2)
        stats.append((tag, rows[0], e))
    ly = 68 + PH + 8
    for i, (tag, d, e) in enumerate(stats):
        dr.text((12 + i * (PW + 12), ly),
                '%-14s vertex: devmax %5.1f mm  flips %d  |  pixel: %s'
                % (tag, 1000 * d['devmax'], d['flips'],
                   'n/a' if e is None else 'devmax %.1f px  flips %d'
                   % (e['devmax'], e['flips'])), fill=(255, 200, 120))
    dr.text((12, ly + 20), 'flips 0 = the forearm is straight.  flips >= 1 = "a stick '
            'bent at both ends".', fill=(170, 190, 215))
    out = os.path.abspath('out/centreline-shell.png')
    img.save(out)
    print('\n[written] %s' % out)


if __name__ == '__main__':
    main()
