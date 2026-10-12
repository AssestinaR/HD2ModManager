"""Where exactly is our foot, in Y, relative to HD2's ankle -- and the same for
the reference.  Prints percentiles so a stray spike cannot fool the mean.
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


def report(tag, P, ankle_y):
    P = np.asarray(P)
    h = P[:, 2].max() - P[:, 2].min()
    for frac, lab in ((0.06, 'z<6%h'), (0.10, 'z<10%h')):
        m = P[(P[:, 2] < P[:, 2].min() + frac * h) & (np.abs(P[:, 0]) > 0.05)]
        if not len(m):
            continue
        q = np.percentile(m[:, 1], [0, 10, 25, 50, 75, 90, 100])
        print('%-5s %-7s n=%-6d y pct 0/10/25/50/75/90/100 = %s'
              % (tag, lab, len(m), np.round(q, 3)))
        print('%-5s %-7s median y %+.4f  ->  %+.4f vs the ankle (y %+.4f)'
              % ('', lab, q[3], q[3] - ankle_y, ankle_y))


def main():
    hd2 = PV.hd2_rig_heads(TOC, BH)
    ank = hd2['l_foot'][1]
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP,
                   align='yaw', spine_map='mesh', fallback='distance',
                   leg_tilt=5.7, leg_splay=4.0, skeleton='native', verbose=False)
    report('OURS', r['out'], ank)

    meta = json.load(open('out/patchdump/units.json', encoding='utf-8'))
    rp = []
    for u in meta:
        fid = u['unitFileId'][2:].lower()
        if fid in REF:
            p, t = PV.read_obj('out/patchdump/unit-%s.obj' % fid)
            if t:
                rp.extend(p)
    report('REF', rp, ank)
    print('\nHD2 ankle y %+.4f   our new_head l_foot y %+.4f (so our rig ankle is '
          '%+.4f off HD2\'s)' % (ank, r['new_head']['l_foot'][1],
                                 r['new_head']['l_foot'][1] - ank))


if __name__ == '__main__':
    main()
