# -*- coding: utf-8 -*-
"""Round 52: how is the ELBOW angle generated, and can a 6.5 deg offset be
simply added on top?

Reads nothing from disk that the pipeline does not already produce: the snapped
joints are in `r['new_head']` (which IS `joint(src_bone)` for every mapped bone,
because `new_head[hd2_name] = joint(src_name)`), and HD2's own rest is in
`r['rest_hd2']`.  So the two rotations the pipeline builds can be rebuilt here,
bit for bit, without touching hd2_style.py:

    R_sh = min_arc_rotation(d_up_src,               d_up_tgt)
    R_el = min_arc_rotation(d_fo_src, R_sh.T @      d_fo_tgt)

and, since the D recursion gives
    D[el] = D[sh] . T(te) . R_el . T(-te),
the world direction of the retargeted forearm is exactly

    d_fo_world = R_sh . R_el . d_fo_src        ( == d_fo_tgt, by construction )

What the user is looking at, though, is the MESH.  So this probe ALSO measures
the mesh's own forearm axis (bin-centroid fit, same method as
probe_arm_angle.py) and reports how far it sits from the bone target -- that
difference is the whole point: the mesh carries the extra `arm_rot` mesh-layer
rotation on top of the bone-level elbow.

usage: python _probe_elbow_axis.py [--deg 6.5]
"""
import argparse
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
MODEL = 'survivor_teenangst'

# the one true combo (mirrors dump_blender.py + HD2_ARM_ROT)
MESH_SHIFT = (0.0, -0.100, -0.090)
HEAD_SCALE = 8.0 / 7.0
ARM_ROT = (10.46, -18.55, 29.63)

SRC = {'l': ('L_UpperArm', 'L_Forearm', 'L_Hand'),
       'r': ('R_UpperArm', 'R_Forearm', 'R_Hand')}
HD2 = {'l': ('l_shoulder', 'l_elbow', 'l_hand'),
       'r': ('r_shoulder', 'r_elbow', 'r_hand')}


def unit(v):
    v = np.asarray(v, float)
    return v / max(float(np.linalg.norm(v)), 1e-12)


def ang_between(a, b):
    return float(np.degrees(np.arccos(np.clip(abs(unit(a) @ unit(b)), -1.0, 1.0))))


def signed_top(a, b):
    """signed turn of `b` relative to `a`, in the X-Y (top) plane, degrees.

    + = counter-clockwise as seen with +X right and +Y up, i.e. the mathematical
    orientation of the X-Y plane.  Looking DOWN at the character from +Z, with
    the character facing +Y, +X is on the character's RIGHT -- which is on the
    viewer's LEFT -- so the viewer's "clockwise" is the opposite sign here.
    Both are printed; the raw value is the honest one."""
    ax, ay = unit(a)[0], unit(a)[1]
    bx, by = unit(b)[0], unit(b)[1]
    return float(np.degrees(np.arctan2(ax * by - ay * bx, ax * bx + ay * by)))


def xz_below(v):
    return float(np.degrees(np.arctan2(abs(v[2]), abs(v[0]))))


def xy_fwd(v):
    return float(np.degrees(np.arctan2(v[1], -v[0])))


