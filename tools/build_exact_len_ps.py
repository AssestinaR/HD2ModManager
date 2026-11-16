#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# ⛔ 已过时（2026-11-16 标记）：本文开头的前提「整段换 SHEX 不生效，所以要精确等长」是**错的**。
#    真正的原因是 NOP 指令 token 写错（0x01000038 → 0x0100003A）与记录头 u32@20 必须为 1。
#    现行工具：tools/build_signed_ps.py（编译 + 定长 NOP 填充 + 复签 + fxc 验证）。
#    本文件只作历史记录，不要再拿它产出要部署的记录。
"""造一条「SHEX 长度与原版完全相同」的自制着色器记录。

为什么这么做：已经验证「改立即数」生效，而「整段换 SHEX」不生效。
两者的关键差别是 **SHEX 长度 / STAT 自洽性**。所以这里：
  1. 用足够多的死代码把 SHEX 撑到接近目标长度；
  2. 用 `nop`(4 B) 精确补齐到目标的**字节数**；
  3. 修正 SHEX 负载头部第二个 dword（= 负载总 dword 数）；
  4. 记录里用**我们自己的 STAT**（与我们的 SHEX 配套），RDEF/ISGN/OSGN 用原版。

用法：
    python tools/build_exact_len_ps.py --hlsl <a.hlsl> --payload <侧车> \
        --donor-size 60504 --shex-size 51828 --out <输出侧车>
"""
import argparse
import os
import struct
import subprocess
import sys

FXC = r'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\fxc.exe'
NOP = 0x01000038
CH = set(b'RDEF ISGN OSGN SHEX STAT PCSG ILDN SPDB IFCE RD11 PRIV'.split())


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


def pad_shex(shex, target):
    """把 SHEX 负载补到 target 字节：末尾加 nop，并修正头部的 dword 计数。"""
    if len(shex) > target:
        raise SystemExit('SHEX %d B 超过目标 %d B（把循环次数调小）' % (len(shex), target))
    head = struct.unpack_from('<2I', shex, 0)
    body = shex[8:]
    need = target - len(shex)
    if need % 4:
        raise SystemExit('差额 %d 不是 4 的倍数' % need)
    body += struct.pack('<%dI' % (need // 4), *([NOP] * (need // 4)))
    total_dwords = (8 + len(body)) // 4
    out = bytearray(target)
    struct.pack_into('<II', out, 0, head[0], total_dwords)
    out[8:] = body
    return bytes(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--hlsl', required=True)
    ap.add_argument('--payload', required=True)
    ap.add_argument('--donor-size', type=int, required=True)
    ap.add_argument('--shex-size', type=int, required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--stat', choices=['ours', 'donor'], default='ours')
    args = ap.parse_args()

    blob = compile_ps(args.hlsl)
    ours = chunks_of(open(blob, 'rb').read())
    print('编译产物: SHEX=%d ISGN=%d OSGN=%d STAT=%d' % (
        len(ours.get('SHEX', b'')), len(ours.get('ISGN', b'')), len(ours.get('OSGN', b'')), len(ours.get('STAT', b''))))
    shex = pad_shex(ours['SHEX'], args.shex_size)
    print('SHEX 补齐到 %d B（头部 dword 计数改为 %d）' % (len(shex), struct.unpack_from('<I', shex, 4)[0]))

    pay = bytearray(open(args.payload, 'rb').read())
    tg = records(pay, args.donor_size)
    ref = tg[0]['ck']
    parts = [('RDEF', ref['RDEF']), ('ISGN', ref['ISGN']), ('OSGN', ref['OSGN']),
             ('SHEX', shex), ('STAT', ours['STAT'] if args.stat == 'ours' else ref['STAT'])]
    nch = len(parts)
    start = (32 + nch * 4 + 3) & ~3
    total = start + sum(8 + len(v) for _, v in parts)
    print('构造总长 %d / 目标 %d  %s' % (total, args.donor_size, '正好' if total == args.donor_size else '差 %+d' % (args.donor_size - total)))
    if total != args.donor_size:
        raise SystemExit('尺寸不符，放弃')
    rec = bytearray(total)
    rec[0:4] = b'DXBC'
    rec[4:20] = pay[tg[0]['off'] + 4:tg[0]['off'] + 20]
    struct.pack_into('<II', rec, 20, 1, total)   # u32@20 必须为 1（676 条原版记录无一例外）
    struct.pack_into('<I', rec, 28, nch)
    cur = start
    for i, (k, v) in enumerate(parts):
        struct.pack_into('<I', rec, 32 + 4 * i, cur)
        rec[cur:cur + 4] = k.encode()
        struct.pack_into('<I', rec, cur + 4, len(v))
        rec[cur + 8:cur + 8 + len(v)] = v
        cur += 8 + len(v)
    assert cur == total
    for r in tg:
        pay[r['off']:r['off'] + args.donor_size] = bytes(rec)
    open(args.out, 'wb').write(bytes(pay))
    print('替换 %d 份 -> %s（%d B，长度未变: %s）' % (
        len(tg), args.out, len(pay), len(pay) == os.path.getsize(args.payload)))


if __name__ == '__main__':
    sys.exit(main())
