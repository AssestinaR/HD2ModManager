"""Best rigid TRANSLATION taking our mesh onto the reference mesh.

Brute force on a grid, objective = median over our sample points of the distance
to the nearest reference point.  Median (not mean) so a stray spike or a lock of
hair cannot dominate.

Reported twice: over the whole mesh, and over the legs only (below 45 % of the
height), because that is where the user sees the offset.

usage: probe_fit_shift.py
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
N = 350


def sample(P, n=N):
    P = np.asarray(P, dtype=np.float64)
    idx = np.linspace(0, len(P) - 1, min(n, len(P))).astype(int)
    return P[idx]


def fit(ours, ref, dys, dzs):
    best = (None, 1e9)
    for dy in dys:
        for dz in dzs:
            P = ours + np.array([0.0, dy, dz])
            D = np.sqrt(((P[:, None, :] - ref[None, :, :]) ** 2).sum(-1))
            obj = float(np.median(D.min(1)))
            if obj < best[1]:
                best = ((dy, dz), obj)
    return best


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

    dys = np.arange(-0.30, 0.1001, 0.01)
    dzs = np.arange(-0.12, 0.0601, 0.01)

    o_all, f_all = sample(ours), sample(ref)
    (dy, dz), obj = fit(o_all, f_all, dys, dzs)
    print('WHOLE mesh : best dy %+.3f dz %+.3f m  (median nearest %.4f m)'
          % (dy, dz, obj))

    def legs(P):
        lo, hi = P[:, 2].min(), P[:, 2].max()
        return P[P[:, 2] < lo + 0.45 * (hi - lo)]

    o_l, f_l = sample(legs(ours)), sample(legs(ref))
    (dy2, dz2), obj2 = fit(o_l, f_l, dys, dzs)
    print('LEGS only  : best dy %+.3f dz %+.3f m  (median nearest %.4f m)'
          % (dy2, dz2, obj2))

    # refine around the legs optimum
    dys2 = np.arange(dy2 - 0.02, dy2 + 0.0201, 0.002)
    dzs2 = np.arange(dz2 - 0.02, dz2 + 0.0201, 0.002)
    (dy3, dz3), obj3 = fit(o_l, f_l, dys2, dzs2)
    print('LEGS refine: best dy %+.3f dz %+.3f m  (median nearest %.4f m)'
          % (dy3, dz3, obj3))

    o_t, f_t = sample(ours[ours[:, 2] > ours[:, 2].min() + 0.6]), \
        sample(ref[ref[:, 2] > ref[:, 2].min() + 0.6])
    (dy4, dz4), obj4 = fit(o_t, f_t, dys, dzs)
    print('TORSO+HEAD : best dy %+.3f dz %+.3f m  (median nearest %.4f m)'
          % (dy4, dz4, obj4))


if __name__ == '__main__':
    main()
