#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""载荷结构地图：列出 HD2 材质载荷里所有 DXBC 记录的分组（stage / ISGN / OSGN / SHEX / 记录尺寸）。

为什么需要它：一份材质载荷里通常有 **几十到八十一组** 记录，只有其中一组是游戏里真正画的 pass。
用「SHEX 最大」或「ISGN 248 / OSGN 44」去猜都会猜错（作者材质上就错了两轮）。
本工具把每组都摆出来，并按 OSGN 里的 SV_Target 数量标出「多渲染目标 = 延迟光照候选」，
这样选组就不再靠猜。

判定只用签名，不依赖 RDEF（HD2 的 RDEF 头字段与 fxc 标准布局不一致，靠不住）。

用法：
    # 直接给载荷文件（.gpu / 解出来的 .gpu_resources 片段）
    python tools/payload_map.py <payload.gpu>

    # 或给 patch + 材质 id，自动从 .gpu_resources 里切出该材质的载荷
    python tools/payload_map.py <xxx.patch_0> --material 0xe577db2cf97fcddc

    # 把某一组记录原样导出（做探针时当 donor）
    python tools/payload_map.py <payload.gpu> --group 3 --out-group group3.bin
"""
import argparse
import collections
import json
import struct
import sys

CH = set(b'RDEF ISGN OSGN SHEX STAT PCSG ILDN SPDB IFCE RD11 PRIV'.split())
MAGIC_PATCH = 0xF0000011
MAGIC_PAYLOAD = 0xB3

SV_NAMES = {
    0: 'undefined', 1: 'SV_Position', 2: 'SV_ClipDistance', 3: 'SV_CullDistance',
    4: 'SV_RenderTargetArrayIndex', 5: 'SV_ViewportArrayIndex', 6: 'SV_VertexID',
    7: 'SV_PrimitiveID', 8: 'SV_InstanceID', 9: 'SV_IsFrontFace', 10: 'SV_SampleIndex',
}


def patch_entries(d):
    if len(d) < 12 or struct.unpack_from('<I', d, 0)[0] != MAGIC_PATCH:
        return None
    tn, en = struct.unpack_from('<II', d, 4)
    eoff = 72 + tn * 32
    out = []
    for k in range(en):
        o = eoff + k * 80
        if o + 80 > len(d):
            break
        f = struct.unpack_from('<QQQQQQQIIIIII', d, o)
        out.append({'fileId': f[0], 'typeId': f[1], 'tocOff': f[2],
                    'gpuOff': f[4], 'tocSize': f[7], 'gpuSize': f[9]})
    return out


def records(d):
    """扫出所有合法 DXBC 记录（与 tools/pass_catalog.py 同一判据）。"""
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


def sig_semantics(chunk):
    """解析 ISGN/OSGN。实测布局：

        u32 元素数; u32 8; 元素 × 24 B; 然后是语义名字符串表
        元素 = (name_off, sem_index, system_value_type, component_type, register, mask)

    NAME_OFF 是相对本 chunk 起点的字符串偏移。
    """
    if not chunk or len(chunk) < 8:
        return []
    cnt, = struct.unpack_from('<I', chunk, 0)
    if cnt > 64:
        return []
    out = []
    for k in range(cnt):
        e = 8 + k * 24
        if e + 24 > len(chunk):
            break
        name_off, sem_idx, sv, comp, reg, mask = struct.unpack_from('<6I', chunk, e)
        name = SV_NAMES.get(sv, '') if sv else ''
        if not name and 0 < name_off < len(chunk):
            z = chunk.find(b'\x00', name_off)
            if z > name_off:
                try:
                    name = chunk[name_off:z].decode('ascii')
                except UnicodeDecodeError:
                    name = ''
        out.append({'name': name, 'index': sem_idx, 'system': sv,
                    'register': reg, 'mask': mask})
    return out


def is_target(s):
    return s['name'].upper().replace('_', '') == 'SVTARGET'


def stage_of(r):
    osgn, isgn = r['chunks'].get('OSGN'), r['chunks'].get('ISGN')
    if not osgn:
        return '?'
    osem = sig_semantics(osgn)
    if any(is_target(s) for s in osem):
        return 'ps'
    if any(s['system'] == 1 for s in osem) or any(
            s['name'].upper().replace('_', '') == 'SVPOSITION' for s in osem):
        return 'vs'
    return '?'


def targets(r):
    return sum(1 for s in sig_semantics(r['chunks'].get('OSGN')) if is_target(s))


def load_payload(args):
    d = open(args.path, 'rb').read()
    ents = patch_entries(d)
    if ents is None:
        return d, None
    if not args.material:
        print('这是 patch TOC，不是载荷。加 --material 0x<材质id> 指定要从里面切哪一条材质。')
        mats = [e for e in ents if e['gpuSize']]
        for e in mats[:20]:
            print('   0x%016x  gpuOff=%d gpuSize=%d' % (e['fileId'], e['gpuOff'], e['gpuSize']))
        raise SystemExit(2)
    mid = int(args.material, 16)
    e = next((x for x in ents if x['fileId'] == mid), None)
    if not e:
        raise SystemExit('patch 里没有材质 0x%016x' % mid)
    if e['gpuSize'] == 0:
        raise SystemExit('材质 0x%016x 的 gpuSize=0（delta 条目，本身不带载荷）' % mid)
    gd = open(args.path + '.gpu_resources', 'rb').read()
    return gd[e['gpuOff']:e['gpuOff'] + e['gpuSize']], e


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('path')
    ap.add_argument('--material', default=None, help='0x<材质 id>：从 patch 里切出该材质的载荷')
    ap.add_argument('--group', type=int, default=None, help='按序号导出一组记录')
    ap.add_argument('--out-group', default=None)
    ap.add_argument('--json', default=None)
    args = ap.parse_args()

    pay, ent = load_payload(args)
    print('载荷 %d B' % len(pay))
    if len(pay) >= 20:
        h = struct.unpack_from('<5I', pay, 0)
        print('头: +0=0x%02x  +4=0x%08x  +8=%d  +12=%d  +16=%d' % tuple(h))
        if ent:
            print('   +4 == 材质 id 高 32 位 ? %s' % ('YES' if h[1] == (ent['fileId'] >> 32) else 'NO'))
        print('   +8 == 载荷长度-8 ? %s' % ('YES' if h[2] == len(pay) - 8 else 'NO'))

    recs = records(pay)
    if not recs:
        raise SystemExit('没有找到合法 DXBC 记录')
    covered = sum(r['size'] for r in recs)
    print('DXBC 记录 %d 条，覆盖 %d / %d B（%d 条之间有 %d B 空隙）' % (
        len(recs), covered, len(pay), len(recs) - 1, len(pay) - covered - recs[0]['off']))

    groups = collections.OrderedDict()
    for idx, r in enumerate(recs):
        key = (stage_of(r), len(r['chunks'].get('ISGN') or b''),
               len(r['chunks'].get('OSGN') or b''), len(r['chunks'].get('SHEX') or b''),
               targets(r), r['size'])
        groups.setdefault(key, []).append(idx)

    rows = []
    print()
    print('  #  %-3s %-5s %-5s %-7s %-3s %-6s %-4s %s' % (
        'stage', 'ISGN', 'OSGN', 'SHEX', 'RT', '记录', '条数', '首条偏移'))
    for gi, (key, idxs) in enumerate(groups.items()):
        st, il, ol, sl, rt, sz = key
        rows.append({'group': gi, 'stage': st, 'isgn': il, 'osgn': ol,
                     'shex': sl, 'rt': rt, 'record': sz, 'count': len(idxs),
                     'first_off': recs[idxs[0]]['off'],
                     'indices': idxs})
        print('  %-2d %-3s %-5d %-5d %-7d %-3d %-6d %-4d %d' % (
            gi, st, il, ol, sl, rt, sz, len(idxs), recs[idxs[0]]['off']))

    cand = [r for r in rows if r['stage'] == 'ps' and r['rt'] >= 2]
    print()
    if cand:
        print('多渲染目标（RT>=2）的像素 pass 候选 —— 延迟光照可见 pass 通常在这里：')
        for r in sorted(cand, key=lambda x: -x['count']):
            print('   #%d  x%d 记录 %d B  SHEX %d  RT=%d' % (
                r['group'], r['count'], r['record'], r['shex'], r['rt']))
    else:
        print('⚠ 没有任何 RT>=2 的像素 pass —— 若网格走延迟光照，这个载荷不可能画出东西。')

    if args.json:
        with open(args.json, 'w', encoding='utf-8') as fh:
            json.dump(rows, fh, ensure_ascii=False, indent=1)
        print('\nJSON -> %s' % args.json)

    if args.group is not None:
        if not 0 <= args.group < len(rows):
            raise SystemExit('组号越界（0..%d）' % (len(rows) - 1))
        r = rows[args.group]
        if not args.out_group:
            raise SystemExit('导出请给 --out-group <路径>')
        blobs = b''
        for i in r['indices']:
            o, sz = recs[i]['off'], recs[i]['size']
            blobs += pay[o:o + sz]
        open(args.out_group, 'wb').write(blobs)
        print('已导出组 #%d 的 %d 条记录 -> %s（%d B）' % (
            r['group'], r['count'], args.out_group, len(blobs)))


if __name__ == '__main__':
    sys.exit(main())
