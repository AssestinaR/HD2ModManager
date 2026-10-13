# -*- coding: utf-8 -*-
"""Combine the per-texture M5/M6 pairs into one readable evidence sheet."""
import os
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

OUT = Path(__file__).resolve().parent / 'out' / 'texfix'
PAIRS = ['face', 'white_hair', 'hair_ear_cat', 'headdress']
T = 400


def font(sz):
    for p in (r'C:\Windows\Fonts\msyh.ttc', r'C:\Windows\Fonts\segoeui.ttf'):
        if os.path.exists(p):
            try:
                return ImageFont.truetype(p, sz)
            except Exception:
                pass
    return ImageFont.load_default()


def main():
    f_h = font(19)
    f_s = font(15)
    W = T * 2 + 30
    H = 74 + len(PAIRS) * (T + 26)
    sheet = Image.new('RGB', (W, H), (16, 16, 20))
    d = ImageDraw.Draw(sheet)
    d.text((10, 8), 'VTF 贴图读取顺序 bug 修复前后（护甲底色贴图，2048x2048）', font=f_h, fill=(255, 235, 120))
    d.text((10, 34), '左 = M5 实机里的字节（mip 链按 largest-first 切）  右 = M6（mip0 取图像块尾部）',
           font=f_s, fill=(200, 200, 210))
    d.text((10, 53), 'TOC 逐字节相同：材质 / 网格 / 权重 / uv 全未改，只有这 14 张贴图的像素变了',
           font=f_s, fill=(150, 230, 255))
    y = 74
    for lab in PAIRS:
        p = OUT / ('m5m6_%s.png' % lab)
        if not p.exists():
            print('missing', p); continue
        im = Image.open(p).convert('RGB')
        # crop the two tiles out of the source pair image (they start at x=8 / T+22, y=46)
        a = im.crop((8, 46, 8 + 620, 46 + 620)).resize((T, T))
        b = im.crop((642, 46, 642 + 620, 46 + 620)).resize((T, T))
        sheet.paste(a, (10, y + 20))
        sheet.paste(b, (T + 20, y + 20))
        d.rectangle([9, y + 19, 10 + T, y + 19 + T], outline=(255, 60, 60), width=2)
        d.rectangle([T + 19, y + 19, T + 20 + T, y + 19 + T], outline=(60, 220, 255), width=2)
        d.text((10, y + 2), '%s' % lab, font=f_s, fill=(255, 200, 120))
        y += T + 26
    out = OUT / 'M6_before_after.png'
    sheet.save(out)
    print('wrote', out, sheet.size)


if __name__ == '__main__':
    main()
