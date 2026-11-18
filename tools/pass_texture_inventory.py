#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""材质 pass 的贴图绑定清单：每条 DXBC pass 能用几张材质贴图、分别叫什么。

为什么需要：定制着色器能用的"通道预算"不是由我们写的 HLSL 决定的，而是由**这条 pass 绑定了几张
材质贴图**决定的。一条只绑 1 张（4 通道）、另一条绑 3 张（12 通道），能做的事完全不同。
所以选 pass 之前必须先把这份清单摆出来。

用法：
    python tools/pass_texture_inventory.py <patch.toc> --material 0x<id> [--json out.json]
    python tools/pass_texture_inventory.py <payload.gpu>
"""
import argparse
import json
import os
import struct
import sys

MAT_TYPE = 0xEAC0B497876ADEDF


def slice_payload_from_patch(path, material_id):
    d = open(path, 'rb').read()
    g = open(path + '.gpu_resources', 'rb').read()
    if struct.unpack_from('<I', d, 0)[0] != 0xF0000011:
        raise SystemExit('不是 patch 文件: %s' % path)
    tn, en = struct.unpack_from('<II', d, 4)
    eo = 72 + tn * 32
    for k in range(en):
        o = eo + k * 80
        fid, = struct.unpack_from('<Q', d, o)
        typ, = struct.unpack_from('<Q', d, o + 8)
        if typ == MAT_TYPE and fid == material_id:
            go, = struct.unpack_from('<Q', d, o + 32)
            gs, = struct.unpack_from('<I', d, o + 64)
            return g[go:go + gs]
    raise SystemExit('plot 里找不到材质 0x%016x' % material_id)


CH = set(b'RDEF ISGN OSGN SHEX STAT PCSG ILDN SPDB IFCE RD11 PRIV'.split())


def dxbc_records(d):
    """扫出所有合法 DXBC 记录。

    实测布局（与 tools/payload_map.py 同一判据，别再按 fxc 习惯猜）：
        +24  u32 totalSize, u32 chunkCount
        +32  chunkCount × u32 chunkOffset（相对记录起点）
        每个 chunk: 4 字节 fourCC + 4 字节未知 + 载荷，到下一个 offset 为止
    """
    out, i, n = [], 0, len(d)
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
        chunks, ok = {}, True
        for k, o in enumerate(offs):
            fcc = bytes(d[s + o:s + o + 4])
            if fcc not in CH:
                ok = False
                break
            end = offs[k + 1] if k + 1 < len(offs) else total
            chunks[fcc.decode()] = d[s + o + 8:s + end]
        if ok:
            out.append((s, total, chunks))
    return out


def rdef_bindings(rdef):
    """解析 RDEF 的 SRV 绑定 → [(bindPoint, name)]。

    HD2 的 RDEF 头与 fxc 标准不一致（payload_map.py 的注释已记录），
    所以这里不信任固定偏移：先在 RDEF 里找绑定表候选，再用"名字能解析成
    __tex_ 开头的字符串"来确认布局。
    """
    out = []
    n = len(rdef)
    if n < 64:
        return out
    for base in range(0, 64, 4):
        if base + 16 > n:
            break
        try:
            bind_count, bind_off = struct.unpack_from('<2I', rdef, base + 8)
        except struct.error:
            break
        if not (1 <= bind_count <= 4096) or not (0 < bind_off < n):
            continue
        if bind_off + bind_count * 32 > n:
            continue
        cand = []
        ok = True
        for k in range(bind_count):
            q = bind_off + k * 32
            name_off, typ = struct.unpack_from('<2I', rdef, q)
            if name_off <= 0 or name_off >= n:
                ok = False
                break
            end = rdef.find(b'\x00', name_off)
            if end < 0 or end - name_off > 128:
                ok = False
                break
            try:
                name = rdef[name_off:end].decode('ascii')
            except UnicodeDecodeError:
                ok = False
                break
            if not name or any(ord(c) < 0x20 or ord(c) > 0x7e for c in name):
                ok = False
                break
            bindpoint_val, = struct.unpack_from('<I', rdef, q + 20)
            cand.append((typ, name, bindpoint_val))
        if ok and cand and any(nm.startswith('__') for _, nm, _ in cand):
            # 取第一个能完整解析的布局
            out = [(bp, nm) for (typ, nm, bp) in cand if typ == 2]
            break
    return out


def osgn_rt_count(osgn):
    """数 OSGN 里 SV_ 语义的输出个数，粗判多渲染目标。"""
    try:
        text = osgn.decode('ascii', 'replace')
        return text.count('SV_Target') + text.count('SV_TARGET')
    except Exception:
        return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('path')
    ap.add_argument('--material', default=None)
    ap.add_argument('--json', default=None)
    ap.add_argument('--tex-only', action='store_true', help='只列绑定里有 __tex_ 的 pass')
    args = ap.parse_args()

    if args.material:
        payload = slice_payload_from_patch(args.path, int(args.material, 16))
        src = '%s#0x%016x' % (os.path.basename(args.path), int(args.material, 16))
    else:
        payload = open(args.path, 'rb').read()
        src = os.path.basename(args.path)

    print('载荷来源: %s   长度 %d B' % (src, len(payload)))
    recs = dxbc_records(payload)
    print('DXBC 记录 %d 条\n' % len(recs))

    rows = []
    for idx, (off, total, chunks) in enumerate(recs):
        if 'RDEF' not in chunks or 'SHEX' not in chunks:
            continue
        bindings = rdef_bindings(chunks['RDEF'])
        tex = [(bp, nm) for bp, nm in bindings if nm.startswith('__tex_')]
        rt = osgn_rt_count(chunks.get('OSGN', b''))
        rows.append({
            'index': idx,
            'offset': off,
            'recordSize': total,
            'isgnSize': len(chunks.get('ISGN', b'')),
            'osgnSize': len(chunks.get('OSGN', b'')),
            'shexSize': len(chunks['SHEX']),
            'rtCount': rt,
            'textures': [{'slot': bp, 'name': nm} for bp, nm in tex],
            'allBindings': len(bindings),
        })

    if args.tex_only:
        rows = [r for r in rows if r['textures']]

    rows.sort(key=lambda r: (-len(r['textures']), r['index']))
    print('%-5s %-8s %-7s %-7s %-8s %-4s %s' % ('#', 'ISGN', 'OSGN', 'SHEX', '记录', 'RT', '材质贴图'))
    for r in rows:
        texdesc = ', '.join('t%d=%s' % (t['slot'], t['name']) for t in r['textures']) or '—'
        print('%-5d %-8d %-7d %-7d %-8d %-4d %s'
              % (r['index'], r['isgnSize'], r['osgnSize'], r['shexSize'], r['recordSize'], r['rtCount'], texdesc))

    multi = [r for r in rows if len(r['textures']) >= 2]
    print('\n绑定 ≥2 张材质贴图的 pass：%d 条' % len(multi))
    for r in multi:
        print('   #%d  ISGN %d / OSGN %d / RT %d   %s'
              % (r['index'], r['isgnSize'], r['osgnSize'], r['rtCount'],
                 ', '.join('t%d=%s' % (t['slot'], t['name']) for t in r['textures'])))

    if args.json:
        os.makedirs(os.path.dirname(os.path.abspath(args.json)) or '.', exist_ok=True)
        with open(args.json, 'w', encoding='utf-8') as f:
            json.dump({'source': src, 'rows': rows}, f, ensure_ascii=False, indent=1)
        print('\nJSON -> %s' % args.json)
    return 0


if __name__ == '__main__':
    sys.exit(main())
