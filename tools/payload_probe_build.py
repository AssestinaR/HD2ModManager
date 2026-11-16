#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""给 HD2 材质载荷里指定的几组记录换上「纯色探针」，并保持记录结构逐字节同构。

为什么要这么做
--------------
一份载荷里有几十组 DXBC 记录，只有其中一组是游戏里真正画的 pass。想一次部署就同时回答
「载荷到底有没有被读」和「哪一组是可见 pass」，最快的办法是**给每组一个不同的颜色**：

    一组 -> 纯红，另一组 -> 纯绿，第三组 -> 纯蓝 ……  一次进游戏，看到哪个颜色就知道是哪一组。

本工具的硬约束（都是踩出来的）：
  1. **记录总长不变**，五个 chunk 的**名字、顺序、长度全部沿用 donor**，只换 SHEX 内容；
     SHEX 不足处补真 NOP `0x0100003A`（写成 0x01000038 会变成另一条指令 -> 非法着色器）。
  2. 记录头 `u32@20 = 1`，校验和按微软定制 MD5 复签（引擎不校验，但复签后 fxc 才能离线反汇编自检）。
  3. 探针 HLSL 的输出签名**按 donor 的 OSGN 生成**：有几个 SV_Target、每个几分量，都照抄掩码。
     读集为空（不声明任何输入），因此永远不会触碰 G3（读分量 ⊆ ISGN 掩码）。

用法
----
    # 1) 先看有哪些组
    python tools/payload_map.py <载荷> --json groups.json

    # 2) 按组号上色（组号来自 --json；可重复）
    python tools/payload_probe_build.py --payload <载荷> --out <输出> \
        --paint 7:1,0,0,1 --paint 43:0,1,0,1 --paint 39:0,0,1,1

    # 3) 直接包成"覆盖某个材质"的 patch（模板 TOC 从已有单材质 patch 复制）
    python tools/payload_probe_build.py --payload <载荷> --out <输出载荷> \
        --paint 7:1,0,0,1 --patch-template <已有 patch_0> --patch-out <新 patch_0>
