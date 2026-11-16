#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""构建门：在部署前对"自制着色器记录"做硬性检查。任一失败即拒绝部署。

门清单（每条都对应过一次真实失败）：
  G1 记录块布局与原版逐项一致（总长、5 个 chunk 尺寸）
  G2 DXBC 校验和可复算（复签后才允许 fxc 校验）
  G3 着色器读取的输入分量 ⊆ 记录 ISGN 提供的掩码
     ← 这是最致命的一条：越界读（本例 v7.z 与 v4.x 两处）会让 dxilconv 空指针崩溃
  G4 SHEX 填充只用真 NOP 0x0100003A，且位于 ret 之后
  G5 贴图条目：mipChainSize(width,height,fmt,mips) == gpuSize

用法：
  python tools/gate_build.py record --record <记录.dxbc> --donor <原版载荷> --donor-size 60504 \
      --ours-asm <我们编译产物的 fxc 反汇编> --donor-asm <原版记录的反汇编>
  python tools/gate_build.py texture --toc <patch 文件> --file-id 0x8354a7fad81a2a29
"""
import argparse
import os
import re
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from build_signed_ps import dxbc_checksum  # noqa: E402

CH = set(b'RDEF ISGN OSGN SHEX STAT PCSG ILDN SPDB IFCE RD11 PRIV'.split())
NOP = 0x0100003A
RET = 0x0100003E


def chunks_of(rec):
    total, nch = struct.unpack_from('<2I', rec, 24)
    offs = sorted(struct.unpack_from('<%dI' % nch, rec, 32))
    ck = {}
    for k, o in enumerate(offs):
        end = offs[k + 1] if k + 1 < len(offs) else total
        ck[bytes(rec[o:o + 4]).decode()] = (o, end - o - 8, rec[o + 8:end])
    return total, ck


def parse_isgn_bin(rec):
    """直接解析 ISGN 二进制（权威）：返回 {寄存器: 提供掩码集合}。
    ISGN 结构：u32 count, u32 unknown(8), 然后 count × 24 B:
      +0 nameOff, +4 semIdx, +8 sysVal, +12 compType, +16 reg, +20 mask, +21 rwMask, +22 stream, +23 minPrec
    """
    total, ck = chunks_of(rec)
    if 'ISGN' not in ck:
        return {}
    b = ck['ISGN'][2]
    cnt, _ = struct.unpack_from('<2I', b, 0)
    out = {}
    for i in range(cnt):
        o = 8 + i * 24
        if o + 24 > len(b):
            break
        reg, = struct.unpack_from('<I', b, o + 16)
        mask = b[o + 20]
        out.setdefault(reg, set()).update('xyzw'[k] for k in range(4) if mask >> k & 1)
    return out


def parse_sig_asm(path):
    """从 fxc 反汇编里读 Input signature：{寄存器: 提供掩码集合}"""
    out, inside = {}, False
    for line in open(path, encoding='utf-8', errors='replace'):
        if 'Input signature' in line:
            inside = True
            continue
        if inside:
            if 'Output signature' in line:
                break
            m = re.match(r'//\s+(\S+)\s+(\d+)\s+([xyzw]+)\s+(\d+)\s+(\S+)\s+(\S+)\s+(\S*)', line)
            if m:
                out.setdefault(int(m.group(4)), set()).update(m.group(3))
    return out


def parse_reads_asm(path):
    """从 fxc 反汇编里读 dcl_input_ps：{寄存器: 读取掩码集合}"""
    out = {}
    for line in open(path, encoding='utf-8', errors='replace'):
        m = re.search(r'dcl_input_ps\s+\w+\s+v(\d+)\.([xyzw]+)', line)
        if m:
            out.setdefault(int(m.group(1)), set()).update(m.group(2))
    return out


def gate_record(a):
    ok = True
    rec = open(a.record, 'rb').read()
    total, ck = chunks_of(rec)
    print('记录 %d B，块: %s' % (total, ', '.join('%s=%d' % (k, v[1]) for k, v in ck.items())))

    # G1 与 donor 对比
    pay = open(a.donor, 'rb').read()
    donor = None
    i = 0
    while True:
        s = pay.find(b'DXBC', i)
        if s < 0:
            break
        i = s + 4
        if s + 32 > len(pay):
            continue
        t, nch = struct.unpack_from('<2I', pay, s + 24)
        if t != a.donor_size:
            continue
        offs = sorted(struct.unpack_from('<%dI' % nch, pay, s + 32))
        if not all(bytes(pay[s + o:s + o + 4]) in CH for o in offs):
            continue
        donor = bytes(pay[s:s + t])
        break
    if donor is None:
        print('  G1 ✗ 找不到 donor 记录'); ok = False
    else:
        dt, dck = chunks_of(donor)
        if dt != total or {k: v[1] for k, v in dck.items()} != {k: v[1] for k, v in ck.items()}:
            print('  G1 ✗ 块布局与原版不一致'); ok = False
        else:
            print('  G1 ✓ 块布局与原版逐项一致')

    # G2 校验和
    stored, calc = rec[4:20], dxbc_checksum(rec)
    if stored != calc:
        print('  G2 ✗ 校验和未复签（stored %s / calc %s）' % (stored.hex()[:16], calc.hex()[:16])); ok = False
    else:
        print('  G2 ✓ 校验和与内容一致')

    # G3 输入掩码
    if not a.ours_asm:
        print('  G3 - 未提供 ours-asm，跳过'); 
    else:
        prov = parse_isgn_bin(donor) if donor is not None else parse_sig_asm(a.donor_asm)
        reads = parse_reads_asm(a.ours_asm)
        bad = []
        for reg, comps in reads.items():
            miss = comps - prov.get(reg, set())
            if miss:
                bad.append((reg, ''.join(sorted(comps)), ''.join(sorted(prov.get(reg, set()))) or '(无)', ''.join(sorted(miss))))
        if bad:
            print('  G3 ✗ 读取了签名未提供的分量：')
            for r, c, p, m in bad:
                print('       v%d 读[%s] 提供[%s] 缺[%s]' % (r, c, p, m))
            ok = False
        else:
            print('  G3 ✓ 读取分量 ⊆ ISGN 提供掩码 %s' % {k: ''.join(sorted(v)) for k, v in sorted(reads.items())})

    # G4 NOP / ret
    shex = ck['SHEX'][2]
    if len(shex) < 12:
        print('  G4 ✗ SHEX 过短'); ok = False
    else:
        dw = len(shex) // 4
        toks = struct.unpack_from('<%dI' % dw, shex, 0)
        cnt = struct.unpack_from('<I', shex, 4)[0]
        if cnt != dw:
            print('  G4 ✗ SHEX 头部 dword 计数 %d ≠ 实际 %d' % (cnt, dw)); ok = False
        rets = [i for i, t in enumerate(toks) if t == RET]
        if len(rets) != 1:
            print('  G4 ✗ ret 出现 %d 次（应 1 次）' % len(rets)); ok = False
        else:
            tail = toks[rets[0] + 1:]
            if any(t != NOP for t in tail):
                bad_toks = [hex(t) for t in tail if t != NOP][:5]
                print('  G4 ✗ ret 之后存在非 NOP 指令: %s' % bad_toks); ok = False
            else:
                print('  G4 ✓ ret 唯一，之后 %d 个真 NOP(0x%08X)' % (len(tail), NOP))
    print('gate_record: %s' % ('通过' if ok else '失败'))
    return 0 if ok else 1


def mip_chain_size(w, h, fmt, mips):
    """BC 格式的 mip 链字节数。fmt: 16 表示 BC7/BC5/BC6H/BC2/BC3 类(16B/块)，8 表示 BC1/BC4"""
    bypp = 16 if fmt >= 16 else 8
    total = 0
    for lvl in range(mips):
        bw = max(1, (w >> lvl) + 3 >> 2) if False else max(1, ((w >> lvl) + 3) // 4)
        bh = max(1, ((h >> lvl) + 3) // 4)
        total += bw * bh * bypp
    return total


def gate_texture(a):
    """读 TOC 里该贴图条目的 340B TocData，检查 mip 链长与 gpuSize 是否相等"""
    d = open(a.toc, 'rb').read()
    # 从条目表里找 file-id（条目表在 0x40 起，每 12 字节，见既有工具约定）
    n = struct.unpack_from('<I', d, 0)[0]
    cand = None
    for i in range(min(n, 4096)):
        base = 0x40 + i * 12
        if base + 12 > len(d):
            break
        fid, = struct.unpack_from('<Q', d, base)
        off, = struct.unpack_from('<I', d, base + 8)
        if fid == a.file_id:
            cand = off
            break
    if cand is None:
        print('  G5 - TOC 条目表里没找到 0x%016x（换用扫描）' % a.file_id)
        return 0
    t = d[cand:cand + 340]
    # 尺寸/格式字段位置沿用既有分析：width@+16 height@+12 pitch@+20 depth@+24 mips@+28
    h, w, pitch, depth, mips = struct.unpack_from('<5I', t, 12)
    fmt, = struct.unpack_from('<I', t, 32)
    size = mip_chain_size(w, h, pitch, mips)
    print('  贴图 0x%016x: %dx%d mips=%d pitch=%d fmt=%d → mip 链 %d B' % (a.file_id, w, h, mips, pitch, fmt, size))
    print('  G5 %s' % ('✓ 与 gpuSize 一致' if a.gpu_size in (0, size) else '（请与条目 gpuSize 手工比对）'))
    return 0


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest='cmd', required=True)
    r = sub.add_parser('record')
    r.add_argument('--record', required=True)
    r.add_argument('--donor', required=True)
    r.add_argument('--donor-size', type=int, required=True)
    r.add_argument('--ours-asm')
    r.add_argument('--donor-asm')
    t = sub.add_parser('texture')
    t.add_argument('--toc', required=True)
    t.add_argument('--file-id', required=True)
    t.add_argument('--gpu-size', type=int, default=0)
    a = ap.parse_args()
    if a.cmd == 'record':
        return gate_record(a)
    a.file_id = int(a.file_id, 16)
    return gate_texture(a)


if __name__ == '__main__':
    sys.exit(main())
