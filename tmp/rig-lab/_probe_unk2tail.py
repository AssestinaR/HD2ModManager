# -*- coding: utf-8 -*-
"""Read-only: dump every MeshInfo.unk2 (32 B) of the structure donor verbatim.

We are about to stop copying `unk2` wholesale and instead write the seven
leading floats ourselves (min.xyz, max.xyz, radius).  The last four bytes are
still unidentified, so we keep them from the donor -- this probe records what
they actually are, per mesh, so the build can preserve them without guessing.

usage: _probe_unk2tail.py
"""
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_patch as P
import hd2_unit as U

DONOR_PATCH = r'E:\mod\【4.28】Chiffon-LUNALICE替换RE-2310\9ba626afa44a3aa3.patch_0'
DONOR_UNIT = 0x9841B16544C8FBAE
CARRIER = r'E:\tmp\hd2-mgroot\orig\unit_0xa1a7563e6ac8b8f8.toc'


def load(path, key=None):
    if key is None:
        return U.Unit.read(open(path, 'rb').read(), b'')
    entries, _ = P.scan_entries(path)
    for e in entries:
        if e['file_id'] == key and e['type_id'] == U.UNIT_ID:
            return U.Unit.read(P.entry_payload(e, path)['toc'], b'')
    raise SystemExit('unit not found')


def dump(u, label):
    print('=' * 78)
    print(label)
    for i, mi in enumerate(u.mesh_infos):
        head = struct.unpack_from('<7f', mi.unk2, 0)
        tail = mi.unk2[28:32]
        print('  mesh %d lod=%-3d stream=%d unk2=%s | tail=%s  (u32=%d, f32=%r)'
              % (i, mi.lod_index, mi.stream_index,
                 ' '.join('%.6f' % x for x in head), tail.hex(' '),
                 struct.unpack('<I', tail)[0], struct.unpack('<f', tail)[0]))


def main():
    dump(load(DONOR_PATCH, DONOR_UNIT), 'DONOR 0x9841b16544c8fbae')
    dump(load(CARRIER), 'CARRIER 0xa1a7563e6ac8b8f8')


if __name__ == '__main__':
    main()
