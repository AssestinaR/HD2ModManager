# -*- coding: utf-8 -*-
"""Read the source model's material list and locate the L4D2 VMT/VTF."""
import collections, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E

VPK = 'D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/Lunalice_Zoey.vpk'
L4D2 = 'D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/'
BASE = 'models/survivors/survivor_teenangst'

ver, do, items = E.parse_vpk_tree(VPK)
by = {p.lower(): (p, a, o, l) for p, a, o, l in items}
mdl = E.read_range(VPK, do, *by[BASE + '.mdl'][1:])
names, cds, skins = E.read_mdl_materials(mdl)
print('cdtextures (%d): %s' % (len(cds), cds))
print('textures (%d):' % len(names))
for i, n in enumerate(names):
    print('   [%2d] %s' % (i, n))
print('skin families %d x %d' % (len(skins), len(skins[0]) if skins else 0))
print('skin[0]: %s' % skins[0] if skins else None)

mdl_parts = E.read_mdl_geometry(mdl)
used = collections.Counter()
for bp in mdl_parts:
    for m in bp['models']:
        for sh in m['meshes']:
            used[sh['material']] += sh['vertexCount']
print('\nmaterial index -> vertexCount: %s' % dict(used))
fam = skins[0] if skins else []
for mi in sorted(used):
    ti = fam[mi] if mi < len(fam) else None
    nm = names[ti] if ti is not None and ti < len(names) else '?'
    cd = cds[0] if cds else ''
    print('   mat %2d -> tex %s -> %s%s  (%d verts)' % (mi, ti, cd, nm, used[mi]))

# now look for the VMTs in the game
print('\n== L4D2 pak search ==')
paks = sorted(p for p in os.listdir(L4D2) if p.lower().endswith('_dir.vpk'))
p = os.path.join(L4D2, paks[0])
v2, b2, items2 = E.parse_vpk_tree(p)
print('%s: %d entries' % (paks[0], len(items2)))
want = set()
for ti in set(fam[:len(names)]) if fam else []:
    if ti is not None and ti < len(names):
        want.add((cds[0] + names[ti]).lower() if cds else names[ti].lower())
for w in sorted(want):
    hits = [x for x in items2 if x[0].lower() == w]
    print('   %-60s -> %s' % (w, hits[:1]))
byname = {x[0].lower(): x for x in items2}
for w in sorted(want):
    if w in byname:
        raw = E.read_range(p, b2, *byname[w][1:])
        print('   %s : %d bytes' % (w, len(raw) if raw else 0))
        if raw:
            print('      %s' % raw[:400].decode('ascii', 'replace').replace('\n', ' | '))
