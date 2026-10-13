# -*- coding: utf-8 -*-
"""READ-ONLY evidence probe for round 60 (forearm softness + material).

Sections
  [1] shader-variable names of our / the reference / a vanilla material
  [2] vertex ABI: our unit's streams vs the vanilla RE-2310 torso's streams
  [3] our unit's bone list (name, parent, world translation)
  [4] arm-region weight census: which bones dominate the arm, and what the
      forearm vertices blend with

Nothing is written; everything goes to stdout.
"""
import os
import struct
import sys

import numpy as np

import hd2_asset as A
import hd2_hash as H
import hd2_patch as P
import hd2_unit as U

HERE = os.path.dirname(os.path.abspath(__file__))
SDK = 'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
SHADERVARS = os.path.join(SDK, 'hashlists', 'shadervariables.txt')
BONEHASH = os.path.join(SDK, 'hashlists', 'bonehash.txt')

OURS = 'E:/mod/Lunalice-RE2310-Test/9ba626afa44a3aa3.patch_0'
REF = 'E:/mod/【4.28】Chiffon-LUNALICE替换RE-2310/9ba626afa44a3aa3.patch_0'
VANILLA_UNIT = 'E:/tmp/hd2-mgroot/re2310-full/unit/unit_0x9841b16544c8fbae.toc'
VANILLA_MAT = 'E:/tmp/hd2-mgroot/re2310-full/material/material_0x0b20ee2c1407bb72.toc'

OUR_UNITS = (0xcae35ee15168145f, 0x9841b16544c8fbae)


def load_shadervars():
    out = {}
    with open(SHADERVARS, encoding='utf-8', errors='replace') as f:
        for line in f:
            p = line.split()
            if len(p) >= 2 and p[1].startswith('0x'):
                out[int(p[1], 16)] = p[0]
    return out


def load_bones():
    return H.load_bonehash(BONEHASH)


SV = load_shadervars()
BONES = load_bones()


def dump_material_vars(m, label):
    print('  --- %s : parent=%s  ntex=%d nvar=%d' % (label, m.parent_name,
                                                     len(m.tex_ids), len(m.variables)))
    for v in m.variables:
        print('      %-34s klass=%-7s vals=%s'
              % (SV.get(v.id, '0x%08x' % v.id), v.klass_name,
                 ', '.join('%.6g' % x for x in v.values)))


def materials_of(path, ids=None):
    out = {}
    entries, raw = P.scan_entries(path)
    for e in entries:
        if e['type_id'] != A.MATERIAL_TYPE_ID:
            continue
        if ids and e['file_id'] not in ids:
            continue
        out[e['file_id']] = A.Material.read(
            raw[e['toc_offset']:e['toc_offset'] + e['toc_size']])
    return out


def our_unit():
    entries, raw = P.scan_entries(OURS)
    for e in entries:
        if e['type_id'] == U.UNIT_ID and e['file_id'] == OUR_UNITS[0]:
            pl = P.entry_payload(e, OURS)
            return U.Unit.read(pl['toc'], pl['gpu']), e
    raise LookupError('carried unit not found')


def describe_streams(u, label):
    print('  %s  version=0x%08x  %d streams' % (label, u.version, len(u.stream_infos)))
    for i, si in enumerate(u.stream_infos):
        comps = ', '.join('%s/%s@%d' % (U.TYPE_NAME.get(c.type, '?'),
                                        U.format_name(c.format, u.version), c.index)
                          for c in si.components)
        print('    s%d nv=%-7d ni=%-8d stride=%-3d ib=%-2d  [%s]'
              % (i, si.num_vertices, si.num_indices, si.vertex_stride,
                 si.index_buffer_type, comps))


def bone_table(u):
    """-> list of dicts (slot, ti_index, hash, name, parent_ti, parent_slot, t)

    The vertex `bone_index` values are *slots*; every slot maps to a TransformInfo
    node through `BoneInfo.real_indices`.
    """
    ti = u.transform_info
    bi = u.bone_infos[0]
    out = []
    slot_of_ti = {}
    for slot, ti_idx in enumerate(bi.real_indices):
        slot_of_ti[ti_idx] = slot
    for slot, ti_idx in enumerate(bi.real_indices):
        h = ti.hashes[ti_idx] if ti_idx < len(ti.hashes) else 0
        m = ti.matrices[ti_idx] if ti_idx < len(ti.matrices) else None
        pti = ti.entries[ti_idx][1] if ti_idx < len(ti.entries) else 0
        out.append(dict(slot=slot, ti=ti_idx, hash=h,
                        name=BONES.get(h, '0x%08x' % h),
                        parent_ti=pti, parent_slot=slot_of_ti.get(pti),
                        t=(m[12], m[13], m[14]) if m else (0, 0, 0)))
    return out


