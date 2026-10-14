# -*- coding: utf-8 -*-
"""READ-ONLY per-shell forearm analysis (expert brief 11.4 step 1+2).

The old forearm probes treat the whole near-axis tube as one surface.  In this
package the tube actually contains several shells (body skin, armour plate,
gauntlet, cloth), and a candidate ramp that fixes one shell can tear another.
This probe therefore:

  1. splits the LOD0 mesh into shells by material section (section == material
     == the order of the appearance manifest groups);
  2. for each ramp a..b, reports, PER SHELL, the 180 deg roll edge-length change
     (real strain, not the old displacement-gradient JUMP) and the 30 deg bend
     mid-segment displacement;
  3. reports shell-vs-shell relative sliding per longitudinal bin, as a cheap
     proxy for the wrist seam / interpenetration risk.

Nothing is written; no patch is produced.
"""
import argparse
import json
import os
import sys

import numpy as np

import hd2_hash as H
import hd2_patch as P
import hd2_unit as U

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
    mi = max((m for m in u.mesh_infos if m.lod_index == 0),
             key=lambda m: m.num_sections)
    verts = u.decode_vertices(mi.stream_index)
    pos = np.asarray([v['position'] for v in verts], float)
    bidx = np.asarray([v['bone_index0'] for v in verts], np.int32)
    wgt = np.asarray([v['bone_weight'] for v in verts], float)
    idx = np.asarray(u.index_list(mi.stream_index), np.int64).reshape(-1, 3)
    ti, bi = u.transform_info, u.bone_infos[0]
    names, heads = {}, {}
    for slot, ti_idx in enumerate(bi.real_indices):
        names[slot] = BONES.get(ti.hashes[ti_idx], '0x%08x' % ti.hashes[ti_idx])
        m = ti.matrices[ti_idx]
        heads[slot] = np.array([m[12], m[13], m[14]], float)
    slot_of = {v: k for k, v in names.items() if not v.startswith('0x')}
    # per-section triangle ranges (section order == manifest group order)
    sec_ranges = []
    for s in mi.sections:
        sec_ranges.append((s.material_index, s.index_offset // 3, s.num_indices // 3))
    return dict(u=u, ti=ti, bi=bi, names=names, heads=heads, slot_of=slot_of,
                pos=pos, bidx=bidx, wgt=wgt, idx=idx, secs=sec_ranges)


def smoothstep(a, b, x):
    z = np.clip((x - a) / (b - a), 0.0, 1.0)
    return z * z * (3.0 - 2.0 * z)


def side_axis(D, side):
    el, hd = side + '_elbow', side + '_hand'
    hs, es = D['slot_of'][hd], D['slot_of'][el]
    hand_sub = set(subtree_slots(D['ti'], D['bi'], hs))
    E, W = D['heads'][es], D['heads'][hs]
    av = W - E
    L = float(np.linalg.norm(av))
    axis = av / L
    rel = D['pos'] - E
    t = rel @ axis
    rad = np.linalg.norm(rel - np.outer(t, axis), axis=1)
    return E, W, axis, L, t, rad, hs, es, hand_sub


def hand_mass(D, hand_sub):
    return np.where(np.isin(D['bidx'], list(hand_sub)), D['wgt'], 0.0).sum(axis=1)


def arm_mass(D, hand_sub, elbow_slot):
    return hand_mass(D, hand_sub) + np.where(D['bidx'] == elbow_slot, D['wgt'], 0.0).sum(axis=1)


def edge_len_delta(pos, d, edges):
    p0 = pos[edges[:, 0]] - pos[edges[:, 1]]
    p1 = (pos[edges[:, 0]] + d[edges[:, 0]]) - (pos[edges[:, 1]] + d[edges[:, 1]])
    l0 = np.linalg.norm(p0, axis=1)
    l1 = np.linalg.norm(p1, axis=1)
    ok = l0 > 1e-8
    return np.abs(l1 - l0)[ok] * 1000.0, l0[ok], l1[ok]


def worst_where(dl, tmid, L):
    """median t/L of the worst 1% edges -- wrist (hidden) or mid-forearm?"""
    if len(dl) == 0:
        return float('nan')
    thr = np.percentile(dl, 99)
    sel = dl >= thr
    return float(np.median(tmid[sel]) / L) if sel.any() else float('nan')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('patch', nargs='+')
    ap.add_argument('--unit', action='append', default=[])
    ap.add_argument('--manifest', default='')
    ap.add_argument('--ramps', default='.30:1.00,.35:1.00,.40:1.00,.45:1.00,.50:1.00,.78:0.92')
    args = ap.parse_args()
    units = [int(x, 16) for x in args.unit] or list(UNITS)
    ramps = [(None, None)] + [tuple(float(v) for v in r.split(':')) for r in args.ramps.split(',')]

    sec_names = {}
    if args.manifest and os.path.isfile(args.manifest):
        mf = json.load(open(args.manifest, encoding='utf-8'))
        for i, g in enumerate(mf['groups']):
            sec_names[i] = g['group']

    for patch in args.patch:
        for unit in units:
            try:
                D = load(patch, unit)
            except StopIteration:
                continue
            print('\n' + '=' * 128)
            print('%s  unit=0x%016x  verts=%d  sections=%d' % (patch, unit, len(D['pos']), len(D['secs'])))
            # ---- shell inventory on the forearm band (left side) ----
            E, W, axis, L, t, rad, hs, es, hand_sub = side_axis(D, 'l')
            band = (rad < 0.10) & (t > 0.05 * L) & (t < 1.05 * L)
            print('forearm L = %.1f mm   shells intersecting the near-axis band (left):' % (L * 1000))
            print('  %-4s %-22s %-9s %-9s %-16s %s' % ('sec', 'material', 'band-tri', 'band-vtx', 't/L range', 'mean r(mm)'))
            active = []
            for mat, t0, nt in D['secs']:
                tri = D['idx'][t0:t0 + nt]
                vb = band[tri].all(axis=1)
                if vb.sum() == 0:
                    continue
                vv = np.unique(tri[vb])
                tt = t[vv] / L
                print('  %-4d %-22s %-9d %-9d %.2f-%.2f        %6.1f'
                      % (mat, sec_names.get(mat, 'sec%d' % mat), int(vb.sum()), len(vv),
                         tt.min(), tt.max(), rad[vv].mean() * 1000))
                active.append(mat)
            # ---- ramp sweep, per shell ----
            print('\n  ramp sweep (mean of left+right).  bend = 30 deg wrist bend mid-segment displacement;')
            print('  roll = 180 deg roll real edge-length change per shell.')
            for a, b in ramps:
                label = 'shipped' if a is None else '%.2f..%.2f' % (a, b)
                # accumulate per-shell per-side
                shell_roll = {m: [] for m in active}
                shell_bend = {m: [] for m in active}
                slide = []
                for side in ('l', 'r'):
                    E, W, axis, L, t, rad, hs, es, hand_sub = side_axis(D, side)
                    roi = (rad < 0.10) & (t > 0.0) & (t < L)
                    band = (rad < 0.10) & (t > 0.05 * L) & (t < 1.05 * L)
                    hand = hand_mass(D, hand_sub)
                    if a is not None:
                        arm = arm_mass(D, hand_sub, es)
                        hand = hand.copy()
                        hand[roi] = arm[roi] * smoothstep(a, b, t[roi] / L)
                    # roll 180 about the arm axis
                    Rr = R_axis(axis, np.pi)
                    droll = hand[:, None] * ((D['pos'] - W) @ Rr.T - (D['pos'] - W))
                    # bend 30 about a transverse axis
                    z = np.array([0.0, 0.0, 1.0])
                    ra = np.cross(axis, z)
                    if np.linalg.norm(ra) < 1e-8:
                        ra = np.cross(axis, np.array([0.0, 1.0, 0.0]))
                    ra /= np.linalg.norm(ra)
                    Rb = R_axis(ra, np.deg2rad(30))
                    dbend = hand[:, None] * ((D['pos'] - W) @ Rb.T - (D['pos'] - W))
                    mid = roi & (t >= 0.25 * L) & (t < 0.70 * L)
                    for mat, t0, nt in D['secs']:
                        if mat not in active:
                            continue
                        tri = D['idx'][t0:t0 + nt]
                        ed = np.concatenate((tri[:, [0, 1]], tri[:, [1, 2]], tri[:, [2, 0]]))
                        eb = band[ed].all(axis=1)
                        if eb.sum():
                            ee = ed[eb]
                            dl, l0, l1 = edge_len_delta(D['pos'], droll, ee)
                            tmid = 0.5 * (t[ee[:, 0]] + t[ee[:, 1]])
                            shell_roll[mat].append((float(np.percentile(dl, 99)),
                                                    float(dl.max()),
                                                    worst_where(dl, tmid, L)))
                        # bend displacement of this shell's verts in the mid window
                        vv = np.unique(tri)
                        mm = mid[vv]
                        if mm.sum():
                            shell_bend[mat].append(float(np.median(np.linalg.norm(dbend[vv[mm]], axis=1)) * 1000))
                    # shell sliding: centroid displacement spread per longitudinal bin
                    bins = np.linspace(0.10, 1.00, 10) * L
                    worst = 0.0
                    for lo, hi in zip(bins[:-1], bins[1:]):
                        cs = []
                        for mat, t0, nt in D['secs']:
                            tri = D['idx'][t0:t0 + nt]
                            vv = np.unique(tri)
                            sel = vv[(t[vv] >= lo) & (t[vv] < hi) & (rad[vv] < 0.10)]
                            if len(sel) >= 8:
                                cs.append(dbend[sel].mean(axis=0))
                        for i in range(len(cs)):
                            for j in range(i + 1, len(cs)):
                                worst = max(worst, float(np.linalg.norm(cs[i] - cs[j]) * 1000))
                    slide.append(worst)
                parts = []
                for mat in active:
                    if shell_roll[mat]:
                        p99 = np.mean([x[0] for x in shell_roll[mat]])
                        mx = np.mean([x[1] for x in shell_roll[mat]])
                        wt = np.nanmean([x[2] for x in shell_roll[mat]])
                        bnd = np.mean(shell_bend[mat]) if shell_bend[mat] else float('nan')
                        parts.append('%s b%4.1f r%4.1f/%4.1f @%.2f'
                                     % (sec_names.get(mat, 's%d' % mat)[:10], bnd, p99, mx, wt))
                print('  %-10s slide %5.1f mm | %s' % (label, np.mean(slide), '  |  '.join(parts)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
