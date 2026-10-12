"""Both skeleton modes, side by side, with the idle-frame guarantee checked."""
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


def rot_ang(A, B):
    R = A[:3, :3].T @ B[:3, :3]
    return float(np.degrees(np.arccos(np.clip((np.trace(R) - 1) / 2.0, -1, 1))))


def main():
    for mode in ('snap', 'native'):
        r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                       spine_map='mesh', fallback='distance', leg_tilt=5.7,
                       leg_splay=4.0, skeleton=mode, verbose=False)
        S, rh = r['rest_shipped'], r['rest_hd2']
        dr = max(rot_ang(S[i], rh[i]) for i in S)
        dh = max(float(np.linalg.norm(S[i][:3, 3] - rh[i][:3, 3])) for i in S)
        d = np.linalg.norm(np.asarray(r['out_idle']) - np.asarray(r['out']), axis=1)
        m = r['metrics']
        print('%-7s rig vs HD2 native: rot %8.2f deg  head %.3e m   |   idle vs '
              'bind: mean %.6f max %.6f m   |   edge_mean %.4f  bad %.2f%%  height %.3f'
              % (mode, dr, dh, d.mean(), d.max(), m['edge_mean'], 100 * m['bad'],
                 m['height_after']))
        np.save('out/mode-%s-out.npy' % mode, r['out'])


if __name__ == '__main__':
    main()
