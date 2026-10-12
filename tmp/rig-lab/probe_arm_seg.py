"""Round 45: where is our mesh's ELBOW along HD2's arm axis?

The user's claim is "our upper arm is a bit short, so the elbow (and everything
past it) sits inboard of the bone corner".  This prints the t-extent of each arm
SEGMENT's vertex cloud along HD2's own `shoulder -> hand` axis, next to where
HD2's elbow/wrist/finger bones actually are.
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H
import export_obj as E

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')

GROUPS = {
    'upper arm  (sh bone)': ['{s}_shoulder', '{s}_shoulder_twist', '{s}_shoulderarmour'],
    'forearm    (el bone)': ['{s}_elbow'],
    'palm       (hand)': ['{s}_hand', '{s}_hand_twist'],
    'fingers': ['{s}_%s_finger%d' % (f, i)
                for f in ('index', 'middle', 'ring', 'pinky', 'thumb')
                for i in (1, 2, 3)],
}


def main():
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                   spine_map='mesh', fallback='region', leg_tilt=5.7,
                   leg_splay=4.0, mesh_shift=(0.0, -0.100, -0.060),
                   upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                   verbose=False)
    rig = E.read_hd2_rig(TOC, BH)
    head = {h['name']: np.array([h['world'][3], h['world'][7], h['world'][11]])
            for h in rig if h['name']}
    res, verts, out = r['resolved'], r['verts'], r['out']
    by_idx_name = {h['index']: h['name'] for h in r['hd2'] if h['name']}
    dom = []
    for k, v in enumerate(verts):
        best, bw = None, -1.0
        for j in range(3):
            w = v['w'][j]
            if w <= bw:
                continue
            hb = res.get(v['bones'][j])
            if hb is None:
                continue
            bw, best = w, hb
        dom.append((by_idx_name.get(best), out[k]))

    for side in ('l', 'r'):
        sh, hd = head['%s_shoulder' % side], head['%s_hand' % side]
        u = hd - sh
        L = float(np.linalg.norm(u))
        u = u / L
        print('\n======== %s arm : t along shoulder->hand (bone %.4f m) ========'
              % (side.upper(), L))
        for nm in ('%s_clavicle' % side, '%s_shoulder' % side, '%s_shoulder_twist' % side,
                   '%s_elbow' % side, '%s_hand' % side, '%s_index_finger1' % side,
                   '%s_index_finger3' % side, '%s_middle_finger3' % side):
            if nm in head:
                print('   BONE  t %+.4f (%+5.2f x bone)  %s'
                      % (float((head[nm] - sh) @ u),
                         float((head[nm] - sh) @ u) / L, nm))
        print('   --')
        for label, pats in GROUPS.items():
            names = set(p.format(s=side) for p in pats)
            P = np.asarray([p for n, p in dom if n in names], float)
            if not len(P):
                continue
            t = (P - sh) @ u
            print('   MESH  %-22s N=%-6d t [%+.4f, %+.4f]  mean %+.4f  (%+5.2f..%+5.2f, '
                  'mean %+5.2f x bone)'
                  % (label, len(P), t.min(), t.max(), t.mean(),
                     t.min() / L, t.max() / L, t.mean() / L))


if __name__ == '__main__':
    main()
