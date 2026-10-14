# -*- coding: utf-8 -*-
"""Round 77c: compare our shipped arm skeleton against vanilla's arm skin.

Read-only.  Answers one question: are the arm bone HEADS in our unit the HD2
standard skeleton (same as vanilla's arm skin unit), or the source skeleton?

usage:
    _probe_arm_heads_vs_vanilla.py
"""
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_hash as H
import hd2_unit as U

SDK = 'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
BONES = H.load_bonehash(os.path.join(SDK, 'hashlists', 'bonehash.txt'))
NAMES = ('l_shoulder', 'l_elbow', 'l_hand', 'l_hand_twist', 'l_wrist',
         'r_shoulder', 'r_elbow', 'r_hand')

OURS = 'E:/tmp/hd2-mgroot/re2310-full/unit/unit_0xcae35ee15168145f.toc'
VANI = 'E:/tmp/hd2-mgroot/ds10/unit/unit_0x4a213e0ae8acf74b.toc'
VANI2 = 'E:/tmp/hd2-mgroot/ds10/unit/unit_0xa1a7563e6ac8b8f8.toc'


def heads_of(toc_path):
    toc = open(toc_path, 'rb').read()
    u = U.Unit.read(toc, b'')
    ti, bi = u.transform_info, u.bone_infos[0]
    out = {}
    for slot, ti_idx in enumerate(bi.real_indices):
        nm = BONES.get(ti.hashes[ti_idx])
        if nm in NAMES:
            m = ti.matrices[ti_idx]
            out[nm] = np.array([m[12], m[13], m[14]], np.float64)
    return out, len(bi.real_indices)


def dump(tag, path):
    hd, nb = heads_of(path)
    print('=== %s   (%s)' % (tag, os.path.basename(path)))
    print('    %d bones total' % nb)
    for nm in NAMES:
        if nm in hd:
            print('    %-14s %s' % (nm, np.array2string(hd[nm], precision=5)))
    return hd


a = dump('OURS  armor unit (shipped)', OURS)
b = dump('VANILLA arm skin unit', VANI)
try:
    c = dump('VANILLA other unit', VANI2)
except Exception as ex:
    c = {}
    print('    (%s: %s)' % (os.path.basename(VANI2), ex))

print()
print('=== deltas vs vanilla arm skin (mm)')
for nm in NAMES:
    if nm in a and nm in b:
        print('    %-14s  ours-vanilla  %8.3f' % (nm, float(np.linalg.norm(a[nm] - b[nm])) * 1000))
print()
for tag, hd in (('OURS', a), ('VANILLA', b)):
    if 'l_elbow' in hd and 'l_hand' in hd:
        print('    %-8s |l_elbow -> l_hand| = %7.2f mm' %
              (tag, float(np.linalg.norm(hd['l_hand'] - hd['l_elbow'])) * 1000))
