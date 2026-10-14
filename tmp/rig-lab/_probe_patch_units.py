# -*- coding: utf-8 -*-
"""READ-ONLY: list the entry types and unit ids in a patch."""
import os
import sys
from collections import Counter

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_patch as P
import hd2_unit as U

NAMES = {U.UNIT_ID: 'unit', U.BONE_ID: 'bones', U.MATERIAL_ID: 'material',
         U.TEXTURE_ID: 'texture'}


def main():
    src = sys.argv[1] if len(sys.argv) > 1 else \
        '/e/mod/MourningVeil-RE2310/9ba626afa44a3aa3.patch_0'
    entries, _ = P.scan_entries(src)
    c = Counter(e['type_id'] for e in entries)
    for t, n in c.items():
        print('%-10s %d' % (NAMES.get(t, '0x%016x' % t), n))
    print('--- units ---')
    for e in entries:
        if e['type_id'] == U.UNIT_ID:
            print('0x%016x' % e['file_id'])
    return 0


if __name__ == '__main__':
    sys.exit(main())
