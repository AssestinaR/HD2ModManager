# -*- coding: utf-8 -*-
"""Does the source model really carry *semi*-transparent lace, or only a hard
cut-out with anti-aliased edges?

L4D2 draws a part's cut-out from the **VTF alpha channel**:
    $alphatest 1  -> hard cut-out (binary; the soft edge is just AA)
    $translucent 1 -> real alpha blending (partial coverage)

HD2's `alphaclip` can only do a hard cut-out (the wiki says "no capabilities
for partial transparency"), so the question matters: if the source really has
broad *intermediate* alpha regions, an alphaclip port will thin them out.

Two measurements, per group:
  1. histogram    -- share of texels at 0 / 255 / strictly between
  2. thickness    -- the share of "intermediate" texels that survive a 1-px and
                     a 3-px erosion.  A pure anti-aliasing rim is 1-2 px wide
                     and vanishes immediately; real partial coverage is a
                     *region* and survives.

Read-only.  Writes tmp/rig-lab/out/lace_report.txt and out/lace/*.png
"""
import os
import sys
from collections import OrderedDict

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

from PIL import Image
from scipy import ndimage

import fix_appearance as FA
import hd2_source as S
import hd2_style as ST

OUTDIR = os.path.join(HERE, 'out', 'lace')
REPORT = os.path.join(HERE, 'out', 'lace_report.txt')

# "intermediate" = neither the fully-cut hole nor the fully-solid cloth, with a
# margin so that pure dithering/BC noise at the ends is not counted
LO, HI = 16, 239

lines = []


def p(s=''):
    lines.append(s)
    print(s, flush=True)


def _erode_frac(mask, it):
    if not mask.any():
        return 0.0
    e = ndimage.binary_erosion(mask, iterations=it)
    return float(e.sum()) / float(mask.sum())


