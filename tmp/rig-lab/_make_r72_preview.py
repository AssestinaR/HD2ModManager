# -*- coding: utf-8 -*-
"""Build the round-72 preview sheet for MourningVeil-RE2310.

Shows, side by side, the bytes that actually ship:
  * the carried Slim torso id  (the whole model)
  * the carried Stocky torso id (same geometry, the other body type)
  * a hidden placeholder unit   (12 verts / 1 tri -- proof the other 20 vanished)

Reads only the shipped patch; nothing is regenerated.
"""
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import hd2_patch as HP
import hd2_unit as U
import _probe_render_patchunit as RP
from _probe_render_npz import W, H, VIEWS, render

PATCH = r'E:/mod/MourningVeil-RE2310/9ba626afa44a3aa3.patch_0'
OUT = r'E:/mod/MourningVeil-RE2310/preview/ship_overview.png'

PANELS = [
    ('0xcae35ee15168145f', 'carried Torso/Slim', 'the whole MourningVeil model'),
    ('0x9841b16544c8fbae', 'carried Torso/Stocky', 'same geometry, other body type'),
    ('0x144d95c1500b73bc', 'hidden 左肩甲/Slim', '12 verts / 1 tri placeholder'),
]
SCALE = 2


def read_unit(patch, unit_id):
    entries, _ = HP.scan_entries(patch)
    for e in entries:
        if e['type_id'] == U.UNIT_ID and e['file_id'] == unit_id:
            pl = HP.entry_payload(e, patch)
            return U.Unit.read(pl['toc'], pl['gpu'])
    raise SystemExit('unit 0x%016x not in %s' % (unit_id, patch))


def main():
    cols = len(PANELS)
    PW, PH = W * SCALE * 2, H * SCALE       # two views per panel
    pad = 14
    img = Image.new('RGB', (pad + cols * (PW + pad), 74 + PH + 28), (24, 26, 31))
    d = ImageDraw.Draw(img)
    d.text((pad, 10), 'MourningVeil-RE2310  shipped patch - what the bytes actually draw',
           fill=(236, 236, 244))
    d.text((pad, 30), 'patch 581484 B / gpu 117215104 B - 82 entries - 22 units '
                      '(2 carried + 20 hidden) - RE-2310 armor archive d923906f8b815987',
           fill=(158, 168, 188))
    d.text((pad, 48), 'each panel: left = front view, right = left view.  '
                      'The helmet is a separate archive and is NOT in this patch.',
           fill=(120, 130, 150))

    for i, (uid, title, sub) in enumerate(PANELS):
        u = read_unit(PATCH, int(uid, 16))
        p, t = RP.gather(u, lod=0, use_transform=True)
        x0 = pad + i * (PW + pad)
        y0 = 74
        for j, vi in enumerate((0, 2)):
            a = render(p, t, VIEWS[vi], bg=(28, 30, 36))
            a = np.clip(a, 0, 255).astype(np.uint8)
            im = Image.fromarray(a).resize((W * SCALE, H * SCALE), Image.NEAREST)
            img.paste(im, (x0 + j * W * SCALE, y0))
        d.text((x0 + 2, y0 + PH + 2), '0x%s' % uid[2:], fill=(210, 220, 235))
        d.text((x0 + 2, y0 + PH + 14), '%s - %s  (%dv %dt)'
               % (title, sub, len(p), len(t)), fill=(150, 165, 190))

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    img.save(OUT)
    print('[written] %s (%dx%d)' % (OUT, img.width, img.height))


if __name__ == '__main__':
    main()
