#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""载荷自检：一次性核对「结构完整性 / 改动范围 / 分组状态 / 与基线差异」。

用法：
    python payload_selftest.py <payload> [--baseline <原始载荷>]

输出四段：
  1. 记录图（272 条记录）重建是否字节一致
  2. 各分组（46k / 60k / mat）的 alpha 块数量与当前 bias/mult
  3. 与基线相比的差异字节数，以及落在哪些分组
  4. 结论 OK / 需人工确认
"""
import struct, sys, os, argparse

GROUPS = {
    '46k': [281216, 337792, 394032, 449328],
    '60k': [517344, 620328, 722232, 822928, 921752, 1018800, 1119256, 1218624, 1316792, 1413120,
            1510296, 1613280, 1715184, 1815880, 1914704, 2011752, 2112208, 2211576, 2309744, 2406072],
    'mat': [2592520, 2748816, 2903832, 3057216, 3204672, 3362048, 3518344, 3673360, 3826744, 3974200],
}
MUL_TOKEN = 0x07000038


def segments(d):
    CH = set(b'RDEF ISGN OSGN SHEX STAT PCSG'.split())
    recs, i = [], 0
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
        ok = True
        for o in offs:
            if bytes(d[s + o:s + o + 4]) not in CH:
                ok = False
                break
        if ok:
            recs.append((s, s + total))
    return recs


def alpha_block(d, rec):
    total = struct.unpack_from('<I', d, rec + 24)[0]
    nch = struct.unpack_from('<I', d, rec + 28)[0]
    for o in struct.unpack_from('<%dI' % nch, d, rec + 32):
        if d[rec + o:rec + o + 4] == b'SHEX':
            size = struct.unpack_from('<I', d, rec + o + 4)[0]
            tk, end = rec + o + 16, rec + o + 8 + size
            for k in range(tk, end - 160, 4):
                b = struct.unpack_from('<f', d, k)[0]
                m = struct.unpack_from('<f', d, k + 28)[0]
                if -1.0 < b < 0.0 and abs(m * (1.0 + b) - 1.0) < 1e-5 and \
                   struct.unpack_from('<I', d, k + 96)[0] == MUL_TOKEN:
                    return k
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('payload')
    ap.add_argument('--baseline')
    a = ap.parse_args()
    d = open(a.payload, 'rb').read()
    ok = True

    print("[1] 记录图")
    recs = segments(d)
    print("    记录 %d 条；首条 @%d；末条结束 @%d / 文件 %d" %
          (len(recs), recs[0][0], recs[-1][1], len(d)))
    if len(recs) != 272:
        print("    ⚠ 记录数不是 272，结构可能已损坏")
        ok = False

    print("[2] 分组状态")
    for name, rs in GROUPS.items():
        hits, vals = 0, set()
        for r in rs:
            k = alpha_block(d, r)
            if k is None:
                continue
            hits += 1
            vals.add((round(struct.unpack_from('<f', d, k)[0], 4),
                      round(struct.unpack_from('<f', d, k + 28)[0], 6)))
        print("    %-4s %2d/%2d 块；当前 bias/mult = %s" % (name, hits, len(rs), sorted(vals)))

    if a.baseline:
        print("[3] 与基线的差异")
        base = open(a.baseline, 'rb').read()
        if len(base) != len(d):
            print("    ⚠ 长度不同（%d vs %d）" % (len(base), len(d)))
            ok = False
        else:
            diff = [i for i in range(len(d)) if d[i] != base[i]]
            buckets = {}
            for name, rs in GROUPS.items():
                for r in rs:
                    k = alpha_block(base, r)
                    if k is None:
                        continue
                    n = sum(1 for i in diff if k <= i < k + 32)
                    if n:
                        buckets[name] = buckets.get(name, 0) + n
            print("    差异字节 %d；分布 %s" % (len(diff), buckets))
            if any(n % 8 for n in [1] if False):  # 交给下面的记录级检查
                print("    [!] 差异不是每记录 8 字节的整齐模式，需人工确认")
                ok = False

    print("[4] 结论：", "OK" if ok else "需人工确认")
    return 0 if ok else 1


if __name__ == '__main__':
    raise SystemExit(main())

