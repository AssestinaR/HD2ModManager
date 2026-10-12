"""Step 2: fit a SCALE for our torso so it matches the reference's torso.

The reference patch is the SAME base model placed on the SAME HD2 rig, so the
two torsos are corresponding surfaces -- only the size differs.  That means the
scale can be fitted directly, without hand-picked feature points:

  ours torso   = our vertices whose dominant HD2 bone is a torso bone
                 (hips / spine1 / spine2 / chest)  -- arms and head excluded by
                 construction, so no |x| window guessing;
  ref  torso   = the reference patch's own torso units, read straight off their
                 tags: `Torso_Undergarment_Slim` + `Hip_Undergarment_Stocky`.

Objective = median over our sample points of the distance to the nearest
reference point (median, so a stray lock of hair cannot dominate).  The scale is
applied about the `hips` joint, so the legs stay where the earlier rounds put
them.

usage: probe_torso2.py
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
TORSO_UNITS = ['cae35ee15168145f', '17da843a4908511e']      # Torso + Hip
TORSO_BONES = ('hips', 'spine1', 'spine2', 'chest')


def build():
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP,
                   align='yaw', spine_map='mesh', fallback='region',
                   leg_tilt=5.7, leg_splay=4.0, skeleton='native',
                   mesh_shift=(0.0, -0.100, -0.060), verbose=False)
    ours = np.asarray(r['out'], dtype=np.float64)
    rig = {h['name']: np.asarray(r['rest_hd2'][h['index']][:3, 3])
           for h in r['hd2'] if h['name']}
    idx2name = {h['index']: h['name'] for h in r['hd2']}
    dom = []
    for v in r['verts']:
        j = int(np.argmax(v['w']))
        dom.append(idx2name.get(r['resolved'].get(v['bones'][j])) if v['w'][j] > 0
                   else None)
    dom = np.array(dom, dtype=object)
    mask = np.isin(dom, list(TORSO_BONES))
    ours_t = ours[mask]

    meta = json.load(open('out/patchdump/units.json', encoding='utf-8'))
    ref_t = []
    for u in meta:
        fid = u['unitFileId'][2:].lower()
        if fid in TORSO_UNITS:
            p, t = PV.read_obj('out/patchdump/unit-%s.obj' % fid)
            if t:
                ref_t.extend(p)
    return ours, ours_t, np.asarray(ref_t, dtype=np.float64), rig, dom, r


def sample(P, n):
    P = np.asarray(P, dtype=np.float64)
    if len(P) <= n:
        return P
    idx = np.linspace(0, len(P) - 1, n).astype(int)
    return P[idx]


def bbox(P):
    return (P[:, 0].min(), P[:, 0].max(), P[:, 1].min(), P[:, 1].max(),
            P[:, 2].min(), P[:, 2].max())


def main():
    ours, ours_t, ref_t, rig, dom, r = build()
    print('our torso verts (dominant HD2 bone in %s): %d / %d'
          % (TORSO_BONES, len(ours_t), len(ours)))
    print('ref torso verts (Torso+Hip units): %d' % len(ref_t))
    for nm, P in (('ours torso', ours_t), ('ref  torso', ref_t)):
        b = bbox(P)
        print('   %-10s x[%+.3f,%+.3f] y[%+.3f,%+.3f] z[%+.3f,%+.3f]'
              % (nm, b[0], b[1], b[2], b[3], b[4], b[5]))
        print('              width %.3f  depth %.3f  height %.3f'
              % (b[1] - b[0], b[3] - b[2], b[5] - b[4]))

    pivot = rig['hips'].copy()
    print('\npivot = hips joint (%.3f, %.3f, %.3f)' % tuple(pivot))

    O = sample(ours_t, 320)
    R = sample(ref_t, 7000)
    O = O - pivot
    R = R - pivot

    def obj_at(s, dy, dz):
        P = O * s + np.array([0.0, dy, dz])
        D = np.sqrt(((P[:, None, :] - R[None, :, :]) ** 2).sum(-1))
        return float(np.median(D.min(1)))

    print('\ncoarse grid  (scale, dy, dz) -> median nearest distance')
    best = (None, 1e9)
    for s in np.arange(1.00, 1.401, 0.02):
        for dy in np.arange(-0.06, 0.0601, 0.02):
            for dz in np.arange(-0.06, 0.0601, 0.02):
                o = obj_at(s, dy, dz)
                if o < best[1]:
                    best = ((s, dy, dz), o)
    (s0, dy0, dz0), o0 = best
    print('   best scale %.3f  dy %+.3f  dz %+.3f  -> %.4f m' % (s0, dy0, dz0, o0))

    print('\nrefine')
    for s in np.arange(s0 - 0.03, s0 + 0.0301, 0.005):
        for dy in np.arange(dy0 - 0.03, dy0 + 0.0301, 0.01):
            for dz in np.arange(dz0 - 0.03, dz0 + 0.0301, 0.01):
                o = obj_at(s, dy, dz)
                if o < best[1]:
                    best = ((s, dy, dz), o)
    (s1, dy1, dz1), o1 = best
    print('   best scale %.4f  dy %+.4f  dz %+.4f  -> %.4f m' % (s1, dy1, dz1, o1))

    print('\nfor reference, the median distance at scale 1.00:')
    for dy in (-0.02, 0.0, 0.02):
        for dz in (-0.02, 0.0, 0.02):
            print('   s=1.00 dy %+.2f dz %+.2f -> %.4f m' % (dy, dz, obj_at(1.0, dy, dz)))


if __name__ == '__main__':
    main()
