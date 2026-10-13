# -*- coding: utf-8 -*-
"""Section -> material resolution for a unit: which material does each mesh
section actually use, and what is that material's parent class?

usage: _probe_unitmats.py <patch_0> <unit_hex>
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_asset as A
import hd2_patch as P
import hd2_unit as U


def main():
    patch = sys.argv[1]
    unit = int(sys.argv[2], 16)
    entries, _ = P.scan_entries(patch)

    mat_parent, mat_slots = {}, {}
    for e in entries:
        if e['type_id'] != A.MATERIAL_TYPE_ID:
            continue
        m = A.Material.read(P.entry_payload(e, patch)['toc'])
        mat_parent[e['file_id']] = (m.parent_name, len(m.tex_ids))
        mat_slots[e['file_id']] = m

    e = next(x for x in entries if x['type_id'] == U.UNIT_ID and x['file_id'] == unit)
    u = U.Unit.read(P.entry_payload(e, patch)['toc'], P.entry_payload(e, patch)['gpu'])

    print('unit 0x%016x : %d mesh_info(s), unit material table %d entries'
          % (unit, len(u.mesh_infos), len(u.material_ids)))
    short_to_full = {}
    for sid, mid in zip(u.section_ids, u.material_ids):
        short_to_full[sid] = mid
        p = mat_parent.get(mid)
        print('   unit material short %08x -> 0x%016x  parent=%s ntex=%s'
              % (sid, mid, p[0] if p else 'NOT IN PATCH', p[1] if p else '-'))

    for mi in u.mesh_infos:
        if mi.lod_index != 0:
            continue
        print('-' * 78)
        print('LOD0 mesh_id=0x%08x  mesh_info.material_ids=%s'
              % (mi.mesh_id, ['%08x' % x for x in mi.material_ids]))
        for si, sec in enumerate(mi.sections):
            if sec.num_indices == 0:
                continue
            short = mi.material_ids[sec.material_index] if sec.material_index < len(mi.material_ids) else None
            full = short_to_full.get(short) if short is not None else None
            p = mat_parent.get(full) if full else None
            print('   sec %2d  tris %6d  mat_idx %d  short %s  -> 0x%016x  parent=%s'
                  % (si, sec.num_indices // 3, sec.material_index,
                     ('%08x' % short) if short is not None else '-',
                     full or 0, p[0] if p else 'NOT IN PATCH'))
    return 0


if __name__ == '__main__':
    sys.exit(main())
