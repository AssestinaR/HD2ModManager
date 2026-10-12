"""The invariants that must never regress, plus the arm re-fit's own cost.

Run this after every change to the pipeline.
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H
import patch_view as PV

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
REF = ['af431352d9b95e83', 'cae35ee15168145f', '17da843a4908511e',
       '91eb7be84016ceab', '7336c6b662522f0e']


def main():
    arm_fit = float(sys.argv[1]) if len(sys.argv) > 1 else 1.0
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP,
                   align='yaw', spine_map='mesh', fallback='region',
                   leg_tilt=5.7, leg_splay=4.0, skeleton='native',
                   mesh_shift=(0.0, -0.100, -0.090),
                   upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                   head_scale=8.0 / 7.0, arm_scratch_upper_gain=0.05,
                   arm_elbow_open=6.5,
                   arm_fit=arm_fit, verbose=False)
    m = r['metrics']
    print('=== arm_fit = %.2f  (NOTE: arm_fit/arm_rot are DEAD under '
          'arm_scratch=True, which is the default and is not on the CLI) ==='
          % arm_fit)
    print('mesh down %.4f back %.4f | upper scale x%.3f pivot z %.3f blend %.2f '
          '| head scale x%.4f | arm gain %+.4f | elbow open %+.2f'
          % (0.090, 0.100, 1.12, 1.15, 0.45, 8.0 / 7.0, 0.05, 6.5))
    print('our zmin %+.4f height %.4f | edge mean %.4f p99 %.4f max %.2f bad>25%% %.2f%%'
          % (float(r['out'][:, 2].min()), m['height_after'], m['edge_mean'],
             m['edge_p99'], m['edge_max'], 100 * m['bad']))
    print('   (edge bad%% is NOT a deformation gauge any more -- upper_scale 1.12 '
          'and head_scale 1.143 are DELIBERATE, and they stretch edges by design)')

    ref = []
    import json
    meta = json.load(open('out/patchdump/units.json', encoding='utf-8'))
    for u in meta:
        if u['unitFileId'][2:].lower() in REF:
            p, _t = PV.read_obj('out/patchdump/unit-%s.obj' % u['unitFileId'][2:].lower())
            ref.extend(p)
    R = np.asarray(ref, float)
    print('ref zmin %+.4f height %.4f' % (R[:, 2].min(), R[:, 2].max() - R[:, 2].min()))

    d = np.linalg.norm(np.asarray(r['out_idle']) - np.asarray(r['out']), axis=1).max()
    print('idle vs bind  max %.2e m   (want 0)' % d)
    worst = 0.0
    for h in r['hd2']:
        i = h['index']
        A = r['rest_hd2'][i]
        S = r['rest_shipped'][i]
        worst = max(worst, float(np.abs(A @ np.linalg.inv(S) - np.eye(4)).max()))
    print('shipped rig vs HD2  max |A.inv(S) - I| %.2e   (want 0)' % worst)
    print('fallback hops %s' % m['hops'])


if __name__ == '__main__':
    main()
