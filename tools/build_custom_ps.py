#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# ⛔ 已过时（2026-11-16 标记）：功能已被 tools/build_signed_ps.py 完整覆盖（后者修掉了 NOP token 与
#    记录头 u32@20 两个致命细节，并内置复签 + fxc 验证）。现行构建器是 build_signed_ps.py；
#    本文件保留作历史记录，不要再产出要部署的记录。
"""把一段 HLSL 编成 ps_5_0，做成一条**定长** DXBC 记录，再等长替换进材质载荷。

这是"自制根材质着色器"的生产线第一环。关键前提（都已实测）：
  · 引擎**不校验** DXBC 的 128 位校验和 —— 所以 fxc 的输出可以直接用
    （当年以为必须逐字节复刻原着色器，那个结论是错的）；
  · 引擎按 **ISGN/OSGN 签名** 与顶点/输出链接 —— 只要签名与目标角色一致即可；
  · 记录可以**等长**替换（已经验证过引擎会执行外来 SHEX），差额用 `PRIV` 块吸收，
    载荷总长、长度字段、所有偏移都不用动。

用法：
    python tools/build_custom_ps.py --hlsl <a.hlsl> --entry main \
        --payload <材质侧车> --donor-size 62652 \
        --out <输出侧车> [--fxc <fxc.exe>] [--check-sig <参考载荷>]
"""
import argparse
import os
import struct
import subprocess
import sys

DEFAULT_FXC = r'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\fxc.exe'
CH = set(b'RDEF ISGN OSGN SHEX STAT PCSG ILDN SPDB IFCE RD11 PRIV'.split())


def compile_hlsl(fxc, hlsl_path, entry, profile='ps_5_0'):
    out = hlsl_path + '.dxbc'
    cmd = [fxc, '/nologo', '/T', profile, '/E', entry, '/Fo', out, hlsl_path]
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode != 0 or not os.path.isfile(out):
        print(r.stdout[-2000:])
        print(r.stderr[-2000:])
        raise SystemExit('fxc 编译失败')
    if r.stdout.strip():
        print('  fxc: %s' % r.stdout.strip()[:300])
    return out


def chunks_of(d):
    if d[:4] != b'DXBC':
        raise SystemExit('不是 DXBC')
    total, nch = struct.unpack_from('<2I', d, 24)
    offs = sorted(struct.unpack_from('<%dI' % nch, d, 32))
    ck = {}
    for k, o in enumerate(offs):
        end = offs[k + 1] if k + 1 < len(offs) else total
        ck[d[o:o + 4].decode()] = d[o + 8:end]
    return ck


def record_chunks(d):
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
            ck[fcc.decode()] = d[s + o + 8:s + end]
        if ok:
            out.append({'off': s, 'size': total, 'chunks': ck})


COMP = {0: 'unknown', 1: 'float32', 2: 'int32', 3: 'uint32'}


def dump_sig(buf, label):
    if len(buf) < 8:
        print('  %s: (空)' % label)
        return []
    cnt, unk = struct.unpack_from('<2I', buf, 0)
    rows = []
    for i in range(cnt):
        o = 8 + i * 24
        if o + 24 > len(buf):
            break
        noff, sidx, svt, ct, reg = struct.unpack_from('<5I', buf, o)
        mask = struct.unpack_from('<4B', buf, o + 20)[0]
        z = buf.find(b'\x00', noff) if 0 < noff < len(buf) else -1
        nm = buf[noff:z].decode('ascii', 'replace') if z > 0 else '?'
        rows.append((nm, sidx, svt, COMP.get(ct, ct), reg, mask))
    print('  %s: %d 元素' % (label, len(rows)))
    for r in rows:
        print('     %-14s idx=%d sv=%d %-7s reg=%d mask=0x%02x' % r)
    return rows


