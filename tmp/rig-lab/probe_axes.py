"""Settle the axis question and measure the two shifts we need.

Prints, for HD2's own rig, our retargeted mesh and the reference patch mesh:
  * the X of l_foot / r_foot / l_hand / r_hand  (rigs)
  * the two foot-patch centroids of each MESH, split by sign of X
  * min z / max z, and the foot-patch centroid (x, y)
"""
import json
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


def foot_patches(P, band=0.01):
    P = np.asarray(P)
    zmin = P[:, 2].min()
    bot = P[P[:, 2] < zmin + band]
    neg = bot[bot[:, 0] < 0]
    pos = bot[bot[:, 0] > 0]
    return (zmin, P[:, 2].max(),
            (neg.mean(0) if len(neg) else None),
            (pos.mean(0) if len(pos) else None))


def main():
    hd2 = PV.hd2_rig_heads(TOC, BH)
    print('-- HD2 native rig, X of the named joints --')
    for n in ('l_foot', 'r_foot', 'l_toe', 'r_toe', 'l_hand', 'r_hand',
              'l_shoulder', 'r_shoulder'):
        if n in hd2:
            print('   %-12s %s' % (n, np.round(hd2[n], 4)))

    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP,
                   align='yaw', spine_map='mesh', fallback='region',
                   leg_tilt=5.7, leg_splay=4.0, skeleton='native', verbose=False)
    ours = np.asarray(r['out'])
    print('\n-- OUR mesh --')
    zmin, zmax, neg, pos = foot_patches(ours)
    print('   z [%.4f, %.4f]  height %.4f' % (zmin, zmax, zmax - zmin))
    print('   foot patch x<0: %s' % (np.round(neg, 4) if neg is not None else None))
    print('   foot patch x>0: %s' % (np.round(pos, 4) if pos is not None else None))

    meta = json.load(open('out/patchdump/units.json', encoding='utf-8'))
    rp, base = [], 0
    for u in meta:
        fid = u['unitFileId'][2:].lower()
        if fid not in REF:
            continue
        p, t = PV.read_obj('out/patchdump/unit-%s.obj' % fid)
        if t:
            rp.extend(p)
    ref = np.asarray(rp)
    print('\n-- REFERENCE mesh --')
    zmin, zmax, neg, pos = foot_patches(ref)
    print('   z [%.4f, %.4f]  height %.4f' % (zmin, zmax, zmax - zmin))
    print('   foot patch x<0: %s' % (np.round(neg, 4) if neg is not None else None))
    print('   foot patch x>0: %s' % (np.round(pos, 4) if pos is not None else None))

    rh = {k: np.asarray(v) for k, v in PV.patch_rig_heads(
        meta, PV.read_bone_names(BH)).items()}
    print('\n-- REFERENCE patch rig, X of the named joints --')
    for n in ('l_foot', 'r_foot', 'l_hand', 'r_hand', 'l_shoulder', 'r_shoulder'):
        if n in rh:
            print('   %-12s %s' % (n, np.round(rh[n], 4)))


if __name__ == '__main__':
    main()
