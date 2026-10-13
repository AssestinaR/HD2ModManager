# -*- coding: utf-8 -*-
"""Per-VPK -> per-HD2-material-group mapping for the 八人包.

For every VPK in the archive, work at the *slot* level (all 92 of them, not the
14 group representatives) and answer:

  * which HD2 material groups its VTFs can repaint  (colour / matcap)
  * which HD2 material groups its VMTs change *semantically* -- compared key by
    key against the VMT the model already resolves to for that slot name, so a
    mere whitespace difference is not reported

Read-only.  Writes tmp/rig-lab/out/_pkg8map.txt
"""
import json
import os
import sys
from collections import OrderedDict

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import numpy as np
import export_obj as E
import hd2_source as S
import hd2_style as ST
import fix_appearance as FA

ROOT = r'E:\tmp\pkg8'
OUT = os.path.join(HERE, 'out', '_pkg8map.txt')

lines = []


def p(s=''):
    lines.append(s)
    print(s, flush=True)


def stem(x):
    s = os.path.basename((x or '').replace('/', '\\'))
    for e in ('.vmt', '.vtf'):
        if s.lower().endswith(e):
            s = s[:-4]
    return s.lower()


def vpk_files(path):
    _v, do, ents = E.parse_vpk_tree(path)
    vmts, vtfs = {}, {}
    for f, a, o, l in ents:
        fl = f.replace('/', '\\').lower()
        b = os.path.basename(fl)
        if fl.endswith('.vmt'):
            vmts[b[:-4]] = (do, a, o, l)
        elif fl.endswith('.vtf'):
            vtfs[b[:-4]] = (do, a, o, l)
    return vmts, vtfs


def main():
    # ---- the model's slot table -------------------------------------------
    vpk = S.ADDONS + S.MODEL_VPK
    _md, mdl, _v, tris4, _s, _sh = ST.load_source(vpk, 'survivor_teenangst')
    tris4 = np.asarray(tris4, np.int64)
    names, cds, _row = S.load_material_slots(mdl)
    packs = S.Packs()
    groups = FA.analyse(tris4, names, packs)

    # id formula must still reproduce the shipped M8 manifest
    man = json.load(open(r'E:\mod\Lunalice-RE2310-M8\lunalice-m8.manifest.json',
                         encoding='utf-8'))
    ok = True
    for d in man['groups']:
        if '0x%016x' % FA.make_id(FA.MAT_ID_PREFIX,
                                  'mat/' + (d['base'] or 'untextured')) != d['material']:
            p('!! material id mismatch for %s' % d['group'])
            ok = False
        if '0x%016x' % FA.make_id(FA.TEX_ID_PREFIX,
                                  'tex/base/' + stem(d['base'])) != d['colorId']:
            p('!! color id mismatch for %s' % d['group'])
            ok = False
    p('id formula reproduces the M8 manifest: %s' % ok)

    # slot -> (vmt name, group stem); and the VMT the model resolves to
    slot_group, slot_vmtname, model_vmt = {}, {}, {}
    for base, g in groups.items():
        st = stem(base)
        for i in g['slots']:
            nm = (names[i] if 0 <= i < len(names) else '').lower()
            slot_group[i] = st
            slot_vmtname[nm] = i
            model_vmt[nm] = packs.vmt_text(names[i]) or ''

    p('')
    p('%-30s %5s %6s  %s' % ('HD2 group (= $basetexture)', 'slots', 'tris',
                              'matcaps'))
    p('-' * 120)
    for base, g in groups.items():
        mc = [os.path.basename(t) for t in g['matcaps']]
        p('%-30s %5d %6d  %s' % (stem(base), len(g['slots']), g['tris'],
                                  mc if mc else '-'))
    p('-' * 120)

    color_stems = {stem(b) for b in groups if b}
    matcap_all = set()
    for g in groups.values():
        for t in g['matcaps']:
            matcap_all.add(stem(t))

    vpks = []
    for dirpath, _dn, fns in os.walk(ROOT):
        for fn in fns:
            if fn.lower().endswith('.vpk'):
                vpks.append(os.path.join(dirpath, fn))
    vpks.sort()

    p('')
    p('=' * 120)
    p('PER-PACK MAP  (slot-level)')
    p('=' * 120)
    table = []
    for path in vpks:
        rel = os.path.relpath(path, ROOT).replace('\\', '/')
        try:
            vmts, vtfs = vpk_files(path)
        except Exception as e:
            p('%-52s ERROR %s' % (rel, e))
            continue
        hit_color = sorted(color_stems & set(vtfs))
        hit_matcap = sorted(matcap_all & set(vtfs))
        # slot-level VMT comparison
        changed = OrderedDict()          # group stem -> [slot, ..]
        details = []
        for v in sorted(vmts):
            if v not in model_vmt:
                continue
            do, a, o, l = vmts[v]
            new = (E.read_range(path, do, a, o, l) or b'').decode('latin-1', 'replace')
            a1, a2 = FA.active_keys(model_vmt[v]), FA.active_keys(new)
            dif = [(k, a1.get(k), a2.get(k)) for k in sorted(set(a1) | set(a2))
                   if a1.get(k) != a2.get(k)]
            if not dif:
                continue
            i = slot_vmtname[v]
            gs = slot_group[i]
            changed.setdefault(gs, []).append(i)
            details.append('%s(slot %d): %s' % (v, i, ', '.join(
                '%s %r->%r' % (k, x, y) for k, x, y in dif[:3])))
        p('')
        p('%-52s %d B' % (rel, os.path.getsize(path)))
        p('   vtf %-3d  repaints colour : %s' % (len(vtfs), hit_color or '-'))
        if hit_matcap:
            p('   vtf       repaints matcap : %s' % hit_matcap)
        p('   vmt %-3d  semantic change : %s'
          % (len(vmts), ('; '.join('%s%r' % (k, v) for k, v in changed.items()))
             if changed else '-'))
        for d in details[:6]:
            p('        %s' % d)
        if not hit_color and not hit_matcap and not changed:
            p('   (no effect on the 14 groups)')
        table.append(dict(rel=rel, size=os.path.getsize(path),
                          vtfs=sorted(vtfs), vmts=sorted(vmts),
                          color=hit_color, matcap=hit_matcap,
                          changed={k: v for k, v in changed.items()}))

    p('')
    p('=' * 120)
    p('SUMMARY -- who can repaint each colour group')
    p('=' * 120)
    for st in sorted(color_stems):
        p('  %-34s <- %s' % (st, [t['rel'] for t in table if st in t['color']] or 'NONE'))
    p('')
    for st in sorted(matcap_all):
        p('  matcap %-30s <- %s' % (st, [t['rel'] for t in table if st in t['matcap']] or 'NONE'))
    p('')
    p('SUMMARY -- who changes material parameters, by group')
    for st in sorted(color_stems):
        p('  %-34s <- %s' % (st, [t['rel'] for t in table if st in t['changed']] or '-'))

    with open(OUT, 'w', encoding='utf-8') as f:
        f.write('\n'.join(lines))
    print('-> %s' % OUT)


if __name__ == '__main__':
    main()
