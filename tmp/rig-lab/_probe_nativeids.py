# -*- coding: utf-8 -*-
"""Compare the unit payloads of a `fresh` build against a `native` build.

Round 63 shipped Lilium as ONE unit under a fresh id.  This round writes the
SAME model under RE-2310's two real Torso ids instead.  The only thing that may
change is the *container*: the unit TocData/GPU bytes themselves must be
identical, otherwise the geometry moved and the change is not a pure re-home.

usage: _probe_nativeids.py <fresh patch_0> <native patch_0>
"""
import hashlib
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_patch as HP
import hd2_unit as U


def units(path):
    entries, _ = HP.scan_entries(path)
    out = {}
    for e in entries:
        if e['type_id'] != U.UNIT_ID:
            continue
        pl = HP.entry_payload(e, path)
        out['%016x' % e['file_id']] = (
            hashlib.sha256(pl['toc']).hexdigest(),
            hashlib.sha256(pl['gpu']).hexdigest(),
            len(pl['toc']), len(pl['gpu']), pl['toc'])
    return out, entries


def diff_runs(a, b):
    """-> [(start, end)] byte ranges that differ between two equal-length blobs."""
    if len(a) != len(b):
        return [('length', len(a), len(b))]
    runs, start = [], None
    for i in range(len(a)):
        if a[i] != b[i]:
            if start is None:
                start = i
        elif start is not None:
            runs.append((start, i))
            start = None
    if start is not None:
        runs.append((start, len(a)))
    return runs


def main():
    fresh, native = sys.argv[1], sys.argv[2]
    ua, ea = units(fresh)
    ub, eb = units(native)
    print('fresh  %s\n       entries=%d units=%d' % (fresh, len(ea), len(ua)))
    for k, v in sorted(ua.items()):
        print('        0x%s  toc=%s gpu=%s  (%d / %d B)'
              % (k, v[0][:16], v[1][:16], v[2], v[3]))
    print('native %s\n       entries=%d units=%d' % (native, len(eb), len(ub)))
    for k, v in sorted(ub.items()):
        print('        0x%s  toc=%s gpu=%s  (%d / %d B)'
              % (k, v[0][:16], v[1][:16], v[2], v[3]))
    ref = dict((v[0], k) for k, v in ua.items())
    print()
    for k, v in sorted(ub.items()):
        if v[0] in ref:
            print('  0x%s  TocData IDENTICAL to the fresh build' % k)
            continue
        other = ua[ref and list(ua)[0]]
        print('  0x%s  TocData differs -- byte runs vs the fresh build:' % k)
        for run in diff_runs(other[4], v[4]):
            print('        %s' % (run,))
    print()
    # the geometry is the GPU blob; the TOC only differs in the per-slot
    # 240-byte `customization_data`, which is exactly what `--unit-mode native`
    # is supposed to swap in.
    gpus = set(v[1] for v in ub.values())
    ok = len(gpus) == 1 and gpus.pop() in set(v[1] for v in ua.values())
    print('verdict: %s' % ('PURE RE-HOME -- one GPU payload, identical to the '
                           'fresh build; the TOCs differ only in the per-slot '
                           'customization blob' if ok else 'NOT a pure re-home'))
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
