# -*- coding: utf-8 -*-
"""Round 70: route the arm's distal follow through l_hand_twist, hand included.

Why
---
Rounds 66/67/68/69 all bound the arm's distal follow to l_hand only, because we
had no evidence that the game drives l_hand_twist.  We now do (see
_probe_vanilla_hand.py / _probe_vanilla_wrist.py): the vanilla skin part
g_torso_arm_l_male puts real weight on l_hand_twist near the wrist, and the
vanilla arm armour unit_0x4a213e0ae8acf74b.toc carries 74.5 there.  A twist bone
does not bend, so routing the forearm's roll through it decouples roll from bend.

What changed vs round 69
------------------------
Round 69:  ROI = forearm only (0 < t < L).  Distal share -> l_hand, twist_frac 0.
Round 70:  ROI = forearm *and* hand *and* fingers (0 < t < hand_limit*L), so the
           ramp can finish inside the hand and there is no artificial boundary at
           the wrist.  Distal share is split by --twist-frac.

Finger articulation is preserved on purpose: only the weight that sits on
{l_elbow, l_hand, l_hand_twist} is redistributed.  Weight on the finger bones
(children of l_hand) is left byte-for-byte alone, so bending a finger still
moves its vertices 100%.  This mirrors vanilla, whose finger tips are ~100% on
the finger bones while the palm blends l_hand / l_hand_twist.

Per ROI vertex:
    other   = weight NOT on {elbow, hand_twist, hand subtree}   (kept)
    fingers = weight on the hand subtree MINUS l_hand itself      (kept)
    armish  = 1 - other - fingers
    s       = smoothstep(--a, --b, t / L)
    elbow   = armish * (1 - s)
    hand    = armish * s * (1 - twist_frac)
    twist   = armish * s * twist_frac

  twist_frac = 0 -> identical in spirit to round 69 (pure l_hand)
  twist_frac = 1 -> the whole distal follow rides the twist bone

Usage:
    python reweight_arm_twist.py --src <patch_0> --out <dir> --name <name>
        [--a 0.22] [--b 1.00] [--twist-frac 0.5] [--radius 0.10]
        [--hand-limit 1.60] [--no-hand] [--dry-run]
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
    ap.add_argument('--name', default='lilium-arm-twist')
    ap.add_argument('--unit', action='append', default=[],
                    help='carried unit id (repeatable); default both RE-2310 torsos')
    ap.add_argument('--a', type=float, default=0.22,
                    help='distal ramp start as a fraction of the forearm length')
    ap.add_argument('--b', type=float, default=1.00,
                    help='distal ramp end (s reaches 1 here)')
    ap.add_argument('--twist-frac', type=float, default=0.5,
                    help='share of the distal follow routed to l_hand_twist')
    ap.add_argument('--radius', type=float, default=0.10)
    ap.add_argument('--hand-limit', type=float, default=1.60,
                    help='ROI reaches this far along elbow->hand (1.0 = wrist)')
    ap.add_argument('--no-hand', action='store_true',
                    help='restrict the ROI to the forearm (round-69 behaviour)')
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
        hand_sub = set(subtree_slots(ti, bi, slot_of[hd]))
        finger_slots = hand_sub - {slot_of[hd]}
        arm_names = set(names[s] for s in hand_sub) | {el, tw}
        finger_names = set(names[s] for s in finger_slots)
        E, W = heads[slot_of[el]], heads[slot_of[hd]]
        axis = W - E
        L = float(np.linalg.norm(axis))
        axis /= L
        rel = pos - E
        t = rel @ axis
        rad = np.linalg.norm(rel - np.outer(t, axis), axis=1)
        t_hi = L if args.no_hand else args.hand_limit * L
        roi = (rad < args.radius) & (t > 0.0) & (t < t_hi)
        a, b = args.a * L, args.b * L
        s = smoothstep(a, b, t)

        n = 0
        for i in np.nonzero(roi)[0]:
            keep, other, fing = [], 0.0, 0.0
            for nm, w in infl[i]:
                if nm in arm_names:
                    if nm in finger_names:
                        fing += w
                        keep.append((nm, w))       # fingers: untouched
                    continue
                keep.append((nm, w))
                other += w
            arm_total = 1.0 - other - fing
            if arm_total <= 0.0:
                continue
            si_ = float(s[i])
            e_w = arm_total * (1.0 - si_)
            t_w = arm_total * si_ * args.twist_frac
            h_w = arm_total * si_ * (1.0 - args.twist_frac)
            if e_w > 0.0:
                keep.append((el, e_w))
            if t_w > 0.0:
                keep.append((tw, t_w))
            if h_w > 0.0:
                keep.append((hd, h_w))
            if keep != infl[i]:
                infl[i] = keep
                changed[i] = True
                n += 1
        stats.append(dict(side=side, length_m=L, roi=int(roi.sum()), changed=int(n),
                          a_mm=a * 1000, b_mm=b * 1000, t_hi_mm=t_hi * 1000))
        log('  %-5s L=%.1f mm  roi=%d  changed=%d  (ramp %.0f..%.0f mm, roi to %.0f mm, '
            'twist_frac %.2f, fingers %d)'
            % (side, L * 1000, int(roi.sum()), n, a * 1000, b * 1000, t_hi * 1000,
               args.twist_frac, len(finger_slots)))

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
                                        note='unit:%s arm twist split' % args.name))
        else:
            out_entries.append(PW.Entry(e['type_id'], e['file_id'], pl['toc'],
                                        pl['stream'], pl['gpu'],
                                        e['unknown1'], e['unknown2'], note='verbatim'))

    res = PW.write_patch(out_entries, args.src, args.out, PATCH_NAME)
    src_gpu = os.path.getsize(args.src + '.gpu_resources')
    log('  wrote %s (%d B) + gpu (%d B; source %d)'
        % (res['toc'], res['toc_size'], res['gpu_size'], src_gpu))
    assert res['gpu_size'] == src_gpu, 'gpu blob size changed'

    meta = dict(schema='hd2mod.rig-lab-arm-twist', sourcePatch=args.src,
                units=['%016x' % x for x in units], streams=streams,
                params=dict(a=args.a, b=args.b, twist_frac=args.twist_frac,
                            radius_m=args.radius, hand_limit=args.hand_limit,
                            include_hand=not args.no_hand),
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
