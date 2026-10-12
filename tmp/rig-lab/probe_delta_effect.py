"""Is the right-arm blow-up caused by the symmetric axis deltas?

Builds a profile with `symmetric_axis_deltas_wxyz` emptied (a pure re-parameter-
isation, so the LEFT arm is unaffected) and measures the mesh-centroid offset for
each hand.  If the right hand snaps into line with the left, the deltas are what
the profile's `basis_i` table is not paired with.
"""
import json
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H
import probe_mesh_vs_rig as MV

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
FLAT = 'out/profile-no-deltas.json'


def main():
    prof = json.load(open(PP, encoding='utf-8'))
    prof['symmetric_axis_deltas_wxyz'] = {}
    json.dump(prof, open(FLAT, 'w', encoding='utf-8'))

    for tag, pf in (('with deltas (profile as shipped)', PP),
                    ('deltas emptied', FLAT)):
        r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, pf, align='yaw',
                       spine_map='mesh', fallback='region', leg_tilt=5.7,
                       leg_splay=4.0, skeleton='native', verbose=False)
        out = np.asarray(r['out'])
        h = float(out[:, 2].max() - out[:, 2].min())
        idx2name = {h2['index']: h2['name'] for h2 in r['hd2']}
        cen = MV.centroids(out, r['verts'], r['resolved'], idx2name)
        rig = {h2['name']: np.asarray(r['rest_hd2'][h2['index']][:3, 3])
               for h2 in r['hd2'] if h2['name']}
        d = np.linalg.norm(out - np.asarray(r['src1']), axis=1)
        print('== %s ==' % tag)
        for nm in ('l_shoulder', 'l_elbow', 'l_hand', 'r_shoulder', 'r_elbow',
                   'r_hand'):
            v = (cen[nm] - rig[nm]) / h
            print('  %-11s centroid-joint  (%+.4f %+.4f %+.4f) h  = (%+.4f %+.4f '
                  '%+.4f) m' % (nm, v[0], v[1], v[2], v[0] * h, v[1] * h, v[2] * h))
        print('  max vertex move %.3f m   verts >0.5 m: %d' % (d.max(),
                                                               int((d > 0.5).sum())))
        print()


if __name__ == '__main__':
    main()
