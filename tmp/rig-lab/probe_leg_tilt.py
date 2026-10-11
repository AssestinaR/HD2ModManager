"""How much leg tilt reproduces HD2's stance?  Measures the bind pose (the A-pose
we actually publish) against the reference patch, for a sweep of tilt angles."""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H

TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
MODEL = 'survivor_teenangst'


def foot_mid(P, band=0.08, side=-1):
    zmin = P[:, 2].min()
    m = P[(P[:, 2] < zmin + band) & (P[:, 0] * side > 0.05)]
    if len(m) < 10:
        m = P[P[:, 2] < zmin + band]
    return float(m[:, 1].min()), float(m[:, 1].max())


def main():
    ref = []
    for fid in ('cae35ee15168145f', '17da843a4908511e',
                '91eb7be84016ceab', '7336c6b662522f0e'):
        for ln in open('out/patchdump/unit-%s.obj' % fid, encoding='utf-8'):
            if ln.startswith('v '):
                _, x, y, z = ln.split()[:4]
                ref.append((float(x), float(y), float(z)))
    ref = np.asarray(ref)
    rlo, rhi = foot_mid(ref)
    print('REFERENCE patch: hip y -0.0190, foot [%+.4f,%+.4f] len %.4f, '
          'hip %.1f%% from heel, ankle at %.1f%%'
          % (rlo, rhi, rhi - rlo, 100 * (-0.0190 - rlo) / (rhi - rlo),
             100 * (-0.1175 - rlo) / (rhi - rlo)))
    print()
    print('%-8s %10s %10s %10s %10s %10s' % ('tilt', 'knee d', 'ankle d', 'neck d',
                                             'head d', 'hip over foot'))
    for tilt in (0.0, 3.0, 5.0, 6.5, 8.0, 10.0):
        r = H.retarget(VPK, MODEL, TOC, BH, PP, align='yaw', spine_map='mesh',
                       fallback='distance', leg_tilt=tilt, verbose=False)
        nh = r['new_head']
        hip = nh['l_thigh']
        P = np.asarray(r['out'])
        lo, hi = foot_mid(P)
        print('%-8.1f %+10.4f %+10.4f %+10.4f %+10.4f %10.1f%%'
              % (tilt, nh['l_knee'][1] - hip[1], nh['l_foot'][1] - hip[1],
                 nh['neck'][1] - hip[1], nh['head'][1] - hip[1],
                 100 * (hip[1] - lo) / (hi - lo)))


if __name__ == '__main__':
    main()
