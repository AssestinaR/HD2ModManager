# -*- coding: utf-8 -*-
"""Texture census for a patch, driven by the materials' own slot tables.

For every Material entry we walk its slots, decode each referenced texture and
report dimensions / DXGI format / alpha statistics, then dump a PNG so the
alpha (and where it is zero) can be inspected.

usage: _probe_texcensus.py <patch_0> <out_prefix>
"""
import os
import struct
import subprocess
import sys
import tempfile
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_asset as A
import hd2_patch as P

TC = Path(r'E:\Data\source\repos\WpfApp1\docs\HD2SDK-CommunityEdition-3.8.0\deps\texconv.exe')
OUT = Path(__file__).resolve().parent / 'out' / 'alpha'


def decode(t, tmp, tag):
    from PIL import Image
    dds = os.path.join(tmp, tag + '.dds')
    with open(dds, 'wb') as f:
        f.write(bytes(t.dds_header) + bytes(t.pixels))
    subprocess.run([str(TC), '-nologo', '-y', '-ft', 'png', '-f', 'R8G8B8A8_UNORM',
                    '-m', '1', '-o', tmp, dds], capture_output=True)
    p = os.path.join(tmp, tag + '.png')
    if not os.path.exists(p):
        return None, None
    im = Image.open(p).convert('RGBA')
    return np.asarray(im, np.uint8), p


def main():
    patch = sys.argv[1]
    prefix = sys.argv[2] if len(sys.argv) > 2 else 'census'
    OUT.mkdir(parents=True, exist_ok=True)
    tmp = tempfile.mkdtemp(prefix='census')
    entries, _ = P.scan_entries(patch)

    tex_by_id, mat_entries = {}, []
    for e in entries:
        if e['type_id'] == A.TEXTURE_TYPE_ID:
            pl = P.entry_payload(e, patch)
            t = A.Texture.read(pl['toc'], pl['gpu'])
            tex_by_id[e['file_id']] = (t, pl)
        elif e['type_id'] == A.MATERIAL_TYPE_ID:
            mat_entries.append(e)

    print('patch %s : %d entries, %d materials, %d textures'
          % (os.path.basename(os.path.dirname(patch)), len(entries),
             len(mat_entries), len(tex_by_id)))

    seen = {}
    for e in mat_entries:
        m = A.Material.read(P.entry_payload(e, patch)['toc'])
        print('-' * 78)
        print('material 0x%016x  parent=%s  ntex=%d' % (e['file_id'], m.parent_name, len(m.tex_ids)))
        for i, (k, tid) in enumerate(zip(m.tex_unks, m.tex_ids)):
            hit = tex_by_id.get(tid)
            where = 'IN-PATCH' if hit else 'base-game'
            if not hit:
                print('   slot %2d key %08x tex 0x%016x  %s' % (i, k, tid, where))
                continue
            t, pl = hit
            if tid in seen:
                a = seen[tid]
                print('   slot %2d key %08x tex 0x%016x  %s %dx%d %s  alpha mean=%.1f zero=%.1f%% (see above)'
                      % (i, k, tid, where, t.width, t.height, t.format_name,
                         a['mean'], a['zero']))
                continue
            arr, png = decode(t, tmp, '%016x' % tid)
            if arr is None:
                print('   slot %2d key %08x tex 0x%016x  DECODE FAIL' % (i, k, tid))
                continue
            al = arr[:, :, 3].astype(np.float64)
            st = dict(mean=float(al.mean()), zero=100.0 * float((al == 0).mean()),
                      full=100.0 * float((al == 255).mean()),
                      mid=100.0 * float(((al > 0) & (al < 255)).mean()))
            seen[tid] = st
            dst = OUT / ('%s_%016x.png' % (prefix, tid))
            import shutil
            shutil.copy2(png, dst)
            print('   slot %2d key %08x tex 0x%016x  %s %dx%d %s  alpha mean=%.1f  ==0:%.1f%%  255:%.1f%%  mid:%.1f%%  -> %s'
                  % (i, k, tid, where, t.width, t.height, t.format_name,
                     st['mean'], st['zero'], st['full'], st['mid'], dst.name))
    return 0


if __name__ == '__main__':
    sys.exit(main())
