# -*- coding: utf-8 -*-
"""READ-ONLY: 从 model.npz 直接渲染「手臂特写」，多视角网格拼图，用于**肉眼**比较不同变体。

为什么不用指标：手臂观感（有没有瘪、有没有鼓包、肘部有没有塌、腕部有没有褶）只能靠看。
本脚本把每个变体的手臂在**同一相机**下渲成 4 个视角，上下排开，直接对比。

⚠️ 骨架（b_head）在所有变体里逐位相同 ⇒ 相机框自动一致，无需额外对齐。

usage:
    _render_arm_npz_views.py --out OUT.png --side left \\
        --npz E:/tmp/r77-arm-off3:OFF基线 E:/tmp/r77-arm-on:ON默认 ...
"""
import argparse
import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)


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


TILE_W, TILE_H = 430, 430
BG = (20, 22, 26)


def seg_dist(p, a, b):
    ab = b - a
    t = np.clip(((p - a) @ ab) / float(ab @ ab), 0.0, 1.0)
    return np.linalg.norm(p - (a + t[:, None] * ab), axis=1)


def render(pos, tris, U_, V_, N_, box, wcol=None):
    px, py, pz = pos @ U_, pos @ V_, pos @ N_
    lo, hi = np.array(box[:2]), np.array(box[2:])
    scale = min((TILE_W - 14) / (hi[0] - lo[0]), (TILE_H - 14) / (hi[1] - lo[1]))
    ox = (TILE_W - (hi[0] - lo[0]) * scale) / 2
    oy = (TILE_H - (hi[1] - lo[1]) * scale) / 2
    sx = (px - lo[0]) * scale + ox
    sy = TILE_H - ((py - lo[1]) * scale + oy)

    img = np.zeros((TILE_H, TILE_W, 3), np.float64)
    img[:, :] = BG
    zbuf = np.full((TILE_H, TILE_W), -1e18)

    p0, p1, p2 = pos[tris[:, 0]], pos[tris[:, 1]], pos[tris[:, 2]]
    nrm = np.cross(p1 - p0, p2 - p0)
    nrm = nrm / np.maximum(np.linalg.norm(nrm, axis=1, keepdims=True), 1e-30)
    L = np.array([0.35, 0.5, 0.79])
    L /= np.linalg.norm(L)
    lam = np.abs(nrm @ L) * 0.62 + 0.38

    for i in range(len(tris)):
        tri = tris[i]
        xs, ys = sx[tri], sy[tri]
        x0, x1 = int(np.floor(xs.min())), int(np.ceil(xs.max()))
        y0, y1 = int(np.floor(ys.min())), int(np.ceil(ys.max()))
        x0, x1 = max(x0, 0), min(x1, TILE_W - 1)
        y0, y1 = max(y0, 0), min(y1, TILE_H - 1)
        if x0 > x1 or y0 > y1:
            continue
        d = (xs[1] - xs[0]) * (ys[2] - ys[0]) - (xs[2] - xs[0]) * (ys[1] - ys[0])
        if abs(d) < 1e-9:
            continue
        gx, gy = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
        w0 = ((xs[1] - gx) * (ys[2] - gy) - (xs[2] - gx) * (ys[1] - gy)) / d
        w1 = ((xs[2] - gx) * (ys[0] - gy) - (xs[0] - gx) * (ys[2] - gy)) / d
        w2 = 1.0 - w0 - w1
        inside = (w0 >= 0) & (w1 >= 0) & (w2 >= 0)
        if not inside.any():
            continue
        depth = w0 * pz[tri[0]] + w1 * pz[tri[1]] + w2 * pz[tri[2]]
        sub = zbuf[y0:y1 + 1, x0:x1 + 1]
        m = inside & (depth > sub)
        if not m.any():
            continue
        sub[m] = depth[m]
        g = w0 * wcol[tri[0]] + w1 * wcol[tri[1]] + w2 * wcol[tri[2]] if wcol is not None \
            else np.zeros_like(depth)
        shade = (lam[i] * 0.80 + 0.20)
        base = np.array([168.0, 172.0, 180.0])
        warm = np.array([232.0, 176.0, 96.0])
        col = (base * (1.0 - g[..., None]) + warm * g[..., None]) * shade
        img[y0:y1 + 1, x0:x1 + 1][m] = col[m]
    return img, (sx, sy, scale, ox, oy)


