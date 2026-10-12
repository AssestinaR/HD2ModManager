"""Per-body-part radius from the mesh centre -- the data behind the cut.

If the far geometry is a whole BODY PART (Nick's `LongSleeve`, Ellis's `Tail`),
then the honest way to separate "far" from "near" is to look at each part's
radius and find the gap.
"""
import os
import re
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E
import hd2_style as H

ADDONS = 'D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons'
MODELS = [
    ('Lunalice_Zoey.vpk', 'survivor_teenangst', 'zoey'),
    ('Elise_Coach.vpk', 'survivor_coach', 'coach'),
    ('Lilium_Sancta_Nick.vpk', 'survivor_gambler', 'nick'),
    ('Serenade_Ellis.vpk', 'survivor_mechanic', 'ellis'),
]


def main():
    for vpk, model, tag in MODELS:
        path = os.path.join(ADDONS, vpk)
        mdl_path, mdl, verts, tris, _src, _sh = H.load_source(path, model)
        P = np.array([[v['pos'][0] * E.IN2M, v['pos'][1] * E.IN2M,
                       v['pos'][2] * E.IN2M] for v in verts])
        c = np.median(P, axis=0)
        d = np.linalg.norm(P - c, axis=1)
        print('\n=== %s ===  centre (%.2f,%.2f,%.2f)' % (tag, *c))
        print('    distance  med %.2f  p75 %.2f  p90 %.2f  p95 %.2f  p99 %.2f  max %.2f'
              % (np.median(d), np.percentile(d, 75), np.percentile(d, 90),
                 np.percentile(d, 95), np.percentile(d, 99), d.max()))

        # per body part radius
        _v, do, ents = E.parse_vpk_tree(path)
        by = {f.lower(): (f, arch, o, l) for f, arch, o, l in ents}
        base = os.path.splitext(mdl_path)[0]

        def blob(p):
            f, arch, o, l = by[p.lower()]
            return E.read_range(path, do, arch, o, l)

        diag = []
        E.build_geometry(E.read_mdl_geometry(mdl), E.read_vvd(blob(base + '.vvd'))[0],
                         E.read_vtx(blob(base + '.dx90.vtx'))[0], diag)
        rows, off = [], 0
        for dd in diag:
            m = re.search(r'verts\s+(\d+)\s+tris\s+(\d+)', dd)
            if not m:
                continue
            nv = int(m.group(1))
            if nv:
                r = d[off:off + nv]
                rows.append((dd[:30].strip(), nv, float(r.max()), float(np.median(r))))
            off += nv
        rows.sort(key=lambda r: -r[2])
        for head, nv, rmax, rmed in rows[:8]:
            print('    %-28s n=%6d  radius max %.2f  med %.2f' % (head, nv, rmax, rmed))


if __name__ == '__main__':
    main()