def main():
    u, entry = our_unit()

    print('=' * 100)
    print('[1] material shader variables (names from hashlists/shadervariables.txt)')
    ours = materials_of(OURS)
    print('  OUR patch: %d materials' % len(ours))
    if ours:
        dump_material_vars(next(iter(ours.values())), 'OURS (basic template)')
    ref = materials_of(REF)
    print('  REFERENCE patch: %d materials' % len(ref))
    for i, m in enumerate(list(ref.values())[:1]):
        dump_material_vars(m, 'REFERENCE[%d]' % i)
    vm = A.Material.read(open(VANILLA_MAT, 'rb').read())
    dump_material_vars(vm, 'VANILLA RE-2310 0x0b20ee2c1407bb72')

    print()
    print('=' * 100)
    print('[2] vertex ABI')
    describe_streams(u, 'OURS 0x%016x' % entry['file_id'])
    vu = U.Unit.read(open(VANILLA_UNIT, 'rb').read())
    describe_streams(vu, 'VANILLA RE-2310 0x9841b16544c8fbae')

    print()
    print('=' * 100)
    print('[3] our bone table (slot -> name, world translation in the idle frame)')
    bt = bone_table(u)
    print('  %d bone slots, %d transform nodes' % (len(bt), u.transform_info.count))
    for b in bt:
        print('    slot %2d ti %3d parent %3s  %-32s t=(%9.5f,%9.5f,%9.5f)'
              % (b['slot'], b['ti'], b['parent_ti'], b['name'], *b['t']))

    print()
    print('=' * 100)
    print('[4] arm weight census')
    print('  bone_infos: %d, each with %s bones'
          % (len(u.bone_infos), [len(b.real_indices) for b in u.bone_infos]))
    verts = u.decode_vertices(0)          # stream 0 = the LOD stream
    keys = sorted(verts[0].keys())
    print('  vertex keys: %s' % keys)
    pos = np.asarray([v['position'] for v in verts], np.float64)
    bidx = np.asarray([v['bone_index0'] for v in verts], np.int32)
    wgt = np.asarray([v['bone_weight'] for v in verts], np.float64)
    print('  %d vertices decoded, stride %d' % (len(pos), u.stream_infos[0].vertex_stride))
    name_of = {b['slot']: b['name'] for b in bt}

    dom = bidx[np.arange(len(bidx)), np.argmax(wgt, axis=1)]
    uniq, cnt = np.unique(dom, return_counts=True)
    print('  dominant-bone histogram (only bones with >=100 vertices):')
    for s, c in sorted(zip(uniq, cnt), key=lambda x: -x[1]):
        if c < 100:
            continue
        sel = dom == s
        p = pos[sel]
        print('    slot %2d %-32s n=%-6d  bbox x[%7.3f,%7.3f] y[%7.3f,%7.3f] '
              'z[%7.3f,%7.3f]'
              % (s, name_of.get(s, '?'), c, p[:, 0].min(), p[:, 0].max(),
                 p[:, 1].min(), p[:, 1].max(), p[:, 2].min(), p[:, 2].max()))

    used = np.unique(bidx[wgt > 0])
    print('  bones that carry any weight: %d' % len(used))

    print()
    print('  blend partners of each arm bone (weights averaged over the vertices '
          'it dominates):')
    for s, name in sorted(name_of.items(), key=lambda kv: kv[1]):
        if not any(k in name.lower() for k in
                   ('shoulder', 'arm', 'elbow', 'hand', 'wrist', 'clavicle',
                    'forearm', 'palm', 'finger')):
            continue
        sel = dom == s
        n = int(sel.sum())
        if n == 0:
            continue
        acc = {}
        for bi_row, w_row in zip(bidx[sel], wgt[sel]):
            for b_i, w in zip(bi_row, w_row):
                if w > 1e-6:
                    acc[b_i] = acc.get(b_i, 0.0) + w
        parts = ', '.join('%s=%.3f' % (name_of.get(k, 'slot%d' % k), v / n)
                          for k, v in sorted(acc.items(), key=lambda x: -x[1]))
        p = pos[sel]
        print('    %-32s n=%-6d  %s' % (name, n, parts))
        print('        extent x[%7.3f,%7.3f] y[%7.3f,%7.3f] z[%7.3f,%7.3f]'
              % (p[:, 0].min(), p[:, 0].max(), p[:, 1].min(), p[:, 1].max(),
                 p[:, 2].min(), p[:, 2].max()))
    return 0


if __name__ == '__main__':
    sys.exit(main())
