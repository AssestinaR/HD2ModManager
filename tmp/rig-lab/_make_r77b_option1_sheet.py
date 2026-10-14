# -*- coding: utf-8 -*-
"""第 77b 轮证据图（PIL 版）：把 4e「关节残差平移」接进 arm_scratch（选项 1）后的手臂指标对比。

数据来自 `_probe_arm_npz_centerline.py --radius 0.30 --side left`，五组 npz 同口径：
  OFF = r77-arm-off3  （= 第 76b 轮出货状态，4e 关闭）
  ON  = r77-arm-on    （4e 开，默认系数 .55/0）
  B   = r77-arm-B     （4e 开，up=1.0 perp=0.0）
  C   = r77-arm-C     （4e 开，up=0.55 perp=1.0）
  A   = r77-arm-A     （4e 开，up=1.0 perp=1.0）
"""
import os

from PIL import Image, ImageDraw, ImageFont


def _font(size, bold=False):
    cands = (['C:/Windows/Fonts/msyhbd.ttc', 'C:/Windows/Fonts/msyh.ttc']
             if bold else ['C:/Windows/Fonts/msyh.ttc', 'C:/Windows/Fonts/msyhbd.ttc'])
    for p in cands:
        if os.path.exists(p):
            try:
                return ImageFont.truetype(p, size)
            except Exception:
                continue
    return ImageFont.load_default()


groups = [
    ('OFF', 'OFF\n(第76b出货)'),
    ('ON', 'ON\n默认 .55/0'),
    ('B', 'B\n1.0/0'),
    ('C', 'C\n.55/1.0'),
    ('A', 'A\n1.0/1.0'),
]
fore_off = {'OFF': 46.5, 'ON': 56.6, 'B': 83.4, 'C': 128.7, 'A': 143.6}
fore_rms = {'OFF': 5.06, 'ON': 8.74, 'B': 8.39, 'C': 3.35, 'A': 5.25}
upper_tilt = {'OFF': 8.55, 'ON': 6.29, 'B': 12.16, 'C': 22.85, 'A': 24.38}

C_OK = (47, 158, 110)
C_BAD = (192, 57, 43)
C_REF = (127, 140, 141)
C_TXT = (34, 34, 34)
C_GRID = (226, 230, 234)
C_BG = (255, 255, 255)

W, H = 1640, 660
img = Image.new('RGB', (W, H), C_BG)
d = ImageDraw.Draw(img)

f_title = _font(28, True)
f_sub = _font(17)
f_panel = _font(21, True)
f_lab = _font(15)
f_val = _font(17, True)
f_foot = _font(17)

d.text((W // 2, 18), '第 77b 轮：选项 1（把 4e 关节残差平移接进 arm_scratch）—— 左臂指标对比',
       font=f_title, fill=C_TXT, anchor='ma')
d.text((W // 2, 56), '五组同口径（npz 中轴线探针 --radius 0.30）；基线 = OFF（第 76b 轮出货状态）',
       font=f_sub, fill=(90, 90, 90), anchor='ma')

PX0, PX1 = 62, W - 46
PY0, PY1 = 172, 455
gap = 46
pw = (PX1 - PX0 - 2 * gap) // 3

panels = [
    ('① 小臂管离 HD2 小臂轴的距离（mm，越小越好）', fore_off, '%.1f', '虚线 = OFF 基线 %.1f mm'),
    ('② 小臂直线度 rms（mm，越小越好）', fore_rms, '%.2f', '虚线 = OFF 基线 %.2f mm'),
    ('③ 大臂倾斜角（度，越小越好）', upper_tilt, '%.2f', '虚线 = OFF 基线 %.2f°'),
]

for pi, (title, data, fmt, ref_label) in enumerate(panels):
    x0 = PX0 + pi * (pw + gap)
    x1 = x0 + pw
    vals = [data[k] for k, _ in groups]
    vmax = max(vals) * 1.30
    ref = data['OFF']

    d.text(((x0 + x1) // 2, PY0 - 56), title, font=f_panel, fill=C_TXT, anchor='ma')
    d.text((x1, PY0 - 26), ref_label % ref, font=f_lab, fill=C_REF, anchor='ra')

    d.line([(x0, PY1), (x1, PY1)], fill=(120, 130, 140), width=2)
    d.line([(x0, PY0), (x0, PY1)], fill=(120, 130, 140), width=2)

    for gi in range(1, 5):
        gy = PY1 - (PY1 - PY0) * gi / 4.0
        d.line([(x0, gy), (x1, gy)], fill=C_GRID, width=1)

    n = len(groups)
    slot = (x1 - x0) / n
    bw = slot * 0.58
    for i, (k, lab) in enumerate(groups):
        cx = x0 + slot * (i + 0.5)
        v = data[k]
        bh = (PY1 - PY0) * v / vmax
        col = C_OK if v <= ref * 1.02 else C_BAD
        d.rectangle([cx - bw / 2, PY1 - bh, cx + bw / 2, PY1], fill=col)
        d.text((cx, PY1 - bh - 7), fmt % v, font=f_val, fill=col, anchor='mb')
        d.text((cx, PY1 + 9), lab, font=f_lab, fill=C_TXT, anchor='ma', align='center')

    by = PY1 - (PY1 - PY0) * ref / vmax
    for xx in range(x0, x1, 13):
        d.line([(xx, by), (min(xx + 8, x1), by)], fill=C_REF, width=2)

d.text((W // 2, H - 122), '绿色 = 不差于基线；红色 = 比基线差。',
       font=f_foot, fill=C_TXT, anchor='ma')
d.text((W // 2, H - 94),
       'ON / A / B / C 全部让小臂管偏离 HD2 轴更多（46.5 → 56.6 / 143.6 / 83.4 / 128.7 mm），',
       font=f_foot, fill=(60, 60, 60), anchor='ma')
d.text((W // 2, H - 66),
       'A 与 C 还把大臂倾斜推到 22~24°（基线 8.55°），B 的直线度 rms 也涨到 8.39。',
       font=f_foot, fill=(60, 60, 60), anchor='ma')
d.text((W // 2, H - 34),
       '结论：选项 1 让手臂整体更差 ⇒ 不采用；交付包仍为第 76b 轮（未改一字节）。',
       font=f_foot, fill=(150, 40, 30), anchor='ma')

out = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'preview',
                   'r77b_option1_arm_metrics.png')
os.makedirs(os.path.dirname(out), exist_ok=True)
img.save(out)
print('wrote', out, img.size)
