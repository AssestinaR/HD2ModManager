# -*- coding: utf-8 -*-
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from PIL import Image, ImageDraw, ImageFont
txt = open('out/lace_report.txt', encoding='utf-8').read().splitlines()
rows = {}
for l in txt:
    p = l.split()
    if len(p) >= 8 and p[1].count('x') == 1 and p[1].split('x')[0].isdigit():
        rows[p[0]] = dict(hw=p[1], slots=int(p[2]), zero=float(p[3].rstrip('%')),
                          full=float(p[4].rstrip('%')), mid=float(p[5].rstrip('%')),
                          tpl=p[7])
th = {}
for l in txt:
    p = l.split()
    if len(p) >= 7 and p[0] in rows and p[2].endswith('%') and p[3].endswith('%'):
        th[p[0]] = (float(p[2].rstrip('%')), float(p[3].rstrip('%')), float(p[4]))
sel = sorted(th, key=lambda k: -th[k][1])
W, RH, TOP, LEFT, RIGHT = 1180, 46, 78, 330, 120
H = TOP + RH * len(sel) + 60
img = Image.new('RGB', (W, H), (26, 27, 32)); d = ImageDraw.Draw(img)
try:
    F = ImageFont.truetype('C:/Windows/Fonts/msyh.ttc', 15)
    FB = ImageFont.truetype('C:/Windows/Fonts/msyh.ttc', 19)
    FS = ImageFont.truetype('C:/Windows/Fonts/msyh.ttc', 13)
except Exception:
    F = FB = FS = ImageFont.load_default()
d.text((18, 14), '源模型 VTF alpha：中间值像素腐蚀存活率', font=FB, fill=(240, 240, 245))
d.text((18, 42), '存活率高 = 真正的半透明蕾丝区；低于 25% 只有 1-2px 抗锯齿细边', font=FS, fill=(160, 165, 175))
plot_w = W - LEFT - RIGHT
for i, k in enumerate(sel):
    e1, e3, ma = th[k]
    y = TOP + i * RH
    d.text((18, y + 4), '%s' % k, font=F, fill=(228, 230, 236))
    d.text((18, y + 22), '%d 槽 · 中间值 %.1f%% · 可见区均值 a=%.0f' % (rows[k]['slots'], rows[k]['mid'], ma),
           font=FS, fill=(150, 155, 168))
    for j, (v, c) in enumerate(((e1, (110, 168, 254)), (e3, (240, 102, 63)))):
        by = y + 8 + j * 15
        d.rectangle([LEFT, by, LEFT + int(plot_w * v / 100.0), by + 11], fill=c)
        d.text((LEFT + int(plot_w * v / 100.0) + 6, by - 2), '%.1f%%' % v, font=FS, fill=(200, 205, 215))
    d.line([LEFT, y + RH - 2, W - RIGHT + 40, y + RH - 2], fill=(48, 50, 58))
x25 = LEFT + int(plot_w * 0.25)
d.line([x25, TOP - 6, x25, H - 52], fill=(150, 150, 150))
d.text((x25 + 4, H - 48), '25%', font=FS, fill=(150, 150, 150))
d.text((18, H - 30), '蓝 = 1px 腐蚀后存活   橙 = 3px 腐蚀后存活', font=FS, fill=(170, 175, 185))
img.save('out/lace_summary.png')
print('-> out/lace_summary.png', img.size)
