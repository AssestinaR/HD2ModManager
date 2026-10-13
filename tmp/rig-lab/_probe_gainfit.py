# -*- coding: utf-8 -*-
"""方案 B 定标：从**出货网格**反推手臂该补多少位移。

为什么不信源侧的关节对比：
`rest_hd2` 是 A-pose、源模型是 T-pose，直接相减会得到 400 mm 级的假误差
（第 65 轮踩过）。而长度虽然可比，却回答不了"手到底该落在哪"。

出货网格没有这个问题：两款模型的 `b_head` 逐字节相同（骨架不动），
最终网格都绑在同一套骨头上，所以**逐骨的顶点云质心差就是用户肉眼看到的那个差**。

本探针给出两件事：

1. 质心差 Δ 沿"手臂轴"的**平行分量** —— 这一份 `arm_scratch_upper_gain` 能吃掉。
   （增益在 T-pose 里沿臂轴刚性平移肘以下，T->A 那一步是绕肩刚体旋转，
    所以出货网格里手就沿**最终臂轴**平移，位移量恰好 = 增益变化量。）
2. 垂直于臂轴的分量 —— 增益**吃不掉**，它来自两款模型各自对齐后手臂落在
   不同的 Y/Z 上（对齐钉的是头顶高度）。这一份要另想办法。

⇒ 结论就是"单靠拉长手臂最多能到多少、还差多少"。

usage: _probe_gainfit.py
"""
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import _probe_out_hands as OH

CORE_MM = 80.0
HAND = ('_hand', 'finger')


def main(argv=None):
    ref = argv[0] if argv and len(argv) > 0 else 'out/blender'
    tgt = argv[1] if argv and len(argv) > 1 else 'out/lilium'
    A, B = OH.load(ref), OH.load(tgt)
    print('参考 = %s   被测 = %s' % (ref, tgt))
    print('骨架不动核对: b_head identical=%s  max|delta|=%.3e'
          % (np.array_equal(A['bhead'], B['bhead']),
             float(np.abs(A['bhead'] - B['bhead']).max())))
    print()

    names = [nm for nm in A['names']
             if nm and any(k in nm for k in HAND) and nm.startswith('l_')]
    # 手臂轴：从出货骨架自己取，A-pose 下的最终方向（= 增益移动手的方向）
    sh = A['names'].index('l_shoulder')
    ha = A['names'].index('l_hand')
    d = A['bhead'][ha] - A['bhead'][sh]
    dhat = d / np.linalg.norm(d)
    print('出货骨架 l_shoulder->l_hand = (%.4f, %.4f, %.4f)  |%.1f mm|'
          % (d[0], d[1], d[2], float(np.linalg.norm(d)) * 1000))
    print('臂轴单位向量 = (%.4f, %.4f, %.4f)' % tuple(dhat))
    print()

    pa, pb = OH.primary(A), OH.primary(B)
    print('%-22s %5s %5s %9s %9s %9s %9s %9s'
          % ('bone', 'nA', 'nB', 'd_par_mm', 'd_perp_mm', '|d|_mm',
             'cenA->bone', 'cenB->bone'))
    par, perp, tot = [], [], []
    deltas = []          # (bone, delta 3-vector)   delta = P_B - P_A
    for nm in names:
        i = A['names'].index(nm)
        ma, mb = pa == i, pb == i
        if not ma.any() or not mb.any():
            continue
        ia = A['bind'][ma]
        ib = B['bind'][mb]
        da = np.linalg.norm(ia - A['bhead'][i], axis=1) * 1000
        db = np.linalg.norm(ib - B['bhead'][i], axis=1) * 1000
        ia, ib = ia[da < CORE_MM], ib[db < CORE_MM]
        if not len(ia) or not len(ib):
            continue
        delta = ib.mean(0) - ia.mean(0)          # Lilium - Lunalice
        p = float(delta @ dhat) * 1000
        q = float(np.linalg.norm(delta - dhat * (delta @ dhat))) * 1000
        par.append(p)
        perp.append(q)
        tot.append(float(np.linalg.norm(delta)) * 1000)
        deltas.append((nm, delta))
        print('%-22s %5d %5d %9.1f %9.1f %9.1f %9.1f %9.1f'
              % (nm, len(ia), len(ib), p, q, np.linalg.norm(delta) * 1000,
                 float(np.linalg.norm(ia.mean(0) - A['bhead'][i])) * 1000,
                 float(np.linalg.norm(ib.mean(0) - B['bhead'][i])) * 1000))
    print()
    # fingers only for the fit: the `_hand` cloud is 853 vs 1371 vertices, i.e.
    # its boundary moved with the weights, so it is the noisy one.
    fing = [d for nm, d in deltas if 'finger' in nm]
    md = np.mean(fing, axis=0)
    print('手指(不含手掌)平均位移向量 delta = P_B - P_A = (%+.4f, %+.4f, %+.4f) m'
          % tuple(md))
    print('   |delta| = %.2f mm   沿臂轴 %+.2f mm   垂直 %.2f mm'
          % (float(np.linalg.norm(md)) * 1000, float(md @ dhat) * 1000,
             float(np.linalg.norm(md - dhat * (md @ dhat))) * 1000))
    print('   ⇒ 要施加给被测网格的位移 = -delta = (%+.4f, %+.4f, %+.4f) m'
          % tuple(-md))
    print()
    print('--- 平行分量(Lilium 相对 Lunalice 差多少)  中位 %+.1f mm / 均值 %+.1f mm'
          % (float(np.median(par)), float(np.mean(par))))
    print('--- 垂直分量                              中位 %5.1f mm / 均值 %5.1f mm'
          % (float(np.median(perp)), float(np.mean(perp))))
    print('--- 总位移                                中位 %5.1f mm / 均值 %5.1f mm'
          % (float(np.median(tot)), float(np.mean(tot))))
    print()
    # 增益的符号：+gain 把手沿 +dhat 推出去。Lilium 比 Lunalice 靠内(p<0) => 要加增益
    dg = -float(np.mean(par)) / 1000.0
    print('⇒ 让平行分量归零需要的增益变化 dg = %+.5f m  => gain %.5f -> %.5f'
          % (dg, 0.05, 0.05 + dg))
    print('   平行分量归零后，剩下的就是垂直分量: 均值 %.1f mm / 中位 %.1f mm'
          % (float(np.mean(perp)), float(np.median(perp))))
    print('   ⇒ 单靠拉长大臂的最好结果 ≈ %.1f mm，用户要的 5 mm 达不到。'
          % float(np.mean(perp)))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
