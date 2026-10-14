# -*- coding: utf-8 -*-
"""Compose the round-76 evidence sheet into the delivery preview folder."""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

SRC = 'E:/tmp/r76-png'
PRE = 'E:/mod/MourningVeil-RE2310/preview'

SHEETS = [
    dict(out='forearm_pivot_rotate_before_after.png', cols=4, w=760,
         title='左小臂：绕肘关节刚体旋转（第76轮）—— 肘部钉死不动（0 位移），'
               '整段小臂绕它刚性转 5.56°，把腕部对准骨轴',
         cells=[
             ('原版 arm skin', 'A-van'), ('r74 校正前', 'A-r74'),
             ('r75b 平移（肘外移 40mm）', 'A-r75b'), ('r76 绕肘旋转（新）', 'A-r76rot'),
             ('肘部放大：原版', 'E-van'), ('r74 校正前', 'E-r74'),
             ('r75b 平移：小臂根部有折痕', 'E-r75b'), ('r76 绕肘旋转：下缘连续', 'E-r76rot'),
             ('腕部放大：原版', 'W-van'), ('r74 校正前：骨线贴小臂上缘', 'W-r74'),
             ('r75b 平移', 'W-r75b'), ('r76 绕肘旋转：骨线穿过腕中', 'W-r76rot'),
         ]),
    dict(out='forearm_pivot_rotate_elbow.png', cols=2, w=900,
         title='肘部：r75b 平移（左，小臂根部有折痕/台阶） vs r76 绕肘旋转（右，下缘连续光滑）',
         cells=[('r75b 平移（旧）', 'E-r75b'), ('r76 绕肘旋转（新）', 'E-r76rot')]),
    dict(out='forearm_pivot_rotate_wrist.png', cols=2, w=900,
         title='腕部：r74 校正前（左，骨线贴小臂上缘、偏移 26 mm） vs r76（右，骨线穿过腕中、偏移 1-7 mm）',
         cells=[('r74 校正前', 'W-r74'), ('r76 绕肘旋转', 'W-r76rot')]),
    dict(out='superseded_forearm_rigid_align_r75b.png', cols=2, w=900,
         title='已作废的第75b轮（刚性平移）：整条小臂压到骨轴上，代价是肘部被拽走 40 mm',
         cells=[('r75b 平移（作废）', 'A-r75b'), ('r75b 平移：肘部放大', 'E-r75b')]),
]


def run(d):
    args = [os.path.join(PRE, d['out']), '--cols=%d' % d['cols'],
            '--width=%d' % d['w'], '--title=%s' % d['title']]
    for lab, name in d['cells']:
        args.append('%s=%s' % (lab, os.path.join(SRC, name + '.png')))
    import _compose_arm_strip as C
    sys.argv = ['compose'] + args
    C.main()


if __name__ == '__main__':
    for d in SHEETS:
        run(d)

