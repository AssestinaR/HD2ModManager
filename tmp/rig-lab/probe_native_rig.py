"""Ship HD2's OWN rig, untouched, and let the mesh be the thing that is posed.

The runtime deforms a vertex by  A[b] . inv(S_bind[b]) .  With A == rest_hd2 at
idle, the ONLY way to guarantee "what we pose offline == what the game shows" is

    S_bind[b] == rest_hd2[b]      for every bone

i.e. ship HD2's native rest verbatim -- no snapping, no basis bake, no axis
conversion.  The reference patch does exactly this (its rig is bit-identical to
HD2's native one) and lets the mesh sit wherever it needs to for looks.

So the T-pose -> A-pose rotation moves the MESH, not the rig:

    rt[b]  = HD2's own T-pose rest  (rh + Dd + basis_t, heads from the chain)
    A[b]   = rt[b] . basis_i[b] . inv(rt[b])      -- a rotation about rt[b].head
    out    = sum_b w . A[b] . src1                -- the mesh we ship
    S_bind = rest_hd2                             -- the rig we ship

and then  A[b] . inv(S_bind[b]) == I  at idle, so the game shows our mesh
byte-for-byte.  The source mesh is only rotated / scaled / translated onto HD2's
T-pose joints, and is allowed to sit off the rig -- that is the point.
"""
import json
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_style as H
import export_obj as E

BH = '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt'
PP = '../pb-ref/HD2-BatchTool-main/assets/body_pose_profile.json'
TOC = 'avatar/unit_0x4d1c334d294dfa97.toc'
VPK = ('D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/'
       'Lunalice_Zoey.vpk')
WATCH = ('hips', 'chest', 'neck', 'head', 'l_shoulder', 'l_elbow', 'l_hand',
         'l_foot', 'l_ball', 'l_toe')


