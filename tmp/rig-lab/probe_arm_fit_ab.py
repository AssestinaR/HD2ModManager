"""Round 45: does the arm re-fit land the joints on HD2's bones?  A/B it.

Runs the pipeline with `arm_fit=0` and `arm_fit=1` and prints, for each arm
segment, the t-extent of its vertex cloud along HD2's `shoulder -> hand` axis --
next to where HD2's elbow / wrist / fingertip bones actually are.
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
    'upper arm': ['{s}_shoulder', '{s}_shoulder_twist', '{s}_shoulderarmour'],
    'forearm  ': ['{s}_elbow'],
    'palm     ': ['{s}_hand', '{s}_hand_twist'],
    'fingers  ': ['{s}_%s_finger%d' % (f, i)
                  for f in ('index', 'middle', 'ring', 'pinky', 'thumb')
                  for i in (1, 2, 3)],
}
BASE = dict(align='yaw', spine_map='mesh', fallback='region', leg_tilt=5.7,
            leg_splay=4.0, mesh_shift=(0.0, -0.100, -0.060),
            upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45, verbose=False)


def run(arm_fit):
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, arm_fit=arm_fit, **BASE)
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
    return r, dom


def main():
    rig = E.read_hd2_rig(TOC, BH)
    head = {h['name']: np.array([h['world'][3], h['world'][7], h['world'][11]])
            for h in rig if h['name']}

    for side in ('l', 'r'):
        sh, hd = head['%s_shoulder' % side], head['%s_hand' % side]
        u = hd - sh
        L = float(np.linalg.norm(u))
        u = u / L
        print('\n########## %s arm  (bone %.4f m) ##########' % (side.upper(), L))
        print('  BONE  elbow  t %+.4f (%+5.2f) | wrist t %+.4f (%+5.2f) | '
              'index_f3 t %+.4f (%+5.2f) | middle_f3 t %+.4f (%+5.2f)'
              % (float((head['%s_elbow' % side] - sh) @ u),
                 float((head['%s_elbow' % side] - sh) @ u) / L,
                 float((head['%s_hand' % side] - sh) @ u),
                 float((head['%s_hand' % side] - sh) @ u) / L,
                 float((head['%s_index_finger3' % side] - sh) @ u),
                 float((head['%s_index_finger3' % side] - sh) @ u) / L,
                 float((head['%s_middle_finger3' % side] - sh) @ u),
                 float((head['%s_middle_finger3' % side] - sh) @ u) / L))
        for arm_fit in (0.0, 1.0):
            r, dom = run(arm_fit)
            print('  --- arm_fit = %.1f ---' % arm_fit)
            for label, pats in GROUPS.items():
                names = set(p.format(s=side) for p in pats)
                P = np.asarray([p for n, p in dom if n in names], float)
                if not len(P):
                    continue
                t = (P - sh) @ u
                print('    MESH %s  t [%+.4f, %+.4f] mean %+.4f  (%+5.2f..%+5.2f)'
                      % (label, t.min(), t.max(), t.mean(), t.min() / L, t.max() / L))


if __name__ == '__main__':
    main()
