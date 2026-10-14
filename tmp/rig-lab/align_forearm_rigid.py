# -*- coding: utf-8 -*-
"""Round 75c: put the forearm on the bone axis with a RIGID move, not a bend.

What went wrong in round 75
---------------------------
Round 75 displaced every vertex by  -alpha(t/L) * limbWeight(v) * e(t)  with e(t)
the PER-STATION tube offset.  e(t) falls from ~41 mm at the elbow to ~23 mm at
the wrist, and alpha ramped in between t/L 0.34 and 0.62.  So the mesh was left
alone over the first 40 % of the forearm and then shoved 30 mm over the next
80 mm -- a kink.  Measured on the shipped package:

    forearm centreline straightness rms   3.90 mm -> 7.69 mm
    tilt vs the bone axis                 4.96 deg -> 12.90 deg
    mesh V interior angle                146.76 deg -> 152.94 deg

The user saw exactly that: "you did not extend the upper arm, you pushed the
forearm outward from its middle, so the arm became a W".

What this does instead
----------------------
A RIGID move of everything distal to the elbow:

    d(v) = -strength * alpha(t/L) * limbWeight(v) * E_bar

with E_bar a SINGLE constant vector -- the mean of the measured offsets, not the
per-station e(t).  Because d no longer depends on t, the forearm is translated,
not sheared: the V survives, the forearm stays straight, and the only place that
deforms at all is the weight ramp near the elbow.

Why the mean and not the per-station value
------------------------------------------
Decomposing the offset into the plane spanned by the two bone axes:

    vanilla   mean 9.9 mm   along the upper arm -3.3 mm
    ours      mean 31.2 mm  along the upper arm -23.7 mm

The excess sits almost entirely along the UPPER ARM direction, pointing away
from the shoulder -- i.e. the mesh behaves as if the upper arm were ~24 mm too
short.  Correcting it therefore moves the elbow a little further out, exactly
the behaviour the user asked for.

usage:
    align_forearm_rigid.py --src <patch_0> --out <dir> --name <name>
        [--side left] [--unit 0x...] [--strength 1.0]
        [--limb-bones subtree] [--mean-lo 0.12] [--mean-hi 0.96]
        [--t0 -1.20] [--t1 1.40] [--fade 0.02]
        [--fit-slab 0.04] [--fit-step 0.02] [--fit-radius 0.12]
        [--gate-radius 0.16] [--max-move 0.06] [--rot-deg 0]
        [--no-normals] [--report-edges] [--measure-only] [--dry-run]
"""
import argparse
import json
import os
import struct
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_hash as H
import hd2_patch as HP
import hd2_patchw as PW
import hd2_unit as U
import hd2_unitbuild as UB

SDK = 'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
PATCH_NAME = '9ba626afa44a3aa3.patch_0'
BONES = H.load_bonehash(os.path.join(SDK, 'hashlists', 'bonehash.txt'))

SIDES = (('left', 'l'), ('right', 'r'))


def log(m=''):
    print(m, flush=True)


# --------------------------------------------------------------- unit helpers
def bone_table(u):
    ti = u.transform_info
    bi = u.bone_infos[0]
    names, heads = {}, {}
    for slot, ti_idx in enumerate(bi.real_indices):
        names[slot] = BONES.get(ti.hashes[ti_idx], '0x%08x' % ti.hashes[ti_idx])
        m = ti.matrices[ti_idx]
        heads[slot] = np.array([m[12], m[13], m[14]], np.float64)
    slot_of = {v: k for k, v in names.items() if not v.startswith('0x')}
    return ti, bi, names, heads, slot_of


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


def model_streams(u):
    return [i for i, s in enumerate(u.stream_infos)
            if s.num_vertices > 1000
            and any(c.type == 7 for c in s.components)
            and any(c.type == 6 for c in s.components)]


def field_offsets(u, si):
    off, out = 0, {}
    for c in u.stream_infos[si].components:
        if c.type == 0 and 'pos' not in out:
            out['pos'] = off
        elif c.type == 1 and 'normal' not in out:
            out['normal'] = off
        elif c.type == 7:
            out['bw'] = off
        elif c.type == 6:
            out['bi'] = off
        off += U.component_size(c.format, u.version)
    return out


