# -*- coding: utf-8 -*-
"""Two numbers the round-65 decision needs, both free of the sleeve contamination.

1. JOINT FIT.  Both source rigs mapped into HD2 space by the pipeline's own
   alignment (same R, same s, same t recipe), then compared joint by joint down
   the arm and finger chains.  This is the user's "5 mm" criterion, measured on
   the thing that actually decides where the hand mesh lands.

   It also answers "how much would we have to stretch": for each segment it
   prints the ratio Lilium/Lunalice, and what a uniform scale about the shoulder
   plus a single upper-arm gain would have to be.

2. FINGER MESH QUALITY.  In the SHIPPED mesh, per HD2 finger bone, the vertex
   cloud splits cleanly into
     * the real finger skin  (close to the bone), and
     * the sleeve bleed      (1.2 m away -- see `_probe_hops.py`).
   Splitting them at a fixed radius lets the real fingers be compared on their
   own, instead of being swamped by the sleeve.

usage: _probe_armfit.py
"""
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import _probe_hops as PH
import _probe_out_hands as OH

CHAIN = ['L_Clavicle', 'L_UpperArm', 'L_Forearm', 'L_Hand',
         'L_Finger0', 'L_Finger01', 'L_Finger02',
         'L_Finger1', 'L_Finger11', 'L_Finger12',
         'L_Finger2', 'L_Finger21', 'L_Finger22',
         'L_Finger3', 'L_Finger31', 'L_Finger32',
         'L_Finger4', 'L_Finger41', 'L_Finger42']
CORE_MM = 80.0          # a finger bone's own skin lives well inside this


def joints(hd2, by_idx_name):
    hby = {h['name']: h for h in hd2 if h['name']}
    out = {}
    for tag, root, vpk in PH.RUNS:
        body = PH.ST.snap_body(PH.SPINE_MAP)
        al = PH.alignment(os.path.join(root, vpk), PH.MODEL, hby, body)
        js = {}
        for nm in CHAIN:
            b = al['short'].get(nm)
            if b is None:
                continue
            p = al['align_pts'](PH.ST.origin_np(
                PH.ST.m_to_np(b['world_m'])).reshape(1, 3))[0] + al['t']
            js[nm] = p
        out[tag] = js
    return out


