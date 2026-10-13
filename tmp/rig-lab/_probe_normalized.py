# -*- coding: utf-8 -*-
"""Read-only audit of an armor-normalize / cross-armor output patch.

For every Unit entry it reports the version, stream/mesh/bone/material counts,
the total vertex count and the per-mesh LOD layout, then classifies the unit as
renderable or placeholder.  Nothing is written.

Usage:
    python _probe_normalized.py <patch_0>
"""
import sys
import os

sys.path.insert(0, '.')
import hd2_patch as HP   # noqa: E402
import hd2_unit as U     # noqa: E402


def main():
    path = sys.argv[1]
    gpu_path = path + '.gpu_resources'
    entries, _raw = HP.scan_entries(path)
    by_type = {}
    for e in entries:
        by_type.setdefault(e['type_id'], []).append(e)

    print('patch    : %s (%d B)' % (path, os.path.getsize(path)))
    if os.path.exists(gpu_path):
        print('gpu      : %s (%d B)' % (gpu_path, os.path.getsize(gpu_path)))
    print('entries  : %d' % len(entries))
    for tid, group in sorted(by_type.items(), key=lambda kv: -len(kv[1])):
        print('  %-22s %d' % (HP.TYPE_NAMES.get(tid, '0x%x' % tid), len(group)))
    print()

    hdr = '%-20s %-10s %5s %5s %5s %5s %7s  %s'
    print(hdr % ('unit_file_id', 'version', 'mesh', 'strm', 'bone', 'mat', 'verts', 'lod layout'))
    real, hidden = [], []
    for e in sorted(by_type.get(HP.UNIT_TYPE_ID, []), key=lambda x: x['file_id']):
        pl = HP.entry_payload(e, path)
        try:
            u = U.Unit.read(pl['toc'], pl['gpu'])
        except Exception as exc:                      # noqa: BLE001
            print('0x%016x  READ FAILED: %s' % (e['file_id'], exc))
            continue
        verts = sum(si.num_vertices for si in u.stream_infos)
        layout = ','.join('lod%d:s%d' % (m.lod_index, m.stream_index) for m in u.mesh_infos)
        tag = 'REAL' if verts > 1000 else 'hidden'
        (real if tag == 'REAL' else hidden).append((e['file_id'], verts))
        print('%-20s %-10d %5d %5d %5d %5d %7d  %s  [%s]' %
              ('0x%016x' % e['file_id'], u.version, len(u.mesh_infos),
               len(u.stream_infos), len(u.bone_infos), len(u.material_ids),
               verts, layout, tag))
    print()
    print('renderable : %d' % len(real))
    for fid, verts in real:
        print('   0x%016x  verts=%d' % (fid, verts))
    print('placeholder: %d' % len(hidden))


if __name__ == '__main__':
    main()
