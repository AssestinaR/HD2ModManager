# -*- coding: utf-8 -*-
"""Recon for the one-unit test package: UVs, material names, L4D2 textures.

Three questions, all answered from files already on this machine:

  1. Are the UVs reachable?  The VVD vertex record is 48 bytes:
        +0  float32[3]  bone weights
        +12 uint8[3]    bone indices  + uint8 boneCount
        +16 float32[3]  position
        +28 float32[3]  normal
        +40 float32[2]  TEXCOORD          <-- export_obj.read_vvd stops at +40
     so the UV is already in the buffer we read, just not extracted.

  2. What materials does the source MDL name?  The MDL keeps a cdtextures list
     plus a texture-name list; a mesh's `material` index points into the latter.

  3. Can we get the L4D2 textures?  The addon VPK carries only .mdl/.vvd/.vtx,
     so the VMTs/VTFs must come from the game's own pak files.

usage: _probe_srcmaterials.py
"""
import collections
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E

VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
L4D2 = 'D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/'
MDL = 'models/survivors/survivor_teenangst.mdl'
VVD = 'models/survivors/survivor_teenangst.vvd'
VTX = 'models/survivors/survivor_teenangst.dx90.vtx'

print('== 1. does the VVD really carry UVs? ======================')
ver, base, items = E.parse_vpk_tree(VPK)
by_name = {p: (a, o, l) for p, a, o, l in items}
with open(VPK, 'rb') as f:
    f.seek(by_name[VVD][1] + base if by_name[VVD][0] == 32767 else 0)
    raw = f.read(by_name[VVD][2])
vvd, lod_count, nvert = E.read_vvd(raw)
uv = [struct.unpack_from('<2f', raw, struct.unpack_from('<i', raw, 56)[0]
                         + i * 48 + 40) for i in range(0, nvert, max(1, nvert // 8))]
print('   LOD0 vertices: %d   lod_count %d' % (nvert, lod_count))
print('   first 8 texcoords: %s' % [(round(a, 4), round(b, 4)) for a, b in uv])
u = [t[0] for t in uv]
print('   u range in that sample: %.3f .. %.3f' % (min(u), max(u)))

print('\n== 2. material names in the MDL ===========================')
with open(VPK, 'rb') as f:
    f.seek(by_name[MDL][1] + base)
    mdl = f.read(by_name[MDL][2])
print('   magic %s  version %d' % (mdl[0:4], struct.unpack_from('<i', mdl, 4)[0]))


def cstr(buf, at, limit=512):
    e = buf.find(b'\0', at, at + limit)
    return buf[at:e].decode('ascii', 'replace')


# hunt for the string tables: any run of printable null-terminated paths
strings = []
i = 0
while i < len(mdl):
    if 32 <= mdl[i] < 127:
        j = i
        while j < len(mdl) and (32 <= mdl[j] < 127):
            j += 1
        if j - i >= 6 and mdl[j:j + 1] == b'\0':
            strings.append((i, mdl[i:j].decode('ascii', 'replace')))
        i = j + 1
    else:
        i += 1
mats = [s for _o, s in strings
        if s.lower().startswith('models/') and s.lower().endswith(('.vmt', '.vmt"' ))
        or s.lower().startswith('models/survivors')]
print('   model/material-looking strings: %d' % len(mats))
for s in mats[:25]:
    print('     %s' % s)
other = collections.Counter(s.split('/')[0] for _o, s in strings)
print('   string-table prefixes: %s' % other.most_common(10))

print('\n== 3. where do the L4D2 VMT/VTF live? =====================')
paks = sorted(p for p in os.listdir(L4D2) if p.lower().endswith('.vpk'))
print('   %s: %d vpk files' % (L4D2, len(paks)))
print('   %s' % paks[:12])
dirs = [p for p in paks if p.lower().endswith('_dir.vpk')]
print('   dir vpks: %s' % dirs)
if dirs:
    p = os.path.join(L4D2, dirs[0])
    try:
        ver2, base2, items2 = E.parse_vpk_tree(p)
        print('   %s: ver %d, %d entries' % (dirs[0], ver2, len(items2)))
        ext = collections.Counter(os.path.splitext(x[0])[1].lower() for x in items2)
        print('   extensions: %s' % ext.most_common(12))
        want = [x for x in items2 if 'survivor_teenangst' in x[0].lower()]
        print('   survivor_teenangst entries: %d' % len(want))
        for x in want[:25]:
            print('     %-72s a=%d o=%d l=%d' % x)
    except Exception as exc:
        print('   could not parse %s: %s' % (p, exc))
