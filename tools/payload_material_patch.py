#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# ⛔ 已过时（2026-11-16 标记）：只改阈值。现行统一入口是 tools/payload_curve_edit.py
#    （--show / --threshold / --curve t2|t4 / --const / --restore，覆盖本工具全部能力）。
#    本文件保留作历史记录。
"""按「菲涅尔阈值」改 HD2 根材质载荷里的视角不透明度曲线。

已破译的算法（详见 docs/agent/hd2-custom-material-pipeline-plan-20261114.md §4.8）：
    alpha = (1 - t^4 * (3 - 2t))^2 ,  t = saturate((N·V - T) * M)
    T = 0.21（原始）, M = 1/(1-T) = 1.2658228

本工具在载荷里定位这两处立即数（10 份材质 pass 副本各一份），按给定 T 重写：
    M = 1/(1-T)   （保持曲线在 [T,1] 上归一）

用法：
    python payload_material_patch.py <payload.bin> --threshold 0.4            # 预览
    python payload_material_patch.py <payload.bin> --threshold 0.4 --apply    # 写回
    python payload_material_patch.py <payload.bin> --threshold 0.21 --apply   # 还原默认
"""
import struct, sys, os, shutil, datetime, argparse

# 原始立即数的位模式（十六进制）
BIAS_BITS_ORIG = 0xBE570A3D   # -0.21
MUL_BITS_ORIG = 0x3FA2067B    # 1.2658228..（=1/(1-0.21)）


def fbits(v):
    return struct.unpack('<I', struct.pack('<f', v))[0]


def find_shex_offsets(d):
    """返回所有含 SHEX 块的 DXBC 记录 [(记录偏移, 记录大小, SHEX 数据起点)]"""
    out = []
    i = 0
    while True:
        s = d.find(b'DXBC', i)
        if s < 0:
            break
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
                out.append((s, total, s + o + 8))   # +8: 跳过 version/length
                break
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('payload')
    ap.add_argument('--threshold', type=float, required=True, help='不透明度起点 T（0..1）：N·V<=T 全不透明')
    ap.add_argument('--mult', type=float, default=None, help='覆盖归一化系数（默认 1/(1-T)）')
    ap.add_argument('--apply', action='store_true')
    a = ap.parse_args()

    if not 0.0 <= a.threshold < 1.0:
        raise SystemExit('threshold 必须在 [0,1) 内')
    mult = a.mult if a.mult is not None else 1.0 / (1.0 - a.threshold)

    d = bytearray(open(a.payload, 'rb').read())
    recs = find_shex_offsets(d)
    print("找到 %d 条含 SHEX 的记录" % len(recs))

    bias_new = fbits(-a.threshold)
    mul_new = fbits(mult)
    nb = nm = 0
    for rec, total, shex in recs:
        # 在该记录的 SHEX 里找原始 bias/mul 位模式
        for k in range(shex, min(shex + total, len(d)) - 4):
            w = struct.unpack_from('<I', d, k)[0]
            if w == BIAS_BITS_ORIG:
                d[k:k + 4] = struct.pack('<I', bias_new)
                nb += 1
            elif w == MUL_BITS_ORIG:
                d[k:k + 4] = struct.pack('<I', mul_new)
                nm += 1

    print("阈值 (-0.21 -> -%.4g) 命中 %d 处；归一化 (-> %.6g) 命中 %d 处" % (a.threshold, nb, mult, nm))
    if nb == 0:
        raise SystemExit("没找到原始阈值立即数：这个载荷可能已经被改过（先用 --threshold 0.21 还原）")

    if not a.apply:
        print("[dry-run] 未写盘")
        return 0

    bak = os.path.join(os.path.dirname(a.payload), '.abtest-backup',
                       datetime.datetime.now().strftime('%Y%m%d-%H%M%S') + "-threshold")
    os.makedirs(bak, exist_ok=True)
    shutil.copy2(a.payload, os.path.join(bak, os.path.basename(a.payload)))
    tmp = a.payload + '.p.tmp'
    open(tmp, 'wb').write(d)
    os.replace(tmp, a.payload)
    print("已写盘；备份 ->", bak)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
