# -*- coding: utf-8 -*-
"""Round 71: contact sheet comparing the albedo textures at several darken
settings (mul 0.40 / 0.65 / 0.78, gamma 1.8 / 2.6) against the current ship.

Layout: one row per texture, one column per variant.  A second sheet renders a
simulated "what the eye sees" crop: albedo multiplied by a mid-grey HD2-ish
light (no 0.30 ceiling), so the difference in overall brightness is obvious.

usage: _make_dark_compare.py
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
os.makedirs(OUT, exist_ok=True)

ROLLBACK = r'E:/tmp/round69-final-rollback/9ba626afa44a3aa3.patch_0'
VARIANTS = [
    ('ship (1.00)', ROLLBACK),
    ('mul 0.78', r'E:/tmp/r71-dark-078/9ba626afa44a3aa3.patch_0'),
    ('mul 0.65', r'E:/tmp/r71-dark-065/9ba626afa44a3aa3.patch_0'),
    ('mul 0.40', r'E:/tmp/r71-dark/9ba626afa44a3aa3.patch_0'),
    ('comp 0.55', r'E:/tmp/r71-dark-c0.55/9ba626afa44a3aa3.patch_0'),
    ('comp 0.45', r'E:/tmp/r71-dark-c0.45/9ba626afa44a3aa3.patch_0'),
    ('gam 2.6', r'E:/tmp/r71-dark-g26/9ba626afa44a3aa3.patch_0'),
]

# (tex id, short label) — the ones the user actually looks at
PICK = [
    (0xb76e2a2ab2287e9c, 'manuka_face'),
    (0xb3ff8629480ec926, 'manuka_body'),
    (0xb26c0af7f21717f5, 'mat_dress_cloth'),
    (0xb99f743a90cbb00c, 'glove_white'),
    (0xbd52ac1f8b242375, '_13'),
]


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


def full_thumb(img, sw=256, sh=256):
    """Whole texture squashed so the user sees the real texel distribution."""
    return np.asarray(Image.fromarray(img).resize((sw, sh), Image.LANCZOS))


# kept for API compat
def center_crop(img, sw=512, sh=256):
    return full_thumb(img, sw, sh)


def label(dr, xy, txt, fg=(255, 255, 255)):
    x, y = xy
    dr.rectangle([x, y, x + len(txt) * 7 + 8, y + 16], fill=(0, 0, 0))
    dr.text((x + 4, y + 3), txt, fill=fg)


def main():
    tmp = tempfile.mkdtemp(prefix='darkcmp')
    CW, CH = 256, 256
    pad = 8
    left = 110
    top = 22

    # ---- sheet 1: raw albedo crops -------------------------------------
    W = left + len(VARIANTS) * (CW + pad) + pad
    H = top + len(PICK) * (CH + pad + 18) + pad
    sheet = Image.new('RGB', (W, H), (22, 22, 26))
    dr = ImageDraw.Draw(sheet)
    for ci, (name, patch) in enumerate(VARIANTS):
        label(dr, (left + ci * (CW + pad) + 4, 4), name)
    for ri, (tid, tl) in enumerate(PICK):
        y0 = top + ri * (CH + pad + 18)
        label(dr, (4, y0 + CH // 2), '%s' % tl)
        for ci, (name, patch) in enumerate(VARIANTS):
            img = decode_tex(patch, tid, tmp, 's1_%d_%d' % (ri, ci))
            crop = center_crop(img, CW, CH)
            sheet.paste(Image.fromarray(crop), (left + ci * (CW + pad), y0))
    p1 = os.path.join(OUT, 'dark_compare_raw.png')
    sheet.save(p1)
    print('wrote %s  %dx%d' % (p1, W, H))

    # ---- sheet 2: simulated render (light ~1.6, no 0.30 ceiling) -------
    LG = 1.6
    W2 = left + len(VARIANTS) * (CW + pad) + pad
    H2 = top + len(PICK) * (CH + pad + 18) + pad
    sheet2 = Image.new('RGB', (W2, H2), (0, 0, 0))
    dr2 = ImageDraw.Draw(sheet2)
    for ci, (name, patch) in enumerate(VARIANTS):
        label(dr2, (left + ci * (CW + pad) + 4, 4), name)
    for ri, (tid, tl) in enumerate(PICK):
        y0 = top + ri * (CH + pad + 18)
        label(dr2, (4, y0 + CH // 2), '%s' % tl)
        for ci, (name, patch) in enumerate(VARIANTS):
            c = center_crop(decode_tex(patch, tid, tmp, 's2_%d_%d' % (ri, ci)), CW, CH)
            lit = np.clip(c.astype(np.float32) * LG, 0, 255).astype(np.uint8)
            sheet2.paste(Image.fromarray(lit), (left + ci * (CW + pad), y0))
    p2 = os.path.join(OUT, 'dark_compare_lit.png')
    sheet2.save(p2)
    print('wrote %s  %dx%d' % (p2, W2, H2))

    # ---- numbers -------------------------------------------------------
    print('')
    print('%-16s %s' % ('texture', '  '.join('%9s' % n for n, _ in VARIANTS)))
    for tid, tl in PICK:
        vals = []
        for name, patch in VARIANTS:
            c = center_crop(decode_tex(patch, tid, tmp, 'n_%016x_%s' % (tid, name[:4])), CW, CH)
            v = float((0.2126 * c[:, :, 0] + 0.7152 * c[:, :, 1] + 0.0722 * c[:, :, 2]).mean())
            vals.append(v)
        print('%-16s %s' % (tl, '  '.join('%9.1f' % v for v in vals)))


if __name__ == '__main__':
    sys.exit(main())
