"""Which body part is the far blob in, for Nick and Ellis?"""
import os
import re
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E
import hd2_style as H

ADDONS = 'D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons'


def part_ranges(mdl, diag):
    parts, off = [], 0
    for d in diag:
        m = re.search(r'verts\s+(\d+)\s+tris\s+(\d+)', d)
        if not m:
            continue
        nv = int(m.group(1))
        parts.append((d[:30].strip(), d[31:65].strip(), off, off + nv))
        off += nv
    return parts


def main():
    for vpk, model, tag in [('Lilium_Sancta_Nick.vpk', 'survivor_gambler', 'nick'),
                            ('Serenade_Ellis.vpk', 'survivor_mechanic', 'ellis')]:
        path = os.path.join(ADDONS, vpk)
        mdl_path, mdl, verts, tris, src, _sh = H.load_source(path, model)
        P = np.array([[v['pos'][0] * E.IN2M, v['pos'][1] * E.IN2M,
                       v['pos'][2] * E.IN2M] for v in verts])
        print('\n=== %s ===  %d verts' % (tag, len(P)))

        lo = np.percentile(P, 1, axis=0)
        hi = np.percentile(P, 99, axis=0)
        out = np.where(np.any((P < lo - 0.30) | (P > hi + 0.30), axis=1))[0]

        # per body part
        _v, do, ents = E.parse_vpk_tree(path)
        by = {f.lower(): (f, arch, o, l) for f, arch, o, l in ents}
        base = os.path.splitext(mdl_path)[0]

        def blob(p):
            f, arch, o, l = by[p.lower()]
            return E.read_range(path, do, arch, o, l)

        diag = []
        E.build_geometry(E.read_mdl_geometry(mdl), E.read_vvd(blob(base + '.vvd'))[0],
                         E.read_vtx(blob(base + '.dx90.vtx'))[0], diag)
        parts = part_ranges(mdl, diag)
        for head, model2, a, b in parts:
            sel = out[(out >= a) & (out < b)]
            if len(sel):
                Q = P[sel]
                print('   %-30s n=%4d  x[%+.2f,%+.2f] y[%+.2f,%+.2f] z[%+.2f,%+.2f]'
                      % (head, len(sel), Q[:, 0].min(), Q[:, 0].max(),
                         Q[:, 1].min(), Q[:, 1].max(), Q[:, 2].min(), Q[:, 2].max()))


if __name__ == '__main__':
    main()
