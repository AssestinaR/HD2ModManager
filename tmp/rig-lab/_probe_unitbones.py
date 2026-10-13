# -*- coding: utf-8 -*-
"""Name every TransformInfo entry of a real unit and compare against our rig."""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_patch as P
import hd2_unit as U
from hd2_hash import murmur32_hash, load_bonehash

HERE = os.path.dirname(os.path.abspath(__file__))
SDK = os.path.join(HERE, '..', '..', 'docs', 'HD2SDK-CommunityEdition-3.8.0')
BONEHASH = os.path.join(SDK, 'hashlists', 'bonehash.txt')

names = load_bonehash(BONEHASH)
print('bonehash entries: %d' % len(names))
print('murmur32("boss") = %d  expected 2601604451  %s'
      % (murmur32_hash(b'boss'), murmur32_hash(b'boss') == 2601604451))

PATCHDIR = 'E:/mod/Lunalice-Visible-Virtual-Model'
tocs = [os.path.join(PATCHDIR, f) for f in os.listdir(PATCHDIR)
        if '.patch_' in f and not f.endswith(('.stream', '.gpu_resources'))]
entries, _ = P.scan_entries(tocs[0])
for e in entries:
    if e['type_id'] != U.UNIT_ID:
        continue
    pl = P.entry_payload(e, tocs[0])
    u = U.Unit.read(pl['toc'], pl['gpu'])
    ti = u.transform_info
    print('\n=== unit 0x%016x  %d transforms ===' % (e['file_id'], ti.count))
    for i in range(ti.count):
        h = ti.hashes[i]
        m = ti.matrices[i]
        print('  [%2d] 0x%08x %-34s parent %2d  pos (%+.4f %+.4f %+.4f)'
              % (i, h, names.get(h, '???'), ti.entries[i][1], m[3], m[7], m[11]))
        print('        row0 %s' % [round(v, 4) for v in m[0:4]])
        print('        row1 %s' % [round(v, 4) for v in m[4:8]])
        print('        row2 %s' % [round(v, 4) for v in m[8:12]])
    for bi_i, bi in enumerate(u.bone_infos):
        print('  boneinfo[%d] real_indices -> %s'
              % (bi_i, [(i, names.get(ti.hashes[i], '?')) for i in bi.real_indices]))
