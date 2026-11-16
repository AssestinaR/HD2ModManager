#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""HD2 根材质「编译载荷」拆包 / 等长替换工具（S2 阶段）。

载荷结构（实测，见 docs/agent/hd2-custom-material-pipeline-plan-20261114.md）：
    0x00  头部 64 字节
    0x40  记录区 / 空隙交替
          每条记录 = 一个 DXBC（RDEF+ISGN+OSGN+SHEX+STAT）
          记录之间是 92 或 3780 字节的"空隙"（内含版本表/常量等）

本工具只做**等长**操作，绝不改变任何长度字段，因此可以安全地原地替换：
    list     列出所有 DXBC 记录（索引 / 偏移 / 大小 / 块集合）
    extract  导出某条记录
    replace  用同长度文件替换某条记录（长度必须相等）
    verify   把整份载荷按"记录 + 空隙"重新拼一遍，要求与原文件字节一致

    python payload_repackage.py list <payload.bin>
    python payload_repackage.py extract <payload.bin> <index> <out.dxbc>
    python payload_repackage.py replace <payload.bin> <index> <new.dxbc>
    python payload_repackage.py verify <payload.bin>
"""
import struct, sys, re, os, hashlib

# ⚠️ PRIV 不是官方 DXBC 块名，但第三方 mod 自带的载荷会用它在五个块里顶替 STAT
#    （实测：全息背包根材质 0xdec4b7db2e8095f3，184/202 条记录是 RDEF+ISGN+OSGN+SHEX+PRIV）。
#    不认它会导致这些记录被整批跳过 —— 看上去"这份载荷只有 18 个着色器"，其实是 202 个。
CHUNKS = set(b'RDEF ISGN OSGN SHEX STAT PCSG ILDN SPDB IFCE RD11 PRIV'.split())
HDR = 0x40


def is_blob(d, s):
    if s + 32 > len(d) or d[s:s + 4] != b'DXBC':
        return None
    total, nch = struct.unpack_from('<2I', d, s + 24)
    if not (32 < total < 1 << 22) or not (1 <= nch <= 16) or s + total > len(d):
        return None
    offs = sorted(struct.unpack_from('<%dI' % nch, d, s + 32))
    if any(o < 32 or o >= total for o in offs):
        return None
    end = None
    for o in offs:
        fcc = bytes(d[s + o:s + o + 4])
        if fcc not in CHUNKS:
            return None
        size, = struct.unpack_from('<I', d, s + o + 4)
        end = o + 8 + size
    if end is None or end > total + 16:
        return None
    return total, [bytes(d[s + o:s + o + 4]).decode('latin1') for o in offs]


def segments(d):
    """返回 [(kind, start, end, info)]，kind ∈ {prologue, record, gap}"""
    recs = []
    for m in re.finditer(b'DXBC', d):
        s = m.start()
        info = is_blob(d, s)
        if not info:
            continue
        total, chunks = info
        if recs and s < recs[-1][1]:
            continue
        recs.append((s, s + total, chunks))
    segs = [('prologue', 0, recs[0][0], [])] if recs and recs[0][0] > 0 else []
    for i, (a, b, chunks) in enumerate(recs):
        segs.append(('record', a, b, chunks))
        if i + 1 < len(recs) and recs[i + 1][0] > b:
            segs.append(('gap', b, recs[i + 1][0], []))
    if recs and recs[-1][1] < len(d):
        segs.append(('gap', recs[-1][1], len(d), []))
    return segs


def cmd_list(d):
    for i, (kind, a, b, chunks) in enumerate(segments(d)):
        if kind != 'record':
            continue
        print("record %3d  offset=%-8d size=%-6d chunks=%s" % (i, a, b - a, '+'.join(chunks)))


def records(d):
    return [s for s in segments(d) if s[0] == 'record']


def cmd_extract(d, idx, out):
    kind, a, b, _ = records(d)[idx]
    open(out, 'wb').write(d[a:b])
    print("exported record %d (%d bytes) -> %s" % (idx, b - a, out))


def cmd_replace(path, d, idx, src):
    kind, a, b, _ = records(d)[idx]
    new = open(src, 'rb').read()
    if len(new) != b - a:
        raise SystemExit("长度不匹配：记录 %d 是 %d 字节，新文件 %d 字节（本工具只做等长替换）"
                         % (idx, b - a, len(new)))
    out = bytearray(d)
    out[a:b] = new
    open(path, 'wb').write(out)
    print("replaced record %d: %d bytes" % (idx, len(new)))


def cmd_verify(path, d):
    segs = segments(d)
    rebuilt = b''.join(d[a:b] for _, a, b, _ in segs)
    same = rebuilt == d
    print("segments=%d records=%d" % (len(segs), sum(1 for s in segs if s[0] == 'record')))
    print("rebuilt sha256 = %s" % hashlib.sha256(rebuilt).hexdigest()[:32])
    print("original sha256 = %s" % hashlib.sha256(d).hexdigest()[:32])
    print("VERIFY:", "OK (byte-identical)" if same else "FAIL")


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    cmd, path = sys.argv[1], sys.argv[2]
    d = open(path, 'rb').read()
    if cmd == 'list':
        cmd_list(d)
    elif cmd == 'extract':
        cmd_extract(d, int(sys.argv[3]), sys.argv[4])
    elif cmd == 'replace':
        cmd_replace(path, d, int(sys.argv[3]), sys.argv[4])
    elif cmd == 'verify':
        cmd_verify(path, d)
    else:
        print(__doc__)
        return 2
    return 0


if __name__ == '__main__':
    raise SystemExit(main())

