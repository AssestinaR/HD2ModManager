# -*- coding: utf-8 -*-
"""Diff two packages entry by entry: prove a candidate changed only what it should.

Usage: python _probe_canddiff.py <baseline patch_0> <candidate patch_0>
"""
import collections
import sys

import hd2_patch as HP
import hd2_asset as A

TYPE_NAME = {A.MATERIAL_TYPE_ID: 'Material', A.TEXTURE_TYPE_ID: 'Texture',
             0xE0A48D0BE9A7453F: 'Unit', 0x18DEAD01056B72E9: 'Bone'}


def main():
    base, cand = sys.argv[1], sys.argv[2]
    eb, _ = HP.scan_entries(base)
    ec, _ = HP.scan_entries(cand)
    print('entries: baseline %d, candidate %d' % (len(eb), len(ec)))
    if len(eb) != len(ec):
        print('!! entry count differs')
        return 1
    diff, toc_diff, gpu_diff, ident_diff = [], [], [], []
    for a, b in zip(eb, ec):
        if a['type_id'] != b['type_id']:
            print('!! entry %d type changed' % a['index'])
            return 1
        if a['file_id'] != b['file_id']:
            # a deliberate substitution (the shared PBR texture gets new content
            # and therefore a content-addressed new id)
            ident_diff.append((a, b))
            continue
        pa = HP.entry_payload(a, base)
        pb = HP.entry_payload(b, cand)
        if pa['toc'] != pb['toc']:
            toc_diff.append(a)
        if pa['gpu'] != pb['gpu']:
            gpu_diff.append(a)
    print('id-substituted entries: %d' % len(ident_diff))
    for a, b in ident_diff:
        print('    0x%016x -> 0x%016x  %s' % (a['file_id'], b['file_id'],
                                              TYPE_NAME.get(a['type_id'], '?')))
    print('toc-changed entries : %d' % len(toc_diff))
    print('gpu-changed entries : %d' % len(gpu_diff))
    for label, lst in (('toc', toc_diff), ('gpu', gpu_diff)):
        c = collections.Counter(TYPE_NAME.get(e['type_id'], '0x%016x' % e['type_id'])
                                for e in lst)
        print('  %s by type: %s' % (label, dict(c)))
    for e in toc_diff[:20]:
        print('    toc 0x%016x %s' % (e['file_id'],
                                      TYPE_NAME.get(e['type_id'], '?')))
    for e in gpu_diff[:20]:
        print('    gpu 0x%016x %s' % (e['file_id'],
                                      TYPE_NAME.get(e['type_id'], '?')))
    return 0


if __name__ == '__main__':
    sys.exit(main())
