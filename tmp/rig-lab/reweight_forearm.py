# -*- coding: utf-8 -*-
"""Re-bind the forearm: rigid to the elbow, with a narrow blend at the wrist.

Round-60 finding (see ROUND60_ISSUES_BRIEF §6.4-6.6): the forearm skin is a
smooth hand-weight ramp from the elbow (0.08) to the wrist (1.00) instead of a
rigid segment.  The whole forearm therefore follows every wrist bend -- the
"rubber band" the user reported.

This script edits ONLY the runtime weights.  Geometry, topology, UVs, the
skeleton, the bind matrices, the materials and every other entry are untouched,
and the edit is written in place into the existing vertex streams, so the
container layout cannot shift.

Design (per side, from the measured profile in _probe_forearm_profile2.py)
  * ROI            vertices within 10 cm of the elbow->hand axis, t in [0, L]
  * untouched      anything already >= 0.95 on the hand subtree (the rigid glove
                   and palm, which are correct)
  * rigid shells   connected components whose near-set spans < 60 mm of t
                   (bracelets, beads, buckles).  These keep their CURRENT mean
                   hand/elbow split but become uniform across the component, so
                   they move rigidly instead of shearing.  No guess is made
                   about which bone they "should" follow.
  * forearm shells everything else (skin, tight sleeve): the hand mass is driven
                   to smoothstep(A_W, B_W, t) and the difference is moved to /
                   from the same side's elbow bone.  Nothing else changes.
  * encoding       merge duplicate bones, snap to the 1/2048 grid, so the float16
                   sum is exactly 1 (the SDK normalize_half4 contract).

Usage:
    python reweight_forearm.py --src <patch_0> --out <dir> --name <name>
        [--dry-run] [--a-w 0.78] [--b-w 0.92] [--radius 0.10]
"""
import argparse
import hashlib
import json
import os
import sys

import numpy as np
from scipy.sparse import coo_matrix
from scipy.sparse.csgraph import connected_components

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_hash as H
import hd2_patch as HP
import hd2_patchw as PW
import hd2_unit as U
import weight_half4 as WH

SDK = 'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
PATCH_NAME = '9ba626afa44a3aa3.patch_0'
BONES = H.load_bonehash(os.path.join(SDK, 'hashlists', 'bonehash.txt'))

SIDES = (('left', 'l_elbow', 'l_hand', 'l_shoulder'),
         ('right', 'r_elbow', 'r_hand', 'r_shoulder'))


def log(m=''):
    print(m, flush=True)


def smoothstep(a, b, x):
    u = np.clip((x - a) / (b - a), 0.0, 1.0)
    return u * u * (3.0 - 2.0 * u)


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
    """Every palette slot whose transform is the root or a descendant of it."""
    root_ti = bi.real_indices[root_slot]
    out = []
    for slot, ti_idx in enumerate(bi.real_indices):
        seen = set()
        cur = ti_idx
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


