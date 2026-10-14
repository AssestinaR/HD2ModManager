# -*- coding: utf-8 -*-
"""Is the wrist bone pivot at the centre of the wrist mesh ring?   (round 74)

Hypothesis under test (user, round 74):
    the wrist bone is not at the centre of the wrist mesh, so rotating the hand
    about the bone swings the wrist ring around instead of spinning it in place
    -- the "clock hand" mismatch (stem at 2 o'clock, tip at 3 o'clock).

Measurement: slice the arm perpendicular to the forearm axis (elbow head ->
wrist head) and, for each slice, report
    * n        vertices in the slice
    * off_mm   distance from the bone axis to the slice centroid
    * dir      direction of that offset, in degrees, in the axis frame
    * r_mm     mean radius of the slice (tube radius)
    * w_hand   mean hand-subtree weight

The bone axis is *exactly* the segment elbow_head -> wrist_head.  If the mesh
were centred on the bones, the centroid offset would sit near zero all the way
through the wrist and the hand weight would ramp symmetrically about t = 0.
A wrist ring whose centroid sits off the axis means a hand rotation about that
axis drags the ring around a circle of that radius instead of spinning it.

usage:
    _probe_wrist_center.py <patch_0> [--unit 0x...] [--side left]
    _probe_wrist_center.py --local <unit_basename_without_ext> [...]
"""
import argparse
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_hash as H
import hd2_patch as P
import hd2_unit as U

SDK = 'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
BONES = H.load_bonehash(os.path.join(SDK, 'hashlists', 'bonehash.txt'))
SIDES = {'left': ('l_elbow', 'l_hand'), 'right': ('r_elbow', 'r_hand')}
SLIM = 0xcae35ee15168145f


def _assemble(u, side):
    ti, bi = u.transform_info, u.bone_infos[0]
    names, heads = {}, {}
    for slot, ti_idx in enumerate(bi.real_indices):
        names[slot] = BONES.get(ti.hashes[ti_idx], '0x%08x' % ti.hashes[ti_idx])
        m = ti.matrices[ti_idx]
        heads[slot] = np.array([m[12], m[13], m[14]], np.float64)
    slot_of = {v: k for k, v in names.items() if not v.startswith('0x')}

    mi = max((m for m in u.mesh_infos if m.lod_index == 0),
             key=lambda m: m.num_sections)
    verts = u.decode_vertices(mi.stream_index)
    n = len(verts)
    pos = np.asarray([v['position'] for v in verts], np.float64)
    bidx = np.asarray([v['bone_index0'] for v in verts], np.int64)
    wgt = np.asarray([v['bone_weight'] for v in verts], np.float64)

    # raw bone_index values are MATERIAL-LOCAL: run them through
    # bi.remaps[material] before using them as slot indices.
    mat_of = np.full(n, -1, np.int32)
    indices = np.asarray(u.index_list(mi.stream_index), np.int64)
    for s in mi.sections:
        sv = np.unique(indices[s.index_offset:s.index_offset + s.num_indices])
        mat_of[sv] = s.material_index
    decoded = bidx.copy()
    for material in np.unique(mat_of[mat_of >= 0]):
        which = mat_of == material
        remap = np.asarray(bi.remaps[material], np.int64)
        decoded[which] = remap[np.minimum(bidx[which], len(remap) - 1)]

    # only vertices referenced by the LOD0 index buffer are actually drawn; the
    # rest belong to other LODs and carry un-remapped (meaningless) indices.
    live = mat_of >= 0
    pos, wgt, decoded = pos[live], wgt[live], decoded[live]

    el, hd = SIDES[side]
    root_ti = bi.real_indices[slot_of[hd]]
    hand_slots = []
    for slot, ti_idx in enumerate(bi.real_indices):
        seen, cur = set(), ti_idx
        while 0 <= cur < ti.count and cur not in seen:
            if cur == root_ti:
                hand_slots.append(slot)
                break
            seen.add(cur)
            cur = ti.entries[cur][1]
    wh = np.zeros(len(pos))
    for j in range(4):
        hit = np.isin(decoded[:, j], np.asarray(hand_slots))
        wh[hit] += wgt[hit, j]

    M = np.asarray(ti.matrices[mi.transform_index], np.float64).reshape(4, 4)
    # vertices come out in mesh-local space; bone heads are in unit space.
    # the transform is row-major with the translation in the LAST ROW.
    pos = pos @ M[:3, :3] + M[3, :3]
    return pos, wh, heads[slot_of[el]], heads[slot_of[hd]], M, names, slot_of


def load_patch(patch, unit, side):
    entries, _ = P.scan_entries(patch)
    e = next(x for x in entries
             if x['type_id'] == U.UNIT_ID and x['file_id'] == unit)
    pl = P.entry_payload(e, patch)
    return _assemble(U.Unit.read(pl['toc'], pl['gpu']), side)


