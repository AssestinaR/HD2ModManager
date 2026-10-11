"""Does the BatchTool round trip explain the rig being 34-180 deg off?

The BatchTool never ships a rig with the pose-profile's axis deltas baked in.  Its
final step is "转回初态 + 原始骨轴" = convert the pose back to INITIAL and the bone
axes back to ORIGINAL.  The chain is:

    INITIAL  --(axes -> SYMMETRIC)-->  SYMMETRIC/INITIAL
             --(bake basis_t)-->       SYMMETRIC/TPOSE      <- this is where the
                                                            source T-pose lives
             --(bake basis_i)-->       SYMMETRIC/INITIAL
             --(axes -> INITIAL)-->    INITIAL/INITIAL       <- what ships

and because basis_i == inverse(basis_t) the two bakes CANCEL on the rig's local
rotations, leaving the shipped rig bit-identical to HD2's native one -- which is
exactly what the reference patch has.  Our pipeline applies only basis_i and never
converts the axes back, so the shipped rig keeps  basis_i  and  Dd  baked in.

This builds the full round trip out of what retarget() already returns and measures
the rotation offset against HD2's native rig.
"""
import json
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
ARM = ('l_clavicle', 'l_shoulder', 'l_elbow', 'l_hand', 'r_clavicle', 'r_shoulder',
       'r_elbow', 'r_hand')


def rot_ang(A, B):
    R = A[:3, :3].T @ B[:3, :3]
    return float(np.degrees(np.arccos(np.clip((np.trace(R) - 1) / 2.0, -1, 1))))


