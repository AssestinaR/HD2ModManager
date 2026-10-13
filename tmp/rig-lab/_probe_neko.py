# -*- coding: utf-8 -*-
"""Full NekoToon key census over the model's own 92 slots.

M7 grouped by $basetexture only.  NekoToon adds a whole vocabulary that
carries appearance information we currently drop:  $matcap / $matcaptexture /
$matcaptint / $matcaplighting / $lightwarptexture / $emission / $emissiontint /
$additive / $phong ...  This probe dumps every *active* key per VMT and groups
the 92 slots by their full active-key signature, so we can design a general
(data driven) rule instead of hard-coding ids.

usage: _probe_neko.py            -> out/_neko.txt
"""
import os
import sys
from collections import OrderedDict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import numpy as np
import export_obj as E
import hd2_source as S
import hd2_style as ST


def _value(v):
    """strip trailing // comment; keep the quoted or first token."""
    v = v.strip()
    if v.startswith('"'):
        end = v.find('"', 1)
        return v[1:end] if end > 0 else v[1:]
    cut = v.find('//')
    if cut >= 0:
        v = v[:cut].strip()
    return v.split()[0] if v.split() else ''


def active_keys(txt):
    """-> OrderedDict '$key' -> value, every non-comment, non-block line."""
    out = OrderedDict()
    if not txt:
        return out
    for line in txt.splitlines():
        s = line.strip()
        if not s or s.startswith('//') or s in ('{', '}'):
            continue
        if not s.startswith('$'):
            continue
        k, _, v = s.partition(' ')
        out[k.lower()] = _value(v)
    return out


def main():
    vpk = S.ADDONS + S.MODEL_VPK
    mdl_path, mdl, verts, tris4, _src, _short = ST.load_source(vpk, 'survivor_teenangst')
    tris4 = np.asarray(tris4, np.int64)
    names, cds, row = S.load_material_slots(mdl)
    used = sorted({int(m) for m in tris4[:, 3]})
    packs = S.Packs()

    print('cdtextures : %s' % cds)
    print('slots used : %d' % len(used))

    # ---- 1. key census -------------------------------------------------
    keycount = {}
    shaders = {}
    for i in used:
        nm = names[i] if 0 <= i < len(names) else None
        txt = packs.vmt_text(nm) if nm else None
        if txt:
            head = txt.strip().split('\n', 1)[0].strip()
            shaders[head] = shaders.get(head, 0) + 1
        for k in active_keys(txt):
            keycount.setdefault(k, []).append(i)
    print('=' * 100)
    print('shader (first line of VMT) census')
    for s, n in sorted(shaders.items(), key=lambda kv: -kv[1]):
        print('   %-24s %d slots' % (s, n))
    print('=' * 100)
    print('active key census (key -> #slots)')
    for k, slots in sorted(keycount.items(), key=lambda kv: -len(kv[1])):
        print('   %-26s %3d   e.g. slots %s' % (k, len(slots), slots[:6]))

    # ---- 2. group by full signature ------------------------------------
    print('=' * 100)
    groups = OrderedDict()
    for i in used:
        nm = names[i] if 0 <= i < len(names) else None
        txt = packs.vmt_text(nm) if nm else None
        ak = active_keys(txt)
        base = os.path.basename(ak.get('$basetexture', '?'))
        key = (base, ak.get('$matcap', '-'), os.path.basename(ak.get('$matcaptexture', '-')),
               ak.get('$matcaptint', '-'), ak.get('$emission', '-'),
               ak.get('$emissiontint', '-'), ak.get('$translucent', '-'),
               ak.get('$alphatest', '-'))
        groups.setdefault(key, []).append(i)
    print('groups by (base, matcap, matcaptex, matcaptint, emis, emistint, transl, atest)')
    for key, slots in groups.items():
        print('   base=%-30s mc=%-3s mtex=%-16s mtint=%-18s em=%-3s emt=%-22s tr=%-3s at=%-3s  n=%2d %s'
              % (key[0], key[1], key[2], key[3], key[4], key[5], key[6], key[7],
                 len(slots), slots[:12]))

    # ---- 3. per-slot dump (compact: only the keys we care about) -------
    print('=' * 100)
    print('per-slot active keys')
    for i in used:
        nm = names[i] if 0 <= i < len(names) else None
        ak = active_keys(packs.vmt_text(nm) if nm else None)
        print('slot %2d  %-46s' % (i, os.path.basename(nm or '?')))
        for k in ('$basetexture', '$bumpmap', '$matcaptexture', '$matcaptint',
                  '$matcap', '$lightwarptexture', '$emission', '$emissiontint',
                  '$emissiontexture', '$translucent', '$alphatest',
                  '$alphatestreference', '$nocull', '$outline', '$outlinetexture'):
            if k in ak:
                print('        %-22s %s' % (k, ak[k]))
    return 0


if __name__ == '__main__':
    sys.exit(main())