"""
import argparse
import collections
import os
import struct
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import payload_map as pm          # 组号必须与 payload_map.py 完全一致，所以复用同一套分组

FXC = r'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\fxc.exe'
NOP = 0x0100003A
CH = set(b'RDEF ISGN OSGN SHEX STAT PCSG ILDN SPDB IFCE RD11 PRIV'.split())
SV_NAMES = pm.SV_NAMES

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


def chunks_of(d):
    total, nch = struct.unpack_from('<2I', d, 24)
    offs = sorted(struct.unpack_from('<%dI' % nch, d, 32))
    order, ck = [], {}
    for k, o in enumerate(offs):
        end = offs[k + 1] if k + 1 < len(offs) else total
        name = bytes(d[o:o + 4]).decode()
        order.append(name)
        ck[name] = bytes(d[o + 8:end])
    return ck, order


def records(d):
    out, i = [], 0
    while True:
        s = d.find(b'DXBC', i)
        if s < 0:
            return out
        i = s + 4
        if s + 32 > len(d):
            continue
        total, nch = struct.unpack_from('<2I', d, s + 24)
        if not (32 < total < (1 << 23)) or not (1 <= nch <= 16) or s + total > len(d):
            continue
        try:
            offs = sorted(struct.unpack_from('<%dI' % nch, d, s + 32))
        except struct.error:
            continue
        if any(o < 32 or o >= total for o in offs):
            continue
        ck, ok = {}, True
        for k, o in enumerate(offs):
            fcc = bytes(d[s + o:s + o + 4])
            if fcc not in CH:
                ok = False
                break
            end = offs[k + 1] if k + 1 < len(offs) else total
            ck[fcc.decode()] = bytes(d[s + o + 8:s + end])
        if ok:
            out.append({'off': s, 'size': total, 'chunks': ck})
    return out


def sig_entries(chunk):
    return pm.sig_semantics(chunk)


def groups_of(pay):
    """与 payload_map.py 完全相同的分组与编号（按首次出现顺序）。"""
    g = collections.OrderedDict()
    for idx, r in enumerate(records(pay)):
        key = (pm.stage_of(r), len(r['chunks'].get('ISGN') or b''),
               len(r['chunks'].get('OSGN') or b''), len(r['chunks'].get('SHEX') or b''),
               pm.targets(r), r['size'])
        g.setdefault(key, []).append(idx)
    return g


def hlsl_for(r, color):
    """按 donor OSGN 生成"只写颜色"的 PS：每个 SV_Target 一个输出，分量数照抄掩码。"""
    entries = pm.sig_semantics(r['chunks'].get('OSGN') or b'')
    targets = [s for s in entries if s['name'].upper().replace('_', '') == 'SVTARGET']
    targets.sort(key=lambda s: s['index'])
    if not targets:
        raise SystemExit('donor 的 OSGN 里没有 SV_Target，不是像素着色器')
    lines = ['// 自动生成：纯色探针，读集为空，输出签名按 donor OSGN 掩码生成',
             'struct PSOut', '{']
    widths = []
    for k, t in enumerate(targets):
        n = bin(t['mask']).count('1') or 4
        ty = {1: 'float', 2: 'float2', 3: 'float3', 4: 'float4'}.get(n)
        if ty is None:
            raise SystemExit('OSGN 目标 %d 的分量数 %d 无法处理' % (t['index'], n))
        widths.append(n)
        lines.append('    %-6s o%d : SV_Target%d;' % (ty, k, t['index']))
    lines += ['};', '', 'PSOut main()', '{', '    PSOut o;']
    for k, n in enumerate(widths):
        vals = [color[i] if i < n else 0.0 for i in range(n)]

        def lit(v):
            t = ('%.9g' % float(v))
            return t if ('.' in t or 'e' in t or 'E' in t) else t + '.0'

        s = ', '.join(lit(v) + 'f' for v in vals)
        ty = {1: 'float', 2: 'float2', 3: 'float3', 4: 'float4'}[n]
        lines.append('    o.o%d = %s(%s);' % (k, ty, s))
    lines += ['    return o;', '}', '']
    return '\n'.join(lines), len(targets)


def pad_shex(shex, target):
    if len(shex) > target:
        raise SystemExit('SHEX %d B > donor %d B' % (len(shex), target))
    head = struct.unpack_from('<2I', shex, 0)
    body = bytearray(shex[8:])
    need = target - len(shex)
    body += struct.pack('<%dI' % (need // 4), *([NOP] * (need // 4)))
    out = bytearray(target)
    struct.pack_into('<II', out, 0, head[0], (8 + len(body)) // 4)
    out[8:] = body
    return bytes(out), need // 4


def compile_ps(src, tag):
    base = os.path.join(os.environ.get('TEMP', '.'), 'hd2probe_%s' % tag)
    open(base + '.hlsl', 'w', encoding='utf-8').write(src)
    r = subprocess.run([FXC, '/nologo', '/T', 'ps_5_0', '/E', 'main', '/Fo', base + '.dxbc',
                        base + '.hlsl'], capture_output=True, text=True)
    if r.returncode != 0 or not os.path.isfile(base + '.dxbc'):
        print(r.stdout[-1200:], r.stderr[-1200:])
        raise SystemExit('fxc 编译失败')
    return base + '.dxbc'


def rebuild_record(donor_rec_bytes, ours_ck, stat_pref):
    ck, order = chunks_of(donor_rec_bytes)
    shex, nnop = pad_shex(ours_ck['SHEX'], len(ck['SHEX']))
    stat = ours_ck['STAT'] if (stat_pref == 'ours' and len(ours_ck['STAT']) == len(ck['STAT'])) else ck['STAT']
    parts = []
    for name in order:
        parts.append((name, shex if name == 'SHEX' else (stat if name == 'STAT' else ck[name])))
    nch = len(parts)
    start = (32 + nch * 4 + 3) & ~3
    total = start + sum(8 + len(v) for _, v in parts)
    if total != len(donor_rec_bytes):
        raise SystemExit('重建后长度 %d != donor %d（STAT 尺寸不一致？）' % (total, len(donor_rec_bytes)))
    rec = bytearray(total)
    rec[0:4] = b'DXBC'
    rec[4:20] = donor_rec_bytes[4:20]
    struct.pack_into('<II', rec, 20, 1, total)
    struct.pack_into('<I', rec, 28, nch)
    cur = start
    for i, (k, v) in enumerate(parts):
        struct.pack_into('<I', rec, 32 + 4 * i, cur)
        rec[cur:cur + 4] = k.encode()
        struct.pack_into('<I', rec, cur + 4, len(v))
        rec[cur + 8:cur + 8 + len(v)] = v
        cur += 8 + len(v)
    assert cur == total
    rec[4:20] = dxbc_checksum(bytes(rec))
    return bytes(rec), nnop


def verify_record(rec, tag):
    base = os.path.join(os.environ.get('TEMP', '.'), 'hd2probe_%s' % tag)
    open(base + '.rec.dxbc', 'wb').write(rec)
    r = subprocess.run([FXC, '/nologo', '/dumpbin', '/Fc', base + '.asm', base + '.rec.dxbc'],
                       capture_output=True, text=True)
    if r.returncode != 0 or not os.path.isfile(base + '.asm') or os.path.getsize(base + '.asm') == 0:
        print('  ✗ fxc 反汇编失败:', (r.stdout or '')[-400:], (r.stderr or '')[-400:])
        return False, None
    text = open(base + '.asm', encoding='utf-8', errors='replace').read()
    return True, text


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--payload', required=True, help='原始载荷（.gpu / 切好的 .gpu_resources 片段）')
    ap.add_argument('--out', required=True, help='输出的载荷（长度必然与输入相同）')
    ap.add_argument('--paint', action='append', default=[],
                    help='组号:r,g,b,a 例如 7:1,0,0,1（可重复）')
    ap.add_argument('--stat', choices=['ours', 'donor'], default='ours')
    ap.add_argument('--patch-template', default=None, help='复制这个 patch_0 的 TOC 来包成可部署 patch')
    ap.add_argument('--patch-out', default=None, help='输出的 patch_0 路径（.gpu_resources 自动加后缀）')
    ap.add_argument('--keep-hlsl', default=None, help='把这些 HLSL 写到该目录，便于复核')
    args = ap.parse_args()

    pay = bytearray(open(args.payload, 'rb').read())
    recs = records(pay)
    groups = groups_of(pay)
    # 组号与 tools/payload_map.py 完全一致：按「首次出现顺序」编号（不是按条数排序）
    order = list(groups.items())
    index = {gi: kv for gi, kv in enumerate(order)}

    if not args.paint:
        raise SystemExit('至少要给一个 --paint 组号:颜色')
    for spec in args.paint:
        gi_s, col_s = spec.split(':')
        gi = int(gi_s)
        if gi not in index:
            raise SystemExit('组号 %d 不存在（0..%d）' % (gi, len(index) - 1))
        key, idxs = index[gi]
        color = [float(x) for x in col_s.split(',')]
        if len(color) == 3:
            color.append(1.0)
        first = recs[idxs[0]]
        donor = bytes(pay[first['off']:first['off'] + first['size']])
        src, ntar = hlsl_for(first, color)
        if args.keep_hlsl:
            os.makedirs(args.keep_hlsl, exist_ok=True)
            open(os.path.join(args.keep_hlsl, 'g%d.hlsl' % gi), 'w', encoding='utf-8').write(src)
        blob = compile_ps(src, 'g%d' % gi)
        ours, _ = chunks_of(open(blob, 'rb').read())
        rec, nnop = rebuild_record(donor, ours, args.stat)
        ok, text = verify_record(rec, 'g%d' % gi)
        print('组 #%-2d  x%-3d 记录 %-6d B  %d 个 SV_Target  颜色 %s  NOP %d  fxc %s' % (
            gi, len(idxs), first['size'], ntar, color, nnop, '✔' if ok else '✗'))
        if ok:
            tail = [l.strip() for l in text.splitlines() if l.strip()][-3:]
            print('        尾部: %s' % ' | '.join(tail))
        if not ok:
            raise SystemExit('探针记录非法，停止')
        for i in idxs:
            r = recs[i]
            assert r['size'] == len(rec)
            pay[r['off']:r['off'] + r['size']] = rec

    open(args.out, 'wb').write(bytes(pay))
    print('写出 %s（%d B，与原载荷等长: %s）' % (args.out, len(pay), len(pay) == os.path.getsize(args.payload)))

    if args.patch_template:
        if not args.patch_out:
            raise SystemExit('给了 --patch-template 就要给 --patch-out')
        toc = open(args.patch_template, 'rb').read()
        open(args.patch_out, 'wb').write(toc)
        open(args.patch_out + '.gpu_resources', 'wb').write(bytes(pay))
        # 直接改 patch 头部里的 gpu 大小字段过于脆弱：要求模板本来就是这个载荷的包
        print('patch  -> %s (%d B)' % (args.patch_out, len(toc)))
        print('gpu    -> %s.gpu_resources (%d B)' % (args.patch_out, len(pay)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
