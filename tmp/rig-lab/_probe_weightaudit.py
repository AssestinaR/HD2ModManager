# -*- coding: utf-8 -*-
"""Audit the weights the pipeline currently ships.

The question is not "are they smooth" -- it is "will HD2 accept them and will
the armour move correctly".  So: influence count, closure (float AND the half4
the engine actually stores), how far each influence's bone is from the vertices
it claims to drive ("flung across the screen"), and where the dominant bone
jumps between neighbouring vertices (a hard seam tears under animation).

usage: _probe_weightaudit.py
"""
import json
import os
import sys

import numpy as np

d = np.load('out/blender/model.npz')
m = json.load(open('out/blender/meta.json', encoding='utf-8'))
names = m['bones']
P = np.asarray(d['ours_bind'], np.float64)
wb = d['w_bone'].astype(np.int64)
wv = np.asarray(d['w_val'], np.float64)
tris = np.asarray(d['tris'], np.int64)
head = np.asarray(d['b_head'], np.float64)
axis = np.asarray(d['b_axis'], np.float64)
blen = np.asarray(d['b_len'], np.float64)
parent = np.asarray(d['b_parent'], np.int64)
NV, NB = len(P), len(names)

live = wb >= 0
wsum = np.where(live, wv, 0.0).sum(1)
nin = live.sum(1)
print('== 1. influence count =====================================')
print('   per vertex: %s  (mean %.3f)' % (np.bincount(nin, minlength=5)[:5], nin.mean()))
print('   weight sum: min %.6f  p50 %.6f  max %.6f' %
      (wsum.min(), np.median(wsum), wsum.max()))
print('   sum != 1 by more than 1e-6: %d vertices' % int((np.abs(wsum - 1) > 1e-6).sum()))
print('   sum == 0 (unweighted): %d vertices' % int((wsum <= 0).sum()))

print('\n== 2. half4 closure (what the engine actually stores) =====')
q = np.round(np.where(live, wv, 0.0) * 2048.0)          # half-ish quantisation
qs = q.sum(1)
print('   quantised integer sums: %s' % np.bincount(qs.astype(int), minlength=3)[:5])
bad = int((qs != 2048).sum())
print('   vertices whose quantised weights do NOT close to 2048: %d (%.3f%%)'
      % (bad, 100.0 * bad / NV))
if bad:
    ex = np.nonzero(qs != 2048)[0][:8]
    for i in ex:
        print('     v%-7d qsum %6d  raw sum %.6f  n_inf %d' % (i, qs[i], wsum[i], nin[i]))
# the kit's fix: hand out the rounding error one unit at a time
rem = 2048 - qs
print('   after the 2048-integer top-up the kit uses: %d vertices still off'
      % int((np.abs(rem) > 2).sum()))

print('\n== 3. bone coverage ======================================')
dom = wb[np.arange(NV), np.argmax(np.where(live, wv, -1), axis=1)]
domname = np.array([names[b] if b >= 0 else '?' for b in dom])
cnt = {}
for i in range(NV):
    cnt[domname[i]] = cnt.get(domname[i], 0) + 1
tot = 0
for k, v in sorted(cnt.items(), key=lambda kv: -kv[1]):
    print('   %-24s %6d  (%.2f%%)' % (k, v, 100.0 * v / NV))
    tot += v
print('   distinct dominant bones: %d of %d in the rig' % (len(cnt), NB))
used = set(wb[live].tolist())
print('   distinct bones with ANY weight: %d' % len(used))
nomap = [names[b] for b in sorted(used) if not names[b]]
print('   weighted bones with no name: %d %s' % (len(nomap), nomap[:8]))

print('\n== 4. how far is each influence from the joint it drives =')
# Distance to the bone HEAD, not to the bone SEGMENT: many HD2 bones have their
# children laterally (dump_blender measures child-direction deviation from the
# bone +X at p95 91 deg), so head->head+axis*len is a bad proxy and reports
# 150 mm for bones that are actually right on top of their vertices.  The head
# is the LBS pivot, which is the thing that matters.
worst = np.zeros(NV)
for t in range(3):
    sel = np.nonzero(live[:, t])[0]
    if not len(sel):
        continue
    dd = np.linalg.norm(P[sel] - head[wb[sel, t]], axis=1)
    np.maximum.at(worst, sel, dd)
print('   vertex -> its farthest influencing JOINT:')
for p in (50, 90, 99, 100):
    print('     p%-4d %7.1f mm' % (p, np.percentile(worst * 1000, p)))
far = int((worst > 0.15).sum())
print('   vertices whose influence is more than 150 mm from its joint: %d (%.3f%%)'
      % (far, 100.0 * far / NV))
if far:
    bad = np.argsort(-worst)[:10]
    for i in bad:
        print('     v%-7d %6.1f mm  dominant %-22s at (%+.3f, %+.3f, %+.3f)'
              % (i, worst[i] * 1000, domname[i], P[i, 0], P[i, 1], P[i, 2]))

print('\n== 5. hard seams (dominant bone jumps between neighbours) =')
edges = np.vstack([tris[:, [0, 1]], tris[:, [1, 2]], tris[:, [0, 2]]])
edges = np.unique(np.sort(edges, axis=1), axis=0)
a, b = edges[:, 0], edges[:, 1]
jump = dom[a] != dom[b]
print('   mesh edges: %d   with a dominant-bone change: %d (%.2f%%)'
      % (len(edges), int(jump.sum()), 100.0 * jump.mean()))
# a "hard" seam = the two ends share almost no influence
wset = [set(np.nonzero(live[i])[0].tolist()) for i in range(NV)]
share = np.zeros(len(edges))
for k in np.nonzero(jump)[0]:
    ia, ib = int(a[k]), int(b[k])
    sa = set(wb[ia][live[ia]].tolist())
    sb = set(wb[ib][live[ib]].tolist())
    share[k] = len(sa & sb) / max(1, min(len(sa), len(sb)))
hard = jump & (share < 0.34)
print('   dominant changes sharing < 1/3 of their influences (TRUE hard seam): %d'
      % int(hard.sum()))
if hard.any():
    rows = []
    for k in np.nonzero(hard)[0]:
        ia, ib = int(a[k]), int(b[k])
        rows.append((float(np.linalg.norm(P[ia] - P[ib])), ia, ib))
    rows.sort(reverse=True)
    print('   the longest such edges (long edge + hard seam = the visible tear):')
    for L, ia, ib in rows[:12]:
        print('     %6.1f mm  %-20s | %-20s  at (%+.3f, %+.3f, %+.3f)'
              % (L * 1000, domname[ia], domname[ib], P[ia, 0], P[ia, 1], P[ia, 2]))
