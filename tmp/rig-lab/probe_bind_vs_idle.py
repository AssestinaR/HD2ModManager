"""Is the forward lean a REAL problem, or only a bind-pose artefact?

Compares, per dataset, the side-view landmarks that the user actually described:
hip vs mid-foot (legs) and hip vs neck/head (torso), measured BOTH on the bind
pose (what the old screenshots showed) and on out_idle (what the game shows).
"""
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


def foot(P, band=0.08, side=-1):
    zmin = P[:, 2].min()
    m = P[(P[:, 2] < zmin + band) & (P[:, 0] * side > 0.05)]
    if len(m) < 10:
        m = P[P[:, 2] < zmin + band]
    return float(m[:, 1].min()), float(m[:, 1].max()), float(m[:, 2].min())


def main():
    rs = {a: H.retarget(VPK, MODEL, TOC, BH, PP, align=a, spine_map='mesh',
                        fallback='region', verbose=False)
          for a in ('kabsch', 'yaw')}

    print('\n-- out_idle: does the alignment even matter? --')
    d = np.abs(np.asarray(rs['kabsch']['out_idle']) - np.asarray(rs['yaw']['out_idle']))
    print('max |kabsch_idle - yaw_idle| = %.6f m' % d.max())

    print('\n-- side-view landmarks (all Y offsets are vs the HIP; -Y = behind) --')
    print('%-30s %8s %8s %8s %8s %8s' % ('dataset', 'hip y', 'knee d', 'ankle d',
                                         'neck d', 'head d'))
    for a in ('kabsch', 'yaw'):
        nh = rs[a]['new_head']
        hip = nh['l_thigh']
        print('%-30s %+8.4f %+8.4f %+8.4f %+8.4f %+8.4f'
              % ('OURS rest / bind (%s)' % a, hip[1],
                 nh['l_knee'][1] - hip[1], nh['l_foot'][1] - hip[1],
                 nh['neck'][1] - hip[1], nh['head'][1] - hip[1]))
    print('%-30s %+8.4f %+8.4f %+8.4f %+8.4f %+8.4f'
          % ('HD2 native rest', -0.0190, -0.0155 + 0.0190, -0.0986,
             -0.0222, -0.0061))

    print('\n-- foot mesh vs hip (the user\'s "hip over the foot arch" test) --')
    for a in ('kabsch', 'yaw'):
        for which in ('out', 'out_idle'):
            P = np.asarray(rs[a][which])
            ylo, yhi, z0 = foot(P)
            hipy = rs[a]['new_head']['l_thigh'][1] if which == 'out' else -0.0190
            pct = 100.0 * (hipy - ylo) / max(yhi - ylo, 1e-9)
            print('%-30s foot y [%+.4f,%+.4f] len %.4f  hip %.1f%% from heel  z0 %+.4f'
                  % ('OURS %s (%s)' % (which, a), ylo, yhi, yhi - ylo, pct, z0))

    # the reference patch
    pts = []
    for fid in ('cae35ee15168145f', '17da843a4908511e',
                '91eb7be84016ceab', '7336c6b662522f0e'):
        for ln in open('out/patchdump/unit-%s.obj' % fid, encoding='utf-8'):
            if ln.startswith('v '):
                _, x, y, z = ln.split()[:4]
                pts.append((float(x), float(y), float(z)))
    ref = np.asarray(pts)
    ylo, yhi, z0 = foot(ref)
    print('%-30s foot y [%+.4f,%+.4f] len %.4f  hip %.1f%% from heel  z0 %+.4f'
          % ('REFERENCE patch', ylo, yhi, yhi - ylo,
             100.0 * (-0.0190 - ylo) / (yhi - ylo), z0))

    print('\n-- height --')
    for a in ('kabsch', 'yaw'):
        for which in ('out', 'out_idle'):
            P = np.asarray(rs[a][which])
            print('%-30s z[%+.4f, %+.4f]  height %.4f'
                  % ('OURS %s (%s)' % (which, a), P[:, 2].min(), P[:, 2].max(),
                     P[:, 2].max() - P[:, 2].min()))
    print('%-30s z[%+.4f, %+.4f]  height %.4f'
          % ('REFERENCE patch', ref[:, 2].min(), ref[:, 2].max(),
             ref[:, 2].max() - ref[:, 2].min()))


if __name__ == '__main__':
    main()