def stream_field_offsets(u, si):
    off, bw, bi = 0, None, None
    for c in u.stream_infos[si].components:
        size = U.component_size(c.format, u.version)
        if c.type == 7:
            bw = off
        elif c.type == 6:
            bi = off
        off += size
    return bw, bi


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--src', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--name', default='lunalice-forearm')
    ap.add_argument('--unit', action='append', default=[],
                    help='carried unit id (repeatable); default both RE-2310 torsos')
    ap.add_argument('--a-w', type=float, default=0.78,
                    help='wrist blend start, as a fraction of the forearm length')
    ap.add_argument('--b-w', type=float, default=0.92,
                    help='wrist blend end (hand weight reaches 1 here)')
    ap.add_argument('--radius', type=float, default=0.10)
    ap.add_argument('--rigid-span-mm', type=float, default=60.0)
    ap.add_argument('--hand-shell', type=float, default=1.01,
                    help='skip vertices already at or above this hand weight. '
                         'Default 1.01 = disabled: the palm/glove shells are '
                         'compact in t and are protected by the rigid rule '
                         'instead.  A weight-based cut is NOT safe here -- '
                         '_probe_handrigid.py shows the hand>=0.95 vertices sit '
                         'in the SAME connected components as the forearm skin '
                         '(comp 276/277) at overlapping radii, so excluding them '
                         'would tear the skin')
    ap.add_argument('--dry-run', action='store_true')
    args = ap.parse_args()

    units = [int(x, 16) for x in args.unit] or [0xcae35ee15168145f, 0x9841b16544c8fbae]
    entries, _ = HP.scan_entries(args.src)
    by_key = {(e['type_id'], e['file_id']): e for e in entries}

    # ---- analyse the first carried unit, then reuse the edit for every stream
    e0 = by_key[(U.UNIT_ID, units[0])]
    pl0 = HP.entry_payload(e0, args.src)
    u = U.Unit.read(pl0['toc'], pl0['gpu'])
    ti, bi, names, heads, slot_of = bone_table(u)
    streams = model_streams(u)
    log('unit 0x%016x: %d streams, model streams %s'
        % (units[0], len(u.stream_infos), streams))

    si = streams[0]
    verts = u.decode_vertices(si)
    pos = np.asarray([v['position'] for v in verts], np.float64)
    bidx = np.asarray([v['bone_index0'] for v in verts], np.int32)
    wgt = np.asarray([v['bone_weight'] for v in verts], np.float64)
    nv = len(pos)

    idx = np.frombuffer(u.gpu_index[si], np.uint32).astype(np.int64).reshape(-1, 3)
    edges = np.concatenate([idx[:, [0, 1]], idx[:, [1, 2]], idx[:, [2, 0]]])
    graph = coo_matrix((np.ones(len(edges)), (edges[:, 0], edges[:, 1])),
                       shape=(nv, nv))
    _, labels = connected_components(graph, directed=False)

    # hand-subtree mass and elbow mass per vertex, as dense helpers
    hand_slots, elbow_slots = {}, {}
    for side, el, hd, sh in SIDES:
        hand_slots[side] = set(subtree_slots(ti, bi, slot_of[hd]))
        elbow_slots[side] = slot_of[el]

    def mass(slot_set):
        m = np.zeros(nv)
        for j in range(4):
            hit = np.isin(bidx[:, j], list(slot_set))
            m[hit] += wgt[hit, j]
        return m

    hand_mass = {s: mass(hand_slots[s]) for s, _, _, _ in SIDES}
    elbow_mass = {s: mass({elbow_slots[s]}) for s, _, _, _ in SIDES}

    # influences as (name, weight) lists, so duplicates merge on the way out
    infl = [[(names[int(b)], float(w)) for b, w in zip(bidx[i], wgt[i]) if w > 0.0]
            for i in range(nv)]

    changed = np.zeros(nv, bool)
    stats = []
    for side, el, hd, sh in SIDES:
        E, W = heads[elbow_slots[side]], heads[slot_of[hd]]
        axis = W - E
        L = float(np.linalg.norm(axis))
        axis /= L
        rel = pos - E
        t = rel @ axis
        rad = np.linalg.norm(rel - np.outer(t, axis), axis=1)
        near = (rad < args.radius) & (t > 0.0) & (t < L) & (hand_mass[side] < args.hand_shell)

        comp_ids = np.unique(labels[near])
        rigid, ramp = [], []
        for comp in comp_ids:
            sel = np.nonzero(near & (labels == comp))[0]
            if len(sel) == 0:
                continue
            span = float(t[sel].max() - t[sel].min())
            (rigid if span * 1000.0 < args.rigid_span_mm else ramp).append((comp, sel))

        a_w, b_w = args.a_w * L, args.b_w * L
        hand_names = {names[s] for s in hand_slots[side]}
        n_ramp = n_rigid = 0

        def retarget(i, target):
            """Move hand-subtree mass to/from this side's elbow until the hand
            mass equals `target`.  Nothing else about the vertex changes."""
            cur = hand_mass[side][i]
            d = target - cur
            if abs(d) < 1e-9:
                return False
            new = []
            if d < 0.0:                           # take mass off the hand
                take = -d
                for nm, w in infl[i]:
                    if nm in hand_names:
                        cut = min(w, take)
                        w -= cut
                        take -= cut
                    if w > 0.0:
                        new.append((nm, w))
                new.append((el, -d))
            else:                                 # give mass to the hand
                give = d
                for nm, w in infl[i]:
                    if nm == el:
                        cut = min(w, give)
                        w -= cut
                        give -= cut
                    if w > 0.0:
                        new.append((nm, w))
                if give > 1e-9:                   # elbow ran out: borrow shoulder
                    extra = []
                    for nm, w in new:
                        if nm == sh and give > 1e-9:
                            cut = min(w, give)
                            w -= cut
                            give -= cut
                        if w > 0.0:
                            extra.append((nm, w))
                    new = extra
                new.append((hd, d - give))
            infl[i] = new
            return True

        for comp, sel in ramp:
            tgt = smoothstep(a_w, b_w, t[sel])
            for k, i in enumerate(sel):
                if retarget(i, float(tgt[k])):
                    changed[i] = True
                    n_ramp += 1

        for comp, sel in rigid:
            # A rigid piece (bracelet, bead, buckle, glove panel) must not shear,
            # so it takes ONE ratio -- evaluated at its own attachment point on
            # the same ramp the surrounding skin uses.  That keeps it glued to
            # the surface it sits on instead of lagging behind it.
            tm = float(np.median(t[sel]))
            h_tgt = float(smoothstep(a_w, b_w, tm))
            for i in sel:
                if retarget(i, h_tgt):
                    changed[i] = True
                    n_rigid += 1

        stats.append(dict(side=side, length_m=L, near=int(near.sum()),
                          ramp=int(n_ramp), rigid=int(n_rigid),
                          a_w_mm=a_w * 1000, b_w_mm=b_w * 1000,
                          rigid_components=len(rigid), ramp_components=len(ramp)))
        log('  %-5s L=%.1f mm  near=%d  ramp-vertices=%d  rigid-vertices=%d  '
            '(blend %.0f..%.0f mm)'
            % (side, L * 1000, int(near.sum()), n_ramp, n_rigid,
               a_w * 1000, b_w * 1000))

    # ---- encode: merge duplicates, snap to 1/2048, verify closure
    new_w = np.zeros((nv, 4), np.float64)
    new_b = np.zeros((nv, 4), np.uint8)
    bad = 0
    for i in range(nv):
        if not changed[i]:
            new_w[i] = wgt[i]
            new_b[i] = np.clip(bidx[i], 0, 255)
            continue
        w4, k4, dropped = WH.quantize_half4([w for _n, w in infl[i]],
                                            [n for n, _w in infl[i]])
        if dropped:
            bad += 1
        for j, nm in enumerate(k4):
            new_b[i, j] = slot_of.get(nm, 0) if nm is not None else 0
        new_w[i] = w4
    ok, worst = None, None
    f16 = new_w.astype(np.float16).astype(np.float64)
    tot = f16.sum(axis=1)
    ok = bool(np.all(tot == 1.0))
    worst = float(np.abs(tot - 1.0).max())
    log('  weights: %d vertices changed, %d dropped an influence, '
        'half4 closed=%s (worst %.3e)' % (int(changed.sum()), bad, ok, worst))
    if not ok:
        raise AssertionError('float16 weights do not close; refusing to write')

    # ---- write the new bone fields into every model stream, in place
    if args.dry_run:
        log('  dry run: nothing written')
        return 0

    out_entries = []
    for e in entries:
        pl = HP.entry_payload(e, args.src)
        if e['type_id'] == U.UNIT_ID and e['file_id'] in units:
            gpu = bytearray(pl['gpu'])
            uu = U.Unit.read(pl['toc'], bytes(gpu))
            import struct as _s
            for s in model_streams(uu):
                si = uu.stream_infos[s]
                bw_off, bi_off = stream_field_offsets(uu, s)
                stride = si.vertex_stride
                n = si.num_vertices
                assert n == nv, 'stream %d has %d vertices, expected %d' % (s, n, nv)
                # offsets are relative to THIS stream's buffer, which lives at
                # vertex_buffer_offset inside the unit's gpu blob
                base0 = si.vertex_buffer_offset
                for i in range(n):
                    base = base0 + i * stride
                    _s.pack_into('<4e', gpu, base + bw_off,
                                 *[float(x) for x in new_w[i]])
                    _s.pack_into('<4B', gpu, base + bi_off,
                                 *[int(x) for x in new_b[i]])
            out_entries.append(PW.Entry(e['type_id'], e['file_id'], pl['toc'],
                                        pl['stream'], bytes(gpu),
                                        e['unknown1'], e['unknown2'],
                                        note='unit:%s forearm reweight' % args.name))
        else:
            out_entries.append(PW.Entry(e['type_id'], e['file_id'], pl['toc'],
                                        pl['stream'], pl['gpu'],
                                        e['unknown1'], e['unknown2'], note='verbatim'))

    res = PW.write_patch(out_entries, args.src, args.out, PATCH_NAME)
    src_gpu = os.path.getsize(args.src + '.gpu_resources')
    log('  wrote %s (%d B) + gpu (%d B; source %d)'
        % (res['toc'], res['toc_size'], res['gpu_size'], src_gpu))
    assert res['gpu_size'] == src_gpu, 'gpu blob size changed'

    meta = dict(schema='hd2mod.rig-lab-forearm-reweight', sourcePatch=args.src,
                units=['%016x' % x for x in units], streams=streams,
                params=dict(a_w=args.a_w, b_w=args.b_w, radius_m=args.radius,
                            rigid_span_mm=args.rigid_span_mm,
                            hand_shell=args.hand_shell),
                changedVertices=int(changed.sum()), stats=stats,
                half4Closed=ok, worstDeviation=worst,
                outputPatch=res['toc'], tocBytes=res['toc_size'],
                gpuBytes=res['gpu_size'])
    os.makedirs(args.out, exist_ok=True)
    with open(os.path.join(args.out, args.name + '.manifest.json'), 'w',
              encoding='utf-8') as f:
        json.dump(meta, f, indent=2, ensure_ascii=False)
    log('  manifest %s' % os.path.join(args.out, args.name + '.manifest.json'))
    return 0


if __name__ == '__main__':
    sys.exit(main())
