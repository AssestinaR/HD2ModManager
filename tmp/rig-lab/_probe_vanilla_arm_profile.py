# -*- coding: utf-8 -*-
"""READ-ONLY: vanilla HD2 forearm weight profile along t/L.

Ground truth for the mechanism found in _probe_forearm_weights.py: our shipped
patch gives l_hand_twist a total weight mass of exactly 0.0 while l_hand carries
2113.  The expert's earlier decode of vanilla left-arm skin 0x586e33e020cbf80f
already showed l_hand_twist=160.23 out of ~644, i.e. vanilla *does* use the twist
helper.  This probe prints the same t/L profile for a vanilla arm unit so we can
see WHERE vanilla puts the twist weight.

Vertex layout of the vanilla arm unit (version 10800438, stride 60):
  position vec3_float, normal u32, uv0..2 vec2_half, bone_weight vec4_half,
  bone_index0/1/2 vec4_uint8  (the three index groups are byte-identical filler;
  bone_index0 is the real one).  Raw indices are material-local and must be run
  through bi.remaps[material].

Usage:
  _probe_vanilla_arm_profile.py E:/tmp/round68-van-arm/unit_0x4a213e0ae8acf74b
"""
import argparse
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_hash as H
import hd2_unit as U

SDK = 'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('base', help='path prefix, with .toc / .gpu_resources next to it')
    ap.add_argument('--radius', type=float, default=0.30)
    args = ap.parse_args()

    toc = open(args.base + '.toc', 'rb').read()
    gpu = open(args.base + '.gpu_resources', 'rb').read()
    u = U.Unit.read(toc, gpu)
    bones = H.load_bonehash(os.path.join(SDK, 'hashlists', 'bonehash.txt'))
    ti, bi = u.transform_info, u.bone_infos[0]
    names, heads = {}, {}
    for slot, ti_idx in enumerate(bi.real_indices):
        names[slot] = bones.get(ti.hashes[ti_idx], '0x%08x' % ti.hashes[ti_idx])
        m = ti.matrices[ti_idx]
        heads[slot] = np.array([m[12], m[13], m[14]], float)
    slot_of = {v: k for k, v in names.items() if not v.startswith('0x')}
    print('unit %s: %d slots, remaps %s' % (os.path.basename(args.base), len(names),
                                            [len(r) for r in bi.remaps]))

    for mi in u.mesh_infos:
        if mi.lod_index != 0:
            continue
        verts = u.decode_vertices(mi.stream_index)
        n = len(verts)
        bidx = np.asarray([v['bone_index0'] for v in verts], np.int64)
        wgt = np.asarray([v['bone_weight'] for v in verts], np.float64)
        dup = all(np.array_equal(v['bone_index0'], v['bone_index1'])
                  and np.array_equal(v['bone_index0'], v['bone_index2']) for v in verts[:500])
        print('lod0 stream=%d verts=%d  index groups identical: %s' % (mi.stream_index, n, dup))

        mat_of = np.full(n, -1, np.int32)
        indices = np.asarray(u.index_list(mi.stream_index), np.int64)
        for s in mi.sections:
            sv = np.unique(indices[s.index_offset:s.index_offset + s.num_indices])
            mat_of[sv] = s.material_index
        live = mat_of >= 0
        print('  LOD0 references %d of %d vertices in this stream' % (int(live.sum()), n))
        decoded = bidx.copy()
        for material in np.unique(mat_of[live]):
            which = mat_of == material
            remap = np.asarray(bi.remaps[material], np.int64)
            decoded[which] = remap[np.minimum(bidx[which], len(remap) - 1)]

        M = np.asarray(ti.matrices[mi.transform_index], np.float64).reshape(4, 4)
        pl = np.asarray([v['position'] for v in verts], np.float64)
        pos = pl @ M[:3, :3].T + M[3, :3]
        print('  mesh transform: rot identity=%s  translation=%s'
              % (np.allclose(M[:3, :3], np.eye(3), atol=1e-6), np.round(M[3, :3], 5)))
        mass = np.zeros(max(len(names), int(decoded.max()) + 1))
        for j in range(wgt.shape[1]):
            np.add.at(mass, decoded[live, j], wgt[live, j])
        print('  total weight per bone over LOD0 (top 16):')
        for slot in np.argsort(-mass)[:16]:
            if mass[slot] <= 0:
                continue
            print('    %3d  %-24s %9.2f' % (slot, names.get(int(slot), '?'), mass[slot]))

        for sd in ('l', 'r'):
            el, hd = sd + '_elbow', sd + '_hand'
            if el not in slot_of or hd not in slot_of:
                continue
            tw = slot_of.get(sd + '_hand_twist')
            stw = slot_of.get(sd + '_shoulder_twist')
            fingers = sorted(s for s, nm in names.items()
                             if nm.startswith(sd + '_') and 'finger' in nm)
            E, W = heads[slot_of[el]], heads[slot_of[hd]]
            axis = W - E
            L = float(np.linalg.norm(axis))
            axis = axis / L
            rel = pos - E
            t = rel @ axis
            rad = np.linalg.norm(rel - np.outer(t, axis), axis=1)

            def wm(slots):
                if not slots:
                    return np.zeros(n)
                return np.where(np.isin(decoded, list(slots)), wgt, 0.0).sum(axis=1)

            m_el, m_hd = wm([slot_of[el]]), wm([slot_of[hd]])
            m_tw, m_stw, m_fg = wm([tw]), wm([stw]), wm(fingers)
            print('\n  [%s] elbow->hand L=%.1f mm   radius gate %.0f mm'
                  % (sd, L * 1000, args.radius * 1000))
            print('     t/L      n     elbow      hand    handTw  shoulderTw    finger')
            for k in range(24):
                lo, hi = k * 0.05, (k + 1) * 0.05
                sel = live & (rad < args.radius) & (t / L >= lo) & (t / L < hi)
                if sel.sum() == 0:
                    continue
                print('     %5.2f %6d  %7.3f  %7.3f  %7.3f  %9.3f  %8.3f'
                      % (0.5 * (lo + hi), int(sel.sum()), m_el[sel].mean(), m_hd[sel].mean(),
                         m_tw[sel].mean(), m_stw[sel].mean(), m_fg[sel].mean()))
    return 0


if __name__ == '__main__':
    sys.exit(main())
