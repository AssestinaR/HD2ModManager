# -*- coding: utf-8 -*-
"""READ-ONLY candidate scan for the residual forearm rubber-band effect.

This probe never writes a patch. It evaluates forearm weight ramps in memory and
reports two different things that must not be conflated:
  * macro bend: how far the synthetic wrist-bend centreline bows;
  * skinning strain: actual edge-length change under a distal roll.

The older probes reported the difference between two endpoint displacement
vectors. That is not a seam/tear metric: a perfectly rigidly rotating edge can
have a large displacement-gradient while its length remains unchanged.
"""
import argparse
import os
import sys

import numpy as np

import hd2_hash as H
import hd2_patch as P
import hd2_unit as U

HERE = os.path.dirname(os.path.abspath(__file__))
SDK = 'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
BONES = H.load_bonehash(os.path.join(SDK, 'hashlists', 'bonehash.txt'))
UNITS = (0xcae35ee15168145f, 0x9841b16544c8fbae)


def R_axis(axis, angle):
    axis = np.asarray(axis, float)
    axis = axis / np.linalg.norm(axis)
    K = np.array([[0.0, -axis[2], axis[1]],
                  [axis[2], 0.0, -axis[0]],
                  [-axis[1], axis[0], 0.0]])
    return np.eye(3) + np.sin(angle) * K + (1.0 - np.cos(angle)) * K @ K


def subtree_slots(ti, bi, root_slot):
    root_ti = bi.real_indices[root_slot]
    out = []
    for slot, ti_idx in enumerate(bi.real_indices):
        seen, cur = set(), ti_idx
        while 0 <= cur < ti.count and cur not in seen:
            if cur == root_ti:
                out.append(slot)
                break
            seen.add(cur)
            cur = ti.entries[cur][1]
    return out


def load(patch, unit_id):
    entries, _ = P.scan_entries(patch)
    e = next(x for x in entries
             if x['type_id'] == U.UNIT_ID and x['file_id'] == unit_id)
    pl = P.entry_payload(e, patch)
    u = U.Unit.read(pl['toc'], pl['gpu'])
    # Use the highest-section LOD0 mesh, matching the established probes.
    mi = max((m for m in u.mesh_infos if m.lod_index == 0),
             key=lambda m: m.num_sections)
    verts = u.decode_vertices(mi.stream_index)
    pos = np.asarray([v['position'] for v in verts], float)
    bidx = np.asarray([v['bone_index0'] for v in verts], np.int32)
    wgt = np.asarray([v['bone_weight'] for v in verts], float)
    idx = np.asarray(u.index_list(mi.stream_index), np.int64).reshape(-1, 3)
    edges = np.concatenate((idx[:, [0, 1]], idx[:, [1, 2]], idx[:, [2, 0]]))
    ti, bi = u.transform_info, u.bone_infos[0]
    names, heads = {}, {}
    for slot, ti_idx in enumerate(bi.real_indices):
        names[slot] = BONES.get(ti.hashes[ti_idx], '0x%08x' % ti.hashes[ti_idx])
        m = ti.matrices[ti_idx]
        heads[slot] = np.array([m[12], m[13], m[14]], float)
    slot_of = {v: k for k, v in names.items() if not v.startswith('0x')}
    return u, ti, bi, names, heads, slot_of, pos, bidx, wgt, edges


def smoothstep(a, b, x):
    if b <= a:
        raise ValueError('candidate ramp requires b > a')
    z = np.clip((x - a) / (b - a), 0.0, 1.0)
    return z * z * (3.0 - 2.0 * z)


def candidate_hand_weight(pos, bidx, wgt, slot_of, ti, bi, heads, side, a, b):
    el, hd = side + '_elbow', side + '_hand'
    hand_slot = slot_of[hd]
    elbow_slot = slot_of[el]
    hand_sub = set(subtree_slots(ti, bi, hand_slot))
    E, W = heads[elbow_slot], heads[hand_slot]
    axis_vec = W - E
    L = float(np.linalg.norm(axis_vec))
    axis = axis_vec / L
    rel = pos - E
    t = rel @ axis
    rad = np.linalg.norm(rel - np.outer(t, axis), axis=1)
    roi = (rad < 0.10) & (t > 0.0) & (t < L)
    hand = np.zeros(len(pos), float)
    for j in range(4):
        hand += np.where(np.isin(bidx[:, j], list(hand_sub)), wgt[:, j], 0.0)
    # Candidate replaces only elbow/hand-subtree mass in the forearm tube.
    # The shipped baseline uses decoded weights unchanged; no bytes are written.
    arm = np.zeros(len(pos), float)
    arm += np.where(bidx == elbow_slot, wgt, 0.0).sum(axis=1)
    arm += np.where(np.isin(bidx, list(hand_sub)), wgt, 0.0).sum(axis=1)
    out = hand.copy()
    if a is not None and b is not None:
        s = smoothstep(a, b, t / L)
        out[roi] = arm[roi] * s[roi]
    return out, E, W, axis, L, t, rad, roi


def displacement(pos, W, axis, angle, t, bend=False):
    if bend:
        # transverse axis, chosen deterministically and perpendicular to arm
        z = np.array([0.0, 0.0, 1.0])
        rot_axis = np.cross(axis, z)
        if np.linalg.norm(rot_axis) < 1e-8:
            rot_axis = np.cross(axis, np.array([0.0, 1.0, 0.0]))
        rot_axis /= np.linalg.norm(rot_axis)
    else:
        rot_axis = axis
    R = R_axis(rot_axis, angle)
    return (pos - W) @ R.T - (pos - W)


