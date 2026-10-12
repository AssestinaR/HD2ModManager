"""Five survivors, LEGACY vs ROUND TRIP, with the full stance config.

Checks that the rig fix (a) does not need per-model tuning and (b) does not cost
anything on the mesh metric while it fixes the shipped bone orientations.
"""
import os
import sys
import traceback

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
ADD = 'D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons'
MODELS = [('Lunalice_Zoey.vpk', 'survivor_teenangst', 'zoey'),
          ('Lunalice_Rochelle.vpk', 'survivor_producer', 'rochelle'),
          ('Elise_Coach.vpk', 'survivor_coach', 'coach'),
          ('Lilium_Sancta_Nick.vpk', 'survivor_gambler', 'nick'),
          ('Serenade_Ellis.vpk', 'survivor_mechanic', 'ellis')]


def rot_ang(A, B):
    R = A[:3, :3].T @ B[:3, :3]
    return float(np.degrees(np.arccos(np.clip((np.trace(R) - 1) / 2.0, -1, 1))))


def main():
    print('%-9s %-9s %10s %9s %7s %8s %8s %9s' %
          ('model', 'skeleton', 'edge_mean', 'edge_max', 'bad%', 'height',
           'rig head', 'idle max'))
    for vpk, model, tag in MODELS:
        for mode in ('snap', 'native'):
            try:
                r = H.retarget(os.path.join(ADD, vpk), model, TOC, BH, PP,
                               align='yaw', spine_map='mesh', fallback='region',
                               leg_tilt=5.7, leg_splay=4.0, skeleton=mode,
                               verbose=False)
            except Exception as exc:                                  # noqa: BLE001
                print('%-9s %-9s FAILED %s' % (tag, mode, exc))
                traceback.print_exc()
                continue
            m = r['metrics']
            S, rh = r['rest_shipped'], r['rest_hd2']
            hd = max(float(np.linalg.norm(S[i][:3, 3] - rh[i][:3, 3])) for i in S)
            d = np.linalg.norm(np.asarray(r['out_idle']) - np.asarray(r['out']),
                               axis=1)
            print('%-9s %-9s %10.4f %9.2f %6.2f%% %8.3f %8.1e %9.4f'
                  % (tag, mode, m['edge_mean'], m['edge_max'], 100 * m['bad'],
                     m['height_after'], hd, d.max()))


if __name__ == '__main__':
    main()
