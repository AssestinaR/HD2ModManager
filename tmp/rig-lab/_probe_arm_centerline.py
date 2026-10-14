# -*- coding: utf-8 -*-
"""READ-ONLY: the arm CENTRELINE of one package, station by station.

Why (round 75b)
---------------
Round 75 pulled the forearm mesh onto the elbow->wrist bone axis with a
smoothly ramped displacement (alpha = 0 at t/L 0.34, 1 at 0.62).  The user
looked at the render and reported the arm became a "W": the mesh was pushed
outward starting from the MIDDLE of the forearm, which put a second bend in the
arm.  The upper arm + forearm should stay a V (about 120 deg interior) with the
elbow just shifting a little.

This probe measures what the eye sees: the medial line of the arm mesh.

For each station along shoulder->elbow and along elbow->wrist it takes the slab
perpendicular to the BONE segment, keeps the limb-weighted vertices inside a
radius gate, least-squares fits a circle to the section and records the fitted
centre.  The polyline of those centres IS the mesh centreline.

It then fits a straight 3D line to each segment's centres (SVD/PCA) and prints

  * tilt   angle between the mesh segment and its bone segment
  * off    perpendicular distance from the joint head to that line
  * rms    residual of the straight fit  (is the mesh segment actually straight?)
  * the interior angle of the mesh V vs the bone V

Nothing is written except the optional --json dump.

usage:
    _probe_arm_centerline.py <patch_0> [--unit 0x...] [--side left]
    _probe_arm_centerline.py --local <base_without_ext> [...]
"""
import argparse
import json
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_hash as H
import hd2_patch as P
import hd2_unit as U

SDK = 'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
BONES = H.load_bonehash(os.path.join(SDK, 'hashlists', 'bonehash.txt'))
SLIM = 0xcae35ee15168145f


def log(m=''):
    print(m, flush=True)


def circle_fit_2d(x, y):
    """Kasa algebraic fit -> (cx, cy, r, rms)."""
    A = np.column_stack([2.0 * x, 2.0 * y, np.ones_like(x)])
    b = x * x + y * y
    sol, *_ = np.linalg.lstsq(A, b, rcond=None)
    cx, cy = float(sol[0]), float(sol[1])
    r = float(np.sqrt(max(sol[2] + cx * cx + cy * cy, 0.0)))
    d = np.hypot(x - cx, y - cy)
    return cx, cy, r, float(np.sqrt(((d - d.mean()) ** 2).mean()))


def live_lod0(u):
    """-> pos (unit space), weights, decoded slots, material per vertex, mesh info."""
    ti, bi = u.transform_info, u.bone_infos[0]
    mi = max((m for m in u.mesh_infos if m.lod_index == 0),
             key=lambda m: m.num_sections)
    verts = u.decode_vertices(mi.stream_index)
    n = len(verts)
    pos = np.asarray([v['position'] for v in verts], np.float64)
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
    pos = pos @ M[:3, :3] + M[3, :3]          # row-major, translation last row
    live = mat_of >= 0
    return dict(pos=pos[live], wgt=wgt[live], dec=decoded[live], mat=mat_of[live],
                mi=mi, n=int(live.sum()))


def bone_table(u):
    ti, bi = u.transform_info, u.bone_infos[0]
    names = {s: BONES.get(ti.hashes[i], '0x%08x' % ti.hashes[i])
             for s, i in enumerate(bi.real_indices)}
    heads = {s: np.array(ti.matrices[i][12:15], np.float64)
             for s, i in enumerate(bi.real_indices)}
    slot_of = {v: k for k, v in names.items() if not v.startswith('0x')}
    return names, heads, slot_of


def limb_mask(dec, wgt, names, pre):
    """True for vertices whose dominant weight sits on this limb's bones."""
    m = np.zeros(len(dec), bool)
    for j in range(4):
        for s in np.unique(dec[:, j]):
            nm = names.get(int(s), '')
            if nm.startswith(pre + '_'):
                m |= (dec[:, j] == s) & (wgt[:, j] > 0.5)
    return m


def frame(A, B):
    ax = B - A
    L = float(np.linalg.norm(ax))
    ax = ax / L
    tmp = np.array([0.0, 0.0, 1.0])
    if abs(float(ax @ tmp)) > 0.9:
        tmp = np.array([0.0, 1.0, 0.0])
    N_ = np.cross(ax, tmp); N_ /= np.linalg.norm(N_)
    V_ = np.cross(N_, ax); V_ /= np.linalg.norm(V_)
    return ax, N_, V_, L


