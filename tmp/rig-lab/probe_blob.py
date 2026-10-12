"""The Nick/Ellis stray blob: is it in the SOURCE mesh, and what is it?

`drop_outlier_tris` filters by EDGE LENGTH, on `src_pts`.  If the stray geometry
is a compact blob sitting far from the body (not a long triangle fan), an
edge-length filter can never remove it -- while it happily deletes the ponytail
and shoe sole, whose flat cards have legitimately long edges.  Check the shape of
the blob in the source mesh.
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E
import hd2_style as H

ADDONS = 'D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons'


def main():
    for vpk, model, tag in [('Lilium_Sancta_Nick.vpk', 'survivor_gambler', 'nick'),
                            ('Serenade_Ellis.vpk', 'survivor_mechanic', 'ellis')]:
        path = os.path.join(ADDONS, vpk)
        _mp, _mdl, verts, tris, src, _sh = H.load_source(path, model)
        P = np.array([[v['pos'][0] * E.IN2M, v['pos'][1] * E.IN2M,
                       v['pos'][2] * E.IN2M] for v in verts])
        print('\n=== %s ===  %d verts / %d tris' % (tag, len(P), len(tris)))
        print('   source bbox x[%.3f,%.3f] y[%.3f,%.3f] z[%.3f,%.3f]'
              % (P[:, 0].min(), P[:, 0].max(), P[:, 1].min(), P[:, 1].max(),
                 P[:, 2].min(), P[:, 2].max()))

        # the body core: robust bbox from the middle 98 %
        lo = np.percentile(P, 1, axis=0)
        hi = np.percentile(P, 99, axis=0)
        out = np.where(np.any((P < lo - 0.30) | (P > hi + 0.30), axis=1))[0]
        print('   %d verts outside the 1-99%% box +- 0.30 m' % len(out))
        if len(out):
            Q = P[out]
            print('   those verts: x[%.3f,%.3f] y[%.3f,%.3f] z[%.3f,%.3f]'
                  % (Q[:, 0].min(), Q[:, 0].max(), Q[:, 1].min(), Q[:, 1].max(),
                     Q[:, 2].min(), Q[:, 2].max()))
            cnt = {}
            for i in out:
                v = verts[i]
                for k in range(3):
                    if v['w'][k] > 0.3:
                        b = v['bones'][k]
                        nm = src[b]['name'] if 0 <= b < len(src) else '?%d' % b
                        cnt[nm] = cnt.get(nm, 0) + 1
            for nm, n in sorted(cnt.items(), key=lambda kv: -kv[1])[:8]:
                print('      bone %-36s %d' % (nm, n))

        # are the far verts connected to the body by long triangles?
        far = set(int(i) for i in out)
        n_cross = 0
        longest = 0.0
        for a, b, c, _m in tris:
            s = {a, b, c}
            if s & far and not s <= far:
                n_cross += 1
                longest = max(longest, max(np.linalg.norm(P[a] - P[b]),
                                           np.linalg.norm(P[b] - P[c]),
                                           np.linalg.norm(P[c] - P[a])))
        print('   triangles bridging blob<->body: %d (longest %.3f m)'
              % (n_cross, longest))


if __name__ == '__main__':
    main()
