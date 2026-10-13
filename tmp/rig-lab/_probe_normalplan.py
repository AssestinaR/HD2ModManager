"""Decide how to build the vertex NORMAL component for the packaged unit.

`model.npz` has no normals and `blender_build.py` never set UVs, so both have to
come from the source VVD (which IS index aligned with `ours_bind`, verified:
`src_tpose == pos * IN2M` to 6e-8).

The source normals live in Source-engine space; `ours_bind` is HD2 space after
`align='yaw'` + scale + local deformations.  So:

  * estimate ONE global rotation `R` with Kabsch(src_tpose -> ours_bind)
  * compare `normalize(R @ n_src)` against the area weighted geometric normal of
    the FINAL mesh, per vertex, to see how much the local deformations matter
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_source as S
import hd2_style as H

MODEL = 'survivor_teenangst'


def main():
    d = np.load('out/blender/model.npz')
    mdl_path, mdl, verts, tris, src, short = H.load_source(
        S.ADDONS + S.MODEL_VPK, MODEL)
    nv = len(verts)
    assert nv == d['ours_bind'].shape[0]

    src_pts = np.asarray([v['pos'] for v in verts], np.float64) * H.E.IN2M
    n_src = np.asarray([v['nrm'] for v in verts], np.float64)
    n_src /= np.linalg.norm(n_src, axis=1, keepdims=True)
    fin = d['ours_bind'].astype(np.float64)

    # ---- global rigid fit (rotation + uniform scale, no translation) --------
    A = src_pts - src_pts.mean(0)
    B = fin - fin.mean(0)
    U, sv, Vt = np.linalg.svd(A.T @ B)
    R = (U @ Vt).T                       # row-vector convention: p' = p @ R
    if np.linalg.det(R) < 0:
        U[:, -1] *= -1
        R = (U @ Vt).T
    scale = float(sv.sum() / (A * A).sum())
    print('global fit: scale=%.5f  det=%.4f' % (scale, np.linalg.det(R)))
    print('  residual p99 = %.4f m, max = %.4f m'
          % (np.percentile(np.linalg.norm(A @ R - B, axis=1), 99),
             np.linalg.norm(A @ R - B, axis=1).max()))

    n_rot = n_src @ R
    n_rot /= np.linalg.norm(n_rot, axis=1, keepdims=True)

    # ---- geometric normals of the FINAL mesh (area weighted) ----------------
    T = d['tris'].astype(np.int64)
    v0, v1, v2 = fin[T[:, 0]], fin[T[:, 1]], fin[T[:, 2]]
    fn = np.cross(v1 - v0, v2 - v0)
    area = np.linalg.norm(fn, axis=1) * 0.5
    fn /= np.maximum(np.linalg.norm(fn, axis=1, keepdims=True), 1e-30)
    gn = np.zeros_like(fin)
    for k in range(3):
        np.add.at(gn, T[:, k], fn * area[:, None])
    ln = np.linalg.norm(gn, axis=1, keepdims=True)
    gn = np.divide(gn, np.maximum(ln, 1e-30))

    # smooth shading: compare against the plain geometric normal only where the
    # vertex is on a smooth surface (|gn| large means the faces agree)
    good = ln[:, 0] > 1e-12
    dot = np.abs((n_rot * gn).sum(1))
    ang = np.degrees(np.arccos(np.clip(dot, -1, 1)))
    print('R@n_src vs geometric normal (n=%d):' % good.sum())
    for q in (50, 75, 90, 95, 99):
        print('   p%-3d = %6.2f deg' % (q, np.percentile(ang[good], q)))
    print('   within 20 deg: %.1f%%   within 40 deg: %.1f%%'
          % (100 * (ang[good] < 20).mean(), 100 * (ang[good] < 40).mean()))

    # per-region breakdown: arms/legs are where the local poses live
    z = fin[:, 2]
    for lo, hi, tag in ((0.0, 0.9, 'legs'), (0.9, 1.35, 'torso'),
                        (1.35, 1.55, 'shoulders'), (1.55, 2.2, 'head/arms')):
        m = good & (z >= lo) & (z < hi)
        if m.sum():
            print('   %-10s n=%-6d p50=%6.2f p90=%6.2f p99=%6.2f'
                  % (tag, m.sum(), np.percentile(ang[m], 50),
                     np.percentile(ang[m], 90), np.percentile(ang[m], 99)))

    np.save('out/blender/normal_rot.npy', R.astype(np.float32))
    print('saved out/blender/normal_rot.npy')


if __name__ == '__main__':
    main()
