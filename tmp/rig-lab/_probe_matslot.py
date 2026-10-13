"""Resolve the chain: mesh.sections[i].material_index -> mesh.material_ids[i]
-> unit.section_ids[k] -> unit.material_ids[k] (the Material asset FileID).

Also prints, per unit, the per-mesh material_ids so the packager knows what a
"section short id" really is.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_patch as HP
import hd2_unit as HU

import glob
cand = glob.glob(r'E:\mod\*\9ba626afa44a3aa3.patch_0')
REF = [c for c in cand if 'Chiffon' in c or '4.28' in c][0]


def main():
    entries, _ = HP.scan_entries(REF)
    units = [e for e in entries if e['type_id'] == HP.UNIT_TYPE_ID]
    for e in units:
        pay = HP.entry_payload(e, REF)
        u = HU.Unit.read(pay['toc'], pay['gpu'])
        print('=' * 96)
        print('0x%016x  nMat=%d  nMesh=%d' % (e['file_id'], len(u.material_ids), len(u.mesh_infos)))
        print('  unit secids  %s' % ['0x%x' % x for x in u.section_ids])
        print('  unit matids  %s' % ['0x%x' % x for x in u.material_ids])
        for mi, m in enumerate(u.mesh_infos):
            print('  m%d lod=%-3d nmat=%d matids=%s'
                  % (mi, m.lod_index, m.num_materials,
                     ['0x%x' % x for x in m.material_ids]))
            for s in m.sections:
                sid = m.material_ids[s.material_index] if s.material_index < len(m.material_ids) else None
                k = u.section_ids.index(sid) if (sid is not None and sid in u.section_ids) else -1
                mid = u.material_ids[k] if k >= 0 else None
                print('      sec matidx=%d -> short 0x%x -> unit slot %d -> material 0x%s  (voff=%d nv=%d ioff=%d ni=%d grp=%d)'
                      % (s.material_index, sid or 0, k,
                         ('%016x' % mid) if mid is not None else 'MISS',
                         s.vertex_offset, s.num_vertices, s.index_offset, s.num_indices, s.group_index))


if __name__ == '__main__':
    main()
