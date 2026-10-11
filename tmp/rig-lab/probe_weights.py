"""Per-bone vertex statistics for the correspondence table.

For every source bone in the correspondence table we report where the mesh it
actually owns sits (centroid + bbox of the vertices it dominates), and where that
mesh would land under the naive H*S^-1 map.  The gap between "source joint" and
"source mesh" is what tells us whether a bad segment ratio actually hurts.
"""
import sys, os, math
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E

VPK = sys.argv[1]
TOC = sys.argv[2]
BONEHASH = sys.argv[3]
MODEL = sys.argv[4] if len(sys.argv) > 4 else 'survivor_gambler'
IN2M = E.IN2M

_v, do, ents = E.parse_vpk_tree(VPK)
by_path = {f.lower(): (f, a, o, l) for f, a, o, l in ents}
cands = [f for f in by_path if f.lower().endswith('.mdl') and MODEL.lower() in os.path.basename(f).lower()]
cands.sort(key=lambda f: (len(os.path.basename(f)), os.path.basename(f)))
mdl_path = cands[0]
base = mdl_path[:-4]
f, arch, o, l = by_path[mdl_path.lower()]
mdl = E.read_range(VPK, do, arch, o, l)
f, arch, o, l = by_path[(base + '.vvd').lower()]
vvd_raw = E.read_range(VPK, do, arch, o, l)
vtx_path = next(p for p in by_path if p.lower() == (base + '.dx90.vtx').lower())
f, arch, o, l = by_path[vtx_path.lower()]
vtx_raw = E.read_range(VPK, do, arch, o, l)

mdl_parts = E.read_mdl_geometry(mdl)
vvd_verts, _lc, _n = E.read_vvd(vvd_raw)
vtx_parts, _ver, _e8 = E.read_vtx(vtx_raw)
diag = []
verts, tris = E.build_geometry(mdl_parts, vvd_verts, vtx_parts, diag)

src = E.read_source_bones(mdl)
hd2 = E.read_hd2_rig(TOC, BONEHASH)
by_name = {h['name']: h for h in hd2 if h['name']}
short = {b['name'][len(E.SRC_PREFIX):]: b for b in src if b['name'].startswith(E.SRC_PREFIX)}

# accumulate per dominant bone
cnt = {}
sumw = {}
cen = {}
bb = {}
for v in verts:
    if v['boneCount'] == 0:
        continue
    b = v['bones'][0]
    p = (v['pos'][0] * IN2M, v['pos'][1] * IN2M, v['pos'][2] * IN2M)
    cnt[b] = cnt.get(b, 0) + 1
    sumw[b] = sumw.get(b, 0.0) + v['w'][0]
    c = cen.setdefault(b, [0.0, 0.0, 0.0])
    c[0] += p[0]; c[1] += p[1]; c[2] += p[2]
    q = bb.setdefault(b, [1e9, 1e9, 1e9, -1e9, -1e9, -1e9])
    for k in range(3):
        q[k] = min(q[k], p[k]); q[3 + k] = max(q[3 + k], p[k])

print('=== %s : %d verts / %d tris ===' % (os.path.basename(mdl_path), len(verts), len(tris)))
print()
print('%-14s %-20s %7s %8s  %-30s %-30s' % ('source', 'hd2', 'verts', 'weight', 'source mesh centroid (m)', 'source joint (m)'))
cmap = dict(E.CORRESPONDENCE)
for sname, hname in E.CORRESPONDENCE:
    sb = short.get(sname)
    hb = by_name.get(hname)
    if sb is None:
        print('%-14s %-20s   MISSING SOURCE' % (sname, hname)); continue
    i = sb['index']
    n = cnt.get(i, 0)
    c = cen.get(i)
    cstr = ('(%7.3f,%7.3f,%7.3f)' % tuple(c[k] / n for k in range(3))) if (c and n) else '        --          '
    j = E.origin(sb['world_m'])
    print('%-14s %-20s %7d %8.1f  %-30s (%7.3f,%7.3f,%7.3f)'
          % (sname, hname, n, sumw.get(i, 0.0), cstr, j[0], j[1], j[2]))

print()
print('--- unweighted / unused source bones in the table ---')
for sname, hname in E.CORRESPONDENCE:
    sb = short.get(sname)
    if sb is not None and cnt.get(sb['index'], 0) == 0:
        print('   %-14s -> %-20s (0 dominant verts)' % (sname, hname))

print()
print('--- top source bones by dominant vertex count (whole rig) ---')
top = sorted(cnt.items(), key=lambda kv: -kv[1])[:25]
for i, n in top:
    nm = src[i]['name']
    if nm.startswith(E.SRC_PREFIX):
        nm = nm[len(E.SRC_PREFIX):]
    c = cen[i]
    print('   %-24s %7d  centroid (%7.3f,%7.3f,%7.3f)'
          % (nm, n, c[0] / n, c[1] / n, c[2] / n))
