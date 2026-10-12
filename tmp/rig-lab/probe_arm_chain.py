"""The arm chain, side by side: our shipped rest vs HD2's own.

The hand work cannot be judged before this is settled, because the hand is the
END of a chain: whatever the clavicle/shoulder/elbow are off by, the hand
inherits.  HD2's own reference patch keeps every one of these bones exactly, so
the numbers below are exactly what our model does differently.
"""
import math
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

CHAIN = ['l_clavicle', 'l_shoulder', 'l_shoulder_twist', 'l_shoulderarmour',
         'l_elbow', 'l_hand', 'l_hand_twist',
         'l_index_finger1', 'l_middle_finger1', 'l_pinky_finger1',
         'l_thumb_finger1', 'l_index_finger3']


def main():
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                   spine_map='mesh', fallback='region', leg_tilt=5.7,
                   leg_splay=4.0, verbose=False)
    hd2 = PV.hd2_rig_heads(TOC, BH)
    ours = {h['name']: np.asarray((r['D'][h['index']] @ r['rest'][h['index']])[:3, 3])
            for h in r['hd2'] if h['name'] and h['index'] in r['rest']}

    print('%-20s | %-22s | %-22s | %7s' % ('bone', 'OURS  (x,y,z)',
                                           'HD2   (x,y,z)', 'gap'))
    for nm in CHAIN:
        if nm not in ours or nm not in hd2:
            continue
        a, b = ours[nm], np.asarray(hd2[nm])
        print('%-20s | %+7.4f %+7.4f %+7.4f | %+7.4f %+7.4f %+7.4f | %7.4f'
              % (nm, a[0], a[1], a[2], b[0], b[1], b[2], np.linalg.norm(a - b)))

    print('\nsegment lengths and the front/side lean (deg, +outward / +forward):')
    for a, b in zip(CHAIN, CHAIN[1:]):
        if a not in ours or b not in ours or a not in hd2 or b not in hd2:
            continue
        va, vb = ours[b] - ours[a], np.asarray(hd2[b]) - np.asarray(hd2[a])
        la, lb = np.linalg.norm(va), np.linalg.norm(vb)
        ang = math.degrees(math.acos(np.clip(float(va @ vb) / max(la * lb, 1e-9), -1, 1)))
        print('  %-20s len %6.4f / %6.4f  dir %5.1f  front %+6.1f / %+6.1f'
              '  side %+6.1f / %+6.1f'
              % ('%s->%s' % (a, b), la, lb, ang,
                 math.degrees(math.atan2(-va[0], -va[2])),
                 math.degrees(math.atan2(-vb[0], -vb[2])),
                 math.degrees(math.atan2(va[1], -va[2])),
                 math.degrees(math.atan2(vb[1], -vb[2]))))

    # how much of the arm chain's gap is a single global shift?
    ks = [k for k in CHAIN if k in ours and k in hd2]
    D = np.array([ours[k] - np.asarray(hd2[k]) for k in ks])
    print('\nmean displacement over the chain: (%+.4f, %+.4f, %+.4f)  |mean| %.4f'
          % (D[:, 0].mean(), D[:, 1].mean(), D[:, 2].mean(),
             np.linalg.norm(D.mean(0))))
    print('residual after removing that mean: max %.4f m'
          % np.linalg.norm(D - D.mean(0), axis=1).max())


if __name__ == '__main__':
    main()
