# -*- coding: utf-8 -*-
"""Independent diff of two candidate patches: what actually changed?

  * TOC byte equality (the whole entry table, headers, materials, mesh, ...)
  * for the GPU blob: every differing byte must fall inside one of the
    expected texture-pixel ranges, and nowhere else.

usage: _probe_m6diff.py <old.patch_0> <new.patch_0> [<old.gpu> <new.gpu>]
"""
import hashlib
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_asset as A
import hd2_patch as HP


def sha(p):
    h = hashlib.sha256()
    with open(p, 'rb') as f:
        for b in iter(lambda: f.read(1 << 20), b''):
            h.update(b)
    return h.hexdigest()


def main():
    old, new = sys.argv[1], sys.argv[2]
    old_gpu = sys.argv[3] if len(sys.argv) > 3 else old + '.gpu_resources'
    new_gpu = sys.argv[4] if len(sys.argv) > 4 else new + '.gpu_resources'

    print('old toc %d B  sha256 %s' % (os.path.getsize(old), sha(old)[:24]))
    print('new toc %d B  sha256 %s' % (os.path.getsize(new), sha(new)[:24]))
    a = open(old, 'rb').read()
    b = open(new, 'rb').read()
    print('TOC byte-identical : %s' % (a == b))
    if a != b:
        d = [i for i in range(min(len(a), len(b))) if a[i] != b[i]]
        print('  %d differing bytes, first at %s' % (len(d), d[:8]))

    ga = open(old_gpu, 'rb').read()
    gb = open(new_gpu, 'rb').read()
    print('GPU sizes %d / %d   byte-identical: %s' % (len(ga), len(gb), ga == gb))

    entries, _ = HP.scan_entries(new)
    ranges = []
    for e in entries:
        if e['type_id'] != A.TEXTURE_TYPE_ID:
            continue
        pl = HP.entry_payload(e, new)
        ranges.append((e['gpu_offset'], e['gpu_offset'] + len(pl['gpu']), e['file_id']))
    ranges.sort()
    print('  %d texture ranges in the GPU blob' % len(ranges))

    diffs = [i for i in range(len(ga)) if ga[i] != gb[i]]
    print('  %d differing GPU bytes' % len(diffs))
    inside = sum(1 for i in diffs if any(lo <= i < hi for lo, hi, _ in ranges))
    print('  inside a texture range: %d   outside: %d' % (inside, len(diffs) - inside))
    if diffs:
        per = {}
        for i in diffs:
            for lo, hi, fid in ranges:
                if lo <= i < hi:
                    per[fid] = per.get(fid, 0) + 1
                    break
        print('  per-texture changed bytes:')
        for lo, hi, fid in ranges:
            if fid in per:
                print('    0x%016x  %8d / %8d  (%.1f%%)'
                      % (fid, per[fid], hi - lo, 100.0 * per[fid] / (hi - lo)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
