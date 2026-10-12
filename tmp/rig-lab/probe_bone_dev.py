"""Where does our shipped rest differ from HD2's own rest?

Our `rest_sym` keeps HD2's bone ORIENTATIONS but moves every mapped head onto
the aligned source joint.  Every one of those moves is a place where the game's
animations -- which are authored against HD2's rest -- will deform our mesh
differently from HD2's.  So the list, sorted by distance, is the shortlist of
things worth fixing BEFORE touching anything as delicate as the hands.

Also flags the bones that have no source counterpart and are filled in
synthetically (spine1, chest, the *_twist / *_shoulderarmour / *_toe / head_tip
bones), because those are guesses, not measurements.
"""
import json
import math
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
SPLAY, TILT = 4.0, 5.7

SYNTH = {'spine1', 'chest', 'l_shoulder_twist', 'r_shoulder_twist',
         'l_hand_twist', 'r_hand_twist', 'l_shoulderarmour', 'r_shoulderarmour',
         'l_toe', 'r_toe', 'head_tip'}


def main():
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                   spine_map='mesh', fallback='region', leg_tilt=TILT,
                   leg_splay=SPLAY, verbose=False)
    # the rest the mesh `out` is actually bound to: D * rest_sym.  The bare
    # `rest_sym` still has the source's T-posed head positions, so measuring
    # against it overstates the hand gap by 23 cm.
    ours = {h['name']: tuple((r['D'][h['index']] @ r['rest'][h['index']])[:3, 3])
            for h in r['hd2'] if h['name'] and h['index'] in r['rest']}
    hd2 = PV.hd2_rig_heads(TOC, BH)

    rows = []
    for nm, p in ours.items():
        q = hd2.get(nm)
        if q is None:
            continue
        d = np.asarray(p) - np.asarray(q)
        rows.append((float(np.linalg.norm(d)), nm, d, p, q))
    rows.sort(reverse=True)

    print('our rest vs HD2 native rest  (splay %.2f, tilt %.2f)' % (SPLAY, TILT))
    print('%-22s %7s %8s %8s %8s %8s %s'
          % ('bone', 'dist', 'dx', 'dy', 'dz', 'hd2 z', 'note'))
    for d, nm, dv, p, q in rows:
        if d < 0.005:
            continue
        print('%-22s %7.4f %+8.4f %+8.4f %+8.4f %8.4f %s'
              % (nm, d, dv[0], dv[1], dv[2], q[2],
                 'SYNTHETIC (no source bone)' if nm in SYNTH else ''))
    print('\nwithin 5 mm: %d of %d' % (sum(1 for x in rows if x[0] < 0.005), len(rows)))

    # segment direction differences along the arm and the spine
    print('\nsegment direction (parent -> child), front-view and side-view angle:')
    chains = [('spine', ['hips', 'spine1', 'spine2', 'chest', 'neck', 'head']),
              ('left arm', ['l_clavicle', 'l_shoulder', 'l_elbow', 'l_hand']),
              ('left leg', ['l_thigh', 'l_knee', 'l_foot'])]
    for label, ch in chains:
        print('  %s:' % label)
        for a, b in zip(ch, ch[1:]):
            if a not in ours or b not in ours or a not in hd2 or b not in hd2:
                continue
            va = np.asarray(ours[b]) - np.asarray(ours[a])
            vb = np.asarray(hd2[b]) - np.asarray(hd2[a])
            la, lb = np.linalg.norm(va), np.linalg.norm(vb)
            ang = math.degrees(math.acos(np.clip(
                float(va @ vb) / max(la * lb, 1e-9), -1, 1)))
            print('    %-12s -> %-12s  len %6.4f / %6.4f  dir %.1f deg  '
                  'front %+6.1f/%+6.1f  side %+6.1f/%+6.1f'
                  % (a, b, la, lb, ang,
                     math.degrees(math.atan2(va[0], -va[2])),
                     math.degrees(math.atan2(vb[0], -vb[2])),
                     math.degrees(math.atan2(va[1], -va[2])),
                     math.degrees(math.atan2(vb[1], -vb[2]))))


if __name__ == '__main__':
    main()
