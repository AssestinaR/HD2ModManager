# -*- coding: utf-8 -*-
"""READ-ONLY: how does HD2 weight the *hand + fingers* of a vanilla skin part?

Round 70 question
-----------------
Round 69 routed the whole forearm's distal follow onto l_hand (twist_frac=0) and
left the hand/fingers untouched.  To try a twist-bone version we must know the
ground truth: in vanilla, do finger vertices carry l_hand_twist weight, or are
they 100% on the finger bones?  That single fact decides whether "include the
hand and fingers in the split" is even correct.

This probe reuses _probe_vanilla_wrist.py's bundle reader (which works around the
GameDataPackageResolver bundled-archive bug) and prints, for a vanilla unit:

  * the palette
  * total weight per bone
  * a profile bucketed along the elbow->hand axis, extended past the wrist to the
    fingertips: mean weight of elbow / hand / hand_twist / forearm_twist / fingers

Usage:
  python _probe_vanilla_hand.py 0x8e95b37379fedf82 --archive 00cf0ef7df912a6e
                                [--out E:/tmp/round70-van] [--side left]
                                [--radius 0.30]
"""
import argparse
import os
import sys

import numpy as np

import _probe_vanilla_wrist as VW
import hd2_hash as H
import hd2_unit as U

SDK = VW.SDK


