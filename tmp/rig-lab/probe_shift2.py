"""Pick a robust Y anchor: compare body-part centroids, normalised by height.

Absolute z bands are not comparable (our mesh is 1.98 m, the reference 2.11 m),
so every band is a fraction of that mesh's own height.
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

BANDS = [('foot', 0.00, 0.06), ('shin', 0.06, 0.23), ('thigh', 0.23, 0.43),
         ('torso', 0.43, 0.73), ('chest', 0.60, 0.80), ('head', 0.80, 1.00),
         ('whole', 0.00, 1.00)]


def parts(P):
    P = np.asarray(P)
    lo, hi = P[:, 2].min(), P[:, 2].max()
    h = hi - lo
    out = {}
    for name, a, b in BANDS:
        m = P[(P[:, 2] >= lo + a * h) & (P[:, 2] < lo + b * h)]
        out[name] = m.mean(0) if len(m) else None
    out['_z'] = (lo, hi)
    return out


def main():
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP,
                   align='yaw', spine_map='mesh', fallback='distance',
                   leg_tilt=5.7, leg_splay=4.0, skeleton='native', verbose=False)
    ours = parts(r['out'])
    meta = json.load(open('out/patchdump/units.json', encoding='utf-8'))
    rp = []
    for u in meta:
        fid = u['unitFileId'][2:].lower()
        if fid in REF:
            p, t = PV.read_obj('out/patchdump/unit-%s.obj' % fid)
            if t:
                rp.extend(p)
    ref = parts(rp)

    print('our mesh z %.4f..%.4f   ref mesh z %.4f..%.4f'
          % (ours['_z'][0], ours['_z'][1], ref['_z'][0], ref['_z'][1]))
    print('\n%-7s %-24s %-24s %s' % ('part', 'ours (x,y,z)', 'ref (x,y,z)',
                                     'ref - ours (y only)'))
    for name, _a, _b in BANDS:
        o, f = ours[name], ref[name]
        if o is None or f is None:
            continue
        print('%-7s %-24s %-24s %+.4f' % (name, np.round(o, 4), np.round(f, 4),
                                          f[1] - o[1]))

    hd2 = PV.hd2_rig_heads(TOC, BH)
    print('\nHD2 rig y: hips %+.4f  l_foot %+.4f  neck %+.4f  head %+.4f'
          % (hd2['hips'][1], hd2['l_foot'][1], hd2['neck'][1], hd2['head'][1]))
    print('HD2 rig z: l_toe %+.4f  l_foot %+.4f  hips %+.4f  head %+.4f'
          % (hd2['l_toe'][2], hd2['l_foot'][2], hd2['hips'][2], hd2['head'][2]))


if __name__ == '__main__':
    main()
