"""Round 45, the decisive measurement: slice the arm along HD2's own axis.

Both meshes are cut into slices along the HD2 `l_shoulder -> l_hand` bone axis,
and for each slice we take the centroid and express it in the axis frame
(t along the bone, s1/s2 perpendicular).  Two things fall straight out:

  * where the arm ENDS   (t_max -- "our hand stops 7 cm short")
  * whether it is BENT   (how s1/s2 drift as t grows -- a straight limb keeps
                          them near constant, a bent one swings)

The reference sleeve is measured the same way, so ours-vs-reference is
apples to apples: same axis, same slices.
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
REF = {'l': 'out/patchdump/unit-91eb7be84016ceab.obj',
       'r': 'out/patchdump/unit-7336c6b662522f0e.obj'}


def arm_cloud(r, side):
    res, verts, out = r['resolved'], r['verts'], r['out']
    by_idx_name = {h['index']: h['name'] for h in r['hd2'] if h['name']}
    keep = set(['%s_clavicle' % side, '%s_shoulder' % side, '%s_shoulder_twist' % side,
                '%s_shoulderarmour' % side, '%s_elbow' % side, '%s_hand' % side,
                '%s_hand_twist' % side] +
               ['%s_%s_finger%d' % (side, f, i)
                for f in ('index', 'middle', 'pinky', 'ring', 'thumb')
                for i in (1, 2, 3)])
    pts = []
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
        if by_idx_name.get(best) in keep:
            pts.append(out[k])
    return np.asarray(pts, float)


def slices(P, o, u, p1, p2, n=14, tmax=1.35):
    """centroid of each slice along the axis, in the (t, s1, s2) frame."""
    rel = P - o
    t = rel @ u
    s1 = rel @ p1
    s2 = rel @ p2
    out = []
    edges = np.linspace(0.0, tmax, n + 1)
    for a, b in zip(edges, edges[1:]):
        m = (t >= a) & (t < b)
        if m.sum() < 20:
            out.append((0.5 * (a + b), None, None, int(m.sum())))
            continue
        out.append((0.5 * (a + b), float(s1[m].mean()), float(s2[m].mean()), int(m.sum())))
    return out, t, s1, s2


def main():
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                   spine_map='mesh', fallback='region', leg_tilt=5.7,
                   leg_splay=4.0, mesh_shift=(0.0, -0.100, -0.060),
                   upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                   verbose=False)
    rig = E.read_hd2_rig(TOC, BH)
    head = {h['name']: np.array([h['world'][3], h['world'][7], h['world'][11]])
            for h in rig if h['name']}

    for side in ('l', 'r'):
        sh, hd = head['%s_shoulder' % side], head['%s_hand' % side]
        u = hd - sh
        L = float(np.linalg.norm(u))
        u = u / L
        up = np.array([0.0, 0.0, 1.0])
        p1 = np.cross(u, up)
        p1 /= max(np.linalg.norm(p1), 1e-9)
        p2 = np.cross(u, p1)

        ours = arm_cloud(r, side)
        ref_pts, _t = PV.read_obj(REF[side])
        ref = np.asarray(ref_pts, float)

        print('\n============ %s arm  (axis = shoulder->hand, bone length %.4f m) '
              '============' % (side.upper(), L))
        for tag, P in (('OURS', ours), ('REF ', ref)):
            so, t, s1, s2 = slices(P, sh, u, p1, p2)
            print('  %s  t along bone: [%+.3f, %+.3f] m  = [%+.2f, %+.2f] x bone len'
                  '   (perp rms %.4f m, N=%d)'
                  % (tag, t.min(), t.max(), t.min() / L, t.max() / L,
                     float(np.sqrt((s1 ** 2 + s2 ** 2).mean())), len(P)))
            print('       slice centres (t m | s1 mm | s2 mm):')
            row = []
            for c, a, b, n in so:
                row.append('%4.2f|%+5.1f|%+5.1f' % (c, 1000 * a, 1000 * b)
                           if a is not None else '%4.2f|  --  ' % c)
            for i in range(0, len(row), 7):
                print('         ' + '   '.join(row[i:i + 7]))

        # the same comparison in the two views the user used
        for tag, P in (('OURS', ours), ('REF ', ref)):
            rel = P - sh
            t = rel @ u
            s1 = rel @ p1
            s2 = rel @ p2
            print('  %s  far end (t_max): t %+.4f m  s1 %+.4f  s2 %+.4f  -> world '
                  '(%+.4f, %+.4f, %+.4f)'
                  % (tag, t.max(), s1[np.argmax(t)], s2[np.argmax(t)],
                     *P[np.argmax(t)]))


if __name__ == '__main__':
    main()
