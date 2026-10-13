# -*- coding: utf-8 -*-
"""READ-ONLY material comparison probe (round 60).

Dumps the Material assets of

  * our deployed patch            (E:/mod/Lunalice-RE2310-Test/...patch_0)
  * the working reference patch   (E:/mod/<4.28 Chiffon>/...patch_0)
  * the vanilla RE-2310 materials (extracted tocs)
  * the SDK templates             (docs/.../materials/*.material)

so the round-60 "everything is black / metallic" report can be pinned to a
concrete difference.  Nothing is written outside stdout.
"""
import os
import struct
import sys

import hd2_asset as A
import hd2_patch as P

HERE = os.path.dirname(os.path.abspath(__file__))
SDK = 'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'

OURS = 'E:/mod/Lunalice-RE2310-Test/9ba626afa44a3aa3.patch_0'
REF = 'E:/mod/【4.28】Chiffon-LUNALICE替换RE-2310/9ba626afa44a3aa3.patch_0'
VANILLA_DIR = 'E:/tmp/hd2-mgroot/re2310-full/material'
VANILLA_DS10 = 'E:/tmp/hd2-mgroot/ds10/material'


def describe(m, label):
    nvar = len(m.variables)
    print('  %-28s parent=%-12s  size=%-5d  ntex=%-2d nvar=%-2d varDataSize=%d'
          % (label, m.parent_name, len(m.write()), len(m.tex_ids), nvar,
             m.variable_data_size))
    for i, (tu, tid) in enumerate(zip(m.tex_unks, m.tex_ids)):
        print('        tex[%d] unk=0x%08x id=0x%016x' % (i, tu, tid))
    for v in m.variables:
        vals = ', '.join('%.6g' % x for x in v.values)
        print('        var klass=%-7s elems=%-2d id=0x%08x off=%-4d stride=%-3d '
              'vals=[%s]' % (v.klass_name, v.elements, v.id, v.offset,
                             v.element_stride, vals))


def from_patch(path, title):
    print('=' * 100)
    print('%s\n  %s' % (title, path))
    entries, raw = P.scan_entries(path)
    n = 0
    for e in entries:
        if e['type_id'] != A.MATERIAL_TYPE_ID:
            continue
        n += 1
        toc = raw[e['toc_offset']:e['toc_offset'] + e['toc_size']]
        m = A.Material.read(toc)
        describe(m, '0x%016x' % e['file_id'])
    if n == 0:
        print('  (no Material entries)')
    return n


def from_dir(path, title):
    print('=' * 100)
    print('%s\n  %s' % (title, path))
    if not os.path.isdir(path):
        print('  (missing)')
        return
    for f in sorted(os.listdir(path)):
        if not f.endswith('.toc'):
            continue
        m = A.Material.read(open(os.path.join(path, f), 'rb').read())
        describe(m, f.replace('material_', '').replace('.toc', ''))


def from_template(name):
    print('=' * 100)
    print('SDK template %s' % name)
    p = os.path.join(SDK, 'materials', name)
    m = A.Material.read(open(p, 'rb').read())
    describe(m, os.path.basename(name))


def main():
    from_patch(OURS, 'OURS (deployed)')
    from_patch(REF, 'REFERENCE mod (renders correctly in game)')
    from_dir(VANILLA_DIR, 'VANILLA RE-2310 materials')
    from_dir(VANILLA_DS10, 'VANILLA DS-10 materials')
    for t in ('basic.material', 'armorlut.material', 'original.material',
              'basic+.material', 'advanced.material'):
        from_template(t)
    return 0


if __name__ == '__main__':
    sys.exit(main())
