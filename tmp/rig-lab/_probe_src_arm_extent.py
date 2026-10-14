# -*- coding: utf-8 -*-
"""READ-ONLY: 起源模型 T-pose 里，手臂网格到底延伸到哪儿？起源关节又在哪儿？

只回答一个问题：`L_Hand` 这个起源关节是不是落在网格的手腕位置。
用 model.npz 的 `src_tpose`（初始刚体对齐后的起源 T-pose 网格）＋管线日志里的
起源关节坐标比对。

usage:
    _probe_src_arm_extent.py <npz_dir> [joint_x,joint_y,joint_z]
"""
import os
import sys

import numpy as np


def main():
    d = sys.argv[1]
    z = np.load(os.path.join(d, 'model.npz'))
    P = np.asarray(z['src_tpose'], np.float64)
    print('src_tpose: %d verts' % len(P))
    print('bbox  x[%.4f, %.4f]  y[%.4f, %.4f]  z[%.4f, %.4f]'
          % (P[:, 0].min(), P[:, 0].max(), P[:, 1].min(), P[:, 1].max(),
             P[:, 2].min(), P[:, 2].max()))

    # the left arm in the T-pose runs along -X at shoulder height
    arm = (P[:, 0] < -0.30) & (P[:, 2] > 1.25) & (P[:, 2] < 1.75)
    A = P[arm]
    print('left-arm region (x<-0.30, 1.25<z<1.75): %d verts' % len(A))
    if len(A):
        print('  x range [%.4f, %.4f]   y range [%.4f, %.4f]   z range [%.4f, %.4f]'
              % (A[:, 0].min(), A[:, 0].max(), A[:, 1].min(), A[:, 1].max(),
                 A[:, 2].min(), A[:, 2].max()))
        for t in np.arange(0.0, 1.01, 0.05):
            x = -0.1154 + t * (-0.7900 + 0.1154)   # shoulder -> fingertip guess
            sel = np.abs(A[:, 0] - x) < 0.01
            if sel.sum() > 5:
                r = A[sel]
                c = r.mean(0)
                rad = float(np.linalg.norm(
                    r[:, 1:3] - c[1:3], axis=1).mean())
                print('    x=%+.4f  n=%3d  centroid y=%+.4f z=%+.4f  mean radius %.1f mm'
                      % (x, int(sel.sum()), c[1], c[2], 1000.0 * rad))

    if len(sys.argv) > 2:
        J = np.array([float(v) for v in sys.argv[2].split(',')], np.float64)
        nv = float(np.min(np.linalg.norm(P - J, axis=1)))
        print('joint %s : nearest src_tpose vertex = %.1f mm'
              % (np.array2string(J, precision=4), 1000.0 * nv))


if __name__ == '__main__':
    main()
