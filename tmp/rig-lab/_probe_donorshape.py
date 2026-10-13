# -*- coding: utf-8 -*-
"""Read-only: is the Chiffon donor unit a torso or a whole character?

If the donor's visible mesh really spans the whole body (feet to head) while
its stored MeshInfo bounds are torso-sized, then two things follow:

  * its bounds are *stale* -- the very defect we are fixing in our own build;
  * a **working, shipped** full-body unit keeps torso-sized culling shells, so
    "scale the shells to the whole body" (our round-59 build) is the unproven
    deviation, not the baseline.

usage: _probe_donorshape.py
"""
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_patch as P
import hd2_unit as U

DONOR_PATCH = r'E:\mod\【4.28】Chiffon-LUNALICE替换RE-2310\9ba626afa44a3aa3.patch_0'
DONOR_UNIT = 0x9841B16544C8FBAE


def main():
    entries, _ = P.scan_entries(DONOR_PATCH)
    e = next(x for x in entries if x['file_id'] == DONOR_UNIT and x['type_id'] == U.UNIT_ID)
    pl = P.entry_payload(e, DONOR_PATCH)
    u = U.Unit.read(pl['toc'], pl['gpu'])
    for si in (0, 1):
        vs = u.decode_vertices(si)
        p = np.asarray([v['position'] for v in vs], np.float64)
        z = p[:, 2]
        print('stream %d: n=%d  bbox min=%s max=%s' % (si, len(p), np.round(p.min(axis=0), 4),
                                                       np.round(p.max(axis=0), 4)))
        for t in (0.0, 0.2, 0.5, 0.9, 1.0, 1.2, 1.4, 1.6, 1.8, 2.0):
            print('    z < %.1f : %7d  (%.1f%%)' % (t, int((z < t).sum()),
                                                    100.0 * (z < t).mean()))
        print('    sample positions:', np.round(p[:4], 4).tolist())
        for q in (0.0, 0.01, 0.1, 1.0, 99.0, 99.9, 99.99, 100.0):
            print('      p%-6s min=%s max=%s' % (q, np.round(np.percentile(p, q, axis=0), 4),
                                                 np.round(np.percentile(p, 100 - q, axis=0), 4)))
        # how many vertices are wide (|x| > 0.45) -- arms out?
        print('    |x|>0.45 : %d ; |y|>0.35 : %d' % (int((np.abs(p[:, 0]) > 0.45).sum()),
                                                     int((np.abs(p[:, 1]) > 0.35).sum())))


if __name__ == '__main__':
    main()
