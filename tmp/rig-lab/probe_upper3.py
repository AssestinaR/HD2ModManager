"""Compare named candidate (scale, pivot z, blend) configs on one objective."""
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
REF_HEAD = 'af431352d9b95e83'
TORSO_BONES = ('hips', 'spine1', 'spine2', 'chest')
CANDS = [
    ('baseline           ', 1.00, 1.15, 0.45),
    ('A fit optimum      ', 1.12, 1.15, 0.45),
    ('A narrower blend   ', 1.12, 1.15, 0.35),
    ('A lower pivot      ', 1.12, 1.10, 0.30),
    ('A2                ', 1.13, 1.12, 0.32),
    ('A3                ', 1.14, 1.10, 0.35),
    ('A4                ', 1.13, 1.15, 0.40),
    ('B narrow waist     ', 1.15, 1.05, 0.20),
]


def apply_upper(P, scale, piv, blend):
    z0, z1 = piv[2] - blend, piv[2] + blend
    t = np.clip((P[:, 2] - z0) / max(z1 - z0, 1e-9), 0.0, 1.0)
    w = t * t * (3.0 - 2.0 * t)
    return piv + (P - piv) * (1.0 + (scale - 1.0) * w)[:, None]


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
    obj = {}
    for u in meta:
        fid = u['unitFileId'][2:].lower()
        if fid in (REF_TORSO, REF_HEAD):
            p, t = PV.read_obj('out/patchdump/unit-%s.obj' % fid)
            if t:
                obj[fid] = np.asarray(p, dtype=np.float64)
    ref_t, ref_h = obj[REF_TORSO], obj[REF_HEAD]

    lo, hi = float(rig['hips'][2]) - 0.05, float(rig['neck'][2])
    band = lambda P: P[(P[:, 2] >= lo) & (P[:, 2] <= hi)]
    R = band(ref_t)
    R = R[np.linspace(0, len(R) - 1, 2200).astype(int)]

    def score(P):
        O = band(P)
        O = O[np.linspace(0, len(O) - 1, min(160, len(O))).astype(int)]
        D = np.sqrt(((O[:, None, :] - R[None, :, :]) ** 2).sum(-1))
        return float(np.median(D.min(1)))

    print('%-19s %-7s %-7s %-6s | %-9s %-8s %-8s %-8s %s'
          % ('config', 'scale', 'pivot', 'blend', 'torso med', 'height',
             'head top', 'hand gap', 'head-vs-joint'))
    piv0 = rig['hips'].astype(np.float64).copy()
    for lab, s, pz, bl in CANDS:
        p = piv0.copy()
        p[2] = pz
        P = apply_upper(ours, s, p, bl) if s != 1.0 else ours.copy()
        gap = min(float(np.linalg.norm(P[dom == nm].mean(0) - rig[nm]))
                  for nm in ('l_hand', 'r_hand'))
        htop = P[:, 2].max()
        print('%-19s %-7.3f %-7.2f %-6.2f | %-9.4f %-8.4f %-8.4f %-8.4f %+.4f'
              % (lab, s, pz, bl, score(P), P[:, 2].max() - P[:, 2].min(), htop,
                 gap, htop - rig['head'][2]))
    print('\nreference: height %.4f   head top %.4f   head top - joint %+.4f'
          % (ref_t[:, 2].max() - ref_t[:, 2].min(), ref_h[:, 2].max(),
             ref_h[:, 2].max() - rig['head'][2]))
    print('head joint z %.4f' % rig['head'][2])


if __name__ == '__main__':
    main()
