#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Donor pass 目录：扫一批材质载荷，列出每份的「材质主 pass」签名、长度与 RDEF 绑定。

用途：等长替换已经验证可行（见 docs/agent/hd2-payload-carrier-and-dynamic-emissive-20261114.md
七之四），所以现在要回答「手上有哪些 pass 可以换」。换的前提是 **ISGN/OSGN 与目标槽位相同**，
而材质主 pass 的签名是 ISGN 248 B / OSGN 44 B（本体 advanced、全息背包、作者 mat 组三者一致）。

用法：
    python tools/pass_catalog.py <文件或目录> [...] [--json out.json]
"""
import argparse
import hashlib
import json
import os
import struct
import sys

CH = set(b'RDEF ISGN OSGN SHEX STAT PCSG ILDN SPDB IFCE RD11 PRIV'.split())
MAT_PASS_ISGN_LEN = 248
MAT_PASS_OSGN_LEN = 44


def records(d, want_sig=None):
    out, i = [], 0
    n = len(d)
    while True:
        s = d.find(b'DXBC', i)
        if s < 0:
            break
        i = s + 4
        if s + 32 > n:
            continue
        total, nch = struct.unpack_from('<2I', d, s + 24)
        if not (32 < total < (1 << 23)) or not (1 <= nch <= 16) or s + total > n:
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
    return out


def rdef_names(chunks):
    """RDEF 里绑定的纹理/sampler 名 + cbuffer 名。"""
    r = chunks.get('RDEF')
    if not r or len(r) < 28:
        return [], []
    try:
        cb_n, cb_off = struct.unpack_from('<2I', r, 0)
        bd_n, bd_off = struct.unpack_from('<2I', r, 8)
    except struct.error:
        return [], []
    if cb_n > 64 or bd_n > 64:
        return [], []

    def cstr(o):
        z = r.find(b'\x00', o)
        if z < 0:
            return None
        try:
            return r[o:z].decode('ascii')
        except UnicodeDecodeError:
            return None

    binds, cbs = [], []
    for k in range(bd_n):
        e = bd_off + k * 32
        if e + 32 > len(r):
            break
        o, = struct.unpack_from('<I', r, e)
        s = cstr(o) if o and o < len(r) else None
        if s:
            binds.append(s)
    for k in range(cb_n):
        e = cb_off + k * 24
        if e + 24 > len(r):
            break
        o, = struct.unpack_from('<I', r, e)
        s = cstr(o) if o and o < len(r) else None
        if s:
            cbs.append(s)
    return binds, cbs


def short(b):
    return hashlib.sha256(b).hexdigest()[:12] if b else '-'


def scan(path):
    d = open(path, 'rb').read()
    if len(d) < 64 or d[:4] != b'DXBC' and struct.unpack_from('<I', d, 0)[0] != 0xb3:
        pass
    mat_low = struct.unpack_from('<I', d, 4)[0] if len(d) >= 8 else 0
    recs = records(d)
    groups = {}
    for r in recs:
        key = (short(r['chunks'].get('ISGN')), len(r['chunks'].get('ISGN') or b''),
               short(r['chunks'].get('OSGN')), len(r['chunks'].get('OSGN') or b''))
        groups.setdefault(key, []).append(r)
    info = {'path': path, 'bytes': len(d), 'mat_low': '%08x' % mat_low,
            'records': len(recs), 'groups': len(groups), 'mat_pass': None}
    mat = None
    # 248/44 这个签名在「小工具着色器」上也会出现，所以不能只靠签名认。
    # 真正的主 pass = **SHEX 最大的那一组**（实测：本体 advanced 49,980 / 全息 39,672）。
    best = None
    for key, rs in groups.items():
        shex = max(len(r['chunks'].get('SHEX') or b'') for r in rs)
        sigok = key[1] == MAT_PASS_ISGN_LEN and key[3] == MAT_PASS_OSGN_LEN
        score = (shex, sigok)
        if best is None or score > best[0]:
            best = (score, key, rs)
    if best:
        key, rs = best[1], best[2]
        binds, cbs = rdef_names(rs[0]['chunks'])
        info['mat_pass'] = {
            'isgn': key[0], 'isgn_len': key[1], 'osgn': key[2], 'osgn_len': key[3],
            'role_match': key[1] == MAT_PASS_ISGN_LEN and key[3] == MAT_PASS_OSGN_LEN,
            'count': len(rs), 'sizes': sorted({r['size'] for r in rs}),
            'shex': max(len(r['chunks'].get('SHEX') or b'') for r in rs),
            'rdef': max(len(r['chunks'].get('RDEF') or b'') for r in rs),
            'binds': binds, 'cbuffers': cbs,
        }
    return info


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('paths', nargs='+')
    ap.add_argument('--json', default=None)
    args = ap.parse_args()

    files = []
    for p in args.paths:
        if os.path.isdir(p):
            files += [os.path.join(p, f) for f in sorted(os.listdir(p))
                      if f.lower().endswith(('.bin', '.gpu', '.gpu_resources'))]
        elif os.path.isfile(p):
            files.append(p)

    out = []
    print('%-22s %-9s %-6s %-8s %-7s %s' % ('载荷', '字节', '记录', '材质id', '主pass', '主 pass 尺寸 / SHEX'))
    for f in files:
        try:
            i = scan(f)
        except Exception as e:
            print('%-22s  读取失败: %s' % (os.path.basename(f), e))
            continue
        out.append(i)
        mp = i['mat_pass']
        tail = ('x%d %s SHEX=%d' % (mp['count'], mp['sizes'], mp['shex'])) if mp else '—（无 248/44 组）'
        print('%-22s %-9d %-6d %-8s %-7s %s' % (
            os.path.basename(f)[:22], i['bytes'], i['records'], i['mat_low'],
            'YES' if mp else 'no', tail))

    donors = [i for i in out if i['mat_pass']]
    print('\n可作材质主 pass donor 的载荷: %d / %d' % (len(donors), len(out)))
    for i in donors:
        mp = i['mat_pass']
        caps = []
        for b in mp['binds']:
            lb = b.lower()
            for key, name in (('opacityclip', '镂空'), ('albedoemissive', '自发光'),
                              ('emissive', '自发光'), ('nar', '法线/AO/粗糙'),
                              ('illuminate', '金属'), ('metallic', '金属'),
                              ('lenscutout', '镜头遮罩'), ('parallax', '视差'),
                              ('detail', '细节法线'), ('wound', '伤口')):
                if key in lb and name not in caps:
                    caps.append(name)
        print('  %-22s id=%s  x%d %s  caps=%s' % (
            os.path.basename(i['path'])[:22], i['mat_low'], mp['count'], mp['sizes'], ','.join(caps) or '-'))

    if args.json:
        with open(args.json, 'w', encoding='utf-8') as fh:
            json.dump(out, fh, ensure_ascii=False, indent=1)
        print('\nJSON -> %s' % args.json)


if __name__ == '__main__':
    sys.exit(main())
