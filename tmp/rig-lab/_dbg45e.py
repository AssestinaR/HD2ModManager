"""Round 45e-2 diagnostic: for the ARM shell only, show how W (the arm-bone LBS
weight sum), the binary mask, g0/g1/g2 and the centre-line offset vary along the
arm axis.  The question it answers: is the "lump" that `rigid` introduced a hard
edge of the binary mask (W crossing 0.5 inside the shell), or something else?

usage: python _dbg45e.py
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H
from probe_layered_render import components, run

ROT = (10.46, -18.55, 29.63)


def group(nm, side):
    if not H.is_arm_bone(nm) or not nm.startswith(side + '_'):
        return None
    tail = nm[len(side) + 1:]
    if 'finger' in tail or tail.startswith('hand'):
        return 2
    if tail.startswith('elbow'):
        return 1
    return 0


def analyse(mode, band, thr=0.5, grow=2):
    os.environ['HD2_ARM_GAP'] = str(band)
    os.environ['HD2_ARM_BAND'] = str(band)
    os.environ['HD2_ARM_MASK'] = str(thr)
    os.environ['HD2_ARM_MASK_GROW'] = str(grow)
    r = run(arm_fit=1.0, arm_rot=ROT, arm_rot_mode=mode)
    P = np.asarray(r['out'], float)
    tris = r['tris']
    hd2 = r['hd2']
    rest = r['rest_shipped']
    idx = {h['name']: h['index'] for h in hd2 if h['name']}
    resolved = r['resolved']
    verts = r['verts']

    a = rest[idx['l_shoulder']][:3, 3].astype(float)
    b = rest[idx['l_hand']][:3, 3].astype(float)
    u = (b - a) / np.linalg.norm(b - a)
    J = [rest[idx[n]][:3, 3].astype(float)
         for n in ('l_shoulder', 'l_elbow', 'l_hand')]
    t = (P - a) @ u
    te = float((J[1] - J[0]) @ u)
    tw = float((J[2] - J[0]) @ u)

    gidx = {}
    for h in hd2:
        gg = group(h['name'], 'l')
        if gg is not None:
            gidx[h['index']] = gg
    W = np.zeros(len(P))
    for j, v in enumerate(verts):
        s = 0.0
        for q in range(3):
            wq = v['w'][q]
            if wq <= 0:
                continue
            gg = gidx.get(resolved.get(v['bones'][q], -1))
            if gg is not None:
                s += wq
        W[j] = s

    lab = components(len(P), tris)
    best, bestp = None, 1e9
    for k in np.unique(lab):
        ss = lab == k
        if ss.sum() < 100:
            continue
        tt = t[ss]
        if tt.min() < -0.05 or tt.min() > 0.32 or tt.max() < 0.50:
            continue
        perp = np.linalg.norm((P[ss] - a) - np.outer(tt, u), axis=1).mean()
        if perp < bestp:
            bestp, best = perp, k
    sel = lab == best

    def ramp(c):
        x = np.clip((t - (c - band)) / (2.0 * band), 0.0, 1.0)
        return x * x * (3.0 - 2.0 * x)

    m = W > thr
    if mode == 'rigid' and grow > 0:
        T2 = np.asarray([q[:3] for q in tris], dtype=np.int64)
        e2 = np.concatenate([T2[:, [0, 1]], T2[:, [1, 2]], T2[:, [2, 0]]])
        s2 = np.concatenate([e2[:, 0], e2[:, 1]])
        d2 = np.concatenate([e2[:, 1], e2[:, 0]])
        for _ in range(grow):
            hit = np.zeros(len(P), dtype=bool)
            np.logical_or.at(hit, s2, m[d2])
            m = m | hit

    print('\n=== mode %s  band %.3f  thr %.2f  grow %d  |  shell #%d (%d verts, '
          'perp %.3f)' % (mode, band, thr, grow, best, int(sel.sum()), bestp))
    print('    te %.3f  tw %.3f   t range %.3f..%.3f'
          % (te, tw, t[sel].min(), t[sel].max()))
    print('    mask: %d / %d verts of the shell are IN (%.1f%%)'
          % (int((m & sel).sum()), int(sel.sum()),
             100.0 * (m & sel).sum() / max(int(sel.sum()), 1)))
    print('    %7s %7s %7s %6s %6s %6s %9s' %
          ('t', 'Wmin', 'Wmean', 'mask', 'g0', 'g1', 'q(mm)'))
    u2 = np.array([u[0], u[2]])
    u2 /= np.linalg.norm(u2)
    n2 = np.array([-u2[1], u2[0]])
    e2p = np.stack([P[:, 0], P[:, 2]], 1) - np.array([a[0], a[2]])
    q = e2p @ n2
    edges = np.arange(0.10, 0.60 + 1e-9, 0.02)
    for i in range(len(edges) - 1):
        ss = sel & (t >= edges[i]) & (t < edges[i + 1])
        if ss.sum() < 5:
            continue
        print('    %7.3f %7.3f %7.3f %6.2f %6.2f %6.2f %9.1f'
              % (0.5 * (edges[i] + edges[i + 1]), W[ss].min(), W[ss].mean(),
                 m[ss].mean(), (m[ss] * ramp(0.0)[ss]).mean(),
                 (m[ss] * ramp(te)[ss]).mean(), 1000.0 * q[ss].mean()))


if __name__ == '__main__':
    for mode, band in (('rigid', 0.015), ('axis', 0.090), ('rigid', 0.090)):
        analyse(mode, band)
