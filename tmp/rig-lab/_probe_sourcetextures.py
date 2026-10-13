# -*- coding: utf-8 -*-
"""Decode the SOURCE textures straight out of the L4D2 addon VPK.

Read-only.  The point: our shipped base colour maps are all "green + white"
bands, which no clothing albedo looks like.  Either the source really is like
that (and the VMT points somewhere unexpected) or our VTF -> BC7 path is
mangling them.  Decoding the originals with the same texconv but straight from
the VTF tells us which.

Prints, per source material slot: the VMT path, `$basetexture`, the VTF's
high-res format, and the decoded image stats; then builds a contact sheet.
"""
import os
import subprocess
import sys
import tempfile
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_source as S
import hd2_style as ST
import hd2_texpack as TP
import hd2_vtf as V

SDK = Path(r'E:\Data\source\repos\WpfApp1\docs\HD2SDK-CommunityEdition-3.8.0')
TEXCONV = SDK / 'deps' / 'texconv.exe'
OUT = Path(r'E:\Data\source\repos\WpfApp1\tmp\rig-lab\out\source-textures')


def dds_to_rgb(hdr, px, tmpdir, tag):
    dds = os.path.join(tmpdir, tag + '.dds')
    with open(dds, 'wb') as f:
        f.write(bytes(hdr) + bytes(px))
    subprocess.run([str(TEXCONV), '-nologo', '-y', '-ft', 'png',
                    '-f', 'R8G8B8A8_UNORM', '-m', '1', '-o', tmpdir, dds],
                   capture_output=True)
    png = os.path.join(tmpdir, tag + '.png')
    if not os.path.exists(png):
        return None, None
    from PIL import Image
    im = Image.open(png).convert('RGBA')
    mean = np.asarray(im, np.float64).reshape(-1, 4).mean(axis=0)
    return im.convert('RGB'), mean


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    tmp = tempfile.mkdtemp(prefix='srctex')
    packs = S.Packs()
    vpk = S.ADDONS + S.MODEL_VPK
    _p, mdl, _v, _t, _s, _sh = ST.load_source(vpk, 'survivor_teenangst')
    names, cds, _skin = S.load_material_slots(mdl)
    print('source has %d material slots' % len(names))

    tiles = []
    for i, nm in enumerate(names):
        text = packs.vmt_text(nm)
        vmt = S.parse_vmt(text) if text else {}
        base = vmt.get('$basetexture')
        bump = vmt.get('$bumpmap')
        vtf = None
        fmt = dims = None
        if base:
            try:
                vtf = packs.vtf_bytes(base)
            except Exception as e:  # noqa: BLE001
                vtf = None
        if vtf:
            v = V.VTF.read(vtf)
            fmt = 'vtf_fmt=%d %dx%d mips=%d' % (v.hi_format, v.width, v.height,
                                                len(v.mips))
            try:
                hdr, px = TP.vtf_to_dds(vtf, TP.DXGI_BC7_SRGB, tag='s%02d' % i)
                rgb, mean = dds_to_rgb(hdr, px, tmp, 's%02d' % i)
            except Exception as e:  # noqa: BLE001
                rgb, mean = None, None
                fmt += '  ENCODE FAIL: %s' % e
        else:
            rgb, mean = None, None
        print('%2d %-46s base=%-40s %s' % (i, nm, base, fmt))
        if mean is not None:
            print('        mean RGB=(%5.1f,%5.1f,%5.1f) A=%5.1f'
                  % (mean[0], mean[1], mean[2], mean[3]))
        if bump:
            print('        $bumpmap=%s' % bump)
        label = '%d %s' % (i, (base or '?').rsplit('\\', 1)[-1])
        tiles.append((label, rgb, fmt or 'no vtf'))

    from PIL import Image, ImageDraw
    CELL, COLS = 300, 4
    rows = (len(tiles) + COLS - 1) // COLS
    sheet = Image.new('RGB', (COLS * CELL, rows * (CELL + 46)), (28, 32, 38))
    dr = ImageDraw.Draw(sheet)
    for k, (label, im, st) in enumerate(tiles):
        cx, cy = (k % COLS) * CELL, (k // COLS) * (CELL + 46)
        if im is not None:
            th = im.copy()
            th.thumbnail((CELL - 8, CELL - 8))
            sheet.paste(th, (cx + 4, cy + 4))
        else:
            dr.rectangle([cx + 4, cy + 4, cx + CELL - 4, cy + CELL - 4],
                         outline=(150, 80, 80))
            dr.text((cx + 10, cy + CELL // 2), 'NO IMAGE', fill=(240, 140, 140))
        dr.text((cx + 4, cy + CELL + 2), label[:42], fill=(235, 240, 246))
        dr.text((cx + 4, cy + CELL + 20), str(st)[:46], fill=(150, 205, 160))
    p = OUT / 'source-textures.png'
    sheet.save(p)
    print()
    print('contact sheet ->', p)
    return 0


if __name__ == '__main__':
    sys.exit(main())