def mesh_axis(r, side):
    """bin-centroid direction of the mesh's forearm, t in [elbow, wrist]."""
    import probe_arm_angle as PA
    idx = {h['name']: h['index'] for h in r['hd2']}
    rest = r['rest_shipped']
    a = np.array(rest[idx['%s_shoulder' % side]][:3, 3], float)
    b = np.array(rest[idx['%s_hand' % side]][:3, 3], float)
    u = unit(b - a)
    t_el = float((np.array(rest[idx['%s_elbow' % side]][:3, 3], float) - a) @ u)
    t_wr = float(np.linalg.norm(b - a))
    P, t, _, _ = PA.arm_cloud(r)
    if side == 'r':                      # mirror the right arm onto the left
        P = P.copy()
        P[:, 0] *= -1.0
    d, n = PA.fit_dir(P, t, t_el, t_wr)
    return d, n, t_el, t_wr


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--deg', type=float, default=6.5)
    a = ap.parse_args()

    r = H.retarget(VPK, MODEL, TOC, BH, PP, align='yaw', spine_map='mesh',
                   fallback='region', leg_tilt=5.7, leg_splay=4.0,
                   skeleton='native', mesh_shift=MESH_SHIFT,
                   upper_scale=1.12, upper_pivot_z=1.15, upper_blend=0.45,
                   head_scale=HEAD_SCALE, arm_rot=ARM_ROT, verbose=False)

    hby = {h['name']: h for h in r['hd2']}
    rest_hd2 = r['rest_hd2']
    nh = r['new_head']

    def head(name):
        return np.array(rest_hd2[hby[name]['index']][:3, 3], float)

    for side in ('l', 'r'):
        s_up, s_fo, s_ha = SRC[side]
        h_sh, h_el, h_ha = HD2[side]
        print('=' * 74)
        print('%s arm   (source %s/%s/%s -> HD2 %s/%s/%s)'
              % (side.upper(), s_up, s_fo, s_ha, h_sh, h_el, h_ha))

        # ---- the four directions the pipeline itself uses -------------------
        p_sh, p_el, p_ha = nh[h_sh], nh[h_el], nh[h_ha]      # == joint(src)
        d_up_src = p_el - p_sh
        d_fo_src = p_ha - p_el
        d_up_tgt = head(h_el) - head(h_sh)
        d_fo_tgt = head(h_ha) - head(h_el)

        print('  source (snapped) upper arm  %s  len %.4f'
              % (np.array2string(unit(d_up_src), precision=3), np.linalg.norm(d_up_src)))
        print('  source (snapped) forearm    %s  len %.4f'
              % (np.array2string(unit(d_fo_src), precision=3), np.linalg.norm(d_fo_src)))
        print('  HD2 native upper arm        %s  len %.4f'
              % (np.array2string(unit(d_up_tgt), precision=3), np.linalg.norm(d_up_tgt)))
        print('  HD2 native forearm          %s  len %.4f'
              % (np.array2string(unit(d_fo_tgt), precision=3), np.linalg.norm(d_fo_tgt)))

        # ---- rebuild R_sh / R_el exactly as the pipeline does ---------------
        R_sh = H.min_arc_rotation(d_up_src, d_up_tgt)
        R_el = H.min_arc_rotation(d_fo_src, R_sh.T @ d_fo_tgt)
        a_sh, x_sh = H.rot_angle_axis(R_sh)
        a_el, x_el = H.rot_angle_axis(R_el)
        print('  R_sh  %7.3f deg about %s' % (a_sh, np.array2string(x_sh, precision=3)))
        print('  R_el  %7.3f deg about %s   <- the elbow "hinge"'
              % (a_el, np.array2string(x_el, precision=3)))

        # ---- what the retargeted forearm actually points along --------------
        d_fo_world = unit(R_sh @ R_el @ d_fo_src)
        print('  R_sh.R_el.d_fo_src          %s   (vs HD2 native above)'
              % np.array2string(d_fo_world, precision=3))
        print('  residual vs HD2 native      %.4f deg'
              % ang_between(d_fo_world, d_fo_tgt))

        # ---- the virtual plane the user is talking about --------------------
        n = unit(np.cross(d_up_tgt, d_fo_tgt))
        bend = ang_between(d_up_tgt, d_fo_tgt)
        print('  elbow BEND (HD2 native)     %.3f deg' % bend)
        print('  plane normal d_up x d_fo    %s   (|z| = %.3f -> %s)'
              % (np.array2string(n, precision=3), abs(n[2]),
                 'mostly HORIZONTAL: a turn here shows up in the FRONT/SIDE view'
                 if abs(n[2]) < 0.6 else
                 'mostly VERTICAL: a turn here shows up in the TOP view'))

        # ---- try adding +/- deg about that normal ---------------------------
        print('  --- adding %+.2f deg about the plane normal ---' % a.deg)
        for sgn in (+1.0, -1.0):
            Rn = H.rot_axis_angle(n, np.radians(sgn * a.deg))
            d2 = unit(Rn @ d_fo_tgt)
            print('    %+5.2f deg -> forearm %s  bend %6.3f (%+.3f)  '
                  'top-view turn %+6.2f deg'
                  % (sgn * a.deg, np.array2string(d2, precision=3),
                     ang_between(d_up_tgt, d2),
                     ang_between(d_up_tgt, d2) - bend,
                     signed_top(d_fo_tgt, d2)))
        print('    front X-Z below horiz: now %6.2f  (was %6.2f)'
              % (xz_below(d_fo_tgt), xz_below(d_fo_tgt)))
        print('    top   X-Y forward    : now %+6.2f' % xy_fwd(d_fo_tgt))

        # ---- how the MESH actually sits (carries arm_rot on top) ------------
        d_mesh, nbins, t_el, t_wr = mesh_axis(r, side)
        if side == 'r':
            # `mesh_axis` mirrored x for the fit, so the direction is mirrored
            # too -- mirror it back before comparing with the left-hand maths.
            d_mesh = d_mesh.copy()
            d_mesh[0] *= -1.0
        print('  mesh forearm axis (fit)     %s  (bins %d, t %.3f..%.3f)'
              % (np.array2string(unit(d_mesh), precision=3), nbins, t_el, t_wr))
        print('  mesh vs HD2 native bone     %+.3f deg  (this is arm_rot + '
              'shell/mask residual)' % ang_between(d_mesh, d_fo_tgt))
        print('  mesh X-Z %6.2f below horiz | mesh X-Y %+6.2f forward'
              % (xz_below(d_mesh), xy_fwd(d_mesh)))
        print()


if __name__ == '__main__':
    main()
