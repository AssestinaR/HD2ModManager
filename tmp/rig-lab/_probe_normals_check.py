# -*- coding: utf-8 -*-
"""READ-ONLY: do the stored (oct-encoded) normals still agree with the geometry?

Round 75: the alignment pass moves vertices, which shears the surface, so it also
rewrites the normals via the inverse transpose of the local deformation.  If that
went wrong the arm renders dark or with stray highlights (the round-60 failure).

This compares the decoded stored normal with the area-weighted geometric normal
rebuilt from the index buffer, before and after, so the two can be put side by
side.

usage:
    _probe_normals_check.py <patch_0> [--unit 0x...]
"""
import argparse
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_patch as P
import hd2_unit as U


def oct_decode(u32):
    a = np.asarray(u32, np.uint32)
    x = (a & 0x3FF).astype(np.float64) / 511.5 - 1.0
    y = ((a >> 10) & 0x3FF).astype(np.float64) / 511.5 - 1.0
    z = 1.0 - (np.abs(x) + np.abs(y))
    t = np.clip(-z, 0.0, 1.0)
    x = x + np.where(x >= 0, -t, t)
    y = y + np.where(y >= 0, -t, t)
    n = np.stack([x, y, z], axis=1)
    return n / np.maximum(np.linalg.norm(n, axis=1, keepdims=True), 1e-30)


def check(label, patch, unit):
    entries, _ = P.scan_entries(patch)
    e = next(x for x in entries if x['type_id'] == U.UNIT_ID and x['file_id'] == unit)
    pl = P.entry_payload(e, patch)
    u = U.Unit.read(pl['toc'], pl['gpu'])
    verts = u.decode_vertices(0)
    pos = np.asarray([v['position'] for v in verts], np.float64)
    nrm = oct_decode(np.asarray([v['normal'] for v in verts], np.uint32))

    idx = np.frombuffer(u.gpu_index[0], np.uint32).reshape(-1, 3).astype(np.int64)
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

    valid = gl[:, 0] > 1e-12
    d = (nrm * geo).sum(axis=1)[valid]
    print('%-14s %7d verts   stored-vs-geometric normal dot:  '
          'mean %+.4f  median %+.4f  min %+.4f   frac>0.9 %.2f%%   frac<0 %.2f%%'
          % (label, len(pos), d.mean(), np.median(d), d.min(),
             100.0 * (d > 0.9).mean(), 100.0 * (d < 0).mean()))
    return d


def raw(patch, unit):
    entries, _ = P.scan_entries(patch)
    e = next(x for x in entries if x['type_id'] == U.UNIT_ID and x['file_id'] == unit)
    pl = P.entry_payload(e, patch)
    u = U.Unit.read(pl['toc'], pl['gpu'])
    verts = u.decode_vertices(0)
    pos = np.asarray([v['position'] for v in verts], np.float64)
    nrm = oct_decode(np.asarray([v['normal'] for v in verts], np.uint32))
    idx = np.frombuffer(u.gpu_index[0], np.uint32).reshape(-1, 3).astype(np.int64)
    return pos, nrm, idx


def geo_normals(pos, idx):
    tri = pos[idx]
    fn = np.cross(tri[:, 1] - tri[:, 0], tri[:, 2] - tri[:, 0])
    ln = np.linalg.norm(fn, axis=1, keepdims=True)
    fn = fn / np.maximum(ln, 1e-30)
    geo = np.zeros_like(pos)
    for k in range(3):
        np.add.at(geo, idx[:, k], fn * (ln[:, 0] * 0.5)[:, None])
    gl = np.linalg.norm(geo, axis=1, keepdims=True)
    return geo / np.maximum(gl, 1e-30), gl[:, 0] > 1e-12


def focus(before, after, unit):
    """Only the vertices the pass actually moved: did their stored normal stay
    consistent with the (now sheared) geometry?"""
    p0, n0, idx = raw(before, unit)
    p1, n1, _ = raw(after, unit)
    moved = np.linalg.norm(p1 - p0, axis=1) > 1e-6
    g0, v0 = geo_normals(p0, idx)
    g1, v1 = geo_normals(p1, idx)
    m = moved & v0 & v1
    d0 = (n0 * g0).sum(axis=1)[m]
    d1 = (n1 * g1).sum(axis=1)[m]
    ang0 = np.degrees(np.arccos(np.clip(d0, -1, 1)))
    ang1 = np.degrees(np.arccos(np.clip(d1, -1, 1)))
    print()
    print('focused on the %d MOVED vertices:' % int(m.sum()))
    print('  stored-vs-geometric angle   before: mean %6.2f deg  median %6.2f  p95 %6.2f'
          % (ang0.mean(), np.median(ang0), np.percentile(ang0, 95)))
    print('                              after : mean %6.2f deg  median %6.2f  p95 %6.2f'
          % (ang1.mean(), np.median(ang1), np.percentile(ang1, 95)))
    chg = np.degrees(np.arccos(np.clip((n0 * n1).sum(axis=1)[m], -1, 1)))
    print('  normal rotated by           mean %6.2f deg  median %6.2f  max %6.2f'
          % (chg.mean(), np.median(chg), chg.max()))
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('patches', nargs='*')
    ap.add_argument('--unit', default='0xcae35ee15168145f')
    ap.add_argument('--labels', default=None)
    ap.add_argument('--focus', nargs=2, default=None,
                    help='before after: restrict the check to moved vertices')
    args = ap.parse_args()
    unit = int(args.unit, 16)
    if args.focus:
        return focus(args.focus[0], args.focus[1], unit)
    labels = args.labels.split(',') if args.labels else \
        [os.path.basename(os.path.dirname(p)) for p in args.patches]
    print()
    for lab, p in zip(labels, args.patches):
        check(lab, p, unit)
    return 0


if __name__ == '__main__':
    sys.exit(main())
