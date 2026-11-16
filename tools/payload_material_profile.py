#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# ⛔ 已过时（2026-11-16 标记）：本工具是 payload_material_patch.py 的加强版，但随后又被
#    tools/payload_curve_edit.py 统一入口吸收（同样的 threshold/curve + --const + --restore）。
#    新脚本请用 payload_curve_edit.py；本文件保留作历史记录。
"""HD2 根材质载荷「观感 profile」工具（按不变量定位 alpha 块）。

alpha = (1 - t^E (3 - 2t))^2 ,  t = saturate((N·V + bias) * mult)
    bias = -T       （T = 不透明度起点阈值）
    mult = 1/(1-T)  （满足 mult * (1 + bias) == 1）

alpha 块内部布局（相对 bias 立即数 P，实测）：
    P+0    float  bias          （add 指令的立即数）
    P+28   float  mult          （mul 指令的立即数）
    P+88   28 字节 = 第一条 mul （t*t）
    P+116  28 字节 = 第二条 mul （再平方 → t^4）；换成 7×nop 即 t^2

用法：
    python payload_material_profile.py <payload> --show
    python payload_material_profile.py <payload> --threshold 0.6 --curve t2 --apply
    python payload_material_profile.py <payload> --threshold 0.21 --curve t4 --apply
"""
import struct, os, shutil, datetime, argparse

MUL_TOKEN = 0x07000038
NOP = 0x0100003A
NOP7 = struct.pack('<7I', *([NOP] * 7))


def shex_regions(d):
    out, i = [], 0
    while True:
        s = d.find(b'DXBC', i)
        if s < 0:
            return out
        i = s + 4
        if s + 32 > len(d):
            continue
        total, nch = struct.unpack_from('<2I', d, s + 24)
        if not (32 < total < 1 << 22) or not (1 <= nch <= 16) or s + total > len(d):
            continue
        try:
            offs = sorted(struct.unpack_from('<%dI' % nch, d, s + 32))
        except struct.error:
            continue
        for o in offs:
            if d[s + o:s + o + 4] == b'SHEX':
                size = struct.unpack_from('<I', d, s + o + 4)[0]
                out.append((s + o + 16, s + o + 8 + size))
                break


def find_blocks(d, regions):
    """返回 [(bias_offset, mult_offset)]，判据 mult*(1+bias)≈1 且相隔 28 字节。"""
    blocks = []
    for start, end in regions:
        for k in range(start, end - 32, 4):
            b = struct.unpack_from('<f', d, k)[0]
            m = struct.unpack_from('<f', d, k + 28)[0]
            if not (0.05 < m < 100.0):
                continue
            if not (-1.0 < b < 0.0):
                continue
            if abs(m * (1.0 + b) - 1.0) > 1e-5:
                continue
            # 结构判据：bias 后 88 字节处必须是第一条 mul，116 处是 mul 或 nop
            if k + 152 > len(d):
                continue
            if struct.unpack_from('<I', d, k + 96)[0] != MUL_TOKEN:
                continue
            w2 = struct.unpack_from('<I', d, k + 124)[0]
            if w2 != MUL_TOKEN and bytes(d[k + 124:k + 152]) != NOP7:
                continue
            blocks.append((k, k + 28))
    return blocks


def show(d):
    regions = shex_regions(d)
    blocks = find_blocks(d, regions)
    t4 = t2 = 0
    for bias_off, _ in blocks:
        mul2 = bias_off + 124
        if mul2 + 28 <= len(d):
            if bytes(d[mul2:mul2 + 28]) == NOP7:
                t2 += 1
            elif struct.unpack_from('<I', d, mul2)[0] == MUL_TOKEN:
                t4 += 1
    print("SHEX 记录 %d 条；alpha 块 %d 个" % (len(regions), len(blocks)))
    if blocks:
        b = struct.unpack_from('<f', d, blocks[0][0])[0]
        m = struct.unpack_from('<f', d, blocks[0][1])[0]
        print("样本: bias=%.6g (T=%.6g), mult=%.8g" % (b, -b, m))
    print("曲线形状: t^4 × %d, t^2 × %d" % (t4, t2))
    return blocks


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('payload')
    ap.add_argument('--threshold', type=float)
    ap.add_argument('--curve', choices=['t2', 't4'])
    ap.add_argument('--show', action='store_true')
    ap.add_argument('--apply', action='store_true')
    a = ap.parse_args()

    d = bytearray(open(a.payload, 'rb').read())
    blocks = show(d)
    if a.threshold is None and a.curve is None:
        return 0
    if not blocks:
        raise SystemExit("没找到 alpha 块：这个载荷可能不是可用的材质载荷")

    msgs = []
    if a.threshold is not None:
        T = a.threshold
        if not 0.0 <= T < 1.0:
            raise SystemExit("threshold 必须在 [0,1)")
        mult = 1.0 / (1.0 - T)
        for bias_off, mult_off in blocks:
            d[bias_off:bias_off + 4] = struct.pack('<f', -T)
            d[mult_off:mult_off + 4] = struct.pack('<f', mult)
        msgs.append("阈值 T=%.4g（M=%.6g），改 %d 个块" % (T, mult, len(blocks)))

    if a.curve is not None:
        sidecar = a.payload + '.curve-t4.bin'
        n = 0
        if a.curve == 't2':
            saved = []
            for bias_off, _ in blocks:
                mul2 = bias_off + 124
                if mul2 + 28 > len(d) or struct.unpack_from('<I', d, mul2)[0] != MUL_TOKEN:
                    continue
                saved.append((mul2, bytes(d[mul2:mul2 + 28])))
                d[mul2:mul2 + 28] = NOP7
                n += 1
            if saved and a.apply:
                with open(sidecar, 'wb') as fh:
                    fh.write(b'CT4\0' + struct.pack('<I', len(saved)))
                    for off, raw in saved:
                        fh.write(struct.pack('<Q', off) + raw)
            msgs.append("曲线→t2，改 %d 个块（原始 mul 已存 %s）" % (n, os.path.basename(sidecar)))
        else:
            if not os.path.exists(sidecar):
                raise SystemExit("还原 t4 需要之前的 sidecar：%s（或用备份整份还原）" % sidecar)
            blob = open(sidecar, 'rb').read()
            assert blob[:4] == b'CT4\0'
            cnt = struct.unpack_from('<I', blob, 4)[0]
            p = 8
            for _ in range(cnt):
                off = struct.unpack_from('<Q', blob, p)[0]; p += 8
                d[off:off + 28] = blob[p:p + 28]; p += 28
                n += 1
            msgs.append("曲线→t4，还原 %d 个块" % n)

    print("；".join(msgs))
    if not a.apply:
        print("[dry-run] 未写盘")
        return 0
    bak = os.path.join(os.path.dirname(a.payload), '.abtest-backup',
                       datetime.datetime.now().strftime('%Y%m%d-%H%M%S') + "-profile")
    os.makedirs(bak, exist_ok=True)
    shutil.copy2(a.payload, os.path.join(bak, os.path.basename(a.payload)))
    tmp = a.payload + '.p.tmp'
    open(tmp, 'wb').write(d)
    os.replace(tmp, a.payload)
    print("已写盘；备份 ->", bak)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())



