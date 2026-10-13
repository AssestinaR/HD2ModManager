# -*- coding: utf-8 -*-
"""READ-ONLY forearm weight profile (round 60).

Answers: is the forearm a rigid segment with a narrow blend band at the wrist,
or a smooth rubber band between `l_elbow` and `l_hand`?
"""
import os
import sys

import numpy as np

import hd2_hash as H
import hd2_patch as P
import hd2_unit as U

HERE = os.path.dirname(os.path.abspath(__file__))
SDK = 'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
OURS = 'E:/mod/Lunalice-RE2310-Test/9ba626afa44a3aa3.patch_0'
OUR_UNIT = 0xcae35ee15168145f

BONES = H.load_bonehash(os.path.join(SDK, 'hashlists', 'bonehash.txt'))


def load(patch=None, unit=None):
    """-> the first unit whose rig exposes both `l_elbow` and `l_hand`."""
    patch = patch or OURS
    entries, _raw = P.scan_entries(patch)
    for e in entries:
        if e['type_id'] != U.UNIT_ID:
            continue
        if unit and e['file_id'] != unit:
            continue
        pl = P.entry_payload(e, patch)
        try:
            u = U.Unit.read(pl['toc'], pl['gpu'])
        except Exception:
            continue
        ti = u.transform_info
        names = {BONES.get(ti.hashes[t], '0x%08x' % ti.hashes[t])
                 for t in u.bone_infos[0].real_indices}
        print('  unit 0x%016x: %d bones  %s'
              % (e['file_id'], len(u.bone_infos[0].real_indices),
                 sorted(names)[:14]))
        if {'l_elbow', 'l_hand'} <= names and len(u.bone_infos[0].real_indices) > 8:
            return u
    raise LookupError('no unit with a full arm rig in %s' % patch)


def main():
    patch = sys.argv[1] if len(sys.argv) > 1 else None
    u = load(patch)
    print('unit under test: %s' % (patch or OURS))
    ti = u.transform_info
    bi = u.bone_infos[0]
    slot_of_ti = {ti_idx: slot for slot, ti_idx in enumerate(bi.real_indices)}
    name = {}
    pos_of = {}
    for slot, ti_idx in enumerate(bi.real_indices):
        h = ti.hashes[ti_idx]
        name[slot] = BONES.get(h, '0x%08x' % h)
        m = ti.matrices[ti_idx]
        pos_of[slot] = np.array([m[12], m[13], m[14]], np.float64)
    slot_of = {v: k for k, v in name.items() if not v.startswith('0x')}

    verts = u.decode_vertices(0)
    pos = np.asarray([v['position'] for v in verts], np.float64)
    bidx = np.asarray([v['bone_index0'] for v in verts], np.int32)
    wgt = np.asarray([v['bone_weight'] for v in verts], np.float64)

    print('=' * 100)
    print('[A] every bone that carries weight (slot, name, #vertices dominated)')
    dom = bidx[np.arange(len(bidx)), np.argmax(wgt, axis=1)]
    used = sorted(set(np.unique(bidx[wgt > 0]).tolist()))
    print('  %d of %d slots carry weight' % (len(used), len(bi.real_indices)))
    for s in used:
        n_dom = int((dom == s).sum())
        n_any = int((wgt[:, :] * (bidx == s)).sum() > 0)  # placeholder
        print('    slot %3d  %-32s dominated=%-6d' % (s, name.get(s, '?'), n_dom))

    print()
    print('=' * 100)
    print('[B] weight profile along the FOREARM axis  (elbow -> hand)')
    for side, el, hd in (('left', 'l_elbow', 'l_hand'), ('right', 'r_elbow', 'r_hand')):
        E = pos_of[slot_of[el]]
        Hh = pos_of[slot_of[hd]]
        u_ax = Hh - E
        L = np.linalg.norm(u_ax)
        u_ax = u_ax / L
        print('  %s: %s%s -> %s%s   length %.4f m' % (side, el, np.round(E, 4),
                                                       hd, np.round(Hh, 4), L))
        rel = pos - E
        s = rel @ u_ax
        # distance to the axis line
        d = np.linalg.norm(rel - np.outer(s, u_ax), axis=1)
        near = (d < 0.10) & (s > -0.03) & (s < L + 0.05)
        idxs = np.nonzero(near)[0]
        print('    %d vertices within 10 cm of the axis, s in [-0.03, L+0.05]'
              % len(idxs))
        bins = np.linspace(-0.03, L + 0.05, 15)
        print('    %-22s %-7s  %s' % ('s (m)', 'n', 'mean weight per bone'))
        for a, b in zip(bins[:-1], bins[1:]):
            sel = idxs[(s[idxs] >= a) & (s[idxs] < b)]
            if len(sel) == 0:
                continue
            acc = {}
            for bi_row, w_row in zip(bidx[sel], wgt[sel]):
                for b_i, w in zip(bi_row, w_row):
                    if w > 1e-6:
                        acc[b_i] = acc.get(b_i, 0.0) + w
            parts = ', '.join('%s=%.2f' % (name.get(k, '?'), v / len(sel))
                              for k, v in sorted(acc.items(), key=lambda x: -x[1])[:4])
            print('    [%6.3f,%6.3f)       %-7d  %s' % (a, b, len(sel), parts))

    print()
    print('=' * 100)
    print('[C] weight profile along the UPPER-ARM axis  (shoulder -> elbow)')
    for side, sh, el in (('left', 'l_shoulder', 'l_elbow'),
                         ('right', 'r_shoulder', 'r_elbow')):
        S = pos_of[slot_of[sh]]
        E = pos_of[slot_of[el]]
        u_ax = E - S
        L = np.linalg.norm(u_ax)
        u_ax = u_ax / L
        print('  %s: %s -> %s   length %.4f m' % (side, sh, el, L))
        rel = pos - S
        s = rel @ u_ax
        d = np.linalg.norm(rel - np.outer(s, u_ax), axis=1)
        near = (d < 0.10) & (s > -0.03) & (s < L + 0.03)
        idxs = np.nonzero(near)[0]
        bins = np.linspace(-0.03, L + 0.03, 12)
        for a, b in zip(bins[:-1], bins[1:]):
            sel = idxs[(s[idxs] >= a) & (s[idxs] < b)]
            if len(sel) == 0:
                continue
            acc = {}
            for bi_row, w_row in zip(bidx[sel], wgt[sel]):
                for b_i, w in zip(bi_row, w_row):
                    if w > 1e-6:
                        acc[b_i] = acc.get(b_i, 0.0) + w
            parts = ', '.join('%s=%.2f' % (name.get(k, '?'), v / len(sel))
                              for k, v in sorted(acc.items(), key=lambda x: -x[1])[:4])
            print('    [%6.3f,%6.3f)       %-7d  %s' % (a, b, len(sel), parts))
    return 0


if __name__ == '__main__':
    sys.exit(main())
