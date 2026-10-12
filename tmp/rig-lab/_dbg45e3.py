"""Round 45e-2: the FAIR centre line.

The earlier vertex-domain centre line sliced the ROTATED mesh with the
shoulder->hand axis of the SHIPPED rig.  Because the block rotates the mesh, that
axis no longer runs along the arm, so each slice cut the arm obliquely and mixed
vertices from different places -- the centroid jittered and the "bend" it showed
was partly an artefact (that is why `rigid` looked like it had a lump).

Here the SELECTOR is fixed: the slices are defined once, with the UNROTATED
geometry, and then the SAME vertex sets are used for every build.  A rigid
rotation preserves straightness, so if a segment is a rigid body its centre line
stays exactly as straight as the unrotated one.

usage: python _dbg45e3.py
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from probe_layered_render import components, run

ROT = (10.46, -18.55, 29.63)


def centreline(P, sel, t0, a, u, edges):
    u2 = np.array([u[0], u[2]])
    u2 /= np.linalg.norm(u2)
    n2 = np.array([-u2[1], u2[0]])
    e2 = np.stack([P[:, 0], P[:, 2]], 1) - np.array([a[0], a[2]])
    q = e2 @ n2
    cs = []
    for i in range(len(edges) - 1):
        ss = sel & (t0 >= edges[i]) & (t0 < edges[i + 1])
        cs.append(np.nan if ss.sum() < 8 else float(q[ss].mean()))
    cs = np.array(cs)
    ok = np.isfinite(cs)
    base = np.linspace(cs[ok][0], cs[ok][-1], int(ok.sum()))
    return cs, float(np.max(np.abs(cs[ok] - base)))


def main():
    os.environ.pop('HD2_ARM_GAP', None)
    r0 = run(arm_fit=1.0, arm_rot=None)
    P0 = np.asarray(r0['out'], float)
    tris = r0['tris']
    rest = r0['rest_shipped']
    idx = {h['name']: h['index'] for h in r0['hd2'] if h['name']}
    a = rest[idx['l_shoulder']][:3, 3].astype(float)
    b = rest[idx['l_hand']][:3, 3].astype(float)
    u = (b - a) / np.linalg.norm(b - a)
    t0 = (P0 - a) @ u

    lab = components(len(P0), tris)
    best, bestp = None, 1e9
    for k in np.unique(lab):
        ss = lab == k
        if ss.sum() < 100:
            continue
        tt = t0[ss]
        if tt.min() < -0.05 or tt.min() > 0.32 or tt.max() < 0.50:
            continue
        perp = np.linalg.norm((P0[ss] - a) - np.outer(tt, u), axis=1).mean()
        if perp < bestp:
            bestp, best = perp, k
    sel = lab == best
    print('arm shell #%d, %d verts, t0 range %.3f..%.3f'
          % (best, int(sel.sum()), t0[sel].min(), t0[sel].max()))

    edges = np.arange(0.15, 0.56 + 1e-9, 0.02)
    print('\n%-12s %10s   centre line q (mm), t = %.2f .. %.2f step %.2f'
          % ('build', 'devmax(mm)', edges[0], edges[-2], edges[1] - edges[0]))
    for tag, kw in (('no arm_rot', dict(arm_fit=1.0, arm_rot=None)),
                    ('rigid 1.5cm', dict(arm_fit=1.0, arm_rot=ROT,
                                         arm_rot_mode='rigid')),
                    ('axis 18cm', dict(arm_fit=1.0, arm_rot=ROT,
                                       arm_rot_mode='axis'))):
        r = run(**kw)
        P = np.asarray(r['out'], float)
        cs, dev = centreline(P, sel, t0, a, u, edges)
        print('%-12s %10.1f   %s'
              % (tag, 1000 * dev,
                 ' '.join('%6.1f' % (1000 * v) for v in cs)))


if __name__ == '__main__':
    main()
