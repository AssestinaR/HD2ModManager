"""Round 45b: how much of the TORSO does the arm re-fit drag along?

The user's "the armpit seam split open" has one mechanism that can produce it:
arm_fit displacing chest/armpit vertices, which the source's weights hand to
`l_shoulder`.  This counts them, for the round-45 build and the round-45b build.

A vertex counts as TORSO when the HD2 bone that dominates its skinning weights is
not an arm bone -- i.e. geometry that has no business following the arm.

usage: python probe_arm_tear.py
"""
import os
import sys
from collections import Counter

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')


def run(**kw):
    base = dict(align='yaw', spine_map='mesh', fallback='region', leg_tilt=5.7,
                leg_splay=4.0, mesh_shift=(0.0, -0.100, -0.060),
                upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                verbose=False)
    base.update(kw)
    return H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, **base)


def main():
    off = run(arm_fit=0.0)
    old = run(arm_fit=1.0, arm_up=0.0, arm_perp=0.0, arm_taper=None)
    new = run(arm_fit=1.0)
    P0 = np.asarray(off['out'], float)
    Pold = np.asarray(old['out'], float)
    Pnew = np.asarray(new['out'], float)

    by_name = {h['index']: h['name'] for h in off['hd2'] if h['name']}
    res = off['resolved']
    verts = off['verts']
    dom = []
    for v in verts:
        best, bw = None, -1.0
        for j in range(3):
            w = v['w'][j]
            if w <= bw:
                continue
            best, bw = res.get(v['bones'][j]), w
        dom.append(by_name.get(best, ''))
    dom = np.array(dom)
    is_arm = np.array([H.is_arm_bone(n) for n in dom])
    # the shoulder/armpit box, and the "inboard of the shoulder joint" test
    a = np.array(off['rest_shipped'][next(h['index'] for h in off['hd2']
                                          if h['name'] == 'l_shoulder')][:3, 3], float)
    b = np.array(off['rest_shipped'][next(h['index'] for h in off['hd2']
                                          if h['name'] == 'l_hand')][:3, 3], float)
    u = (b - a) / np.linalg.norm(b - a)
    t = (P0 - a) @ u
    torso = ~is_arm

    print('%-34s %8s %8s %8s %8s' %
          ('', 'n(>2cm)', 'n(>5cm)', 'max(m)', 'mean(m)'))
    for label, P in (('round-45  (no taper)', Pold),
                     ('round-45b (taper+arm_up)', Pnew)):
        d = np.linalg.norm(P - P0, axis=1)
        for tag, sel in (('ALL torso-dominant verts', torso),
                         ('  ... AND t < +0.05 (shoulder band)', torso & (t < 0.05)),
                         ('  ... AND x < -0.05 (LEFT side only)', torso & (P0[:, 0] < -0.05)),
                         ('  ... AND t<+0.05 AND x<-0.05 (L armpit)',
                          torso & (t < 0.05) & (P0[:, 0] < -0.05))):
            dd = d[sel]
            print('%-34s %8d %8d %8.4f %8.4f   [%s]'
                  % (label, int((dd > 0.02).sum()), int((dd > 0.05).sum()),
                     float(dd.max()), float(dd.mean()), tag))
        print('')

    print('sanity: torso-dominant verts total %d of %d; arm-dominant %d'
          % (int(torso.sum()), len(dom), int(is_arm.sum())))
    c = Counter(dom[is_arm & (t < 0.05)])
    print('arm-dominant verts INBOARD of the shoulder joint: %d  top bones %s'
          % (int((is_arm & (t < 0.05)).sum()), c.most_common(5)))


if __name__ == '__main__':
    main()
