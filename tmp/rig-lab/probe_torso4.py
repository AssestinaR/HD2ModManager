"""Clean scale fit over the torso band only.

Objective = median, over our torso sample points, of the distance to the nearest
reference torso point.  Both sets are restricted to the SAME z band
(hips -> neck), so the skirt and legs cannot bias the answer.  The objective is
printed as a function of scale, with the best (dy, dz) chosen for each scale, so
the minimum is visible rather than merely asserted.
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
NO = 200
NR = 2500


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
    ours_t = ours[np.isin(dom, list(TORSO_BONES))]

    meta = json.load(open('out/patchdump/units.json', encoding='utf-8'))
    rp = []
    for u in meta:
        if u['unitFileId'][2:].lower() == REF_TORSO:
            p, t = PV.read_obj('out/patchdump/unit-%s.obj' % REF_TORSO)
            if t:
                rp.extend(p)
    ref_t = np.asarray(rp, dtype=np.float64)

    lo = float(rig['hips'][2]) - 0.05
    hi = float(rig['neck'][2])
    O = ours_t[(ours_t[:, 2] >= lo) & (ours_t[:, 2] <= hi)]
    R = ref_t[(ref_t[:, 2] >= lo) & (ref_t[:, 2] <= hi)]
    print('band z %.3f..%.3f   our torso pts %d   ref torso pts %d'
          % (lo, hi, len(O), len(R)))
    O = O[np.linspace(0, len(O) - 1, min(NO, len(O))).astype(int)]
    R = R[np.linspace(0, len(R) - 1, min(NR, len(R))).astype(int)]

    piv = rig['hips'].astype(np.float64).copy()
    O = O - piv
    R = R - piv

    # scale about a pivot on the body axis at height pz, plus a y shift
    def obj(s, pz, dy):
        d = np.array([0.0, 0.0, pz - piv[2]])
        P = (O - d) * s + d + np.array([0.0, dy, 0.0])
        D = np.sqrt(((P[:, None, :] - R[None, :, :]) ** 2).sum(-1))
        return float(np.median(D.min(1)))

    print('\n%-6s %-8s %-8s %s' % ('scale', 'pivot z', 'dy', 'median nearest (m)'))
    best = (None, 1e9)
    for s in np.arange(1.00, 1.3001, 0.02):
        b = (None, 1e9)
        for pz in np.arange(0.90, 1.6001, 0.05):
            for dy in (-0.02, 0.0, 0.02):
                o = obj(s, pz, dy)
                if o < b[1]:
                    b = ((pz, dy), o)
        print('%.2f   %.3f    %+.2f     %.4f' % (s, b[0][0], b[0][1], b[1]))
        if b[1] < best[1]:
            best = ((s, b[0][0], b[0][1]), b[1])

    (s0, pz0, dy0), o0 = best
    print('\ncoarse best: scale %.3f  pivot z %.3f  dy %+.3f  -> %.4f m'
          % (s0, pz0, dy0, o0))
    for s in np.arange(s0 - 0.02, s0 + 0.0201, 0.005):
        for pz in np.arange(pz0 - 0.05, pz0 + 0.0501, 0.02):
            for dy in (-0.01, 0.0, 0.01):
                o = obj(s, pz, dy)
                if o < best[1]:
                    best = ((s, pz, dy), o)
    (s1, pz1, dy1), o1 = best
    print('refined    : scale %.4f  pivot z %.3f  dy %+.4f  -> %.4f m'
          % (s1, pz1, dy1, o1))
    print('\nscale 1.00 for comparison: %.4f m (at its own best pivot/dy)'
          % min(obj(1.0, pz, dy) for pz in np.arange(0.90, 1.6001, 0.05)
                for dy in (-0.02, 0.0, 0.02)))


if __name__ == '__main__':
    main()
