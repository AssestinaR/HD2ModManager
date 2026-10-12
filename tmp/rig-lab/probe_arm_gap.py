"""Round 45b: is the armpit actually TORN (two surfaces pulled apart), or is it
just a shading seam?

A "split seam" is not a stretched triangle -- stretching cannot open a hole in a
connected surface.  It means two SEPARATE mesh islands used to overlap there and
no longer do.  So: split the mesh into connected components, and for every pair of
big components measure the closest approach inside the shoulder box, with the arm
re-fit off and on.

usage: python probe_arm_gap.py
"""
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

BOX = (-0.50, -0.30, 1.14, -0.04, 0.32, 1.62)   # x0,y0,z0,x1,y1,z1 -- L shoulder
MIN_SIZE = 150


def run(arm_fit):
    return H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                      spine_map='mesh', fallback='region', leg_tilt=5.7,
                      leg_splay=4.0, mesh_shift=(0.0, -0.100, -0.060),
                      upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                      arm_fit=arm_fit, verbose=False)


def components(nv, tris):
    parent = np.arange(nv, dtype=np.int64)

    def find(x):
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    for t in tris:
        a, b, c = int(t[0]), int(t[1]), int(t[2])
        ra, rb, rc = find(a), find(b), find(c)
        if rb != ra:
            parent[rb] = ra
        if rc != find(a):
            parent[find(rc)] = find(a)
    root = np.array([find(i) for i in range(nv)])
    return root


def main():
    a = run(0.0)
    b = run(1.0)
    tris = np.asarray([t[:3] for t in a['tris']], dtype=np.int64)
    P0 = np.asarray(a['out'], float)
    P1 = np.asarray(b['out'], float)
    nv = len(P0)
    root = components(nv, tris)

    # label the components and describe them
    uniq, inv, cnt = np.unique(root, return_inverse=True, return_counts=True)
    order = np.argsort(-cnt)
    verts = a['verts']
    res = a['resolved']
    by_name = {h['index']: h['name'] for h in a['hd2'] if h['name']}

    print('mesh: %d verts, %d tris, %d connected components (%d with >=%d verts)'
          % (nv, len(tris), len(uniq), int((cnt >= MIN_SIZE).sum()), MIN_SIZE))
    big = [i for i in order if cnt[i] >= MIN_SIZE]
    label = {}
    for li, i in enumerate(big):
        sel = np.where(inv == i)[0]
        c = Counter()
        for k in sel[::max(1, len(sel) // 400)]:
            v = verts[k]
            best, bw = None, -1.0
            for j in range(3):
                w = v['w'][j]
                if w <= bw:
                    continue
                best, bw = res.get(v['bones'][j]), w
            c[by_name.get(best)] += 1
        label[i] = 'C%d' % li
        bb0, bb1 = P0[sel].min(0), P0[sel].max(0)
        print('  %s  %6d verts  bbox x[%+.3f,%+.3f] z[%+.3f,%+.3f]  top bones %s'
              % (label[i], len(sel), bb0[0], bb1[0], bb0[2], bb1[2],
                 c.most_common(4)))

    x0, y0, z0, x1, y1, z1 = BOX
    inside = ((P0[:, 0] > x0) & (P0[:, 0] < x1) & (P0[:, 1] > y0) &
              (P0[:, 1] < y1) & (P0[:, 2] > z0) & (P0[:, 2] < z1))
    print('\nshoulder box x[%.2f,%.2f] y[%.2f,%.2f] z[%.2f,%.2f] holds %d verts'
          % (BOX[0], BOX[3], BOX[1], BOX[4], BOX[2], BOX[5], int(inside.sum())))

    print('\nclosest approach between components inside that box (metres):')
    print('  %-10s %-10s %6s %6s %10s %10s' %
          ('A', 'B', 'nA', 'nB', 'arm_fit 0', 'arm_fit 1'))
    for ii in range(len(big)):
        for jj in range(ii + 1, len(big)):
            ia, ib = big[ii], big[jj]
            ma = inside & (inv == ia)
            mb = inside & (inv == ib)
            if ma.sum() < 20 or mb.sum() < 20:
                continue
            A0, B0 = P0[ma], P0[mb]
            A1, B1 = P1[ma], P1[mb]

            def mind(P, Q):
                best = 1e9
                for s in range(0, len(P), 400):
                    d = np.linalg.norm(P[s:s + 400, None, :] - Q[None, :, :], axis=2)
                    best = min(best, float(d.min()))
                return best
            print('  %-10s %-10s %6d %6d %10.4f %10.4f'
                  % (label[ia], label[ib], int(ma.sum()), int(mb.sum()),
                     mind(A0, B0), mind(A1, B1)))


if __name__ == '__main__':
    main()
