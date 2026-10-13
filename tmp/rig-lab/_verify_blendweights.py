# -*- coding: utf-8 -*-
"""Read the vertex groups straight out of zoey.blend and check the contract.

This is the end-to-end check that matters: it does not trust model.npz, it
opens the file the user will hand to the HD2SDK addon and reads
`OURS_bind`'s vertex groups the way the addon will.

Run with Blender:
  blender.exe --background --factory-startup --python _verify_blendweights.py -- \
      <zoey.blend> [out.json]
"""
import json
import math
import os
import struct
import sys

import bpy


def argv():
    a = sys.argv
    return a[a.index('--') + 1:] if '--' in a else []


def half(v):
    return struct.unpack('<e', struct.pack('<e', float(v)))[0]


def main():
    a = argv()
    blend = a[0] if a else 'out/blender/zoey.blend'
    out = a[1] if len(a) > 1 else 'out/blend-weight-check.json'
    bpy.ops.wm.open_mainfile(filepath=os.path.abspath(blend))

    ob = bpy.data.objects.get('OURS_bind')
    if ob is None:
        raise SystemExit('no OURS_bind object in %s (have %s)'
                         % (blend, [o.name for o in bpy.data.objects]))
    names = [g.name for g in ob.vertex_groups]
    me = ob.data
    nv = len(me.vertices)

    sums = []
    bad_sum = 0
    dup_bone = 0
    not_grid = 0
    zero = 0
    n_inf = {}
    worst = 0.0
    worst_v = -1
    for v in me.vertices:
        gs = [(g.group, float(g.weight)) for g in v.groups if g.weight > 0.0]
        n_inf[len(gs)] = n_inf.get(len(gs), 0) + 1
        if not gs:
            zero += 1
            sums.append(0.0)
            continue
        if len({g for g, _w in gs}) != len(gs):
            dup_bone += 1
        s = math.fsum(w for _g, w in gs)
        sums.append(s)
        if s != 1.0:
            bad_sum += 1
        for _g, w in gs:
            k = w * 2048.0
            if abs(k - round(k)) > 1e-9 or half(w) != w:
                not_grid += 1
                break
        d = abs(s - 1.0)
        if d > worst:
            worst, worst_v = d, v.index

    hsum_ok = 0
    hsum_bad = 0
    for v in me.vertices:
        gs = [float(g.weight) for g in v.groups if g.weight > 0.0]
        if not gs:
            continue
        if math.fsum(half(w) for w in gs) == 1.0:
            hsum_ok += 1
        else:
            hsum_bad += 1

    rep = dict(
        blend=os.path.abspath(blend),
        object=ob.name, vertices=nv, vertex_groups=len(names),
        influence_count=dict(sorted(n_inf.items())),
        weight_sum=dict(min=min(sums), max=max(sums)),
        vertices_sum_not_1=bad_sum,
        worst_sum_error=worst, worst_vertex=worst_v,
        vertices_with_duplicate_bone=dup_bone,
        weights_not_on_1_2048_grid=not_grid,
        vertices_with_no_influence=zero,
        half_sum_exactly_1=hsum_ok,
        half_sum_not_1=hsum_bad,
    )
    print(json.dumps(rep, indent=1, ensure_ascii=False))
    json.dump(rep, open(out, 'w', encoding='utf-8'), indent=1, ensure_ascii=False)
    print('[written] %s' % os.path.abspath(out))


main()
