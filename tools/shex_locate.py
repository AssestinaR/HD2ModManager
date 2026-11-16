#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""SHEX 指令定位器：**只按长度字段遍历**（不解码操作数），因此不会像全解码那样跑偏。

用途：把 fxc 的反汇编行号与 SHEX 内的字节偏移对应起来，从而精确改写某一条指令。

用法：
    python tools/shex_locate.py <记录.dxbc> [--show-head] [--grep 关键字]
"""
import argparse
import struct
import sys

OPNAMES = {
    0: 'add', 14: 'div', 15: 'dp2', 16: 'dp3', 17: 'dp4', 25: 'exp', 31: 'if', 47: 'log',
    50: 'mad', 51: 'min', 52: 'mov', 53: 'movc', 54: 'mul', 56: 'nop', 59: 'popc', 61: 'ret',
    68: 'sample', 71: 'sample_l', 74: 'sqrt', 75: 'switch', 76: 'sincos',
    93: 'dcl_input', 99: 'dcl_output', 101: 'dcl_temps', 106: 'dcl_globalflags',
}
NOOP_TOKEN = (1 << 24) | 56          # nop：opcode 56，长度 1 dword


def shex_of(rec):
    total, nch = struct.unpack_from('<2I', rec, 24)
    offs = sorted(struct.unpack_from('<%dI' % nch, rec, 32))
    for k, o in enumerate(offs):
        if rec[o:o + 4] == b'SHEX':
            size = struct.unpack_from('<I', rec, o + 4)[0]
            return rec[o + 16:o + 8 + size]
    return None


def walk(shex):
    """按长度字段逐条前进；返回 [(byte_off, dwords, opcode)]"""
    out = []
    i, n = 0, len(shex) // 4
    toks = struct.unpack_from('<%dI' % n, shex, 0)
    while i < n:
        t = toks[i]
        op = t & 0x7FF
        ln = (t >> 24) & 0x7F
        if ln == 0:
            out.append((i * 4, 1, -1))     # 长度 0：非法，记一笔并前进 1
            i += 1
            continue
        out.append((i * 4, ln, op))
        if op == 61:                       # ret 之后正常应结束
            i += ln
            if i * 4 >= len(shex):
                break
            continue
        i += ln
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('dxbc')
    ap.add_argument('--show-head', type=int, default=0, help='打印前 N 条')
    ap.add_argument('--tail', type=int, default=0, help='打印后 N 条')
    args = ap.parse_args()

    rec = open(args.dxbc, 'rb').read()
    shex = shex_of(rec)
    if shex is None:
        raise SystemExit('没有 SHEX')
    items = walk(shex)
    print('SHEX 指令区 %d B = %d dwords' % (len(shex), len(shex) // 4))
    print('按长度字段遍历出 %d 条指令（最后一条 opcode=%d，61=ret）' % (len(items), items[-1][2]))
    bad = [x for x in items if x[2] < 0]
    print('长度为 0 的非法项: %d' % len(bad))
    tot = sum(d for _, d, _ in items)
    print('dword 总和 %d / %d' % (tot, len(shex) // 4))
    if args.show_head:
        for off, ln, op in items[:args.show_head]:
            print('  +%-6d %-4ddw %s' % (off, ln, OPNAMES.get(op, 'op%d' % op)))
    if args.tail:
        print('  ...')
        for off, ln, op in items[-args.tail:]:
            print('  +%-6d %-4ddw %s' % (off, ln, OPNAMES.get(op, 'op%d' % op)))


if __name__ == '__main__':
    sys.exit(main())