def load_npz(path):
    z = np.load(os.path.join(path, 'model.npz'))
    import json
    meta = json.load(open(os.path.join(path, 'meta.json'), encoding='utf-8'))
    return (np.asarray(z['ours_bind'], np.float64), np.asarray(z['tris'], np.int64),
            np.asarray(z['w_bone'], np.int64), np.asarray(z['w_val'], np.float64),
            np.asarray(z['b_head'], np.float64), meta['bones'])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', required=True)
    ap.add_argument('--side', default='left', choices=('left', 'right'))
    ap.add_argument('--npz', nargs='+', required=True, help='DIR:LABEL ...')
    ap.add_argument('--radius', type=float, default=0.26,
                    help='keep mesh within this distance of the arm bone chain')
    ap.add_argument('--margin', type=float, default=0.055)
    ap.add_argument('--focus', default='whole',
                    choices=('whole', 'upper', 'fore', 'wrist'),
                    help='which part of the arm the camera frames')
    args = ap.parse_args()

    pre = 'l' if args.side == 'left' else 'r'
    SD = 'L' if args.side == 'left' else 'R'

    variants = []
    for spec in args.npz:
        d, _, lab = spec.rpartition(':')   # rpartition: Windows 盘符里也有 ':'
        if not os.path.isdir(d):
            d, lab = spec, ''              # 没给 LABEL（且不是 'dir:label'）
        variants.append((d, lab or os.path.basename(d)))

    # ---- reference frame: bones are identical across variants, but read the
    #      first one to build the camera and reuse it for all rows -----------
    pos0, tri, wb, wv, bh, names = load_npz(variants[0][0])
    slot = {n: i for i, n in enumerate(names)}
    S = bh[slot[pre + '_shoulder']]
    E = bh[slot[pre + '_elbow']]
    W = bh[slot[pre + '_hand']]

    vu, vf = E - S, W - E
    nrm = np.cross(vu, vf)
    nrm /= np.linalg.norm(nrm)
    SX = vu / np.linalg.norm(vu) + vf / np.linalg.norm(vf)
    SX /= np.linalg.norm(SX)
    SY = np.cross(nrm, SX)
    SY /= np.linalg.norm(SY)
    SDn = np.cross(SX, SY)

    # view list: (label, U_, V_, N_)
    fwd = np.array([0.0, 1.0, 0.0])
    up = np.array([0.0, 0.0, 1.0])
    right = np.array([1.0, 0.0, 0.0])
    q = (SX + 0.7 * np.cross(SDn, SX))
    q /= np.linalg.norm(q)
    qy = np.cross(SDn, q)
    qy /= np.linalg.norm(qy)

    # ---- camera framing points, per focus ---------------------------------
    d_f = (W - E) / max(float(np.linalg.norm(W - E)), 1e-9)
    d_u = (E - S) / max(float(np.linalg.norm(E - S)), 1e-9)
    FOCUS = {
        'whole': ([S, E, W, W + d_f * 0.10], args.margin),
        'upper': ([S - d_u * 0.04, E + d_u * 0.04], args.margin * 0.6),
        'fore':  ([E - d_f * 0.05, W + d_f * 0.07], args.margin * 0.6),
        'wrist': ([W - d_f * 0.10, W + d_f * 0.07], args.margin * 0.6),
    }
    focus = args.focus
    bp, marg = FOCUS[focus]

    VIEWS = [
        ('弯折平面（正对肘 V）', SX, SY, SDn),
        ('正面 (+Y 看)', right, up, -fwd),
        ('俯视 (+Z 看)', right, fwd, up),
        ('3/4 视角', q, qy, SDn),
    ]
    if focus == 'whole':
        VIEWS = [('弯折平面（正对肘 V）', SX, SY, SDn),
                 ('弯折平面·侧看', SY, SDn, SX),
                 ('正面 (+Y 看)', right, up, -fwd),
                 ('俯视 (+Z 看)', right, fwd, up),
                 ('3/4 视角', q, qy, SDn)]

    # common camera box: from the focus's bone points + margin, in each view's frame
    boxes = []
    bp = np.stack(bp)
    for _t, U_, V_, N_ in VIEWS:
        px, py = bp @ U_, bp @ V_
        boxes.append((float(px.min()) - marg, float(py.min()) - marg,
                      float(px.max()) + marg, float(py.max()) + marg))

    # ---- per-variant arm mesh (tube around the bone chain) -----------------
    rows = []
    for d, lab in variants:
        p, tr, wb, wv, bh2, nm = load_npz(d)
        keep = np.minimum(seg_dist(p, S, E), seg_dist(p, E, W)) < args.radius
        tris = tr[keep[tr].all(axis=1)]
        # colour by "hand" weight so the wrist transition is visible
        hidx = [i for i, n in enumerate(nm) if n.startswith(pre + '_hand')]
        hw = np.zeros(len(p))
        for i in hidx:
            hw += np.where(wb == i, wv, 0.0).sum(axis=1)
        rows.append((lab, p, tris, np.clip(hw, 0, 1)))

    # ---- compose ----------------------------------------------------------
    HEAD = 74
    LEFTW = 168
    cols = len(VIEWS)
    Wt = LEFTW + TILE_W * cols
    Ht = HEAD + TILE_H * len(rows) + 96
    canvas = Image.new('RGB', (Wt, Ht), (14, 15, 18))
    dr = ImageDraw.Draw(canvas)
    f_h = _font(21, True)
    f_lab = _font(19, True)
    f_small = _font(15)
    f_foot = _font(16)

    dr.text((Wt // 2, 10),
            '第 77b 轮 · 手臂特写（%s 臂）· 同一相机 · 从 model.npz 直接渲染'
            % ('左' if args.side == 'left' else '右'),
            font=f_h, fill=(240, 243, 248), anchor='ma')

    for c, (t, *_rest) in enumerate(VIEWS):
        dr.text((LEFTW + TILE_W * c + TILE_W // 2, HEAD - 24), t,
                font=f_small, fill=(200, 208, 220), anchor='ma')

    for r, (lab, p, tris, hw) in enumerate(rows):
        y0 = HEAD + TILE_H * r
        if r:
            dr.line([(0, y0 - 2), (Wt, y0 - 2)], fill=(60, 64, 72), width=1)
        dr.text((12, y0 + TILE_H // 2), lab, font=f_lab, fill=(255, 214, 120))
        for c, (_t, U_, V_, N_) in enumerate(VIEWS):
            img, _sc = render(p, tris, U_, V_, N_, boxes[c], wcol=hw)
            tile = Image.fromarray(np.clip(img, 0, 255).astype(np.uint8))
            tdr = ImageDraw.Draw(tile)
            # bone chain overlay
            px, py = (np.stack([S, E, W]) @ U_), (np.stack([S, E, W]) @ V_)
            lo, hi = np.array(boxes[c][:2]), np.array(boxes[c][2:])
            sc = min((TILE_W - 14) / (hi[0] - lo[0]), (TILE_H - 14) / (hi[1] - lo[1]))
            oxx = (TILE_W - (hi[0] - lo[0]) * sc) / 2
            oyy = (TILE_H - (hi[1] - lo[1]) * sc) / 2
            pts = [((x - lo[0]) * sc + oxx, TILE_H - ((y - lo[1]) * sc + oyy))
                   for x, y in zip(px, py)]
            tdr.line(pts, fill=(255, 90, 90), width=3)
            for (jx, jy) in pts:
                tdr.ellipse([jx - 5, jy - 5, jx + 5, jy + 5], outline=(255, 210, 90), width=2)
            canvas.paste(tile, (LEFTW + TILE_W * c, y0))
            if c:
                dr.line([(LEFTW + TILE_W * c - 1, y0), (LEFTW + TILE_W * c - 1, y0 + TILE_H)],
                        fill=(60, 64, 72), width=1)

    # legend / footer
    ly = HEAD + TILE_H * len(rows) + 12
    dr.text((LEFTW + 8, ly), '红 = HD2 骨骼链 shoulder→elbow→wrist（所有变体逐位相同）；'
                             '黄圈 = 关节；颜色偏黄 = l_hand 权重高（腕部过渡带）',
            font=f_small, fill=(190, 198, 210))
    dr.text((LEFTW + 8, ly + 24),
            '本图只做几何呈现，不做任何“好/坏”判断 —— 请直接看外观。',
            font=f_small, fill=(255, 170, 90))
    dr.text((LEFTW + 8, ly + 50),
            '半径 %.2f m 内的手臂网格；相机框由骨骼决定（+/- %.3f m 余量）'
            % (args.radius, args.margin),
            font=f_small, fill=(140, 148, 160))

    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    canvas.save(args.out)
    print('wrote %s  (%dx%d)  rows=%d cols=%d'
          % (args.out, canvas.width, canvas.height, len(rows), cols))
    return 0


if __name__ == '__main__':
    sys.exit(main())
