"""Round 45: WHERE does the arm re-fit stretch the mesh?

The global `edge max` jumps from 2.15 to 12.43 when arm_fit goes 0 -> 1.  A single
stretched triangle is not a problem, but it must not be somewhere visible.  This
lists the worst triangles and the body region their vertices are weighted to.
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')


def run(arm_fit):
    return H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                      spine_map='mesh', fallback='region', leg_tilt=5.7,
                      leg_splay=4.0, mesh_shift=(0.0, -0.100, -0.060),
                      upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                      arm_fit=arm_fit, verbose=False)


def main():
    a = run(0.0)
    b = run(1.0)
    tris = np.asarray([t[:3] for t in a['tris']])
    P0 = np.asarray(a['out'], float)
    P1 = np.asarray(b['out'], float)

    def edge_ratio(P):
        e = np.stack([np.linalg.norm(P[tris[:, 0]] - P[tris[:, 1]], axis=1),
                      np.linalg.norm(P[tris[:, 1]] - P[tris[:, 2]], axis=1),
                      np.linalg.norm(P[tris[:, 2]] - P[tris[:, 0]], axis=1)], 1)
        e0 = np.stack([np.linalg.norm(P0[tris[:, 0]] - P0[tris[:, 1]], axis=1),
                       np.linalg.norm(P0[tris[:, 1]] - P0[tris[:, 2]], axis=1),
                       np.linalg.norm(P0[tris[:, 2]] - P0[tris[:, 0]], axis=1)], 1)
        return e.max(1) / np.maximum(e0.max(1), 1e-9)

    for tag, P in (('arm_fit 0', P0), ('arm_fit 1', P1)):
        r = edge_ratio(P)
        print('%s: edge-ratio vs the ORIGINAL mesh  p99 %.2f max %.2f  |  >2x: %d '
              '| >5x: %d  of %d tris'
              % (tag, float(np.percentile(r, 99)), r.max(), int((r > 2).sum()),
                 int((r > 5).sum()), len(r)))

    # where are the >5x triangles?
    r = edge_ratio(P1)
    bad = np.where(r > 5.0)[0]
    print('\ntriangles stretched >5x by the arm re-fit: %d' % len(bad))
    if len(bad):
        cen = P1[tris[bad]].mean(1)
        print('  centroid bbox  x[%+.3f,%+.3f] y[%+.3f,%+.3f] z[%+.3f,%+.3f]'
              % (cen[:, 0].min(), cen[:, 0].max(), cen[:, 1].min(), cen[:, 1].max(),
                 cen[:, 2].min(), cen[:, 2].max()))
        # which source bone dominates the worst ones
        verts = a['verts']
        res = a['resolved']
        by_idx_name = {h['index']: h['name'] for h in a['hd2'] if h['name']}
        from collections import Counter
        cnt = Counter()
        for ti in bad:
            for vi in tris[ti]:
                v = verts[vi]
                best, bw = None, -1.0
                for j in range(3):
                    w = v['w'][j]
                    if w <= bw:
                        continue
                    best, bw = res.get(v['bones'][j]), w
                cnt[by_idx_name.get(best)] += 1
        print('  dominant bones of their vertices: %s' % cnt.most_common(8))
        # original edge length distribution
        e0 = np.stack([np.linalg.norm(P0[tris[bad, 0]] - P0[tris[bad, 1]], axis=1),
                       np.linalg.norm(P0[tris[bad, 1]] - P0[tris[bad, 2]], axis=1),
                       np.linalg.norm(P0[tris[bad, 2]] - P0[tris[bad, 0]], axis=1)], 1)
        print('  their ORIGINAL longest edge: min %.4f med %.4f max %.4f m'
              % (e0.max(1).min(), float(np.median(e0.max(1))), e0.max(1).max()))


if __name__ == '__main__':
    main()
