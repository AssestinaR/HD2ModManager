"""Where do BLEND and RIGID each put their worst edges?  Round 45d."""
import os
import sys
from collections import Counter

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
AR = (10.46, -18.55, 29.63)
BASE = dict(align='yaw', spine_map='mesh', fallback='region', leg_tilt=5.7,
            leg_splay=4.0, mesh_shift=(0.0, -0.100, -0.060), upper_scale=1.12,
            upper_pivot_z=1.15, upper_blend=0.45, arm_fit=1.0, verbose=False)


def run(**kw):
    b = dict(BASE); b.update(kw)
    return H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, **b)


def main():
    off = run(arm_fit=0.0)
    tris = np.asarray([t[:3] for t in off['tris']])
    P0 = np.asarray(off['out'], float)
    verts = off['verts']
    res = off['resolved']
    by_idx = {h['index']: h['name'] for h in off['hd2'] if h['name']}
    dom = []
    for v in verts:
        best, bw = None, -1.0
        for j in range(3):
            w = v['w'][j]
            if w <= bw:
                continue
            best, bw = res.get(v['bones'][j]), w
        dom.append(by_idx.get(best, ''))
    dom = np.array(dom)

    idx = {h['name']: h['index'] for h in off['hd2'] if h['name']}
    rest = off['rest_shipped']
    a = rest[idx['l_shoulder']][:3, 3].astype(float)
    b = rest[idx['l_hand']][:3, 3].astype(float)
    u = (b - a) / np.linalg.norm(b - a)
    t0 = (P0 - a) @ u
    perp0 = np.linalg.norm((P0 - a) - np.outer(t0, u), axis=1)
    region = (P0[:, 0] < -0.15) & (perp0 < 0.16)

    e0 = np.stack([np.linalg.norm(P0[tris[:, 0]] - P0[tris[:, 1]], axis=1),
                   np.linalg.norm(P0[tris[:, 1]] - P0[tris[:, 2]], axis=1),
                   np.linalg.norm(P0[tris[:, 2]] - P0[tris[:, 0]], axis=1)], 1)
    inreg = region[tris].all(1)

    for mode in ('blend', 'rigid'):
        r = run(arm_rot=AR, arm_rot_mode=mode)
        P = np.asarray(r['out'], float)
        e = np.stack([np.linalg.norm(P[tris[:, 0]] - P[tris[:, 1]], axis=1),
                      np.linalg.norm(P[tris[:, 1]] - P[tris[:, 2]], axis=1),
                      np.linalg.norm(P[tris[:, 2]] - P[tris[:, 0]], axis=1)], 1)
        ratio = (e / np.maximum(e0, 1e-9)).max(1)
        bad = np.where(inreg & (ratio > 2.0))[0]
        print('\n=== %s : %d triangles in the left arm >2x ===' % (mode, len(bad)))
        cen = P[tris[bad]].mean(1)
        tt = (cen - a) @ u
        hist, be = np.histogram(tt, bins=[-0.1, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.8])
        for i in range(len(hist)):
            if hist[i]:
                print('    t %+.2f..%+.2f : %d' % (be[i], be[i + 1], hist[i]))
        c = Counter()
        for ti in bad:
            for vi in tris[ti]:
                c[dom[vi]] += 1
        print('  dominant bones: %s' % c.most_common(6))
        o = np.argsort(-ratio[bad])[:5]
        for k in o:
            ti = bad[k]
            print('    ratio %.2f  orig edge %.4f m  centroid (%+.3f %+.3f %+.3f)'
                  % (ratio[ti], float(e0[ti].max()), cen[k, 0], cen[k, 1],
                     cen[k, 2]))

    # print the composed pivots
    print('\n--- composed pivots (rigid) ---')
    for side in ('l',):
        nms = ('%s_shoulder' % side, '%s_elbow' % side, '%s_hand' % side)
        J = [rest[idx[n]][:3, 3].astype(float) for n in nms]
        th = [np.radians(AR[k]) for k in range(3)]
        M = np.eye(2); cv = np.zeros(2)
        for k in range(3):
            Jk = J[k][[0, 2]]
            Mk = np.array([[np.cos(th[k]), np.sin(th[k])],
                           [-np.sin(th[k]), np.cos(th[k])]])
            cv = M @ (Jk - Mk @ Jk) + cv
            M = M @ Mk
            A = np.eye(2) - M
            pv = np.linalg.solve(A, cv)
            print('  seg %d: joint (x %+.4f z %+.4f) -> pivot (x %+.4f z %+.4f) '
                  'cum angle %+.2f deg' % (k, Jk[0], Jk[1], pv[0], pv[1],
                                           np.degrees(sum(th[:k + 1]))))


if __name__ == '__main__':
    main()
