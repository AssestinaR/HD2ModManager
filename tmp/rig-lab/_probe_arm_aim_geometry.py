# -*- coding: utf-8 -*-
"""量出"以源肘为枢轴"这件事到底在管线里怎么做的，以及用户提的"对准 HD2 腕点"
与现行"对齐 HD2 骨方向"差多少度。只读，不改任何交付字节。

背景（管线现状，hd2_style.py）：
  * `rest_sym`（snapped rest）的 **head 就是对齐后的源关节**（hd2_style.py:908
    `joint()` / :914 `new_head[hn] = joint(sn)`），网格始终绕 `rest_sym` 烘焙
    （:737-742）。所以"骨骼随网格移动"是**已经成立**的。
  * 大臂：`R_sh = min_arc(源肩->源肘, HD2肩->HD2肘)`，枢轴 = 源肩（:1271）。
  * 小臂：`R_el = min_arc(源肘->源腕, R_sh^-1 . (HD2肘->HD2腕))`，枢轴 = 源肘
    （:1322）。**目标方向用的是 HD2 自己的肘**（`rest_hd2`），不是源肘。
  * 再叠加一个手调常数 `arm_elbow_open = 6.5`（在小臂弯曲平面内再张 6.5°）。

用户提案的差别只有一处：目标方向改成 `HD2腕 − 源肘`（从"我们肘在哪"指向
HD2 的腕），而不是 `HD2腕 − HD2肘`。

usage:
    _probe_arm_aim_geometry.py [--vpk PATH] [--model STEM] [--json FILE]
"""
import argparse
import json
import math
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import export_obj as E
import hd2_style as ST

TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
DEFAULT_VPK = 'E:/tmp/mourning/MourningVeil_Zoey.vpk'
DEFAULT_MODEL = 'survivor_teenangst'
# the params the shipped MourningVeil build used (E:/tmp/mourning/out/meta.json)
SPINE_MAP = 'mesh'
HEAD_OFFSET = -0.015


def ang_deg(a, b):
    a = np.asarray(a, float)
    b = np.asarray(b, float)
    na, nb = np.linalg.norm(a), np.linalg.norm(b)
    if na < 1e-12 or nb < 1e-12:
        return float('nan')
    return float(math.degrees(math.acos(np.clip(float(a @ b) / (na * nb), -1.0, 1.0))))


