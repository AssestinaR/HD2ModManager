"""How much to shift the mesh, measured against the reference patch.

Z  : our lowest vertex -> the reference's lowest vertex.
Y  : our sole-patch centroid -> the reference's sole-patch centroid.

The sole patch is "everything within `band` of the mesh's own minimum z", so the
result must be stable in `band`; we print three values to check that.
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


def sole(P, band):
    P = np.asarray(P)
    zmin = P[:, 2].min()
    return zmin, P[P[:, 2] < zmin + band].mean(0)


def main():
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP,
                   align='yaw', spine_map='mesh', fallback='region',
                   leg_tilt=5.7, leg_splay=4.0, skeleton='native', verbose=False)
    ours = np.asarray(r['out'])
    meta = json.load(open('out/patchdump/units.json', encoding='utf-8'))
    rp = []
    for u in meta:
        fid = u['unitFileId'][2:].lower()
        if fid in REF:
            p, t = PV.read_obj('out/patchdump/unit-%s.obj' % fid)
            if t:
                rp.extend(p)
    ref = np.asarray(rp)

    print('%-6s %10s %10s | %-28s %-28s %s'
          % ('band', 'our zmin', 'ref zmin', 'our sole (x,y,z)',
             'ref sole (x,y,z)', 'shift (dx,dy,dz)'))
    for band in (0.010, 0.020, 0.030, 0.050):
        oz, oc = sole(ours, band)
        rz, rc = sole(ref, band)
        d = rz - oz, rc[1] - oc[1], rz - oz
        print('%-6.3f %10.4f %10.4f | %-28s %-28s (%+.4f,%+.4f,%+.4f)'
              % (band, oz, rz, np.round(oc, 4), np.round(rc, 4), 0.0, d[1], d[0]))

    # landmarks relative to HD2's own ankle, so we can see WHICH anchor moves
    hd2 = PV.hd2_rig_heads(TOC, BH)
    ank = hd2['l_foot']
    print('\nHD2 l_foot (y=%.4f)   our sole y - ankle y = %+.4f   ref sole y - ankle y = %+.4f'
          % (ank[1], sole(ours, 0.02)[1][1] - ank[1], sole(ref, 0.02)[1][1] - ank[1]))
    print('our new_head l_foot  = %s' % np.round(r['new_head']['l_foot'], 4))
    print('our new_head hips    = %s' % np.round(r['new_head']['hips'], 4))
    print('our new_head l_hand  = %s' % np.round(r['new_head']['l_hand'], 4))
    print('our mesh bbox z [%.4f, %.4f]  y [%.4f, %.4f]  x [%.4f, %.4f]'
          % (ours[:, 2].min(), ours[:, 2].max(), ours[:, 1].min(), ours[:, 1].max(),
             ours[:, 0].min(), ours[:, 0].max()))
    print('ref mesh bbox z [%.4f, %.4f]  y [%.4f, %.4f]  x [%.4f, %.4f]'
          % (ref[:, 2].min(), ref[:, 2].max(), ref[:, 1].min(), ref[:, 1].max(),
             ref[:, 0].min(), ref[:, 0].max()))


if __name__ == '__main__':
    main()
