"""probe_mpose.py -- does the final posed rig equal HD2's native rest?

`D[b] = M_pose[b] . inv(M_rest[b])`, so `M_pose[b] = D[b] . rest_sym[b]` is the
skeleton the mesh ends up matched to.  For the mesh to look right it must equal
HD2's own rest (the A-pose the reference patch also uses).  Print the head and
rotation gap, per bone, for all four skeleton x axis_deltas combinations.
"""
import math
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
kw = dict(align='yaw', spine_map='mesh', fallback='distance',
          leg_tilt=5.7, leg_splay=4.0, verbose=False)

NAMES = ('hips', 'chest', 'neck', 'head', 'l_clavicle', 'l_shoulder', 'l_elbow',
         'l_hand', 'r_clavicle', 'r_shoulder', 'r_elbow', 'r_hand',
         'l_thigh', 'l_knee', 'l_foot')


def ang(A, B):
    R = np.asarray(A)[:3, :3] @ np.asarray(B)[:3, :3].T
    c = (np.trace(R) - 1.0) / 2.0
    return math.degrees(math.acos(max(-1.0, min(1.0, c))))


for mode in ('native', 'snap'):
    for ad in (True, False):
        r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP,
                       skeleton=mode, axis_deltas=ad, **kw)
        byn = {h['name']: h['index'] for h in r['hd2'] if h['name']}
        print('\n== %s / deltas %s' % (mode, 'ON' if ad else 'OFF'))
        worst_h = 0.0
        worst_r = 0.0
        for nm in NAMES:
            i = byn[nm]
            Mp = np.asarray(r['D'][i]) @ np.asarray(r['rest'][i])
            rh = np.asarray(r['rest_hd2'][i])
            dh = float(np.linalg.norm(Mp[:3, 3] - rh[:3, 3]))
            da = ang(Mp, rh)
            worst_h = max(worst_h, dh)
            worst_r = max(worst_r, da)
            print('   %-12s |head| %.4f m  |rot| %7.2f deg   M_pose %s  rest_hd2 %s'
                  % (nm, dh, da, np.round(Mp[:3, 3], 3), np.round(rh[:3, 3], 3)))
        print('   WORST |head| %.4f m   |rot| %.2f deg' % (worst_h, worst_r))
