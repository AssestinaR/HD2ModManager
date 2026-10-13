# -*- coding: utf-8 -*-
"""Resolve the source model's 92 materials down to actual VTF files.

Source model -> MDL texture entry -> `mstudiotexture_t.material` (a VMT path)
-> VMT `$basetexture` / `$bumpmap` -> VTF.  Prints, for all 92, what each VMT
asks for and whether the VTF is present in any of the Lunalice addon packs.

usage: _probe_vmtresolve.py
"""
import collections
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E

ADD = 'D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
PACKS = ['Lunalice_Zoey.vpk',
         'Lunalice基础材质A1主体.vpk',
         'Lunalice基础材质B1服装色白.vpk',
         'Lunalice基础材质C2金属装饰品色银.vpk',
         'Lunalice基础材质D1发色白.vpk',
         'Lunalice基础材质E3瞳色紫2.vpk',
         'Lunalice基础材质F1身体描边.vpk']

# ------------------------------------------------------------------ collect
vmt_blob = {}
vtf_paths = set()
for v in PACKS:
    p = ADD + v
    if not os.path.exists(p):
        print('MISSING %s' % v)
        continue
    _vv, d, es = E.parse_vpk_tree(p)
    for f, a, o, l in es:
        fl = f.lower()
        if fl.endswith('.vmt'):
            vmt_blob[fl.replace('/', '\\')] = (p, d, a, o, l)
            vmt_blob[os.path.basename(fl)] = (p, d, a, o, l)
        elif fl.endswith('.vtf'):
            vtf_paths.add(fl.replace('/', '\\'))
print('vmt %d   vtf %d' % (len(vmt_blob), len(vtf_paths)))

# ------------------------------------------------------------------ the model
_v, do, ents = E.parse_vpk_tree(ADD + 'Lunalice_Zoey.vpk')
by = {f.lower(): (f, a, o, l) for f, a, o, l in ents}
cands = [f for f in by if f.lower().endswith('.mdl') and 'teenangst' in os.path.basename(f).lower()]
cands.sort(key=lambda f: (len(os.path.basename(f)), os.path.basename(f)))
mdl_path = cands[0]
f, a, o, l = by[mdl_path.lower()]
mdl = E.read_range(ADD + 'Lunalice_Zoey.vpk', do, a, o, l)

nt, = struct.unpack_from('<i', mdl, 204)
to, = struct.unpack_from('<i', mdl, 208)
nc, = struct.unpack_from('<i', mdl, 212)
co, = struct.unpack_from('<i', mdl, 216)
cds = [E.cstr(mdl, struct.unpack_from('<i', mdl, co + 4 * i)[0]) for i in range(nc)]
names, vmtpaths = [], []
for i in range(nt):
    e = to + i * 64
    ni, = struct.unpack_from('<i', mdl, e)
    mi, = struct.unpack_from('<i', mdl, e + 16)
    names.append(E.cstr(mdl, e + ni))
    vmtpaths.append(E.cstr(mdl, e + mi) if mi else '')

print('\n== 92 entries: texture name / VMT path ==')
for i in range(nt):
    print('  %2d  %-42s %s' % (i, names[i], vmtpaths[i]))


def read_vmt(path):
    key = path.lower().replace('/', '\\')
    if key in vmt_blob:
        p, d, a, o, l = vmt_blob[key]
        return E.read_range(p, d, a, o, l).decode('latin-1', 'replace')
    return None


print('\n== resolution ==')
counts = collections.Counter()
missing_vmt, missing_vtf = [], []
base_textures = set()
for i in range(nt):
    txt = read_vmt(vmtpaths[i])
    if txt is None:
        missing_vmt.append(names[i])
        counts['no vmt'] += 1
        continue
    base = None
    for line in txt.splitlines():
        line = line.strip()
        if line.lower().startswith('$basetexture'):
            base = line.split(None, 1)[1].strip().strip('"').replace('/', '\\').lower()
            break
    if not base:
        counts['vmt without $basetexture'] += 1
        continue
    base_textures.add(base)
    if base + '.vtf' in vtf_paths:
        counts['ok'] += 1
    else:
        counts['vtf absent'] += 1
        missing_vtf.append((names[i], base))

print('  %s' % dict(counts))
print('  distinct $basetexture values: %d' % len(base_textures))
print('  no vmt      : %s' % missing_vmt)
print('  vtf absent  :')
for n, b in missing_vtf:
    print('      %-42s -> %s' % (n, b))

print('\n== all $basetexture values ==')
for b in sorted(base_textures):
    print('  %-56s %s' % (b, 'PRESENT' if b + '.vtf' in vtf_paths else 'absent'))
