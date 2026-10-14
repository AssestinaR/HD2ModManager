# -*- coding: utf-8 -*-
"""READ-ONLY 定位线索（不是结论）：网格的「手」离 HD2 自己的腕骨有多远。

对每个 model.npz 变体：
  * 取 l_hand 子树权重 > 0.9 的顶点（手套/手壳）算质心 C
  * 报告 C 到 HD2 自己的 l_hand 骨 head 的距离，以及 C 在
    (HD2 elbow -> HD2 hand) 轴上的投影 t 和垂直距离 r
  * 同时报告小臂（l_elbow 子树，权重>0.6）的拟合管轴方向与
    HD2 自己 l_elbow->l_hand 方向的夹角

⚠️ 这只是「手大概在不在腕骨附近」的定位线索，**不构成好坏判断**。
   观感只能靠渲染图 / Blender / 游戏里看。

usage:
    _probe_hand_vs_hd2.py <npz_dir> [<npz_dir> ...]
"""
import json
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

# "our elbow" -- the SOURCE T-pose elbow after the leg stance, the upper-arm
# gain and the shoulder rotation.  Read straight off the pipeline log
# (`dump_blender.py --arm-aim-wrist`): `pivot = source elbow (...)`.  It is the
# SAME for every variant, because only the forearm rotation differs.
ENOW = {'l': np.array([-0.4061, 0.0785, 1.4024]),
        'r': np.array([0.4055, 0.0785, 1.4014])}

# the source T-pose forearm length (metres) -- |joint(L_Hand) - joint(L_Forearm)|
SRC_FOREARM = 0.2885

# where the source `L_Hand` JOINT actually ends up.  For a given variant the
# forearm direction is known from the mesh itself, so:
#     our_wrist = our_elbow + SRC_FOREARM * (mesh forearm->hand direction)
# The caller passes it in via --wrist L:x,y,z  R:x,y,z  (read off the pipeline
# log / `_probe_arm_aim_geometry.py`).
WRIST = {}


def main():
    dirs = []
    for a in sys.argv[1:]:
        if a.startswith('--wrist='):
            for part in a[len('--wrist='):].split(';'):
                sd, _, xyz = part.partition(':')
                WRIST[sd.strip()] = np.array(
                    [float(v) for v in xyz.split(',')], np.float64)
            continue
        dirs.append(a)

    for d in dirs:
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
            hidx = [i for i, n in enumerate(names) if n.startswith(pre + '_hand')]
            eidx = [i for i, n in enumerate(names)
                    if n in ('%s_elbow' % pre, '%s_hand_twist' % pre)]
            def wsum(idx):
                w = np.zeros(len(pos))
                for i in idx:
                    w += np.where(wb == i, wv, 0.0).sum(axis=1)
                return w
            hw = wsum(hidx)
            ew = wsum(eidx)

            # the WRIST RING: the weight transition band between forearm and
            # hand.  Its centroid is the best mesh-side stand-in for "where our
            # wrist joint ended up".
            band = (hw > 0.30) & (hw < 0.70)
            if band.sum() > 20:
                Cb = pos[band].mean(0)
                print('  %s  wrist-band centroid (w_hand 0.30..0.70, n=%d)  '
                      'dist to HD2 %s_hand head = %7.1f mm'
                      % (pre, int(band.sum()), pre,
                         1000.0 * float(np.linalg.norm(Cb - W))))
            else:
                print('  %s  (no wrist band found, n=%d)' % (pre, int(band.sum())))

            if (hw > 0.9).sum() > 50:
                C = pos[hw > 0.9].mean(0)
                axis = (W - E) / float(np.linalg.norm(W - E))
                rel = C - E
                t = float(rel @ axis)
                r = float(np.linalg.norm(rel - t * axis))
                print('  %s  hand-shell centroid  dist to HD2 %s_hand head = %7.1f mm'
                      % (pre, pre, 1000.0 * float(np.linalg.norm(C - W))))
                print('      along HD2 elbow->hand axis t = %7.1f mm   perpendicular r = %7.1f mm'
                      % (1000.0 * t, 1000.0 * r))
            else:
                print('  %s  (no hand shell found)' % pre)

            # forearm tube axis vs HD2's own forearm axis
            m = (ew > 0.6)
            if m.sum() > 200:
                P = pos[m]
                axis = (W - E) / float(np.linalg.norm(W - E))
                rel = P - E
                t = rel @ axis
                perp = rel - np.outer(t, axis)
                # PCA over the shell gives the tube direction
                X = P - P.mean(0)
                _u, _s, vt = np.linalg.svd(X, full_matrices=False)
                tube = vt[0] / np.linalg.norm(vt[0])
                if float(tube @ axis) < 0:
                    tube = -tube
                ang = float(np.degrees(np.arccos(np.clip(float(tube @ axis), -1, 1))))
                off = float(np.linalg.norm(perp.mean(0)))
                print('      forearm shell PCA axis vs HD2 elbow->hand axis : %6.2f deg'
                      % ang)
                print('      forearm shell mean offset from that axis       : %6.1f mm'
                      % (1000.0 * off))

                # DECISIVE: forearm->hand direction of the MESH, compared with
                # (a) HD2's own elbow->hand direction and (b) the aim direction
                # "our elbow -> HD2 hand" (the user's rule).
                if (hw > 0.9).sum() > 50:
                    C_f = P.mean(0)
                    C_h = pos[hw > 0.9].mean(0)
                    dmesh = C_h - C_f
                    dmesh = dmesh / float(np.linalg.norm(dmesh))
                    own = (W - E) / float(np.linalg.norm(W - E))
                    aim = (W - ENOW[pre]) / float(np.linalg.norm(W - ENOW[pre]))
                    print('      MESH forearm->hand direction vs HD2 own  : %6.2f deg'
                          % float(np.degrees(np.arccos(np.clip(float(dmesh @ own), -1, 1)))))
                    print('      MESH forearm->hand direction vs the AIM : %6.2f deg'
                          % float(np.degrees(np.arccos(np.clip(float(dmesh @ aim), -1, 1)))))

                    if pre in WRIST:
                        Wp = WRIST[pre]
                        nv = float(np.min(np.linalg.norm(pos - Wp, axis=1)))
                        Cb2 = pos[band].mean(0) if band.sum() > 20 else None
                        print('      predicted OUR-WRIST %s : nearest mesh vertex %.1f mm'
                              % (np.array2string(Wp, precision=4), 1000.0 * nv))
                        if Cb2 is not None:
                            print('      ... and that point is %.1f mm from HD2 %s_hand head'
                                  % (1000.0 * float(np.linalg.norm(Wp - W)),
                                     pre))
        print()


if __name__ == '__main__':
    main()
