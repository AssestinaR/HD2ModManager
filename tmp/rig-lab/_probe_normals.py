# -*- coding: utf-8 -*-
"""READ-ONLY normal check (round 60).

Decodes the stored oct-encoded normals and compares them with the geometric
face normals rebuilt from the index buffer.  If the stored normals are flipped
(or otherwise wrong) the model renders dark with stray specular highlights --
exactly the round-60 report.
"""
import os
import sys

import numpy as np

import hd2_patch as P
import hd2_unit as U

OURS = 'E:/mod/Lunalice-RE2310-Test/9ba626afa44a3aa3.patch_0'
OUR_UNIT = 0xcae35ee15168145f


def oct_decode(u32):
    """HD2 `_unk_normal`: 10-10 bit octahedral, dequantised the way our own
    `hd2_unitbuild.oct_encode` quantises it:  code = (v + 1) * 511.5."""
    a = np.asarray(u32, np.uint32)
    x = (a & 0x3FF).astype(np.float64) / 511.5 - 1.0
    y = ((a >> 10) & 0x3FF).astype(np.float64) / 511.5 - 1.0
    z = 1.0 - (np.abs(x) + np.abs(y))
    t = np.clip(-z, 0.0, 1.0)
    x = x + np.where(x >= 0, -t, t)
    y = y + np.where(y >= 0, -t, t)
    n = np.stack([x, y, z], axis=1)
    return n / np.maximum(np.linalg.norm(n, axis=1, keepdims=True), 1e-30)


def roundtrip_check():
    """Decode(encode(v)) must reproduce v -- otherwise the decode above is wrong."""
    import hd2_unitbuild as UB
    rng = np.random.default_rng(7)
    v = rng.normal(size=(20000, 3))
    v /= np.linalg.norm(v, axis=1, keepdims=True)
    back = oct_decode(UB.oct_encode(v))
    d = (v * back).sum(axis=1)
    print('round-trip decode(encode(v)): worst angle %.4f deg, mean %.4f deg'
          % (np.degrees(np.arccos(np.clip(d.min(), -1, 1))),
             np.degrees(np.arccos(np.clip(d.mean(), -1, 1)))))
    for probe in ((0, 0, 1), (0, 0, -1), (1, 0, 0), (-1, 0, 0), (0, 1, 0),
                  (0, -1, 0)):
        p = np.asarray([probe], np.float64)
        print('   %-14s -> %s' % (probe, np.round(oct_decode(UB.oct_encode(p))[0], 4)))


def main():
    roundtrip_check()
    print()
    entries, _raw = P.scan_entries(OURS)
    for e in entries:
        if e['type_id'] == U.UNIT_ID and e['file_id'] == OUR_UNIT:
            pl = P.entry_payload(e, OURS)
            u = U.Unit.read(pl['toc'], pl['gpu'])
            break
    else:
        raise LookupError('unit not found')

    verts = u.decode_vertices(0)
    pos = np.asarray([v['position'] for v in verts], np.float64)
    nor_raw = np.asarray([v['normal'] for v in verts], np.uint32)
    nrm = oct_decode(nor_raw)
    print('vertices: %d' % len(pos))
    print('stored normal length: mean %.6f  min %.6f  max %.6f'
          % (np.linalg.norm(nrm, axis=1).mean(),
             np.linalg.norm(nrm, axis=1).min(),
             np.linalg.norm(nrm, axis=1).max()))

    # geometric normals from the index buffer
    idx = np.frombuffer(u.gpu_index[0], np.uint32).reshape(-1, 3)
    tri = pos[idx]
    fn = np.cross(tri[:, 1] - tri[:, 0], tri[:, 2] - tri[:, 0])
    ln = np.linalg.norm(fn, axis=1, keepdims=True)
    fn = fn / np.maximum(ln, 1e-30)
    area = ln[:, 0] * 0.5
    geo = np.zeros_like(pos)
    for k in range(3):
        np.add.at(geo, idx[:, k], fn * area[:, None])
    gl = np.linalg.norm(geo, axis=1, keepdims=True)
    geo = geo / np.maximum(gl, 1e-30)

    dot = (nrm * geo).sum(axis=1)
    valid = gl[:, 0] > 1e-12
    d = dot[valid]
    print('stored vs geometric normal, dot product over %d vertices:' % len(d))
    print('  mean %+.4f   median %+.4f   min %+.4f   max %+.4f'
          % (d.mean(), np.median(d), d.min(), d.max()))
    for thr in (-0.9, -0.5, 0.0, 0.5, 0.9):
        print('    dot > %+.1f : %6.2f %%' % (thr, 100.0 * (d > thr).mean()))

    # radial test (assumes a roughly convex humanoid)
    c = pos.mean(axis=0)
    rad = pos - c
    rl = np.linalg.norm(rad, axis=1, keepdims=True)
    rad = rad / np.maximum(rl, 1e-30)
    dr = (nrm * rad).sum(axis=1)
    print('stored normal vs outward-from-centroid: mean %+.4f  '
          'fraction outward %.2f %%' % (dr.mean(), 100.0 * (dr > 0).mean()))

    # winding: signed volume of the mesh (positive => outward-facing CCW)
    v0, v1, v2 = tri[:, 0], tri[:, 1], tri[:, 2]
    vol = np.einsum('ij,ij->i', v0, np.cross(v1, v2)).sum() / 6.0
    print('signed volume of the whole mesh: %+.6f m^3  (positive => the '
          'triangle winding is outward)' % vol)
    return 0


if __name__ == '__main__':
    sys.exit(main())