def build(profile=PP, spine_map='mesh', align='yaw', head_offset=-0.015,
          leg_tilt=0.0, leg_splay=0.0, vpk=VPK, model='survivor_teenangst',
          toc=TOC, bonehash=BH, verbose=True):
    prof = json.load(open(profile, encoding='utf-8'))
    deltas = prof['symmetric_axis_deltas_wxyz']
    body = set(prof['body_bones'])
    bt = {k: H.q_wxyz_to_m(v) for k, v in prof['tpose_basis_rotations_wxyz'].items()}
    bi = {k: H.q_wxyz_to_m(v)
          for k, v in prof['initial_basis_rotations_from_baked_tpose_wxyz'].items()}

    hd2 = E.read_hd2_rig(toc, bonehash)
    rh = {h['index']: H.m_to_np(h['world']) for h in hd2}
    by_idx = {h['index']: h for h in hd2}
    hby = {h['name']: h for h in hd2 if h['name']}
    order = sorted(hd2, key=lambda h: h['index'])

    def par(h):
        p = h.get('parent')
        return None if (p is None or p == h['index'] or p not in by_idx) else by_idx[p]

    def dd(n):
        return H.q_wxyz_to_m(deltas[n]) if n in body and n in deltas else np.eye(4)

    rt = {}
    for h in order:
        p = par(h)
        L = rh[h['index']] if p is None else (np.linalg.inv(rh[p['index']]) @ rh[h['index']])
        L = L @ dd(h['name']) @ bt.get(h['name'], np.eye(4))
        rt[h['index']] = L if p is None else rt[p['index']] @ L

    mdl_path, mdl, verts, tris, src, short = H.load_source(vpk, model)
    pairs = H.snap_pairs(spine_map)
    idx2name = {h['index']: h['name'] for h in hd2}

    A = np.array([H.origin_np(H.m_to_np(short[n]['world_m']))
                  for n, hn in pairs if n in short and hn in hby])
    B = np.array([rt[hby[hn]['index']][:3, 3]
                  for n, hn in pairs if n in short and hn in hby])
    R, _ca, _cb = (H.yaw_rotation(A, B) if align == 'yaw' else H.kabsch(A, B)[0])

    src_pts = np.array([[v['pos'][0] * E.IN2M, v['pos'][1] * E.IN2M,
                         v['pos'][2] * E.IN2M] for v in verts], dtype=np.float64)
    lo = float(src_pts[:, 2].min())
    sole = src_pts[src_pts[:, 2] < lo + 0.01].mean(0)
    head_src_v = H.origin_np(H.m_to_np(short['Head1']['world_m']))
    head_t = rt[hby['head']['index']][:3, 3]
    rot_only = lambda P: P @ R.T
    src_h = float(rot_only(head_src_v.reshape(1, 3))[0][2]
                  - rot_only(sole.reshape(1, 3))[0][2])
    s = float(head_t[2] + head_offset) / max(src_h, 1e-6)
    align_pts = lambda P: ((P - sole) * s) @ R.T
    t = head_t + np.array([0.0, 0.0, head_offset]) - align_pts(head_src_v.reshape(1, 3))[0]
    src1 = align_pts(src_pts) + t

    def ajoint(b):
        return align_pts(H.origin_np(H.m_to_np(b['world_m'])).reshape(1, 3))[0] + t

    s2h = {}
    for sn, hn in pairs + H.SNAP_TOES:
        sb, hb = short.get(sn), hby.get(hn)
        if sb is not None and hb is not None:
            s2h[sb['index']] = hb['index']
    resolved, _hp, _bd = H.resolve_bone_map(src, s2h, 'distance', ajoint, idx2name)

    # ---- the T-pose -> A-pose deform, applied to the MESH ---------------
    # This is `deform[b] = deform[p] . rt[b] . basis_i[b] . inv(rt[b])`, i.e. the
    # accumulated pose bake, exactly as Blender computes it.  Using only the
    # per-bone local term would rotate each subtree in isolation and leave the
    # mesh in a T-pose.
    Am = {}
    for h in order:
        i = h['index']
        p = par(h)
        Ap = Am[p['index']] if p is not None else np.eye(4)
        rs = rt[i]
        Am[i] = Ap @ rs @ bi.get(h['name'], np.eye(4)) @ np.linalg.inv(rs)
    out = np.empty_like(src1)
    for k, v in enumerate(verts):
        acc = np.zeros(3)
        tot = 0.0
        p4 = np.append(src1[k], 1.0)
        for j in range(3):
            w = v['w'][j]
            if w <= 0:
                continue
            M = Am.get(resolved.get(v['bones'][j]))
            acc += w * ((M if M is not None else np.eye(4)) @ p4)[:3]
            tot += w
        out[k] = acc / tot if tot > 0 else src1[k]

    # ---- the shipped rig is HD2's native one, verbatim --------------------
    S_bind = {i: rh[i].copy() for i in rh}
    idle = {i: rh[i] @ np.linalg.inv(S_bind[i]) for i in rh}
    d_id = max(float(np.linalg.norm(idle[i][:3, :3] - np.eye(3)))
               + float(np.linalg.norm(idle[i][:3, 3])) for i in idle)

    em, p50, p95, p99, emax, ebad = E.edge_distortion(
        [tuple(p) for p in src1], [tuple(p) for p in out], tris)
    if verbose:
        print('rig shipped == HD2 native (max |A.inv(S)-I| = %.2e)' % d_id)
        print('align %s: %.4f deg, scale %.5f   source height %.4f -> %.4f m'
              % (align, H.rot_angle_axis(R)[0], s, src_h, float(head_t[2])))
        print('mesh edge_mean %.4f max %.2f bad>25%% %.2f%%   z [%.4f, %.4f]'
              % (em, emax, 100 * ebad, out[:, 2].min(), out[:, 2].max()))
        print('HD2 T-pose joints (the rig the mesh must be posed against):')
        for nm in WATCH:
            h = hby.get(nm)
            if h:
                print('  %-11s T (%+.4f %+.4f %+.4f)   A (%+.4f %+.4f %+.4f)'
                      % (nm, *rt[h['index']][:3, 3], *rh[h['index']][:3, 3]))
    return dict(out=out, src1=src1, tris=tris, verts=verts, resolved=resolved,
                hd2=hd2, rt=rt, rh=rh, S_bind=S_bind, R=R, s=s, t=t, sole=sole,
                src_pts=src_pts, metrics=dict(edge_mean=em, edge_max=emax, bad=ebad,
                                              height=float(out[:, 2].max() - out[:, 2].min())))


if __name__ == '__main__':
    r = build()
    np.save('out/native-out.npy', r['out'])
    np.save('out/native-rig.npy', np.array([r['S_bind'][i][:3, 3]
                                            for i in sorted(r['S_bind'])]))
    print('[saved] out/native-out.npy')
