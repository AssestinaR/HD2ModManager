"""WHICH body part do the dropped vertices belong to?

`probe_missing.py` showed the 0.06 m cut removes 510 vertices whose dominant
bones are `source_extra_62/63` (shoes) and `source_extra_72..77` + `Bip01_Head1`
(hair).  This script turns that into a body-part answer, by reconstructing the
vertex range of every body part from `build_geometry`'s diagnostics.
"""
import os
import re
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E
import hd2_style as H

VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
MODEL = 'survivor_teenangst'


def main():
    mdl_path, mdl, verts, tris, src, short = H.load_source(VPK, MODEL)
    P = np.array([[v['pos'][0] * E.IN2M, v['pos'][1] * E.IN2M,
                   v['pos'][2] * E.IN2M] for v in verts])

    # rebuild with diag so we know each body part's vertex range
    diag = []
    mdl_parts = E.read_mdl_geometry(mdl)
    _v, do, ents = E.parse_vpk_tree(VPK)
    by = {f.lower(): (f, arch, o, l) for f, arch, o, l in ents}
    base = os.path.splitext(mdl_path)[0]

    def blob(p):
        f, arch, o, l = by[p.lower()]
        return E.read_range(VPK, do, arch, o, l)

    E.build_geometry(mdl_parts, E.read_vvd(blob(base + '.vvd'))[0],
                     E.read_vtx(blob(base + '.dx90.vtx'))[0], diag)

    # cumulative vertex ranges, in the SAME order build_geometry appended them
    parts = []
    off = 0
    for d in diag:
        m = re.search(r'verts\s+(\d+)\s+tris\s+(\d+)', d)
        if not m:
            continue
        nv = int(m.group(1))
        head = d[:30].strip()
        model = d[31:65].strip()
        parts.append((head, model, off, off + nv))
        off += nv
    assert off == len(P), (off, len(P))

    # ---- which triangles survive the 0.06 cut -----------------------------
    L = np.empty(len(tris))
    for i, (a, b, c, _m) in enumerate(tris):
        L[i] = max(np.linalg.norm(P[a] - P[b]), np.linalg.norm(P[b] - P[c]),
                   np.linalg.norm(P[c] - P[a]))
    keep = L <= 0.06
    used = set()
    for i in np.where(keep)[0]:
        a, b, c, _m = tris[i]
        used.update((a, b, c))
    lost = np.array(sorted(set(range(len(P))) - used))
    print('%d verts lost at lim 0.06 m\n' % len(lost))

    # ---- attribute lost verts to body parts --------------------------------
    print('%-30s %-34s %8s %8s %8s' % ('bodypart', 'model', 'verts', 'lost', '%'))
    for head, model, a, b in parts:
        sel = lost[(lost >= a) & (lost < b)]
        n = b - a
        if n == 0:
            continue
        print('%-30s %-34s %8d %8d %7.2f%%'
              % (head, model, n, len(sel), 100.0 * len(sel) / n))

    # ---- detail: bbox + bones of the lost verts, per part -----------------
    print('\n-- lost-vertex detail (bbox + dominant bones) --')
    for head, model, a, b in parts:
        sel = lost[(lost >= a) & (lost < b)]
        if len(sel) == 0:
            continue
        Q = P[sel]
        cnt = {}
        for i in sel:
            v = verts[i]
            for k in range(3):
                if v['w'][k] > 0.3:
                    bb = v['bones'][k]
                    nm = src[bb]['name'] if 0 <= bb < len(src) else '?%d' % bb
                    cnt[nm] = cnt.get(nm, 0) + 1
        top = sorted(cnt.items(), key=lambda kv: -kv[1])[:5]
        print('   %-28s n=%3d  x[%+.3f,%+.3f] y[%+.3f,%+.3f] z[%+.3f,%+.3f]'
              % (head, len(sel), Q[:, 0].min(), Q[:, 0].max(),
                 Q[:, 1].min(), Q[:, 1].max(), Q[:, 2].min(), Q[:, 2].max()))
        print('        bones: %s'
              % ', '.join('%s(%d)' % (n, c) for n, c in top))

    # ---- sanity: are the shoe / hair parts actually present & sized? -------
    print('\n-- key parts (presence check) --')
    for head, model, a, b in parts:
        key = (head + ' ' + model).lower()
        if any(k in key for k in ('shoe', 'head', 'hair', 'dress', 'ribbon',
                                  'cat', 'tail', 'skirt', 'heel', 'boot')):
            z = P[a:b, 2]
            print('   %-30s %-34s verts %6d  z[%.3f,%.3f]'
                  % (head, model, b - a, z.min(), z.max()))


if __name__ == '__main__':
    main()
