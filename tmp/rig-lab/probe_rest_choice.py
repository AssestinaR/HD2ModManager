"""Which rest does the mesh actually get bound to, and how far is it from HD2's?

`retarget` returns `rest=rest_sym` (HD2's rest hierarchy with the heads snapped
onto the source joints), but the comment on `out_idle` says the shipped rest is
`S_A = D * rest_sym`.  Those are very different objects: `rest_sym` still has the
source's T-POSE head positions, whereas D is the T->A conversion that the mesh
`out` has already been baked with.  So one of the two puts the bones where the
mesh thinks they are and the other does not.

This prints, per bone, the head of each candidate and its distance from HD2's
own rest head -- i.e. how far the game will carry that piece of mesh at idle.
"""
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

WATCH = ['hips', 'spine1', 'spine2', 'chest', 'neck', 'head', 'head_tip',
         'l_clavicle', 'l_shoulder', 'l_elbow', 'l_hand', 'l_index_finger1',
         'l_thigh', 'l_knee', 'l_foot', 'l_ball', 'l_toe']


def main():
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                   spine_map='mesh', fallback='distance', leg_tilt=5.7,
                   leg_splay=4.0, verbose=False)
    hd2 = PV.hd2_rig_heads(TOC, BH)
    by_name = {h['name']: h['index'] for h in r['hd2'] if h['name']}
    rest_sym, D, rest_hd2 = r['rest'], r['D'], r['rest_hd2']

    def head(M):
        return M[:3, 3]

    print('%-18s | %-24s | %-24s | %-24s' % ('bone', 'rest_sym (returned)',
                                             'D*rest_sym', 'HD2 native'))
    print('%-18s | %8s %8s | %8s %8s | %8s %8s'
          % ('', 'z', 'dist', 'z', 'dist', 'z', 'dist'))
    tot = {'sym': 0.0, 'da': 0.0}
    n = 0
    for nm in WATCH:
        i = by_name.get(nm)
        if i is None or i not in rest_sym or i not in rest_hd2:
            continue
        a = head(rest_sym[i])
        b = head(D[i] @ rest_sym[i])
        c = head(rest_hd2[i])
        da, db = np.linalg.norm(a - c), np.linalg.norm(b - c)
        print('%-18s | %8.4f %8.4f | %8.4f %8.4f | %8.4f %8s'
              % (nm, a[2], da, b[2], db, c[2], '-'))
        tot['sym'] += da
        tot['da'] += db
        n += 1
    print('\nmean over %d watched bones:  rest_sym %.4f m   D*rest_sym %.4f m'
          % (n, tot['sym'] / n, tot['da'] / n))

    # all bones
    for label, get in (('rest_sym', lambda i: head(rest_sym[i])),
                       ('D*rest_sym', lambda i: head(D[i] @ rest_sym[i]))):
        ds = [np.linalg.norm(get(i) - head(rest_hd2[i]))
              for i in rest_hd2 if i in D]
        ds = np.asarray(ds)
        print('%-12s over %d bones: mean %.4f  median %.4f  max %.4f'
              % (label, len(ds), ds.mean(), np.median(ds), ds.max()))

    # and: where does the SOURCE mesh's own joint sit, for scale?
    src = r['src1']
    print('\nfor scale: our aligned source mesh bbox z [%.3f, %.3f]'
          % (src[:, 2].min(), src[:, 2].max()))


if __name__ == '__main__':
    main()
