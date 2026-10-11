"""Which joint tears, and by how much?

`out_idle` -- the pose the game actually shows -- is `T[b] . p` with
`T[b] = rest_hd2[b] . inv(rest_sym[b])`, blended by weight.  If every bone moved
by the same transform nothing would tear; the tearing lives in the DIFFERENCE
between a bone's transform and its parent's.

So for each bone measure

    local[b] = T[b] . inv(T[parent])          -- how the joint shears
    head_slip[b] = | T[b](head_b) - T[parent](head_b) |

`head_slip` is the one that matters: it is literally how far the bone's own
joint is dragged away from where the parent's transform would have put it, i.e.
the length over which the mesh is stretched at that joint.
"""
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


def ang(M):
    t = np.trace(M[:3, :3])
    return np.degrees(np.arccos(np.clip((t - 1) / 2.0, -1, 1)))


def main():
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                   spine_map='mesh', fallback='distance', leg_tilt=5.7,
                   leg_splay=4.0, verbose=False)
    hd2 = r['hd2']
    by_idx = {h['index']: h for h in hd2}
    rest_sym, rest_hd2 = r['rest'], r['rest_hd2']
    T = {i: rest_hd2[i] @ np.linalg.inv(rest_sym[i]) for i in rest_hd2}

    rows = []
    for h in hd2:
        i, p = h['index'], h.get('parent')
        if i not in T:
            continue
        if p is None or p == i or p not in T:
            continue
        head = rest_sym[i][:3, 3]
        a = (T[i] @ np.append(head, 1.0))[:3]
        b = (T[p] @ np.append(head, 1.0))[:3]
        loc = T[i] @ np.linalg.inv(T[p])
        rows.append((float(np.linalg.norm(a - b)), h['name'],
                     ang(loc[:3, :3]), float(np.linalg.norm(loc[:3, 3])),
                     float(np.linalg.norm(a - head))))
    rows.sort(key=lambda x: -x[0])

    print('joints ranked by head_slip (how far the joint is dragged relative to'
          ' its parent):')
    print('%-22s %9s %10s %10s %10s' % ('bone', 'head_slip', 'local rot', 'local tr', 'abs move'))
    for hs, nm, ra, tl, am in rows[:26]:
        print('%-22s %9.4f %9.1f d %9.4f %9.4f' % (nm, hs, ra, tl, am))
    hs = np.array([x[0] for x in rows])
    print('\n%d joints; head_slip mean %.4f  median %.4f  p90 %.4f  max %.4f'
          % (len(hs), hs.mean(), np.median(hs), np.percentile(hs, 90), hs.max()))
    for thr in (0.02, 0.05, 0.10):
        print('  joints with head_slip > %.2f m: %d' % (thr, int((hs > thr).sum())))


if __name__ == '__main__':
    main()
