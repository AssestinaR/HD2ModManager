# -*- coding: utf-8 -*-
"""Where does a unit's bone REST POSE actually live?

Three candidates and they disagree, so this probe prints all three side by side:
  A. the unit's own TransformInfo (local TRS hierarchy)
  B. the unit's BoneInfo matrices (claimed to be mesh-relative inverse bind)
  C. the `Bone` asset the unit points at through header.bones_ref

Also cross-checks the unit's bone hashes against the avatar toc's 107-bone rig
(tmp/rig-lab/avatar/unit_0x4d1c334d294dfa97.toc) that the retarget uses.
usage: _probe_bonepose.py
"""
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_patch
import hd2_unit

PATCH = r'E:\mod\【4.28】Chiffon-LUNALICE替换RE-2310\9ba626afa44a3aa3.patch_0'
BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
AVATAR = 'avatar/unit_0x4d1c334d294dfa97.toc'

names = hd2_patch.bone_hash_file(BH)
entries, _raw = hd2_patch.scan_entries(PATCH)

print('== per-unit bones_ref ==')
for e in entries:
    if e['type_id'] != hd2_unit.UNIT_ID:
        continue
    toc = hd2_patch.entry_payload(e, PATCH)['toc']
    u = hd2_unit.Unit.read(toc, b'')
    print('  0x%016x  bones_ref 0x%016x  ntf %-3d' % (e['file_id'], u.bones_ref, u.transform_info.count))

print('\n== the Bone assets in the patch ==')
for e in entries:
    if e['type_id'] != hd2_unit.BONE_ID:
        continue
    toc = hd2_patch.entry_payload(e, PATCH)['toc']
    print('  Bone 0x%016x  toc %d B' % (e['file_id'], len(toc)))
    print('    head: %s' % toc[:64].hex(' '))
    print('    as u32: %s' % list(struct.unpack_from('<16I', toc, 0)))
    print('    as u64: %s' % ['0x%x' % v for v in struct.unpack_from('<8Q', toc, 0)])

# ---- A: the unit's own TransformInfo, non-identity entries only -------------
print('\n== A. unit TransformInfo: which transforms are NOT identity? ==')
for e in entries:
    if e['type_id'] != hd2_unit.UNIT_ID:
        continue
    toc = hd2_patch.entry_payload(e, PATCH)['toc']
    u = hd2_unit.Unit.read(toc, b'')
    ti = u.transform_info
    nonid = []
    for i in range(ti.count):
        m = ti.matrices[i]
        if any(abs(m[j * 4 + k] - (1.0 if j == k else 0.0)) > 1e-6
               for j in range(4) for k in range(4)):
            nonid.append(i)
    print('  0x%016x ntf %-3d non-identity %s' % (e['file_id'], ti.count, nonid))

# ---- B: BoneInfo matrices vs the transform positions ------------------------
e = next(x for x in entries if x['file_id'] == 0x9841b16544c8fbae)
toc = hd2_patch.entry_payload(e, PATCH)['toc']
u = hd2_unit.Unit.read(toc, b'')
bi = u.bone_infos[0]
ti = u.transform_info
print('\n== B. unit 0x9841b16544c8fbae: BoneInfo slot -> transform ==')
for slot, tf in enumerate(bi.real_indices):
    m = bi.bones[slot]
    h = ti.hashes[tf]
    print('  slot %2d -> tf %2d hash %-11d %-16s  ibind T=(%+.4f,%+.4f,%+.4f)'
          % (slot, tf, h, names.get(h, ''), m[12], m[13], m[14]))

# ---- C: the avatar rig ------------------------------------------------------
print('\n== C. avatar toc %s ==' % AVATAR)
d = open(AVATAR, 'rb').read()
tio, = struct.unpack_from('<I', d, 0x34)
n, = struct.unpack_from('<I', d, tio)
mo = tio + 16 + n * 64
eo = mo + n * 64
ho = eo + n * 4
av = {}
for i in range(n):
    par, = struct.unpack_from('<H', d, eo + i * 4 + 2)
    raw = list(struct.unpack_from('<16f', d, mo + i * 64))
    m = [raw[0], raw[4], raw[8], raw[12], raw[1], raw[5], raw[9], raw[13],
         raw[2], raw[6], raw[10], raw[14], raw[3], raw[7], raw[11], raw[15]]
    av[i] = dict(parent=par, hash=struct.unpack_from('<I', d, ho + i * 4)[0], m=m)
print('  %d transforms' % n)
nonid = [i for i in av if any(abs(av[i]['m'][j * 4 + k] - (1.0 if j == k else 0.0)) > 1e-6
                              for j in range(4) for k in range(4))]
print('  non-identity: %s' % nonid)
byhash = {}
for i, a in av.items():
    byhash.setdefault(a['hash'], []).append(i)

print('\n  hash -> avatar index, for the 23 hashes of the torso unit:')
for tf in range(u.transform_info.count):
    h = u.transform_info.hashes[tf]
    print('    tf %2d hash %-11d %-18s avatar %s'
          % (tf, h, names.get(h, ''), byhash.get(h, 'ABSENT')))

print('\n== how many of our 107 rig bones does the patch skeleton name? ==')
patch_hashes = set()
for e in entries:
    if e['type_id'] != hd2_unit.UNIT_ID:
        continue
    toc = hd2_patch.entry_payload(e, PATCH)['toc']
    uu = hd2_unit.Unit.read(toc, b'')
    patch_hashes |= set(uu.transform_info.hashes)
av_hashes = {a['hash'] for a in av.values()}
print('  avatar %d hashes, patch units %d hashes, avatar-only %d, patch-only %d'
      % (len(av_hashes), len(patch_hashes), len(av_hashes - patch_hashes),
         len(patch_hashes - av_hashes)))
print('  patch-only: %s' % [('%d/%s' % (h, names.get(h, '')))
                            for h in sorted(patch_hashes - av_hashes)][:40])
