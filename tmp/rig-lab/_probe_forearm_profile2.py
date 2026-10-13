# -*- coding: utf-8 -*-
"""READ-ONLY: per-shell hand/elbow profile along the forearm axis.

Splits the round-60 "near-axis cylinder" selection by
  * side,
  * the material its triangles use (skin vs glove vs ornament),
  * whether the vertex is already fully bound to the hand (the rigid glove cuff)
so the transition band can be chosen from data instead of guessed.

Usage: python _probe_forearm_profile2.py [patch_0] [unit_hex]
"""
import json
import os
import sys

import numpy as np

import hd2_hash as H
import hd2_patch as P
import hd2_unit as U

HERE = os.path.dirname(os.path.abspath(__file__))
SDK = 'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
DEFAULT_PATCH = 'E:/mod/Lunalice-RE2310-M1/9ba626afa44a3aa3.patch_0'
DEFAULT_UNIT = 0xcae35ee15168145f
MANIFEST = 'E:/mod/Lunalice-Native-RE2310/lunalice-native-re2310.manifest.json'

BONES = H.load_bonehash(os.path.join(SDK, 'hashlists', 'bonehash.txt'))


def main():
    patch = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_PATCH
    unit_id = int(sys.argv[2], 16) if len(sys.argv) > 2 else DEFAULT_UNIT
    manifest = json.load(open(MANIFEST, encoding='utf-8'))
    by_id = {int(m['id'], 16): m['name'].split('\\')[-1] for m in manifest['materials']}

    entries, _ = P.scan_entries(patch)
    e = next(x for x in entries
             if x['type_id'] == U.UNIT_ID and x['file_id'] == unit_id)
    pl = P.entry_payload(e, patch)
    u = U.Unit.read(pl['toc'], pl['gpu'])

    ti = u.transform_info
    bi = u.bone_infos[0]
    names, heads = {}, {}
    for slot, ti_idx in enumerate(bi.real_indices):
        names[slot] = BONES.get(ti.hashes[ti_idx], '0x%08x' % ti.hashes[ti_idx])
        m = ti.matrices[ti_idx]
        heads[slot] = np.array([m[12], m[13], m[14]], np.float64)
    slot_of = {v: k for k, v in names.items() if not v.startswith('0x')}

    mi = max((m for m in u.mesh_infos if m.lod_index == 0), key=lambda m: m.num_sections)
    stream = mi.stream_index
    verts = u.decode_vertices(stream)
    pos = np.asarray([v['position'] for v in verts], np.float64)
    bidx = np.asarray([v['bone_index0'] for v in verts], np.int32)
    wgt = np.asarray([v['bone_weight'] for v in verts], np.float64)
    idx = np.frombuffer(u.gpu_index[stream], np.uint32).astype(np.int64).reshape(-1, 3)

    asset_of = {}
    vmat = np.full(len(pos), '', object)
    for sec in mi.sections:
        if sec.num_indices == 0:
            continue
        asset = u.material_ids[u.section_ids.index(mi.material_ids[sec.material_index])]
        nm = by_id.get(asset, '0x%016x' % asset)
        a0 = sec.index_offset // 3
        a1 = (sec.index_offset + sec.num_indices) // 3
        for t in idx[a0:a1]:
            vmat[t] = nm

    # hand subtree (hand + every descendant, i.e. the fingers)
    for side, el, hd, sh in (('left', 'l_elbow', 'l_hand', 'l_shoulder'),
                             ('right', 'r_elbow', 'r_hand', 'r_shoulder')):
        e_s, h_s, s_s = slot_of[el], slot_of[hd], slot_of[sh]
        E, W = heads[e_s], heads[h_s]
        axis = W - E
        L = float(np.linalg.norm(axis))
        axis /= L
        rel = pos - E
        t = rel @ axis
        rad = np.linalg.norm(rel - np.outer(t, axis), axis=1)

        hand_ti = bi.real_indices[h_s]

        def in_hand_subtree(ti_idx):
            seen = set()
            while 0 <= ti_idx < ti.count and ti_idx not in seen:
                if ti_idx == hand_ti:
                    return True
                seen.add(ti_idx)
                ti_idx = ti.entries[ti_idx][1]
            return False

        subtree = {s for s, ti_idx in enumerate(bi.real_indices)
                   if in_hand_subtree(ti_idx)}
        wh = np.zeros(len(pos))
        for j in range(4):
            m = np.isin(bidx[:, j], list(subtree))
            wh[m] += wgt[m, j]
        wel = np.zeros(len(pos))
        for j in range(4):
            m = bidx[:, j] == e_s
            wel[m] += wgt[m, j]
        wsh = np.zeros(len(pos))
        for j in range(4):
            m = bidx[:, j] == s_s
            wsh[m] += wgt[m, j]

        near = (rad < 0.10) & (t > 0) & (t < L)
        print()
        print('=' * 104)
        print('%s  axis %s -> %s  L = %.1f mm   (t = 0 at the elbow head, t = L at the wrist joint)'
              % (side, el, hd, L * 1000))
        for label, mask in (('skin (shinano_body)', near & (vmat == 'shinano_body')),
                            ('glove (wristcuffs)', near & (vmat == 'glove_white01_wristcuffs')),
                            ('ornament (metal)', near & (vmat == 'ornament_metal_underwear')),
                            ('any material', near)):
            if mask.sum() == 0:
                continue
            print('  --- %s   %d vertices' % (label, int(mask.sum())))
            print('    %-16s %-6s %-8s %-8s %-8s %-8s' %
                  ('t (mm)', 'n', 'hand', 'elbow', 'shoulder', 'other'))
            lo, hi = 0.0, L
            edges = np.linspace(lo, hi, 16)
            for a, b in zip(edges[:-1], edges[1:]):
                sel = np.nonzero(mask & (t >= a) & (t < b))[0]
                if len(sel) == 0:
                    continue
                hh, ee, ss = wh[sel].mean(), wel[sel].mean(), wsh[sel].mean()
                print('    %6.0f..%-9.0f %-6d %-8.3f %-8.3f %-8.3f %-8.3f'
                      % (a * 1000, b * 1000, len(sel), hh, ee, ss,
                         max(0.0, 1.0 - hh - ee - ss)))
        # where does the rigid hand shell start?
        rigid = near & (wh > 0.95)
        if rigid.sum():
            print('  rigid (hand >= 0.95) vertices in the near set: %d, t = %.0f..%.0f mm,'
                  ' radius %.1f..%.1f mm'
                  % (int(rigid.sum()), t[rigid].min() * 1000, t[rigid].max() * 1000,
                     rad[rigid].min() * 1000, rad[rigid].max() * 1000))
    return 0


if __name__ == '__main__':
    sys.exit(main())