def main():
    hd2, by_idx_name, by_idx, children, parents = PH.rig()
    J = joints(hd2, by_idx_name)
    a, b = J['Lunalice'], J['Lilium  ']

    print('=' * 84)
    print('1. 关节对齐差（两款源模型各自对齐到 HD2 空间后逐关节相减；mm）')
    print('   %-12s %10s %10s %10s %10s %8s' % ('joint', 'X', 'Y', 'Z', '|d|',
                                                'ratio*'))
    print('   * ratio = |该关节到肩| 的 Lilium/Lunalice 比')
    for nm in CHAIN:
        if nm not in a or nm not in b:
            print('   %-12s  (missing)' % nm)
            continue
        d = (b[nm] - a[nm]) * 1000.0
        la = float(np.linalg.norm(a[nm] - a['L_UpperArm'])) * 1000
        lb = float(np.linalg.norm(b[nm] - b['L_UpperArm'])) * 1000
        rr = (lb / la) if la > 1e-6 else float('nan')
        print('   %-12s %10.2f %10.2f %10.2f %10.2f %8.3f'
              % (nm, d[0], d[1], d[2], np.linalg.norm(d), rr))
    print()

    print('   段长对照（mm）:')
    print('   %-24s %10s %10s %10s %8s' % ('segment', 'Lunalice', 'Lilium',
                                            'diff', 'ratio'))
    segs = [('L_UpperArm', 'L_Forearm'), ('L_Forearm', 'L_Hand'),
            ('L_UpperArm', 'L_Hand')]
    for i in range(5):
        k = 'L_Finger%d' % i
        segs.append((k, k + '1'))
        segs.append((k + '1', k + '2'))
        segs.append((k, k + '2'))
    for p, q in segs:
        if p not in a or q not in a:
            continue
        la = float(np.linalg.norm(a[q] - a[p])) * 1000
        lb = float(np.linalg.norm(b[q] - b[p])) * 1000
        print('   %-24s %10.2f %10.2f %10.2f %8.3f'
              % ('%s->%s' % (p.replace('L_', ''), q.replace('L_', '')),
                 la, lb, lb - la, lb / la))
    print()

    print('   要达到 Lunalice 的手位，Lilium 需要多少补偿:')
    ra = a['L_Hand'] - a['L_UpperArm']
    rb = b['L_Hand'] - b['L_UpperArm']
    print('     UpperArm->Hand 向量  Lunalice=%s  Lilium=%s'
          % (np.round(ra * 1000, 2), np.round(rb * 1000, 2)))
    print('     统一缩放（绕肩）需要 ×%.4f  （现有 pipeline 用 ×%.4f 的全局 scale）'
          % (np.linalg.norm(ra) / np.linalg.norm(rb),
             float(np.ravel(__import__('json').load(
                 open(os.path.join(HERE, 'out/lilium/meta.json'),
                      encoding='utf-8'))['metrics']['scale'])[0])))
    print('     或沿肩->肘轴加长 %.2f mm（现有 arm_scratch_upper_gain = 50 mm）'
          % ((np.linalg.norm(ra) - np.linalg.norm(rb)) * 1000))
    print()

    # ---- 2. shipped finger mesh, sleeve separated out ---------------------
    print('=' * 84)
    print('2. 出货网格：逐 HD2 手指骨，把「真手指皮肤」与「袖子渗漏」分开')
    print('   core = 距骨头部 < %.0f mm；far = 其余（袖子）' % CORE_MM)
    A, B = OH.load('out/blender'), OH.load('out/lilium')
    pa, pb = OH.primary(A), OH.primary(B)
    names = [n for n in A['names'] if 'finger' in (n or '')]
    print('   %-22s %6s %6s %9s %9s   %9s %9s'
          % ('bone', 'nA', 'nB', 'coreA_mm', 'coreB_mm', 'farA', 'farB'))
    tot = dict(fa=0, fb=0, ca=[], cb=[])
    for nm in names:
        i = A['names'].index(nm)
        ia = A['bind'][pa == i]
        ib = B['bind'][pb == i]
        da = np.linalg.norm(ia - A['bhead'][i], axis=1) * 1000
        db = np.linalg.norm(ib - B['bhead'][i], axis=1) * 1000
        ca, cb = da < CORE_MM, db < CORE_MM
        tot['fa'] += int((~ca).sum())
        tot['fb'] += int((~cb).sum())
        if ca.any() and cb.any():
            cena = ia[ca].mean(0)
            cenb = ib[cb].mean(0)
            dd = float(np.linalg.norm(cenb - cena)) * 1000
            tot['ca'].append(dd)
            print('   %-22s %6d %6d %9.1f %9.1f   %9d %9d   core质心差 %6.1f mm'
                  % (nm, len(ia), len(ib), da[ca].mean(), db[cb].mean(),
                     int((~ca).sum()), int((~cb).sum()), dd))
        else:
            print('   %-22s %6d %6d %9s %9s   %9d %9d'
                  % (nm, len(ia), len(ib), '-', '-', int((~ca).sum()),
                     int((~cb).sum())))
    print('   --- 袖子渗漏顶点合计: Lunalice %d / Lilium %d' % (tot['fa'], tot['fb']))
    if tot['ca']:
        print('   --- 真手指 core 质心差: 中位 %.1f mm / 最大 %.1f mm'
              % (np.median(tot['ca']), max(tot['ca'])))
    print()

    # ---- 3. how much gain, and what is left over --------------------------
    # `arm_scratch_upper_gain` rigidly translates the whole elbow-and-below
    # subtree by `gain` along the SOURCE shoulder->elbow axis, so it is exactly
    # the knob for "Lilium's arm is short".  Both models' arm axes come out of
    # the alignment as (-1,0,0), so a gain difference is a pure X shift.
    #
    # NOTE: this is measured in the T-POSE frame (the frame the fix acts in).
    # Comparing the T-posed source joints with HD2's own joints would be
    # meaningless -- HD2's rest is an A-pose, its hands hang at the hips.
    print('=' * 84)
    print('3. 手臂加长量：拟合最优 gain，以及加完还剩多少残差（mm）')
    SET = [n for n in CHAIN if n.startswith('L_Finger') or n == 'L_Hand']
    u = a['L_Forearm'] - a['L_UpperArm']
    u = u / np.linalg.norm(u)

    def resid(g):
        r = {}
        for nm in SET:
            if nm not in a or nm not in b:
                continue
            r[nm] = float(np.linalg.norm((b[nm] + u * g) - a[nm])) * 1000.0
        return r

    # least squares over the whole hand+finger set
    num = den = 0.0
    for nm in SET:
        if nm not in a or nm not in b:
            continue
        d = a[nm] - b[nm]
        num += float(d @ u)
        den += 1.0
    g_best = num / den
    print('   最小二乘最优 gain = %.5f m  （现状 0.05，即需再加 %.2f mm）'
          % (g_best, (g_best - 0.05) * 1000))
    for g, lbl in ((0.05, '现状'), (0.08665, '手位对齐'), (g_best, '最小二乘')):
        r = resid(g)
        v = sorted(r.values())
        print('   gain=%.5f (%s): 中位 %5.1f / 90%% %5.1f / 最差 %5.1f mm'
              % (g, lbl, float(np.median(v)), v[int(0.9 * (len(v) - 1))], v[-1]))
    print()
    print('   逐关节残差（相对 Lunalice）:')
    print('   %-12s %10s %10s %10s' % ('joint', 'gain .05', 'gain .0867',
                                       'gain %.5f' % g_best))
    r0, r1, r2 = resid(0.05), resid(0.08665), resid(g_best)
    for nm in SET:
        if nm in r0:
            print('   %-12s %10.1f %10.1f %10.1f'
                  % (nm, r0[nm], r1[nm], r2[nm]))
    return 0


if __name__ == '__main__':
    sys.exit(main())
