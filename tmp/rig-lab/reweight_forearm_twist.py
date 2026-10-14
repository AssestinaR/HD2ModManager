# -*- coding: utf-8 -*-
"""Round 68: split the forearm's distal follow between l_hand_twist and l_hand.

Why
---
Round-67 bound the whole forearm rigidly to l_elbow with a narrow hand-weight
band (0.78L..0.92L).  Measured consequence (_probe_wrist_roll.py): a single
33 mm mesh edge spans that whole band, so one edge carries a 0.945 weight change
-> an 83 mm tear at a 180 deg wrist roll.  Round-66's wide band only produced a
41 mm jump and its two endpoints had *equal* weights (0.991 / 0.983) -- i.e. that
jump was purely geometric (radius 62 vs 84 mm), not a tear.  So round-66 got the
ROLL right and the BEND wrong; round-67 fixed the bend and broke the roll.

The rig's answer
----------------
HD2 ships l_hand_twist / r_hand_twist: leaf bones co-located with the hand,
children of the elbow, currently carrying ZERO weight.  A twist bone does not
bend, so putting the forearm's roll there decouples it from the wrist bend.

What this writes, per ROI vertex (radius < --radius, 0 < t < L along elbow->hand)
    o  = weight NOT on {hand subtree, elbow, twist}      (preserved untouched)
    s  = smoothstep(--a, --b, t / L)                     (distal factor)
    elbow = (1-o) * (1 - s)
    twist = (1-o) * s * twist_frac
    hand  = (1-o) * s * (1 - twist_frac)
  twist_frac = 1  -> all distal follow on the twist bone (bend stays rigid)
  twist_frac = 0  -> all distal follow on l_hand (round-66 style, couples bend)

Everything outside the ROI, and geometry / topology / UV / skeleton / materials,
is untouched.

Usage:
    python reweight_forearm_twist.py --src <patch_0> --out <dir> --name <name>
        [--a 0.50] [--b 1.00] [--twist-frac 1.0] [--radius 0.10] [--dry-run]
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
import weight_half4 as WH

SDK = 'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
PATCH_NAME = '9ba626afa44a3aa3.patch_0'
BONES = H.load_bonehash(os.path.join(SDK, 'hashlists', 'bonehash.txt'))

SIDES = (('left', 'l_elbow', 'l_hand', 'l_hand_twist'),
         ('right', 'r_elbow', 'r_hand', 'r_hand_twist'))


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
    ap.add_argument('--name', default='lilium-forearm-twist')
    ap.add_argument('--unit', action='append', default=[],
                    help='carried unit id (repeatable); default both RE-2310 torsos')
    ap.add_argument('--a', type=float, default=0.50,
                    help='distal ramp start as a fraction of the forearm length')
    ap.add_argument('--b', type=float, default=1.00,
                    help='distal ramp end (s reaches 1 here)')
    ap.add_argument('--twist-frac', type=float, default=1.0,
                    help='share of the distal follow routed to l_hand_twist')
    ap.add_argument('--hand-start', type=float, default=1.01,
                    help='t/L where the twist share starts handing over to l_hand '
                         '(default 1.01 = never inside the ROI, so no behaviour change)')
    ap.add_argument('--hand-end', type=float, default=1.21,
                    help='t/L where l_hand owns the whole distal share')
    ap.add_argument('--radius', type=float, default=0.10)
    ap.add_argument('--dry-run', action='store_true')
    args = ap.parse_args()

    units = [int(x, 16) for x in args.unit] or [0xcae35ee15168145f, 0x9841b16544c8fbae]
    entries, _ = HP.scan_entries(args.src)
    by_key = {(e['type_id'], e['file_id']): e for e in entries}

    e0 = by_key[(U.UNIT_ID, units[0])]
    pl0 = HP.entry_payload(e0, args.src)
    u = U.Unit.read(pl0['toc'], pl0['gpu'])
    ti, bi, names, heads, slot_of = bone_table(u)
    streams = model_streams(u)
    log('unit 0x%016x: %d streams, model streams %s'
        % (units[0], len(u.stream_infos), streams))

    for side, el, hd, tw in SIDES:
        for nm in (el, hd, tw):
            if nm not in slot_of:
                raise SystemExit('bone %s not in this skeleton' % nm)

    si = streams[0]
    verts = u.decode_vertices(si)
    pos = np.asarray([v['position'] for v in verts], np.float64)
    bidx = np.asarray([v['bone_index0'] for v in verts], np.int32)
    wgt = np.asarray([v['bone_weight'] for v in verts], np.float64)
    nv = len(pos)

    infl = [[(names[int(b)], float(w)) for b, w in zip(bidx[i], wgt[i]) if w > 0.0]
            for i in range(nv)]

    changed = np.zeros(nv, bool)
    stats = []
    for side, el, hd, tw in SIDES:
        hand_slots = set(subtree_slots(ti, bi, slot_of[hd]))
        # Only the three bones of the elbow -> twist -> hand chain take part in
        # the redistribution.  The rest of the hand subtree (the fingers) must
        # be left alone: the ROI now reaches past the wrist, and treating the
        # whole subtree as "ours" would wipe the finger weights there.
        arm_names = {el, hd, tw}
        E, W = heads[slot_of[el]], heads[slot_of[hd]]
        axis = W - E
        L = float(np.linalg.norm(axis))
        axis /= L
        rel = pos - E
        t = rel @ axis
        rad = np.linalg.norm(rel - np.outer(t, axis), axis=1)
        # The handover ramp (hand_start..hand_end) straddles the wrist at t/L=1,
        # so the ROI has to reach past it.  With the old `t < L` bound the ramp
        # was truncated mid-way and left a step at the wrist (round 74: the
        # "stem at 2 o'clock, tip at 3 o'clock" mismatch).
        t_end = max(L, args.hand_end * L)
        roi = (rad < args.radius) & (t > 0.0) & (t < t_end)
        a, b = args.a * L, args.b * L
        s = smoothstep(a, b, t)
        # Third stage: near the wrist the twist share hands over to l_hand.
        # Vanilla does exactly this (see _probe_vanilla_arm_profile.py): l_hand
        # stays ~0 for the whole forearm and only takes over past t/L ~0.9.
        h = smoothstep(args.hand_start * L, args.hand_end * L, t)

        n = 0
        for i in np.nonzero(roi)[0]:
            keep, other = [], 0.0
            for nm, w in infl[i]:
                if nm in arm_names:
                    continue
                keep.append((nm, w))
                other += w
            arm_total = 1.0 - other
            if arm_total <= 0.0:
                continue
            si_ = float(s[i])
            hi_ = float(h[i])
            e_w = arm_total * (1.0 - si_)
            t_w = arm_total * si_ * args.twist_frac * (1.0 - hi_)
            h_w = arm_total * si_ * ((1.0 - args.twist_frac)
                                     + args.twist_frac * hi_)
            if e_w > 0.0:
                keep.append((el, e_w))
            if t_w > 0.0:
                keep.append((tw, t_w))
            if h_w > 0.0:
                keep.append((hd, h_w))
            # compare order-insensitively: widening the ROI makes us revisit
            # vertices we now leave numerically identical, and a mere reorder
            # must not count as a change (it would break the no-op guarantee).
            if sorted(keep) != sorted(infl[i]):
                infl[i] = keep
                changed[i] = True
                n += 1
        stats.append(dict(side=side, length_m=L, roi=int(roi.sum()), changed=int(n),
                          a_mm=a * 1000, b_mm=b * 1000))
        log('  %-5s L=%.1f mm  roi=%d  changed=%d  (ramp %.0f..%.0f mm, twist_frac %.2f)'
            % (side, L * 1000, int(roi.sum()), n, a * 1000, b * 1000, args.twist_frac))

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
    f16 = new_w.astype(np.float16).astype(np.float64)
    tot = f16.sum(axis=1)
    ok = bool(np.all(tot == 1.0))
    worst = float(np.abs(tot - 1.0).max())
    log('  weights: %d vertices changed, %d dropped an influence, '
        'half4 closed=%s (worst %.3e)' % (int(changed.sum()), bad, ok, worst))
    if not ok:
        raise AssertionError('float16 weights do not close; refusing to write')

    if args.dry_run:
        log('  dry run: nothing written')
        return 0

    out_entries = []
    for e in entries:
        pl = HP.entry_payload(e, args.src)
        if e['type_id'] == U.UNIT_ID and e['file_id'] in units:
            gpu = bytearray(pl['gpu'])
            uu = U.Unit.read(pl['toc'], bytes(gpu))
            for s_i in model_streams(uu):
                si_ = uu.stream_infos[s_i]
                bw_off, bi_off = stream_field_offsets(uu, s_i)
                stride = si_.vertex_stride
                n = si_.num_vertices
                assert n == nv, 'stream %d has %d vertices, expected %d' % (s_i, n, nv)
                base0 = si_.vertex_buffer_offset
                for i in range(n):
                    base = base0 + i * stride
                    struct.pack_into('<4e', gpu, base + bw_off,
                                     *[float(x) for x in new_w[i]])
                    struct.pack_into('<4B', gpu, base + bi_off,
                                     *[int(x) for x in new_b[i]])
            out_entries.append(PW.Entry(e['type_id'], e['file_id'], pl['toc'],
                                        pl['stream'], bytes(gpu),
                                        e['unknown1'], e['unknown2'],
                                        note='unit:%s forearm twist split' % args.name))
        else:
            out_entries.append(PW.Entry(e['type_id'], e['file_id'], pl['toc'],
                                        pl['stream'], pl['gpu'],
                                        e['unknown1'], e['unknown2'], note='verbatim'))

    res = PW.write_patch(out_entries, args.src, args.out, PATCH_NAME)
    src_gpu = os.path.getsize(args.src + '.gpu_resources')
    log('  wrote %s (%d B) + gpu (%d B; source %d)'
        % (res['toc'], res['toc_size'], res['gpu_size'], src_gpu))
    assert res['gpu_size'] == src_gpu, 'gpu blob size changed'

    meta = dict(schema='hd2mod.rig-lab-forearm-twist', sourcePatch=args.src,
                units=['%016x' % x for x in units], streams=streams,
                params=dict(a=args.a, b=args.b, twist_frac=args.twist_frac,
                            hand_start=args.hand_start, hand_end=args.hand_end,
                            radius_m=args.radius),
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
