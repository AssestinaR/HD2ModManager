# -*- coding: utf-8 -*-
"""READ-ONLY: 网格小臂**实际**绕哪一点转？

方法：两个 model.npz 变体只差「肘部目标方向」。取只被小臂骨
(`l/r_elbow` + `l/r_hand_twist`) 支配的顶点（权重 > 0.9），做 Kabsch 刚体拟合
得到相对变换 (R, t)，再解出它的**螺旋轴**：

    t = (I - R) c      ->  c = pinv(I - R) @ t  (+ lambda * axis)

`c` 就是网格实际转动的中心。把它和下面几点比：
  * HD2 自己的 l_elbow head
  * 管线日志里写的 pivot（"working elbow"，起源肘随大臂转过去的位置）
  * HD2 自己的 l_shoulder head

usage:
    _probe_elbow_pivot_empirical.py <npz_A> <npz_B>
"""
import json
import os
import sys

import numpy as np

ENOW = {'l': np.array([-0.4061, 0.0785, 1.4024]),
        'r': np.array([0.4055, 0.0785, 1.4014])}


def load(d):
    z = np.load(os.path.join(d, 'model.npz'))
    meta = json.load(open(os.path.join(d, 'meta.json'), encoding='utf-8'))
    names = list(meta['bones'])
    slot = {}
    for i, n in enumerate(names):
        slot.setdefault(n, i)
    return (np.asarray(z['ours_bind'], np.float64),
            np.asarray(z['w_bone'], np.int64),
            np.asarray(z['w_val'], np.float64),
            np.asarray(z['b_head'], np.float64), names, slot)


def kabsch(A, B):
    ca, cb = A.mean(0), B.mean(0)
    H = (A - ca).T @ (B - cb)
    U, _s, Vt = np.linalg.svd(H)
    D = np.eye(3)
    if np.linalg.det(Vt.T @ U.T) < 0:
        D[2, 2] = -1.0
    R = Vt.T @ D @ U.T
    t = cb - R @ ca
    return R, t


def main():
    pa, pb = sys.argv[1], sys.argv[2]
    Pa, wba, wva, bha, names_a, slot_a = load(pa)
    Pb, wbb, wvb, bhb, names_b, slot_b = load(pb)
    assert names_a == names_b, 'bone list differs'
    names, slot, bh = names_a, slot_a, bha

    print('A = %s' % pa)
    print('B = %s' % pb)
    for pre in ('l', 'r'):
        idx = [i for i, n in enumerate(names)
               if n in ('%s_elbow' % pre, '%s_hand_twist' % pre)
               or n.startswith('%s_hand' % pre)]
        w = np.zeros(len(Pa))
        for i in idx:
            w += np.where(wba == i, wva, 0.0).sum(axis=1)
        moved = np.linalg.norm(Pb - Pa, axis=1) > 0.001
        m = (w > 0.9) & moved
        if m.sum() < 50:
            m = (w > 0.5) & moved
        A, B = Pa[m], Pb[m]
        R, t = kabsch(A, B)
        res = float(np.sqrt(np.mean(np.sum((A @ R.T + t - B) ** 2, axis=1))))
        ang = float(np.degrees(np.arccos(np.clip((np.trace(R) - 1.0) / 2.0, -1, 1))))
        # rotation axis = the eigenvector of R for eigenvalue 1
        ev, evec = np.linalg.eig(R)
        k = int(np.argmin(np.abs(ev - 1.0)))
        n = np.real(evec[:, k])
        n /= np.linalg.norm(n)
        if float(n @ np.array([0.0, 1.0, 0.0])) < 0 and abs(float(n[1])) > 0.5:
            n = -n
        # point on the screw axis
        M = np.eye(3) - R
        c = np.linalg.pinv(M) @ t
        E = bh[slot[pre + '_elbow']]
        S = bh[slot[pre + '_shoulder']]
        W = bh[slot[pre + '_hand']]
        print('  --- %s   elbow-subtree shell (w>0.5, moved): %d verts   Kabsch residual %.3f mm'
              % (pre, int(m.sum()), 1000 * res))
        print('      relative rotation  %.2f deg about (%.3f, %.3f, %.3f)'
              % (ang, n[0], n[1], n[2]))
        print('      effective pivot    %s  (only its distance to the AXIS matters)'
              % np.array2string(c, precision=4))
        for lbl, Q in (('HD2 l_shoulder', S), ('HD2 l_elbow', E), ('HD2 l_hand', W),
                       ('log pivot (working elbow)', ENOW[pre])):
            d = Q - c
            dperp = d - (d @ n) * n
            print('        %-26s %s   dist to AXIS %7.1f mm'
                  % (lbl, np.array2string(Q, precision=4),
                     1000 * float(np.linalg.norm(dperp))))
    print()


if __name__ == '__main__':
    main()