def remaps_identity(u):
    for bi in u.bone_infos:
        for remap in bi.remaps:
            if list(remap) != list(range(len(remap))):
                return False
    return True


def live_lod0(u):
    ti, bi = u.transform_info, u.bone_infos[0]
    mi = max((m for m in u.mesh_infos if m.lod_index == 0),
             key=lambda m: m.num_sections)
    verts = u.decode_vertices(mi.stream_index)
    n = len(verts)
    pos_local = np.asarray([v['position'] for v in verts], np.float64)
    nor_raw = np.asarray([v['normal'] for v in verts], np.uint32)
    bidx = np.asarray([v['bone_index0'] for v in verts], np.int64)
    wgt = np.asarray([v['bone_weight'] for v in verts], np.float64)

    mat_of = np.full(n, -1, np.int32)
    flat = np.asarray(u.index_list(mi.stream_index), np.int64)
    for s in mi.sections:
        sv = np.unique(flat[s.index_offset:s.index_offset + s.num_indices])
        mat_of[sv] = s.material_index

    decoded = bidx.copy()
    for material in np.unique(mat_of[mat_of >= 0]):
        which = mat_of == material
        remap = np.asarray(bi.remaps[material], np.int64)
        decoded[which] = remap[np.minimum(bidx[which], len(remap) - 1)]

    M = np.asarray(ti.matrices[mi.transform_index], np.float64).reshape(4, 4)
    R, tr = M[:3, :3], M[3, :3]
    pos_unit = pos_local @ R + tr
    return dict(pos=pos_unit, pos_local=pos_local, nor_raw=nor_raw, wgt=wgt,
                dec=decoded, bidx=bidx, mat=mat_of, R=R, tr=tr, mi=mi, n=n,
                live=np.nonzero(mat_of >= 0)[0])


# ------------------------------------------------------------------ geometry
def circle_fit_2d(x, y):
    A = np.column_stack([2.0 * x, 2.0 * y, np.ones_like(x)])
    b = x * x + y * y
    sol, *_ = np.linalg.lstsq(A, b, rcond=None)
    cx, cy = float(sol[0]), float(sol[1])
    r = float(np.sqrt(max(sol[2] + cx * cx + cy * cy, 0.0)))
    d = np.hypot(x - cx, y - cy)
    return cx, cy, r, float(np.sqrt(((d - d.mean()) ** 2).mean()))


def frame(E, W):
    ax = W - E
    L = float(np.linalg.norm(ax))
    ax = ax / L
    tmp = np.array([0.0, 0.0, 1.0])
    if abs(float(ax @ tmp)) > 0.9:
        tmp = np.array([0.0, 1.0, 0.0])
    N_ = np.cross(ax, tmp); N_ /= np.linalg.norm(N_)
    V_ = np.cross(N_, ax); V_ /= np.linalg.norm(V_)
    return ax, N_, V_, L


def fade_alpha(tl, t0, t1, fw):
    up = np.clip((tl - t0) / fw, 0.0, 1.0)
    dn = np.clip((t1 - tl) / fw, 0.0, 1.0)
    a = 0.5 - 0.5 * np.cos(np.pi * np.minimum(up, dn))
    a[(tl <= t0) | (tl >= t1)] = 0.0
    return a


def oct_decode(u32):
    a = np.asarray(u32, np.uint32)
    x = (a & 0x3FF).astype(np.float64) / 511.5 - 1.0
    y = ((a >> 10) & 0x3FF).astype(np.float64) / 511.5 - 1.0
    z = 1.0 - (np.abs(x) + np.abs(y))
    tt = np.clip(-z, 0.0, 1.0)
    x = x + np.where(x >= 0, -tt, tt)
    y = y + np.where(y >= 0, -tt, tt)
    n = np.stack([x, y, z], axis=1)
    return n / np.maximum(np.linalg.norm(n, axis=1, keepdims=True), 1e-30)