def edge_stats(pos, d, edges, edge_mask):
    if not edge_mask.any():
        return dict(n=0, abs_med=0.0, abs_p99=0.0, abs_max=0.0,
                    strain_med=0.0, strain_p99=0.0, strain_max=0.0)
    e = edges[edge_mask]
    p0 = pos[e[:, 0]] - pos[e[:, 1]]
    p1 = (pos[e[:, 0]] + d[e[:, 0]]) - (pos[e[:, 1]] + d[e[:, 1]])
    l0 = np.linalg.norm(p0, axis=1)
    l1 = np.linalg.norm(p1, axis=1)
    delta = np.abs(l1 - l0)
    valid = l0 > 1e-8
    # Relative strain is dominated by sub-millimetre edges. Restrict it to
    # original edges >= 5 mm; absolute length deltas still cover all real edges.
    long_edge = l0 >= 0.005
    strain = np.abs(l1[long_edge] / l0[long_edge] - 1.0)
    return dict(n=int(valid.sum()), abs_med=float(np.median(delta[valid]) * 1000),
                abs_p99=float(np.percentile(delta[valid], 99) * 1000),
                abs_max=float(delta[valid].max() * 1000),
                strain_med=float(np.median(strain) * 100),
                strain_p99=float(np.percentile(strain, 99) * 100),
                strain_max=float(strain.max() * 100))


def centreline_bow(pos, moved, t, L, roi):
    # Centroids in longitudinal bins; line through the first/last centroids.
    bins = np.linspace(0.10, 0.98, 15) * L
    cs = []
    for lo, hi in zip(bins[:-1], bins[1:]):
        sel = roi & (t >= lo) & (t < hi)
        if sel.sum():
            cs.append(moved[sel].mean(axis=0))
    if len(cs) < 3:
        return float('nan')
    cs = np.asarray(cs)
    v = cs[-1] - cs[0]
    v /= np.linalg.norm(v)
    d = cs - cs[0]
    perp = np.linalg.norm(d - np.outer(d @ v, v), axis=1)
    return float(perp.max() * 1000)


def analyse(patch, unit_id):
    u, ti, bi, names, heads, slot_of, pos, bidx, wgt, edges = load(patch, unit_id)
    print('\n' + '=' * 118)
    print('%s unit=0x%016x verts=%d' % (patch, unit_id, len(pos)))
    print('a..b | bend30 mid-med(mm) bow(mm) | roll180 edge-delta med/p99/max(mm) | roll180 strain p99/max(%)')
    for a, b in ((None, None), (0.22, 1.00), (0.40, 1.00), (0.50, 1.00), (0.60, 1.00), (0.70, 1.00), (0.78, 0.92)):
        rows = []
        for side in ('l', 'r'):
            hand, E, W, axis, L, t, rad, roi = candidate_hand_weight(
                pos, bidx, wgt, slot_of, ti, bi, heads, side, a, b)
            bend_delta = displacement(pos, W, axis, np.deg2rad(30), t, bend=True)
            bend_mag = np.linalg.norm(hand[:, None] * bend_delta, axis=1)
            mid = roi & (t >= 0.25 * L) & (t < 0.70 * L)
            moved = pos + hand[:, None] * bend_delta
            bow = centreline_bow(pos, moved, t, L, roi)
            roll_delta = hand[:, None] * displacement(pos, W, axis, np.pi, t, bend=False)
            edge_band = (rad[edges[:, 0]] < 0.10) & (rad[edges[:, 1]] < 0.10)
            edge_band &= (t[edges[:, 0]] > 0.05 * L) & (t[edges[:, 0]] < 1.05 * L)
            edge_band &= (t[edges[:, 1]] > 0.05 * L) & (t[edges[:, 1]] < 1.05 * L)
            stats = edge_stats(pos, roll_delta, edges, edge_band)
            rows.append((float(np.median(bend_mag[mid]) * 1000), bow, stats))
        bm = np.mean([r[0] for r in rows]); bow = np.mean([r[1] for r in rows])
        med = np.mean([r[2]['abs_med'] for r in rows]); p99 = np.mean([r[2]['abs_p99'] for r in rows]); mx = np.mean([r[2]['abs_max'] for r in rows])
        sp99 = np.mean([r[2]['strain_p99'] for r in rows]); smx = np.mean([r[2]['strain_max'] for r in rows])
        label = 'shipped    ' if a is None else '%0.2f..%0.2f' % (a, b)
        print('%s | %8.2f %8.2f | %8.2f / %8.2f / %8.2f | %8.3f / %8.3f' % (label,bm,bow,med,p99,mx,sp99,smx))
    # deliberate negative control: all forearm armish mass switches at .80L
    hand, E, W, axis, L, t, rad, roi = candidate_hand_weight(pos,bidx,wgt,slot_of,ti,bi,heads,'l',0.799,0.801)
    d = hand[:,None] * displacement(pos,W,axis,np.pi,t,False)
    eb = (rad[edges[:,0]]<.10)&(rad[edges[:,1]]<.10)&(t[edges[:,0]]>.05*L)&(t[edges[:,0]]<1.05*L)&(t[edges[:,1]]>.05*L)&(t[edges[:,1]]<1.05*L)
    neg=edge_stats(pos,d,edges,eb)
    print('negative hard-step control (left, 0.80L): roll edge strain p99/max = %.3f / %.3f %%' % (neg['strain_p99'],neg['strain_max']))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('patch', nargs='+')
    ap.add_argument('--unit', action='append', default=[])
    args = ap.parse_args()
    units = [int(x, 16) for x in args.unit] or list(UNITS)
    for patch in args.patch:
        for unit in units:
            try:
                analyse(patch, unit)
            except StopIteration:
                continue
    return 0


if __name__ == '__main__':
    sys.exit(main())
