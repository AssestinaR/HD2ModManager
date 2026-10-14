# -*- coding: utf-8 -*-
"""READ-ONLY：把「网格 + HD2 标准骨架连线」渲成多视角对比图，专供**肉眼**判断。

第 78 轮（用户反馈：「没有 hd2 标准骨架连线的图我拿什么参考？」）：
本脚本在网格上**画出完整的 HD2 标准骨架**（每根骨的 head→head+axis*len），
手臂骨链加粗，肘/腕关节画成标记点，并标出 HD2 自己的 l_elbow / l_hand 位置。
所有变体的骨架逐位相同 ⇒ 相机框自动一致。

usage:
    _render_skeleton_compare.py --out OUT.png --side left \\
        --npz E:/tmp/r77-arm-off3:OFF交付 E:/tmp/r78-arm-aim:R78新规则 \\
        [--focus body|arm] [--views front,top,side,q34,bend]
"""
import argparse
import importlib
import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
RAV = importlib.import_module('_render_arm_npz_views')
render = RAV.render

TILE_W, TILE_H = 460, 520
BG = (18, 20, 24)


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


def load_npz(path):
    z = np.load(os.path.join(path, 'model.npz'))
    import json
    meta = json.load(open(os.path.join(path, 'meta.json'), encoding='utf-8'))
    return dict(
        pos=np.asarray(z['ours_bind'], np.float64),
        tris=np.asarray(z['tris'], np.int64),
        wb=np.asarray(z['w_bone'], np.int64),
        wv=np.asarray(z['w_val'], np.float64),
        bh=np.asarray(z['b_head'], np.float64),
        ba=np.asarray(z['b_axis'], np.float64),
        bl=np.asarray(z['b_len'], np.float64),
        bp=np.asarray(z['b_parent'], np.int64),
        names=list(meta['bones']),
    )


