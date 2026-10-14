# -*- coding: utf-8 -*-
"""Compose the round-75 before/after evidence into one sheet."""
import os
import sys

from PIL import Image, ImageDraw, ImageFont

PRE = 'E:/mod/MourningVeil-RE2310/preview'
OUT = os.path.join(PRE, 'forearm_align_before_after.png')

CJK_CANDIDATES = [
    'C:/Windows/Fonts/msyh.ttc',      # Microsoft YaHei
    'C:/Windows/Fonts/msyhbd.ttc',
    'C:/Windows/Fonts/simhei.ttf',    # SimHei
    'C:/Windows/Fonts/simsun.ttc',    # SimSun
]


def _font(size):
    for p in CJK_CANDIDATES:
        if os.path.exists(p):
            try:
                return ImageFont.truetype(p, size)
            except Exception:
                continue
    return ImageFont.load_default()


FONT_T = _font(17)
FONT_S = _font(15)

ROWS = [
    ('中段截面 (t/L ~0.69)   橙=我们  蓝=原版  红十字=骨轴',
     'forearm_align_xsection_mid_before.png',
     'forearm_align_xsection_mid_after.png'),
    ('腕部截面 (t/L ~0.99)',
     'forearm_align_xsection_wrist_before.png',
     'forearm_align_xsection_wrist_after.png'),
    ('正视渲染 + 手臂骨链红线',
     'forearm_align_front_before.png',
     'forearm_align_front_after.png'),
]
HEAD = 26
W = 620
PAD = 10


def load(name):
    im = Image.open(os.path.join(PRE, name)).convert('RGB')
    h = int(round(im.height * W / im.width))
    return im.resize((W, h), Image.LANCZOS)


def main():
    tiles = [(load(b), load(a)) for _t, b, a in ROWS]
    heights = [max(b.height, a.height) for b, a in tiles]
    total_h = PAD + sum(HEAD + h + PAD for h in heights)
    total_w = PAD + W + PAD + W + PAD
    sheet = Image.new('RGB', (total_w, total_h), (12, 13, 17))
    dr = ImageDraw.Draw(sheet)
    y = PAD
    for (title, _b, _a), (bi, ai), h in zip(ROWS, tiles, heights):
        dr.text((PAD + 4, y + 4), title, fill=(236, 240, 248), font=FONT_T)
        dr.text((PAD + W + PAD + 4, y + 4), '改前  |  改后', fill=(255, 214, 120),
                font=FONT_T)
        y += HEAD
        sheet.paste(bi, (PAD, y))
        sheet.paste(ai, (PAD + W + PAD, y))
        y += h + PAD
    dr.text((PAD + 4, total_h - 22),
            '左小臂网格校正位移场：d(v) = -alpha(t/L) * limbWeight(v) * e(t)   '
            '（手/手指零位移，肘与上臂不动）', fill=(150, 160, 175), font=FONT_S)
    sheet.save(OUT)
    print('wrote %s (%dx%d)' % (OUT, total_w, total_h))
    return 0


if __name__ == '__main__':
    sys.exit(main())
