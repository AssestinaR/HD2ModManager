"""Sweep the axis-mode arm MASK threshold (and the band) on the cost metrics."""
import os
import sys

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
                      arm_fit=1.0, arm_rot=AR, arm_rot_mode='axis', verbose=False)


print('%-6s %-6s %8s %8s %8s %8s' % ('mask', 'band', 'mean', 'p99', 'max', 'bad'))
for mask in ('0.5', '0.8', '0.95', '1.2'):
    for band in ('0.055', '0.090'):
        os.environ['HD2_ARM_MASK'] = mask
        os.environ['HD2_ARM_BAND'] = band
        m = run()['metrics']
        print('%-6s %-6s %8.4f %8.4f %8.2f %7.2f%%'
              % (mask, band, m['edge_mean'], m['edge_p99'], m['edge_max'],
                 100 * m['bad']))
