"""Final-mesh vertex normals.

`model.npz` carries no normals, and `blender_build.py` never set UVs, so both
come from the source VVD -- which IS index aligned with `ours_bind` (verified:
`src_tpose == pos * IN2M` to 6e-8, and `npz.tris == source tris[:, :3]`).

Two facts drive this module:

  1. The source VTX index blocks are wound INWARD relative to the authored
     vertex normals.  Measured on the raw source: `cross(v1-v0, v2-v0)` agrees
     with the vertex normal only 5.1% of the time; swapping v1/v2 gives 94.9%.
     The signed volume confirms it: as-read -0.486 m^3 (inward), flipped
     +0.486 m^3 (outward), and the shipping reference torso unit measures
     +0.100 m^3.  HD2 back-face culls, so shipping the un-flipped winding would
     render the model inside out.  => `WINDING_FLIP` below.

  2. A single global rotation cannot carry the source normals onto the final
     mesh: the pipeline also scales the upper body (1.12 about z=1.15), scales
     the head (8/7) and rotates the arms/legs/hands, so a global Kabsch fit
     leaves p99 = 0.43 m of position residual.  Instead each triangle gets its
     own rigid frame (source triangle frame -> final triangle frame), which is
     exact for that triangle's plane, and each vertex normal is the area
     weighted average of its incident triangles' rotated normals.
"""
import numpy as np

# source triangle order -> HD2 outward winding
WINDING_FLIP = (0, 2, 1)


def _unit(v):
    return v / np.maximum(np.linalg.norm(v, axis=-1, keepdims=True), 1e-30)


def final_normals(src_pos, src_nrm, tris, fin_pos, flip=WINDING_FLIP):
    """-> (nv, 3) unit normals in the FINAL mesh's space.

    src_pos / fin_pos : (nv, 3)
    src_nrm           : (nv, 3) authored normals (source space)
    tris              : (nt, 3) int, in SOURCE winding (this function flips)
    """
    T = np.asarray(tris, np.int64)[:, list(flip)]
    s0, s1, s2 = src_pos[T[:, 0]], src_pos[T[:, 1]], src_pos[T[:, 2]]
    f0, f1, f2 = fin_pos[T[:, 0]], fin_pos[T[:, 1]], fin_pos[T[:, 2]]

    es = _unit(s1 - s0)
    fs = _unit(np.cross(s1 - s0, s2 - s0))
    gs = np.cross(es, fs)
    ef = _unit(f1 - f0)
    ff = _unit(np.cross(f1 - f0, f2 - f0))
    gf = np.cross(ef, ff)

    # row-vector convention: basis vectors are the ROWS of each 3x3
    Ms = np.stack([es, fs, gs], axis=1)          # (nt, 3, 3), rows = source frame
    Mf = np.stack([ef, ff, gf], axis=1)          # (nt, 3, 3), rows = final frame
    # R = Mf^T @ Ms  (both orthonormal) -> maps source vectors into final space
    R = np.einsum('nji,njk->nik', Mf, Ms)

    area = np.linalg.norm(np.cross(f1 - f0, f2 - f0), axis=1) * 0.5
    nv = len(src_pos)
    acc = np.zeros((nv, 3))
    wsum = np.zeros(nv)
    for k in range(3):
        rot = np.einsum('nij,nj->ni', R, src_nrm[T[:, k]])
        np.add.at(acc, T[:, k], rot * area[:, None])
        np.add.at(wsum, T[:, k], area)
    acc = np.where(wsum[:, None] > 0, acc / np.maximum(wsum, 1e-30)[:, None], 0.0)
    # vertices with no triangle (should not happen) keep their source normal
    dead = wsum <= 0
    if dead.any():
        acc[dead] = src_nrm[dead]
    return _unit(acc)


def geometric_normals(fin_pos, tris, flip=WINDING_FLIP):
    """Area weighted smooth normals of the final mesh (diagnostics only)."""
    T = np.asarray(tris, np.int64)[:, list(flip)]
    f0, f1, f2 = fin_pos[T[:, 0]], fin_pos[T[:, 1]], fin_pos[T[:, 2]]
    fn = np.cross(f1 - f0, f2 - f0)
    area = np.linalg.norm(fn, axis=1) * 0.5
    fn = _unit(fn)
    acc = np.zeros_like(fin_pos)
    for k in range(3):
        np.add.at(acc, T[:, k], fn * area[:, None])
    return _unit(acc)


if __name__ == '__main__':
    import os
    import sys
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import export_obj as E
    import hd2_source as S
    import hd2_style as H

    vpk = S.ADDONS + S.MODEL_VPK
    _p, _m, verts, tris, _s, _sh = H.load_source(vpk, 'survivor_teenangst')
    d = np.load('out/blender/model.npz')
    src_pos = np.asarray([v['pos'] for v in verts], np.float64) * E.IN2M
    src_nrm = _unit(np.asarray([v['nrm'] for v in verts], np.float64))
    T = np.asarray(tris, np.int64)[:, :3]
    fin = d['ours_bind'].astype(np.float64)

    n = final_normals(src_pos, src_nrm, T, fin)
    g = geometric_normals(fin, T)
    ang = np.degrees(np.arccos(np.clip(np.einsum('ij,ij->i', n, g), -1, 1)))
    print('per-triangle-frame normal vs final geometric normal:')
    for q in (25, 50, 75, 90, 95, 99):
        print('   p%-3d = %6.2f deg' % (q, np.percentile(ang, q)))
    print('   within 15 deg: %.1f%%   within 30: %.1f%%'
          % (100 * (ang < 15).mean(), 100 * (ang < 30).mean()))

    # winding of the FINAL mesh must now be outward
    a, b, c = fin[T[:, 0]], fin[T[:, 1]], fin[T[:, 2]]
    print('final mesh signed volume (flipped) = %+.5f m3'
          % (np.einsum('ij,ij->i', a, np.cross(b, c)).sum() / 6.0))
    print('final mesh signed volume (as-read) = %+.5f m3'
          % (np.einsum('ij,ij->i', a, np.cross(c, b)).sum() / 6.0))
