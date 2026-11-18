#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""用色卡测量结果标定"引擎显示端"的传递函数： 屏幕 = tonemap(gain × 反照率 + ambient)

为什么要拟合 tonemap：引擎最后会把线性亮度压到 0~1 再编码成 sRGB。这个压缩是**非线性**的，
只用一个增益拟合，亮部会系统性偏低（我们第一轮就看到了：红块比线性预期低 26%）。
灰阶做成 8 级密集采样，就是为了把这个曲线解出来。

模型（线性空间）：
    x = albedo
    y = T(gain·x + ambient),  T 为扩展 Reinhard： y = L(1 + L/Lw²) / (1 + L)
    输出 = sRGB 编码(y)

未知量：gain（光照）、ambient（环境底噪）、Lw（白点）。
用 8 级灰阶最小二乘求解，然后在纯色块上验证。

用法：
    python tools/calibrate_from_card.py --measure <measure.json> [--out render_model.json]
"""
import argparse
import json
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from card_layout import GRAYS, srgb255_to_linear, linear_to_srgb_float  # noqa: E402


def reinhard(L, lw):
    return L * (1.0 + L / max(1e-6, lw * lw)) / (1.0 + L)


def fit(grays_x, grays_y):
    """网格 + 局部细化，解 (gain, ambient, lw)。"""
    best = None
    for gain in np.linspace(0.05, 1.5, 60):
        for ambient in np.linspace(-0.02, 0.08, 21):
            L = gain * grays_x + ambient
            for lw in (0.5, 0.8, 1.0, 1.5, 2.0, 3.0, 5.0, 10.0):
                y = np.array([reinhard(v, lw) for v in L])
                err = float(np.sum((y - grays_y) ** 2))
                if best is None or err < best[0]:
                    best = (err, gain, ambient, lw)
    # 细化
    err, gain, ambient, lw = best
    for _ in range(3):
        for g in np.linspace(gain * 0.85, gain * 1.15, 25):
            for a in np.linspace(ambient - 0.01, ambient + 0.01, 11):
                L = g * grays_x + a
                y = np.array([reinhard(v, lw) for v in L])
                e = float(np.sum((y - grays_y) ** 2))
                if e < err:
                    err, gain, ambient = e, g, a
        for w in np.linspace(max(0.3, lw * 0.7), lw * 1.4, 25):
            L = gain * grays_x + ambient
            y = np.array([reinhard(v, w) for v in L])
            e = float(np.sum((y - grays_y) ** 2))
            if e < err:
                err, lw = e, w
    return gain, ambient, lw


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--measure', required=True)
    ap.add_argument('--out', default=None)
    args = ap.parse_args()

    d = json.load(open(args.measure, encoding='utf-8'))
    by_name = {r['name']: r for r in d['rows']}

    gx, gy, names = [], [], []
    for n in GRAYS:
        if n not in by_name:
            continue
        gx.append(float(np.mean(by_name[n]['linear'])))
        gy.append(float(np.mean([srgb255_to_linear(c) for c in by_name[n]['got']])))
        names.append(n)
    if len(gx) < 4:
        raise SystemExit('灰阶点不足（%d 个），无法拟合 tonemap' % len(gx))
    gx, gy = np.array(gx), np.array(gy)

    gain, ambient, lw = fit(gx, gy)
    pred = np.array([reinhard(gain * x + ambient, lw) for x in gx])
    r2 = 1 - np.sum((gy - pred) ** 2) / np.sum((gy - gy.mean()) ** 2)

    print('灰阶拟合（%d 级）：' % len(gx))
    print('  %-8s %-10s %-12s %-12s' % ('色块', '反照率', '实测(线性)', '模型'))
    for n, x, y, p in zip(names, gx, gy, pred):
        print('  %-8s %-10.3f %-12.4f %-12.4f' % (n, x, y, p))
    print()
    print('  屏幕 = tonemap(%.4f × 反照率 + %.4f)，tonemap 白点 Lw=%.3f，R²=%.5f'
          % (gain, ambient, lw, r2))

    print()
    print('预测对照（线性空间，模型 vs 实测）：')
    worst = 0.0
    for r in d['rows']:
        x = np.array(r['linear'], float)
        got = np.array([srgb255_to_linear(c) for c in r['got']])
        pred_lin = np.array([reinhard(gain * v + ambient, lw) for v in x])
        pred_srgb = [round(255 * linear_to_srgb_float(v)) for v in pred_lin]
        got_srgb = [int(c) for c in r['got']]
        diff = [pred_srgb[i] - got_srgb[i] for i in range(3)]
        worst = max(worst, max(abs(v) for v in diff))
        print('  %-8s 模型 %-18s 实测 %-18s 差 %s' % (r['name'], str(tuple(pred_srgb)), str(tuple(got_srgb)), diff))
    print('  最大残差 %d 级（0~255）' % worst)

    model = {
        'schema': 'hd2.render-model/2',
        'source': args.measure,
        'space': 'linear',
        'gain': round(float(gain), 5),
        'ambient': round(float(ambient), 5),
        'tonemap': 'reinhard-extended',
        'l_white': round(float(lw), 4),
        'fit_r2': round(float(r2), 5),
        'max_residual_srgb': int(worst),
    }
    print()
    for k, v in model.items():
        print('  %-20s %s' % (k, v))
    if args.out:
        with open(args.out, 'w', encoding='utf-8') as f:
            json.dump(model, f, ensure_ascii=False, indent=1)
        print('\n已写出 %s' % args.out)
    return 0


if __name__ == '__main__':
    sys.exit(main())