def lod0_edges(u, mi):
    flat = np.asarray(u.index_list(mi.stream_index), np.int64).reshape(-1, 3)
    e = np.concatenate([flat[:, [0, 1]], flat[:, [1, 2]], flat[:, [2, 0]]])
    e = np.sort(e, axis=1)
    e = np.unique(e, axis=0)
    return e[:, 0], e[:, 1]


def edge_stretch_report(pos0, pos1, edges, t, tl, moved, limbw=None, dec=None,
                        wgt=None, names=None):
    i, j = edges
    l0 = np.linalg.norm(pos0[i] - pos0[j], axis=1)
    l1 = np.linalg.norm(pos1[i] - pos1[j], axis=1)
    ok = l0 > 1e-6
    ratio = np.ones_like(l0)
    ratio[ok] = l1[ok] / l0[ok]
    touch = moved[i] | moved[j]
    r = ratio[touch]
    if not len(r):
        log('  edges: none touched')
        return
    order = np.argsort(-np.abs(r - 1.0))
    idx = np.nonzero(touch)[0][order]
    log('  edges touched: %d of %d   length ratio  min %.3f  p99 %.3f  max %.3f'
        % (len(r), len(l0), r.min(), np.percentile(r, 99), r.max()))
    log('    worst 8:')

    def dom(v):
        if dec is None or names is None or wgt is None:
            return '?'
        return names.get(int(dec[v, int(np.argmax(wgt[v]))]), '?')

    for k in idx[:8]:
        a, b = int(i[k]), int(j[k])
        extra = ''
        if limbw is not None:
            extra = ('   limbW %.3f/%.3f   dom %s/%s'
                     % (limbw[a], limbw[b], dom(a), dom(b)))
        log('      t/L %.2f  t %7.1f mm  len %.2f -> %.2f mm  x%.3f%s'
            % (tl[a], t[a] * 1000, l0[k] * 1000, l1[k] * 1000, ratio[k], extra))


def rotation_matrix(axis, deg):
    a = np.asarray(axis, np.float64)
    a = a / max(np.linalg.norm(a), 1e-30)
    th = np.radians(deg)
    K = np.array([[0.0, -a[2], a[1]], [a[2], 0.0, -a[0]], [-a[1], a[0], 0.0]])
    return np.eye(3) + np.sin(th) * K + (1.0 - np.cos(th)) * (K @ K)


