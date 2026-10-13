# -*- coding: utf-8 -*-
"""方案 B 的地基：手臂该有多长，是 HD2 说了算，不是 Lunalice 说了算。

`arm_scratch_upper_gain` 的作用是"把肘以下整段沿 T-pose 肩->肘轴刚性平移 `gain`"，
于是它精确地等于"这条手臂短了多少"。但 0.05 这个值是**手调**出来的，而且只对
Lunalice 成立 —— 换一个源模型（Lilium）就短 36.65 mm，手就落在骨头内侧。

本探针量的是**唯一有资格当基准的那个长度**：

    gain(model) 应该 = |rest_hd2[l_hand] - rest_hd2[l_shoulder]|
                       - |align(L_Hand) - align(L_UpperArm)|

左边是 HD2 自己的骨架（最终网格就绑在它上面、游戏动画驱动的也是它），右边是
源模型按管线自己的对齐映射进 HD2 空间之后的手臂长度。两边相减 = 这条手臂短了
多少 = 应该补多少。

为什么可以直接相减：对齐是"绕竖轴旋转 + 一个统一缩放 + 一个平移"，**保长**；
`gain` 在 T-pose 里沿臂轴平移肘以下，之后 T->A 的那一步是绕肩的刚体旋转，
**同样保长**。所以长度在三个坐标系里是同一个数，可以放心比较。

同时打印三个关节（肩/肘/手）各自到 HD2 的**三维偏移**，用来回答：
"补完长度之后还剩多少？"——如果两款模型的 Y/Z 偏移同号同量级，说明那是
**对齐本身的残差**（对齐钉的是头顶，不是肩），不是模型差异。

usage: _probe_armtarget.py
"""
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import _probe_hops as PH

CH = [('L_UpperArm', 'l_shoulder'), ('L_Forearm', 'l_elbow'),
      ('L_Hand', 'l_hand')]
SEGS = [('L_UpperArm', 'L_Forearm'), ('L_Forearm', 'L_Hand'),
        ('L_UpperArm', 'L_Hand')]
HS = [('l_shoulder', 'l_elbow'), ('l_elbow', 'l_hand'),
      ('l_shoulder', 'l_hand')]


def mm(v):
    return float(np.linalg.norm(v)) * 1000.0


def main():
    hd2, by_idx_name, by_idx, children, parents = PH.rig()
    hby = {h['name']: h for h in hd2 if h['name']}

    # ---- HD2's own skeleton, in the frame the shipped mesh is bound to ----
    H = {}
    for nm in ('l_shoulder', 'l_elbow', 'l_hand'):
        H[nm] = PH.ST.origin_np(PH.ST.m_to_np(hby[nm]['world']))
    print('=' * 84)
    print('HD2 自己的手臂长度（rest_hd2，最终网格就绑在它上面）')
    for p, q in HS:
        print('   %-22s %8.2f mm' % ('%s -> %s' % (p, q), mm(H[q] - H[p])))
    print()

    # ---- each source model, aligned by the pipeline's own recipe ----------
    body = PH.ST.snap_body(PH.SPINE_MAP)
    A = {}
    for tag, root, vpk in PH.RUNS:
        al = PH.alignment(os.path.join(root, vpk), PH.MODEL, hby, body)
        js = {}
        for s_nm, _h_nm in CH:
            b = al['short'].get(s_nm)
            if b is None:
                continue
            js[s_nm] = al['align_pts'](PH.ST.origin_np(
                PH.ST.m_to_np(b['world_m'])).reshape(1, 3))[0] + al['t']
        A[tag] = js

    print('=' * 84)
    print('源模型（按管线自己的对齐映射进 HD2 空间）的手臂长度 / 与 HD2 的差')
    print('   %-10s %-22s %10s %10s %10s' % ('model', 'segment', 'len_mm',
                                             'HD2_mm', 'need_gain'))
    for tag in A:
        for (p, q), (hp, hq) in zip(SEGS, HS):
            if p not in A[tag] or q not in A[tag]:
                continue
            ls = mm(A[tag][q] - A[tag][p])
            lh = mm(H[hq] - H[hp])
            print('   %-10s %-22s %10.2f %10.2f %+10.2f'
                  % (tag, '%s->%s' % (p.replace('L_', ''),
                                      q.replace('L_', '')), ls, lh, lh - ls))
        print()

    print('=' * 84)
    print('逐关节三维偏移：源模型（对齐后） - HD2 自己（mm）')
    print('   %-10s %-12s %9s %9s %9s %9s' % ('model', 'joint', 'dX', 'dY',
                                              'dZ', '|d|'))
    for tag in A:
        for s_nm, h_nm in CH:
            if s_nm not in A[tag]:
                continue
            d = (A[tag][s_nm] - H[h_nm]) * 1000.0
            print('   %-10s %-12s %9.2f %9.2f %9.2f %9.2f'
                  % (tag, s_nm, d[0], d[1], d[2], float(np.linalg.norm(d))))
        print()

    # ---- the rule, and what it would change for the CURRENT shipped value --
    print('=' * 84)
    print('⇒ 方案 B 规则:  gain(model) = |HD2 l_shoulder->l_hand| - |align(L_UpperArm->L_Hand)|')
    lh = mm(H['l_hand'] - H['l_shoulder'])
    for tag in A:
        if 'L_Hand' not in A[tag] or 'L_UpperArm' not in A[tag]:
            continue
        ls = mm(A[tag]['L_Hand'] - A[tag]['L_UpperArm'])
        g = (lh - ls) / 1000.0
        print('   %-10s 臂长 %8.2f mm -> gain = %.5f m   (现状写死 0.05000，'
              '差 %+.2f mm)' % (tag, ls, g, (g - 0.05) * 1000.0))
    return 0


if __name__ == '__main__':
    sys.exit(main())
