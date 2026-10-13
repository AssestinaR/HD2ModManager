# -*- coding: utf-8 -*-
"""Dump Material payloads (loose .toc files OR a patch) with every shader
variable id and texture-slot key resolved to its SDK name.

usage:
  _probe_matdump.py toc <dir-or-file> [<dir-or-file> ...]
  _probe_matdump.py patch <patch_0>
"""
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_asset as A
import hd2_patch as P

SDK = Path(r'E:\Data\source\repos\WpfApp1\docs\HD2SDK-CommunityEdition-3.8.0')

# the ids the SDK templates declare, so a payload can be matched to a template
TEMPLATE_PARENT = {
    0xef9b3fc2fc1ba61e: 'advanced',
    0xd84d04634a1e2f60: 'alphaclip',
    0xc6042e3403385d40: 'alphaclip+',
    0x54ae9ce1a8fafe8b: 'original',
    0xd51d2a873865d4e5: 'basic',
    0x7712ee7808e0ec50: 'basic+',
    0x2f9c50d9129837fe: 'basic+OLD',
    0xd3701fc725106c09: 'emissive',
    0xf5ebd93181fdf00c: 'armorlut',
    0x84e5de1cc6b74ada: 'translucent',
}
# the slot-key sets the SDK templates declare, so an unknown parent still gets
# a family name
TEMPLATE_SLOTS = {
    'advanced': [0xa72cb013, 0x479fb1ef, 0x4c567810, 0x90b84a53, 0xe719da57,
                 0xe67ac0c7, 0x736a0029, 0x04fb61ad, 0xd2f99d38, 0xf8e31d7b,
                 0xa59f5e11],
    'alphaclip': [0x1d57dcf3, 0xcbde381b, 0x848ba63b],
    'alphaclip+': [0x1d57dcf3, 0xca6f2cf1, 0x848ba63b, 0xcbde381b],
    'original': [0x7ca0d044, 0xc985395a, 0xa72cb013, 0x479fb1ef, 0xdf3ee984,
                 0xcaed6cd6, 0xd2f99d38, 0xe7bd9019, 0xd47db28b, 0xff2c91cc,
                 0x736a0029, 0xf8e31d7b, 0xa59f5e11],
    'basic': [0x756f6fa6, 0xac652e43, 0xcaed6cd6],
    'basic+': [0x756f6fa6, 0xac652e43, 0xcaed6cd6],
    'emissive': [0x1d57dcf3, 0xca6f2cf1, 0x848ba63b],
    'armorlut': [0x632a8b80, 0xa17b45a8, 0x81d4c49d, 0xc2eb8d6e, 0x30e2d136,
                 0xd2f99d38, 0x05a27dd5, 0xb281e5f2, 0x37831285, 0x7e662968,
                 0x6834aa9b],
    'translucent': [0xdab4d0ee],
}
# human labels for the slots we care about (from the SDK's own tooltips where
# known, plus the family the key belongs to)
KNOWN_SLOT = {
    0x479fb1ef: 'SclarFieldOpacity',
    0xe67ac0c7: 'AlbedoEmissive',
    0x4c567810: 'Nar (Normal/AO/Roughness)',
    0x90b84a53: 'IlluminateData',
    0xcaed6cd6: 'Normal',
    0xcbde381b: 'OpacityClipMap',
    0x848ba63b: 'BaseColorMetalMap',
    0x1d57dcf3: 'NormalXyAoRoughMap',
    0xdab4d0ee: 'NormalXyRoughnessOpacity',
    0x7ca0d044: '?0',
    0xc985395a: 'EmissiveColor',
    0x736a0029: 'WoundNormal',
    0xf8e31d7b: 'WoundData',
    0xa59f5e11: 'WoundDerivative',
    0xd2f99d38: 'WeatheringSpecial',
    0xdf3ee984: '?4',
    0xe7bd9019: '?7',
    0xd47db28b: '?8',
    0xff2c91cc: '?9',
}
OPACITY_VARS = {
    0xcb8c5728: 'OpacitySharpness',
    0xccddd706: 'OpacityOffset',
    0x529a4aaf: 'OpacityThreshold',
    0xab58e70d: 'OpacityBase',
}


def load_names():
    out = {}
    for fn in ('shadervariables.txt', 'friendlynames.txt'):
        p = SDK / 'hashlists' / fn
        if not p.exists():
            continue
        for line in open(p, encoding='utf-8', errors='replace'):
            parts = line.split()
            if len(parts) < 2 or not parts[-1].lower().startswith('0x'):
                continue
            try:
                h = int(parts[-1], 16)
            except ValueError:
                continue
            out.setdefault(h, ' '.join(parts[:-1]))
    return out


def family_of(parent, slots):
    if parent in TEMPLATE_PARENT:
        return TEMPLATE_PARENT[parent]
    for fam, keys in TEMPLATE_SLOTS.items():
        if list(slots) == keys:
            return fam + ' (by slot layout)'
    return '?'


def dump(tag, fid, m, names):
    fam = family_of(m.parent_material_id, m.tex_unks)
    print('-' * 78)
    print('%s  id=%s  parent=%s (0x%016x)  ntex=%d nvar=%d'
          % (tag, ('0x%016x' % fid) if fid else '-', m.parent_name,
             m.parent_material_id, len(m.tex_ids), len(m.variables)))
    print('   family: %s' % fam)
    for i, (k, t) in enumerate(zip(m.tex_unks, m.tex_ids)):
        nm = KNOWN_SLOT.get(k) or names.get(k, '?')
        print('   slot %2d key %08x %-28s tex %s'
              % (i, k, nm, ('0x%016x' % t) if t else '0'))
    for v in m.variables:
        nm = OPACITY_VARS.get(v.id) or names.get(v.id, '?')
        mark = '  <<<' if v.id in OPACITY_VARS else ''
        print('   var 0x%08x %-26s %-7s  %s%s'
              % (v.id, nm, v.klass_name, ['%.6g' % x for x in v.values], mark))


def main():
    mode = sys.argv[1]
    names = load_names()
    print('loaded %d hash names' % len(names))
    if mode == 'patch':
        for p in sys.argv[2:]:
            print('=' * 78)
            print('PATCH %s' % p)
            entries, _ = P.scan_entries(p)
            for e in entries:
                if e['type_id'] != A.MATERIAL_TYPE_ID:
                    continue
                dump('material', e['file_id'],
                     A.Material.read(P.entry_payload(e, p)['toc']), names)
    else:
        files = []
        for a in sys.argv[2:]:
            if os.path.isdir(a):
                files += [os.path.join(a, f) for f in sorted(os.listdir(a))
                          if f.endswith('.toc')]
            else:
                files.append(a)
        for f in files:
            fid = 0
            base = os.path.basename(f)
            if base.startswith('material_0x'):
                fid = int(base[len('material_0x'):-len('.toc')], 16)
            print('=' * 78)
            print('TOC %s' % f)
            dump('material', fid, A.Material.read(open(f, 'rb').read()), names)
    return 0


if __name__ == '__main__':
    sys.exit(main())