def bone_segments(bh, ba, bl, bp):
    """Every bone as head -> head + axis*len (len 0 = collapsed helper, skipped)."""
    segs = []
    for i in range(len(bh)):
        L = float(bl[i])
        if L <= 1e-5:
            continue
        segs.append((bh[i], bh[i] + ba[i] * L, i))
    return segs


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', required=True)
    ap.add_argument('--side', default='left', choices=('left', 'right'))
    ap.add_argument('--npz', nargs='+', required=True, help='DIR:LABEL ...')
    ap.add_argument('--focus', default='arm', choices=('body', 'arm', 'wrist', 'elbow'))
    ap.add_argument('--views', default='')
    ap.add_argument('--margin', type=float, default=0.07)
    ap.add_argument('--arm-radius', type=float, default=0.20)
    ap.add_argument('--arm-wmin', type=float, default=0.35,
                    help='with --focus arm, also drop vertices whose total weight '
                         'on the arm chain is below this (strips the cape/skirt)')
    ap.add_argument('--tile', type=int, default=460,
                    help='tile size in px (width); height = width * 520/460')
    args = ap.parse_args()
    globals()['TILE_W'] = args.tile
    globals()['TILE_H'] = int(round(args.tile * 520.0 / 460.0))
    RAV.TILE_W = globals()['TILE_W']
    RAV.TILE_H = globals()['TILE_H']

    pre = 'l' if args.side == 'left' else 'r'

    variants = []
    for spec in args.npz:
        d, _, lab = spec.rpartition(':')
        if not os.path.isdir(d):
            d, lab = spec, ''
        variants.append((d, lab or os.path.basename(d)))

    V = [load_npz(d) for d, _ in variants]
    ref = V[0]
    slot = {n: i for i, n in enumerate(ref['names'])}
    S = ref['bh'][slot[pre + '_shoulder']]
    E = ref['bh'][slot[pre + '_elbow']]
    W = ref['bh'][slot[pre + '_hand']]

    right = np.array([1.0, 0.0, 0.0])
    fwd = np.array([0.0, 1.0, 0.0])
    up = np.array([0.0, 0.0, 1.0])
    q = np.array([0.62, -0.72, 0.31]); q /= np.linalg.norm(q)
    qy = np.cross(np.cross(q, up), q); qy /= np.linalg.norm(qy)

    ALLV = {
        'front': ('正面 (+Y 看)', right, up, -fwd),
        'back': ('背面 (-Y 看)', -right, up, fwd),
        'top': ('俯视 (+Z 看)', right, fwd, up),
        'side': ('侧视 (+X 看)', fwd, up, -right),
        'q34': ('3/4 视角', q, qy, np.cross(q, qy)),
    }
    if args.views:
        keys = [k for k in args.views.split(',') if k in ALLV]
    else:
        keys = (['front', 'top', 'side', 'q34'] if args.focus == 'body'
                else ['front', 'top', 'side', 'q34'])
    VIEWS = [ALLV[k] for k in keys]

    # ---- camera framing ---------------------------------------------------
    marg = args.margin
    if args.focus == 'body':
        box_pts = np.stack([ref['pos'].min(0), ref['pos'].max(0)])
    else:
        d_f = (W - E) / max(float(np.linalg.norm(W - E)), 1e-9)
        d_u = (E - S) / max(float(np.linalg.norm(E - S)), 1e-9)
        if args.focus in ('wrist', 'elbow'):
            ctr = W if args.focus == 'wrist' else E
            h = 0.17
            box_pts = np.stack([ctr + np.array([sx * h, sy * h, sz * h])
                                for sx in (-1, 1) for sy in (-1, 1) for sz in (-1, 1)])
            marg = 0.0
        else:
            box_pts = np.stack([S - d_u * 0.05, E, W, W + d_f * 0.13])

    boxes = []
    for _t, U_, Vv, N_ in VIEWS:
        px, py = box_pts @ U_, box_pts @ Vv
        boxes.append((float(px.min()) - marg, float(py.min()) - marg,
                      float(px.max()) + marg, float(py.max()) + marg))

    # ---- render rows ------------------------------------------------------
    rows = []
    for k, (d, lab) in enumerate(variants):
        v = V[k]
        p, tr, wb, wv = v['pos'], v['tris'], v['wb'], v['wv']
        if args.focus == 'arm':
            keep = np.minimum(RAV.seg_dist(p, S, E),
                              RAV.seg_dist(p, E, W)) < args.arm_radius
            armn = ['%s_shoulder' % pre, '%s_elbow' % pre,
                    '%s_hand' % pre, '%s_hand_twist' % pre]
            aidx = [i for i, n in enumerate(v['names']) if n in armn]
            aw = np.zeros(len(p))
            for i in aidx:
                aw += np.where(wb == i, wv, 0.0).sum(axis=1)
            keep &= aw > args.arm_wmin
            tr = tr[keep[tr].all(axis=1)]
        hidx = [i for i, n in enumerate(v['names']) if n.startswith(pre + '_hand')]
        hw = np.zeros(len(p))
        for i in hidx:
            hw += np.where(wb == i, wv, 0.0).sum(axis=1)
        rows.append((lab, p, tr, np.clip(hw, 0, 1), v))

    HEAD = 82
    LEFTW = 150
    cols = len(VIEWS)
    Wt = LEFTW + TILE_W * cols
    Ht = HEAD + TILE_H * len(rows) + 108
    canvas = Image.new('RGB', (Wt, Ht), (13, 14, 17))
    dr = ImageDraw.Draw(canvas)
    f_h = _font(22, True)
    f_lab = _font(20, True)
    f_small = _font(15)
    f_tiny = _font(14)

    dr.text((Wt // 2, 12),
            '第 78 轮 · 网格 + HD2 标准骨架（%s臂）· 同一相机 · 从 model.npz 直接渲染'
            % ('左' if args.side == 'left' else '右'),
            font=f_h, fill=(240, 243, 248), anchor='ma')

    for c, (t, *_r) in enumerate(VIEWS):
        dr.text((LEFTW + TILE_W * c + TILE_W // 2, HEAD - 26), t,
                font=f_small, fill=(205, 212, 224), anchor='ma')

    for r, (lab, p, tris, hw, v) in enumerate(rows):
        y0 = HEAD + TILE_H * r
        if r:
            dr.line([(0, y0 - 2), (Wt, y0 - 2)], fill=(58, 62, 70), width=1)
        dr.text((12, y0 + TILE_H // 2), lab, font=f_lab, fill=(255, 214, 120))
        segs = bone_segments(v['bh'], v['ba'], v['bl'], v['bp'])

        for c, (_t, U_, Vv, N_) in enumerate(VIEWS):
            img, (sx, sy, sc, oxx, oyy) = render(p, tris, U_, Vv, N_, boxes[c],
                                                 wcol=hw)
            tile = Image.fromarray(np.clip(img, 0, 255).astype(np.uint8))
            tdr = ImageDraw.Draw(tile)
            lo, hi = np.array(boxes[c][:2]), np.array(boxes[c][2:])

            def to_px(P):
                P = np.atleast_2d(P)
                return [((float(x) - lo[0]) * sc + oxx,
                         TILE_H - ((float(y) - lo[1]) * sc + oyy))
                        for x, y in zip(P @ U_, P @ Vv)]

            # --- full HD2 skeleton: thin dark red ---
            for h0, h1, _i in segs:
                (x0, y0p), (x1, y1p) = to_px(np.stack([h0, h1]))
                tdr.line([(x0, y0p), (x1, y1p)], fill=(150, 40, 40), width=1)
            # --- arm chain: bright red, thicker ---
            for sd in ('l', 'r'):
                ch = ['%s_shoulder' % sd, '%s_elbow' % sd,
                      '%s_hand' % sd, '%s_hand_twist' % sd]
                pts = [v['bh'][slot[n]] for n in ch]
                tdr.line(to_px(np.stack(pts)), fill=(255, 70, 70), width=3)
            # --- joints ---
            for nm, colr, rad in (('%s_shoulder' % pre, (255, 210, 90), 4),
                                  ('%s_elbow' % pre, (255, 140, 40), 6),
                                  ('%s_hand' % pre, (90, 190, 255), 6)):
                jx, jy = to_px(v['bh'][slot[nm]])[0]
                tdr.ellipse([jx - rad, jy - rad, jx + rad, jy + rad],
                            outline=colr, width=2)

            canvas.paste(tile, (LEFTW + TILE_W * c, y0))
            if c:
                dr.line([(LEFTW + TILE_W * c - 1, y0),
                         (LEFTW + TILE_W * c - 1, y0 + TILE_H)],
                        fill=(58, 62, 70), width=1)

    ly = HEAD + TILE_H * len(rows) + 12
    dr.text((LEFTW + 8, ly),
            '深红细线 = HD2 标准骨架全部骨骼；亮红粗线 = 手臂骨链 '
            'shoulder→elbow→hand→hand_twist',
            font=f_small, fill=(200, 206, 218))
    dr.text((LEFTW + 8, ly + 22),
            '橙圈 = HD2 自己的 l_elbow   蓝圈 = HD2 自己的 l_hand（腕骨）   '
            '黄圈 = HD2 的肩；网格偏黄 = l_hand 权重高',
            font=f_small, fill=(200, 206, 218))
    dr.text((LEFTW + 8, ly + 46),
            '所有变体的骨架逐位相同 ⇒ 骨架就是那把"尺"，直接看网格相对它的姿态。',
            font=f_small, fill=(140, 148, 160))
    dr.text((LEFTW + 8, ly + 70),
            '本图只做几何呈现，不做任何"好/坏"判断 —— 请直接看外观。',
            font=f_small, fill=(255, 170, 90))

    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    canvas.save(args.out)
    print('wrote %s  (%dx%d)  rows=%d cols=%d'
          % (args.out, canvas.width, canvas.height, len(rows), cols))
    return 0


if __name__ == '__main__':
    sys.exit(main())
