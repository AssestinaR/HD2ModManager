# -*- coding: utf-8 -*-
"""把「手臂平面内的等比剖面」画成 PNG（第 78 轮）。

原点 = HD2 的 l_elbow；横轴 = HD2 肘->腕方向；纵轴 = 平面内垂直方向（朝起源肘那一侧）。
单位毫米，等比。画出：
  * HD2 骨架（shoulder -> elbow -> hand）
  * 起源/工作肘（管线日志里写的 pivot）
  * 交付版 / R78 两版的**网格小臂中线**（对分片质心做直线拟合）

usage:
    _render_arm_plane_schematic.py <out.png> <npzA:标签A> <npzB:标签B>
"""
import json
import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

W, H = 1100, 830
BG = (18, 20, 24)
FG = (232, 234, 238)
MUTED = (150, 155, 165)

S = np.array([-0.2101, -0.0689, 1.4745])
E = np.array([-0.4723, -0.0539, 1.3236])
WH = np.array([-0.6566, 0.1401, 1.2189])
ENOW = np.array([-0.4061, 0.0785, 1.4024])

E1 = (WH - E) / np.linalg.norm(WH - E)
_t = (ENOW - E) - ((ENOW - E) @ E1) * E1
E2 = _t / np.linalg.norm(_t)


def px(x):
    return 90.0 + (x + 280.0) * 1.55


def py(y):
    return 420.0 - y * 1.50


def P3(P):
    d = P - E
    return px(float(d @ E1) * 1000.0), py(float(d @ E2) * 1000.0)


def fit_centreline(d):
    z = np.load(os.path.join(d, 'model.npz'))
    meta = json.load(open(os.path.join(d, 'meta.json'), encoding='utf-8'))
    names = list(meta['bones'])
    pos = np.asarray(z['ours_bind'], np.float64)
    wb = np.asarray(z['w_bone'], np.int64)
    wv = np.asarray(z['w_val'], np.float64)
    idx = [i for i, n in enumerate(names)
           if n in ('l_elbow', 'l_hand_twist') or n.startswith('l_hand')]
    w = np.zeros(len(pos))
    for i in idx:
        w += np.where(wb == i, wv, 0.0).sum(axis=1)
    P = pos[w > 0.5]
    rel = P - E
    t = rel @ E1
    rows = []
    for tt in np.arange(-0.02, 0.401, 0.02):
        s = np.abs(t - tt) < 0.01
        if s.sum() < 8:
            continue
        rows.append((tt, float((rel[s] @ E2).mean())))
    R = [r for r in rows if 0.0 <= r[0] <= 0.28]
    T = np.array([r[0] for r in R])
    Y = np.array([r[1] for r in R])
    k = np.polyfit(T, Y, 1)
    return k, float(np.degrees(np.arctan(k[0])))


def font(sz, bold=False):
    for p in (['C:/Windows/Fonts/msyhbd.ttc', 'C:/Windows/Fonts/msyh.ttc'] if bold
              else ['C:/Windows/Fonts/msyh.ttc']):
        if os.path.exists(p):
            try:
                return ImageFont.truetype(p, sz)
            except Exception:
                pass
    return ImageFont.load_default()


def main():
    out = sys.argv[1]
    specs = []
    for s in sys.argv[2:]:
        d, _, lab = s.rpartition(':')
        specs.append((d, lab))

    im = Image.new('RGB', (W, H), BG)
    dr = ImageDraw.Draw(im)
    f_title = font(22, True)
    f_lab = font(17)
    f_sm = font(15)
    f_note = font(16)

    dr.text((40, 18), '左小臂剖面（真实毫米，等比）：HD2 骨架 · 起源肘 · 两版的小臂中线',
            font=f_title, fill=FG)

    # HD2 skeleton
    pS, pE, pW = P3(S), P3(E), P3(WH)
    dr.line([pS, pE], fill=(55, 138, 221), width=5)
    dr.line([pE, pW], fill=(55, 138, 221), width=5)
    for p, c, r in ((pS, (250, 199, 117), 9), (pE, (239, 159, 39), 9),
                    (pW, (133, 183, 235), 9)):
        dr.ellipse([p[0] - r, p[1] - r, p[0] + r, p[1] + r], fill=c,
                   outline=(120, 120, 120))
    pN = P3(ENOW)
    dr.line([pE, pN], fill=(120, 122, 128), width=2)
    dr.ellipse([pN[0] - 9, pN[1] - 9, pN[0] + 9, pN[1] + 9], fill=(127, 119, 221),
               outline=(83, 74, 183))
    # the HD2 wrist guide
    dr.line([(pW[0], 330), (pW[0], 690)], fill=(120, 122, 128), width=1)

    dr.text((pS[0] + 14, pS[1] - 26), 'HD2 骨架 l_shoulder', font=f_lab, fill=FG)
    dr.text((pE[0] - 130, pE[1] + 22), 'HD2 l_elbow', font=f_lab, fill=FG)
    dr.text((pW[0] - 96, pW[1] - 48), 'HD2 l_hand（HD2 的腕）', font=f_lab, fill=FG)
    dr.text((pN[0] + 16, pN[1] - 24),
            '起源肘：你假设的旋转中心（离 HD2 肘 167.7 mm）', font=f_lab, fill=(170, 165, 245))

    cols = [(29, 158, 117), (226, 75, 74)]
    ytxt = 700
    for (d, lab), c in zip(specs, cols):
        k, ang = fit_centreline(d)
        p0 = (px(0.0), py(k[1] * 1000.0))
        p1 = (px(287.3), py((k[0] * 0.28736 + k[1]) * 1000.0))
        dr.line([p0, p1], fill=c, width=5)
        u287 = (k[0] * 0.28736 + k[1]) * 1000.0
        dr.line([(40, ytxt), (80, ytxt)], fill=c, width=5)
        dr.text((92, ytxt - 10),
                '%s：小臂中线 u = %+.4f·t %+.1f mm   →   t=287 mm 处 u = %+.1f mm，'
                '方向差 %+.1f°' % (lab, k[0], k[1] * 1000.0, u287, ang),
                font=f_note, fill=FG)
        ytxt += 30
    dr.text((40, ytxt + 4),
            '实测：网格小臂绕 HD2 的 l_elbow 转（转动轴离它 48–52 mm），不是绕起源肘（167.7 mm 外）。',
            font=f_note, fill=MUTED)

    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    im.save(out)
    print('wrote %s  (%dx%d)' % (out, W, H))


if __name__ == '__main__':
    main()
