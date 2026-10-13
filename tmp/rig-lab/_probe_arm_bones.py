# -*- coding: utf-8 -*-
"""READ-ONLY: dump the arm bone chain of a carried unit -- names, parents, rest
heads, and how much skin weight each bone actually carries.

Motivation (round 68): the user reports a hard seam at the wrist under wrist
ROTATION (pronation), not under wrist bend.  HD2's bone name table contains
l_forearm_twist / r_forearm_twist / l_hand_twist / r_hand_twist, which we had
assumed did not exist.  If the game animates pronation on a twist bone that sits
BETWEEN l_elbow and l_hand, then a forearm bound purely to l_elbow cannot follow
it while the hand (bound to l_hand, a descendant) follows it fully -- exactly the
reported symptom.

Usage: python _probe_arm_bones.py <patch_0> [--unit 0x...]
"""
import sys

import numpy as np

import hd2_hash as H
import hd2_patch as P
import hd2_unit as U

SDK = 'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
BONES = H.load_bonehash(SDK + '/hashlists/bonehash.txt')
UNIT = 0xcae35ee15168145f

ARM_WORDS = ('shoulder', 'elbow', 'forearm', 'hand', 'wrist', 'twist', 'finger',
             'thumb', 'index', 'middle', 'ring', 'pinky')


def load(patch, unit):
    entries, _ = P.scan_entries(patch)
    e = next(x for x in entries if x['type_id'] == U.UNIT_ID and x['file_id'] == unit)
    pl = P.entry_payload(e, patch)
    return U.Unit.read(pl['toc'], pl['gpu'])


def main():
    argv = sys.argv[1:]
    unit = UNIT
    if '--unit' in argv:
        k = argv.index('--unit')
        unit = int(argv[k + 1], 16)
        argv = argv[:k] + argv[k + 2:]
    patch = argv[0]

    u = load(patch, unit)
    ti, bi = u.transform_info, u.bone_infos[0]

    slot_of_ti = {}
    names, heads, parent_ti = {}, {}, {}
    for slot, ti_idx in enumerate(bi.real_indices):
        slot_of_ti[ti_idx] = slot
        names[slot] = BONES.get(ti.hashes[ti_idx], '0x%08x' % ti.hashes[ti_idx])
        m = ti.matrices[ti_idx]
        heads[slot] = np.array([m[12], m[13], m[14]], np.float64)
        parent_ti[slot] = int(ti.entries[ti_idx][1])

    # --- weight mass per palette slot, summed over every model stream
    streams = [i for i, s in enumerate(u.stream_infos)
               if s.num_vertices > 1000
               and any(c.type == 7 for c in s.components)
               and any(c.type == 6 for c in s.components)]
    mass = np.zeros(len(bi.real_indices))
    nverts = 0
    for si in streams:
        verts = u.decode_vertices(si)
        nverts += len(verts)
        for v in verts:
            for j in range(4):
                w = float(v['bone_weight'][j])
                if w > 0.0:
                    b = int(v['bone_index0'][j])
                    if 0 <= b < len(mass):
                        mass[b] += w

    print('patch  %s' % patch)
    print('unit   0x%016x   %d slots   %d model streams   %d vertices'
          % (unit, len(bi.real_indices), len(streams), nverts))
    print()

    def is_arm(nm):
        low = nm.lower()
        return any(w in low for w in ARM_WORDS)

    def chain(slot):
        out, seen = [], set()
        cur = slot
        while cur is not None and cur not in seen:
            seen.add(cur)
            out.append(cur)
            p = parent_ti[cur]
            cur = slot_of_ti.get(p) if p >= 0 else None
        return out

    # roots of arm chains: arm bones whose parent is NOT an arm bone
    roots = []
    for slot in range(len(bi.real_indices)):
        nm = names[slot]
        if nm.startswith('0x') or not is_arm(nm):
            continue
        p = parent_ti[slot]
        pnm = names[slot_of_ti[p]] if p >= 0 and p in slot_of_ti else ''
        if not (pnm and is_arm(pnm)):
            roots.append(slot)

    for root in sorted(roots, key=lambda s: names[s]):
        ch = chain(root)
        print('chain  %s' % ' <- '.join(names[s] for s in ch))
        for depth, s in enumerate(ch):
            m = heads[s]
            print('   %s%-22s slot %3d  head (%8.4f,%8.4f,%8.4f)  weight %10.2f'
                  % ('  ' * depth, names[s], s, m[0], m[1], m[2], mass[s]))
        print()

    print('=== full subtree under each shoulder (descendants only) ===')
    for root_name in ('l_shoulder', 'r_shoulder'):
        root = None
        for slot in range(len(bi.real_indices)):
            if names[slot] == root_name:
                root = slot
                break
        if root is None:
            continue
        print('subtree of %s (slot %d)' % (root_name, root))

        def walk(slot, depth):
            for k in range(len(bi.real_indices)):
                if parent_ti[k] == bi.real_indices[slot]:
                    m = heads[k]
                    print('   %s%-24s slot %3d  head (%8.4f,%8.4f,%8.4f)  w %10.2f'
                          % ('  ' * depth, names[k], k, m[0], m[1], m[2], mass[k]))
                    walk(k, depth + 1)

        walk(root, 1)
        print()

    print('=== every bone whose name contains twist/wrist/forearm ===')
    for slot in range(len(bi.real_indices)):
        nm = names[slot]
        low = nm.lower()
        if any(w in low for w in ('twist', 'wrist', 'forearm')):
            p = parent_ti[slot]
            pnm = names[slot_of_ti[p]] if p >= 0 and p in slot_of_ti else '<root>'
            kids = [names[k] for k in range(len(bi.real_indices))
                    if parent_ti[k] == bi.real_indices[slot]]
            print('  %-22s slot %3d  parent %-18s weight %10.2f  children %s'
                  % (nm, slot, pnm, mass[slot], kids))
    return 0


if __name__ == '__main__':
    sys.exit(main())
