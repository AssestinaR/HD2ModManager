#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""构建"自制像素着色器"记录：等长 + 正确 NOP 填充 + 复签校验和（用于离线自验证）。

踩过的坑（都在这里被修掉）：
  · NOP 的指令 token 是 **0x0100003A**（opcode 58）。写成 0x01000038 会变成另一条指令，
    出现在 ret 之后就是非法着色器 —— 之前所有"等长自制 SHEX"都栽在这里。
  · DXBC 记录头 u32@20 必须是 **1**（676 条原版记录无一例外）。
  · 校验和是微软定制 MD5（见 .dsh/tmp/audit_checksum.py），**引擎不校验**
    （作者自己的记录就是坏校验和却能用），但**fxc 会校验**，所以复签才能离线反汇编验证。

用法：
    python tools/build_signed_ps.py --hlsl a.hlsl --payload 侧车 --donor-size 60504 \
        --shex-size 51828 --out 输出侧车 [--stat ours|donor] [--verify]
"""
import argparse
import os
import struct
import subprocess
import sys

FXC = r'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\fxc.exe'
NOP = 0x0100003A          # ← 正确值
CH = set(b'RDEF ISGN OSGN SHEX STAT PCSG ILDN SPDB IFCE RD11 PRIV'.split())

_K = [int(abs(__import__("math").sin(i + 1)) * (2 ** 32)) & 0xFFFFFFFF for i in range(64)]
_S = [7, 12, 17, 22] * 4 + [5, 9, 14, 20] * 4 + [4, 11, 16, 23] * 4 + [6, 10, 15, 21] * 4


def _compress(state, block):
    A, B, C, D = state
    M = list(struct.unpack("<16I", block))
    a, b, c, d = A, B, C, D
    for i in range(64):
        if i < 16:
            f = (b & c) | ((~b) & d); g = i
        elif i < 32:
            f = (d & b) | ((~d) & c); g = (5 * i + 1) % 16
        elif i < 48:
            f = b ^ c ^ d; g = (3 * i + 5) % 16
        else:
            f = c ^ (b | (~d)); g = (7 * i) % 16
        f = (f + a + _K[i] + M[g]) & 0xFFFFFFFF
        a, d, c, b = d, c, b, (b + (((f << _S[i]) | (f >> (32 - _S[i]))) & 0xFFFFFFFF)) & 0xFFFFFFFF
    return [(A + a) & 0xFFFFFFFF, (B + b) & 0xFFFFFFFF, (C + c) & 0xFFFFFFFF, (D + d) & 0xFFFFFFFF]


def dxbc_checksum(buf):
    """微软定制 MD5：范围 buf[20:]，特殊 padding。已用本体 adv_main.cso 验证逐字节一致。"""
    data = buf[20:]
    length = len(data)
    num_bits = (length * 8) & 0xFFFFFFFF
    num_bits2 = ((num_bits >> 2) | 1) & 0xFFFFFFFF
    leftover = length % 64
    stream = bytearray(data[:length - leftover])
    if leftover >= 56:
        stream += data[length - leftover:]
        stream += b"\x80" + b"\x00" * (64 - leftover - 1)
        blk = bytearray(64)
        struct.pack_into("<I", blk, 0, num_bits)
        struct.pack_into("<I", blk, 60, num_bits2)
        stream += blk
    else:
        stream += struct.pack("<I", num_bits)
        stream += data[length - leftover:]
        pad = 64 - leftover - 4
        blk = bytearray(64)
        blk[0] = 0x80
        struct.pack_into("<I", blk, pad - 4, num_bits2)
        stream += blk[:pad]
    st = [0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476]
    for i in range(0, len(stream), 64):
        st = _compress(st, bytes(stream[i:i + 64]))
    return b"".join(struct.pack("<I", x) for x in st)


def compile_ps(hlsl, entry='main'):
    out = hlsl + '.dxbc'
    r = subprocess.run([FXC, '/nologo', '/T', 'ps_5_0', '/E', entry, '/Fo', out, hlsl],
                       capture_output=True, text=True)
    if r.returncode != 0 or not os.path.isfile(out):
        print(r.stdout[-1500:], r.stderr[-1500:])
        raise SystemExit('fxc 编译失败')
    return out


def chunks_of(d):
    total, nch = struct.unpack_from('<2I', d, 24)
    offs = sorted(struct.unpack_from('<%dI' % nch, d, 32))
    ck = {}
    for k, o in enumerate(offs):
        end = offs[k + 1] if k + 1 < len(offs) else total
        ck[bytes(d[o:o + 4]).decode()] = bytes(d[o + 8:end])
    return ck


def records(d, want):
    out, i = [], 0
    while True:
        s = d.find(b'DXBC', i)
        if s < 0:
            return out
        i = s + 4
        if s + 32 > len(d):
            continue
        total, nch = struct.unpack_from('<2I', d, s + 24)
        if total != want:
            continue
        offs = sorted(struct.unpack_from('<%dI' % nch, d, s + 32))
        if not all(bytes(d[s + o:s + o + 4]) in CH for o in offs):
            continue
        ck = {}
        for k, o in enumerate(offs):
            end = offs[k + 1] if k + 1 < len(offs) else total
            ck[bytes(d[s + o:s + o + 4]).decode()] = bytes(d[s + o + 8:s + end])
        out.append({'off': s, 'ck': ck})
    return out


def chunk_order(d, off):
    """按记录里的 chunk 表返回 chunk 名字的顺序。"""
    nch, = struct.unpack_from('<I', d, off + 28)
    offs = sorted(struct.unpack_from('<%dI' % nch, d, off + 32))
    return [bytes(d[off + o:off + o + 4]).decode('latin1') for o in offs]


def pad_shex(shex, target):
    if len(shex) > target:
        raise SystemExit('SHEX %d B > 目标 %d B' % (len(shex), target))
    head = struct.unpack_from('<2I', shex, 0)
    body = bytearray(shex[8:])
    need = target - len(shex)
    assert need % 4 == 0
    body += struct.pack('<%dI' % (need // 4), *([NOP] * (need // 4)))
    out = bytearray(target)
    struct.pack_into('<II', out, 0, head[0], (8 + len(body)) // 4)
    out[8:] = body
    return bytes(out), need // 4


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--hlsl', required=True)
    ap.add_argument('--payload', required=True)
    ap.add_argument('--donor-size', type=int, required=True)
    ap.add_argument('--shex-size', type=int, required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--stat', choices=['ours', 'donor'], default='ours')
    ap.add_argument('--tail', choices=['ours', 'donor'], default='ours',
                    help='SHEX 之后的尾部 chunk 用哪边的：ours=我们的 STAT；donor=照抄 donor 的其余 chunk（含 PRIV）')
    ap.add_argument('--no-sign', action='store_true')
    ap.add_argument('--verify', action='store_true')
    args = ap.parse_args()

    blob = compile_ps(args.hlsl)
    ours = chunks_of(open(blob, 'rb').read())
    shex, nnop = pad_shex(ours['SHEX'], args.shex_size)
    print('编译: SHEX=%d ISGN=%d OSGN=%d STAT=%d ; 用 %d 个 NOP(0x%08X) 补到 %d B' % (
        len(ours['SHEX']), len(ours['ISGN']), len(ours['OSGN']), len(ours['STAT']),
        nnop, NOP, len(shex)))

    pay = bytearray(open(args.payload, 'rb').read())
    tg = records(pay, args.donor_size)
    ref = tg[0]['ck']
    ref_order = chunk_order(pay, tg[0]['off'])
    parts = [('RDEF', ref['RDEF']), ('ISGN', ref['ISGN']), ('OSGN', ref['OSGN']),
             ('SHEX', shex)]
    if args.tail == 'donor':
        # 照抄 donor 在 SHEX 之后的 chunk（顺序与名字都要一致）。
        # 有的记录尾部是 PRIV（8,184 B）而不是 STAT；丢掉它会让记录短一截、尺寸对不上。
        for name in ref_order[3:]:
            if name == 'SHEX':
                continue
            parts.append((name, ref[name]))
    else:
        parts.append(('STAT', ours['STAT'] if args.stat == 'ours' else ref['STAT']))
    nch = len(parts)
    start = (32 + nch * 4 + 3) & ~3
    total = start + sum(8 + len(v) for _, v in parts)
    print('构造 %d B / 目标 %d B  %s' % (total, args.donor_size, '✔' if total == args.donor_size else '差 %+d' % (args.donor_size - total)))
    if total != args.donor_size:
        raise SystemExit('尺寸不符')

    rec = bytearray(total)
    rec[0:4] = b'DXBC'
    rec[4:20] = pay[tg[0]['off'] + 4:tg[0]['off'] + 20]
    struct.pack_into('<II', rec, 20, 1, total)      # u32@20 = 1
    struct.pack_into('<I', rec, 28, nch)
    cur = start
    for i, (k, v) in enumerate(parts):
        struct.pack_into('<I', rec, 32 + 4 * i, cur)
        rec[cur:cur + 4] = k.encode()
        struct.pack_into('<I', rec, cur + 4, len(v))
        rec[cur + 8:cur + 8 + len(v)] = v
        cur += 8 + len(v)
    assert cur == total

    if not args.no_sign:
        rec[4:20] = dxbc_checksum(bytes(rec))
        print('已复签: %s' % bytes(rec[4:20]).hex())

    for r in tg:
        pay[r['off']:r['off'] + args.donor_size] = bytes(rec)
    open(args.out, 'wb').write(bytes(pay))
    print('替换 %d 份 -> %s（%d B，长度未变: %s）' % (
        len(tg), args.out, len(pay), len(pay) == os.path.getsize(args.payload)))

    if args.verify:
        single = args.out + '.rec0.dxbc'
        open(single, 'wb').write(bytes(rec))
        asm = args.out + '.rec0.asm'
        r = subprocess.run([FXC, '/nologo', '/dumpbin', '/Fc', asm, single], capture_output=True, text=True)
        if r.returncode != 0 or not os.path.isfile(asm) or os.path.getsize(asm) == 0:
            print('  ✗ fxc 反汇编失败（产物非法）')
            print((r.stdout or '')[-600:], (r.stderr or '')[-600:])
            return 1
        text = open(asm, encoding='utf-8', errors='replace').read()
        print('  ✓ fxc 反汇编成功（%d 行）' % text.count('\n'))
        tail = [l.rstrip() for l in text.splitlines() if l.strip()][-8:]
        print('  --- 尾部 8 行 ---')
        for l in tail:
            print('    | %s' % l.strip())
        bad = [l for l in text.splitlines() if l.strip() == 'mul' or l.strip().startswith('mul ')]
        print('  ret 之后是否还有指令: %s' % ('检查尾部即可'))
    return 0


if __name__ == '__main__':
    sys.exit(main())
