# -*- coding: utf-8 -*-
"""Map Material shader-variable ids and texture slot keys to their SDK names.

usage: _probe_varnames.py
"""
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_asset as A

SDK = Path(r'E:\Data\source\repos\WpfApp1\docs\HD2SDK-CommunityEdition-3.8.0')

ADVANCED_VARS = [0xa85d996a, 0x02325c30, 0xcb8c5728, 0x7415ab88, 0x6fd56a28,
                 0x14f7ceac, 0x2e2fcb48, 0xd43cfc8b, 0x8de83b3e, 0x2c9d0736,
                 0x6e8e2d58, 0x868e1f43, 0x38dae7bd, 0x4485c321, 0x834e87ba,
                 0x613a8a38, 0xb132b95e, 0x3412359b, 0xccddd706, 0x11855634,
                 0xcd18a390, 0xcff8242b, 0x6a0b8014, 0x88b1a3e2, 0x325d6bd5,
                 0x6f2bffb2, 0x3de979ac, 0x15e00526, 0x80b10f6d, 0xf85d2474,
                 0xee28022c, 0xd0a0e0fc, 0xc985395a, 0x8753c2fe, 0x4c5d8840,
                 0xc998ac9f, 0x4408549c, 0x8b09df61, 0x92607599, 0x77ce49fa,
                 0x02f6dc5b, 0xc3befbff]
ALPHACLIP_VARS = [0x529a4aaf, 0xa3351311, 0x43695f7b, 0x64aab07b, 0x6fd0b9e7,
                  0x60e7d2a1, 0x4a7cd0ef, 0xbd16a396, 0xa83f44cd, 0x6ddbae8f,
                  0x9ed04da2]
TRANSLUCENT_VARS = [0xa3351311, 0xab58e70d, 0x43695f7b, 0x64aab07b, 0x4dec56f9,
                    0x6fd0b9e7, 0x393cf79e, 0xcb577b8f, 0xa83f44cd, 0x6ddbae8f,
                    0x5a341fb6, 0xbd16a396, 0x60e7d2a1]
ADVANCED_SLOTS = [0xa72cb013, 0x479fb1ef, 0x4c567810, 0x90b84a53, 0xe719da57,
                  0xe67ac0c7, 0x736a0029, 0x04fb61ad, 0xd2f99d38, 0xf8e31d7b,
                  0xa59f5e11]
ALPHACLIP_SLOTS = [0x1d57dcf3, 0xcbde381b, 0x848ba63b]
TRANSLUCENT_SLOTS = [0xdab4d0ee]


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


def show(title, ids, names):
    print('=' * 78)
    print(title)
    for i in ids:
        print('   0x%08x  %s' % (i, names.get(i, '??')))


def main():
    names = load_names()
    print('loaded %d hash names' % len(names))
    show('advanced.material  shader variables (42)', ADVANCED_VARS, names)
    show('alphaclip.material  shader variables (11)', ALPHACLIP_VARS, names)
    show('translucent.material  shader variables (13)', TRANSLUCENT_VARS, names)
    show('advanced.material  texture slot keys (11)', ADVANCED_SLOTS, names)
    show('alphaclip.material  texture slot keys (3)', ALPHACLIP_SLOTS, names)
    show('translucent.material  texture slot keys (1)', TRANSLUCENT_SLOTS, names)
    print('=' * 78)
    print('all names containing alpha / clip / translucen / opacity / mask:')
    hits = [(h, n) for h, n in names.items()
            if any(t in n.lower() for t in ('alpha', 'clip', 'translucen', 'opacity', 'cutout'))]
    for h, n in sorted(hits, key=lambda x: x[1].lower())[:80]:
        print('   0x%08x  %s' % (h, n))
    print('   (%d total)' % len(hits))
    return 0


if __name__ == '__main__':
    sys.exit(main())
