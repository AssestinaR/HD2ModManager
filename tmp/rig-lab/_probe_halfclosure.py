# -*- coding: utf-8 -*-
"""Half4 closure -- done with the SDK's ACTUAL criterion, not a proxy.

The previous probe used `round(w * 2048)` as a stand-in for float16.  That is
the right *scale* (1/2048 = the spacing of halves in [0.5, 1]) but it is NOT
float16: halves in [0.25, 0.5) step by 2^-12, so `w*2048` is a half-integer
there and the "integer sum == 2048" test is simply too strict.  It reports a
failure that the SDK would not.

The SDK's real rule is `hd2_system_skin_weights.normalize_half4`:
  * normalise in float,
  * round each of the 4 to the nearest half,
  * require `math.fsum(quantized) == 1` EXACTLY,
  * if not: try moving ONE element to the exact residual, else fall back to a
    2048-integer split of the rounding error.
This script runs that rule verbatim (imported, not re-implemented) on the
weights the pipeline currently ships, and reports how many vertices need the
residual trick, how many need the integer fallback, and how far the shipped
half weights actually are from closing.

usage: _probe_halfclosure.py
"""
import collections
import importlib.util
import json
import math
import os
import struct
import sys

import numpy as np

KIT = os.path.abspath('../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools')
SDK = os.path.abspath('../pb-ref/sdk-raw')
sys.path.insert(0, KIT)
sys.path.insert(0, SDK)

spec = importlib.util.spec_from_file_location(
    'hsw', os.path.join(SDK, 'hd2_system_skin_weights.py'))
hsw = importlib.util.module_from_spec(spec)
spec.loader.exec_module(hsw)

d = np.load('out/blender/model.npz')
m = json.load(open('out/blender/meta.json', encoding='utf-8'))
names = m['bones']
wb = d['w_bone'].astype(np.int64)
wv = np.asarray(d['w_val'], np.float64)
NV = len(wb)
live = wb >= 0


def half(v):
    return struct.unpack('<e', struct.pack('<e', float(v)))[0]


def row4(i):
    """The SDK helper demands exactly four slots; we ship three.  Pad with 0."""
    keys = [names[int(b)] if b >= 0 else '?' for b in wb[i]]
    w = [float(v) if live[i, t] else 0.0 for t, v in enumerate(wv[i])]
    while len(keys) < 4:
        keys.append('?')
        w.append(0.0)
    return w, keys


# ---------------------------------------------------------------- as shipped
print('== A. what we ship now ====================================')
h = np.zeros_like(wv)
for t in range(wv.shape[1]):
    sel = live[:, t]
    h[sel, t] = [half(v) for v in wv[sel, t]]
hsum = h.sum(1)
print('   sum of the SHIPPED halves (float64 sum):')
print('     min %.17g  max %.17g' % (hsum.min(), hsum.max()))
off = np.abs(hsum - 1.0)
print('   vertices whose half sum != 1 exactly: %d (%.3f%%)'
      % (int((off > 0).sum()), 100.0 * (off > 0).mean()))
print('   max |half sum - 1| = %.3e   (SDK tolerance is 0: it must be exact)'
      % off.max())
print('   the old proxy (round(w*2048) sums to 2048) reported: %d (%.3f%%)'
      % (int((np.round(np.where(live, wv, 0.0) * 2048.0).sum(1) != 2048).sum()),
         100.0 * (np.round(np.where(live, wv, 0.0) * 2048.0).sum(1) != 2048).mean()))

# ------------------------------------------------------- the SDK's own fix
print('\n== B. run the SDK helper verbatim on every vertex =========')
paths = collections.Counter()
worst_move = 0.0
worst_move_v = -1
errors = []
for i in range(NV):
    w, keys = row4(i)
    if sum(w) <= 0:
        paths['all-zero (skipped)'] += 1
        continue
    try:
        q = hsw.normalize_half4(w, keys)
    except ValueError as e:
        paths['RAISED: %s' % e] += 1
        continue
    s = math.fsum(q)
    if s != 1.0:
        paths['SDK output does not sum to 1 (BUG)'] += 1
        continue
    # which branch did it take?
    naive = [half(v / math.fsum(w)) for v in w]
    if math.fsum(naive) == 1.0:
        paths['naive half rounding already closes'] += 1
    else:
        single = 0
        active = [j for j, v in enumerate(w) if v > 0]
        for j in active:
            res = 1 - math.fsum(naive[k] for k in range(4) if k != j)
            if 0 <= res <= 1 and half(res) == res:
                single += 1
        paths['needs the residual trick (%d candidate(s))' % single] += 1
    move = max(abs(a - b) for a, b in zip(q, [v / math.fsum(w) for v in w]))
    if move > worst_move:
        worst_move, worst_move_v = move, i

for k, v in paths.most_common():
    print('   %-46s %6d  (%.3f%%)' % (k, v, 100.0 * v / NV))
print('   worst single-weight move by the SDK helper: %.6g  (v%d)  '
      '-- SDK bound is 1/1024 = %.6g'
      % (worst_move, worst_move_v, 1.0 / 1024.0))

# ------------------------------------------------------------ what it costs
print('\n== C. does the fix change anything visible? ===============')
h2 = np.zeros_like(wv)
for i in range(NV):
    w, keys = row4(i)
    if sum(w) <= 0:
        h2[i] = 0.0
        continue
    h2[i] = hsw.normalize_half4(w, keys)[:wv.shape[1]]
delta = np.abs(h2 - wv).max(1)
print('   max |fixed - shipped| per vertex: p50 %.3e  p90 %.3e  p100 %.3e'
      % (np.percentile(delta, 50), np.percentile(delta, 90), delta.max()))
print('   vertices changed at all: %d (%.3f%%)'
      % (int((delta > 0).sum()), 100.0 * (delta > 0).mean()))
# how much would a mesh vertex move if its 1/2048 share of a 2 m limb flips?
print('   a 1/2048 weight step on a 0.5 m rigid part moves a vertex by %.4f mm'
      % (500.0 / 2048.0))