# ---------------------------------------------------------------------- main
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--src', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--name', default='forearm-rigid')
    ap.add_argument('--unit', action='append', default=[])
    ap.add_argument('--side', default='left', choices=('left', 'right', 'both'))
    ap.add_argument('--strength', type=float, default=1.0)
    ap.add_argument('--limb-bones', default='subtree',
                    choices=('subtree', 'forearm'),
                    help='subtree: everything distal to the elbow moves rigidly, so '
                         'the wrist cannot shear.  forearm: only l_elbow + '
                         'l_hand_twist, the hand is then left behind.')
    ap.add_argument('--mean-lo', type=float, default=0.12,
                    help='lowest t/L station that feeds the mean offset')
    ap.add_argument('--mean-hi', type=float, default=0.96)
    ap.add_argument('--t0', type=float, default=-2.0,
                    help='alpha window.  Keep it wider than the whole limb: the fade '
                         'must come from the skin weights (limbW), otherwise the '
                         'window edge cuts through the hand and tears it.')
    ap.add_argument('--t1', type=float, default=2.0)
    ap.add_argument('--fade', type=float, default=0.02)
    ap.add_argument('--fit-slab', type=float, default=0.04)
    ap.add_argument('--fit-step', type=float, default=0.02)
    ap.add_argument('--fit-radius', type=float, default=0.12)
    ap.add_argument('--gate-radius', type=float, default=0.50)
    ap.add_argument('--max-move', type=float, default=0.06)
    ap.add_argument('--fit-rot', type=int, default=1,
                    help='1: also solve for a rigid rotation about the elbow, so the '
                         'residual offset is minimised by a translation + rotation '
                         '(both rigid, so the forearm still cannot bend).  0: pure '
                         'translation.')
    ap.add_argument('--rot-deg', type=float, default=0.0,
                    help='extra manual rotation about the elbow, on top of --fit-rot, '
                         'about the normal of the bone V plane, in degrees')
    ap.add_argument('--no-normals', action='store_true')
    ap.add_argument('--report-edges', action='store_true')
    ap.add_argument('--measure-only', action='store_true')
    ap.add_argument('--dry-run', action='store_true')
    args = ap.parse_args()

    units = [int(x, 16) for x in args.unit] or [0xcae35ee15168145f, 0x9841b16544c8fbae]
    sides = [s for s, _p in SIDES] if args.side == 'both' else [args.side]

    entries, _ = HP.scan_entries(args.src)
    by_key = {(e['type_id'], e['file_id']): e for e in entries}

    e0 = by_key[(U.UNIT_ID, units[0])]
    pl0 = HP.entry_payload(e0, args.src)
    u0 = U.Unit.read(pl0['toc'], pl0['gpu'])
    info0 = live_lod0(u0)
    ti0, bi0, names0, heads0, slot_of0 = bone_table(u0)
    ident = remaps_identity(u0)
    log('unit 0x%016x: %d vertices on LOD0, %d live, remaps identity=%s'
        % (units[0], info0['n'], len(info0['live']), ident))

    plans = {}
    for side, pre in SIDES:
        if side not in sides:
            continue
        el, hd = pre + '_elbow', pre + '_hand'
        E, W = heads0[slot_of0[el]], heads0[slot_of0[hd]]
        ax, N_, V_, L = frame(E, W)

        rel = info0['pos'] - E
        t = rel @ ax
        perp = rel - np.outer(t, ax)
        rad = np.linalg.norm(perp, axis=1)
        x2, y2 = perp @ N_, perp @ V_
        tl = t / L

        subtree = set(subtree_slots(ti0, bi0, slot_of0[el]))
        if args.limb_bones == 'subtree':
            limb_slots = subtree
        else:
            limb_slots = {slot_of0[el], slot_of0[pre + '_hand_twist']}
        limbw = (np.isin(info0['dec'], list(limb_slots)) * info0['wgt']).sum(axis=1)

        mask = np.zeros(info0['n'], bool)
        mask[info0['live']] = True
        mask &= limbw > 0.6

        # ---- station fits -----------------------------------------------------
        ts, cxs, cys, ns, rmss, wl, we, wt, wh = [], [], [], [], [], [], [], [], []
        k0, k1 = int(np.floor(-0.30 / args.fit_step)), int(np.ceil(1.20 / args.fit_step))
        for k in range(k0, k1 + 1):
            c = k * args.fit_step * L
            m = mask & (np.abs(t - c) < args.fit_slab * L) & (rad < args.fit_radius)
            if int(m.sum()) < 10:
                continue
            cx, cy, r, rms = circle_fit_2d(x2[m], y2[m])
            ts.append(c / L); cxs.append(cx); cys.append(cy)
            ns.append(int(m.sum())); rmss.append(rms)
            wl.append(float(limbw[m].mean()))
            we.append(float((np.isin(info0['dec'][m], [slot_of0[el]])
                             * info0['wgt'][m]).sum(axis=1).mean()))
            wt.append(float((np.isin(info0['dec'][m], [slot_of0[pre + '_hand_twist']])
                             * info0['wgt'][m]).sum(axis=1).mean()))
            wh.append(float((np.isin(info0['dec'][m], subtree
                                     - {slot_of0[el], slot_of0[pre + '_hand_twist']})
                             * info0['wgt'][m]).sum(axis=1).mean()))
        ts = np.asarray(ts); cxs = np.asarray(cxs); cys = np.asarray(cys)
        ns = np.asarray(ns); rmss = np.asarray(rmss)
        good = (ns >= 10) & (rmss <= 0.006)
        if not good.any():
            raise SystemExit('%s: no clean station' % side)
        cxs_s = np.interp(ts, ts[good], cxs[good])
        cys_s = np.interp(ts, ts[good], cys[good])
        good2 = good & (ts >= args.mean_lo) & (ts <= args.mean_hi)
        if not good2.any():
            raise SystemExit('%s: no clean station inside the mean window' % side)

        # ---- the rigid move: translation + rotation about the elbow ----------
        # minimise  sum_i n_i * |e_i + tau + psi * t_i * u_hat|^2
        # u_hat = P_ x ax is the direction a point at parameter t travels when the
        # distal block rotates about the elbow, so the rotation is free at the
        # elbow and the block never bends.
        P_ = np.cross(heads0[slot_of0[pre + '_shoulder']] - E, ax) \
            if pre + '_shoulder' in slot_of0 else np.cross(N_, ax)
        P_ = P_ / max(np.linalg.norm(P_), 1e-30)
        u_hat = np.cross(P_, ax)
        ux, uy = float(u_hat @ N_), float(u_hat @ V_)

        sel = good2
        ti = ts[sel]
        wi = ns[sel].astype(np.float64)
        exi, eyi = cxs_s[sel], cys_s[sel]
        ncol = 3 if args.fit_rot else 2
        A = np.zeros((2 * len(ti), ncol))
        b = np.zeros(2 * len(ti))
        A[0::2, 0] = 1.0
        A[1::2, 1] = 1.0
        if args.fit_rot:
            A[0::2, 2] = ti * ux
            A[1::2, 2] = ti * uy
        b[0::2] = -exi
        b[1::2] = -eyi
        sw = np.repeat(np.sqrt(np.maximum(wi, 1.0)), 2)
        sol, *_ = np.linalg.lstsq(A * sw[:, None], b * sw, rcond=None)
        ex_bar, ey_bar = float(sol[0]), float(sol[1])
        psi = float(sol[2]) if args.fit_rot else 0.0
        psi += np.radians(args.rot_deg)
        psi_deg = float(np.degrees(psi))

        def resid_at(k):
            s = args.strength
            dx = cxs_s[k] + s * (ex_bar + psi * ts[k] * ux)
            dy = cys_s[k] + s * (ey_bar + psi * ts[k] * uy)
            return float(np.hypot(dx, dy))

        r = np.array([resid_at(k) for k in np.nonzero(sel)[0]])

        log()
        log('=' * 104)
        log('%s  elbow -> hand   L = %.1f mm    RIGID move of the whole distal block'
            % (side, L * 1000))
        log('=' * 104)
        log('  least squares over t/L %.2f..%.2f (%d stations):'
            % (args.mean_lo, args.mean_hi, int(good2.sum())))
        log('    translation  %+.1f mm along N_   %+.1f mm along V_   |tau| = %.1f mm'
            % (ex_bar * 1000, ey_bar * 1000, np.hypot(ex_bar, ey_bar) * 1000))
        if args.fit_rot:
            log('    rotation     %+.2f deg about the elbow (axis P_, through the '
                'elbow head -> zero displacement there)' % psi_deg)
        log('    residual offset  mean %.1f mm   rms %.1f mm   max %.1f mm'
            % (r.mean() * 1000, np.sqrt((r ** 2).mean()) * 1000, r.max() * 1000))
        log('    residual orbit at a 90 deg twist:  mean %.1f mm   max %.1f mm'
            % (2 * r.mean() * np.sin(np.radians(45)) * 1000,
               2 * r.max() * np.sin(np.radians(45)) * 1000))
        log()
        log('  %6s %6s %8s %8s %8s %8s   %8s %8s %8s   %9s %9s'
            % ('t/L', 'n', 'off(mm)', 'dir(deg)', 'rms(mm)', 'limbW',
               'w_elbow', 'w_twist', 'w_hand', 'resid', 'orbit@90'))
        for k in range(len(ts)):
            resid = resid_at(k)
            log('  %6.2f %6d %8.1f %8.1f %8.2f %8.3f   %8.3f %8.3f %8.3f   %9.1f %9.1f'
                % (ts[k], ns[k], float(np.hypot(cxs_s[k], cys_s[k])) * 1000,
                   np.degrees(np.arctan2(cys_s[k], cxs_s[k])),
                   rmss[k] * 1000, wl[k], we[k], wt[k], wh[k],
                   resid * 1000, 2.0 * resid * np.sin(np.radians(45.0)) * 1000))

        plans[side] = dict(pre=pre, E=E, W=W, ax=ax, N_=N_, V_=V_, L=L, P_=P_,
                           ex_bar=ex_bar, ey_bar=ey_bar, psi=psi,
                           limb_slots=sorted(limb_slots))

    if args.measure_only:
        log()
        log('measure only: nothing written')
        return 0
    if args.dry_run:
        log()
        log('dry run: nothing written')
        return 0

    # ---- write ---------------------------------------------------------------
    out_entries, report = [], []
    for e in entries:
        pl = HP.entry_payload(e, args.src)
        if e['type_id'] != U.UNIT_ID or e['file_id'] not in units:
            out_entries.append(PW.Entry(e['type_id'], e['file_id'], pl['toc'],
                                        pl['stream'], pl['gpu'],
                                        e['unknown1'], e['unknown2'], note='verbatim'))
            continue
        uu = U.Unit.read(pl['toc'], bytes(pl['gpu']))
        inf = live_lod0(uu)
        tiu, biu, namesu, headsu, slot_ofu = bone_table(uu)
        need = [plans[s]['pre'] + '_elbow' for s in plans]
        if any(nm not in slot_ofu for nm in need):
            log('unit 0x%016x: no %s, left verbatim' % (e['file_id'], '/'.join(need)))
            out_entries.append(PW.Entry(e['type_id'], e['file_id'], pl['toc'],
                                        pl['stream'], pl['gpu'],
                                        e['unknown1'], e['unknown2'],
                                        note='verbatim (no arm bones)'))
            continue

        n = inf['n']
        R = inf['R']
        Rinv = np.linalg.inv(R)
        pos_local = inf['pos_local']
        nrm_local = oct_decode(inf['nor_raw'])

        new_pos = pos_local.copy()
        new_nrm = nrm_local.copy()
        moved = np.zeros(n, bool)
        dmax = 0.0

        for side in plans:
            p = plans[side]
            ax, N_, V_, L, E = p['ax'], p['N_'], p['V_'], p['L'], p['E']
            rel = inf['pos'] - E
            t = rel @ ax
            rad = np.linalg.norm(rel - np.outer(t, ax), axis=1)
            tl = t / L

            limb_slots = set(subtree_slots(tiu, biu, slot_ofu[p['pre'] + '_elbow'])) \
                if args.limb_bones == 'subtree' else \
                {slot_ofu[p['pre'] + '_elbow'], slot_ofu[p['pre'] + '_hand_twist']}
            limbw = (np.isin(inf['dec'], list(limb_slots)) * inf['wgt']).sum(axis=1)
            last_limbw = limbw

            al = fade_alpha(tl, args.t0, args.t1, args.fade)
            w = args.strength * al * limbw
            w[rad > args.gate_radius] = 0.0
            if not ident:
                keep = np.zeros(n, bool)
                keep[inf['live']] = True
                w[~keep] = 0.0

            # ---- the rigid move: one translation + one rotation about E --------
            # both terms are rigid, so every vertex of the distal block travels
            # by the same rigid transform and the forearm cannot bend
            ebar = p['ex_bar'] * N_ + p['ey_bar'] * V_
            d3_unit = -w[:, None] * ebar[None, :]

            if abs(p['psi']) > 1e-9:
                Rm = rotation_matrix(p['P_'], np.degrees(p['psi']))
                d3_unit = d3_unit - w[:, None] * ((inf['pos'] - E) @ (Rm - np.eye(3)).T)

            mag = np.linalg.norm(d3_unit, axis=1)
            over = mag > args.max_move
            if over.any():
                d3_unit[over] *= (args.max_move / mag[over])[:, None]
            dmax = max(dmax, float(mag.max()))

            new_pos += d3_unit @ Rinv
            moved |= w > 0.0

            if not args.no_normals:
                # a pure translation does not rotate normals; only the alpha ramp
                # does, so correct with the derivative of alpha along t
                dt = 0.0005
                al1 = fade_alpha(tl + dt / L, args.t0, args.t1, args.fade)
                db = -(args.strength * al1 * limbw)[:, None] * ebar[None, :]
                uvec = (db - d3_unit) / dt
                u_loc = uvec @ Rinv
                a_loc = ax @ Rinv
                dot = (nrm_local * u_loc).sum(axis=1)
                nn = nrm_local - a_loc[None, :] * dot[:, None]
                nn = nn / np.maximum(np.linalg.norm(nn, axis=1, keepdims=True), 1e-30)
                sel = (w > 0.0) & (np.abs(al1 - al) > 0.0)
                new_nrm[sel] = nn[sel]

        log()
        log('unit 0x%016x: %d vertices moved, max |d| = %.1f mm'
            % (e['file_id'], int(moved.sum()), dmax * 1000))
        if args.report_edges and moved.any():
            p0 = plans[list(plans)[0]]
            rel = inf['pos'] - p0['E']
            tt = rel @ p0['ax']
            edge_stretch_report(inf['pos_local'], new_pos,
                                lod0_edges(uu, inf['mi']), tt, tt / p0['L'], moved,
                                limbw=last_limbw, dec=inf['dec'], wgt=inf['wgt'],
                                names=namesu)

        gpu = bytearray(pl['gpu'])
        uu2 = U.Unit.read(pl['toc'], bytes(gpu))
        for s_i in model_streams(uu2):
            si_ = uu2.stream_infos[s_i]
            fo = field_offsets(uu2, s_i)
            stride = si_.vertex_stride
            nv = si_.num_vertices
            assert nv == n, 'stream %d has %d vertices, expected %d' % (s_i, nv, n)
            base0 = si_.vertex_buffer_offset
            enc = UB.oct_encode(new_nrm)
            for i in np.nonzero(moved)[0]:
                base = base0 + i * stride
                struct.pack_into('<3f', gpu, base + fo['pos'],
                                 float(new_pos[i, 0]), float(new_pos[i, 1]),
                                 float(new_pos[i, 2]))
                if not args.no_normals:
                    struct.pack_into('<I', gpu, base + fo['normal'], int(enc[i]))
        out_entries.append(PW.Entry(e['type_id'], e['file_id'], pl['toc'],
                                    pl['stream'], bytes(gpu),
                                    e['unknown1'], e['unknown2'],
                                    note='unit:%s forearm rigid align' % args.name))
        report.append(dict(unit='%016x' % e['file_id'], moved=int(moved.sum()),
                           maxMoveMm=dmax * 1000))

    res = PW.write_patch(out_entries, args.src, args.out, PATCH_NAME)
    src_gpu = os.path.getsize(args.src + '.gpu_resources')
    log('wrote %s (%d B) + gpu (%d B; source %d)'
        % (res['toc'], res['toc_size'], res['gpu_size'], src_gpu))
    assert res['gpu_size'] == src_gpu, 'gpu blob size changed'

    meta = dict(schema='hd2mod.rig-lab-forearm-rigid', sourcePatch=args.src,
                units=['%016x' % x for x in units], sides=list(plans),
                params=dict(strength=args.strength, limb_bones=args.limb_bones,
                            mean_lo=args.mean_lo, mean_hi=args.mean_hi,
                            t0=args.t0, t1=args.t1, fade=args.fade,
                            fit_slab=args.fit_slab, fit_step=args.fit_step,
                            fit_radius_m=args.fit_radius,
                            gate_radius_m=args.gate_radius,
                            max_move_m=args.max_move, fit_rot=bool(args.fit_rot),
                            rot_deg_extra=args.rot_deg,
                            normals=not args.no_normals),
                offsets={s: dict(alongN_mm=plans[s]['ex_bar'] * 1000,
                                 alongV_mm=plans[s]['ey_bar'] * 1000,
                                 rot_deg=np.degrees(plans[s]['psi']))
                         for s in plans},
                report=report, outputPatch=res['toc'], tocBytes=res['toc_size'],
                gpuBytes=res['gpu_size'])
    os.makedirs(args.out, exist_ok=True)
    with open(os.path.join(args.out, args.name + '.manifest.json'), 'w',
              encoding='utf-8') as f:
        json.dump(meta, f, indent=2, ensure_ascii=False)
    log('manifest %s' % os.path.join(args.out, args.name + '.manifest.json'))
    return 0


if __name__ == '__main__':
    sys.exit(main())
