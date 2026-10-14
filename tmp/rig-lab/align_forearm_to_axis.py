# -*- coding: utf-8 -*-
"""Round 75: pull the forearm mesh back onto the elbow -> wrist bone axis.

Root cause (round 75)
---------------------
The wrist "clock hand" the user reports -- "the stem sits at 2 o'clock, the tip
at 3" while the wrist twists about the arm axis -- is not a weight problem.
Round 75 measured the effective turn factor f = w_hand + 0.78 * w_twist and the
wrist band matches vanilla to 0.002.  The bones are bit-identical to vanilla's
arm skin.  What is NOT identical is where the mesh sits:

    least-squares circle fit of each forearm cross-section, offset of the
    fitted tube axis from the elbow->wrist bone axis

        t/L     ours     vanilla
        0.10    42.6 mm   15.3 mm
        0.30    35.8      12.6
        0.50    33.4      10.9
        0.70    27.8       7.0
        0.90    24.0       6.3
        1.00    22.1      12.3
        (beyond the wrist both curves rise together, 43/43 at t=330 mm --
         that part is the hand and is correctly placed, so it is left alone)

Our forearm tube runs ~25 mm further off the bone than vanilla's.  A twist
rotates the tube about the BONE axis, so an eccentric tube does not spin in
place: it orbits by 2 * e * sin(theta/2) -- 40 mm at a 90 deg twist for e = 30 mm
-- and because e changes along the arm, two rings orbit by different amounts and
the surface between them shears.  That shear is the bent "clock hand".

What this writes
----------------
For every vertex of the elbow subtree, inside a t/L window with a smooth fade at
both ends:

    d(v) = -strength * alpha(t/L) * limb_weight(v) * e(t)

  e(t)   fitted tube-axis offset, a 2-D vector in the plane perpendicular to the
         elbow->wrist axis, smoothed over the station fits
  alpha  cosine fade: 0 at --t0, 1 by --t0+--fade, 1 until --t1---fade,
         0 at --t1.  The default window keeps the elbow and the wrist junction
         exactly where they are, so no seam can open there.
  limb_weight  the vertex's total weight on the elbow subtree.  This makes the
         elbow end of the correction follow the skin weights instead of a hard
         bone mask, so a shoulder/elbow blend vertex moves half as far.

Vertex normals are corrected too, by the inverse transpose of the local
deformation (which reduces to  n' = normalize(n - axis * (d'(t) . n))  because
the displacement is always perpendicular to the axis).

Nothing else changes: topology, UVs, weights, skeleton, materials and the TOC are
untouched, and the GPU blob keeps its size byte for byte.

Usage:
    python align_forearm_to_axis.py --src <patch_0> --out <dir> --name <name>
        [--side left] [--unit 0x...] [--strength 1.0]
        [--t0 0.06] [--t1 0.98] [--fade 0.16]
        [--fit-slab 0.04] [--fit-step 0.02] [--fit-radius 0.12]
        [--gate-radius 0.16] [--max-move 0.06] [--measure-only] [--dry-run]
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
    """-> {'pos':.., 'normal':.., 'bw':.., 'bi':..} byte offsets inside the stride."""
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
    """-> dict with unit-space and local positions, weights, decoded slots,
    material per vertex and the mesh transform, for the LOD0 mesh."""
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
    """Kasa algebraic fit -> (cx, cy, r, rms)."""
    A = np.column_stack([2.0 * x, 2.0 * y, np.ones_like(x)])
    b = x * x + y * y
    sol, *_ = np.linalg.lstsq(A, b, rcond=None)
    cx, cy = float(sol[0]), float(sol[1])
    r = float(np.sqrt(max(sol[2] + cx * cx + cy * cy, 0.0)))
    d = np.hypot(x - cx, y - cy)
    return cx, cy, r, float(np.sqrt(((d - d.mean()) ** 2).mean()))


def rodrigues(w):
    """Rotation matrix (column-vector convention) from a rotation vector."""
    th = float(np.linalg.norm(w))
    if th < 1e-12:
        return np.eye(3)
    k = np.asarray(w, np.float64) / th
    K = np.array([[0.0, -k[2], k[1]],
                  [k[2], 0.0, -k[0]],
                  [-k[1], k[0], 0.0]])
    return np.eye(3) + np.sin(th) * K + (1.0 - np.cos(th)) * (K @ K)


def fit_offset_profile(x2, y2, t, rad, mask, L, slab, step, fit_radius,
                       min_n=10, max_rms=0.004):
    """-> (stations_t_over_L, cx, cy, n, rms) for the clean stations only."""
    ts, cxs, cys, ns, rmss = [], [], [], [], []
    k0, k1 = int(np.floor(-0.20 / step)), int(np.ceil(1.20 / step))
    for k in range(k0, k1 + 1):
        c = k * step * L
        m = mask & (np.abs(t - c) < slab * L) & (rad < fit_radius)
        if int(m.sum()) < min_n:
            continue
        cx, cy, r, rms = circle_fit_2d(x2[m], y2[m])
        ts.append(c / L)
        cxs.append(cx)
        cys.append(cy)
        ns.append(int(m.sum()))
        rmss.append(rms)
    return (np.asarray(ts), np.asarray(cxs), np.asarray(cys),
            np.asarray(ns), np.asarray(rmss))


def smooth_series(ts, vals, good, win=3):
    """Interpolate the rejected stations, then a short moving average."""
    if not good.any():
        return vals
    out = np.interp(ts, ts[good], vals[good])
    if win > 1:
        pad = win // 2
        ext = np.concatenate([np.full(pad, out[0]), out, np.full(pad, out[-1])])
        ker = np.ones(win) / win
        out = np.convolve(ext, ker, mode='valid')
    return out


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
    """-> (i, j) unique undirected edges of the LOD0 index buffer."""
    flat = np.asarray(u.index_list(mi.stream_index), np.int64).reshape(-1, 3)
    e = np.concatenate([flat[:, [0, 1]], flat[:, [1, 2]], flat[:, [2, 0]]])
    e = np.sort(e, axis=1)
    e = np.unique(e, axis=0)
    return e[:, 0], e[:, 1]


def edge_stretch_report(pos0, pos1, edges, t, tl, moved):
    """Print the worst edge-length changes; a tear shows up as a huge ratio."""
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
    for k in idx[:8]:
        log('      t/L %.2f  t %7.1f mm  len %.2f -> %.2f mm  x%.3f'
            % (tl[i[k]], t[i[k]] * 1000, l0[k] * 1000, l1[k] * 1000, ratio[k]))


# ---------------------------------------------------------------------- main
def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--src', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--name', default='forearm-align')
    ap.add_argument('--unit', action='append', default=[],
                    help='unit id to edit (repeatable); default both RE-2310 units')
    ap.add_argument('--side', default='left', choices=('left', 'right', 'both'))
    ap.add_argument('--strength', type=float, default=1.0,
                    help='fraction of the measured eccentricity removed (1 = centred)')
    ap.add_argument('--t0', type=float, default=0.06, help='fade starts here, in t/L')
    ap.add_argument('--t1', type=float, default=0.98, help='fade ends here, in t/L')
    ap.add_argument('--fade', type=float, default=0.16, help='cosine fade width, in t/L')
    ap.add_argument('--fit-slab', type=float, default=0.04, help='slab half-width, in t/L')
    ap.add_argument('--fit-step', type=float, default=0.02, help='station spacing, in t/L')
    ap.add_argument('--fit-radius', type=float, default=0.12,
                    help='only vertices this close to the bone axis feed the fit')
    ap.add_argument('--gate-radius', type=float, default=0.16,
                    help='never move a vertex further than this from the bone axis')
    ap.add_argument('--max-move', type=float, default=0.06, help='clamp on |d| in metres')
    ap.add_argument('--no-normals', action='store_true',
                    help='skip the normal correction (for A/B only)')
    ap.add_argument('--measure-only', action='store_true')
    ap.add_argument('--limb-bones', default='subtree', choices=('subtree', 'forearm'),
                    help='subtree: scale the correction by the weight on the whole '
                         'elbow subtree (the hand moves with the wrist).  forearm: '
                         'only l_elbow + l_hand_twist, so the hand and fingers are '
                         'never displaced (the wrist then gets a partial correction).')
    ap.add_argument('--mode', default='offset', choices=('offset', 'rotate'),
                    help='offset (round 75/75b): displace each station by its own '
                         'measured offset, so the WHOLE forearm lands on the bone '
                         'axis -- the elbow end moves ~44 mm and creases.  '
                         'rotate (round 76): keep the elbow joint as a fixed pivot '
                         'and rotate the whole forearm rigidly about it so the WRIST '
                         'lands on the bone axis.  Displacement is 0 at the elbow and '
                         'grows linearly to the wrist, so no new bend and no elbow crease.')
    ap.add_argument('--pivot-lo', type=float, default=0.86,
                    help='rotate mode: lower t/L bound of the window whose offsets the '
                         'rotation is fitted to cancel (the wrist band)')
    ap.add_argument('--pivot-hi', type=float, default=1.06,
                    help='rotate mode: upper t/L bound of that window')
    ap.add_argument('--report-edges', action='store_true',
                    help='print the worst edge-length changes (tear check)')
    ap.add_argument('--dry-run', action='store_true')
    args = ap.parse_args()

    units = [int(x, 16) for x in args.unit] or [0xcae35ee15168145f, 0x9841b16544c8fbae]
    sides = [s for s, _p in SIDES] if args.side == 'both' else [args.side]

    entries, _ = HP.scan_entries(args.src)
    by_key = {(e['type_id'], e['file_id']): e for e in entries}

    # ---- measure on the first unit, and report the profile we will use -------
    e0 = by_key[(U.UNIT_ID, units[0])]
    pl0 = HP.entry_payload(e0, args.src)
    u0 = U.Unit.read(pl0['toc'], pl0['gpu'])
    info0 = live_lod0(u0)
    ti0, bi0, names0, heads0, slot_of0 = bone_table(u0)
    ident = remaps_identity(u0)
    log('unit 0x%016x: %d vertices on LOD0, %d live, remaps identity=%s'
        % (units[0], info0['n'], len(info0['live']), ident))
    if not ident:
        log('  WARNING: remaps are not identity; only LOD0-live vertices are safe')

    plans = {}
    for side, pre in SIDES:
        if side not in sides:
            continue
        el, hd = pre + '_elbow', pre + '_hand'
        E, W = heads0[slot_of0[el]], heads0[slot_of0[hd]]
        axis = W - E
        L = float(np.linalg.norm(axis))
        axis = axis / L
        tmp = np.array([0.0, 0.0, 1.0])
        if abs(float(axis @ tmp)) > 0.9:
            tmp = np.array([0.0, 1.0, 0.0])
        N_ = np.cross(axis, tmp); N_ /= np.linalg.norm(N_)
        V_ = np.cross(N_, axis); V_ /= np.linalg.norm(V_)

        rel = info0['pos'] - E
        t = rel @ axis
        perp = rel - np.outer(t, axis)
        rad = np.linalg.norm(perp, axis=1)
        x2, y2 = perp @ N_, perp @ V_

        limb_slots = set(subtree_slots(ti0, bi0, slot_of0[el]))
        limbw = np.isin(info0['dec'], list(limb_slots)) * info0['wgt']
        limbw = limbw.sum(axis=1)

        mask = np.zeros(info0['n'], bool)
        mask[info0['live']] = True
        mask &= limbw > 0.6

        ts, cxs, cys, ns, rmss = fit_offset_profile(
            x2, y2, t, rad, mask, L, args.fit_slab, args.fit_step, args.fit_radius)
        good = (ns >= 10) & (rmss <= 0.004)
        if not good.any():
            raise SystemExit('%s: no clean station, cannot fit the tube axis' % side)
        cxs_s = smooth_series(ts, cxs, good)
        cys_s = smooth_series(ts, cys, good)

        log()
        log('=' * 96)
        log('%s  elbow -> hand   L = %.1f mm   (t/L, fitted tube-axis offset)'
            % (side, L * 1000))
        log('=' * 96)
        log('  %7s %6s %9s %9s %8s %8s %6s' %
            ('t(mm)', 't/L', 'cx(mm)', 'cy(mm)', 'off(mm)', 'rms(mm)', 'used'))
        for k in range(len(ts)):
            log('  %7.0f %6.2f %9.1f %9.1f %8.1f %8.2f %6s'
                % (ts[k] * L * 1000, ts[k], cxs_s[k] * 1000, cys_s[k] * 1000,
                   float(np.hypot(cxs_s[k], cys_s[k])) * 1000,
                   rmss[k] * 1000, 'yes' if good[k] else 'no'))

        # ---- the pivot rotation ------------------------------------------------
        # We want a rotation R about E such that R * (C(t) - E) is parallel to the
        # bone axis for every station t of the chosen window.  C(t) - E = t*axis +
        # e(t) with e(t) perpendicular to axis, so for a small rotation vector
        # omega (perpendicular to axis) the perpendicular residual is
        #     e(t) + t * (w_N * V_ - w_V * N_)
        # (the frame is right-handed with axis x V_ = N_, so N_ x axis = V_ and
        #  V_ x axis = -N_) -- linear in omega, hence a closed-form 2x2 fit.
        sel = (ts >= args.pivot_lo) & (ts <= args.pivot_hi)
        if not sel.any():
            raise SystemExit('%s: pivot window %.2f..%.2f holds no station'
                             % (side, args.pivot_lo, args.pivot_hi))
        tt = ts[sel] * L
        den = float((tt * tt).sum())
        w_v = +float((tt * cxs_s[sel]).sum()) / den
        w_n = -float((tt * cys_s[sel]).sum()) / den
        omega = w_n * N_ + w_v * V_
        R_rot = rodrigues(omega)
        ang_deg = float(np.degrees(np.linalg.norm(omega)))

        log()
        log('  pivot rotation about %s_elbow  (window t/L %.2f..%.2f, %d stations)'
            % (pre, args.pivot_lo, args.pivot_hi, int(sel.sum())))
        log('    angle  %6.2f deg   axis(n_unit) %s'
            % (ang_deg, np.array2string(np.asarray(omega) / max(
                np.linalg.norm(omega), 1e-12), precision=3)))
        log('    the elbow stays put (|p-E| = 0 there); the wrist band moves by '
            'about %.1f mm' % (np.linalg.norm(omega) * L * 1000))

        plans[side] = dict(pre=pre, E=E, W=W, axis=axis, N_=N_, V_=V_, L=L,
                           ts=ts, cxs=cxs_s, cys=cys_s,
                           Rrot=R_rot, omega=omega, rot_deg=ang_deg)

    if args.measure_only:
        log()
        log('measure only: nothing written')
        return 0

    if args.dry_run:
        log()
        log('dry run: nothing written')
        return 0

    # ---- write ---------------------------------------------------------------
    out_entries = []
    report = []
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
            log('unit 0x%016x: no %s, left verbatim'
                % (e['file_id'], '/'.join(need)))
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
            axis, N_, V_, L = p['axis'], p['N_'], p['V_'], p['L']
            rel = inf['pos'] - p['E']
            t = rel @ axis
            rad = np.linalg.norm(rel - np.outer(t, axis), axis=1)
            tl = t / L

            if args.limb_bones == 'subtree':
                limb_slots = set(subtree_slots(tiu, biu,
                                               slot_ofu[p['pre'] + '_elbow']))
            else:
                limb_slots = {slot_ofu[p['pre'] + '_elbow'],
                              slot_ofu[p['pre'] + '_hand_twist']}
            limbw = (np.isin(inf['dec'], list(limb_slots)) * inf['wgt']).sum(axis=1)

            ex = np.interp(tl, p['ts'], p['cxs'])
            ey = np.interp(tl, p['ts'], p['cys'])
            al = fade_alpha(tl, args.t0, args.t1, args.fade)

            w = args.strength * al * limbw
            w[rad > args.gate_radius] = 0.0
            if not ident:
                keep = np.zeros(n, bool)
                keep[inf['live']] = True
                w[~keep] = 0.0

            if args.mode == 'rotate':
                # rigid rotation of the forearm about the elbow joint
                rel_u = inf['pos'] - p['E']
                d3_unit = w[:, None] * (rel_u @ p['Rrot'].T - rel_u)
                mag = np.linalg.norm(d3_unit, axis=1)
                over = mag > args.max_move
                if over.any():
                    d3_unit[over] *= (args.max_move / mag[over])[:, None]
            else:
                d2 = -w[:, None] * np.stack([ex, ey], axis=1)
                mag = np.linalg.norm(d2, axis=1)
                over = mag > args.max_move
                if over.any():
                    d2[over] *= (args.max_move / mag[over])[:, None]
                d3_unit = d2[:, 0:1] * N_ + d2[:, 1:2] * V_
            dmax = max(dmax, float(np.linalg.norm(d3_unit, axis=1).max()))

            d_local = d3_unit @ Rinv
            new_pos += d_local
            moved |= w > 0.0

            if not args.no_normals:
                if args.mode == 'rotate':
                    # a pure rotation carries normals with it; the weight blend
                    # then mixes rotation and identity (exact to first order)
                    Rloc = Rinv @ p['Rrot'] @ R
                    ww = np.clip(w, 0.0, 1.0)[:, None]
                    nn = (1.0 - ww) * nrm_local + ww * (nrm_local @ Rloc.T)
                else:
                    # derivative of the displacement along t
                    dt = 0.0005
                    ex1 = np.interp(tl + dt / L, p['ts'], p['cxs'])
                    ey1 = np.interp(tl + dt / L, p['ts'], p['cys'])
                    al1 = fade_alpha(tl + dt / L, args.t0, args.t1, args.fade)
                    d2b = -(args.strength * al1 * limbw)[:, None] * np.stack([ex1, ey1], axis=1)
                    d3b = d2b[:, 0:1] * N_ + d2b[:, 1:2] * V_
                    uvec = (d3b - d3_unit) / dt
                    u_loc = uvec @ Rinv
                    a_loc = axis @ Rinv
                    dot = (nrm_local * u_loc).sum(axis=1)
                    nn = nrm_local - a_loc[None, :] * dot[:, None]
                ln = np.linalg.norm(nn, axis=1, keepdims=True)
                nn = nn / np.maximum(ln, 1e-30)
                sel = w > 0.0
                new_nrm[sel] = nn[sel]

        log()
        log('unit 0x%016x: %d vertices moved, max |d| = %.1f mm'
            % (e['file_id'], int(moved.sum()), dmax * 1000))
        if args.report_edges and moved.any():
            p0 = plans[list(plans)[0]]
            rel = inf['pos'] - p0['E']
            tt = rel @ p0['axis']
            edge_stretch_report(inf['pos_local'], new_pos,
                                lod0_edges(uu, inf['mi']), tt, tt / p0['L'], moved)

        # ---- pack back into the GPU blob
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
                                    note='unit:%s forearm align to bone axis' % args.name))
        report.append(dict(unit='%016x' % e['file_id'], moved=int(moved.sum()),
                           maxMoveMm=dmax * 1000))

    res = PW.write_patch(out_entries, args.src, args.out, PATCH_NAME)
    src_gpu = os.path.getsize(args.src + '.gpu_resources')
    log('wrote %s (%d B) + gpu (%d B; source %d)'
        % (res['toc'], res['toc_size'], res['gpu_size'], src_gpu))
    assert res['gpu_size'] == src_gpu, 'gpu blob size changed'

    meta = dict(schema='hd2mod.rig-lab-forearm-align', sourcePatch=args.src,
                units=['%016x' % x for x in units], sides=list(plans),
                params=dict(mode=args.mode, strength=args.strength, t0=args.t0,
                            t1=args.t1, fade=args.fade, fit_slab=args.fit_slab,
                            fit_step=args.fit_step, fit_radius_m=args.fit_radius,
                            gate_radius_m=args.gate_radius,
                            max_move_m=args.max_move,
                            pivot_lo=args.pivot_lo, pivot_hi=args.pivot_hi,
                            rot_deg={s: plans[s]['rot_deg'] for s in plans},
                            normals=not args.no_normals),
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
