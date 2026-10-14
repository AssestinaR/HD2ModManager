# -*- coding: utf-8 -*-
"""Round 71: what the character will actually look like in HD2.

We simulate HD2's `advanced` PBR:  final = albedo_linear * scene_light  (no
0.30 ceiling).  We take the *shipped* (already-HD2-encoded) albedo of each
variant, linearise it, multiply by a plausible Helldivers scene irradiance and
re-encode for display.  The point is not a physically exact render -- it is to
show, side by side, where each darken setting lands once HD2's lighting is
applied.

usage: _make_ingame_sim.py
"""
import os
import subprocess
import sys
import tempfile

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_asset as A
import hd2_patch as HP
import hd2_texpack as TP

TEXCONV = TP.TEXCONV
OUT = r'E:/tmp/r71-color'

VARIANTS = [
    ('ship / 1.00', r'E:/tmp/round69-final-rollback/9ba626afa44a3aa3.patch_0'),
    ('mul 0.78', r'E:/tmp/r71-dark-078/9ba626afa44a3aa3.patch_0'),
    ('mul 0.65', r'E:/tmp/r71-dark-065/9ba626afa44a3aa3.patch_0'),
    ('mul 0.55', r'E:/tmp/r71-dark-055/9ba626afa44a3aa3.patch_0'),
    ('mul 0.40', r'E:/tmp/r71-dark/9ba626afa44a3aa3.patch_0'),
]

PICK = [
    (0xb76e2a2ab2287e9c, 'face'),
    (0xb3ff8629480ec926, 'body'),
    (0xb26c0af7f21717f5, 'cloth'),
    (0xbe759f65db6aa899, '_01_1'),
]

# Helldivers scene: a bright outdoor sun.  HD2 does NOT clamp albedo*light, so
# the sim lets it blow out exactly like the game does.
LIGHTS = [('overcast 0.8', 0.8), ('sun 1.6', 1.6)]


def _run(cmd):
    p = subprocess.run(cmd, capture_output=True)
    if p.returncode != 0:
        raise RuntimeError('%s\n%s' % (cmd, p.stderr.decode('utf-8', 'replace')))
    return p


def decode_tex(patch, tid, tmp, tag):
    entries, _ = HP.scan_entries(patch)
    for e in entries:
        if e['type_id'] != A.TEXTURE_TYPE_ID or e['file_id'] != tid:
            continue
        pl = HP.entry_payload(e, patch)
        t = A.Texture.read(pl['toc'], pl['gpu'])
        dds = os.path.join(tmp, tag + '.dds')
        open(dds, 'wb').write(t.to_dds())
        png = os.path.join(tmp, tag + '.png')
        if os.path.exists(png):
            os.remove(png)
        _run([TEXCONV, '-nologo', '-y', '-ft', 'png', '-f', 'R8G8B8A8_UNORM_SRGB',
              '-m', '1', '-o', tmp, dds])
        return np.asarray(Image.open(png).convert('RGB'), np.uint8)
    raise SystemExit('tex 0x%016x not in %s' % (tid, patch))


def thumb(img, s=200):
    return np.asarray(Image.fromarray(img).resize((s, s), Image.LANCZOS))


def srgb_to_lin(x):
    x = x / 255.0
    return np.where(x <= 0.04045, x / 12.92, ((x + 0.055) / 1.055) ** 2.4)


def lin_to_srgb(x):
    x = np.clip(x, 0, 1)
    return np.where(x <= 0.0031308, x * 12.92, 1.055 * x ** (1 / 2.4) - 0.055) * 255.0


def label(dr, xy, txt):
    x, y = xy
    dr.rectangle([x, y, x + len(txt) * 7 + 8, y + 16], fill=(0, 0, 0))
    dr.text((x + 4, y + 3), txt, fill=(255, 255, 255))


def build(tmp, light_name, light, fname):
    CW = CH = 200
    pad = 8
    left = 70
    top = 20
    W = left + len(VARIANTS) * (CW + pad) + pad
    H = top + len(PICK) * (CH + pad + 16) + pad
    sheet = Image.new('RGB', (W, H), (18, 18, 22))
    dr = ImageDraw.Draw(sheet)
    for ci, (vn, _) in enumerate(VARIANTS):
        label(dr, (left + ci * (CW + pad) + 4, 3), vn)
    for ri, (tid, tl) in enumerate(PICK):
        y0 = top + ri * (CH + pad + 16)
        label(dr, (4, y0 + CH // 2), tl)
        for ci, (vn, patch) in enumerate(VARIANTS):
            c = thumb(decode_tex(patch, tid, tmp, '%s_%d_%d' % (fname, ri, ci)))
            lit = lin_to_srgb(srgb_to_lin(c.astype(np.float32)) * light).astype(np.uint8)
            sheet.paste(Image.fromarray(lit), (left + ci * (CW + pad), y0))
    p = os.path.join(OUT, fname)
    sheet.save(p)
    print('wrote %s  (%s)  %dx%d' % (p, light_name, W, H))


def main():
    tmp = tempfile.mkdtemp(prefix='ingamesim')
    for nm, lg in LIGHTS:
        build(tmp, nm, lg, 'ingame_%s.png' % nm.split()[0])


if __name__ == '__main__':
    sys.exit(main())
