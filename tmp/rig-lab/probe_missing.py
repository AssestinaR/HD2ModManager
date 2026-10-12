"""Is the geometry missing from the DATA, or only from the FIGURE?

`drop_outlier_tris` exists because Nick/Ellis ship a stray spike (a 1.8 m / 1.0 m
triangle fan).  But its rule is "any triangle with an edge longer than
max(8 x median, 0.06 m) is an artefact" -- and on a 120 k-vertex mesh the median
edge is a few millimetres, so the effective cut-off is 6 cm.  Anything modelled as
a large flat card (a ponytail strand, the block of a platform shoe) has edges
longer than that and gets deleted from the render.
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import export_obj as E
import hd2_style as H

VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
MODEL = 'survivor_teenangst'


def edge_lens(P, tris):
    out = np.empty(len(tris))
    for i, (a, b, c, _m) in enumerate(tris):
        out[i] = max(np.linalg.norm(P[a] - P[b]), np.linalg.norm(P[b] - P[c]),
                     np.linalg.norm(P[c] - P[a]))
    return out


def main():
    mdl_path, mdl, verts, tris, src, short = H.load_source(VPK, MODEL)
    P = np.array([[v['pos'][0] * E.IN2M, v['pos'][1] * E.IN2M,
                   v['pos'][2] * E.IN2M] for v in verts])
    print('loaded %d verts / %d tris' % (len(P), len(tris)))

    L = edge_lens(P, tris)
    print('\n-- triangle max-edge distribution (m) --')
    for q in (50, 75, 90, 99, 99.5, 99.9, 100):
        print('   p%-5s %.4f' % (q, np.percentile(L, q)))
    med = float(np.median(L))
    print('   median %.5f  ->  drop_outlier_tris cuts at max(8*med, 0.06) = %.4f'
          % (med, max(8 * med, 0.06)))

    print('\n-- what each cut-off would delete --')
    for lim in (0.06, 0.10, 0.20, 0.40, 0.80):
        keep = L <= lim
        used = set()
        for i in np.where(keep)[0]:
            a, b, c, _m = tris[i]
            used.update((a, b, c))
        lost = len(P) - len(used)
        print('   lim %.2f m: keeps %6d/%6d tris, loses %6d verts'
              % (lim, int(keep.sum()), len(tris), lost))

    # ---- what exactly does the 0.06 cut remove? ---------------------------
    keep = L <= 0.06
    used = set()
    for i in np.where(keep)[0]:
        a, b, c, _m = tris[i]
        used.update((a, b, c))
    lost = sorted(set(range(len(P))) - used)
    Q = P[lost]
    print('\n-- the %d vertices the current cut deletes --' % len(lost))
    print('   bbox x[%.3f,%.3f] y[%.3f,%.3f] z[%.3f,%.3f]'
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
    print('   dominant bones:')
    for nm, n in sorted(cnt.items(), key=lambda kv: -kv[1])[:12]:
        print('      %-34s %d' % (nm, n))

    # how far back / how low is the WHOLE mesh -- i.e. is the tail even there?
    print('\n-- whole mesh bbox (m) --')
    print('   x[%.3f,%.3f] y[%.3f,%.3f] z[%.3f,%.3f]'
          % (P[:, 0].min(), P[:, 0].max(), P[:, 1].min(), P[:, 1].max(),
             P[:, 2].min(), P[:, 2].max()))
    back = P[P[:, 1] < np.percentile(P[:, 1], 0.5)]
    print('   the 0.5%% most-backward vertices: y[%.3f,%.3f] z[%.3f,%.3f] n=%d'
          % (back[:, 1].min(), back[:, 1].max(), back[:, 2].min(),
             back[:, 2].max(), len(back)))


if __name__ == '__main__':
    main()
