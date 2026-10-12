"""Fit the parameters of the ACTUAL transform (scale, pivot z, blend width).

probe_torso4 fitted an idealised pure scale and landed on a pivot at the chest,
which would drag the hips down into the legs.  The transform we can actually ship
has a smoothstep ramp, so fit THAT: for each (scale, pivot_z, blend) apply the
exact same formula `hd2_style.retarget` uses and score it against the reference
torso.
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
REF_TORSO = 'cae35ee15168145f'
TORSO_BONES = ('hips', 'spine1', 'spine2', 'chest')
NO, NR = 160, 2200


def apply_upper(P, scale, piv, blend):
    z0 = piv[2] - blend
    z1 = piv[2] + blend
    t = np.clip((P[:, 2] - z0) / max(z1 - z0, 1e-9), 0.0, 1.0)
    w = t * t * (3.0 - 2.0 * t)
    s = (1.0 + (scale - 1.0) * w)[:, None]
    return piv + (P - piv) * s


def main():
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP,
                   align='yaw', spine_map='mesh', fallback='region',
                   leg_tilt=5.7, leg_splay=4.0, skeleton='native',
                   mesh_shift=(0.0, -0.100, -0.060), verbose=False)
    ours = np.asarray(r['out'], dtype=np.float64)
    rig = {h['name']: np.asarray(r['rest_hd2'][h['index']][:3, 3])
           for h in r['hd2'] if h['name']}
    idx2name = {h['index']: h['name'] for h in r['hd2']}
    dom = np.array([idx2name.get(r['resolved'].get(v['bones'][int(np.argmax(v['w']))]))
                    if v['w'][int(np.argmax(v['w']))] > 0 else None
                    for v in r['verts']], dtype=object)

    meta = json.load(open('out/patchdump/units.json', encoding='utf-8'))
    rp = []
    for u in meta:
        if u['unitFileId'][2:].lower() == REF_TORSO:
            p, t = PV.read_obj('out/patchdump/unit-%s.obj' % REF_TORSO)
            if t:
                rp.extend(p)
    ref_t = np.asarray(rp, dtype=np.float64)

    lo, hi = float(rig['hips'][2]) - 0.05, float(rig['neck'][2])
    band = lambda P: P[(P[:, 2] >= lo) & (P[:, 2] <= hi)]
    R = band(ref_t)
    R = R[np.linspace(0, len(R) - 1, NR).astype(int)]

    def score(P):
        O = band(P)
        O = O[np.linspace(0, len(O) - 1, min(NO, len(O))).astype(int)]
        D = np.sqrt(((O[:, None, :] - R[None, :, :]) ** 2).sum(-1))
        return float(np.median(D.min(1)))

    ours_t = ours[np.isin(dom, list(TORSO_BONES))]
    print('baseline (no upper scale): %.4f m' % score(ours))

    piv = rig['hips'].astype(np.float64).copy()
    results = []
    for scale in np.arange(1.08, 1.2401, 0.02):
        for pz in np.arange(0.95, 1.4001, 0.05):
            for blend in (0.10, 0.20, 0.30, 0.45):
                p = piv.copy()
                p[2] = pz
                # the REAL transform moves every vertex above the ramp, not just
                # the torso-weighted ones (arms/head ride along)
                P = apply_upper(ours, scale, p, blend)
                results.append((score(P), scale, pz, blend))
    results.sort()
    print('\n%-8s %-8s %-7s %s' % ('scale', 'pivot z', 'blend', 'median (m)'))
    for sc, s, pz, bl in results[:14]:
        print('%.3f    %.3f    %.2f     %.4f' % (s, pz, bl, sc))
    print('\nworst few, for contrast:')
    for sc, s, pz, bl in results[-3:]:
        print('%.3f    %.3f    %.2f     %.4f' % (s, pz, bl, sc))

    # what the best does to the legs and to the hands
    sc, s, pz, bl = results[0]
    p = piv.copy()
    p[2] = pz
    P = apply_upper(ours, s, p, bl)
    print('\nbest: scale %.3f pivot z %.3f blend %.2f' % (s, pz, bl))
    print('   height %.4f (was %.4f)   sole z %.4f (was %.4f)'
          % (P[:, 2].max() - P[:, 2].min(), ours[:, 2].max() - ours[:, 2].min(),
             P[:, 2].min(), ours[:, 2].min()))
    for nm in ('l_hand', 'r_hand'):
        a = ours[dom == nm].mean(0)
        b = P[dom == nm].mean(0)
        print('   %s centroid x %+.3f -> %+.3f   joint %+.3f   gap %.3f -> %.3f m'
              % (nm, a[0], b[0], rig[nm][0], np.linalg.norm(a - rig[nm]),
                 np.linalg.norm(b - rig[nm])))
    for nm in ('l_foot', 'l_knee', 'hips', 'l_shoulder', 'head'):
        a = ours[dom == nm].mean(0) if (dom == nm).any() else None
        b = P[dom == nm].mean(0) if (dom == nm).any() else None
        if a is None:
            continue
        print('   %-11s centroid z %.4f -> %.4f   (joint z %.4f)'
              % (nm, a[2], b[2], rig[nm][2]))


if __name__ == '__main__':
    main()