def main():
    os.makedirs(OUTDIR, exist_ok=True)
    vpk = S.ADDONS + S.MODEL_VPK
    _md, mdl, _v, tris4, _s, _sh = ST.load_source(vpk, 'survivor_teenangst')
    tris4 = np.asarray(tris4, np.int64)
    names, _cds, _row = S.load_material_slots(mdl)
    packs = S.Packs()
    groups = FA.analyse(tris4, names, packs)

    import json
    man = json.load(open(r'E:\mod\Lunalice-RE2310-M8\lunalice-m8.manifest.json',
                         encoding='utf-8'))
    m8g = {d['group']: d for d in man['groups']}

    import tempfile
    tmpdir = tempfile.mkdtemp(prefix='lace')

    rows = []
    p('=' * 118)
    p('SOURCE ALPHA CENSUS -- is there real semi-transparency, or only a cut-out?')
    p('=' * 118)
    p('%-30s %7s %6s %7s %7s %7s %7s  %-9s %s'
      % ('group (= $basetexture)', 'WxH', 'slots', 'a=0%', 'a=255%', 'mid%',
         'mid/vis%', 'HD2', 'why'))
    p('-' * 118)

    for base, g in groups.items():
        stem = os.path.basename(base or 'untextured')
        arr, vtf = FA.decode_vtf(packs, stem, tmpdir, 'l_' + stem)
        why = []
        if g['why_alpha']:
            why.append(','.join(sorted({w.split('(')[-1].rstrip(')')
                                        for w in g['why_alpha']})))
        row = dict(group=stem, slots=len(g['slots']), tris=g['tris'],
                   template=m8g.get(stem, {}).get('template'),
                   matcap=m8g.get(stem, {}).get('matcapDominant'),
                   why=' '.join(why))
        if arr is None:
            p('%-30s %7s %6d %7s %7s %7s %7s  %-9s %s'
              % (stem, '-', len(g['slots']), '-', '-', '-', '-',
                 row['template'], 'no decodable VTF'))
            rows.append(row)
            continue
        a = arr[:, :, 3].astype(np.int32)
        h, w = a.shape
        tot = float(a.size)
        z = float((a == 0).sum()) / tot
        f = float((a == 255).sum()) / tot
        mid = (a > 0) & (a < 255)
        midf = float(mid.sum()) / tot
        vis = float((a > 0).sum()) + 1e-9
        mid_vis = float(mid.sum()) / vis
        e1 = _erode_frac(mid, 1)
        e3 = _erode_frac(mid, 3)
        row.update(shape='%dx%d' % (w, h), zero=z, full=f, mid=midf,
                   midOfVisible=mid_vis, erode1=e1, erode3=e3,
                   meanAlphaVisible=float(a[a > 0].mean()) if (a > 0).any() else 0.0,
                   strictMid=float(((a >= LO) & (a <= HI)).sum()) / tot)
        p('%-30s %7s %6d %6.1f%% %6.1f%% %6.2f%% %6.2f%%  %-9s %s'
          % (stem, row['shape'], len(g['slots']), z * 100, f * 100, midf * 100,
             mid_vis * 100, row['template'], row['why']))
        rows.append(row)

    p('-' * 118)
    p('mid%%      = texels strictly between 0 and 255, over the whole image')
    p('mid/vis%%  = the same, but over only the texels the part actually shows (a>0)')
    p('erodeN    = share of the "intermediate" texels that survive an N-px erosion:')
    p('            0%% means the soft pixels are a 1-2 px anti-aliasing rim only;')
    p('            a high value means a real semi-transparent REGION.')

    # ---- thickness table for the alpha-carrying groups --------------------
    p('')
    p('=' * 118)
    p('THICKNESS TEST (only groups whose texture carries alpha at all)')
    p('=' * 118)
    p('%-30s %8s %8s %9s %9s  %s'
      % ('group', 'mid tex', 'erode 1', 'erode 3', 'mean a|vis', 'verdict'))
    p('-' * 118)
    verdicts = OrderedDict()
    for r in rows:
        if 'mid' not in r or r['mid'] == 0:
            continue
        n_mid = int(r['mid'] * 0)  # placeholder
        if r['erode1'] >= 0.55 and r['erode3'] >= 0.25:
            v = 'REAL semi-transparent region'
        elif r['erode1'] >= 0.25:
            v = 'partial: soft rim + some area'
        else:
            v = 'cut-out only (soft rim is anti-aliasing)'
        verdicts[r['group']] = v
        p('%-30s %7.2f%% %7.1f%% %8.1f%% %9.1f  %s'
          % (r['group'], r['mid'] * 100, r['erode1'] * 100, r['erode3'] * 100,
             r['meanAlphaVisible'], v))
    p('-' * 118)

    # ---- $translucent vs $alphatest per group -----------------------------
    p('')
    p('=' * 118)
    p('WHICH MECHANISM THE SOURCE ASKS FOR (per group, from the 92 slots)')
    p('=' * 118)
    for base, g in groups.items():
        stem = os.path.basename(base or 'untextured')
        tr = [s for s in g['why_alpha'] if '$translucent' in s]
        at = [s for s in g['why_alpha'] if '$alphatest' in s]
        p('  %-30s  translucent %2d slot(s) %-34s alphatest %2d slot(s)'
          % (stem, len(tr), (', '.join(tr[:3])[:34] if tr else '-'), len(at)))

    # ---- pictures ---------------------------------------------------------
    SZ = 360
    tiles = []
    for r in rows:
        if 'mid' not in r:
            continue
        stem = r['group']
        arr, vtf = FA.decode_vtf(packs, stem, tmpdir, 'p_' + stem)
        a = arr[:, :, 3].astype(np.int32)
        mid = ((a > 0) & (a < 255)).astype(np.uint8) * 255
        e1 = (ndimage.binary_erosion(mid > 0, iterations=1).astype(np.uint8)) * 255
        e3 = (ndimage.binary_erosion(mid > 0, iterations=3).astype(np.uint8)) * 255
        t = [Image.fromarray(arr[:, :, :3], 'RGB'),
             Image.fromarray(np.dstack([a.astype(np.uint8)] * 3), 'RGB'),
             Image.fromarray(np.dstack([mid] * 3), 'RGB'),
             Image.fromarray(np.dstack([np.maximum(e1, e3 // 2)] * 3), 'RGB')]
        sheet = Image.new('RGB', (SZ * 4, SZ + 18), (24, 24, 28))
        for i, im in enumerate(t):
            sheet.paste(im.resize((SZ, SZ), Image.LANCZOS), (i * SZ, 18))
        from PIL import ImageDraw
        d = ImageDraw.Draw(sheet)
        d.text((4, 3), '%s   src RGB | alpha | intermediate(1..254) | survives erosion'
               % stem, fill=(230, 230, 230))
        path = os.path.join(OUTDIR, 'sheet_%s.png' % stem)
        sheet.save(path)
        tiles.append((stem, sheet))

    with open(REPORT, 'w', encoding='utf-8') as f:
        f.write('\n'.join(lines))
    print('-> %s' % REPORT)
    print('-> %s' % OUTDIR)


if __name__ == '__main__':
    main()
