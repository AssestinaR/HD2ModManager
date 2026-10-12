"""Where does the "1.8 m spike" actually live: source, aligned, or retargeted?

`drop_outlier_tris` is called on `src_pts`, and its docstring says the spike "is
present in the source bind pose as well".  But the raw source mesh has a maximum
edge of only 0.337 m (Nick).  So either the spike lives in `out`, or the
docstring is stale.  Measure all three meshes.
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H

ADDONS = 'D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons'
MODELS = [
    ('Lunalice_Zoey.vpk', 'survivor_teenangst', 'zoey'),
    ('Lunalice_Rochelle.vpk', 'survivor_producer', 'rochelle'),
    ('Elise_Coach.vpk', 'survivor_coach', 'coach'),
    ('Lilium_Sancta_Nick.vpk', 'survivor_gambler', 'nick'),
    ('Serenade_Ellis.vpk', 'survivor_mechanic', 'ellis'),
]
BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'


def maxedge(P, tris):
    L = np.empty(len(tris))
    for i, (a, b, c, _m) in enumerate(tris):
        L[i] = max(np.linalg.norm(P[a] - P[b]), np.linalg.norm(P[b] - P[c]),
                   np.linalg.norm(P[c] - P[a]))
    return L


def main():
    print('%-10s %10s %10s %10s %10s' % ('model', 'src max', 'src1 max',
                                          'out max', 'out bbox diag'))
    for vpk, model, tag in MODELS:
        path = os.path.join(ADDONS, vpk)
        r = H.retarget(path, model, TOC, BH, PP, align='yaw', spine_map='mesh',
                       fallback='distance', leg_tilt=5.7, leg_splay=4.0,
                       skeleton='native', verbose=False)
        tris = r['tris']
        a = maxedge(r['src_pts'], tris).max()
        b = maxedge(r['src1'], tris).max()
        c = maxedge(r['out'], tris).max()
        o = r['out']
        d = np.linalg.norm([np.ptp(o[:, 0]), np.ptp(o[:, 1]), np.ptp(o[:, 2])])
        print('%-10s %10.4f %10.4f %10.4f %10.4f' % (tag, a, b, c, d))
        print('           out bbox x[%.3f,%.3f] y[%.3f,%.3f] z[%.3f,%.3f]'
              % (o[:, 0].min(), o[:, 0].max(), o[:, 1].min(), o[:, 1].max(),
                 o[:, 2].min(), o[:, 2].max()))
        cen = np.median(o, axis=0)
        far = np.linalg.norm(o - cen, axis=1)
        for thr in (0.8, 1.2, 1.8):
            n = int((far > thr).sum())
            if n:
                q = o[far > thr]
                print('           %d verts > %.1f m from centre, '
                      'x[%.2f,%.2f] y[%.2f,%.2f] z[%.2f,%.2f]'
                      % (n, thr, q[:, 0].min(), q[:, 0].max(), q[:, 1].min(),
                         q[:, 1].max(), q[:, 2].min(), q[:, 2].max()))


if __name__ == '__main__':
    main()
