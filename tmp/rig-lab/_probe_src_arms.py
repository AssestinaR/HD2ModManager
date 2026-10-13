# -*- coding: utf-8 -*-
"""Compare the two SOURCE rigs (L4D2) side by side, arm/hand/finger first.

The question this answers: the Lilium conversion's fingers came out wrong while
everything else looks fine.  Is that because Lilium's arm is a different length
than Lunalice's -- the model the pose constants were tuned against?

Both rigs are ValveBiped, so their own conventions cancel: a bone-to-bone
distance in one is directly comparable with the same distance in the other.  No
alignment, no rotation, no fitting -- just measure both and subtract.

usage: _probe_src_arms.py
"""
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import export_obj as E
import hd2_source as S
import hd2_style as ST

LUNALICE = (S.ADDONS, 'Lunalice_Zoey.vpk')
LILIUM = ('E:/tmp/lilium/', 'Lilium_Sancta_Zoey.vpk')
MODEL = 'survivor_teenangst'

ARMS = ['Clavicle', 'UpperArm', 'Forearm', 'Hand']
FINGER_NAMES = {0: 'thumb', 1: 'index', 2: 'middle', 3: 'ring', 4: 'pinky'}


def load(root, vpk):
    _p, _mdl, verts, tris, _src, short = ST.load_source(os.path.join(root, vpk),
                                                        MODEL)
    pos = {}
    for nm, b in short.items():
        pos[nm] = np.asarray(b['world_m'], np.float64).reshape(4, 4)[:3, 3]
    v = np.array([x['pos'] for x in verts], np.float64)
    return dict(pos=pos, verts=v, tris=tris, short=short)


def seg(a, b, side='L'):
    return float(np.linalg.norm(a['pos']['%s_%s' % (side, b[1])]
                                - a['pos']['%s_%s' % (side, b[0])]))


def main():
    A = load(*LUNALICE)
    B = load(*LILIUM)
    print('=== 源模型几何 ===')
    for tag, d in (('Lunalice', A), ('Lilium  ', B)):
        v = d['verts']
        print('  %s  verts=%-7d tris=%-7d  height(Z)=%.4f m  X=[%.3f,%.3f] '
              'Y=[%.3f,%.3f]' % (tag, len(v), len(d['tris']),
                                 v[:, 2].max() - v[:, 2].min(),
                                 v[:, 0].min(), v[:, 0].max(),
                                 v[:, 1].min(), v[:, 1].max()))
    print()

    # every bone both rigs share, in their own world space
    common = sorted(set(A['pos']) & set(B['pos']))
    print('=== 骨骼世界坐标差（不拟合、不平移；单位 mm）===')
    print('  %-14s %-24s %-24s %8s' % ('bone', 'Lunalice (x,y,z)', 'Lilium (x,y,z)', 'delta'))
    rows = []
    for nm in common:
        pa, pb = A['pos'][nm], B['pos'][nm]
        rows.append((float(np.linalg.norm(pb - pa)) * 1000.0, nm, pa, pb))
    rows.sort(reverse=True)
    for d, nm, pa, pb in rows:
        print('  %-14s (%7.4f,%7.4f,%7.4f)  (%7.4f,%7.4f,%7.4f)  %7.1f'
              % (nm, pa[0], pa[1], pa[2], pb[0], pb[1], pb[2], d))
    print('  --- 最大 %s %.1f mm / 中位 %.1f mm ---'
          % (rows[0][1], rows[0][0], np.median([r[0] for r in rows])))
    print()

    print('=== 手臂链段长（左右两侧，单位 mm）===')
    chain = [('Clavicle', 'UpperArm'), ('UpperArm', 'Forearm'),
             ('Forearm', 'Hand')]
    print('  %-26s %10s %10s %9s %8s' % ('segment', 'Lunalice', 'Lilium',
                                         'diff', 'ratio'))
    for side in ('L', 'R'):
        for a, b in chain:
            la = float(np.linalg.norm(A['pos']['%s_%s' % (side, b)]
                                      - A['pos']['%s_%s' % (side, a)])) * 1000
            lb = float(np.linalg.norm(B['pos']['%s_%s' % (side, b)]
                                      - B['pos']['%s_%s' % (side, a)])) * 1000
            print('  %-26s %10.2f %10.2f %9.2f %8.3f'
                  % ('%s: %s->%s' % (side, a, b), la, lb, lb - la, lb / la))
    for side in ('L', 'R'):
        # shoulder -> hand, the whole reach that decides where the hand lands
        la = float(np.linalg.norm(A['pos']['%s_Hand' % side]
                                  - A['pos']['%s_UpperArm' % side])) * 1000
        lb = float(np.linalg.norm(B['pos']['%s_Hand' % side]
                                  - B['pos']['%s_UpperArm' % side])) * 1000
        print('  %-26s %10.2f %10.2f %9.2f %8.3f'
              % ('%s: UpperArm->Hand (reach)' % side, la, lb, lb - la, lb / la))
    print()

    print('=== 手指：每指总长（Finger0 -> Finger02，单位 mm）===')
    print('  %-22s %10s %10s %9s %8s' % ('finger', 'Lunalice', 'Lilium',
                                         'diff', 'ratio'))
    for side in ('L', 'R'):
        for i, nm in FINGER_NAMES.items():
            k0, k2 = '%s_Finger%d' % (side, i), '%s_Finger%d2' % (side, i)
            if k0 not in A['pos'] or k2 not in A['pos']:
                continue
            la = float(np.linalg.norm(A['pos'][k2] - A['pos'][k0])) * 1000
            lb = float(np.linalg.norm(B['pos'][k2] - B['pos'][k0])) * 1000
            print('  %-22s %10.2f %10.2f %9.2f %8.3f'
                  % ('%s %s' % (side, nm), la, lb, lb - la, lb / la))
    print()

    print('=== 手指：逐节段长（左拇指 / 左食指 / 左中指，单位 mm）===')
    print('  %-26s %10s %10s %9s %8s' % ('segment', 'Lunalice', 'Lilium',
                                         'diff', 'ratio'))
    for i in (0, 1, 2):
        for j in range(3):
            a = '%s_Finger%d' % ('L', i) + ('' if j == 0 else str(j))
            b = '%s_Finger%d' % ('L', i) + str(j + 1)
            if a not in A['pos'] or b not in A['pos']:
                continue
            la = float(np.linalg.norm(A['pos'][b] - A['pos'][a])) * 1000
            lb = float(np.linalg.norm(B['pos'][b] - B['pos'][a])) * 1000
            print('  %-26s %10.2f %10.2f %9.2f %8.3f'
                  % ('%s->%s' % (a, b), la, lb, lb - la, lb / la))
    return 0


if __name__ == '__main__':
    sys.exit(main())
