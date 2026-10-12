"""Pick a SAFE `drop_outlier_tris` floor, on numbers.

The function exists to remove the stray spike Nick (1.8 m) and Ellis (1.0 m)
ship.  But a FIXED 6 cm floor also deletes legitimate large flat cards (hair
strands, shoe soles).  This script measures, for all five models:

  * the mesh height,
  * the 99.9th percentile edge (i.e. the largest "normal" triangle),
  * the largest edge overall (the spike, if any),
  * the smallest floor that keeps every normal triangle,
  * the largest floor that still removes the spike.

If there is a comfortable gap between the two, a `floor_frac * height` rule can
sit inside it for every model at once.
"""
import os
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


def main():
    print('%-10s %7s %9s %9s %9s %9s %9s %9s'
          % ('model', 'height', 'p99', 'p99.9', 'max', 'minFloor',
             'maxFloor', 'gap'))
    for vpk, model, tag in MODELS:
        path = os.path.join(ADDONS, vpk)
        _mp, _mdl, verts, tris, _src, _sh = H.load_source(path, model)
        P = np.array([[v['pos'][0] * E.IN2M, v['pos'][1] * E.IN2M,
                       v['pos'][2] * E.IN2M] for v in verts])
        L = np.empty(len(tris))
        for i, (a, b, c, _m) in enumerate(tris):
            L[i] = max(np.linalg.norm(P[a] - P[b]), np.linalg.norm(P[b] - P[c]),
                       np.linalg.norm(P[c] - P[a]))
        h = float(P[:, 2].max() - P[:, 2].min())
        p99 = float(np.percentile(L, 99))
        p999 = float(np.percentile(L, 99.9))
        mx = float(L.max())
        # smallest floor keeping every triangle below the spike: use p99.9 as a
        # robust "normal max" (p100 would be the spike itself)
        min_floor = p999
        # largest floor still deleting the spike: the spike's own edge
        max_floor = mx
        print('%-10s %7.3f %9.4f %9.4f %9.4f %9.4f %9.4f %9.4f'
              % (tag, h, p99, p999, mx, min_floor, max_floor,
                 max_floor - min_floor))
        print('           -> spike is %s (%.3f m = %.2f x height)'
              % ('YES' if mx > 2 * p999 else 'no', mx, mx / h))


if __name__ == '__main__':
    main()
