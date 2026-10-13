# -*- coding: utf-8 -*-
"""Dump every Material's shader-variable table + slot keys for a patch, so we can
tell whether the base-colour sampling is modulated by a UV transform variable.

usage: _probe_matvars.py <patch_0> [name_filter]
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_asset as A
import hd2_patch as P

NAMES = {
    0xb9f84331454d8282: 'shinano_face', 0xbe569f23c33c3083: 'white',
    0xbde6be9e588bc941: '_4', 0xb8a1ba9aed375bed: 'ornament_metal',
    0xb6e760aee076359d: 'shinano_body', 0xb66b54bafd3dd768: 'shinano_hair_ear',
    0xbbd058dd84a87e8c: 'headdress_white01_catears', 0xbea2867f775bc39b: 'lace_white01_catears',
    0xbb106a56c4d48a2b: 'bra_white01_bra_inner', 0xb3ba47e7968a0ab5: 'glove_white01_wristcuffs',
    0xb8f74de60c5f8803: 'dress_white01_dress_corset', 0xbc9d1471d87f8337: 'lower_white01_underwear',
    0xb65a1090f1511be3: 'shoes_white01_highheels', 0xbf29404cde77bd86: 'shoes_02',
}


def main():
    patch = sys.argv[1]
    flt = sys.argv[2] if len(sys.argv) > 2 else None
    entries, _ = P.scan_entries(patch)
    for e in entries:
        if e['type_id'] != A.MATERIAL_TYPE_ID:
            continue
        fid = e['file_id']
        m = A.Material.read(P.entry_payload(e, patch)['toc'])
        base = m.tex_ids[5] if len(m.tex_ids) > 5 else 0
        nm = NAMES.get(base, 'tex0x%016x' % base)
        if flt and flt not in nm:
            continue
        print('=' * 78)
        print('material 0x%016x  base=%s   parent=%s  ntex=%d nvar=%d'
              % (fid, nm, m.parent_name, len(m.tex_ids), len(m.variables)))
        for i, (k, t) in enumerate(zip(m.tex_unks, m.tex_ids)):
            print('   slot %2d  key %08x  tex 0x%016x' % (i, k, t))
        for v in m.variables:
            print('   var id=0x%08x  %-7s elems=%d stride=%d off=%d  vals=%s'
                  % (v.id, v.klass_name, v.elements, v.element_stride, v.offset,
                     ['%.6g' % x for x in v.values]))
    return 0


if __name__ == '__main__':
    sys.exit(main())
