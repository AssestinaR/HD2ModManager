# -*- coding: utf-8 -*-
"""READ-ONLY: is the forearm mesh CONNECTED to the hand mesh?

Round 75, before writing the alignment field.  If the forearm and the glove/hand
are one connected surface, moving the forearm ring at the wrist drags the hand
with it; if they are two separate components, a correction that stops at the
wrist opens no seam at all.  Union-find over the LOD0 index buffer answers it
directly.

Also reports, per component, how its vertices distribute over the bone groups
(elbow / twist / hand / fingers), which tells us where each shell lives.

usage:
    _probe_arm_components.py <patch_0> [--unit 0x...] [--side left]
"""
import argparse
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import _probe_twist_lag as TL


def components(nv, tris):
    parent = np.arange(nv, dtype=np.int64)

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    for a, b, c in tris:
        ra, rb, rc = find(a), find(b), find(c)
        for x in (rb, rc):
            if x != ra:
                parent[x] = ra
    roots = np.array([find(i) for i in range(nv)], np.int64)
    return roots


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('patch', nargs='?')
    ap.add_argument('--local', default=None)
    ap.add_argument('--unit', default='0x%016x' % TL.SLIM)
    ap.add_argument('--side', default='left')
    ap.add_argument('--label', default=None)
    args = ap.parse_args()

    if args.local:
        d = TL.load_local(args.local, args.side)
        label = args.label or os.path.basename(args.local)
    else:
        d = TL.load_patch(args.patch, int(args.unit, 16), args.side)
        label = args.label or os.path.basename(args.patch)

    u = d['unit']
    mi = d['mi']
    pre = 'l' if args.side == 'left' else 'r'
    flat = np.asarray(u.index_list(mi.stream_index), np.int64)
    tris = flat.reshape(-1, 3)

    ti, bi = u.transform_info, u.bone_infos[0]
    names = {s: TL.BONES.get(ti.hashes[i], '0x%08x' % ti.hashes[i])
             for s, i in enumerate(bi.real_indices)}
    slot_of = {v: k for k, v in names.items() if not v.startswith('0x')}

    # which LOD0-live vertex indices exist at all
    live = np.unique(flat)
    nv = int(live.max()) + 1
    roots = components(nv, tris)
    print()
    print('=' * 92)
    print('%s  [%s]  LOD0 mesh: %d live vertices, %d triangles'
          % (label, args.side, len(live), len(tris)))
    print('=' * 92)

    pos, wgt, dec, mat = d['pos'], None, None, None
    # reuse the probe's own loader for the weights
    posl, wgtl, decl, matl = TL._live_lod0(u, mi)

    E, W = d['E'], d['W']
    axis = W - E
    L = float(np.linalg.norm(axis))
    axis = axis / L
    rel = posl - E
    t = rel @ axis
    rad = np.linalg.norm(rel - np.outer(t, axis), axis=1)

    # group each vertex by its dominant bone
    groups = {}
    for s, nm in names.items():
        groups[nm] = []
    dom = []
    for i in range(len(posl)):
        j = int(np.argmax(wgtl[i]))
        dom.append(names.get(int(decl[i, j]), '?'))

    # map live-order index back to stream index
    live_sorted = np.sort(live)
    stream_of_live = live_sorted          # live vertices, ascending
    root_of_live = roots[live_sorted]

    # for each dominant bone name, the set of components it appears in
    from collections import Counter
    per_bone = {}
    for k, nm in enumerate(dom):
        per_bone.setdefault(nm, []).append(k)

    print('  dominant bone            n     components   t(mm) range     |t| band')
    want = [nm for nm in per_bone if nm.startswith(pre + '_')]
    for nm in sorted(want, key=lambda x: min(t[np.asarray(per_bone[x])])):
        idx = np.asarray(per_bone[nm], np.int64)
        comps = Counter(root_of_live[idx].tolist())
        tt = t[idx]
        print('  %-22s %5d  %12d   %7.0f..%7.0f   rad<=%.0f'
              % (nm, len(idx), len(comps), tt.min() * 1000, tt.max() * 1000,
                 rad[idx].max() * 1000))

    # the components that hold forearm (elbow) vs hand/finger vertices
    def comps_of(names_):
        out = set()
        for nm in names_:
            if nm in per_bone:
                out |= set(root_of_live[np.asarray(per_bone[nm])].tolist())
        return out

    elbow_c = comps_of([pre + '_elbow'])
    twist_c = comps_of([pre + '_hand_twist'])
    hand_c = comps_of([nm for nm in per_bone
                       if nm.startswith(pre + '_') and 'finger' in nm]
                      + [pre + '_hand'])
    print()
    print('  components: elbow %d   twist %d   hand/fingers %d'
          % (len(elbow_c), len(twist_c), len(hand_c)))
    print('  elbow  & twist shared: %d' % len(elbow_c & twist_c))
    print('  twist  & hand  shared: %d' % len(twist_c & hand_c))
    print('  elbow  & hand  shared: %d' % len(elbow_c & hand_c))
    sizes = Counter(root_of_live.tolist())
    print('  component sizes (top 8): %s'
          % ', '.join('%d' % v for _k, v in sizes.most_common(8)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
