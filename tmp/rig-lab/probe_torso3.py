"""Per-slice torso width/depth: ours (torso bones) vs reference (torso unit).

Arms and hair are excluded on BOTH sides -- ours by HD2 bone dominance, the
reference by using only its `Torso_Undergarment_Slim` unit -- so the numbers are
a like-for-like comparison of the upper garment.
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
REF_TORSO = 'cae35ee15168145f'          # Torso_Undergarment_Slim
REF_HIP = '17da843a4908511e'            # Hip_Undergarment_Stocky
TORSO_BONES = ('hips', 'spine1', 'spine2', 'chest')


def main():
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
        dom.append(idx2name.get(r['resolved'].get(v['bones'][j]))
                   if v['w'][j] > 0 else None)
    dom = np.array(dom, dtype=object)
    ours_t = ours[np.isin(dom, list(TORSO_BONES))]

    meta = json.load(open('out/patchdump/units.json', encoding='utf-8'))
    rp = []
    for u in meta:
        if u['unitFileId'][2:].lower() == REF_TORSO:
            p, t = PV.read_obj('out/patchdump/unit-%s.obj' % REF_TORSO)
            if t:
                rp.extend(p)
    ref_t = np.asarray(rp, dtype=np.float64)

    z0, z1 = float(rig['hips'][2]), float(rig['neck'][2])
    n = 16
    print('torso-only slices, hips %.3f -> neck %.3f' % (z0, z1))
    print('%-7s | %-22s | %-22s | %s'
          % ('z', 'OURS  width depth', 'REF   width depth', 'ratio w / d'))
    rw, rd = [], []
    for i in range(n):
        a = z0 + (z1 - z0) * i / n
        b = z0 + (z1 - z0) * (i + 1) / n
        so = ours_t[(ours_t[:, 2] >= a) & (ours_t[:, 2] < b)]
        sr = ref_t[(ref_t[:, 2] >= a) & (ref_t[:, 2] < b)]
        if len(so) < 20 or len(sr) < 20:
            continue
        wo = so[:, 0].max() - so[:, 0].min()
        do = so[:, 1].max() - so[:, 1].min()
        wr = sr[:, 0].max() - sr[:, 0].min()
        dr = sr[:, 1].max() - sr[:, 1].min()
        rw.append(wr / wo)
        rd.append(dr / do)
        print('%.3f   | %7.3f %7.3f        | %7.3f %7.3f        | %.3f / %.3f'
              % ((a + b) / 2, wo, do, wr, dr, wr / wo, dr / do))
    print('\nmean ratio over the torso slices:  width %.4f   depth %.4f'
          % (float(np.mean(rw)), float(np.mean(rd))))
    print('median ratio:                      width %.4f   depth %.4f'
          % (float(np.median(rw)), float(np.median(rd))))

    # the same, but including the hip unit for the reference and hips/thigh
    # dominance for ours, i.e. the whole garment
    print('\n-- whole garment (ours: hips..chest bones; ref: Torso+Hip units) --')
    rp2 = []
    for u in meta:
        if u['unitFileId'][2:].lower() in (REF_TORSO, REF_HIP):
            p, t = PV.read_obj('out/patchdump/unit-%s.obj' % u['unitFileId'][2:].lower())
            if t:
                rp2.extend(p)
    ref_g = np.asarray(rp2)
    for lo, hi in ((0.95, 1.20), (1.20, 1.45), (1.45, 1.62)):
        so = ours[(ours[:, 2] >= lo) & (ours[:, 2] < hi)]
        sr = ref_g[(ref_g[:, 2] >= lo) & (ref_g[:, 2] < hi)]
        print('  z %.2f-%.2f  ours w %.3f d %.3f | ref w %.3f d %.3f'
              % (lo, hi, so[:, 0].ptp(), so[:, 1].ptp(),
                 sr[:, 0].ptp(), sr[:, 1].ptp()))


if __name__ == '__main__':
    main()
