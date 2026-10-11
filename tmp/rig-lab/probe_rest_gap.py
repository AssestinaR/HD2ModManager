"""How far is OUR shipped rig (S_A = D . rest_sym) from HD2's NATIVE rig?

The reference patch's rig is bit-identical to HD2's native rig, so "distance to
HD2 native" is the ground-truth quality metric for the shipped skeleton.  The
mesh is bound to S_A, and the game deforms by  rest_hd2[b] . inv(S_A[b])  at
idle, so a bone whose S_A rotation is off by Dd rotates its whole subtree's mesh
by Dd every frame.

Reported per configuration:
  head   |S_A[b].head - rest_hd2[b].head|           (m)
  rot    angle between S_A[b] rotation and HD2's    (deg)
  slip   head_slip: how far the parent's idle map drags bone b's joint
"""
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

ARM = ('l_clavicle', 'l_shoulder', 'l_elbow', 'l_hand', 'r_clavicle', 'r_shoulder',
       'r_elbow', 'r_hand')


def rot_ang(A, B):
    R = A[:3, :3].T @ B[:3, :3]
    t = np.trace(R)
    return float(np.degrees(np.arccos(np.clip((t - 1) / 2.0, -1, 1))))


def run(axis_deltas, axis_roundtrip=True):
    return H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                      spine_map='mesh', fallback='distance', leg_tilt=5.7,
                      leg_splay=4.0, axis_deltas=axis_deltas,
                      axis_roundtrip=axis_roundtrip, verbose=False)


def main():
    for tag, ad, rt in (('LEGACY  deltas ON  (S_A = D.rest_sym)', True, False),
                        ('LEGACY  deltas OFF', False, False),
                        ('ROUND TRIP  (what ships)', True, True)):
        r = run(ad, rt)
        hd2 = r['hd2']
        by_idx = {h['index']: h for h in hd2}
        SA = r['rest_shipped']
        T = {i: r['rest_hd2'][i] @ np.linalg.inv(SA[i]) for i in r['rest_shipped']}

        dh, dr, slip = [], [], []
        worst = []
        for h in hd2:
            i, p = h['index'], h.get('parent')
            if i not in SA:
                continue
            a = float(np.linalg.norm(SA[i][:3, 3] - r['rest_hd2'][i][:3, 3]))
            b = rot_ang(SA[i], r['rest_hd2'][i])
            dh.append(a)
            dr.append(b)
            if p is None or p == i or p not in T:
                continue
            head = SA[i][:3, 3]
            s = float(np.linalg.norm((T[i] @ np.append(head, 1.0))[:3]
                                     - (T[p] @ np.append(head, 1.0))[:3]))
            slip.append(s)
            worst.append((s, h['name']))
        dh, dr, slip = map(np.asarray, (dh, dr, slip))
        worst.sort(key=lambda x: -x[0])
        d = np.linalg.norm(np.asarray(r['out_idle']) - np.asarray(r['out']), axis=1)
        print('== %s ==' % tag)
        print('  head offset vs HD2 native:  mean %.4f  median %.4f  p90 %.4f  max %.4f m'
              % (dh.mean(), np.median(dh), np.percentile(dh, 90), dh.max()))
        print('  rot offset  vs HD2 native:  mean %6.2f  median %6.2f  p90 %6.2f  max %6.2f deg'
              % (dr.mean(), np.median(dr), np.percentile(dr, 90), dr.max()))
        print('  bones with rot offset > 5 deg : %d of %d' % (int((dr > 5).sum()), len(dr)))
        print('  head_slip (idle shear):     mean %.4f  median %.4f  p90 %.4f  max %.4f m'
              % (slip.mean(), np.median(slip), np.percentile(slip, 90), slip.max()))
        print('  worst slip: %s' % ', '.join('%s %.3f' % (n, v) for v, n in worst[:6]))
        print('  idle frame moves the mesh:  mean %.4f  max %.4f m' % (d.mean(), d.max()))
        print('  arm chain head offsets:')
        for nm in ARM:
            i = next((h['index'] for h in hd2 if h['name'] == nm), None)
            if i is None or i not in SA:
                continue
            print('    %-16s head %.4f m   rot %6.2f deg'
                  % (nm, float(np.linalg.norm(SA[i][:3, 3]
                                              - r['rest_hd2'][i][:3, 3])),
                     rot_ang(SA[i], r['rest_hd2'][i])))
        print('  height %.3f   edge_mean %.4f   bad %.2f%%'
              % (r['metrics']['height_after'], r['metrics']['edge_mean'],
                 100 * r['metrics']['bad']))
        print()


if __name__ == '__main__':
    main()
