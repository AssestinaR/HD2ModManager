"""How many of the 92 L4D2 material slots are actually used, and which
textures do they resolve to?"""
import os
import sys
from collections import Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_source as S
import hd2_style as H

VPK = S.ADDONS + S.MODEL_VPK
MODEL = 'survivor_teenangst'


def main():
    p = S.Packs()
    mdl_path, mdl, verts, tris, src, short = H.load_source(VPK, MODEL)
    print('mdl', mdl_path)
    names, cds, skin = S.load_material_slots(mdl)
    print('verts %d tris %d slots %d' % (len(verts), len(tris), len(names)))
    print('cds', cds, 'skin row', skin[:8], 'len', len(skin))
    used = Counter(t[3] for t in tris)
    print('distinct material indices used: %d' % len(used))
    missing = [i for i in used if not (0 <= i < len(names))]
    print('out-of-range indices:', missing[:10])

    ok = miss = 0
    for i, n in sorted(used.items(), key=lambda kv: -kv[1]):
        nm = names[i] if 0 <= i < len(names) else '?'
        vmt_text = p.vmt_text(nm)
        vmt = S.parse_vmt(vmt_text) if vmt_text else None
        bt = vmt.get('$basetexture') if vmt else None
        bm = vmt.get('$bumpmap') if vmt else None
        vtf = p.vtf_bytes(bt) if bt else None
        if vtf:
            ok += 1
        else:
            miss += 1
        print('  %3d  %-42s tris=%-6d vmt=%-5s base=%-40s(%s) bump=%-30s(%s)'
              % (i, nm, used[i], 'yes' if vmt else 'NO',
                 os.path.basename(bt) if bt else '-', 'ok' if vtf else 'MISS',
                 os.path.basename(bm) if bm else '-',
                 'ok' if (bm and p.vtf_bytes(bm)) else ('-' if not bm else 'MISS')))
    print('resolved %d / missing %d' % (ok, miss))


if __name__ == '__main__':
    main()

