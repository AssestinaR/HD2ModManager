"""Verify --upper-scale: does it close the torso gap, and where do the hands land?

usage: probe_upper.py [scale] [blend]
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
HAND_BONES = ('l_hand', 'r_hand')


def run(scale, blend):
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP,
                   align='yaw', spine_map='mesh', fallback='region',
                   leg_tilt=5.7, leg_splay=4.0, skeleton='native',
                   mesh_shift=(0.0, -0.100, -0.060),
                   upper_scale=scale, upper_blend=blend, verbose=False)
    ours = np.asarray(r['out'], dtype=np.float64)
    rig = {h['name']: np.asarray(r['rest_hd2'][h['index']][:3, 3])
           for h in r['hd2'] if h['name']}
    idx2name = {h['index']: h['name'] for h in r['hd2']}
    dom = np.array([idx2name.get(r['resolved'].get(v['bones'][int(np.argmax(v['w']))]))
                    if v['w'][int(np.argmax(v['w']))] > 0 else None
                    for v in r['verts']], dtype=object)
    return ours, rig, dom, r


def main():
    scale = float(sys.argv[1]) if len(sys.argv) > 1 else 1.12
    blend = float(sys.argv[2]) if len(sys.argv) > 2 else 0.20

    meta = json.load(open('out/patchdump/units.json', encoding='utf-8'))
    rp = []
    for u in meta:
        if u['unitFileId'][2:].lower() == REF_TORSO:
            p, t = PV.read_obj('out/patchdump/unit-%s.obj' % REF_TORSO)
            if t:
                rp.extend(p)
    ref_t = np.asarray(rp, dtype=np.float64)

    ours1, rig, dom1, r1 = run(1.0, blend)
    ours2, _rig2, dom2, r2 = run(scale, blend)

    print('=== torso slice ratios (ref / ours) ===')
    z0, z1 = float(rig['hips'][2]), float(rig['neck'][2])
    n = 12
    rw1, rd1, rw2, rd2 = [], [], [], []
    print('%-7s | %-17s | %-17s' % ('z', 'scale 1.00  w / d', 'scale %.2f  w / d' % scale))
    for i in range(n):
        a = z0 + (z1 - z0) * i / n
        b = z0 + (z1 - z0) * (i + 1) / n
        sr = ref_t[(ref_t[:, 2] >= a) & (ref_t[:, 2] < b)]
        if len(sr) < 20:
            continue
        wr = sr[:, 0].max() - sr[:, 0].min()
        dr = sr[:, 1].max() - sr[:, 1].min()
        row = ''
        vals = []
        for ours, dom in ((ours1, dom1), (ours2, dom2)):
            so = ours[np.isin(dom, list(TORSO_BONES))]
            so = so[(so[:, 2] >= a) & (so[:, 2] < b)]
            if len(so) < 20:
                vals.append((float('nan'), float('nan')))
                continue
            vals.append((wr / max(so[:, 0].max() - so[:, 0].min(), 1e-9),
                         dr / max(so[:, 1].max() - so[:, 1].min(), 1e-9)))
        rw1.append(vals[0][0]); rd1.append(vals[0][1])
        rw2.append(vals[1][0]); rd2.append(vals[1][1])
        print('%.3f   | %7.3f / %7.3f | %7.3f / %7.3f'
              % ((a + b) / 2, vals[0][0], vals[0][1], vals[1][0], vals[1][1]))
    print('mean  width %.4f depth %.4f   ->   width %.4f depth %.4f'
          % (np.nanmean(rw1), np.nanmean(rd1), np.nanmean(rw2), np.nanmean(rd2)))

    print('\n=== hand gap (mesh centroid of hand verts vs the rig hand joint) ===')
    for lab, ours, dom in (('scale 1.00', ours1, dom1), ('scale %.2f' % scale, ours2, dom2)):
        for nm in HAND_BONES:
            sel = ours[dom == nm]
            if not len(sel):
                continue
            c = sel.mean(0)
            print('   %-10s %s centroid (%+.3f %+.3f %+.3f)  joint (%+.3f %+.3f %+.3f)'
                  '  gap %.3f m' % (lab, nm, c[0], c[1], c[2], rig[nm][0],
                                    rig[nm][1], rig[nm][2],
                                    float(np.linalg.norm(c - rig[nm]))))

    print('\n=== global ===')
    for lab, ours in (('scale 1.00', ours1), ('scale %.2f' % scale, ours2)):
        print('   %-10s height %.4f  z[%+.3f,%+.3f]  x[%+.3f,%+.3f]  y[%+.3f,%+.3f]'
              % (lab, ours[:, 2].max() - ours[:, 2].min(), ours[:, 2].min(),
                 ours[:, 2].max(), ours[:, 0].min(), ours[:, 0].max(),
                 ours[:, 1].min(), ours[:, 1].max()))
    print('   reference  height %.4f' % (ref_t[:, 2].max() - ref_t[:, 2].min()))


if __name__ == '__main__':
    main()
