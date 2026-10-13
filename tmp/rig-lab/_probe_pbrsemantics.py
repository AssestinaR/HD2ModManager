# -*- coding: utf-8 -*-
"""Compare OUR material slot->texture bindings against the VANILLA RE-2310 armour.

Read-only.  Question being answered: M1 fixed the slot/ID table layout and the
game showed *zero* change, so the table is not the visible cause.  The next
cheapest hypothesis is the TEXTURE CONTENT itself -- in particular the 4x4
`shared PBR` placeholder, which measures (R=0, G=158, B=0).  If the vanilla
armour's PBR map carries a non-zero AO channel in the same slot, our placeholder
is starving the shader and *no* reference fix can ever help.

Prints, for both sides:
    material -> parent template, slot keys, slot ids
    each slot id -> decoded texture format/size/mean RGBA
"""
import os
import struct
import subprocess
import sys
import tempfile
from collections import OrderedDict
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_asset as A
import hd2_patch as P

SDK = Path(r'E:\Data\source\repos\WpfApp1\docs\HD2SDK-CommunityEdition-3.8.0')
TEXCONV = SDK / 'deps' / 'texconv.exe'
OURS = sys.argv[1] if len(sys.argv) > 1 else \
    r'E:/mod/Lunalice-RE2310-M1/9ba626afa44a3aa3.patch_0'
VANILLA = Path(r'E:\tmp\hd2-mgroot\re2310-full')

SLOT_NAMES = ('PBR', 'Base Colour', 'Normal')


# ------------------------------------------------------------------ decoding
def dds_mean(dds_bytes, gpu):
    tmp = tempfile.mkdtemp(prefix='pbrsem')
    dds = os.path.join(tmp, 't.dds')
    with open(dds, 'wb') as f:
        f.write(dds_bytes + gpu)
    p = subprocess.run([str(TEXCONV), '-nologo', '-y', '-ft', 'png',
                        '-f', 'R8G8B8A8_UNORM', '-m', '1', '-o', tmp, dds],
                       capture_output=True)
    png = os.path.join(tmp, 't.png')
    if not os.path.exists(png):
        return None, p.stdout.decode('utf-8', 'replace')[:50]
    from PIL import Image
    arr = np.asarray(Image.open(png).convert('RGBA'), np.float64).reshape(-1, 4)
    return arr.mean(axis=0), None


def tex_stats(toc_bytes, gpu_bytes):
    try:
        t = A.Texture.read(toc_bytes, gpu_bytes)
    except Exception as e:  # noqa: BLE001
        return None, 'parse: %s' % e
    if not gpu_bytes:
        return ('%s %dx%d  (no GPU payload)' % (t.format_name, t.width, t.height)), None
    mean, err = dds_mean(bytes(t.dds_header), gpu_bytes)
    if mean is None:
        return '%s %dx%d  decode-failed %s' % (t.format_name, t.width, t.height, err), None
    return ('%s %dx%d  mean=(%6.1f,%6.1f,%6.1f,%6.1f)'
            % (t.format_name, t.width, t.height, mean[0], mean[1], mean[2], mean[3])), None


# ------------------------------------------------------------------ our side
def our_materials():
    entries, raw = P.scan_entries(OURS)
    tex_by_id = {}
    for e in entries:
        if e['type_id'] == A.TEXTURE_TYPE_ID:
            pl = P.entry_payload(e, OURS)
            tex_by_id[e['file_id']] = (pl['toc'], pl['gpu'])
    out = []
    for e in entries:
        if e['type_id'] != A.MATERIAL_TYPE_ID:
            continue
        pl = P.entry_payload(e, OURS)
        m = A.Material.read(pl['toc'])
        out.append((e['file_id'], m, tex_by_id))
    return out


def vanilla_materials():
    tex_by_id = {}
    for f in sorted((VANILLA / 'texture').glob('*.toc')):
        fid = int(f.stem.split('_')[1], 16)
        gpu_p = f.with_suffix('.gpu_resources')
        tex_by_id[fid] = (f.read_bytes(),
                          gpu_p.read_bytes() if gpu_p.exists() else b'')
    out = []
    for f in sorted((VANILLA / 'material').glob('*.toc')):
        fid = int(f.stem.split('_')[1], 16)
        m = A.Material.read(f.read_bytes())
        out.append((fid, m, tex_by_id))
    return out


def report(title, mats):
    print('=' * 100)
    print(title)
    print('=' * 100)
    seen = OrderedDict()
    for fid, m, tex_by_id in mats:
        print('--- material 0x%016x  parent=%-14s ntex=%d nvar=%d'
              % (fid, m.parent_name, len(m.tex_ids), len(m.variables)))
        for i, tid in enumerate(m.tex_ids):
            slot = SLOT_NAMES[i] if i < len(SLOT_NAMES) else 'slot%d' % i
            key = m.tex_unks[i] if i < len(m.tex_unks) else 0
            tag = '%s  key=0x%08x  id=0x%016x' % (slot, key, tid)
            if tid not in seen:
                if tid in tex_by_id:
                    t, _ = tex_stats(*tex_by_id[tid])
                    seen[tid] = t
                else:
                    seen[tid] = 'NOT IN PACKAGE'
            print('      %-46s %s' % (tag, seen[tid]))
    print()


def main():
    report('OURS  (M1, deployed)', our_materials())
    report('VANILLA RE-2310', vanilla_materials())
    return 0


if __name__ == '__main__':
    sys.exit(main())
