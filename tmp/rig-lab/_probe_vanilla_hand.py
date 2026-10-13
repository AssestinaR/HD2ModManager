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
        ncol = min(bidx.shape[1], wgt.shape[1])
        bidx, wgt = bidx[:, :ncol], wgt[:, :ncol]
        pos = np.asarray([v['position'] for v in verts], np.float64)
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
            m = np.zeros(len(pos))
            if slots:
                hit = np.isin(bidx, list(slots)).any(axis=1)
                m[hit] = wgt[hit].sum(axis=1)
            return m

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
