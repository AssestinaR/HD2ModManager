# -*- coding: utf-8 -*-
"""READ-ONLY: 网格小臂/手的**中轴线**到底在哪，离 HD2 自己的骨轴多远。

`_probe_hand_vs_hd2.py` 只报质心，会被"最近点换了顶点"和壳体大小污染。
这里改成正经的中心线测量：

  1. 选手臂顶点：l/r_elbow + l/r_hand_twist + l/r_hand 子树权重之和 > 0.5
  2. 以 HD2 的 elbow->hand 轴为参数轴，按 t（mm）切薄片
  3. 每片取质心 -> 得到网格自己的中心线
  4. 报 t、片内点数、离 HD2 轴的垂直偏移（u,v 两个分量 + 模长）
  5. 对 t in [0, 280] 的质心做直线拟合 -> 网格小臂轴方向 vs HD2 轴方向的夹角，
     以及拟合轴在 t=0 / t=280 处离 HD2 轴的垂直距离

⚠️ 只报几何事实，不做任何"好/坏"判断。观感只能靠渲染图 / Blender / 游戏。

usage:
    _probe_forearm_centerline_vs_hd2.py <npz_dir> [<npz_dir> ...]
"""
import json
import os
import sys

import numpy as np

# the WORKING elbow (source elbow after leg stance / upper-arm gain / shoulder
# rotation) -- same for every variant, read off the pipeline log.
ENOW = {'l': np.array([-0.4061, 0.0785, 1.4024]),
        'r': np.array([0.4055, 0.0785, 1.4014])}


def main():
    for d in sys.argv[1:]:
        z = np.load(os.path.join(d, 'model.npz'))
        meta = json.load(open(os.path.join(d, 'meta.json'), encoding='utf-8'))
        names = list(meta['bones'])
        slot = {}
        for i, n in enumerate(names):
            slot.setdefault(n, i)
        pos = np.asarray(z['ours_bind'], np.float64)
        wb = np.asarray(z['w_bone'], np.int64)
        wv = np.asarray(z['w_val'], np.float64)
        bh = np.asarray(z['b_head'], np.float64)

        print('=== %s' % d)
        for pre in ('l', 'r'):
            E = bh[slot[pre + '_elbow']]
            W = bh[slot[pre + '_hand']]
            axis = (W - E) / float(np.linalg.norm(W - E))
            # a stable perpendicular frame
            tmp = np.array([0.0, 0.0, 1.0])
            if abs(float(axis @ tmp)) > 0.9:
                tmp = np.array([0.0, 1.0, 0.0])
            u = np.cross(axis, tmp)
            u /= np.linalg.norm(u)
            v = np.cross(axis, u)

            idx = [i for i, n in enumerate(names)
                   if n == '%s_elbow' % pre or n == '%s_hand_twist' % pre
                   or n.startswith('%s_hand' % pre)]
            w = np.zeros(len(pos))
            for i in idx:
                w += np.where(wb == i, wv, 0.0).sum(axis=1)
            m = w > 0.5
            P = pos[m]
            rel = P - E
            t = rel @ axis
            perp = rel - np.outer(t, axis)
            pu = perp @ u
            pv = perp @ v

            print('  --- %s  (arm verts w>0.5: %d)   HD2 elbow %s'
                  % (pre, int(m.sum()), np.array2string(E, precision=4)))
            print('      t(mm)    n   du(mm)   dv(mm)   |d|(mm)')
            rows = []
            for tt in np.arange(-0.08, 0.401, 0.02):
                sel = np.abs(t - tt) < 0.01
                if sel.sum() < 8:
                    continue
                cu = float(pu[sel].mean())
                cv = float(pv[sel].mean())
                rows.append((tt, int(sel.sum()), cu, cv))
                print('     %+7.1f %5d  %+7.1f  %+7.1f  %8.1f'
                      % (1000 * tt, int(sel.sum()), 1000 * cu, 1000 * cv,
                         1000 * float(np.hypot(cu, cv))))

            # least-squares line through the station centroids, t in [0, 280] mm
            R = [r for r in rows if 0.0 <= r[0] <= 0.28]
            if len(R) >= 5:
                T = np.array([r[0] for r in R])
                U = np.array([r[2] for r in R])
                V = np.array([r[3] for r in R])
                ku = np.polyfit(T, U, 1)
                kv = np.polyfit(T, V, 1)
                # the mesh centre-line as a 3-D line: P(t) = E + t*axis + u*U + v*V
                d3 = axis + u * ku[0] + v * kv[0]
                d3 /= np.linalg.norm(d3)
                ang = float(np.degrees(np.arccos(np.clip(float(d3 @ axis), -1, 1))))
                o0 = float(np.hypot(ku[1], kv[1]))
                o1 = float(np.hypot(ku[0] * 0.28 + ku[1],
                                    kv[0] * 0.28 + kv[1]))
                print('      mesh forearm centre-line  vs HD2 elbow->hand axis : %6.2f deg'
                      % ang)
                print('      perpendicular offset at t=   0 mm : %6.1f mm'
                      % (1000 * o0))
                print('      perpendicular offset at t= 280 mm : %6.1f mm'
                      % (1000 * o1))

            # how far is the mesh centre-line from the WORKING elbow?
            relN = ENOW[pre] - E
            tN = float(relN @ axis)
            s = np.abs(t - tN) < 0.01
            if s.sum() >= 8:
                cu = float(pu[s].mean())
                cv = float(pv[s].mean())
                c = E + tN * axis + cu * u + cv * v
                print('      mesh centre-line at the WORKING elbow (t=%+.1f mm): '
                      '%s   |mesh - working elbow| = %.1f mm'
                      % (1000 * tN, np.array2string(c, precision=4),
                         1000 * float(np.linalg.norm(c - ENOW[pre]))))
            print()


if __name__ == '__main__':
    main()
