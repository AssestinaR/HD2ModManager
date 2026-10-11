"""Does republishing the rest as HD2's OWN rest change anything when animated?

Option A (current):          rest = D*rest_sym, mesh = out
Option B (reference-style):  rest = rest_hd2,   mesh = out_idle

Claim: for ANY animated pose A both produce the same world mesh, because

  A @ inv(rest_sym) @ out      and      A @ inv(rest_hd2) @ out_idle

are the same expression once you substitute out = D*rest_sym*src1 and
out_idle = rest_hd2 @ inv(D*rest_sym) @ out.

If true, Option B is free: identical animation, but the BIND pose (hence any
static preview) already stands in HD2's stance -- which is exactly what the
reference patch author shipped (their skeleton is HD2's rest, verbatim).

Tested with a deliberately non-trivial A: random rotations on torso/leg/arm.
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H

TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
MODEL = 'survivor_teenangst'


def skin(P, verts, boneM):
    out = np.empty_like(P)
    for k, v in enumerate(verts):
        acc = np.zeros(3)
        tot = 0.0
        p4 = np.append(P[k], 1.0)
        for j in range(3):
            w = v['w'][j]
            if w <= 0:
                continue
            M = boneM.get(v['bones'][j])
            if M is None:
                M = np.eye(4)
            acc += w * (M @ p4)[:3]
            tot += w
        out[k] = acc / tot if tot > 0 else P[k]
    return out


def main():
    r = H.retarget(VPK, MODEL, TOC, BH, PP, align='yaw', spine_map='mesh',
                   fallback='distance', verbose=False)
    hd2, rest_hd2, resolved = r['hd2'], r['rest_hd2'], r['resolved']
    verts, rest_sym, D = r['verts'], r['rest'], r['D']
    out, out_idle = np.asarray(r['out']), np.asarray(r['out_idle'])

    # bone matrix the pipeline would use for a vertex's slot j
    def M_of(boneM, b):
        return boneM.get(resolved.get(b), np.eye(4))

    # ---- a deliberately non-trivial animation ----------------------------
    rng = np.random.default_rng(7)
    targets = ('spine1', 'spine2', 'chest', 'neck', 'head',
               'l_thigh', 'l_knee', 'l_foot', 'r_thigh', 'r_knee',
               'l_shoulder', 'l_elbow', 'r_shoulder', 'r_elbow', 'hips')
    A = {}
    used = []
    for h in hd2:
        M = rest_hd2[h['index']].copy()
        if h['name'] in targets:
            ang = float(rng.uniform(-0.5, 0.5))
            ax = rng.normal(size=3)
            ax /= np.linalg.norm(ax)
            K = np.array([[0, -ax[2], ax[1]], [ax[2], 0, -ax[0]], [-ax[1], ax[0], 0]])
            R = np.eye(4)
            R[:3, :3] = np.eye(3) + np.sin(ang) * K + (1 - np.cos(ang)) * (K @ K)
            c = M[:3, 3].copy()
            A[h['index']] = H.trans(c) @ R @ H.trans(-c) @ M
            used.append(h['name'])
        else:
            A[h['index']] = M
    print('animation rotates: %s' % ', '.join(used))

    # ---- Option A: rest = D*rest_sym, mesh = out ------------------------
    MA = {h['index']: A[h['index']] @ np.linalg.inv(D[h['index']] @ rest_sym[h['index']])
          for h in hd2}
    # vertices of `out` are indexed by the SAME source bone indices
    resA = np.empty_like(out)
    for k, v in enumerate(verts):
        acc = np.zeros(3)
        tot = 0.0
        p4 = np.append(out[k], 1.0)
        for j in range(3):
            w = v['w'][j]
            if w <= 0:
                continue
            acc += w * (M_of(MA, v['bones'][j]) @ p4)[:3]
            tot += w
        resA[k] = acc / tot if tot > 0 else out[k]

    # ---- Option B: rest = rest_hd2, mesh = out_idle ----------------------
    MB = {h['index']: A[h['index']] @ np.linalg.inv(rest_hd2[h['index']])
          for h in hd2}
    resB = np.empty_like(out_idle)
    for k, v in enumerate(verts):
        acc = np.zeros(3)
        tot = 0.0
        p4 = np.append(out_idle[k], 1.0)
        for j in range(3):
            w = v['w'][j]
            if w <= 0:
                continue
            acc += w * (M_of(MB, v['bones'][j]) @ p4)[:3]
            tot += w
        resB[k] = acc / tot if tot > 0 else out_idle[k]

    d = np.linalg.norm(resA - resB, axis=1)
    print('\nA vs B under a non-idle pose:  mean %.3e  p99 %.3e  max %.3e  (metres)'
          % (d.mean(), np.percentile(d, 99), d.max()))

    # ---- and the bind poses themselves ----------------------------------
    print('bind-pose standing height: A %.4f m   B %.4f m'
          % (out[:, 2].max() - out[:, 2].min(),
             out_idle[:, 2].max() - out_idle[:, 2].min()))
    for tag, P in (('A  (rest=D*rest_sym, mesh=out)', out),
                   ('B  (rest=rest_hd2,   mesh=out_idle)', out_idle)):
        zmin = P[:, 2].min()
        m = P[(P[:, 2] < zmin + 0.08) & (P[:, 0] < -0.05)]
        ylo, yhi = float(m[:, 1].min()), float(m[:, 1].max())
        hipy = -0.0190 if tag.startswith('B') else float(r['new_head']['l_thigh'][1])
        print('%-40s foot y [%+.4f,%+.4f]  hip at %.1f%% from heel'
              % (tag, ylo, yhi, 100.0 * (hipy - ylo) / (yhi - ylo)))


if __name__ == '__main__':
    main()
