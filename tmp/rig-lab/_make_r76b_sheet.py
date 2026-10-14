# -*- coding: utf-8 -*-
"""Round 76b: render the RIGHT arm before/after and compose the evidence sheet.

The vanilla reference unit (0x4a213e0ae8acf74b) is the LEFT arm skin only, so
there is no vanilla right arm to show; the sheet compares our own before/after.
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

BEFORE = 'E:/tmp/r76b-ship-rollback/9ba626afa44a3aa3.patch_0'   # r76 left-only
AFTER = 'E:/tmp/r76b/9ba626afa44a3aa3.patch_0'                  # r76b both arms
TMP = 'E:/tmp/r76b-png'
PRE = 'E:/mod/MourningVeil-RE2310/preview'


def render(out, src, extra):
    import _render_arm_vplane as V
    sys.argv = ['render', out, src, '--side', 'right'] + extra
    V.main()


def compose(out, title, cells, cols, w):
    import _compose_arm_strip as C
    args = [out, '--cols=%d' % cols, '--width=%d' % w, '--title=%s' % title]
    for lab, name in cells:
        args.append('%s=%s' % (lab, os.path.join(TMP, name + '.png')))
    sys.argv = ['compose'] + args
    C.main()


if __name__ == '__main__':
    os.makedirs(TMP, exist_ok=True)
    jobs = [
        ('RA', []),                                   # whole arm in the bend plane
        ('RE', ['--center-on', 'elbow', '--half', '0.155']),
        ('RW', ['--center-on', 'wrist', '--half', '0.125']),
    ]
    for tag, extra in jobs:
        render(os.path.join(TMP, tag + '-before.png'), BEFORE, extra + ['--label', 'r76b 改前'])
        render(os.path.join(TMP, tag + '-after.png'), AFTER, extra + ['--label', 'r76b 改后'])

    compose(os.path.join(PRE, 'forearm_pivot_rotate_right_before_after.png'),
            '右小臂：同样"钉死肘部、绕它旋转"（第 76b 轮）—— 肘部 41.8→42.0 mm 一点没动，'
            '腕带 26.6/26.7/22.5/23.6 → 1.6/5.7/5.9/6.9 mm',
            [('整臂：改前（骨线贴小臂上缘）', 'RA-before'), ('整臂：改后（骨线穿过小臂）', 'RA-after'),
             ('肘部放大：改前', 'RE-before'), ('肘部放大：改后（没动）', 'RE-after'),
             ('腕部放大：改前', 'RW-before'), ('腕部放大：改后（骨线穿腕中）', 'RW-after')],
            cols=2, w=900)
