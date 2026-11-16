#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""HD2 根材质载荷：视角曲线（N·V）编辑工具 —— 统一入口。

背景（详见 docs/agent/hd2-custom-material-pipeline-plan-20261114.md）：
    载荷里 34 条记录带同一条「视角曲线」代码块：
        alpha = (1 - t^E * (3 - 2t))^2 ,  t = saturate((N·V - T) * 1/(1-T))
    其中 o0.w 是**光照混合权重**（≈1 完整受光 / ≈0 不受光发黑）。
    实测只有 60KB 家族的 A 组（8 份同一着色器 4bc0104d）影响可见黑层。

能力：
    --threshold T     改阈值（bias=-T, mult=1/(1-T)）
    --curve t2|t4     把 t^4 改成 t^2（7×nop 等长替换）或还原
    --const A         把曲线结果整条换成常数（仅家族 A，mul→mov+nop 等长替换）

用法：
    python payload_curve_edit.py <gpu_resources> --show
    python payload_curve_edit.py <gpu_resources> --family A --threshold 0.12 --apply
    python payload_curve_edit.py <gpu_resources> --family A --const 0.5 --apply
    python payload_curve_edit.py <gpu_resources> --restore
"""
import struct, os, sys, glob, shutil, datetime, argparse, hashlib

GROUPS = {
    '46k': [281216, 337792, 394032, 449328],
    '60k': [517344, 620328, 722232, 822928, 921752, 1018800, 1119256, 1218624, 1316792, 1413120,
            1510296, 1613280, 1715184, 1815880, 1914704, 2011752, 2112208, 2211576, 2309744, 2406072],
    'mat': [2592520, 2748816, 2903832, 3057216, 3204672, 3362048, 3518344, 3673360, 3826744, 3974200],
}
FAMILIES = {
    'A': [517344, 620328, 722232, 822928, 1510296, 1613280, 1715184, 1815880],
    'B': [1018800, 1119256, 1218624, 1316792, 2011752, 2112208, 2211576, 2309744],
    'C': [921752, 1914704],
    'D': [1413120, 2406072],
}
MUL_OP, MOV_OP, NOP = 56, 54, 0x0100003A
IMM32 = 0x00004001


def shex_range(d, rec):
    nch = struct.unpack_from('<I', d, rec + 28)[0]
    for o in struct.unpack_from('<%dI' % nch, d, rec + 32):
        if d[rec + o:rec + o + 4] == b'SHEX':
            size = struct.unpack_from('<I', d, rec + o + 4)[0]
            return rec + o + 16, rec + o + 8 + size
    return None, None


def alpha_block(d, rec):
    start, end = shex_range(d, rec)
    if start is None:
        return None
    for k in range(start, end - 160, 4):
        b = struct.unpack_from('<f', d, k)[0]
        m = struct.unpack_from('<f', d, k + 28)[0]
        if -1.0 < b < 0.0 and abs(m * (1.0 + b) - 1.0) < 1e-5:
            if struct.unpack_from('<I', d, k + 96)[0] == 0x07000038:
                return k
    return None


def const_site(d, rec):
    """定位「曲线结果落盘」的那条 mul r40.w, r34.x, r34.y（家族 A 专用）。"""
    start, end = shex_range(d, rec)
    if start is None:
        return None
    blk = alpha_block(d, rec)
    if blk is None:
        return None
    for k in range(blk, end - 28, 4):
        t = struct.unpack_from('<I', d, k)[0]
        if (t & 0x7FF) != MUL_OP or ((t >> 24) & 0x7F) != 7:
            continue
        if struct.unpack_from('<I', d, k + 8)[0] != 40:
            continue
        if struct.unpack_from('<2I', d, k + 12) != (0x0010000A, 34):
            continue
        if struct.unpack_from('<2I', d, k + 20) != (0x0010001A, 34):
            continue
        return k
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('payload')
    ap.add_argument('--group', choices=sorted(GROUPS))
    ap.add_argument('--family', choices=sorted(FAMILIES))
    ap.add_argument('--records', help='逗号分隔的记录偏移')
    ap.add_argument('--threshold', type=float)
    ap.add_argument('--mult', type=float, help='手动指定归一化系数（可与 threshold 反向，用于翻转方向）')
    ap.add_argument('--curve', choices=['t2', 't4'])
    ap.add_argument('--const', dest='const_alpha', type=float)
    ap.add_argument('--show', action='store_true')
    ap.add_argument('--apply', action='store_true')
    ap.add_argument('--restore', action='store_true')
    a = ap.parse_args()

    if a.restore:
        baks = sorted(glob.glob(os.path.join(os.path.dirname(a.payload), '.abtest-backup', '*',
                                             os.path.basename(a.payload))))
        if not baks:
            raise SystemExit('没有备份可还原')
        shutil.copy2(baks[-1], a.payload)
        print('已还原：', baks[-1])
        return 0

    d = bytearray(open(a.payload, 'rb').read())

    if a.show or (a.threshold is None and a.curve is None and a.const_alpha is None):
        print('记录组：')
        for name, recs in GROUPS.items():
            vals = set()
            for r in recs:
                k = alpha_block(d, r)
                if k is not None:
                    vals.add((round(struct.unpack_from('<f', d, k)[0], 4),
                              round(struct.unpack_from('<f', d, k + 28)[0], 6)))
            print('  %-4s %2d/%2d 块  bias/mult=%s' % (name, len(vals) and sum(
                1 for r in recs if alpha_block(d, r)) or 0, len(recs), sorted(vals)))
        print('家族（60KB 内部的 4 个不同着色器）：')
        for name, recs in FAMILIES.items():
            hs = {hashlib.sha256(bytes(d[r:r + struct.unpack_from('<I', d, r + 24)[0]])).hexdigest()[:8]
                  for r in recs}
            print('  %-2s %2d 条，内容 %s' % (name, len(recs), ','.join(sorted(hs))))
        return 0

    targets = []
    if a.group:
        targets += GROUPS[a.group]
    if a.family:
        targets += FAMILIES[a.family]
    if a.records:
        targets += [int(x) for x in a.records.split(',') if x.strip()]

    n_thr = n_cur = n_const = 0
    for rec in targets:
        k = alpha_block(d, rec)
        if k is None:
            print('  [!] @%d 没有 alpha 块' % rec)
            continue
        if a.threshold is not None:
            T = a.threshold
            d[k:k + 4] = struct.pack('<f', -T)
            m = a.mult if a.mult is not None else (1.0 / (1.0 - T) if T < 1 else 1.0)
            d[k + 28:k + 32] = struct.pack('<f', m)
            n_thr += 1
        if a.curve is not None:
            mul2 = k + 124
            if (struct.unpack_from('<I', d, mul2)[0] & 0x7FF) == MUL_OP:
                if a.curve == 't2':
                    d[mul2:mul2 + 28] = struct.pack('<7I', *([NOP] * 7))
                    n_cur += 1
        if a.const_alpha is not None:
            site = const_site(d, rec)
            if site is None:
                print('  [!] @%d 没找到曲线收尾指令' % rec)
                continue
            dest_tok, dest_i = struct.unpack_from('<2I', d, site + 4)
            d[site:site + 28] = struct.pack(
                '<7I', (5 << 24) | MOV_OP, dest_tok, dest_i, IMM32,
                struct.unpack('<I', struct.pack('<f', a.const_alpha))[0], NOP, NOP)
            n_const += 1

    print('改动：阈值 %d 条，曲线 %d 条，常数 %d 条' % (n_thr, n_cur, n_const))
    if not a.apply:
        print('[dry-run] 未写盘')
        return 0

    bak = os.path.join(os.path.dirname(a.payload), '.abtest-backup',
                       datetime.datetime.now().strftime('%Y%m%d-%H%M%S') + '-curveedit')
    os.makedirs(bak, exist_ok=True)
    shutil.copy2(a.payload, os.path.join(bak, os.path.basename(a.payload)))
    tmp = a.payload + '.c.tmp'
    open(tmp, 'wb').write(d)
    os.replace(tmp, a.payload)
    print('已写盘；备份 ->', bak)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())