def fmt(v):
    return '(%+.4f, %+.4f, %+.4f)' % (v[0], v[1], v[2])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--vpk', default=DEFAULT_VPK)
    ap.add_argument('--model', default=DEFAULT_MODEL)
    ap.add_argument('--json', default='')
    # the two fixed numbers the shipped build uses (meta.json params)
    ap.add_argument('--gain', type=float, default=0.05,
                    help='arm_scratch_upper_gain: axial stretch of the upper arm, m')
    ap.add_argument('--elbow-open', type=float, default=6.5,
                    help='arm_elbow_open: extra elbow-plane opening, deg')
    args = ap.parse_args()

    _p, _m, verts, _t, src, short = ST.load_source(args.vpk, args.model)
    hd2 = E.read_hd2_rig(TOC, BH)
    hby = {h['name']: h for h in hd2 if h['name']}
    rest_hd2 = {h['index']: ST.m_to_np(h['world']) for h in hd2}

    # ---- replicate the pipeline's initial rigid fit (hd2_style.py:851-900) ---
    body = ST.snap_body(SPINE_MAP)
    A = np.array([ST.origin_np(ST.m_to_np(short[n]['world_m'])) for n, _h in body])
    B = np.array([ST.origin_np(ST.m_to_np(hby[h]['world'])) for _n, h in body])
    R, _ca, _cb = ST.yaw_rotation(A, B)
    yaw_ang, _yaw_ax = ST.rot_angle_axis(R)

    src_pts = np.array([[v['pos'][0] * E.IN2M, v['pos'][1] * E.IN2M,
                         v['pos'][2] * E.IN2M] for v in verts], dtype=np.float64)
    lo = float(src_pts[:, 2].min())
    sole = src_pts[src_pts[:, 2] < lo + 0.01].mean(0)
    head_src_v = ST.origin_np(ST.m_to_np(short['Head1']['world_m']))
    head_hd2 = ST.origin_np(ST.m_to_np(hby['head']['world']))
    src_h = float((head_src_v.reshape(1, 3) @ R.T)[0][2]
                  - (sole.reshape(1, 3) @ R.T)[0][2])
    s = (head_hd2[2] + HEAD_OFFSET) / max(src_h, 1e-6)

    def align_pts(P):
        return ((P - sole) * s) @ R.T

    t = (head_hd2 + np.array([0.0, 0.0, HEAD_OFFSET])) - align_pts(head_src_v.reshape(1, 3))[0]

    def joint(sname):
        return align_pts(ST.origin_np(ST.m_to_np(short[sname]['world_m'])).reshape(1, 3))[0] + t

    # ---- 1c. LEG TILT (hd2_style.py:957-981) ------------------------------
    # Rotates the whole rig about the ANKLES by `leg_tilt` and then translates
    # every bone that is not a leg bone by `delta = Rl(hips) - hips`.  The arm
    # joints ARE translated by it, so a probe that stops at `joint()` lands
    # 9.5 cm out in Y.  (Round 77: this is what made the first aim probe wrong.)
    LEG_TILT = 5.7
    _ankle = 0.5 * (joint('L_Foot') + joint('R_Foot'))
    _th = math.radians(LEG_TILT)
    _ct, _st = math.cos(_th), math.sin(_th)
    _Rl = (ST.trans(_ankle)
           @ np.array([[1, 0, 0, 0], [0, _ct, _st, 0],
                       [0, -_st, _ct, 0], [0, 0, 0, 1]], float)
           @ ST.trans(-_ankle))
    _hips = joint('Pelvis')
    leg_delta = (_Rl @ np.append(_hips, 1.0))[:3] - _hips
    print('  leg tilt %.2f deg about the ankles -> whole-rig delta %s'
          % (LEG_TILT, fmt(leg_delta)))

    def ajoint(sname):
        return joint(sname) + leg_delta

    print('=== initial rigid fit ===')
    print('  yaw %.4f deg   scale %.6f   translate %s' % (yaw_ang, s, fmt(t)))
    print('  sole %s   raw L_UpperArm %s   raw Head1 %s'
          % (fmt(sole), fmt(ST.origin_np(ST.m_to_np(short['L_UpperArm']['world_m']))),
             fmt(head_src_v)))

    out = dict(yawDeg=yaw_ang, scale=s, translate=[float(x) for x in t], arms={})

    for side, SD in (('l', 'L'), ('r', 'R')):
        S_s = ajoint('%s_UpperArm' % SD)
        E_s = ajoint('%s_Forearm' % SD)
        W_s = ajoint('%s_Hand' % SD)
        S_h = ST.origin_np(rest_hd2[hby['%s_shoulder' % side]['index']])
        E_h = ST.origin_np(rest_hd2[hby['%s_elbow' % side]['index']])
        W_h = ST.origin_np(rest_hd2[hby['%s_hand' % side]['index']])

        # ---- step 3b: the upper-arm gain (a pure translation of the elbow and
        # everything below it, along the SOURCE T-pose shoulder->elbow axis) ----
        u_src = (E_s - S_s) / max(float(np.linalg.norm(E_s - S_s)), 1e-12)
        g_delta = u_src * args.gain
        E_g = E_s + g_delta
        W_g = W_s + g_delta

        d_up_src = E_s - S_s
        d_up_tgt = E_h - S_h
        R_sh = ST.min_arc_rotation(d_up_src, d_up_tgt)
        sh_ang, sh_ax = ST.rot_angle_axis(R_sh)

        # after the shoulder rotation the whole limb is rigid, so:
        #   source elbow  -> E_s' = S_s + R_sh (E_g - S_s)
        #   source wrist  -> W_s' = S_s + R_sh (W_g - S_s)
        E_s2 = S_s + R_sh @ (E_g - S_s)
        W_s2 = S_s + R_sh @ (W_g - S_s)

        d_fo_src = W_g - E_g                      # forearm vector, pre-shoulder frame
        d_fo_src2 = W_s2 - E_s2                   # == R_sh @ d_fo_src
        d_fo_tgt = W_h - E_h                      # what the pipeline aims at (HD2's own)
        d_fo_aim = W_h - E_s2                     # what the user proposes (our elbow -> HD2 wrist)

        # what the pipeline actually solves for (in the pre-shoulder frame)
        R_el = ST.min_arc_rotation(d_fo_src, R_sh.T @ d_fo_tgt)
        el_ang, el_ax = ST.rot_angle_axis(R_el)
        # the user's version
        R_el_aim = ST.min_arc_rotation(d_fo_src, R_sh.T @ d_fo_aim)
        el_ang_aim, _ = ST.rot_angle_axis(R_el_aim)

        # decompose the elbow position error in HD2's upper-arm frame
        u_h = (E_h - S_h) / max(float(np.linalg.norm(E_h - S_h)), 1e-12)
        err = E_s2 - E_h
        along = float(err @ u_h)
        perp = float(np.linalg.norm(err - u_h * along))

        print('=== %s arm ===' % ('LEFT' if side == 'l' else 'RIGHT'))
        print('  source shoulder %s   HD2 shoulder %s   |delta| %.1f mm'
              % (fmt(S_s), fmt(S_h), 1000 * np.linalg.norm(S_s - S_h)))
        print('  source elbow    %s   HD2 elbow    %s   |delta| %.1f mm'
              % (fmt(E_s), fmt(E_h), 1000 * np.linalg.norm(E_s - E_h)))
        print('  source wrist    %s   HD2 wrist    %s   |delta| %.1f mm'
              % (fmt(W_s), fmt(W_h), 1000 * np.linalg.norm(W_s - W_h)))
        print('  upper-arm gain %+.4f m along source shoulder->elbow (%s)'
              % (args.gain, fmt(u_src)))
        print('  source elbow AFTER shoulder rot %s  (moved %.1f mm)'
              % (fmt(E_s2), 1000 * np.linalg.norm(E_s2 - E_s)))
        print('  >>> elbow still %.1f mm from HD2 elbow:  %+.1f mm along HD2 upper arm'
              '  + %.1f mm perpendicular  <<<' % (1000 * np.linalg.norm(err), 1000 * along, 1000 * perp))
        print('  forearm length: source %.1f mm   HD2 %.1f mm   (ratio %.4f)'
              % (1000 * np.linalg.norm(d_fo_src), 1000 * np.linalg.norm(d_fo_tgt),
                 np.linalg.norm(d_fo_src) / np.linalg.norm(d_fo_tgt)))
        print('  |HD2 wrist - our elbow| = %.1f mm   (our forearm = %.1f mm)'
              % (1000 * np.linalg.norm(d_fo_aim), 1000 * np.linalg.norm(d_fo_src)))
        print('  shoulder min-arc %.2f deg about (%+.3f, %+.3f, %+.3f)'
              % (sh_ang, sh_ax[0], sh_ax[1], sh_ax[2]))
        print('  --- elbow, three candidate targets (all pivot = source elbow) ---')
        print('   [pipeline]  source forearm -> (HD2 elbow->HD2 wrist) : %6.2f deg' % el_ang)
        print('   [user aim]  source forearm -> (source elbow -> HD2 wrist) : %6.2f deg' % el_ang_aim)
        print('   angle between the two TARGET directions : %.2f deg'
              % ang_deg(d_fo_tgt, d_fo_aim))
        print('   angle between source forearm (after shoulder) and HD2 own dir : %.2f deg'
              % ang_deg(d_fo_src2, d_fo_tgt))
        print('   angle between source forearm (after shoulder) and user aim    : %.2f deg'
              % ang_deg(d_fo_src2, d_fo_aim))
        # where does the wrist end up under each rule?
        def wrist_under(target):
            Rr = ST.min_arc_rotation(d_fo_src, R_sh.T @ target)
            d_new = R_sh @ (Rr @ d_fo_src)
            return E_s2 + d_new * (np.linalg.norm(d_fo_src2) / max(np.linalg.norm(d_new), 1e-12))
        for nm, tgt in (('pipeline', d_fo_tgt), ('user aim', d_fo_aim)):
            Wnew = wrist_under(tgt)
            print('   wrist if %-9s : %s   -> %.1f mm from HD2 wrist'
                  % (nm, fmt(Wnew), 1000 * np.linalg.norm(Wnew - W_h)))
        print('   (with elbow_open %+.2f deg on top of the pipeline solve: )' % args.elbow_open)
        n_w = np.cross(d_up_tgt, d_fo_tgt)
        nn = float(np.linalg.norm(n_w))
        d_fo_open = ST.rot_axis_angle(n_w / nn, -math.radians(args.elbow_open)) @ d_fo_tgt
        Wnew = wrist_under(d_fo_open)
        print('      wrist : %s   -> %.1f mm from HD2 wrist   (dir off HD2-own by %.2f deg)'
              % (fmt(Wnew), 1000 * np.linalg.norm(Wnew - W_h), ang_deg(d_fo_open, d_fo_tgt)))
        print('   angle between (pipeline + open) and the user aim : %.2f deg'
              % ang_deg(d_fo_open, d_fo_aim))

        out['arms'][side] = dict(
            sourceShoulder=[float(x) for x in S_s], sourceElbow=[float(x) for x in E_s],
            sourceWrist=[float(x) for x in W_s],
            hd2Shoulder=[float(x) for x in S_h], hd2Elbow=[float(x) for x in E_h],
            hd2Wrist=[float(x) for x in W_h],
            sourceElbowAfterShoulder=[float(x) for x in E_s2],
            gainM=args.gain,
            elbowDeltaMm=1000 * float(np.linalg.norm(E_s - E_h)),
            shoulderDeltaMm=1000 * float(np.linalg.norm(S_s - S_h)),
            wristDeltaMm=1000 * float(np.linalg.norm(W_s - W_h)),
            elbowResidualMm=1000 * float(np.linalg.norm(err)),
            elbowResidualAlongMm=1000 * along,
            elbowResidualPerpMm=1000 * perp,
            forearmLenSrcMm=1000 * float(np.linalg.norm(d_fo_src)),
            forearmLenHd2Mm=1000 * float(np.linalg.norm(d_fo_tgt)),
            ourElbowToHd2WristMm=1000 * float(np.linalg.norm(d_fo_aim)),
            shoulderMinArcDeg=sh_ang,
            pipelineElbowDeg=el_ang, userAimElbowDeg=el_ang_aim,
            targetDirGapDeg=ang_deg(d_fo_tgt, d_fo_aim),
        )

    if args.json:
        with open(args.json, 'w', encoding='utf-8') as fh:
            json.dump(out, fh, ensure_ascii=False, indent=1)
        print('wrote %s' % args.json)


if __name__ == '__main__':
    main()
