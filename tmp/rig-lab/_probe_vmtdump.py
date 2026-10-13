"""Dump the raw text of every distinct VMT the 92 material slots resolve to,
plus a compact slot table, so we know exactly which texture keys exist."""
import os
import sys
from collections import Counter, OrderedDict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_source as S
import hd2_style as H

VPK = S.ADDONS + S.MODEL_VPK
MODEL = 'survivor_teenangst'


def main():
    p = S.Packs()
    mdl_path, mdl, verts, tris, src, short = H.load_source(VPK, MODEL)
    names, cds, skin = S.load_material_slots(mdl)
    used = Counter(t[3] for t in tris)

    vmt_of = {}
    for i in used:
        nm = names[i]
        txt = p.vmt_text(nm)
        vmt_of[i] = (nm, txt, S.parse_vmt(txt) if txt else None)

    distinct = OrderedDict()
    for i, (nm, txt, d) in vmt_of.items():
        base = d.get('$basetexture') if d else None
        distinct.setdefault(os.path.basename(base) if base else '<none>', []).append(i)
    print('== distinct $basetexture values: %d' % len(distinct))
    for b, idxs in sorted(distinct.items(), key=lambda kv: -sum(used[i] for i in kv[1])):
        print('   %-42s slots=%2d tris=%d' % (b, len(idxs), sum(used[i] for i in idxs)))

    # print a handful of distinct VMT texts
    seen = set()
    shown = 0
    for i in sorted(used, key=lambda k: -used[k]):
        nm, txt, d = vmt_of[i]
        base = os.path.basename((d or {}).get('$basetexture', '') or '')
        if base in seen:
            continue
        seen.add(base)
        shown += 1
        print('\n---- slot %d  %s  (%s) ----' % (i, nm, base))
        print(txt)
        if shown >= 12:
            break

    # which keys appear anywhere
    keys = Counter()
    for _i, (_n, _t, d) in vmt_of.items():
        if d:
            keys.update(d.get('_raw', {}).keys())
    print('\n== VMT keys seen ==')
    for k, c in keys.most_common():
        print('   %-28s %d' % (k, c))


if __name__ == '__main__':
    main()
