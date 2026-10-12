"""Round 45: print the per-bone arm residual `shipped head - working head`.

The arm re-fit displaces each vertex by the weight-blended residual.  If the
residual VARIES a lot along the chain the arm gets sheared/rotated, which is what
the render suggests -- this prints the numbers.
"""
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


def main():
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                   spine_map='mesh', fallback='region', leg_tilt=5.7,
                   leg_splay=4.0, mesh_shift=(0.0, -0.100, -0.060),
                   upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                   arm_fit=0.0, verbose=False)
    for side in ('l', 'r'):
        print('\n=== %s arm residual (shipped - working) ===' % side.upper())
        print('%-22s %-26s %-26s %-26s %8s' % ('bone', 'working (x,y,z)',
                                               'shipped (x,y,z)', 'residual', '|d|'))
        for h in r['hd2']:
            nm = h['name']
            if not H.is_arm_bone(nm):
                continue
            i = h['index']
            w = r['D'][i] @ r['rest'][i]
            s = r['rest_shipped'][i]
            a = np.asarray([w[0, 3], w[1, 3], w[2, 3]])
            b = np.asarray([s[0, 3], s[1, 3], s[2, 3]])
            d = b - a
            print('%-22s %+8.4f %+8.4f %+8.4f  %+8.4f %+8.4f %+8.4f  %+8.4f %+8.4f '
                  '%+8.4f  %7.4f'
                  % (nm, a[0], a[1], a[2], b[0], b[1], b[2],
                     d[0], d[1], d[2], float(np.linalg.norm(d))))


if __name__ == '__main__':
    main()
