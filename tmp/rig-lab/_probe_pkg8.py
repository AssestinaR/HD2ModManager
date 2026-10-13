# -*- coding: utf-8 -*-
"""Inventory of the 【SHOP】Lunalice八人包101 archive.

For every VPK in the extracted tree: list its VMT/VTF (and mdl) entries, and
cross-reference the *model's own* 92-slot table so we can say, per pack,
"this pack supplies the $basetexture of slots {..}".

Read-only.  Writes tmp/rig-lab/out/_pkg8.txt.
"""
import os
import sys
from collections import OrderedDict

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import export_obj as E
import hd2_source as S
import hd2_style as ST
import numpy as np

ROOT = r'E:\tmp\pkg8'
OUT = os.path.join(HERE, 'out', '_pkg8.txt')


def vpk_entries(path):
    ver, do, ents = E.parse_vpk_tree(path)
    return ver, do, ents


def main():
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    lines = []

    def p(s=''):
        lines.append(s)
        print(s, flush=True)

    # ---- the model's own slot table --------------------------------------
    vpk = S.ADDONS + S.MODEL_VPK
    _md, mdl, _v, tris4, _s, _sh = ST.load_source(vpk, 'survivor_teenangst')
    tris4 = np.asarray(tris4, np.int64)
    names, cds, _row = S.load_material_slots(mdl)
    used = sorted(int(x) for x in np.unique(tris4[:, 3]))
    p('model cdtextures: %s' % cds)
    p('model slots referenced: %d' % len(used))

    # slot -> vmt basename, slot -> basetexture
    slot_vmt, slot_base = {}, {}
    packs0 = S.Packs()
    for i in used:
        nm = names[i] if 0 <= i < len(names) else None
        txt = packs0.vmt_text(nm) if nm else None
        vmt = S.parse_vmt(txt) if txt else {}
        slot_vmt[i] = (nm or '').lower()
        slot_base[i] = (vmt.get('$basetexture') or '')
    def stem(x):
        s = os.path.basename(x)
        for e in ('.vmt', '.vtf'):
            if s.lower().endswith(e):
                s = s[:-4]
        return s

    want_vmt = {stem(v): v for v in slot_vmt.values()}
    want_vtf = {stem(b): b for b in slot_base.values() if b}

    # ---- walk the extracted tree -----------------------------------------
    vpks = []
    for dirpath, _dn, fns in os.walk(ROOT):
        for fn in fns:
            if fn.lower().endswith('.vpk'):
                vpks.append(os.path.join(dirpath, fn))
    vpks.sort()

    summary = []
    for path in vpks:
        rel = os.path.relpath(path, ROOT).replace('\\', '/')
        try:
            ver, do, ents = vpk_entries(path)
        except Exception as e:
            p('%-58s  ERROR %s' % (rel, e))
            continue
        vmts, vtfs, mdls, others = [], [], [], []
        for f, a, o, l in ents:
            fl = f.replace('/', '\\').lower()
            base = os.path.basename(fl)
            if fl.endswith('.vmt'):
                vmts.append(base[:-4])
            elif fl.endswith('.vtf'):
                vtfs.append(base[:-4])
            elif fl.endswith(('.mdl', '.vvd', '.vtx')):
                mdls.append(base)
            else:
                others.append(base)
        hit_vmt = sorted(set(v for v in vmts if v in want_vmt))
        hit_vtf = sorted(set(v for v in vtfs if v in want_vtf))
        slots_vmt = sorted(i for i, v in slot_vmt.items() if stem(v) in vmts)
        slots_vtf = sorted(i for i, b in slot_base.items()
                           if b and stem(b) in vtfs)
        summary.append(dict(rel=rel, size=os.path.getsize(path), n=len(ents),
                            vmts=vmts, vtfs=vtfs, mdls=mdls,
                            hit_vmt=hit_vmt, hit_vtf=hit_vtf,
                            slots_vmt=slots_vmt, slots_vtf=slots_vtf))
        p('')
        p('=' * 110)
        p('%-58s  %d B  entries=%d' % (rel, os.path.getsize(path), len(ents)))
        if mdls:
            p('   model   : %d  %s' % (len(mdls), mdls[:6]))
        p('   vmt     : %d  (in model table: %d) %s'
          % (len(vmts), len(hit_vmt), sorted(vmts) if len(vmts) <= 8 else hit_vmt[:12]))
        p('   vtf     : %d  (in model table: %d) %s'
          % (len(vtfs), len(hit_vtf), sorted(vtfs) if len(vtfs) <= 8 else hit_vtf[:12]))
        if others:
            p('   other   : %d  %s' % (len(others), sorted(set(others))[:10]))
        if slots_vmt:
            p('   slots via vmt : %s' % slots_vmt)
        if slots_vtf:
            p('   slots via vtf : %s' % slots_vtf)

    p('')
    p('=' * 110)
    p('TOTAL %d vpks' % len(summary))
    with open(OUT, 'w', encoding='utf-8') as f:
        f.write('\n'.join(lines))
    print('-> %s' % OUT)


if __name__ == '__main__':
    main()
