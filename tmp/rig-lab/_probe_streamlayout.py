# -*- coding: utf-8 -*-
"""READ-ONLY: print the vertex component layout of a unit's streams.

Needed before any in-place weight edit: the bone_weight / bone_index fields
must be located by the stream's own component table, not by assumption.

Usage: python _probe_streamlayout.py [patch_0] [unit_hex]
"""
import os
import sys

import hd2_unit as U
import hd2_patch as P

DEFAULT_PATCH = 'E:/mod/Lunalice-RE2310-M1/9ba626afa44a3aa3.patch_0'
DEFAULT_UNIT = 0xcae35ee15168145f

TYPE_NAME = {0: 'position', 1: 'normal', 4: 'uv', 5: 'color', 6: 'bone_index',
             7: 'bone_weight', 8: 'blend_weight', 9: 'blend_index'}


def main():
    patch = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_PATCH
    unit_id = int(sys.argv[2], 16) if len(sys.argv) > 2 else DEFAULT_UNIT
    entries, _ = P.scan_entries(patch)
    e = next(x for x in entries
             if x['type_id'] == U.UNIT_ID and x['file_id'] == unit_id)
    pl = P.entry_payload(e, patch)
    u = U.Unit.read(pl['toc'], pl['gpu'])
    print('unit 0x%016x  version %d  %d streams  %d meshes  gpu %d B'
          % (unit_id, u.version, len(u.stream_infos), len(u.mesh_infos), len(pl['gpu'])))
    for si, s in enumerate(u.stream_infos):
        print()
        print('  stream %d: %d vertices  %d indices  stride %d  ibtype %d'
              % (si, s.num_vertices, s.num_indices, s.vertex_stride,
                 s.index_buffer_type))
        off = 0
        for c in s.components:
            nm = TYPE_NAME.get(c.type, 'type%d' % c.type)
            key = nm if nm != 'uv' else 'uv%d' % c.index
            size = U.component_size(c.format, u.version)
            print('    +%-3d %-14s format=%-4d index=%d  size=%d'
                  % (off, key, c.format, c.index, size))
            off += size
        print('    total %d (stride %d)' % (off, s.vertex_stride))
    for mi, m in enumerate(u.mesh_infos):
        print('  mesh %d: lod=%d stream=%d MeshID=0x%08x TF=%d sections=%d'
              % (mi, m.lod_index, m.stream_index, m.mesh_id, m.transform_index,
                 m.num_sections))
    return 0


if __name__ == '__main__':
    sys.exit(main())
