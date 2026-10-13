# -*- coding: utf-8 -*-
"""Export every NekoToon matcap texture used by the model + the flat 1x1 bases.

usage: _probe_matcap.py   -> out/matcap/<name>.png , out/matcap/_stats.txt
"""
import os, sys, subprocess
from pathlib import Path
import numpy as np
sys.path.insert(0, str(Path(__file__).resolve().parent))
import hd2_source as S
import hd2_vtf as V
import hd2_texpack as TP
from PIL import Image

SDK = r'E:/Data/source/repos/WpfApp1/docs/HD2SDK-CommunityEdition-3.8.0'
TEXCONV = os.path.join(SDK, 'deps', 'texconv.exe')
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'out', 'matcap')
os.makedirs(OUT, exist_ok=True)

packs = S.Packs()
stems = ['Matcap1', 'Matcap8', '6', '11', 'Ornament_metal_Underwear',
         'Ornament_metal_Underwear_N', '_4', 'White', 'Shinano_face',
         'HeadDress_White01_CatEars', 'Lace_White01_CatEars', 'Bra_White01_Bra_Inner',
         'Glove_White01_WristCuffs', 'Dress_White01_Dress_Corset',
         'Lower_White01_Underwear', 'Shoes_White01_Highheels', 'Shoes_02',
         'Shinano_body', 'Shinano_hair_ear']
lines = []
for stem in stems:
    raw = packs.vtf_bytes(stem)
    if raw is None:
        lines.append('%-34s MISSING' % stem)
        continue
    v = V.VTF.read(raw)
    lines.append('%-34s %-12s %dx%d mips=%d' % (stem, v.format_name, v.width, v.height, v.mip_count))
    dxgi = TP.VTF_TO_WRAPPER_DXGI.get(v.hi_format)
    if dxgi is None:
        lines.append('        (format %d has no wrapper mapping)' % v.hi_format)
        continue
    dds = os.path.join(OUT, stem + '.dds')
    with open(dds, 'wb') as f:
        f.write(TP._wrapper_dds(v, dxgi, v.width, v.height, v.mips[0], None))
    p = subprocess.run([TEXCONV, '-nologo', '-y', '-ft', 'png', '-f', 'R8G8B8A8_UNORM',
                        '-m', '1', '-o', OUT, dds], capture_output=True)
    png = os.path.join(OUT, stem + '.png')
    if not os.path.exists(png):
        lines.append('        texconv failed: %s' % p.stderr.decode('utf-8','replace')[:200])
        continue
    a = np.asarray(Image.open(png).convert('RGBA'), np.int32)
    rgb = a[:, :, :3]
    lum = rgb.mean(axis=2)
    lines.append('        RGB mean=%s  lum min=%.1f max=%.1f std=%.1f  p5=%.1f p50=%.1f p95=%.1f'
                 % (rgb.reshape(-1,3).mean(axis=0).round(1).tolist(),
                    lum.min(), lum.max(), lum.std(),
                    np.percentile(lum,5), np.percentile(lum,50), np.percentile(lum,95)))
    if v.width <= 8:
        lines.append('        pixels: %s' % rgb.reshape(-1,3)[:8].tolist())
    lines.append('        alpha  min=%.0f max=%.0f mean=%.1f' % (a[:,:,3].min(), a[:,:,3].max(), a[:,:,3].mean()))
    os.remove(dds)
txt = '\n'.join(lines)
print(txt)
open(os.path.join(OUT, '_stats.txt'), 'w', encoding='utf-8').write(txt)
