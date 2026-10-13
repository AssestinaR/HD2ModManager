# -*- coding: utf-8 -*-
"""READ-ONLY texture comparison (round 60): ours vs the working reference mod.

For every Texture asset in a patch prints DXGI format, size, mip count and the
actual GPU byte count, so a "black model" can be traced to a bad texture.
"""
import os
import sys

import hd2_asset as A
import hd2_patch as P

OURS = 'E:/mod/Lunalice-RE2310-Test/9ba626afa44a3aa3.patch_0'
REF = 'E:/mod/【4.28】Chiffon-LUNALICE替换RE-2310/9ba626afa44a3aa3.patch_0'


def dump(path, title):
    print('=' * 100)
    print('%s\n  %s' % (title, path))
    entries, raw = P.scan_entries(path)
    tex = [e for e in entries if e['type_id'] == A.TEXTURE_TYPE_ID]
    print('  %d Texture entries' % len(tex))
    for e in sorted(tex, key=lambda x: -x['gpu_size'])[:40]:
        toc = raw[e['toc_offset']:e['toc_offset'] + e['toc_size']]
        t = A.Texture.read(toc)
        px = e['gpu_size']
        # BC block bytes per 16 texels; BC7/BC5 = 16, BC1/BC4 = 8
        blocks = max(1, (t.width + 3) // 4) * max(1, (t.height + 3) // 4)
        per = {70: 8, 71: 8, 72: 8, 73: 16, 74: 16, 75: 16, 76: 16, 77: 16,
               78: 16, 79: 8, 80: 8, 81: 8, 82: 16, 83: 16, 84: 16}.get(
                   t.dxgi_format, 4)
        expect = blocks * per
        print('    0x%016x  %-18s %5dx%-5d mips=%-2d  gpu=%-9d top-mip-expect=%-9d %s'
              % (e['file_id'], t.format_name, t.width, t.height, t.mip_count,
                 px, expect, 'OK' if px >= expect else 'TOO SMALL'))
    return len(tex)


def main():
    dump(OURS, 'OURS (deployed)')
    dump(REF, 'REFERENCE mod')
    return 0


if __name__ == '__main__':
    sys.exit(main())
