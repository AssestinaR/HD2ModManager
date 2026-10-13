# -*- coding: utf-8 -*-
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_asset as A
from hd2_hash import load_bonehash
SDK = os.path.join('..','..','docs','HD2SDK-CommunityEdition-3.8.0')
# shader variable names
shv = {}
for line in open(os.path.join(SDK,'hashlists','shadervariables.txt'), encoding='utf-8', errors='replace'):
    p = line.split()
    if len(p) >= 2:
        try: shv[int(p[1],16)] = p[0]
        except ValueError: pass
print('shader variables known: %d' % len(shv))
for f in ('original','basic','basic+','alphaclip','armorlut','advanced','emissive','translucent'):
    path = os.path.join(SDK,'materials', f + '.material')
    if not os.path.exists(path): continue
    d = open(path,'rb').read()
    m = A.Material.read(d)
    print('\n=== %s.material (%d B) ===' % (f, len(d)))
    print('  undat1 %s  end_offset %d  undat2 %d  parent 0x%x (%s)' % (m.undat1.hex(), m.end_offset, m.undat2, m.parent_material_id, m.parent_name))
    print('  undat3 %s' % m.undat3.hex())
    print('  undat4 %s' % m.undat4.hex())
    print('  undat5 %s  undat6 %s  varDataSize %d' % (m.undat5.hex(), m.undat6.hex(), m.variable_data_size))
    print('  textures %d:' % len(m.tex_ids))
    for i,(tu,tid) in enumerate(zip(m.tex_unks,m.tex_ids)):
        print('    [%d] unk %d id 0x%016x' % (i,tu,tid))
    print('  variables %d:' % len(m.variables))
    for v in m.variables:
        print('    %-24s klass %d (%s) elems %d id 0x%08x off %3d stride %d vals %s'
              % (shv.get(v.id, '?'), v.klass, v.klass_name, v.elements, v.id, v.offset, v.element_stride,
                 [round(x,4) for x in v.values]))
    print('  remaining %d B: %s' % (len(m.remaining), m.remaining.hex()))
    rt = A.Material.read(m.write())
    print('  roundtrip: %s' % ('OK' if rt.write()==m.write() else 'MISMATCH'))
