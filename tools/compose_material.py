#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""合成材质变体：把某个 patch 里的一个材质条目，换成一个「根材质」（自己的载荷 + TocData）。

这是生产线的核心动作。已验证的三件事：
  1. 整条 DXBC 记录等长替换会被引擎接受（docs/.../七之四）；
  2. 材质 TocData 可以整体换掉、贴图条目不动（七之七）；
  3. 根材质的 TocData 必须是裸 TocData（`PayloadDump --toc` 出来的 `.toc`）。

用法：
    python tools/compose_material.py \
        --base <原始 patch_0> --file-id 0xe577db2cf97fcddc \
        --root-dir <含 <id>.toc 与 <id>.gpu 的目录> \
        [--set-slot <槽key hex>=<纹理fileId hex> ...] \
        [--set-var <变量名或hex>=<v1,v2,...> ...] \
        --out <输出目录> [--name <文件名前缀>]

产物：<out>/<name>          （TOC）
      <out>/<name>.gpu_resources
"""
import argparse
import os
import struct
import sys

MAGIC = 0xF0000011
NAMES = {}


def load_names(path):
    if not path or not os.path.isfile(path):
        return
    for line in open(path, 'r', encoding='utf-8', errors='replace'):
        q = line.split()
        if len(q) >= 2 and q[1].lower().startswith('0x'):
            try:
                NAMES[int(q[1], 16) & 0xffffffff] = q[0]
            except ValueError:
                pass


def parse_patch(d):
    magic, = struct.unpack_from('<I', d, 0)
    if magic != MAGIC:
        raise SystemExit('不是 patch TOC（magic=0x%08x）' % magic)
    tn, en = struct.unpack_from('<II', d, 4)
    types = [d[72 + k * 32:72 + (k + 1) * 32] for k in range(tn)]
    eoff = 72 + tn * 32
    entries = []
    for k in range(en):
        o = eoff + k * 80
        raw = bytearray(d[o:o + 80])
        f = struct.unpack_from('<QQQQQQQIIIIII', raw, 0)
        entries.append({'raw': raw, 'fileId': f[0], 'typeId': f[1], 'tocOff': f[2],
                        'gpuOff': f[4], 'tocSize': f[7], 'gpuSize': f[9]})
    return d[:72], types, entries


def toc_geometry(t):
    ntex, = struct.unpack_from('<I', t, 64)
    nvar, = struct.unpack_from('<I', t, 104)
    return ntex, nvar, 136 + 12 * ntex, 136 + 12 * ntex + 20 * nvar


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--base', required=True)
    ap.add_argument('--file-id', required=True)
    ap.add_argument('--root-dir', required=True)
    ap.add_argument('--root-id', default=None, help='根材质 id；省略则用 --file-id')
    ap.add_argument('--set-slot', nargs='*', default=[])
    ap.add_argument('--set-var', nargs='*', default=[])
    ap.add_argument('--out', required=True)
    ap.add_argument('--name', default=None)
    ap.add_argument('--names-file', default=None)
    ap.add_argument('--child', action='store_true',
                    help='做成子材质：parent=根材质、gpuSize=0、undat2=0，不携带任何载荷')
    args = ap.parse_args()
    load_names(args.names_file)

    want = int(args.file_id, 16)
    base_toc = open(args.base, 'rb').read()
    base_gpu = bytearray(open(args.base + '.gpu_resources', 'rb').read())
    header, types, entries = parse_patch(base_toc)
    hit = [e for e in entries if e['fileId'] == want]
    if not hit:
        raise SystemExit('base 里没有材质 0x%016x' % want)
    tgt = hit[0]

    stem = '%016x' % (int(args.root_id, 16) if args.root_id else want)
    root_dir = args.root_dir
    # 支持两种命名：<id>.toc/<id>.gpu 或 <id>.toc/<id>.gpu_resources
    toc_path = os.path.join(root_dir, stem + '.toc')
    gpu_path = os.path.join(root_dir, stem + '.gpu')
    if not os.path.isfile(gpu_path):
        gpu_path = os.path.join(root_dir, stem + '.gpu_resources')
    if not os.path.isfile(toc_path) or not os.path.isfile(gpu_path):
        raise SystemExit('找不到根材质文件: %s / %s' % (toc_path, gpu_path))
    new_toc = bytearray(open(toc_path, 'rb').read())
    new_gpu = open(gpu_path, 'rb').read()

    if args.child:
        # 子材质：parent 指向根材质，自己不携带载荷（undat2 必须为 0，否则与 gpuSize=0 矛盾）
        root_id = int(args.root_id, 16) if args.root_id else want
        struct.pack_into('<Q', new_toc, 16, 0)
        struct.pack_into('<Q', new_toc, 24, root_id)
        print('  子材质模式：parent=0x%016x, undat2=0' % root_id)
    else:
        struct.pack_into('<Q', new_toc, 16, len(new_gpu))
    ntex, nvar, rbase, vbase = toc_geometry(new_toc)
    print('根材质 0x%s: TocData %d B, 载荷 %d B, 槽 %d, 变量 %d' % (
        stem, len(new_toc), len(new_gpu), ntex, nvar))

    # 槽覆盖
    for spec in args.set_slot:
        k, v = spec.split('=')
        key, tex = int(k, 16), int(v, 16)
        done = False
        for i in range(ntex):
            if struct.unpack_from('<I', new_toc, 136 + 4 * i)[0] == key:
                struct.pack_into('<Q', new_toc, 136 + 4 * ntex + 8 * i, tex)
                print('  槽 0x%08x (%s) -> tex 0x%016x' % (key, NAMES.get(key, '?'), tex))
                done = True
                break
        if not done:
            print('  WARN 槽 0x%08x 不存在，忽略' % key)

    # 变量覆盖
    rev = {v: k for k, v in NAMES.items()}
    for spec in args.set_var:
        k, v = spec.split('=')
        want_id = int(k, 16) if k.lower().startswith('0x') else rev.get(k)
        if want_id is None:
            print('  WARN 未知变量名 %s' % k)
            continue
        vals = [float(x) for x in v.split(',')]
        done = False
        for i in range(nvar):
            kl, el, vid, off, st = struct.unpack_from('<5I', new_toc, rbase + 20 * i)
            if vid == want_id:
                for j, f in enumerate(vals):
                    struct.pack_into('<f', new_toc, vbase + off + 4 * j, f)
                print('  变量 %-22s = %s' % (NAMES.get(vid, '0x%08x' % vid), vals))
                done = True
                break
        if not done:
            print('  WARN 变量 0x%08x 不在这份 TocData 里' % want_id)

    # 重建 TOC
    head_len = 72 + len(types) * 32
    blobs = []
    cur = head_len + len(entries) * 80
    for e in entries:
        blob = bytes(new_toc) if e['fileId'] == want else base_toc[e['tocOff']:e['tocOff'] + e['tocSize']]
        blobs.append((e, blob, cur))
        cur += len(blob)
    out = bytearray(header)
    for t in types:
        out += t
    for (e, blob, off) in blobs:
        raw = bytearray(e['raw'])
        struct.pack_into('<Q', raw, 16, off)
        struct.pack_into('<I', raw, 56, len(blob))
        if e['fileId'] == want:
            struct.pack_into('<I', raw, 64, 0 if args.child else len(new_gpu))  # gpuSize
            struct.pack_into('<Q', raw, 32, 0)              # gpuOff = 0
        out += raw
    for (e, blob, off) in blobs:
        out += blob

    # 侧车：前 len(new_gpu) 覆盖，其余不动
    if len(new_gpu) > len(base_gpu):
        raise SystemExit('根材质载荷 %d B 比侧车 %d B 还大' % (len(new_gpu), len(base_gpu)))
    if not args.child:
        base_gpu[0:len(new_gpu)] = new_gpu

    os.makedirs(args.out, exist_ok=True)
    name = args.name or ('%s.patch_0' % '9ba626afa44a3aa3')
    toc_out = os.path.join(args.out, name)
    gpu_out = toc_out + '.gpu_resources'
    open(toc_out, 'wb').write(bytes(out))
    open(gpu_out, 'wb').write(bytes(base_gpu))
    print('\n写出 %s (%d B) + %s (%d B)' % (toc_out, len(out), os.path.basename(gpu_out), len(base_gpu)))

    # 自检
    _, _, e2 = parse_patch(bytes(out))
    for e in e2:
        tag = '  <<< 材质' if e['fileId'] == want else ''
        print('  0x%016x tocOff=%-5d tocSize=%-5d gpuOff=%-9d gpuSize=%-9d%s' % (
            e['fileId'], e['tocOff'], e['tocSize'], e['gpuOff'], e['gpuSize'], tag))
    got = [e for e in e2 if e['fileId'] == want][0]
    ok = bytes(out)[got['tocOff']:got['tocOff'] + got['tocSize']] == bytes(new_toc)
    print('  自检：材质 TocData 与根材质一致 = %s' % ok)


if __name__ == '__main__':
    sys.exit(main())
