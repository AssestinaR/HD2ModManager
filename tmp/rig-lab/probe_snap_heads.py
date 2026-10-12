"""probe_snap_heads.py -- compare the snapped HD2 heads between the two modes.

`D`'s rotations came out identical, so the 2 m blow-up must come from the joint
POSITIONS that `rest_sym` is built on.  Print `new_head` / `rest_sym` heads for
the torso and both arms, plus HD2's own rest, for native vs snap.
"""
import json
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

NAMES = ('hips', 'chest', 'neck', 'head',
         'l_clavicle', 'l_shoulder', 'l_elbow', 'l_hand',
         'r_clavicle', 'r_shoulder', 'r_elbow', 'r_hand')

res = {}
for mode in ('native', 'snap'):
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, skeleton=mode, **kw)
    res[mode] = r
    byname = {h['name']: h['index'] for h in r['hd2'] if h['name']}
    print('\n== %s' % mode)
    for nm in NAMES:
        i = byname[nm]
        nh = r['new_head'].get(nm)
        rs = r['rest'][i][:3, 3]
        rh = r['rest_hd2'][i][:3, 3]
        print('  %-12s new_head %s  rest_sym %s  rest_hd2 %s'
              % (nm,
                 np.round(nh, 3) if nh is not None else 'None',
                 np.round(rs, 3), np.round(rh, 3)))

print('\n-- rest_sym head: native vs snap (should differ only in position) --')
a = res['native']
b = res['snap']
byn = {h['name']: h['index'] for h in a['hd2'] if h['name']}
for nm in NAMES:
    i = byn[nm]
    pa = a['rest'][i][:3, 3]
    pb = b['rest'][i][:3, 3]
    print('  %-12s native %s  snap %s   |d| %.4f'
          % (nm, np.round(pa, 3), np.round(pb, 3), float(np.linalg.norm(pa - pb))))
