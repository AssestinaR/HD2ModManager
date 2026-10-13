# -*- coding: utf-8 -*-
"""READ-ONLY: prove a weight candidate changed ONLY the weight bytes.

Byte-diffs the carried unit's GPU payload between two packages and maps every
differing byte back to (stream, vertex, field).  Anything outside the
bone_weight / bone_index fields is a defect.

Usage: python _probe_weightdiff.py <before patch_0> <after patch_0>
"""
import sys

import hd2_patch as P
import hd2_unit as U

UNIT = 0xcae35ee15168145f


def payload(patch):
    entries, _ = P.scan_entries(patch)
    e = next(x for x in entries
             if x['type_id'] == U.UNIT_ID and x['file_id'] == UNIT)
    return P.entry_payload(e, patch)


def field_map(u):
    """(stream, byte lo, byte hi, field name, stride) per component.

    Offsets are relative to the START OF THAT STREAM's vertex buffer, so the
    caller must diff each stream separately (the streams are independent
    buffers; `gpu_vertex` is already split per stream).
    """
    out = []
    for si, s in enumerate(u.stream_infos):
        off = 0
        for c in s.components:
            size = U.component_size(c.format, u.version)
            name = {0: 'position', 1: 'normal', 4: 'uv', 5: 'color',
                    6: 'bone_index', 7: 'bone_weight'}.get(c.type, 'type%d' % c.type)
            out.append((si, off, off + size, name, s.vertex_stride))
            off += size
    return out


def main():
    a = payload(sys.argv[1])
    b = payload(sys.argv[2])
    print('before %d B, after %d B' % (len(a['gpu']), len(b['gpu'])))
    assert len(a['gpu']) == len(b['gpu']), 'payload size changed'
    ua = U.Unit.read(a['toc'], a['gpu'])
    ub = U.Unit.read(b['toc'], b['gpu'])
    fields = field_map(ua)

    import collections
    per_field = collections.Counter()
    per_stream = collections.Counter()
    touched = set()
    outside = 0
    total_diff = 0
    for si, (va, vb) in enumerate(zip(ua.gpu_vertex, ub.gpu_vertex)):
        if len(va) != len(vb):
            print('!! stream %d size changed' % si)
            return 1
        diff = [i for i in range(len(va)) if va[i] != vb[i]]
        total_diff += len(diff)
        if not diff:
            continue
        per_stream[si] = len(diff)
        comp = [f for f in fields if f[0] == si]
        stride = ua.stream_infos[si].vertex_stride
        for off in diff:
            base = (off // stride) * stride
            for _s, lo, hi, name, _st in comp:
                if lo <= off - base < hi:
                    per_field[name] += 1
                    touched.add((si, off // stride))
                    break
            else:
                outside += 1
    print('differing bytes: %d of %d (%.3f%%)'
          % (total_diff, len(a['gpu']), 100.0 * total_diff / len(a['gpu'])))
    print('by field :', dict(per_field))
    print('by stream:', dict(per_stream))
    print('bytes outside any declared vertex field:', outside)
    print('vertices touched: %d' % len(touched))
    ok = outside == 0 and set(per_field) <= {'bone_weight', 'bone_index'}
    print('RESULT: %s' % ('PASS - only weight bytes changed' if ok else 'FAIL'))
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
