# -*- coding: utf-8 -*-
"""What actually reaches the .blend's vertex groups today?

The export chain is:  hd2_style.retarget -> dump_blender (model.npz)
                      -> blender_build (vertex groups) -> HD2SDK addon -> patch

Two things in that chain can break the SDK's half4 contract, and both are
downstream of the weights the pipeline computes:

  A. blender_build.py does `np.round(w, 3)` before writing vertex groups.
     Rounding each influence to 3 decimals does NOT preserve the sum, so the
     .blend groups do not add to 1 at all (0.333+0.333+0.333 = 0.999).
  B. The SDK addon's export path calls Blender's own
     `vertex_group_normalize_all(lock_active=False)` (float normalise) and then
     casts to vec4_half.  A float normalise followed by a half cast does not
     land on an exact decoded sum.

This probe measures A and B, and checks the two preconditions for the fix the
expert recommended (largest-remainder on the 1/2048 grid):

  1. no vertex lists the SAME HD2 bone twice (a duplicate would make the
     group-add a REPLACE and silently drop one influence),
  2. after quantising to k/2048, Blender's float normalise is a NO-OP and the
     half cast is exact, so the package comes out closed without touching the
     addon.

usage: _probe_exportchain.py
"""
import collections
import json
import math
import os
import struct
import sys

import numpy as np

d = np.load('out/blender/model.npz')
m = json.load(open('out/blender/meta.json', encoding='utf-8'))
names = m['bones']
wb = d['w_bone'].astype(np.int64)
wv = np.asarray(d['w_val'], np.float64)
NV = len(wb)
live = wb >= 0

print('== 0. the three influences a vertex carries ================')
nin = live.sum(1)
print('   influence count %s (mean %.3f)'
      % (np.bincount(nin, minlength=5)[:5], nin.mean()))

print('\n== 1. duplicate HD2 bone inside one vertex ================')
dup = 0
dup_only = 0
dup_mixed = 0
dup_ex = []
mixed_ex = []
for i in range(NV):
    b = [int(x) for x in wb[i][live[i]]]
    if len(b) != len(set(b)):
        dup += 1
        if len(set(b)) == 1:
            dup_only += 1
        else:
            dup_mixed += 1
            if len(mixed_ex) < 6:
                mixed_ex.append((i, b, [round(float(x), 6) for x in wv[i][live[i]]]))
        if len(dup_ex) < 3:
            dup_ex.append((i, b, [round(float(x), 6) for x in wv[i][live[i]]]))
print('   vertices with a repeated HD2 bone: %d (%.4f%%)'
      % (dup, 100.0 * dup / NV))
print('     ... and that is the ONLY bone          : %d  -> merge is exact' % dup_only)
print('     ... and another bone is also present   : %d  -> MERGE IS LOSSY TODAY'
      % dup_mixed)
for i, b, w in dup_ex:
    print('     dup-only  v%-7d bones %s weights %s' % (i, b, w))
for i, b, w in mixed_ex:
    print('     DUP+MIXED v%-7d bones %s weights %s' % (i, b, w))
print('   -> blender_build adds each slot with \'REPLACE\', so the later slot '
      'overwrites the earlier one.  When the two slots are the SAME bone and '
      'there is nothing else, the export-time normalise happens to repair it; '
      'when another bone is present the ratio is silently wrong.')

print('\n== 2. what np.round(w, 3) does to the sum ================')
r3 = np.round(wv, 3)
s3 = np.where(live, r3, 0.0).sum(1)
print('   sum after 3-decimal rounding: min %.6f  p50 %.6f  max %.6f'
      % (s3.min(), np.median(s3), s3.max()))
print('   vertices whose rounded sum != 1 exactly: %d (%.3f%%)'
      % (int((np.abs(s3 - 1) > 0).sum()), 100.0 * (np.abs(s3 - 1) > 0).mean()))
print('   worst |sum-1| after rounding: %.6f' % np.abs(s3 - 1).max())

print('\n== 3. Blender normalise + half cast, on TODAY\'s numbers =====')
# blender: vertex_group_normalize_all  ->  w / sum(w)   (float32 in Blender)
norm = np.zeros_like(wv)
tot = s3
ok = tot > 0
norm[ok] = r3[ok] / tot[ok, None]
# SDK writes vec4_half
h = np.zeros_like(norm)
for t in range(norm.shape[1]):
    sel = live[:, t]
    h[sel, t] = [struct.unpack('<e', struct.pack('<e', float(v)))[0]
                 for v in norm[sel, t]]
hsum = h.sum(1)
print('   decoded half sum: min %.17g  max %.17g' % (hsum.min(), hsum.max()))
bad = int((np.abs(hsum - 1.0) > 0).sum())
print('   vertices whose decoded half sum != 1: %d (%.3f%%)'
      % (bad, 100.0 * bad / NV))
print('   max |decoded sum - 1| = %.3e' % np.abs(hsum - 1.0).max())

