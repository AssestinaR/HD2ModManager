"""Round 45: pick `arm_perp` by comparing the arm cloud with the REFERENCE's.

The reference patch's sleeve is the target the user is judging against, and its
arm sits essentially on HD2's bones (its y-extent matches the finger bones').  So
for arm_perp in {0, 0.25, 0.5, 1} this prints our arm cloud's x/y/z extents next
to the reference's, plus the elbow/fingertip error along the bone axis.
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H
import patch_view as PV
import export_obj as E

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
REF = 'out/patchdump/unit-91eb7be84016ceab.obj'
UP = ['l_shoulder', 'l_shoulder_twist', 'l_shoulderarmour']
FG = ['l_%s_finger%d' % (f, i)
      for f in ('index', 'middle', 'ring', 'pinky', 'thumb') for i in (1, 2, 3)]


def main():
    rig = E.read_hd2_rig(TOC, BH)
    head = {h['name']: np.array([h['world'][3], h['world'][7], h['world'][11]])
            for h in rig if h['name']}
    sh, hd = head['l_shoulder'], head['l_hand']
    u = (hd - sh) / np.linalg.norm(hd - sh)
    L = float(np.linalg.norm(hd - sh))
    be = float((head['l_elbow'] - sh) @ u)
    bf = float((head['l_middle_finger3'] - sh) @ u)

    rp, _t = PV.read_obj(REF)
    R = np.asarray(rp, float)
    print('%-9s %-28s %-28s %8s %8s' % ('arm_perp', 'x[y]z extent (ours)',
                                        'ref extent', 'elbow', 'tip'))
    print('%-9s %-28s %-28s %+8.4f %+8.4f' % ('BONE', '', '', be, bf))
    print('%-9s %-28s %-28s' % ('REF', '', 'x[%+.3f,%+.3f] y[%+.3f,%+.3f] z[%+.3f,%+.3f]'
                                % (R[:, 0].min(), R[:, 0].max(), R[:, 1].min(),
                                   R[:, 1].max(), R[:, 2].min(), R[:, 2].max())))
    for ap in (0.0, 0.25, 0.5, 1.0):
        r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                       spine_map='mesh', fallback='region', leg_tilt=5.7,
                       leg_splay=4.0, mesh_shift=(0.0, -0.100, -0.060),
                       upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                       arm_fit=1.0, arm_perp=ap, verbose=False)
        res, verts, out = r['resolved'], r['verts'], r['out']
        by = {h['index']: h['name'] for h in r['hd2'] if h['name']}
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
            dom.append((by.get(best), out[k]))
        P = np.asarray([p for n, p in dom
                        if n and n.startswith('l_') and H.is_arm_bone(n)], float)
        Pu = np.asarray([p for n, p in dom if n in UP], float)
        Pf = np.asarray([p for n, p in dom if n in FG], float)
        te = float(((Pu - sh) @ u).max())
        tf = float(((Pf - sh) @ u).max())
        print('%-9.2f x[%+.3f,%+.3f] y[%+.3f,%+.3f] z[%+.3f,%+.3f]  %+8.4f %+8.4f'
              % (ap, P[:, 0].min(), P[:, 0].max(), P[:, 1].min(), P[:, 1].max(),
                 P[:, 2].min(), P[:, 2].max(), te, tf))


if __name__ == '__main__':
    main()
