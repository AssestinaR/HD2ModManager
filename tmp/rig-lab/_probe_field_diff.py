# -*- coding: utf-8 -*-
"""READ-ONLY: which vertex FIELDS changed between two patches, byte for byte.

Round 75 guard.  A geometry pass must touch only `position` (and, deliberately,
`normal`); if it also moved UVs, weights or the bone index, something is wrong.
And if the normal offset was never written, this shows it.

usage:
    _probe_field_diff.py <before> <after> [--unit 0x...]
"""
import argparse
import os
import sys
from collections import Counter

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_patch as P
import hd2_unit as U

TYPE_NAME = {0: 'position', 1: 'normal', 2: 'tangent', 3: 'bitangent', 4: 'uv',
             5: 'color', 6: 'bone_index', 7: 'bone_weight'}


def payload(patch, unit):
    entries, _ = P.scan_entries(patch)
    e = next(x for x in entries if x['type_id'] == U.UNIT_ID and x['file_id'] == unit)
    pl = P.entry_payload(e, patch)
    return pl['toc'], pl['gpu']


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('before')
    ap.add_argument('after')
    ap.add_argument('--unit', default='0xcae35ee15168145f')
    args = ap.parse_args()
    unit = int(args.unit, 16)

    toc0, gpu0 = payload(args.before, unit)
    toc1, gpu1 = payload(args.after, unit)
    print('toc identical: %s   (%d vs %d bytes)' % (toc0 == toc1, len(toc0), len(toc1)))
    print('gpu identical: %s   (%d vs %d bytes)' % (gpu0 == gpu1, len(gpu0), len(gpu1)))
    if len(gpu0) != len(gpu1):
        return 1

    u = U.Unit.read(toc0, gpu0)
    a = np.frombuffer(gpu0, np.uint8)
    b = np.frombuffer(gpu1, np.uint8)
    diff = a != b
    total = int(diff.sum())
    print('bytes changed: %d of %d  (%.4f%%)' % (total, len(a), 100.0 * total / len(a)))

    # classify every changed byte by the field it lands in, per stream
    print()
    print('  %-6s %-10s %10s %10s' % ('stream', 'field', 'bytes', 'vertices'))
    grand = Counter()
    for si_idx, si in enumerate(u.stream_infos):
        if not si.vertex_buffer_size or not si.num_vertices:
            continue
        stride = si.vertex_stride
        base0 = si.vertex_buffer_offset
        fields = []
        off = 0
        for c in si.components:
            sz = U.component_size(c.format, u.version)
            fields.append((TYPE_NAME.get(c.type, 'type%d' % c.type), off, sz))
            off += sz
        idx = np.arange(si.num_vertices)
        rows = base0 + idx[:, None] * stride + np.arange(stride)[None, :]
        d = diff[rows.ravel()].reshape(si.num_vertices, stride)
        if not d.any():
            continue
        for name, foff, fsz in fields:
            sub = d[:, foff:foff + fsz]
            nbytes = int(sub.sum())
            if not nbytes:
                continue
            nverts = int((sub.any(axis=1)).sum())
            print('  %-6d %-10s %10d %10d' % (si_idx, name, nbytes, nverts))
            grand[name] += nbytes
    print()
    print('  total by field: %s' % dict(grand))
    # bytes that fell outside every declared field
    covered = np.zeros(len(a), bool)
    for si_idx, si in enumerate(u.stream_infos):
        if not si.vertex_buffer_size or not si.num_vertices:
            continue
        base0 = si.vertex_buffer_offset
        covered[base0:base0 + si.vertex_buffer_size] = True
    outside = int((diff & ~covered).sum())
    print('  changed bytes OUTSIDE the vertex buffers: %d' % outside)
    return 0


if __name__ == '__main__':
    sys.exit(main())
