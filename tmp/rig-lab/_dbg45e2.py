"""Round 45e-2: is the forearm of `rigid` actually RIGID?

The centre-line in the vertex domain is measured by slicing along the
shoulder->hand axis of the SHIPPED rig and taking the (x,z) centroid of each
slice.  But the rotation MOVES the mesh, so slicing the rotated mesh with the
ORIGINAL axis cuts the arm obliquely -- a slice then mixes vertices that came
from different places, and the centroid jitters.  That jitter is a measurement
artefact, not a bend.

This probe removes the artefact: it takes one FIXED vertex set (selected with
the UNROTATED geometry), and asks how much the shape of that set changed, by
Kabsch-fitting a rigid transform onto the unrotated set and reporting the
residual.  residual == 0  <=>  the segment is a rigid body  <=>  it cannot be
bent by this block.

NOTE the selector must be restricted to ARM-weighted vertices.  `t` is the
projection onto the shoulder->hand axis, which points DOWNWARD, so the skirt,
the legs and the hair project onto t = 0.3..0.5 as well; those vertices have
mask 0 and do not rotate, and mixing them in inflates the residual.  The first
version of this probe did exactly that and reported a bogus 69 mm.

usage: python _dbg45e2.py
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H
from probe_layered_render import run

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


def arm_weight(r):
    hd2, resolved, verts = r['hd2'], r['resolved'], r['verts']
    gidx = {}
    for h in hd2:
        gg = group(h['name'], 'l')
        if gg is not None:
            gidx[h['index']] = gg
    W = np.zeros(len(verts))
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
    return W


def kabsch_residual(A, B):
    """per-point residual of the best rigid transform A -> B."""
    X, Y = A - A.mean(0), B - B.mean(0)
    U, S, Vt = np.linalg.svd(X.T @ Y)
    d = np.sign(np.linalg.det(Vt.T @ U.T))
    R = Vt.T @ np.diag([1.0, 1.0, d]) @ U.T
    return np.linalg.norm((R @ X.T).T - Y, axis=1)


def main():
    os.environ.pop('HD2_ARM_GAP', None)
    r0 = run(arm_fit=1.0, arm_rot=None)
    P0 = np.asarray(r0['out'], float)
    hd2 = r0['hd2']
    rest = r0['rest_shipped']
    idx = {h['name']: h['index'] for h in hd2 if h['name']}
    a = rest[idx['l_shoulder']][:3, 3].astype(float)
    b = rest[idx['l_hand']][:3, 3].astype(float)
    u = (b - a) / np.linalg.norm(b - a)
    t0 = (P0 - a) @ u
    te = float((rest[idx['l_elbow']][:3, 3] - a) @ u)
    W = arm_weight(r0)
    arm = W > 0.5
    print('arm-weighted verts: %d / %d   (elbow at t=%.3f)'
          % (int(arm.sum()), len(W), te))

    regions = [('upper arm  0.16..0.26', 0.16, 0.26),
               ('elbow band 0.26..0.31', 0.26, 0.31),
               ('forearm    0.32..0.52', 0.32, 0.52),
               ('hand       0.56..0.62', 0.56, 0.62)]
    for mode, band in (('rigid', '0.015'), ('rigid', '0.050'), ('axis', '0.090')):
        os.environ['HD2_ARM_GAP'] = band
        r1 = run(arm_fit=1.0, arm_rot=ROT, arm_rot_mode=mode)
        P1 = np.asarray(r1['out'], float)
        print('\n=== %s (gap/band %s m) : rigid-fit residual of the '
              'ARM vertices vs no-arm_rot ===' % (mode, band))
        print('%-30s %6s %10s %10s %10s' %
              ('region (by UNROTATED t)', 'verts', 'mean(mm)', 'p99(mm)',
               'max(mm)'))
        for tag, lo, hi in regions:
            sel = (t0 >= lo) & (t0 < hi) & arm
            if sel.sum() < 20:
                print('%-30s %6d  (too few)' % (tag, int(sel.sum())))
                continue
            res = 1000.0 * kabsch_residual(P0[sel], P1[sel])
            print('%-30s %6d %10.3f %10.3f %10.3f'
                  % (tag, int(sel.sum()), res.mean(),
                     np.percentile(res, 99), res.max()))


if __name__ == '__main__':
    main()