def stations(pos, mask, A, B, step, radius, min_n, mat=None, split_mat=False):
    """Circle-fit the section at every station of A->B -> the mesh centreline."""
    ax, N_, V_, L = frame(A, B)
    rel = pos - A
    t = rel @ ax
    perp = rel - np.outer(t, ax)
    rad = np.linalg.norm(perp, axis=1)
    rows = []
    for k in range(int(round(1.0 / step))):
        u = (k + 0.5) * step
        c = u * L
        m = mask & (np.abs(t - c) < step * L * 0.5) & (rad < radius)
        if int(m.sum()) < min_n:
            continue
        x, y = perp[m] @ N_, perp[m] @ V_
        cx, cy, r, rms = circle_fit_2d(x, y)
        row = dict(u=u, t=c, n=int(m.sum()), cx=cx, cy=cy,
                   off=float(np.hypot(cx, cy)),
                   ang=float(np.degrees(np.arctan2(cy, cx))),
                   r=r, rms=rms,
                   C=(A + c * ax + cx * N_ + cy * V_).tolist())
        if split_mat and mat is not None:
            per = {}
            for mi_ in np.unique(mat[m]):
                mm = m & (mat == mi_)
                if int(mm.sum()) < 6:
                    continue
                xx, yy = perp[mm] @ N_, perp[mm] @ V_
                c2x, c2y, r2, rms2 = circle_fit_2d(xx, yy)
                per[int(mi_)] = dict(n=int(mm.sum()), cx=c2x, cy=c2y,
                                     off=float(np.hypot(c2x, c2y)),
                                     ang=float(np.degrees(np.arctan2(c2y, c2x))),
                                     r=r2, rms=rms2)
            row['per_mat'] = per
        rows.append(row)
    return dict(A=A.tolist(), B=B.tolist(), ax=ax.tolist(), N_=N_.tolist(),
                V_=V_.tolist(), L=L, rows=rows)


def straight_fit(seg, lo, hi):
    """PCA straight line through the centres of a segment window."""
    sel = [r for r in seg['rows'] if lo <= r['u'] <= hi]
    if len(sel) < 3:
        return None
    C = np.stack([np.array(r['C']) for r in sel])
    mean = C.mean(axis=0)
    X = C - mean
    _, _s, Vt = np.linalg.svd(X, full_matrices=False)
    d = Vt[0]
    ax = np.array(seg['ax'])
    if float(d @ ax) < 0:
        d = -d
    perp = X - np.outer(X @ d, d)
    rms = float(np.sqrt((perp ** 2).sum(axis=1).mean()))
    A = np.array(seg['A'])
    w = A - mean
    off_vec = w - float(w @ d) * d
    return dict(d=d.tolist(), mean=mean.tolist(), rms=rms,
                off=float(np.linalg.norm(off_vec)), off_vec=off_vec.tolist(),
                tilt=float(np.degrees(np.arccos(np.clip(float(d @ ax), -1.0, 1.0)))),
                n=len(sel), lo=lo, hi=hi)


def measure(pos, mask, names, heads, slot_of, side, step, radius, min_n,
            fit_lo, fit_hi, mat=None, split_mat=False):
    pre = 'l' if side == 'left' else 'r'
    S = heads[slot_of[pre + '_shoulder']]
    E = heads[slot_of[pre + '_elbow']]
    W = heads[slot_of[pre + '_hand']]
    up = stations(pos, mask, S, E, step, radius, min_n, mat, split_mat)
    fa = stations(pos, mask, E, W, step, radius, min_n, mat, split_mat)
    f_up = straight_fit(up, fit_lo, fit_hi)
    f_fa = straight_fit(fa, fit_lo, fit_hi)

    def interior(f1, f2):
        if f1 is None or f2 is None:
            return None
        d1, d2 = np.array(f1['d']), np.array(f2['d'])
        # both point away from the elbow -> interior angle of the V
        return float(180.0 - np.degrees(np.arccos(np.clip(float(d1 @ d2), -1, 1))))

    d1 = E - S; d1 /= np.linalg.norm(d1)
    d2 = W - E; d2 /= np.linalg.norm(d2)
    bone_v = float(180.0 - np.degrees(np.arccos(np.clip(float(d1 @ d2), -1, 1))))
    return dict(side=side, S=S, E=E, W=W, upper=up, forearm=fa,
                fit_upper=f_up, fit_forearm=f_fa,
                mesh_v=interior(f_up, f_fa), bone_v=bone_v)


