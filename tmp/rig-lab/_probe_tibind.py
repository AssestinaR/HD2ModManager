# -*- coding: utf-8 -*-
"""Does the runtime have enough information to bind our 107 bones?

The unit's `TransformInfo` is a *sparse* declaration of the nodes the unit cares
about.  The runtime matches a unit node to a skeleton bone by the **name hash**
in `TransformInfo.hashes` (murmur64(name) >> 32) -- the matrix in the unit is
only the *bind* pose.  If our appended 107 bones carry the wrong hashes, the
game will skin the mesh to garbage.

So: compare, slot by slot,

  * our unit's TransformInfo hashes  vs  the avatar toc's TransformInfo hashes
  * the `entries` (incriment, parent) graph
  * the reference torso's own 23 nodes, to see which slots it actually uses
"""
import os
import struct
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_hash as H
import hd2_patch as HP
import hd2_unit as U

OURS = r'E:\mod\Lunalice-RigTest\9ba626afa44a3aa3.patch_0'
REF = r'E:\mod\Lunalice-Visible-Virtual-Model\9ba626afa44a3aa3.patch_0'
AVATAR = os.path.join(HERE, 'avatar', 'unit_0x4d1c334d294dfa97.toc')
META = os.path.join(HERE, 'out', 'blender', 'meta.json')


def load_unit(path, file_id=None):
    entries, _ = HP.scan_entries(path)
    for e in entries:
        if e['type_id'] == U.UNIT_ID and (file_id is None or e['file_id'] == file_id):
            pl = HP.entry_payload(e, path)
            return e, U.Unit.read(pl['toc'], pl['gpu'])
    raise SystemExit('no unit')


import json

ours_e, ours = load_unit(OURS)
ref_e, ref = load_unit(REF, 0xD841B16544C8FBAE)
raw = open(AVATAR, 'rb').read()
ti_off = struct.unpack_from('<I', raw, 0x34)[0]
av = U.Unit.read(raw, b'')

meta = json.load(open(META, encoding='utf-8'))
names = list(meta['bones'])
print('avatar bones in meta.json : %d' % len(names))
print('avatar TransformInfo count: %d' % av.transform_info.count)
print('our  TransformInfo count  : %d' % ours.transform_info.count)
print('ref  TransformInfo count  : %d' % ref.transform_info.count)

# meta.json bone names vs the avatar toc's hashes
def nh(n):
    return H.murmur64_hash(n.encode('utf-8')) >> 32

mism = [k for k in range(min(107, av.transform_info.count))
        if nh(names[k]) != av.transform_info.hashes[k]]
print('meta name-hash vs avatar toc hash: %d mismatch' % len(mism))
if mism[:5]:
    for k in mism[:5]:
        print('   k=%d %-24s meta=0x%08x avatar=0x%08x'
              % (k, names[k], nh(names[k]), av.transform_info.hashes[k]))

# name lookup for hashes
bh = {}
try:
    for line in open(os.path.join(HERE, 'bonehash.txt'), encoding='utf-8', errors='replace'):
        p = line.split()
        if len(p) >= 2:
            try:
                bh[int(p[0], 16)] = p[1]
            except ValueError:
                pass
except OSError as exc:
    print('bonehash.txt: %s' % exc)
print('bonehash.txt entries: %d' % len(bh))


def hname(h):
    return bh.get(h, '?')


print()
print('=== ours: slots 0..25 ===')
for i in range(min(26, ours.transform_info.count)):
    m = np.asarray(ours.transform_info.matrices[i], np.float64).reshape(4, 4)
    ident = np.abs(m - np.eye(4)).max() < 1e-9
    h = ours.transform_info.hashes[i]
    print('  [%3d] hash=0x%08x %-22s parent=%-4s ident=%s  T=%s'
          % (i, h, hname(h), ours.transform_info.entries[i][1], ident,
             np.round(m[3, :3], 4)))

print()
print('=== avatar: slots 0..15 + 100..106 ===')
for i in list(range(16)) + list(range(100, av.transform_info.count)):
    m = np.asarray(av.transform_info.matrices[i], np.float64).reshape(4, 4)
    ident = np.abs(m - np.eye(4)).max() < 1e-9
    h = av.transform_info.hashes[i]
    print('  [%3d] hash=0x%08x %-22s parent=%-4s ident=%s  T=%s'
          % (i, h, hname(h), av.transform_info.entries[i][1], ident,
             np.round(m[3, :3], 4)))

print()
print('=== ours[23+k] vs avatar[k] for k in 0..106 ===')
bad_h = bad_m = bad_p = 0
for k in range(107):
    a = 23 + k
    if ours.transform_info.hashes[a] != av.transform_info.hashes[k]:
        bad_h += 1
        if bad_h <= 5:
            print('  hash mismatch k=%d ours=0x%08x avatar=0x%08x'
                  % (k, ours.transform_info.hashes[a], av.transform_info.hashes[k]))
    mo = np.asarray(ours.transform_info.matrices[a], np.float64)
    ma = np.asarray(av.transform_info.matrices[k], np.float64)
    if np.abs(mo - ma).max() > 1e-9:
        bad_m += 1
    # parent: ours stores absolute index? compare against avatar's raw parent
    po = ours.transform_info.entries[a]
    pa = av.transform_info.entries[k]
    if po != pa:
        bad_p += 1
print('  hash mismatches : %d / 107' % bad_h)
print('  matrix mismatches: %d / 107' % bad_m)
print('  entry mismatches : %d / 107   (ours[23+k]=%s vs avatar[k]=%s)'
      % (bad_p, ours.transform_info.entries[23], av.transform_info.entries[0]))

print()
print('=== reference torso: which slots are non-identity / what hashes ===')
for i in range(ref.transform_info.count):
    m = np.asarray(ref.transform_info.matrices[i], np.float64).reshape(4, 4)
    ident = np.abs(m - np.eye(4)).max() < 1e-9
    h = ref.transform_info.hashes[i]
    print('  [%3d] hash=0x%08x %-22s parent=%-4s ident=%s  T=%s'
          % (i, h, hname(h), ref.transform_info.entries[i][1], ident,
             np.round(m[3, :3], 4)))

print()
print('=== reference torso real_indices -> hash ===')
bi = ref.bone_infos[0]
print('  num_bones=%d real_indices=%s' % (bi.num_bones, bi.real_indices))
for ri in bi.real_indices:
    h = ref.transform_info.hashes[ri]
    print('    slot %-3d hash=0x%08x %s' % (ri, h, hname(h)))
