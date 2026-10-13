# -*- coding: utf-8 -*-
"""Enumerate materials: parent class, slot keys, texture ids, shader variables.

Works on a patch (all Material entries) or on loose .material template files.

usage:
  _probe_alpha_mat.py patch  <patch_0> [<patch_0> ...]
  _probe_alpha_mat.py sdk
"""
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_asset as A
import hd2_patch as P

SDK = Path(r'E:\Data\source\repos\WpfApp1\docs\HD2SDK-CommunityEdition-3.8.0\materials')


def dump_material(tag, fid, m):
    print('-' * 78)
    print('%s  id=%s  parent=%s (0x%016x)  ntex=%d nvar=%d'
          % (tag, ('0x%016x' % fid) if fid else '-', m.parent_name,
             m.parent_material_id, len(m.tex_ids), len(m.variables)))
    for i, (k, t) in enumerate(zip(m.tex_unks, m.tex_ids)):
        print('    slot %2d  key %08x  tex %s' % (i, k, ('0x%016x' % t) if t else '0'))
    for v in m.variables:
        print('    var 0x%08x %-7s n=%d stride=%d off=%d  %s'
              % (v.id, v.klass_name, v.elements, v.element_stride, v.offset,
                 ['%.6g' % x for x in v.values]))


def do_patch(path):
    print('=' * 78)
    print('PATCH %s' % path)
    entries, _ = P.scan_entries(path)
    nm = 0
    for e in entries:
        if e['type_id'] != A.MATERIAL_TYPE_ID:
            continue
        m = A.Material.read(P.entry_payload(e, path)['toc'])
        dump_material('material', e['file_id'], m)
        nm += 1
    print('  -> %d materials' % nm)


def do_sdk():
    for f in sorted(os.listdir(SDK)):
        if not f.endswith('.material'):
            continue
        data = open(os.path.join(SDK, f), 'rb').read()
        try:
            m = A.Material.read(data)
        except Exception as ex:
            print('%-24s READ FAILED %s' % (f, ex))
            continue
        dump_material(f, 0, m)


def main():
    if sys.argv[1] == 'sdk':
        do_sdk()
    else:
        for p in sys.argv[2:]:
            do_patch(p)
    return 0


if __name__ == '__main__':
    sys.exit(main())
