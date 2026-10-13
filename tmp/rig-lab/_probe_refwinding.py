"""Does the SHIPPING reference unit have consistent winding?

We decode the reference unit's own vertex normals (`unk_normal`, oct 10-10) and
compare each triangle's geometric normal against them.  The Chiffon/Lunalice
patch renders correctly in game, so whatever it does is the behaviour we must
match.  If it comes out ~100% agree and ours is 70%, OUR winding is broken.
"""
import math
import os
import struct
import sys
from collections import Counter

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_patch as HP
import hd2_unit as U

TOC = r'E:\mod\【4.28】Chiffon-LUNALICE替换RE-2310\9ba626afa44a3aa3.patch_0'


def oct_decode(rx, ry):
    n = np.stack([rx, ry, 1.0 - np.abs(rx) - np.abs(ry)], axis=-1)
    t = np.clip(-n[..., 2], 0.0, 1.0)
    n[..., 0] += np.where(n[..., 0] >= 0, -t, t)
    n[..., 1] += np.where(n[..., 1] >= 0, -t, t)
    return n / np.maximum(np.linalg.norm(n, axis=-1, keepdims=True), 1e-30)


def main():
    entries, _raw = HP.scan_entries(TOC)
    for e in entries:
        if e['type_id'] != U.UNIT_ID:
            continue
        pl = HP.entry_payload(e, TOC)
        u = U.Unit.read(pl['toc'], pl['gpu'])
        # pick the skinned stream (the one with the most vertices)
        si = max(range(len(u.stream_infos)), key=lambda i: u.stream_infos[i].num_vertices)
        s = u.stream_infos[si]
        vs = u.decode_vertices(si)
        idx = u.index_list(si)
        pos = np.asarray([v['position'] for v in vs], np.float64)
        nrm_raw = np.asarray([v['normal'] for v in vs], np.int64)
        rx = ((nrm_raw & 0x3ff) * (2.0 / 1023.0) - 1.0)
        ry = (((nrm_raw >> 10) & 0x3ff) * (2.0 / 1023.0) - 1.0)
        nrm = oct_decode(rx, ry)
        T = np.asarray(idx, np.int64).reshape(-1, 3)
        T = T[T.max(1) < len(pos)]
        v0, v1, v2 = pos[T[:, 0]], pos[T[:, 1]], pos[T[:, 2]]
        fn = np.cross(v1 - v0, v2 - v0)
        ln = np.linalg.norm(fn, axis=1)
        ok = ln > 1e-12
        fn = fn[ok] / ln[ok, None]
        T = T[ok]
        mn = (nrm[T[:, 0]] + nrm[T[:, 1]] + nrm[T[:, 2]]) / 3.0
        dot = (fn * mn).sum(1)
        print('%-18s stream %d nv=%d tris=%d  agree %.1f%%  disagree %.1f%%'
              % ('0x%x' % e['file_id'], si, len(pos), len(T),
                 100 * (dot > 0).mean(), 100 * (dot <= 0).mean()))
        print('     dot p5/p25/p50/p75/p95 = %s'
              % np.round(np.percentile(dot, [5, 25, 50, 75, 95]), 4))


if __name__ == '__main__':
    main()
