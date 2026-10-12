"""Round 45, step 1: separate "our arm is BENT" from "our arm is SHORT".

The centroid probe (`probe_arm_fit.py`) mixes both effects, because a centroid is
a cloud centre and not a limb direction.  This one measures each arm SEGMENT on
its own:

  * cloud  = vertices whose dominant resolved HD2 bone is that segment's bone
             (shoulder bone = upper arm, elbow bone = forearm, hand bone = palm)
  * axis   = principal eigenvector of the cloud  -> the limb's direction
  * extent = projection of the cloud onto the HD2 bone axis, as a fraction of
             the bone length (0 = shoulder/elbow/wrist joint, 1 = the far joint)

so we can say "the forearm cloud runs from t=0.31 to t=1.04 of the bone" or "our
upper arm points 20 deg more forward than the bone does".

Also dumps the same for the reference sleeve unit and for the SOURCE T-pose mesh,
so the comparison is apples to apples.
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

FINGER_SUFFIX = ('_index_finger1', '_middle_finger1', '_pinky_finger1',
                 '_thumb_finger1', '_ring_finger1')


def pca_axis(P):
    Q = P - P.mean(0)
    w, v = np.linalg.eigh(Q.T @ Q)
    return v[:, -1]


def front(u):
    """angle below horizontal in the x-z plane, deg, 0 = horizontal."""
    return math.degrees(math.atan2(-float(u[2]), abs(float(u[0]))))


def top(u):
    """angle in the x-y plane, deg, 0 = +x, ccw (this is the TOP view)."""
    return math.degrees(math.atan2(float(u[1]), float(u[0])))


def cloud_by_bone(r, chain_names):
    """{bone name: (N,3) vertex array} using the dominant resolved HD2 bone."""
    res, verts, out = r['resolved'], r['verts'], r['out']
    by_idx_name = {h['index']: h['name'] for h in r['hd2'] if h['name']}
    buckets = {nm: [] for nm in chain_names}
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
        nm = by_idx_name.get(best)
        if nm in buckets:
            buckets[nm].append(out[k])
    return {nm: np.asarray(p, float) for nm, p in buckets.items() if p}


def report_segment(label, P, head, tail, other_head):
    if P is None or len(P) < 8:
        print('  %-26s (no cloud)' % label)
        return
    u = np.asarray(tail, float) - np.asarray(head, float)
    L = float(np.linalg.norm(u))
    un = u / max(L, 1e-9)
    t = (P - np.asarray(head, float)) @ un / max(L, 1e-9)
    perp = P - (np.asarray(head, float) + np.outer(t * L, un))
    ax = pca_axis(P)
    if float(ax @ un) < 0:
        ax = -ax
    print('  %-26s N=%-6d axis front %+6.1f / bone %+6.1f  top %+7.1f / %+7.1f '
          '| axis-vs-bone %4.1f deg'
          % (label, len(P), front(ax), front(un), top(ax), top(un),
             math.degrees(math.acos(np.clip(float(ax @ un), -1, 1)))))
    print('  %-26s extent t [%+.2f, %+.2f] of bone  (bone len %.4f m)  perp rms '
          '%.4f m  mean |v-head| %.4f'
          % ('', t.min(), t.max(), L, float(np.sqrt((perp ** 2).sum(1).mean())),
             float(np.linalg.norm(P - np.asarray(head, float), axis=1).mean())))


def main():
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                   spine_map='mesh', fallback='region', leg_tilt=5.7,
                   leg_splay=4.0, mesh_shift=(0.0, -0.100, -0.060),
                   upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                   verbose=False)
    head = {h['name']: r['rest_hd2'][h['index']][:3, 3].astype(float)
            for h in r['hd2'] if h['name']}

    for side in ('l', 'r'):
        up, fore, hand = ('%s_shoulder' % side, '%s_elbow' % side,
                          '%s_hand' % side)
        fing = ['%s%s' % (side, s) for s in FINGER_SUFFIX]
        chain = [up, fore, hand] + fing
        cl = cloud_by_bone(r, chain)
        allarm = np.vstack([cl[k] for k in chain if k in cl])
        print('\n================ %s arm  (OURS) ================' % side.upper())
        print('  whole arm cloud: N=%d  x[%+.4f,%+.4f] y[%+.4f,%+.4f] z[%+.4f,%+.4f]'
              % (len(allarm), allarm[:, 0].min(), allarm[:, 0].max(),
                 allarm[:, 1].min(), allarm[:, 1].max(),
                 allarm[:, 2].min(), allarm[:, 2].max()))
        tip = allarm[np.argmax(np.linalg.norm(allarm - head[up], axis=1))]
        print('  farthest point from the shoulder bone head: (%+.4f, %+.4f, %+.4f)'
              % tuple(tip))
        report_segment('upper arm (sh->el)', cl.get(up), head[up], head[fore], None)
        report_segment('forearm (el->wr)', cl.get(fore), head[fore], head[hand], None)
        # hand: bone axis = wrist -> index finger head
        if fing[0] in head:
            report_segment('palm (wr->finger)', cl.get(hand), head[hand],
                           head[fing[0]], None)
        # where does the palm END, along the forearm axis?
        if hand in cl:
            u = (head[hand] - head[fore]) / max(
                float(np.linalg.norm(head[hand] - head[fore])), 1e-9)
            t = (cl[hand] - head[fore]) @ u
            L = float(np.linalg.norm(head[hand] - head[fore]))
            print('  palm along the forearm bone: t [%+.3f, %+.3f] m  (bone %.3f m)'
                  % (t.min(), t.max(), L))

    print('\n================ reference sleeve (Slim), no weights ============')
    for fid, tag, side in (('91eb7be84016ceab', 'LeftArm', 'l'),
                           ('7336c6b662522f0e', 'RightArm', 'r')):
        p, _ = PV.read_obj('out/patchdump/unit-%s.obj' % fid)
        P = np.asarray(p, float)
        up, fore, hand = ('%s_shoulder' % side, '%s_elbow' % side,
                          '%s_hand' % side)
        print('\n  --- %s  (%d verts) ---' % (tag, len(P)))
        report_segment('sleeve as a whole', P, head[up], head[hand], None)

    print('\n================ SOURCE T-pose mesh (before any bake) ===========')
    src = r['src1']
    res, verts = r['resolved'], r['verts']
    by_idx_name = {h['index']: h['name'] for h in r['hd2'] if h['name']}
    for side in ('l', 'r'):
        up, fore = '%s_shoulder' % side, '%s_elbow' % side
        sel = []
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
            if by_idx_name.get(best) == up:
                sel.append(src[k])
        P = np.asarray(sel, float)
        print('  %s upper arm: N=%d' % (side.upper(), len(P)))
        report_segment('%s src upper arm' % side.upper(), P, head[up], head[fore], None)


if __name__ == '__main__':
    main()