def main():
    r = H.retarget(VPK, 'survivor_teenangst', TOC, BH, PP, align='yaw',
                   spine_map='mesh', fallback='distance', leg_tilt=5.7,
                   leg_splay=4.0, axis_deltas=True, verbose=False)
    prof = json.load(open(PP, encoding='utf-8'))
    deltas = prof['symmetric_axis_deltas_wxyz']
    body = set(prof['body_bones'])
    bt = {k: H.q_wxyz_to_m(v) for k, v in prof['tpose_basis_rotations_wxyz'].items()}
    bi = {k: H.q_wxyz_to_m(v)
          for k, v in prof['initial_basis_rotations_from_baked_tpose_wxyz'].items()}

    hd2, rh, nh = r['hd2'], r['rest_hd2'], r['new_head']
    by_idx = {h['index']: h for h in hd2}
    order = sorted(hd2, key=lambda h: h['index'])

    def par(h):
        p = h.get('parent')
        return None if (p is None or p == h['index'] or p not in by_idx) else by_idx[p]

    def dd(name):
        return H.q_wxyz_to_m(deltas[name]) if name in body and name in deltas else np.eye(4)

    # ---- the T-pose working rest: SYMMETRIC axes, basis_t baked ------------
    rst = {}
    for h in order:
        p = par(h)
        L = H.m_to_np(h['world']) if p is None else (
            np.linalg.inv(rh[p['index']]) @ rh[h['index']])
        base = (L if p is None else rst[p['index']] @ L) @ dd(h['name']) @ bt.get(
            h['name'], np.eye(4))
        t = nh.get(h['name'])
        if t is not None:
            base = H.trans(t - H.origin_np(base)) @ base
        rst[h['index']] = base

    # ---- bake basis_i (TPOSE -> INITIAL) ----------------------------------
    def deform_from(rest, basis):
        D = {}
        for h in order:
            p = par(h)
            Dp = D[p['index']] if p is not None else np.eye(4)
            rs = rest[h['index']]
            D[h['index']] = Dp @ rs @ basis.get(h['name'], np.eye(4)) @ np.linalg.inv(rs)
        return D

    D = deform_from(rst, bi)
    pose = {h['index']: D[h['index']] @ rst[h['index']] for h in order}

    # ---- axes SYMMETRIC -> INITIAL ----------------------------------------
    # convert_body_axis_space only rotates each bone's OWN frame; it explicitly
    # keeps the head (desired = Matrix.LocRotScale(bone.head_local, new_rot, 1),
    # and it raises if the head drifts more than 1e-5).  So take the rotation from
    # the chain and put the head back where the pose bake left it.
    pose_init = {}
    for h in order:
        i = h['index']
        p = par(h)
        L = pose[i] if p is None else (np.linalg.inv(pose[p['index']]) @ pose[i])
        L = L @ np.linalg.inv(dd(h['name']))
        chain = L if p is None else pose_init[p['index']] @ L
        pose_init[i] = H.trans(pose[i][:3, 3] - H.origin_np(chain)) @ chain

    # ---- what we ship today ------------------------------------------------
    sa = {i: r['D'][i] @ r['rest'][i] for i in r['rest']}

    for tag, S in (('today (S_A = D.rest_sym)', sa),
                   ('BatchTool round trip', pose_init)):
        dh, dr, worst = [], [], []
        for h in order:
            i = h['index']
            if i not in S:
                continue
            dh.append(float(np.linalg.norm(S[i][:3, 3] - rh[i][:3, 3])))
            a = rot_ang(S[i], rh[i])
            dr.append(a)
            worst.append((a, h['name']))
        dh, dr = np.asarray(dh), np.asarray(dr)
        worst.sort(key=lambda x: -x[0])
        print('== %s ==' % tag)
        print('  head vs HD2 native: mean %.4f  median %.4f  max %.4f m'
              % (dh.mean(), np.median(dh), dh.max()))
        print('  rot  vs HD2 native: mean %6.2f  median %6.2f  max %6.2f deg  ( >5deg: %d/%d )'
              % (dr.mean(), np.median(dr), dr.max(), int((dr > 5).sum()), len(dr)))
        print('  worst rot: %s' % ', '.join('%s %.1f' % (n, a) for a, n in worst[:6]))
        print('  arm chain:')
        for nm in ARM:
            i = next((h['index'] for h in hd2 if h['name'] == nm), None)
            if i is None or i not in S:
                continue
            print('    %-16s head %.4f m   rot %6.2f deg'
                  % (nm, float(np.linalg.norm(S[i][:3, 3] - rh[i][:3, 3])),
                     rot_ang(S[i], rh[i])))
        print()

    # ---- the mesh the round trip would ship -------------------------------
    verts = r['verts']
    resolved = r['resolved']
    by_name = {h['index']: h['name'] for h in hd2}
    src1 = r['src1']
    out = np.empty_like(src1)
    for k, v in enumerate(verts):
        acc = np.zeros(3)
        tot = 0.0
        p4 = np.append(src1[k], 1.0)
        for j in range(3):
            w = v['w'][j]
            if w <= 0:
                continue
            M = D.get(resolved.get(v['bones'][j]))
            acc += w * ((M if M is not None else np.eye(4)) @ p4)[:3]
            tot += w
        out[k] = acc / tot if tot > 0 else src1[k]
    idle = {i: rh[i] @ np.linalg.inv(pose_init[i]) for i in pose_init}
    out_idle = np.empty_like(out)
    for k, v in enumerate(verts):
        acc = np.zeros(3)
        tot = 0.0
        p4 = np.append(out[k], 1.0)
        for j in range(3):
            w = v['w'][j]
            if w <= 0:
                continue
            M = idle.get(resolved.get(v['bones'][j]))
            acc += w * ((M if M is not None else np.eye(4)) @ p4)[:3]
            tot += w
        out_idle[k] = acc / tot if tot > 0 else out[k]

    em, p50, p95, p99, emax, ebad = H.E.edge_distortion(
        [tuple(p) for p in src1], [tuple(p) for p in out], r['tris'])
    print('round-trip mesh: edge_mean %.4f max %.2f bad>25%% %.2f%%  height %.3f  '
          'zmin %.4f' % (em, emax, 100 * ebad,
                         float(out[:, 2].max() - out[:, 2].min()), out[:, 2].min()))
    d = np.linalg.norm(out_idle - out, axis=1)
    print('round-trip idle displacement from bind: mean %.4f max %.4f m'
          % (d.mean(), d.max()))
    np.save('out/rt-out.npy', out)
    np.save('out/rt-idle.npy', out_idle)
    np.save('out/rt-rest.npy', np.array([pose_init[i][:3, 3] for i in sorted(pose_init)]))


if __name__ == '__main__':
    main()
