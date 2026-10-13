# -*- coding: utf-8 -*-
"""Pick the `region`-fallback constraint from data, not from a guess.

`_probe_hops.py` shows the sleeve binds 0.8-1.6 m away while every Lunalice
fallback stays under 0.44 m.  But an ABSOLUTE cap would be a magic number tied
to this model's size.  The scale-free question is:

    is the best candidate a genuine REFINEMENT of the mapped ancestor,
    or merely the least-bad member of a bad lot?

so for every fallback bone this probe reports

    d_best      distance to the candidate `region` picks today
    d_anc       distance to the MAPPED ANCESTOR it hangs from
    ratio       d_best / d_anc
    depth       how many HD2 levels the candidate sits BELOW that ancestor

A correct fallback (shoes -> ankle/ball, hair -> head, skirt -> hips) should
have the candidate either AT the ancestor (ratio 1.0, depth 0) or clearly
closer than it (ratio well under 1).  A runaway one should look like the sleeve:
a deep candidate that is not meaningfully closer than the ancestor.

usage: _probe_fallback_rule.py
"""
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import _probe_hops as PH

ST = PH.ST


def analyse(tag, root, vpk, hd2, by_idx_name, by_idx, children, parents):
    body = ST.snap_body(PH.SPINE_MAP)
    hby = {h['name']: h for h in hd2 if h['name']}
    al = PH.alignment(os.path.join(root, vpk), PH.MODEL, hby, body)
    src, short = al['src'], al['short']

    m = {}
    for sn, hn in ST.snap_pairs(PH.SPINE_MAP) + ST.SNAP_TOES:
        sb, hb = short.get(sn), hby.get(hn)
        if sb is not None and hb is not None:
            m[sb['index']] = hb['index']

    def ajoint(b):
        return al['align_pts'](ST.origin_np(
            ST.m_to_np(b['world_m'])).reshape(1, 3))[0] + al['t']

    # replicate `region` exactly, but keep the intermediate quantities
    cand = [(m[b['index']], ajoint(b)) for b in src if b['index'] in m]
    MARK = {'l_clavicle': 'l_arm', 'r_clavicle': 'r_arm',
            'l_thigh': 'l_leg', 'r_thigh': 'r_leg', 'neck': 'head'}
    sub, lim = {}, {}

    def subtree(r):
        if r not in sub:
            seen, stack = set(), [r]
            while stack:
                n = stack.pop()
                if n in seen:
                    continue
                seen.add(n)
                stack.extend(children.get(n, ()))
            sub[r] = seen
        return sub[r]

    def limb(h):
        if h not in lim:
            cur, n, got = h, 0, 'torso'
            while cur is not None and n < 64:
                nmv = by_idx_name.get(cur)
                if nmv in MARK:
                    got = MARK[nmv]
                    break
                cur = parents.get(cur)
                n += 1
            lim[h] = got
        return lim[h]

    # depth of every HD2 bone below the limb root, for the "depth" column
    def depth_below(root_h, h):
        cur, n = h, 0
        while cur is not None and n < 64:
            if cur == root_h:
                return n
            cur = parents.get(cur)
            n += 1
        return None

    # per-bone vertex counts (primary weight) so noise can be filtered
    prim = {}
    for v in al['verts']:
        j = int(np.argmax(v['w']))
        prim[v['bones'][j]] = prim.get(v['bones'][j], 0) + 1

    rows = []
    for b in src:
        i = b['index']
        if i in m:
            continue
        cur, n = b['parent'], 1
        while cur >= 0 and cur not in m and n <= 64:
            cur = src[cur]['parent']
            n += 1
        root_h = m.get(cur)
        if root_h is None:
            continue
        allow = subtree(root_h)
        lk = limb(root_h)
        pool = [(hi, sp) for hi, sp in cand
                if hi in allow and limb(hi) == lk] or cand
        p = ajoint(b)
        best, bd = None, 1e30
        for hi, sp in pool:
            d = float(((p - sp) ** 2).sum())
            if d < bd:
                bd, best = d, hi
        anc = next((sp for hi, sp in cand if hi == root_h), None)
        da = float(np.linalg.norm(p - anc)) if anc is not None else float('nan')
        rows.append(dict(name=b['name'], hops=n, prim=prim.get(i, 0),
                         root=by_idx_name.get(root_h),
                         best=by_idx_name.get(best),
                         dbest=float(np.sqrt(bd)), danc=da,
                         depth=depth_below(root_h, best)))

    rows.sort(key=lambda r: -r['dbest'])
    print('=' * 100)
    print('%s' % tag)
    print('  %-24s %4s %5s %-18s %-20s %8s %8s %6s %6s'
          % ('source bone', 'hops', 'prim', 'mapped ancestor', '-> candidate',
             'd_best', 'd_anc', 'ratio', 'depth'))
    for r in rows:
        rr = (r['dbest'] / r['danc']) if r['danc'] > 1e-9 else float('nan')
        print('  %-24s %4d %5d %-18s %-20s %8.3f %8.3f %6.2f %6s'
              % (r['name'], r['hops'], r['prim'], r['root'], r['best'],
                 r['dbest'], r['danc'], rr, r['depth']))
    return rows


def main():
    hd2, by_idx_name, by_idx, children, parents = PH.rig()
    all_rows = {}
    for tag, root, vpk in PH.RUNS:
        all_rows[tag] = analyse(tag, root, vpk, hd2, by_idx_name, by_idx,
                                children, parents)

    print()
    print('=' * 100)
    print('候选判据对比（只看真的带几何的骨头, prim>0）')
    for tag, rows in all_rows.items():
        rs = [r for r in rows if r['prim'] > 0]
        rr = [r['dbest'] / r['danc'] for r in rs if r['danc'] > 1e-9]
        print('  %s: %d 根带几何的兜底骨   d_best 最大 %.3f m   '
              'ratio 最小 %.2f / 中位 %.2f / 最大 %.2f'
              % (tag, len(rs), max(r['dbest'] for r in rs),
                 min(rr), float(np.median(rr)), max(rr)))
    print()
    print('把 ratio 阈值从 1.00 往下扫，看各自会改绑多少根:')
    print('  %-8s %-12s %-12s' % ('ratio<', 'Lunalice改绑', 'Lilium改绑'))
    for t in (1.0, 0.9, 0.8, 0.75, 0.7, 0.6, 0.5):
        out = []
        for tag, rows in all_rows.items():
            rs = [r for r in rows if r['prim'] > 0 and r['danc'] > 1e-9]
            k = sum(1 for r in rs if r['dbest'] / r['danc'] >= t
                    and r['best'] != r['root'])
            out.append(k)
        print('  %-8.2f %-12d %-12d' % (t, out[0], out[1]))
    return 0


if __name__ == '__main__':
    sys.exit(main())
