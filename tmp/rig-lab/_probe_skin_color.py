# -*- coding: utf-8 -*-
"""Zoom in on the SKIN groups: what does the source author, what do we ship,
and how does the L4D2 NekoToon shader turn albedo into final on-screen colour?

The NekoToon formula (from the source VMTs) is:
    final = albedo * lightwarp(lighting) + matcap * matcaptint
so the *albedo* is only the first term.  A pure multiplication can only DARKEN
or keep albedo; it can never brighten.  So if the game looks WASHED OUT (lighter
than source), and our albedo == source albedo byte for byte, the lightening
must come from either the lightwarp texture or HD2's own tonemap/exposure.

usage: _probe_skin_color.py
"""
import os, sys, tempfile, json
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import hd2_patch as HP
import hd2_vtf as V
import hd2_texpack as TP
import hd2_source as S
import hd2_asset as A
from PIL import Image

OUT = r'E:/tmp/r71-color'
os.makedirs(OUT, exist_ok=True)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from _probe_color_faithful import decode_dds, vtf_rgba, lum

PACKS = dict(addons=r'E:/tmp/lilium', model_vpk='Lilium_Sancta_Zoey.vpk', packs=[
    'Lilium_Sancta基础材质A1主体.vpk', 'Lilium_Sancta基础材质B1服装色白.vpk',
    'Lilium_Sancta基础材质C2金属银色系.vpk', 'Lilium_Sancta基础材质D1身体描边.vpk',
    'Lilium_Sancta基础材质E1发色银灰.vpk', 'Lilium_Sancta基础材质F1瞳色银灰.vpk',
    'Lilium_Sancta_Zoey.vpk'])


def main():
    packs = S.Packs(**PACKS)
    tmp = tempfile.mkdtemp(prefix='skin')

    print('=== SKIN / BODY source textures and the lightwarp chain ===\n')
    for stem in ('shinano_body', 'shinano_face', 'facewarp', 'basewarp', 'skinwarp',
                 'shinano_face_e'):
        raw = packs.vtf_bytes(stem)
        if raw is None:
            print('%-18s <not in packs>' % stem); continue
        v = V.VTF.read(raw)
        a = vtf_rgba(v, tmp, 'sk_' + stem)
        if a is None:
            print('%-18s %dx%d %s <undecodable>' % (stem, v.width, v.height, v.format_name)); continue
        rgb = a[:, :, :3].astype(np.float64)
        print('%-18s %5dx%-5d %-12s  mean %6.1f %6.1f %6.1f  L %6.1f  minL %5.1f  maxL %5.1f'
              % (stem, v.width, v.height, v.format_name,
                 rgb[:, :, 0].mean(), rgb[:, :, 1].mean(), rgb[:, :, 2].mean(),
                 lum(a).mean(), lum(a).min(), lum(a).max()))
        # what a pure multiply of body x warp looks like at a few light levels
        Image.fromarray(a[:, :, :3], 'RGB').resize((384, 384), Image.LANCZOS).save(
            os.path.join(OUT, 'src_%s.png' % stem))

    # ---- the lightwarp multiplier curve --------------------------------
    print('\n=== how the source NekoToon lightwarp maps lighting -> albedo scale ===')
    for stem in ('facewarp', 'basewarp', 'skinwarp'):
        raw = packs.vtf_bytes(stem)
        if raw is None:
            continue
        v = V.VTF.read(raw)
        a = vtf_rgba(v, tmp, 'w_' + stem)
        if a is None:
            continue
        # the warp is a 1D-ish LUT laid out horizontally usually: sample the
        # middle row across x
        row = a[a.shape[0] // 2, :, :3].astype(np.float64)
        print('%-12s  x=0 %s  x=25%% %s  x=50%% %s  x=75%% %s  x=100%% %s'
              % (stem,
                 row[0].round(0).astype(int), row[len(row) // 4].round(0).astype(int),
                 row[len(row) // 2].round(0).astype(int),
                 row[3 * len(row) // 4].round(0).astype(int), row[-1].round(0).astype(int)))


if __name__ == '__main__':
    main()
