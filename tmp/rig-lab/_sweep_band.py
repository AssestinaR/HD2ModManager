"""Sweep the axis-mode transition band width and report the cost metrics."""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
AR = (10.46, -18.55, 29.63)


def run():
    return H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                      spine_map='mesh', fallback='region', leg_tilt=5.7,
                      leg_splay=4.0, mesh_shift=(0.0, -0.100, -0.060),
                      upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                      arm_fit=1.0, arm_rot=AR, arm_rot_mode='axis',
                      verbose=False)


print('%-8s %8s %8s %8s %8s' % ('band', 'mean', 'p99', 'max', 'bad>25%'))
for b in ('0.040', '0.055', '0.070', '0.090', '0.120'):
    os.environ['HD2_ARM_BAND'] = b
    r = run()
    m = r['metrics']
    print('%-8s %8.4f %8.4f %8.2f %7.2f%%'
          % (b, m['edge_mean'], m['edge_p99'], m['edge_max'], 100 * m['bad']))
