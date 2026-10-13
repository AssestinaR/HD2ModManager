# -*- coding: utf-8 -*-
"""Name the bones behind the `hops` histogram, and the geometry they drag.

`meta.json` only aggregates the fallback depth (`{'7': 2}` = two bones sit seven
levels away from their nearest MAPPED ancestor), so it cannot tell us WHICH
bones those are.  This probe re-walks the SOURCE hierarchy exactly the way
`hd2_style.resolve_bone_map` does and then, for every bone that needed a
fallback, reports

  * how many hops it took,
  * which HD2 bone the `region` rule actually bound it to and how far that
    joint sits from the source joint (the `by_dist` list retarget only prints
    the worst six of),
  * how many source vertices carry its weight and where those vertices are.

`hops` is purely structural -- it is the number of unmapped ancestors walked --
so it needs no alignment.  `resolved` DOES need one, because the `region` rule
picks the nearest joint; that alignment is reproduced here verbatim from
`retarget` steps 1a/1b (yaw-only fit + one uniform scale + one translation), so
the answer is the same one the real pipeline got.

usage: _probe_hops.py [--min-hops N] [--only l_pinky,r_pinky]
"""
import argparse
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import export_obj as E
import hd2_source as S
import hd2_style as ST

TOC = os.path.join(HERE, 'avatar/unit_0x4d1c334d294dfa97.toc')
BH = os.path.join(HERE,
                  '../pb-ref/HD2PhysBone-ReferenceSource-main/kit/tools/bonehash.txt')
SPINE_MAP = 'mesh'
HEAD_OFFSET = -0.015

RUNS = [('Lunalice', S.ADDONS, 'Lunalice_Zoey.vpk'),
        ('Lilium  ', 'E:/tmp/lilium/', 'Lilium_Sancta_Zoey.vpk')]
MODEL = 'survivor_teenangst'


def rig():
    hd2 = E.read_hd2_rig(TOC, BH)
    by_idx_name = {h['index']: h['name'] for h in hd2}
    by_idx = {h['index']: h for h in hd2}
    children, parents = {}, {}
    for h in hd2:
        p = h.get('parent')
        if p is None or p == h['index'] or p not in by_idx:
            continue
        children.setdefault(p, []).append(h['index'])
        parents[h['index']] = p
    return hd2, by_idx_name, by_idx, children, parents


def alignment(vpk, model, hby, body):
    """Reproduce retarget 1a/1b: yaw R, uniform s, translation t, and `sole`."""
    _p, _mdl, verts, tris, src, short = ST.load_source(vpk, model)
    src_pts = np.array([[v['pos'][0] * E.IN2M, v['pos'][1] * E.IN2M,
                         v['pos'][2] * E.IN2M] for v in verts], np.float64)
    A = np.array([ST.origin_np(ST.m_to_np(short[n]['world_m'])) for n, _h in body])
    B = np.array([ST.origin_np(ST.m_to_np(hby[h]['world'])) for _n, h in body])
    R, _ca, _cb = ST.yaw_rotation(A, B)
    lo = float(src_pts[:, 2].min())
    sole = src_pts[src_pts[:, 2] < lo + 0.01].mean(0)
    head_src = ST.origin_np(ST.m_to_np(short['Head1']['world_m']))
    head_hd2 = ST.origin_np(ST.m_to_np(hby['head']['world']))

    def rot_only(P):
        return P @ R.T

    src_h = float(rot_only(head_src.reshape(1, 3))[0][2]
                  - rot_only(sole.reshape(1, 3))[0][2])
    s = (head_hd2[2] + HEAD_OFFSET - 0.0) / max(src_h, 1e-6)

    def align_pts(P):
        return ((P - sole) * s) @ R.T

    t = (head_hd2 + np.array([0.0, 0.0, HEAD_OFFSET])) - align_pts(
        head_src.reshape(1, 3))[0]
    return dict(src=src, short=short, verts=verts, tris=tris, s=s, R=R,
                align_pts=align_pts, t=t)


