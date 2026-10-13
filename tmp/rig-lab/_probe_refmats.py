# -*- coding: utf-8 -*-
"""The reference patch's Material + Texture entries, decoded, so the packager has
a byte-level template for both.

usage: _probe_refmats.py
"""
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_asset
import hd2_patch

PATCH = r'E:\mod\【4.28】Chiffon-LUNALICE替换RE-2310\9ba626afa44a3aa3.patch_0'
SHADERVARS = ('../../docs/HD2SDK-CommunityEdition-3.8.0/hashlists/shadervariables.txt')

names = {}
for line in open(SHADERVARS, encoding='utf-8', errors='replace'):
    p = line.split()
    if len(p) >= 2:
        try:
            names[int(p[1], 16)] = p[0]
        except ValueError:
            pass

entries, _raw = hd2_patch.scan_entries(PATCH)

print('== Material entries ==')
for e in entries:
    if e['type_id'] != hd2_asset.MATERIAL_TYPE_ID:
        continue
    pl = hd2_patch.entry_payload(e, PATCH)
    m = hd2_asset.Material.read(pl['toc'])
    print('\n  0x%016x  toc %d B  gpu %d B  stream %d B'
          % (e['file_id'], len(pl['toc']), len(pl['gpu']), len(pl['stream'])))
    print('    undat1 %s  EndOffset %d  undat2 %d  parent 0x%016x (%s)'
          % (m.undat1.hex(' '), m.end_offset, m.undat2, m.parent_material_id,
             m.parent_name))
    print('    undat3 %s' % m.undat3.hex(' '))
    print('    undat4 %s' % m.undat4.hex(' '))
    print('    undat5 %s' % m.undat5.hex(' '))
    print('    undat6 %s' % m.undat6.hex(' '))
    print('    varDataSize %d   remaining %d B   recomputed header %d'
          % (m.variable_data_size, len(m.remaining),
             12 + 4 + 8 + 8 + 32 + 4 + 36 + 4 + 12 + 4 + 12
             + 12 * len(m.tex_ids) + 20 * len(m.variables)))
    print('    textures:')
    for tu, tid in zip(m.tex_unks, m.tex_ids):
        print('      unk %-10d id 0x%016x' % (tu, tid))
    print('    variables:')
    for v in m.variables:
        print('      %-28s klass %-8s elems %-3d id 0x%08x off %-5d stride %-4d vals %s'
              % (names.get(v.id, '?'), v.klass_name, v.elements, v.id, v.offset,
                 v.element_stride, ['%.6g' % x for x in v.values]))

print('\n== Texture entries ==')
for e in entries:
    if e['type_id'] != hd2_asset.TEXTURE_TYPE_ID:
        continue
    pl = hd2_patch.entry_payload(e, PATCH)
    t = hd2_asset.Texture.read(pl['toc'], pl['gpu'])
    print('  0x%016x  toc %d  gpu %-10d  %dx%d mips %d  %-16s array %d  unkID 0x%08x'
          % (e['file_id'], len(pl['toc']), len(pl['gpu']), t.width, t.height,
             t.mip_count, t.format_name, t.array_size, t.unk_id))
    print('        dds flags 0x%08x  pitch %d  caps 0x%08x  caps2 0x%08x  misc2 0x%08x'
          % (struct.unpack_from('<I', t.dds_header, 8)[0],
             struct.unpack_from('<I', t.dds_header, 20)[0],
             struct.unpack_from('<I', t.dds_header, 108)[0],
             struct.unpack_from('<I', t.dds_header, 112)[0],
             struct.unpack_from('<I', t.dds_header, 144)[0]))
    print('        mipmap_info %s' % (t.mipmap_info[:4],))
    print('        header[0:16] %s' % bytes(t.dds_header[0:16]).hex(' '))
    print('        gpu bytes = W*H*block? %d'
          % (max(1, (t.width + 3) // 4) * max(1, (t.height + 3) // 4)
             * hd2_asset.BC_BLOCK_BYTES.get(t.dxgi_format, 0)))

print('\n== unit -> material id -> which are in the patch? ==')
patch_mats = {e['file_id'] for e in entries if e['type_id'] == hd2_asset.MATERIAL_TYPE_ID}
patch_texs = {e['file_id'] for e in entries if e['type_id'] == hd2_asset.TEXTURE_TYPE_ID}
used = set()
for e in entries:
    if e['type_id'] != 0xE0A48D0BE9A7453F:
        continue
    toc = hd2_patch.entry_payload(e, PATCH)['toc']
    off, = struct.unpack_from('<I', toc, 0x70)
    n, = struct.unpack_from('<I', toc, off)
    ids = [struct.unpack_from('<Q', toc, off + 4 + 4 * n + 8 * i)[0] for i in range(n)]
    used |= set(ids)
for mid in sorted(used):
    print('  material 0x%016x  %s' % (mid, 'IN PATCH' if mid in patch_mats else 'from base game'))