def load_local(base, side):
    toc = open(base + '.toc', 'rb').read()
    gpu = open(base + '.gpu_resources', 'rb').read()
    return _assemble(U.Unit.read(toc, gpu), side)


def profile(pos, wh, E, W, radius, lo, hi, step):
    axis = W - E
    L = float(np.linalg.norm(axis))
    axis = axis / L
    tmp = np.array([0.0, 0.0, 1.0])
    if abs(float(axis @ tmp)) > 0.9:
        tmp = np.array([0.0, 1.0, 0.0])
    N_ = np.cross(axis, tmp); N_ /= np.linalg.norm(N_)
    V_ = np.cross(N_, axis); V_ /= np.linalg.norm(V_)

    rel = pos - W
    t = rel @ axis
    perp = rel - np.outer(t, axis)
    rad = np.linalg.norm(perp, axis=1)

    print('  forearm axis length L = %.1f mm' % (L * 1000.0))
    print()
    print('   t(mm)     n   off(mm)    dir(deg)   r(mm)  w_hand')
    worst = (0.0, 0.0)
    for a in np.arange(lo, hi - 1e-9, step):
        b = a + step
        m = (t >= a) & (t < b) & (rad < radius)
        if int(m.sum()) < 5:
            print('  %7.0f  %4d        -          -        -       -'
                  % (a * 1000.0, int(m.sum())))
            continue
        c = perp[m].mean(axis=0)
        off = float(np.linalg.norm(c))
        ang = float(np.degrees(np.arctan2(c @ N_, c @ V_)))
        if abs(a) <= 0.06 and off > worst[1]:
            worst = (a * 1000.0, off)
        print('  %7.0f  %4d  %7.2f  %9.1f  %6.2f  %6.3f'
              % (a * 1000.0, int(m.sum()), off * 1000.0, ang,
                 rad[m].mean() * 1000.0, wh[m].mean()))
    print()
    print('  worst |centroid offset| within +/-60 mm of the wrist bone:'
          ' %.2f mm  (at t = %.0f mm)' % (worst[1] * 1000.0, worst[0]))

    # how far is the wrist bone pivot from the mesh it is supposed to spin?
    ring = (np.abs(t) <= 0.02) & (rad < radius)
    if int(ring.sum()) >= 5:
        rc = perp[ring].mean(axis=0)
        print('  wrist ring (|t|<=20 mm) : %d verts, centroid %.2f mm off the bone axis'
              % (int(ring.sum()), float(np.linalg.norm(rc)) * 1000.0))
    # where does the hand weight hand over?
    m = rad < radius
    if int(m.sum()) > 10:
        tt, ww = t[m], wh[m]
        o = np.argsort(tt)
        tt, ww = tt[o], ww[o]

        def at(v):
            s = tt[ww >= v]
            return float(s.min()) * 1000.0 if len(s) else float('nan')

        print('  hand weight ramp (t, mm) : 0.1 at %.0f, 0.5 at %.0f, 0.9 at %.0f'
              % (at(0.1), at(0.5), at(0.9)))
    return L


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('patch', nargs='?')
    ap.add_argument('--local', default=None,
                    help='unit basename without extension (uses .toc/.gpu_resources)')
    ap.add_argument('--unit', default='0x%016x' % SLIM)
    ap.add_argument('--side', default='left', choices=('left', 'right'))
    ap.add_argument('--radius', type=float, default=0.10,
                    help='max distance from the axis to count a vertex (m)')
    ap.add_argument('--lo', type=float, default=-0.30)
    ap.add_argument('--hi', type=float, default=0.30)
    ap.add_argument('--step', type=float, default=0.02)
    args = ap.parse_args()

    if args.local:
        pos, wh, E, W, M, names, slot_of = load_local(args.local, args.side)
        label = args.local
    else:
        pos, wh, E, W, M, names, slot_of = load_patch(
            args.patch, int(args.unit, 16), args.side)
        label = '%s  unit %s' % (os.path.basename(args.patch), args.unit)

    print('=== %s  (%s side) ===' % (label, args.side))
    print('  vertices (LOD0)          : %d' % len(pos))
    print('  mesh transform identity  : %s' % bool(np.allclose(M, np.eye(4), atol=1e-6)))
    print('  l_elbow head (unit space): %s' % np.round(E, 4))
    print('  l_hand  head (unit space): %s' % np.round(W, 4))
    print('  bones present            : %s'
          % ', '.join(sorted(n for n in names.values()
                             if n.startswith(args.side[0] + '_') and
                             any(k in n for k in ('hand', 'elbow', 'shoulder')))))
    print()
    profile(pos, wh, E, W, args.radius, args.lo, args.hi, args.step)
    return 0


if __name__ == '__main__':
    sys.exit(main())
