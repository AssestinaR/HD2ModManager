# -*- coding: utf-8 -*-
"""What materials does OUR merged mesh actually need?

Re-runs export_obj's source readers (MDL / VVD / VTX) to recover, for every one
of the 121704 vertices and 159363 triangles, the source material index and the
resolved texture name -- neither of which dump_blender.py saved.  The vertex
order must line up with out/blender/model.npz; that is checked, not assumed.

usage: _probe_meshmat.py
"""
import collections
import json
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
os.chdir(HERE)
sys.path.insert(0, HERE)
import export_obj as E

VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
MODEL = 'survivor_teenangst'

_v, do, ents = E.parse_vpk_tree(VPK)
by_path = {f.lower(): (f, arch, o, l) for f, arch, o, l in ents}
cands = [f for f in by_path if f.lower().endswith('.mdl') and MODEL.lower() in os.path.basename(f).lower()]
cands.sort(key=lambda f: (len(os.path.basename(f)), os.path.basename(f)))
mdl_path = cands[0]
base = mdl_path[:-4]


def blob(path):
    f, arch, o, l = by_path[path.lower()]
    return E.read_range(VPK, do, arch, o, l)


mdl = blob(mdl_path)
vvd_raw = blob(base + '.vvd')
vtx_path = next(p for p in by_path if p.lower().endswith('.dx90.vtx')
                and os.path.basename(p).lower().startswith(os.path.basename(base).lower()))
vtx_raw = blob(vtx_path)

mdl_parts = E.read_mdl_geometry(mdl)
vvd_verts, lod_count, lod0_n = E.read_vvd(vvd_raw)
vtx_parts, vtx_ver, extra8 = E.read_vtx(vtx_raw)
diag = []
verts, tris = E.build_geometry(mdl_parts, vvd_verts, vtx_parts, diag)

names, cds, skins = E.read_mdl_materials(mdl)
print('cdtextures: %s' % cds)
print('textures   : %d   skin families: %d  skinref: %d'
      % (len(names), len(skins), len(skins[0]) if skins else 0))
print('mesh: %d verts / %d tris' % (len(verts), len(tris)))

d = np.load('out/blender/model.npz')
print('model.npz : %d verts / %d tris' % (len(d['ours_bind']), len(d['tris'])))
assert len(verts) == len(d['ours_bind']), 'vertex count mismatch'
assert len(tris) == len(d['tris']), 'triangle count mismatch'

# vertex order check: model.npz src_tpose is the source geometry after the
# initial rigid fit, so compare the TRIANGLE topology instead of positions.
t_src = np.asarray([(t[0], t[1], t[2]) for t in tris], np.int64)
t_npz = np.asarray(d['tris'], np.int64)
print('triangle topology identical: %s' % bool((t_src == t_npz).all()))

mat_of_tri = np.asarray([t[3] for t in tris], np.int64)
print('\ndistinct material indices on triangles: %s'
      % sorted(set(mat_of_tri.tolist())))

family0 = skins[0]
print('\n== material index -> texture (skin family 0) ==')
hist = collections.Counter(mat_of_tri.tolist())
for mi, cnt in sorted(hist.items()):
    tex = family0[mi] if mi < len(family0) else None
    print('  mat %-3d  tris %-7d  texture %s' % (mi, cnt, tex))

# per-vertex material (each vertex belongs to exactly one section)
vmat = {}
for t in tris:
    for vi in t[:3]:
        vmat.setdefault(vi, set()).add(t[3])
multi = sum(1 for v in vmat.values() if len(v) > 1)
print('\nvertices in more than one material group: %d' % multi)

# UV range sanity
uvs = np.asarray([v['uv'] for v in verts], np.float64)
print('uv0 range  u %.4f..%.4f   v %.4f..%.4f'
      % (uvs[:, 0].min(), uvs[:, 0].max(), uvs[:, 1].min(), uvs[:, 1].max()))

# how many distinct source texture NAMES would a name-based grouping give
byname = collections.defaultdict(int)
for t in tris:
    nm = family0[t[3]] if t[3] < len(family0) else '?'
    byname[nm] += 1
print('\ndistinct texture names used by triangles: %d' % len(byname))
for nm, cnt in sorted(byname.items(), key=lambda kv: -kv[1]):
    print('  %-40s %d tris' % (nm, cnt))
