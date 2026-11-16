#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""离线着色器预览器：不用进游戏，秒级出图。

定位
----
它**不执行 DXBC**，而是把 `famA_product3.hlsl` 里那套数学用 numpy 逐像素重算一遍：
颜色、alpha、镂空、金属高光、时间呼吸、以及最容易搞错的 **线性 → sRGB** 编码。
所以它回答的是「这个参数组合看起来对不对」，**不回答**「引擎会不会画这条 pass」。

它能替你做掉的事（原本每次都要重启游戏）：
  · 换贴图后长什么样
  · EXPOSURE / METAL_K / GLOSS / CUT_THRESH 扫参的效果
  · 透明度呼吸到不同相位时的样子
  · 线性值映射到屏幕后到底有多亮（"乳白"就是这么来的）

参数自动同步：直接从 HLSL 里读 `static const float NAME = 数值;`，
所以改 HLSL 之后不用再改一遍预览器。

用法
----
    python tools/shader_preview.py --hlsl .dsh/tmp/famA_product3.hlsl \
        --texture E:/tmp/hd2-stocking/p2/stocking.png --out E:/tmp/preview.png
    python tools/shader_preview.py ... --frames 4        # 出 4 个呼吸相位
    python tools/shader_preview.py ... --tile 6 3        # 贴图平铺次数
