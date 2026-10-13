# -*- coding: utf-8 -*-
"""Per-model-slot material audit -- the *general* view.

`build_patch.build_materials` groups the model's 92 material slots by
`$basetexture`, so one HD2 material can cover many source slots.  This probe
walks the model's own slot table (not a basename guess) and prints, for every
slot, the VMT it resolves to and every translucency / metal flag in it, then
summarises per basetexture group.

usage: _probe_slots.py
"""
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import export_obj as E
import hd2_source as S

VMT_KEYS = ('$translucent', '$alphatest', '$alphatestreference',
            '$allowalphatocoverage', '$mostlyopaque', '$vertexalpha',
            '$vertexcolor', '$nocull', '$phong', '$phongexponent',
            '$phongtint', '$phongfresnelranges', '$envmap', '$envmaptint',
            '$color2', '$color', '$surfaceprop', '$metalness', '$roughness',
            '$basetexture', '$bumpmap', '$ssbump', '$detail')


def main():
    vpk = S.ADDONS + S.MODEL_VPK
    mdl_path, mdl, verts, tris4, _src, _short = ST_load(vpk)
    import numpy as np
    tris4 = np.asarray(tris4, np.int64)
    names, cds, row = S.load_material_slots(mdl)
    used = sorted({int(m) for m in tris4[:, 3]})
    packs = S.Packs()

    print('model      : %s' % mdl_path)
    print('cdtextures : %s' % cds)
    print('slots      : %d named, %d used by triangles, skin row %d entries'
          % (len(names), len(used), len(row)))
    print('=' * 100)

    by_base = {}
    for i in used:
        nm = names[i] if 0 <= i < len(names) else None
        txt = packs.vmt_text(nm) if nm else None
        vmt = S.parse_vmt(txt) if txt else {}
        base = vmt.get('$basetexture')
        flags = []
        for line in (txt or '').splitlines():
            s = line.strip()
            if not s:
                continue
            commented = s.startswith('//')
            body = s[2:].strip() if commented else s
            key = body.split(None, 1)[0].lower() if body.split() else ''
            if key in VMT_KEYS:
                flags.append(('X ' if commented else 'A ') + body)
        active = [f for f in flags if f.startswith('A ')]
        by_base.setdefault(base, []).append(i)
        print('slot %2d  vmt=%-52s base=%s' % (i, os.path.basename(nm or '?'),
                                               os.path.basename(base or '?')))
        for f in flags:
            mark = '   <<< ACTIVE' if f.startswith('A ') else ''
            print('        %s%s' % (f, mark))
        if not flags:
            print('        (no recognised keys)')

    print('=' * 100)
    print('per-basetexture group summary (%d groups)' % len(by_base))
    for base, slots in by_base.items():
        txts = {packs.vmt_text(names[i]) for i in slots if 0 <= i < len(names)}
        act = set()
        for t in txts:
            for line in (t or '').splitlines():
                s = line.strip()
                if not s or s.startswith('//'):
                    continue
                k = s.split(None, 1)[0].lower()
                if k in VMT_KEYS:
                    act.add(k)
        raw = packs.vtf_bytes(base) if base else None
        fmt = '-'
        if raw:
            import hd2_vtf as V
            v = V.VTF.read(raw)
            fmt = '%s %dx%d' % (v.format_name, v.width, v.height)
        print('  %-34s slots=%2d  vmt=%d  %-16s active: %s'
              % (os.path.basename(base or '?'), len(slots), len(txts), fmt,
                 ','.join(sorted(act)) or '-'))
    return 0


def ST_load(vpk):
    """reuse the same loader build_patch uses"""
    import hd2_style as ST
    return ST.load_source(vpk, 'survivor_teenangst')


if __name__ == '__main__':
    sys.exit(main())
