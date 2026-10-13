# -*- coding: utf-8 -*-
"""Locate the user's in-game screenshot inside the hair atlas.

If the game samples the atlas the way the mesh's uv0 says, the screenshot should
correspond to a region whose *span* matches the section's uv0 span (~0.98 of the
atlas).  If the game instead magnifies a small island, the best match will sit in
a small window.  Objective, no eyeballing.

usage: _probe_shotlocate.py <shot.png> <atlas.png> [tag]
"""
import sys
from pathlib import Path

import numpy as np
from PIL import Image
from scipy.signal import fftconvolve

OUT = Path(r'E:\Data\source\repos\WpfApp1\tmp\rig-lab\out\uv-crops')


def ncc(a, t):
    """Normalised cross-correlation of template t (h,w) over image a (H,W)."""
    th, tw = t.shape
    H, W = a.shape
    oy, ox = H - th + 1, W - tw + 1
    if oy <= 0 or ox <= 0:
        return None
    t0 = t - t.mean()
    tn = np.sqrt((t0 * t0).sum())
    if tn == 0:
        return None
    ones = np.ones_like(t)
    s = fftconvolve(a, ones[::-1, ::-1], mode='valid')
    s2 = fftconvolve(a * a, ones[::-1, ::-1], mode='valid')
    n = th * tw
    mean = s / n
    var = np.maximum(s2 / n - mean * mean, 0.0)
    num = fftconvolve(a, t0[::-1, ::-1], mode='valid')
    den = np.sqrt(var * n) * tn
    with np.errstate(divide='ignore', invalid='ignore'):
        r = np.where(den > 1e-9, num / den, 0.0)
    return r


def main():
    shot = Image.open(sys.argv[1]).convert('L')
    atlas = Image.open(sys.argv[2]).convert('L')
    tag = sys.argv[3] if len(sys.argv) > 3 else 'hair'
    AW, AH = atlas.size
    print('shot %s  atlas %s' % (shot.size, atlas.size))

    # work on a small template so the FFT stays cheap
    TW = 128
    TH = max(8, int(round(shot.height * TW / shot.width)))
    tmpl_base = shot.resize((TW, TH), Image.LANCZOS)
    A = np.asarray(atlas, np.float64)
    A = A - A.mean()

    results = []
    for mag in (1, 1.25, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10, 12, 16):
        tw, th = int(round(TW * mag)), int(round(TH * mag))
        if th >= AH or tw >= AW:
            continue
        t = np.asarray(tmpl_base.resize((tw, th), Image.LANCZOS), np.float64)
        t = t - t.mean()
        r = ncc(A, t)
        if r is None:
            continue
        iy, ix = np.unravel_index(np.argmax(r), r.shape)
        best = r[iy, ix]
        results.append((best, mag, ix, iy, tw, th))
        print('  mag %5.2f  win %4dx%-4d  ncc %.4f  atlas px (%d,%d)'
              % (mag, tw, th, best, ix, iy))

    results.sort(reverse=True)
    print('BEST: ncc %.4f at magnification %.2f, atlas window x=%d..%d y=%d..%d'
          % (results[0][0], results[0][1], results[0][2], results[0][2] + results[0][4],
             results[0][3], results[0][3] + results[0][5]))

    # contact sheet: the shot, and the best window from each of the top 3 scales
    tiles = [('shot', shot.convert('RGB'))]
    for best, mag, ix, iy, tw, th in results[:3]:
        c = atlas.crop((ix, iy, ix + tw, iy + th)).convert('RGB')
        tiles.append(('mag %.2f  ncc %.3f' % (mag, best), c))
    CELL = 300
    sheet = Image.new('RGB', (CELL * len(tiles), CELL + 22), (28, 32, 38))
    from PIL import ImageDraw
    dr = ImageDraw.Draw(sheet)
    for k, (lab, im) in enumerate(tiles):
        th2 = im.copy(); th2.thumbnail((CELL - 6, CELL - 6))
        sheet.paste(th2, (k * CELL + 3, 3))
        dr.text((k * CELL + 4, CELL + 4), lab, fill=(235, 240, 246))
    p = OUT / ('shotlocate-%s.png' % tag)
    sheet.save(p)
    print('->', p)
    return 0


if __name__ == '__main__':
    sys.exit(main())
