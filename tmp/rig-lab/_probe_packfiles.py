# -*- coding: utf-8 -*-
"""List every VMT/VTF path in the source packs, grouped by directory.

The model's own `cdtextures` are ['matchacheese\\Lunalice\\opt\\',
'matchacheese\\Lunalice\\'], i.e. an `opt\\` overlay directory is searched
FIRST.  `hd2_source.Packs` keys its lookups by basename, so an `opt\\X.vtf` and
an `X.vtf` with the same basename silently collide.

usage: _probe_packfiles.py
"""
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import export_obj as E
import hd2_source as S


def main():
    for name in S.PACK_VPKS:
        p = os.path.join(S.ADDONS, name)
        if not os.path.exists(p):
            print('MISSING %s' % name)
            continue
        _ver, do, ents = E.parse_vpk_tree(p)
        vtfs = sorted(f for f, *_ in ents if f.lower().endswith('.vtf'))
        vmts = sorted(f for f, *_ in ents if f.lower().endswith('.vmt'))
        print('=' * 100)
        print('%s  : %d vtf, %d vmt' % (name, len(vtfs), len(vmts)))
        dirs = {}
        for f in vtfs:
            d = os.path.dirname(f.replace('/', '\\')).lower()
            dirs.setdefault(d, []).append(os.path.basename(f))
        for d, files in sorted(dirs.items()):
            print('   %-46s %3d files' % (d, len(files)))
            if len(files) <= 12:
                print('        %s' % ', '.join(sorted(files)))

    print('=' * 100)
    print('basename collisions across the whole pack set')
    packs = S.Packs()
    for ext, table, short in (('vtf', packs.vtf, packs._vtf_short),
                              ('vmt', packs.vmt, packs._vmt_short)):
        names = {}
        for path in table:
            names.setdefault(os.path.basename(path)[:-4], []).append(path)
        col = {k: v for k, v in names.items() if len(v) > 1}
        print('  .%s : %d distinct basenames, %d COLLIDING' % (ext, len(names), len(col)))
        for k, v in sorted(col.items())[:40]:
            print('      %-34s -> %s' % (k, ' | '.join(sorted(v))))
        # which one does the lookup actually return?
        for k in sorted(col)[:40]:
            got = short.get(k)
            print('        lookup(%s) -> %s' % (k, got))
    return 0


if __name__ == '__main__':
    sys.exit(main())