def dump_segment(title, seg, fit, split_mat=False):
    log()
    log('-' * 100)
    log('%s   bone length %.1f mm' % (title, seg['L'] * 1000))
    log('-' * 100)
    log('  %6s %7s %5s %9s %9s %8s %7s %7s'
        % ('t/L', 't(mm)', 'n', 'off(mm)', 'dir(deg)', 'r(mm)', 'rms(mm)', 'c-off'))
    for r in seg['rows']:
        log('  %6.2f %7.0f %5d %9.1f %9.1f %8.1f %7.2f %7.1f'
            % (r['u'], r['t'] * 1000, r['n'], r['off'] * 1000, r['ang'],
               r['r'] * 1000, r['rms'] * 1000, r['off'] * 1000))
        if split_mat:
            for mi_, p in sorted(r.get('per_mat', {}).items()):
                log('         mat %-2d n %5d  off %7.1f  dir %8.1f  r %6.1f  rms %5.2f'
                    % (mi_, p['n'], p['off'] * 1000, p['ang'], p['r'] * 1000,
                       p['rms'] * 1000))
    if fit:
        log('  straight fit over t/L %.2f..%.2f (%d stations):'
            % (fit['lo'], fit['hi'], fit['n']))
        log('    tilt vs bone axis  %6.2f deg' % fit['tilt'])
        log('    offset at joint    %6.1f mm' % (fit['off'] * 1000))
        log('    straightness rms   %6.2f mm  (small = the mesh segment is straight)'
            % (fit['rms'] * 1000))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('patch', nargs='?')
    ap.add_argument('--local', default=None)
    ap.add_argument('--unit', default='0x%016x' % SLIM)
    ap.add_argument('--side', default='left', choices=('left', 'right'))
    ap.add_argument('--step', type=float, default=0.04)
    ap.add_argument('--radius', type=float, default=0.13)
    ap.add_argument('--min-n', type=int, default=8)
    ap.add_argument('--fit-lo', type=float, default=0.20)
    ap.add_argument('--fit-hi', type=float, default=0.96)
    ap.add_argument('--label', default=None)
    ap.add_argument('--split-mat', action='store_true',
                    help='also fit each material shell separately (the wrist ring is '
                         'its own rigid shell and biases the combined fit)')
    ap.add_argument('--json', default=None, help='dump the centreline to this file')
    args = ap.parse_args()

    if args.local:
        toc = open(args.local + '.toc', 'rb').read()
        gpu = open(args.local + '.gpu_resources', 'rb').read()
        u = U.Unit.read(toc, gpu)
        label = args.label or os.path.basename(args.local)
    else:
        entries, _ = P.scan_entries(args.patch)
        e = next(x for x in entries if x['type_id'] == U.UNIT_ID
                 and x['file_id'] == int(args.unit, 16))
        pl = P.entry_payload(e, args.patch)
        u = U.Unit.read(pl['toc'], pl['gpu'])
        label = args.label or os.path.basename(args.patch)

    d = live_lod0(u)
    names, heads, slot_of = bone_table(u)
    mask = limb_mask(d['dec'], d['wgt'], names,
                     'l' if args.side == 'left' else 'r')

    log('=' * 100)
    log('%s   [%s]   %d LOD0-live vertices, %d on this limb'
        % (label, args.side, d['n'], int(mask.sum())))
    log('=' * 100)

    m = measure(d['pos'], mask, names, heads, slot_of, args.side,
                args.step, args.radius, args.min_n, args.fit_lo, args.fit_hi,
                mat=d['mat'], split_mat=args.split_mat)
    dump_segment('UPPER ARM   shoulder -> elbow', m['upper'], m['fit_upper'],
                 args.split_mat)
    dump_segment('FOREARM     elbow -> hand', m['forearm'], m['fit_forearm'],
                 args.split_mat)

    log()
    log('=' * 100)
    log('  bone V interior angle   %6.2f deg' % m['bone_v'])
    if m['mesh_v'] is not None:
        log('  mesh V interior angle   %6.2f deg   (delta %+6.2f deg)'
            % (m['mesh_v'], m['mesh_v'] - m['bone_v']))
    if m['fit_forearm']:
        log('  forearm mesh vs bone:  tilt %+.2f deg, offset at elbow %.1f mm'
            % (m['fit_forearm']['tilt'], m['fit_forearm']['off'] * 1000))
    if m['fit_upper']:
        log('  upper   mesh vs bone:  tilt %+.2f deg, offset at shoulder %.1f mm'
            % (m['fit_upper']['tilt'], m['fit_upper']['off'] * 1000))
    log('=' * 100)

    if args.json:
        payload = dict(label=label, side=args.side,
                       step=args.step, radius=args.radius,
                       S=m['S'].tolist(), E=m['E'].tolist(), W=m['W'].tolist(),
                       upper=m['upper'], forearm=m['forearm'],
                       fit_upper=m['fit_upper'], fit_forearm=m['fit_forearm'],
                       mesh_v=m['mesh_v'], bone_v=m['bone_v'])
        with open(args.json, 'w', encoding='utf-8') as f:
            json.dump(payload, f, indent=1)
        log('json -> %s' % args.json)
    return 0


if __name__ == '__main__':
    sys.exit(main())
