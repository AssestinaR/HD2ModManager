# -*- coding: utf-8 -*-
"""只读：核对一个 patch unit 的顶点缓冲与 `model.npz` 的 `ours_bind` 是否一一对应。

用途：若对应关系是固定仿射（或恒等），就能只替换顶点位置来造 ON 版游戏包，
而不必重跑 build_patch / fix_materials / fix_appearance。

usage:
    _probe_unit_vs_npz.py <patch_0> <npz_dir> [--unit 0xcae35ee15168145f]
"""
import argparse
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_unit as U
import hd2_patch as P


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('patch')
    ap.add_argument('npzdir')
    ap.add_argument('--unit', default='0xcae35ee15168145f')
    args = ap.parse_args()

    z = np.load(os.path.join(args.npzdir, 'model.npz'))
    ours = np.asarray(z['ours_bind'], np.float64)
    tris = np.asarray(z['tris'], np.int64)

    entries, _ = P.scan_entries(args.patch)
    e = next(x for x in entries if x['type_id'] == U.UNIT_ID
             and x['file_id'] == int(args.unit, 16))
    pl = P.entry_payload(e, args.patch)
    u = U.Unit.read(pl['toc'], pl['gpu'])
    ti = u.transform_info
    print('unit %s   meshes %d  streams %d' % (args.unit, len(u.mesh_infos),
                                               len(u.stream_infos)))
    for mi in u.mesh_infos:
        try:
            n = len(u.decode_vertices(mi.stream_index))
        except Exception as ex:
            n = 'ERR %r' % (ex,)
        print('   mesh lod=%s sections=%d stream=%s verts=%s'
              % (mi.lod_index, mi.num_sections, mi.stream_index, n))

    mi = max((m for m in u.mesh_infos if m.lod_index == 0), key=lambda m: m.num_sections)
    verts = u.decode_vertices(mi.stream_index)
    pos = np.asarray([v['position'] for v in verts], np.float64)
    M = np.asarray(ti.matrices[mi.transform_index], np.float64).reshape(4, 4)
    print('mesh transform_index %d  matrix:\n%s' % (mi.transform_index,
                                                    np.array2string(M, precision=6)))
    pos_w = pos @ M[:3, :3] + M[3, :3]

    print('npz ours_bind %s   unit stream %s' % (ours.shape, pos.shape))
    if pos.shape != ours.shape:
        print('!! 形状不同 —— 需要按索引重排，不能直接逐顶点比')
        # try: compare bounding boxes as a sanity check
        print('  npz  bbox %s .. %s' % (np.round(ours.min(0), 4), np.round(ours.max(0), 4)))
        print('  unit bbox %s .. %s' % (np.round(pos_w.min(0), 4), np.round(pos_w.max(0), 4)))
        return 1

    d = np.linalg.norm(pos_w - ours, axis=1)
    print('raw  : max %.6g  mean %.6g  n>1e-4 %d' % (d.max(), d.mean(), int((d > 1e-4).sum())))
    d2 = np.linalg.norm(pos - ours, axis=1)
    print('no-T : max %.6g  mean %.6g  n>1e-4 %d' % (d2.max(), d2.mean(), int((d2 > 1e-4).sum())))

    # also check triangles match
    idx = np.asarray(u.index_list(mi.stream_index), np.int64).reshape(-1, 3)
    print('unit tris %d   npz tris %d' % (len(idx), len(tris)))
    s = set(map(tuple, np.sort(tris, axis=1)[:2000].tolist()))
    s2 = set(map(tuple, np.sort(idx, axis=1)[:2000].tolist()))
    print('first-2000 tri overlap: %d / 2000' % len(s & s2))
    return 0


if __name__ == '__main__':
    sys.exit(main())