"""
import argparse
import os
import re
import sys

import numpy as np
from PIL import Image

SKIN = np.array([0.78, 0.58, 0.50], dtype=np.float32)   # 肤色（线性）
DARK = np.array([0.03, 0.03, 0.035], dtype=np.float32)  # 深色块
SUN = np.array([1.0, 1.0, 1.0], dtype=np.float32)


def srgb(x):
    x = np.clip(x, 0.0, 1.0)
    return np.where(x <= 0.0031308, x * 12.92, 1.055 * np.power(x, 1 / 2.4) - 0.055)


def read_consts(path):
    """从 HLSL 里抠出 static const float NAME = 0.2; 这类常量。"""
    out = {}
    try:
        txt = open(path, encoding='utf-8', errors='replace').read()
    except OSError:
        return out
    for m in re.finditer(r'static\s+const\s+float\s+(\w+)\s*=\s*([-\d.eE+]+)', txt):
        try:
            out[m.group(1)] = float(m.group(2))
        except ValueError:
            pass
    return out


def load_tex(path):
    im = Image.open(path).convert('RGBA')
    a = np.asarray(im, dtype=np.float32) / 255.0
    return a


def sample(tex, u, v):
    h, w = tex.shape[:2]
    x = np.clip((u % 1.0) * (w - 1), 0, w - 1)
    y = np.clip((v % 1.0) * (h - 1), 0, h - 1)
    x0 = np.floor(x).astype(np.int32); x1 = np.minimum(x0 + 1, w - 1)
    y0 = np.floor(y).astype(np.int32); y1 = np.minimum(y0 + 1, h - 1)
    fx = (x - x0)[..., None]; fy = (y - y0)[..., None]
    return ((tex[y0, x0] * (1 - fx) + tex[y0, x1] * fx) * (1 - fy)
            + (tex[y1, x0] * (1 - fx) + tex[y1, x1] * fx) * fy)


def render(tex, P, t, tile, res, radius):
    H, W = res
    ys, xs = np.mgrid[0:H, 0:W].astype(np.float32)
    px = (xs / (W - 1)) * 2.0 - 1.0          # -1..1
    py = 1.0 - (ys / (H - 1)) * 2.0

    inside = (np.abs(px) <= radius) & (np.abs(py) <= 0.95)

    # 圆柱正面：z = sqrt(R^2 - x^2)，法线 = (x/R, 0, z/R)
    z = np.sqrt(np.maximum(radius ** 2 - px ** 2, 0.0))
    n = np.stack([px / radius, np.zeros_like(px), z / radius], axis=-1)
    n /= np.maximum(np.linalg.norm(n, axis=-1, keepdims=True), 1e-6)

    u = (np.arctan2(px, z) / (2 * np.pi) + 0.5) * tile[0]
    v = ((py * 0.5 + 0.5)) * tile[1]
    s = sample(tex, u, v)

    cut = P.get('CUT_THRESH', 0.5)
    hole = s[..., 0] < cut
    alive = inside & (~hole)

    cull = P.get('CULL_BACK', 0.0)   # 圆柱背面不画，这里只用正面
    _ = cull

    V = np.stack([np.zeros_like(px), np.zeros_like(px), np.ones_like(px)], axis=-1)
    L = np.array([0.4, 0.5, 0.76], dtype=np.float32); L /= np.linalg.norm(L)
    Hh = V + L; Hh /= np.linalg.norm(Hh, axis=-1, keepdims=True)

    nl = np.clip(np.sum(n * L, axis=-1), 0, 1)
    nh = np.clip(np.sum(n * Hh, axis=-1), 0, 1)
    nv = np.clip(np.sum(n * V, axis=-1), 0, 1)

    GLOSS = P.get('GLOSS', 60.0)
    spec = np.power(nh, GLOSS)
    fres = np.power(1.0 - nv, 5.0)

    base = np.stack([s[..., 1], s[..., 2], s[..., 1]], axis=-1) * P.get('BASE_TINT', 1.0)
    amb = P.get('AMBIENT', 0.30)
    EXP = P.get('EXPOSURE', 0.20)
    diff = base * (amb + (1 - amb) * nl[..., None]) * EXP
    metal = SUN * (spec[..., None] * P.get('METAL_K', 0.10)
                   + fres[..., None] * P.get('RIM_K', 0.02)) * EXP

    pulse = 0.5 + 0.5 * np.sin(t * P.get('PULSE_RATE', 2.0))
    emis = base * P.get('EMIS_PULSE', 0.10) * pulse * EXP

    color = np.clip(diff + metal + emis, 0, 1)

    a = np.clip(s[..., 3], 0, 1) * (P.get('ALPHA_PULSE_MIN', 0.35)
                                    + (1 - P.get('ALPHA_PULSE_MIN', 0.35)) * pulse)

    # 背景：灰阶棋盘，透明度一眼可见（透明处能看见格子，实心处盖住格子）
    cell = 42
    chk = ((((xs // cell) + (ys // cell)) % 2) == 0)[..., None]
    bg = np.where(chk, np.array([0.62, 0.62, 0.64], np.float32),
                       np.array([0.10, 0.10, 0.11], np.float32))

    out = bg * (1 - a[..., None]) + color * a[..., None]
    out = np.where(alive[..., None], out, bg)
    return srgb(out), alive.mean()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--hlsl', required=True)
    ap.add_argument('--texture', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--frames', type=int, default=3, help='呼吸相位数量')
    ap.add_argument('--res', default='420x420')
    ap.add_argument('--tile', default='4x2', help='贴图平铺 UxV')
    ap.add_argument('--radius', type=float, default=0.42)
    ap.add_argument('--set', action='append', default=[], help='覆盖常量 NAME=VALUE')
    ap.add_argument('--sweep', default=None, help='扫参：NAME=v1,v2,v3（沿横向拼成对比图）')
    args = ap.parse_args()

    P = read_consts(args.hlsl)
    for kv in args.set:
        k, v = kv.split('=')
        P[k.strip()] = float(v)
    print('从 HLSL 读到常量:')
    for k in sorted(P):
        print('   %-16s %g' % (k, P[k]))

    tex = load_tex(args.texture)
    W, H = (int(x) for x in args.res.lower().split('x'))
    tile = tuple(float(x) for x in args.tile.lower().split('x'))

    period = 2 * np.pi / max(P.get('PULSE_RATE', 2.0), 1e-6)

    if args.sweep:
        name, vals = args.sweep.split('=')
        name = name.strip()
        shots = []
        for v in vals.split(','):
            Q = dict(P)
            Q[name] = float(v)
            img, cov = render(tex, Q, period * 0.25, tile, (H, W), args.radius)
            shots.append(img)
            print('%-14s = %-8s 覆盖率 %.1f%%' % (name, v, cov * 100))
        sheet = np.concatenate(shots, axis=1)
        os.makedirs(os.path.dirname(os.path.abspath(args.out)) or '.', exist_ok=True)
        Image.fromarray((np.clip(sheet, 0, 1) * 255).astype(np.uint8)).save(args.out)
        print('扫参图 %s  (%dx%d)' % (args.out, sheet.shape[1], sheet.shape[0]))
        return 0

    frames = []
    for i in range(max(1, args.frames)):
        t = period * i / max(1, args.frames)
        img, cov = render(tex, P, t, tile, (H, W), args.radius)
        frames.append(img)
        print('相位 %d/%d  t=%.2fs  覆盖率 %.1f%%' % (i + 1, args.frames, t, cov * 100))

    sheet = np.concatenate(frames, axis=1) if len(frames) > 1 else frames[0]
    os.makedirs(os.path.dirname(os.path.abspath(args.out)) or '.', exist_ok=True)
    Image.fromarray((np.clip(sheet, 0, 1) * 255).astype(np.uint8)).save(args.out)
    print('写出 %s  (%dx%d)' % (args.out, sheet.shape[1], sheet.shape[0]))
    return 0


if __name__ == '__main__':
    sys.exit(main())
