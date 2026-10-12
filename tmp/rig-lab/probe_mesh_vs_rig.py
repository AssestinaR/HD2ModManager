"""Where is the MESH relative to the rig we ship?

With `skeleton='native'` the rig is HD2's own, so any remaining discrepancy is a
MESH placement problem -- exactly what the next round has to fix.  Two views:

1. for each HD2 bone, the weighted centroid of the mesh vertices it dominates vs
   the bone's own joint (both in the shipped frame);
2. the shipped joint positions, ours vs the reference, normalised by each body's
   own height so a size difference does not masquerade as a placement error.
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
WATCH = ('hips', 'spine1', 'spine2', 'chest', 'neck', 'head',
         'l_clavicle', 'l_shoulder', 'l_elbow', 'l_hand',
         'r_shoulder', 'r_elbow', 'r_hand',
         'l_thigh', 'l_knee', 'l_foot', 'l_ball', 'l_toe')


def centroids(pts, verts, resolved, idx2name):
    acc, wsum = {}, {}
    for k, v in enumerate(verts):
        j = int(np.argmax(v['w']))
        if v['w'][j] <= 0:
            continue
        nm = idx2name.get(resolved.get(v['bones'][j]))
        if nm is None:
            continue
        acc.setdefault(nm, np.zeros(3))
        wsum[nm] = wsum.get(nm, 0.0) + 1.0
        acc[nm] += np.asarray(pts[k])
    return {n: acc[n] / wsum[n] for n in acc}


def main():
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                   spine_map='mesh', fallback='distance', leg_tilt=5.7,
                   leg_splay=4.0, skeleton='native', verbose=False)
    out = np.asarray(r['out'])
    idx2name = {h['index']: h['name'] for h in r['hd2']}
    cen = centroids(out, r['verts'], r['resolved'], idx2name)
    rig = {h['name']: np.asarray(r['rest_hd2'][h['index']][:3, 3])
           for h in r['hd2'] if h['name']}
    h_ours = float(out[:, 2].max() - out[:, 2].min())

    names = PV.read_bone_names(BH)
    meta = json.load(open('out/patchdump/units.json', encoding='utf-8'))
    pts, base = [], 0
    for u in meta:
        fid = u['unitFileId'][2:].lower()
        if fid not in REF:
            continue
        p, t = PV.read_obj('out/patchdump/unit-%s.obj' % fid)
        if t:
            pts.extend(p)
    pts = np.asarray(pts)
    h_ref = float(pts[:, 2].max() - pts[:, 2].min())
    ref_rig = {k: np.asarray(v) for k, v in PV.patch_rig_heads(meta, names).items()}

    print('body height:  ours %.4f m   reference %.4f m   ratio %.4f'
          % (h_ours, h_ref, h_ref / h_ours))
    print('mesh sole z:   ours %.4f   reference %.4f' % (out[:, 2].min(),
                                                         pts[:, 2].min()))
    print()
    print('1) mesh centroid minus the joint it is bound to, in units of our height')
    print('%-13s %9s %9s %9s' % ('bone', 'dx', 'dy', 'dz'))
    for nm in WATCH:
        if nm not in cen or nm not in rig:
            continue
        d = (cen[nm] - rig[nm]) / h_ours
        print('%-13s %+9.4f %+9.4f %+9.4f' % (nm, d[0], d[1], d[2]))

    print()
    print('2) shipped joint positions / body height -- ours vs the reference')
    print('%-13s %-24s %-24s %s' % ('bone', 'ours', 'reference', 'ours - ref'))
    for nm in WATCH:
        if nm not in rig:
            continue
        o = rig[nm] / h_ours
        s = '%-13s (%+.4f %+.4f %+.4f)' % (nm, o[0], o[1], o[2])
        if nm in ref_rig:
            q = ref_rig[nm] / h_ref
            s += '   (%+.4f %+.4f %+.4f)   (%+.4f %+.4f %+.4f)' % (
                q[0], q[1], q[2], o[0] - q[0], o[1] - q[1], o[2] - q[2])
        print(s)


if __name__ == '__main__':
    main()
