"""probe_native_out.py -- is `out` a T-pose under skeleton='native'?

Prints, for both skeleton modes: the src1/out bounding boxes, how far the mesh
moved, and the per-bone rotation of the deformer `D`.  If native's `D` is the
identity on the arms, that is the bug the user is looking at.
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
kw = dict(align='yaw', spine_map='mesh', fallback='region',
          leg_tilt=5.7, leg_splay=4.0, verbose=False)


def ang(M):
    R = np.asarray(M)[:3, :3]
    c = (np.trace(R) - 1.0) / 2.0
    return float(np.degrees(np.arccos(max(-1.0, min(1.0, c)))))


prof = json.load(open(PP, encoding='utf-8'))
print('profile keys:', sorted(prof.keys()))
bi = prof['initial_basis_rotations_from_baked_tpose_wxyz']
bt = prof['tpose_basis_rotations_wxyz']
print('basis_i n=%d  basis_t n=%d' % (len(bi), len(bt)))
print('basis_i non-trivial (rot > 1 deg):',
      sum(1 for q in bi.values() if ang(H.q_wxyz_to_m(q)) > 1.0))
print('basis_i names:', sorted(bi))
print('basis_t non-trivial (rot > 1 deg):',
      sum(1 for q in bt.values() if ang(H.q_wxyz_to_m(q)) > 1.0))
print('basis_t names:', sorted(bt))
print('body_bones n=%d' % len(prof['body_bones']))

for mode in ('native', 'snap'):
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, skeleton=mode, **kw)
    out = np.asarray(r['out'])
    src1 = np.asarray(r['src1'])
    d = np.linalg.norm(out - src1, axis=1)
    print('\n== %s' % mode)
    print('   src1 bbox  x[%+.3f,%+.3f] y[%+.3f,%+.3f] z[%+.3f,%+.3f]'
          % (src1[:, 0].min(), src1[:, 0].max(), src1[:, 1].min(),
             src1[:, 1].max(), src1[:, 2].min(), src1[:, 2].max()))
    print('   out  bbox  x[%+.3f,%+.3f] y[%+.3f,%+.3f] z[%+.3f,%+.3f]'
          % (out[:, 0].min(), out[:, 0].max(), out[:, 1].min(),
             out[:, 1].max(), out[:, 2].min(), out[:, 2].max()))
    print('   |out-src1| mean %.4f  p99 %.4f  max %.4f'
          % (d.mean(), np.percentile(d, 99), d.max()))
    hd2 = r['hd2']
    byname = {h['name']: h['index'] for h in hd2 if h['name']}
    for nm in ('hips', 'spine1', 'spine2', 'chest', 'neck', 'head',
               'l_clavicle', 'l_shoulder', 'l_elbow', 'l_hand',
               'l_thigh', 'l_knee', 'l_foot'):
        i = byname.get(nm)
        if i is None or i not in r['D']:
            continue
        print('   D[%-13s] rot %8.2f deg  t %s'
              % (nm, ang(r['D'][i]), np.round(r['D'][i][:3, 3], 3)))
    for nm in ('l_shoulder', 'l_elbow', 'l_hand'):
        i = byname.get(nm)
        if i is None:
            continue
        print('   rest_shipped[%-11s] head %s   rest_hd2 head %s'
              % (nm, np.round(r['rest_shipped'][i][:3, 3], 3),
                 np.round(r['rest_hd2'][i][:3, 3], 3)))
