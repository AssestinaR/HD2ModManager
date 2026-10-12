"""WHY does the hair follow the arm?

Round 2.5: in the A-pose bake the back hair and the head ribbon get dragged down
and inward with the arms.  The suspicion is the FALLBACK: source bones that have
no HD2 counterpart (`source_extra_*`, i.e. the jiggle/hair/ribbon bones) are bound
to whichever mapped bone's JOINT sits nearest -- and a hair bone that hangs beside
the shoulder is nearer to the shoulder than to the head.

This prints, for every orphan bone, what `distance` picks, what the source
HIERARCHY would pick, and how many vertices each one actually drives.  Then it
turns that into a per-body-part answer: how much of each part ends up on arm bones.

usage: probe_orphan.py
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


def main():
    r = H.retarget(VPK, MODEL, TOC, BH, PP,
                   align='yaw', spine_map='mesh', fallback='distance',
                   leg_tilt=5.7, leg_splay=4.0, skeleton='native',
                   mesh_shift=(0.0, -0.100, -0.060),
                   upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                   verbose=False)
    src = r['src']
    verts = r['verts']
    hd2name = {h['index']: (h['name'] or 'bone_%d' % h['index']) for h in r['hd2']}
    direct = r['src_index_to_hd2']
    resolved = r['resolved']

    # how many vertices each source bone dominates
    cnt = {}
    cent = {}
    for k, v in enumerate(verts):
        for j in range(3):
            if v['w'][j] > 0.5:
                b = v['bones'][j]
                cnt[b] = cnt.get(b, 0) + 1
                cent.setdefault(b, []).append(k)

    def ancestor(i):
        cur, n = src[i]['parent'], 0
        while cur >= 0 and cur not in direct and n < 64:
            cur = src[cur]['parent']
            n += 1
        return cur

    orph = [b for b in src if b['index'] not in direct]
    print('source bones %d | name-mapped %d | orphan %d'
          % (len(src), len(direct), len(orph)))

    print('\n=== orphan bones with >=1 dominated vertex ===')
    print('%-22s %-22s %-16s %-16s %6s  %s'
          % ('bone', 'parent', 'dist picks', 'parent picks', 'verts', 'centroid'))
    rows = []
    for b in orph:
        i = b['index']
        if cnt.get(i, 0) == 0:
            continue
        dn = hd2name.get(resolved.get(i))
        a = ancestor(i)
        pn = hd2name.get(direct.get(a)) if a is not None and a in direct else None
        idx = cent[i]
        c = np.asarray(r['out'])[idx].mean(axis=0)
        rows.append((cnt[i], b['name'], src[b['parent']]['name'] if b['parent'] >= 0 else '-',
                     dn, pn, c))
    for n, nm, par, dn, pn, c in sorted(rows, key=lambda t: -t[0]):
        print('%-22s %-22s %-16s %-16s %6d  %+.3f %+.3f %+.3f'
              % (nm, par, dn, pn, n, c[0], c[1], c[2]))

    # ---- per body part: where do its vertices land? ------------------------
    mdl = r['mdl']
    mdl_path = r['mdl_path']
    diag = []
    _v, do, ents = E.parse_vpk_tree(VPK)
    by = {f.lower(): (f, arch, o, l) for f, arch, o, l in ents}
    base = os.path.splitext(mdl_path)[0]

    def blob(p):
        f, arch, o, l = by[p.lower()]
        return E.read_range(VPK, do, arch, o, l)

    E.build_geometry(E.read_mdl_geometry(mdl),
                     E.read_vvd(blob(base + '.vvd'))[0],
                     E.read_vtx(blob(base + '.dx90.vtx'))[0], diag)
    parts, off = [], 0
    for d in diag:
        m = re.search(r'verts\s+(\d+)\s+tris\s+(\d+)', d)
        if not m:
            continue
        nv = int(m.group(1))
        parts.append((d[:30].strip(), d[31:65].strip(), off, off + nv))
        off += nv

    KEY = ('head', 'hair', 'ribbon', 'bow', 'shoe', 'boot', 'heel', 'sleeve',
           'dress', 'skirt', 'cuff', 'tights', 'sock', 'tail', 'base')
    print('\n=== per body part: share of vertices on ARM bones ===')
    print('%-26s %-30s %7s %7s %7s   %s'
          % ('bodypart', 'model', 'verts', 'arm', 'arm%', 'top bones'))
    for head, model, a, b in parts:
        key = (head + ' ' + model).lower()
        if not any(k in key for k in KEY):
            continue
        tally = {}
        arm = 0
        for i in range(a, b):
            v = verts[i]
            j = int(np.argmax(v['w']))
            h = resolved.get(v['bones'][j])
            nm = hd2name.get(h, '?')
            tally[nm] = tally.get(nm, 0) + 1
            if ARM.match(nm or ''):
                arm += 1
        n = b - a
        if n == 0:
            continue
        top = sorted(tally.items(), key=lambda kv: -kv[1])[:4]
        print('%-26s %-30s %7d %7d %6.1f%%   %s'
              % (head[:26], model[:30], n, arm, 100.0 * arm / n,
                 ', '.join('%s:%d' % t for t in top)))


if __name__ == '__main__':
    main()
