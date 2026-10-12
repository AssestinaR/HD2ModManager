"""Did `fallback='region'` fix the hair without breaking anything else?

Runs the pipeline twice -- once with the old `distance` fallback, once with the new
`region` one -- and reports:
  * which orphan bones changed hands,
  * per body part, the share of vertices landing on arm bones (before/after),
  * per body part, how far the mesh actually moves between the two runs.

The A-pose bake is where the damage shows, so the displacement is measured on
`out` (the bind mesh), not on `out_idle` -- under `skeleton='native'` the idle
frame is the identity by construction and would hide everything.

usage: probe_region.py
"""
import os
import re
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E
import hd2_style as H

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
MODEL = 'survivor_teenangst'

ARM = re.compile(r'^[lr]_(clavicle|shoulder|elbow|hand|wrist)')

KW = dict(align='yaw', spine_map='mesh', leg_tilt=5.7, leg_splay=4.0,
          skeleton='native', mesh_shift=(0.0, -0.100, -0.060),
          upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45, verbose=False)


def run(fb):
    return H.retarget(VPK, MODEL, TOC, BH, PP, fallback=fb, **KW)


def parts_of(r):
    mdl_path = r['mdl_path']
    diag = []
    _v, do, ents = E.parse_vpk_tree(VPK)
    by = {f.lower(): (f, arch, o, l) for f, arch, o, l in ents}
    base = os.path.splitext(mdl_path)[0]

    def blob(p):
        f, arch, o, l = by[p.lower()]
        return E.read_range(VPK, do, arch, o, l)

    E.build_geometry(E.read_mdl_geometry(r['mdl']),
                     E.read_vvd(blob(base + '.vvd'))[0],
                     E.read_vtx(blob(base + '.dx90.vtx'))[0], diag)
    out, off = [], 0
    for d in diag:
        m = re.search(r'verts\s+(\d+)\s+tris\s+(\d+)', d)
        if not m:
            continue
        nv = int(m.group(1))
        out.append((d[:30].strip(), d[31:65].strip(), off, off + nv))
        off += nv
    return out


def main():
    a = run('distance')
    b = run('region')

    na = {h['index']: (h['name'] or 'b%d' % h['index']) for h in a['hd2']}
    nb = {h['index']: (h['name'] or 'b%d' % h['index']) for h in b['hd2']}

    src = a['src']
    verts = a['verts']
    cnt = {}
    for v in verts:
        for j in range(3):
            if v['w'][j] > 0.5:
                cnt[v['bones'][j]] = cnt.get(v['bones'][j], 0) + 1

    print('=== orphan bones that changed hands ===')
    changed = 0
    for s in src:
        i = s['index']
        x, y = a['resolved'].get(i), b['resolved'].get(i)
        if x == y:
            continue
        changed += 1
        print('  %-22s %-14s -> %-14s  %5d verts'
              % (s['name'], na.get(x, '?'), nb.get(y, '?'), cnt.get(i, 0)))
    print('  (%d of %d source bones changed)' % (changed, len(src)))

    parts = parts_of(a)
    print('\n=== per body part: arm share and how far the mesh moved ===')
    print('%-24s %-26s %7s %8s %8s %9s %9s'
          % ('bodypart', 'model', 'verts', 'arm%old', 'arm%new', 'd.mean', 'd.max'))
    KEY = ('head', 'hair', 'ribbon', 'bow', 'shoe', 'boot', 'heel', 'sleeve',
           'dress', 'skirt', 'cuff', 'tights', 'sock', 'base', 'corset')
    A, B = np.asarray(a['out']), np.asarray(b['out'])
    for head, model, p, q in parts:
        key = (head + ' ' + model).lower()
        if not any(k in key for k in KEY):
            continue
        n = q - p
        if n == 0:
            continue
        oa = ob = 0
        for i in range(p, q):
            v = verts[i]
            j = int(np.argmax(v['w']))
            if ARM.match(na.get(a['resolved'].get(v['bones'][j]), '') or ''):
                oa += 1
            if ARM.match(nb.get(b['resolved'].get(v['bones'][j]), '') or ''):
                ob += 1
        d = np.linalg.norm(B[p:q] - A[p:q], axis=1)
        print('%-24s %-26s %7d %7.1f%% %7.1f%% %9.4f %9.4f'
              % (head[:24], model[:26], n, 100.0 * oa / n, 100.0 * ob / n,
                 d.mean(), d.max()))

    print('\nwhole mesh: mean %.4f  max %.4f  m' % (
        float(np.linalg.norm(B - A, axis=1).mean()),
        float(np.linalg.norm(B - A, axis=1).max())))

    # ---- what is STILL on arm bones, and is it legitimately there? ---------
    print('\n=== source bones that map to ARM bones (after the change) ===')
    tally = {}
    for v in verts:
        j = int(np.argmax(v['w']))
        h = b['resolved'].get(v['bones'][j])
        if ARM.match(nb.get(h, '') or ''):
            tally[v['bones'][j]] = tally.get(v['bones'][j], 0) + 1
    for bi, n in sorted(tally.items(), key=lambda kv: -kv[1]):
        name = src[bi]['name'] if 0 <= bi < len(src) else '?'
        kind = 'name-mapped' if bi in a['src_index_to_hd2'] else 'FALLBACK'
        print('  %-24s %-12s %6d verts -> %s'
              % (name, kind, n, nb.get(b['resolved'].get(bi))))



if __name__ == '__main__':
    main()
