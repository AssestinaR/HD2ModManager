"""Verify the new spatial `drop_outlier_tris` on all five models.

Expected, with margin_frac = 0.45:
  zoey / rochelle / coach : 0 triangles hidden  -> ponytail + shoe heel kept
  nick                    : only the far `LongSleeve` blob hidden
  ellis                   : the `Tail` KEPT (it is legit geometry)
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
    ('Lunalice_Rochelle.vpk', 'survivor_producer', 'rochelle'),
    ('Elise_Coach.vpk', 'survivor_coach', 'coach'),
    ('Lilium_Sancta_Nick.vpk', 'survivor_gambler', 'nick'),
    ('Serenade_Ellis.vpk', 'survivor_mechanic', 'ellis'),
]


def part_ranges(diag):
    parts, off = [], 0
    for d in diag:
        m = re.search(r'verts\s+(\d+)\s+tris\s+(\d+)', d)
        if not m:
            continue
        nv = int(m.group(1))
        parts.append((d[:30].strip(), off, off + nv))
        off += nv
    return parts


def main():
    for vpk, model, tag in MODELS:
        path = os.path.join(ADDONS, vpk)
        mdl_path, mdl, verts, tris, src, _sh = H.load_source(path, model)
        P = np.array([[v['pos'][0] * E.IN2M, v['pos'][1] * E.IN2M,
                       v['pos'][2] * E.IN2M] for v in verts])
        kept, (ndrop, box) = H.drop_outlier_tris(P, tris)
        used = set()
        for a, b, c, _m in kept:
            used.update((a, b, c))
        lost = np.array(sorted(set(range(len(P))) - used))
        print('\n=== %-9s %6d tris -> %6d kept (%d hidden) | %d verts lost'
              % (tag, len(tris), len(kept), ndrop, len(lost)))
        print('    raw bbox  x[%+.2f,%+.2f] y[%+.2f,%+.2f] z[%+.2f,%+.2f]'
              % (P[:, 0].min(), P[:, 0].max(), P[:, 1].min(), P[:, 1].max(),
                 P[:, 2].min(), P[:, 2].max()))
        print('    radius cut %.3f m' % box)
        if len(lost):
            Q = P[lost]
            print('    lost bbox x[%+.2f,%+.2f] y[%+.2f,%+.2f] z[%+.2f,%+.2f]'
                  % (Q[:, 0].min(), Q[:, 0].max(), Q[:, 1].min(), Q[:, 1].max(),
                     Q[:, 2].min(), Q[:, 2].max()))
            cnt = {}
            for i in lost:
                v = verts[i]
                for k in range(3):
                    if v['w'][k] > 0.3:
                        b = v['bones'][k]
                        nm = src[b]['name'] if 0 <= b < len(src) else '?%d' % b
                        cnt[nm] = cnt.get(nm, 0) + 1
            for nm, n in sorted(cnt.items(), key=lambda kv: -kv[1])[:6]:
                print('       bone %-34s %d' % (nm, n))

        # the thing the user actually complained about: ponytail + shoe heel
        for head, a, b in part_ranges_for(mdl_path, mdl, path):
            sel = lost[(lost >= a) & (lost < b)]
            if len(sel):
                print('       part %-26s loses %d verts' % (head, len(sel)))


def part_ranges_for(mdl_path, mdl, path):
    _v, do, ents = E.parse_vpk_tree(path)
    by = {f.lower(): (f, arch, o, l) for f, arch, o, l in ents}
    base = os.path.splitext(mdl_path)[0]

    def blob(p):
        f, arch, o, l = by[p.lower()]
        return E.read_range(path, do, arch, o, l)

    diag = []
    E.build_geometry(E.read_mdl_geometry(mdl), E.read_vvd(blob(base + '.vvd'))[0],
                     E.read_vtx(blob(base + '.dx90.vtx'))[0], diag)
    return part_ranges(diag)


if __name__ == '__main__':
    main()
