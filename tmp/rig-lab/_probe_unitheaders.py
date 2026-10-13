# -*- coding: utf-8 -*-
"""Dump the per-unit header fields + customisation blob for the 9 visible units.

If we want the model to show up **without** the Lua registry we have to write our
unit under the *original game* unit ids (the manifest's `oldUnit` values).  That
only works if the per-slot header fields we inherit from the torso donor are
harmless for the other slots -- so look at what the other eight actually carry.
"""
import json
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_patch as HP
import hd2_unit as U

REF_DIR = r'E:\mod\Lunalice-Visible-Virtual-Model'
REF = os.path.join(REF_DIR, '9ba626afa44a3aa3.patch_0')
MAN = os.path.join(REF_DIR, 'lunalice-visible-model.manifest.json')

man = json.load(open(MAN, encoding='utf-8'))
pieces = man['model']['pieces']

entries, _ = HP.scan_entries(REF)
by_id = {e['file_id']: e for e in entries if e['type_id'] == U.UNIT_ID}


def show(raw, tag):
    """customization_data: u32 x4 header then 4 groups of (len, count, len, str)."""
    try:
        n = struct.unpack_from('<4I', raw, 0)
    except struct.error:
        print('    %-6s <too short>' % tag)
        return
    p = 16
    strings = []
    for _ in range(4):
        try:
            a, b, ln = struct.unpack_from('<3I', raw, p)
        except struct.error:
            break
        p += 12
        s = raw[p:p + ln]
        p += (ln + 3) // 4 * 4
        strings.append(s.rstrip(b'\x00').decode('latin1'))
    print('    %-6s hdr=%s  %s' % (tag, n, ' | '.join(strings)))


for pc in pieces:
    old = int(pc['oldUnit'], 16)
    new = int(pc['unit'], 16)
    e = by_id.get(new)
    if e is None:
        print('%-6s %-18s -> 0x%016x  MISSING' % (pc['part'], pc['body'], new))
        continue
    pl = HP.entry_payload(e, REF)
    u = U.Unit.read(pl['toc'], pl['gpu'])
    print('%-8s %-7s slot=%d ptype=%d  old=0x%016x new=0x%016x'
          % (pc['part'], pc['body'], pc['slot'], pc['pieceType'], old, new))
    print('    header_data1=%d version=%d streams=%d meshes=%d materials=%d'
          % (u.header_data1, u.version, len(u.stream_infos), len(u.mesh_infos),
             len(u.material_ids)))
    print('    lodgroup=%dB conn=%dB cust=%dB unkhdr1=%dB prelight=%dB unrev2=%dB'
          % (len(u.lod_group_list_data), len(u.connecting_bone_data),
             len(u.customization_data), len(u.unk_header_data1),
             len(u.pre_light_list_data), len(u.unreversed_data2)))
    show(u.customization_data, 'cust')
    print('    tf count=%d  bones=%d  section_ids=%s'
          % (u.transform_info.count, u.bone_infos[0].num_bones,
             ['0x%x' % x for x in u.section_ids]))
