# -*- coding: utf-8 -*-
"""Which of the 92 source materials can we actually resolve to a VTF?

The MDL lists 92 textures and the skin family is the identity, so every
material index really does have its own texture.  This probe prints those 92
names next to the VMT/VTF actually shipped by the Lunalice addon packs, so we
know how many HD2 materials the packager has to emit and how many will have to
fall back to a flat colour.

usage: _probe_texresolve.py
"""
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E

ADDONS = 'D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
VPKS = ['Lunalice_Zoey.vpk',
        'Lunalice基础材质A1主体.vpk',
        'Lunalice基础材质B1服装色白.vpk',
        'Lunalice基础材质C2金属装饰品色银.vpk',
        'Lunalice基础材质D1发色白.vpk',
        'Lunalice基础材质E3瞳色紫2.vpk',
        'Lunalice基础材质F1身体描边.vpk',
        '[l4n_survivor] Lunalice_.vpk']

# ---------------------------------------------------------------- the 92 names
_v, do, ents = E.parse_vpk_tree(ADDONS + 'Lunalice_Zoey.vpk')
by = {f.lower(): (f, a, o, l) for f, a, o, l in ents}
cands = [f for f in by if f.lower().endswith('.mdl') and 'teenangst' in os.path.basename(f).lower()]
cands.sort(key=lambda f: (len(os.path.basename(f)), os.path.basename(f)))
mdl_path = cands[0]


def blob(path):
    f, a, o, l = by[path.lower()]
    return E.read_range(ADDONS + 'Lunalice_Zoey.vpk', do, a, o, l)


mdl = blob(mdl_path)
nt, = struct.unpack_from('<i', mdl, 204)
to, = struct.unpack_from('<i', mdl, 208)
cds = [E.cstr(mdl, struct.unpack_from('<i', mdl, 806152 + 4 * i)[0]) for i in range(2)]
names = []
for i in range(nt):
    e = to + i * 64
    ni, = struct.unpack_from('<i', mdl, e)
    names.append(E.cstr(mdl, e + ni))

print('cdtextures %s' % cds)
print('\n== the 92 source textures ==')
for i, n in enumerate(names):
    print('  %2d  %s' % (i, n))

# ------------------------------------------------------- what the packs ship
print('\n== what the addon packs ship ==')
allvmt, allvtf = set(), set()
for v in VPKS:
    p = ADDONS + v
    if not os.path.exists(p):
        print('  MISSING %s' % v)
        continue
    try:
        _vv, d, es = E.parse_vpk_tree(p)
    except Exception as ex:
        print('  ERR %s %s' % (v, ex))
        continue
    vm = sorted(f for f, a, o, l in es if f.lower().endswith('.vmt'))
    vt = sorted(f for f, a, o, l in es if f.lower().endswith('.vtf'))
    allvmt |= {os.path.basename(x).lower()[:-4] for x in vm}
    allvtf |= {os.path.basename(x).lower()[:-4] for x in vt}
    print('  %-34s vmt %-3d vtf %-3d' % (v, len(vm), len(vt)))
    for x in vm[:6]:
        print('        %s' % x)
    if len(vm) > 6:
        print('        ... %d more' % (len(vm) - 6))

print('\n== can each of the 92 be found? ==')
hit = 0
for i, n in enumerate(names):
    k = os.path.basename(n).lower()
    m = 'vmt' if k in allvmt else ''
    t = 'vtf' if k in allvtf else ''
    if m or t:
        hit += 1
    print('  %2d  %-42s %s %s' % (i, n, m, t))
print('\nresolved at least one way: %d / %d' % (hit, len(names)))