def cols(verts, keys, dtype):
    arrs = [np.asarray([v[k] for v in verts], dtype).reshape(len(verts), -1)
            for k in keys]
    return np.concatenate(arrs, axis=1) if arrs else np.zeros((len(verts), 0), dtype)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('file_id')
    ap.add_argument('--archive', default=None)
    ap.add_argument('--out', default='E:/tmp/round70-van')
    ap.add_argument('--side', default='left', choices=('left', 'right'))
    ap.add_argument('--radius', type=float, default=0.30)
    ap.add_argument('--no-write', action='store_true',
                    help='inspect bundled payload without overwriting extracted evidence')
    args = ap.parse_args()

    fid = int(args.file_id, 16)
    rows = VW.lookup(fid, args.archive)
    if not rows:
        print('no archive entry')
        return 1
    packages = VW.load_packages()
    archive_id, toc_off, toc_size, gpu_off, gpu_size = rows[0]
    toc = VW.extract_bundle_range(archive_id, toc_off, toc_size, packages)
    gpu = VW.extract_bundle_range(archive_id + '.gpu_resources', gpu_off, gpu_size, packages)
    if gpu is None and gpu_size:
        gpu = VW.extract_bundle_range(archive_id, gpu_off, gpu_size, packages)
    if toc is None or gpu is None:
        print('FAILED to reconstruct payload')
        return 1
    if not args.no_write:
        os.makedirs(args.out, exist_ok=True)
        base = os.path.join(args.out, 'unit_0x%016x' % fid)
        with open(base + '.toc', 'wb') as f:
            f.write(toc)
        with open(base + '.gpu_resources', 'wb') as f:
            f.write(gpu)

    bones = H.load_bonehash(SDK + '/hashlists/bonehash.txt')
    unit = U.Unit.read(toc, gpu)
    ti, bi = unit.transform_info, unit.bone_infos[0]
    names, heads = {}, {}
    for slot, ti_idx in enumerate(bi.real_indices):
        names[slot] = bones.get(ti.hashes[ti_idx], '0x%08x' % ti.hashes[ti_idx])
        m = ti.matrices[ti_idx]
        heads[slot] = np.array([m[12], m[13], m[14]], np.float64)
    slot_of = {v: k for k, v in names.items() if not v.startswith('0x')}

    print('palette (%d slots)' % len(names))
    for slot in sorted(names):
        print('  %3d  %-26s head=(%9.5f,%9.5f,%9.5f)' % (slot, names[slot], *heads[slot]))

    for mi in unit.mesh_infos:
        if mi.lod_index != 0:
            continue
        verts = unit.decode_vertices(mi.stream_index)
        bi_keys = sorted(k for k in verts[0] if k.startswith('bone_index'))
        bw_keys = sorted(k for k in verts[0] if k.startswith('bone_weight'))
        bidx = cols(verts, bi_keys, np.int64)
        wgt = cols(verts, bw_keys, np.float64)
        if bi_keys != ['bone_index0'] or bidx.shape != wgt.shape:
            raise ValueError('probe supports one matching index/weight group only; do not truncate native multi-group data')
        mesh_matrix = np.asarray(ti.matrices[mi.transform_index]).reshape(4, 4)
        if not np.allclose(mesh_matrix, np.eye(4), atol=1e-6, rtol=0):
            raise ValueError('probe requires identity mesh transform; convert spaces before profiling')
        ncol = wgt.shape[1]
        # Vanilla bone_index values are *material-local* indices, NOT bi slots.
        # SDK import resolves remaps[material][raw_index] -> bi.real_indices.
        mat_of = np.full(len(verts), -1, dtype=np.int32)
        indices = np.asarray(unit.index_list(mi.stream_index), dtype=np.int64)
        for section in mi.sections:
            section_verts = np.unique(indices[section.index_offset:
                                              section.index_offset + section.num_indices])
            conflict = (mat_of[section_verts] >= 0) & (mat_of[section_verts] != section.material_index)
            if conflict.any():
                raise ValueError('vertices shared across different material remaps')
            mat_of[section_verts] = section.material_index
        if (mat_of < 0).any():
            raise ValueError('unreferenced vertices: material remap is unknown')
        original_indices = bidx.copy()
        decoded = np.zeros_like(bidx)
        for material in np.unique(mat_of):
            if material >= len(bi.remaps):
                raise ValueError('material remap out of range')
            which = mat_of == material
            remap = np.asarray(bi.remaps[material], dtype=np.int64)
            active = wgt[which] > 1e-6
            if (bidx[which][active] >= len(remap)).any():
                raise ValueError('positive weight references missing remap index')
            decoded[which] = remap[np.minimum(bidx[which], len(remap) - 1)]
        bidx = decoded
        pos = np.asarray([v['position'] for v in verts], np.float64)
        print('  material remaps: %s; positive raw indices changed: %d'
              % (list(map(len, bi.remaps)), int(((bidx != original_indices) & (wgt > 1e-6)).sum())))
        print()
        print('mesh lod0 stream=%d verts=%d  bidx%s wgt%s'
              % (mi.stream_index, len(pos), bidx.shape, wgt.shape))

        mass = np.zeros(max(len(names), int(bidx.max()) + 1))
        for j in range(ncol):
            np.add.at(mass, bidx[:, j], wgt[:, j])
        print('  total weight per bone (top 24):')
        for slot in np.argsort(-mass)[:24]:
            if mass[slot] <= 0:
                continue
            print('    %3d  %-26s %10.2f' % (slot, names.get(int(slot), '?'), mass[slot]))

        sd = args.side[0]
        el, hd = sd + '_elbow', sd + '_hand'
        if el not in slot_of or hd not in slot_of:
            print('  (%s chain not in this palette)' % args.side)
            continue
        tw = slot_of.get(sd + '_hand_twist')
        ft = slot_of.get(sd + '_forearm_twist')
        wt = slot_of.get(sd + '_wrist_twist')
        fingers = sorted(s for s, n in names.items() if n.startswith(sd + '_') and 'finger' in n)

        E, W = heads[slot_of[el]], heads[slot_of[hd]]
        axis = W - E
        L = float(np.linalg.norm(axis))
        axis /= L
        rel = pos - E
        t = rel @ axis
        rad = np.linalg.norm(rel - np.outer(t, axis), axis=1)

        def wmask(slots):
            # Match each weight COLUMN to its corresponding palette-index column.
            # Selecting whole rows would incorrectly count every bone on that row.
            if not slots:
                return np.zeros(len(pos))
            return np.where(np.isin(bidx, list(slots)), wgt, 0.0).sum(axis=1)

        m_el, m_hd = wmask([slot_of[el]]), wmask([slot_of[hd]])
        m_tw, m_ft, m_wt = wmask([tw]), wmask([ft]), wmask([wt])
        m_fg = wmask(fingers)

        print()
        print('  [%s] elbow->hand L=%.1f mm, radius gate %.0f mm' % (args.side, L * 1000, args.radius * 1000))
        print('     t/L      n      elbow     hand    handTw   foreTw  wristTw   finger')
        for k in range(28):
            lo, hi = k * 0.05, (k + 1) * 0.05
            sel = (rad < args.radius) & (t / L >= lo) & (t / L < hi)
            if sel.sum() == 0:
                continue
            print('     %5.2f  %6d   %7.3f  %7.3f  %7.3f  %7.3f  %7.3f  %7.3f'
                  % (0.5 * (lo + hi), int(sel.sum()), m_el[sel].mean(), m_hd[sel].mean(),
                     m_tw[sel].mean(), m_ft[sel].mean(), m_wt[sel].mean(), m_fg[sel].mean()))

        # and a per-finger-bone breakdown beyond the wrist
        far = rad < args.radius
        far &= (t / L > 1.0)
        if far.sum():
            print('  past the wrist (t/L>1): %d verts; mean finger weight %.3f, '
                  'mean hand %.3f, mean twist %.3f'
                  % (int(far.sum()), m_fg[far].mean(), m_hd[far].mean(), m_tw[far].mean()))
    return 0


if __name__ == '__main__':
    sys.exit(main())
