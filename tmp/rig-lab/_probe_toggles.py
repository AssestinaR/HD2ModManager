# -*- coding: utf-8 -*-
"""Feasibility of the 材质开关 (part on/off) packs.

Every one of those packs replaces a slot's VMT with

    VertexLitGeneric { $no_draw 1  $zero 0  Proxies { Equals { srcVar1 $zero
                                                              resultVar $alpha } } }

i.e. "do not draw this slot".  HD2 has no per-slot draw switch: one HD2
material serves a whole group of source slots, so a toggle is directly
expressible **only when the slots it silences are exactly one whole group**.

This probe measures that, per pack, so we know which toggles can become a
patch as-is and which need the per-slot UV-mask route.

Read-only.  Writes tmp/rig-lab/out/_toggles.txt
"""
import os
import sys
from collections import OrderedDict

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import numpy as np
import export_obj as E
import fix_appearance as FA
import hd2_source as S
import hd2_style as ST

ROOT = r'E:\tmp\pkg8'
OUT = os.path.join(HERE, 'out', '_toggles.txt')
lines = []


def p(s=''):
    lines.append(s)
    print(s, flush=True)


def main():
    vpk = S.ADDONS + S.MODEL_VPK
    _md, mdl, _v, tris4, _s, _sh = ST.load_source(vpk, 'survivor_teenangst')
    tris4 = np.asarray(tris4, np.int64)
    names, _cds, _row = S.load_material_slots(mdl)
    packs = S.Packs()
    groups = FA.analyse(tris4, names, packs)

    slot_group, group_slots = {}, {}
    for base, g in groups.items():
        st = os.path.basename(base or 'untextured')
        group_slots[st] = set(g['slots'])
        for i in g['slots']:
            slot_group[i] = st
    model_vmt = {}
    for base, g in groups.items():
        for i in g['slots']:
            nm = names[i].lower()
            model_vmt[nm] = packs.vmt_text(names[i]) or ''

    d = os.path.join(ROOT, '材质开关')
    vpks = sorted(os.path.join(d, f) for f in os.listdir(d) if f.lower().endswith('.vpk'))

    p('=' * 118)
    p('材质开关 (hide-this-part) packs -- can one HD2 material express them?')
    p('=' * 118)
    p('%-46s %6s  %-58s %s' % ('pack', 'slots', 'groups silenced', 'verdict'))
    p('-' * 118)
    rows = []
    for path in vpks:
        rel = os.path.basename(path)
        _v, do, ents = E.parse_vpk_tree(path)
        changed = []
        nodraw = []
        for f, a, o, l in ents:
            fl = f.replace('/', '\\').lower()
            if not fl.endswith('.vmt'):
                continue
            b = os.path.basename(fl)[:-4]
            if b not in model_vmt:
                continue
            new = (E.read_range(path, do, a, o, l) or b'').decode('latin-1', 'replace')
            a1, a2 = FA.active_keys(model_vmt[b]), FA.active_keys(new)
            if any(a1.get(k) != a2.get(k) for k in set(a1) | set(a2)):
                changed.append(b)
            if FA._num(a2.get('$no_draw')):
                nodraw.append(b)
        silenced = sorted({i for b in changed for i, nm in enumerate(names)
                           if nm.lower() == b and i in slot_group})
        hidden = sorted({i for b in nodraw for i, nm in enumerate(names)
                         if nm.lower() == b and i in slot_group})
        gs = OrderedDict()
        for i in silenced:
            gs.setdefault(slot_group[i], []).append(i)
        whole, part = [], []
        for gst, sl in gs.items():
            if set(sl) == group_slots[gst]:
                whole.append(gst)
            else:
                part.append('%s(%d/%d)' % (gst, len(sl), len(group_slots[gst])))
        if not hidden:
            verdict = 'no $no_draw (not a hide switch)'
        elif whole and not part:
            verdict = 'OK whole-group'
        elif part:
            verdict = 'PARTIAL'
        else:
            verdict = 'no effect'
        p('%-46s %6d  %-58s %s'
          % (rel[:46], len(hidden),
             ('hide: ' + ','.join(whole) if whole else '') +
             (' partial: ' + ','.join(part) if part else '') or '-', verdict))
        rows.append(dict(pack=rel, slots=hidden, whole=whole, part=part,
                         verdict=verdict))
    p('-' * 118)
    n_ok = sum(1 for r in rows if r['verdict'].startswith('OK'))
    n_part = sum(1 for r in rows if r['verdict'] == 'PARTIAL')
    n_none = sum(1 for r in rows if not r['verdict'].startswith(('OK', 'PARTIAL')))
    p('%d packs: %d whole-group (portable now), %d partial (need per-slot material), '
      '%d not a hide switch' % (len(rows), n_ok, n_part, n_none))
    with open(OUT, 'w', encoding='utf-8') as f:
        f.write('\n'.join(lines))
    print('-> %s' % OUT)


if __name__ == '__main__':
    main()