def build_record(ck, total_target):
    """用给定块造一条恰好 total_target 字节的记录；差额全部给 PRIV。"""
    order = ['RDEF', 'ISGN', 'OSGN', 'SHEX', 'STAT']
    order = [k for k in order if k in ck]
    order.append('PRIV')
    nch = len(order)
    start = (32 + nch * 4 + 3) & ~3
    fixed = sum(8 + len(ck[k]) for k in order if k != 'PRIV')
    priv = total_target - start - fixed - 8
    if priv < 0:
        raise SystemExit('目标 %d B 装不下（差 %d）' % (total_target, -priv))
    out = bytearray(total_target)
    out[0:4] = b'DXBC'
    out[4:20] = b'\x00' * 16          # 校验和留 0：引擎不校验，fxc 才会
    struct.pack_into('<II', out, 20, 1, total_target)   # u32@20 必须为 1
    struct.pack_into('<I', out, 28, nch)
    cur = start
    for i, k in enumerate(order):
        data = ck.get(k, b'') if k != 'PRIV' else b'\x00' * priv
        struct.pack_into('<I', out, 32 + 4 * i, cur)
        out[cur:cur + 4] = k.encode()
        struct.pack_into('<I', out, cur + 4, len(data))
        out[cur + 8:cur + 8 + len(data)] = data
        cur += 8 + len(data)
    assert cur == total_target, (cur, total_target)
    return bytes(out), priv


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--hlsl', required=True)
    ap.add_argument('--entry', default='main')
    ap.add_argument('--payload', required=True)
    ap.add_argument('--donor-size', type=int, required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--fxc', default=DEFAULT_FXC)
    ap.add_argument('--check-sig', default=None, help='参考载荷：比对 ISGN/OSGN 是否与其一致')
    args = ap.parse_args()

    print('编译 %s（%s）…' % (args.hlsl, args.entry))
    blob = compile_hlsl(args.fxc, args.hlsl, args.entry)
    d = open(blob, 'rb').read()
    ck = chunks_of(d)
    print('  块: %s' % '+'.join('%s(%d)' % (k, len(v)) for k, v in ck.items()))

    if args.check_sig:
        ref = open(args.check_sig, 'rb').read()
        refrec = next((r for r in record_chunks(ref) if r['size'] == args.donor_size), None)
        if refrec:
            for name in ('ISGN', 'OSGN'):
                a, b = ck.get(name, b''), refrec['chunks'].get(name, b'')
                same = a == b
                print('  %s 与参考一致: %s  (自造 %d B / 参考 %d B)' % (name, '✅' if same else '❌', len(a), len(b)))
                if not same:
                    ra = dump_sig(a, '自造 ' + name)
                    rb = dump_sig(b, '参考 ' + name)
                    for i, (x, y) in enumerate(zip(ra, rb)):
                        if x != y:
                            print('     >>> 第 %d 个元素不同: 自造 %s / 参考 %s' % (i, x, y))
                    print('     >>> 签名不一致，引擎可能链接失败 —— 先别部署')

    rec, priv = build_record(ck, args.donor_size)
    print('  记录 %d B（PRIV 补 %d B）' % (len(rec), priv))

    pay = bytearray(open(args.payload, 'rb').read())
    tgts = [r for r in record_chunks(pay) if r['size'] == args.donor_size]
    print('  载荷里 %d B 的记录: %d 份' % (args.donor_size, len(tgts)))
    for r in tgts:
        pay[r['off']:r['off'] + args.donor_size] = rec
    open(args.out, 'wb').write(bytes(pay))
    again = [r for r in record_chunks(pay) if r['size'] == args.donor_size]
    same = sum(1 for r in again if bytes(pay[r['off']:r['off'] + args.donor_size]) == rec)
    print('  写入 %s（%d B，长度未变: %s）；自检 %d/%d 份等于新记录' % (
        args.out, len(pay), len(pay) == os.path.getsize(args.payload), same, len(again)))


if __name__ == '__main__':
    sys.exit(main())