def analyse(tag, root, vpk, hd2, by_idx_name, by_idx, children, parents,
            min_hops, only):
    body = ST.snap_body(SPINE_MAP)
    hby = {h['name']: h for h in hd2 if h['name']}
    al = alignment(os.path.join(root, vpk), MODEL, hby, body)
    src, short, verts = al['src'], al['short'], al['verts']

    src_index_to_hd2 = {}
    for sn, hn in ST.snap_pairs(SPINE_MAP) + ST.SNAP_TOES:
        sb, hb = short.get(sn), hby.get(hn)
        if sb is not None and hb is not None:
            src_index_to_hd2[sb['index']] = hb['index']

    def ajoint(b):
        return al['align_pts'](ST.origin_np(
            ST.m_to_np(b['world_m'])).reshape(1, 3))[0] + al['t']

    resolved, hops, by_dist = ST.resolve_bone_map(
        src, src_index_to_hd2, 'region', ajoint, by_idx_name, children, parents)

    # ---- per-bone geometry ownership (source side) ------------------------
    vlist = np.array([v['pos'] for v in verts], np.float64)
    nref = {}
    idx_of = []
    for k, v in enumerate(verts):
        idx_of.append([v['bones'][j] for j in range(3)])
    prim = {}
    for k, v in enumerate(verts):
        js = [v['bones'][j] for j in range(3)]
        ws = [v['w'][j] for j in range(3)]
        if not js:
            continue
        j = int(np.argmax(ws))
        prim.setdefault(js[j], []).append(k)

    for i, row in enumerate(idx_of):
        for j in row:
            nref[j] = nref.get(j, 0) + 1

    hist = {}
    for i, n in hops.items():
        hist[n] = hist.get(n, 0) + 1
    print('=' * 78)
    print('%s  hops 直方图: %s' % (tag, dict(sorted(hist.items()))))
    print('  已直接映射的源骨 %d 根 / 需兜底 %d 根'
          % (len(src_index_to_hd2), len(hops) - len(src_index_to_hd2)))
    print()

    dmap = {nm: (h, d) for nm, h, d in by_dist}
    rows = []
    for b in src:
        i = b['index']
        n = hops.get(i)
        if n is None or n < min_hops:
            continue
        if only and not any(o in (b['name'] or '') for o in only):
            continue
        hname, dist = dmap.get(b['name'], (by_idx_name.get(resolved.get(i)), -1.0))
        ks = prim.get(i, [])
        if ks:
            pts = vlist[ks]
            bb = 'X[%.3f,%.3f] Y[%.3f,%.3f] Z[%.3f,%.3f]' % (
                pts[:, 0].min(), pts[:, 0].max(), pts[:, 1].min(),
                pts[:, 1].max(), pts[:, 2].min(), pts[:, 2].max())
            cen = pts.mean(0)
            cen = '(%.3f,%.3f,%.3f)' % (cen[0], cen[1], cen[2])
        else:
            bb, cen = '-', '-'
        rows.append((n, -dist if dist >= 0 else -9e9, b['name'], i,
                     hname, dist, len(ks), nref.get(i, 0), cen, bb))

    rows.sort(key=lambda r: (-r[0], r[1]))
    print('  %-24s %4s %-22s %8s %7s %7s  %s'
          % ('source bone', 'hops', '-> HD2 bone', 'dist(m)', 'prim#', 'ref#',
             'primary-vertex centroid / bbox'))
    for n, _nd, nm, i, hname, dist, nk, nrf, cen, bb in rows:
        print('  %-24s %4d %-22s %8s %7d %7d  %s %s'
              % (nm, n, str(hname), ('%.3f' % dist) if dist >= 0 else '?',
                 nk, nrf, cen, bb))
    print()
    return dict(hops=hist, resolved=resolved, by_dist=dmap)


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument('--min-hops', type=int, default=2)
    ap.add_argument('--only', default='')
    a = ap.parse_args(argv)
    only = [x.strip() for x in a.only.split(',') if x.strip()]

    hd2, by_idx_name, by_idx, children, parents = rig()
    out = {}
    for tag, root, vpk in RUNS:
        out[tag] = analyse(tag, root, vpk, hd2, by_idx_name, by_idx,
                           children, parents, a.min_hops, only)
    return 0


if __name__ == '__main__':
    sys.exit(main())
