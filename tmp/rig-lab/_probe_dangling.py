# -*- coding: utf-8 -*-
"""Is the CLI's dangling material short id harmless?

`_verify_normalized.py` reports one FAIL for every armour-normalize output: the
hidden placeholder units reference a material short id that their own
`section_ids` table does not define.  This probe answers the only question that
matters -- do those sections ever draw anything?

usage: _probe_dangling.py <patch_0> [<patch_0> ...]
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_patch as HP
import hd2_unit as U


def report(path):
    entries, _ = HP.scan_entries(path)
    print('== %s' % path)
    for e in entries:
        if e['type_id'] != U.UNIT_ID:
            continue
        pl = HP.entry_payload(e, path)
        u = U.Unit.read(pl['toc'], pl['gpu'])
        rows = []
        for mi in u.mesh_infos:
            for sec in mi.sections:
                if sec.material_index >= len(mi.material_ids):
                    continue
                slot = mi.material_ids[sec.material_index]
                if slot in u.section_ids:
                    continue
                rows.append((mi.lod_index, sec.material_index, slot,
                             sec.num_indices, sec.num_vertices))
        if not rows:
            continue
        print('   unit 0x%016x  %d dangling slot reference(s)' % (e['file_id'], len(rows)))
        for lod, mi_idx, slot, ni, nv in rows:
            print('      lod=%-3d section=%-3d short_id=0x%08x  num_indices=%d  '
                  'num_vertices=%d' % (lod, mi_idx, slot, ni, nv))


if __name__ == '__main__':
    for p in sys.argv[1:]:
        report(p)
