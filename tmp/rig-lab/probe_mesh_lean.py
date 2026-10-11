"""How far OUT does each model's leg MESH lean, measured in the front view?

The bones are only half the story: the reference patch deliberately keeps its
mesh feet inboard of its own foot bones, so "match the bones" and "match the
mesh" are two different answers.  This probe measures the mesh, by taking the
X centre of the left leg (x < 0) in thin horizontal slabs and fitting a line
x = a + b*z.  The lean is atan(|b|).
"""
import json
import math
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import patch_view as PV

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
LEG_UNITS = ['17da843a4908511e']


def fit_leg(P, label, zs, xsign=-1):
    """Fit the left leg's X centre against z, over the given slab centres."""
    rows = []
    for z in zs:
        q = P[(P[:, 2] > z - 0.015) & (P[:, 2] < z + 0.015) & (P[:, 0] * xsign > 0.02)]
        if len(q) < 8:
            continue
        rows.append((z, q[:, 0].mean()))
    if len(rows) < 3:
        print('  %s: not enough slabs' % label)
        return
    z = np.array([r[0] for r in rows])
    x = np.array([r[1] for r in rows])
    b, a = np.polyfit(z, x, 1)
    ang = math.degrees(math.atan(abs(b)))
    print('  %-26s lean %5.2f deg   x(z) = %+.4f %+.4f*z' % (label, ang, a, b))
    print('      ' + '  '.join('z%.2f:%+.4f' % r for r in rows))
    return ang


def main():
    meta = json.load(open('out/patchdump/units.json', encoding='utf-8'))
    ref_mesh = []
    for u in meta:
        if u['unitFileId'][2:].lower() in LEG_UNITS:
            p, _t = PV.read_obj('out/patchdump/unit-%s.obj' % u['unitFileId'][2:].lower())
            ref_mesh.extend(p)
    ref = np.asarray(ref_mesh)
    print('REFERENCE PATCH  (height %.3f)' % (ref[:, 2].max() - ref[:, 2].min()))
    zs = np.arange(0.05, 0.90, 0.05)
    fit_leg(ref, 'ref mesh left leg', zs)

    import hd2_style as H
    r = H.retarget('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
                   'Lunalice_Zoey.vpk', 'survivor_teenangst',
                   'avatar/unit_0x4d1c334d294dfa97.toc', BH,
                   '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json',
                   align='yaw', spine_map='mesh', fallback='distance', verbose=False)
    ours = np.asarray(r['out'])
    print('OURS             (height %.3f)' % (ours[:, 2].max() - ours[:, 2].min()))
    zs2 = np.arange(0.05, 0.90, 0.05) * (ours[:, 2].max() - ours[:, 2].min()) / \
        (ref[:, 2].max() - ref[:, 2].min())
    fit_leg(ours, 'our mesh left leg', zs2)

    # and the bones, for reference
    hd2 = PV.hd2_rig_heads('avatar/unit_0x4d1c334d294dfa97.toc', BH)
    print('HD2 BONES: hip->knee->ankle lean %.2f / %.2f deg (span %.4f -> %.4f m)'
          % (math.degrees(math.atan2(abs(hd2['l_knee'][0] - hd2['l_thigh'][0]),
                                     hd2['l_thigh'][2] - hd2['l_knee'][2])),
             math.degrees(math.atan2(abs(hd2['l_foot'][0] - hd2['l_knee'][0]),
                                     hd2['l_knee'][2] - hd2['l_foot'][2])),
             abs(hd2['r_thigh'][0] - hd2['l_thigh'][0]),
             abs(hd2['r_foot'][0] - hd2['l_foot'][0])))


if __name__ == '__main__':
    main()