print('\n== 4. the expert\'s fix: largest remainder on 1/2048 ======')


def merge_duplicates(weights, keys):
    """Sum the influences that share a bone, keep the stable-key order."""
    acc = {}
    order = []
    for w, k in zip(weights, keys):
        if w <= 0:
            continue
        if k not in acc:
            acc[k] = 0.0
            order.append(k)
        acc[k] += float(w)
    return [acc[k] for k in order], list(order)


def quantize_half4(weights, keys):
    """1/2048 grid, largest remainder, stable bone-key tie-break.

    Returns a list of four floats (k/2048) whose float32 values sum to exactly
    1.0 and whose float16 images also sum to exactly 1.0.
    """
    weights, keys = merge_duplicates(weights, keys)
    if len(weights) > 4:
        raise ValueError('more than four influences after the merge')
    weights = weights + [0.0] * (4 - len(weights))
    keys = keys + ['?'] * (4 - len(keys))
    total = math.fsum(weights)
    if total <= 0:
        return [0.0, 0.0, 0.0, 0.0]
    units, fracs, active = [], [], []
    for i in range(4):
        scaled = weights[i] / total * 2048.0
        u = int(math.floor(scaled))
        units.append(u)
        fracs.append(scaled - u)
        if weights[i] > 0:
            active.append(i)
    remaining = 2048 - sum(units)
    order = sorted(active, key=lambda i: (-fracs[i], str(keys[i])))
    if not 0 <= remaining <= len(order):
        raise ValueError('bad remainder %d' % remaining)
    for i in order[:remaining]:
        units[i] += 1
    return [u / 2048.0 for u in units]


q = np.zeros((NV, 4), np.float64)
qkey = [None] * NV
changed = 0
worst = 0.0
worst_i = -1
merged = 0
for i in range(NV):
    keys = [names[int(b)] if b >= 0 else '?' for b in wb[i]]
    w = [float(v) if live[i, t] else 0.0 for t, v in enumerate(wv[i])]
    if sum(w) <= 0:
        continue
    qq = quantize_half4(w, keys)
    q[i] = qq
    mw, mk = merge_duplicates(w, keys)
    qkey[i] = mk + ['?'] * (4 - len(mk))
    if len(mk) < sum(1 for v in w if v > 0):
        merged += 1
    # compare PER BONE, not positionally: the merge reorders the slots
    before = {}
    for ww, kk in zip(w, keys):
        if ww > 0:
            before[kk] = before.get(kk, 0.0) + ww
    delta = 0.0
    for kk in set(before) | set(k for k, v in zip(qkey[i], qq) if v > 0):
        delta = max(delta, abs(before.get(kk, 0.0)
                               - dict(zip(qkey[i], qq)).get(kk, 0.0)))
    if delta > 0:
        changed += 1
    if delta > worst:
        worst, worst_i = delta, i

print('   vertices where a duplicate bone was merged: %d (%.3f%%)'
      % (merged, 100.0 * merged / NV))

print('   vertices changed at all: %d (%.3f%%)'
      % (changed, 100.0 * changed / NV))
print('   worst single-weight move: %.10f (v%d)' % (worst, worst_i))
print('   is every k/2048 exactly half-representable?', end=' ')
allh = True
for i in range(0, NV, 97):
    for v in q[i]:
        if v > 0 and struct.unpack('<e', struct.pack('<e', v))[0] != v:
            allh = False
print(allh)

# float32 sum, then Blender's normalise, then half cast
q32 = q.astype(np.float32).astype(np.float64)
s32 = q32.sum(1)
print('   float32 sum of the quantised weights: min %.17g max %.17g'
      % (s32.min(), s32.max()))
print('   -> float32 sum == 1 exactly for all: %s' % bool((s32 == 1.0).all()))
norm2 = q32 / s32[:, None]          # Blender's normalise, a no-op here
h2 = np.zeros_like(norm2)
for t in range(norm2.shape[1]):
    h2[:, t] = [struct.unpack('<e', struct.pack('<e', float(v)))[0]
                for v in norm2[:, t]]
hs2 = h2.sum(1)
print('   decoded half sum after quantise+normalise+cast: min %.17g max %.17g'
      % (hs2.min(), hs2.max()))
print('   -> decoded sum == 1 exactly for all: %s' % bool((hs2 == 1.0).all()))
print('   -> max |decoded sum - 1| = %.3e' % np.abs(hs2 - 1.0).max())

print('\n== 5. does quantising add a bone influence? ================')
added = 0
removed = 0
for i in range(NV):
    before = set(k for k, v in zip([names[int(b)] if b >= 0 else '?' for b in wb[i]],
                                   wv[i]) if v > 0)
    after = set(k for k, v in zip(qkey[i], q[i]) if v > 0)
    added += len(after - before)
    removed += len(before - after)
print('   influences added:   %d' % added)
print('   influences zeroed:  %d' % removed)
print('   (the SDK contract forbids adding; zeroing a tiny weight is allowed '
      'but worth knowing)')
