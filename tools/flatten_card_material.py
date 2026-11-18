"""把色卡材质的非 albedo 槽全部指向一张中性平贴图（完整重打包 patch）。

patch 结构（实测）：
    [0..4)   magic 0xF0000011
    [4..8)   类型表条目数 tn
    [8..12)  条目数 en
    [12..72) 保留（原样复制）
    [72 .. 72+tn*32)  类型表
    [.. + en*80)      条目表（每条 80 B：+0 fileId,+8 typeId,+16 tocOff,+32 gpuOff,+56 tocSize,+64 gpuSize,+76 序号）
    TocData 区
侧车 .gpu_resources 按条目 gpuOff/gpuSize 排列。

因为条目表在头部，新增条目必须重排所有偏移，所以这里做一次完整重打包。
"""
import os
import struct
import subprocess
import sys

import cv2
import numpy as np

REPO = r'E:\Data\source\repos\WpfApp1'
PATCH = sys.argv[1] if len(sys.argv) > 1 else r'E:\mod\Manuka_JABIS_Bill\Overwrite\9ba626afa44a3aa3.patch_2'
TEX_TYPE = 0xCD4238C6A0C69E32
MAT_TYPE = 0xEAC0B497876ADEDF
UNIT_TYPE = 0xE0A48D0BE9A7453F
CARD_TEX = 0xDEF7DC54E86D6001
FLAT_TEX = 0xDEF7DC54E86D6003
MAT_ID = 0xDEF7DC54E86D6002
ALBEDO_SLOT_KEY = 0xE67AC0C7
FLAT_DIR = r'E:\tmp\flat2048'
TEXCONV = os.path.join(REPO, r'HD2D_SDK_PLACEHOLDER')
TEXCONV = os.path.join(REPO, r'tools\ref\HD2SDK-CommunityEdition\deps\texconv.exe')


def make_flat_pixels():
    os.makedirs(FLAT_DIR, exist_ok=True)
    png = os.path.join(FLAT_DIR, 'flat.png')
    img = np.zeros((2048, 2048, 3), np.uint8)
    img[:, :] = (255, 128, 128)          # BGR -> RGB(128,128,255)，法线解码后接近 +Z
    cv2.imwrite(png, img)
    subprocess.run([TEXCONV, '-nologo', '-f', 'BC7_UNORM', '-m', '1', '-y',
                    '-o', FLAT_DIR, png], capture_output=True, text=True)
    d = open(os.path.join(FLAT_DIR, 'flat.dds'), 'rb').read()
    return d[148:]


def read_patch(path):
    d = open(path, 'rb').read()
    gpu = open(path + '.gpu_resources', 'rb').read()
    magic, = struct.unpack_from('<I', d, 0)
    assert magic == 0xF0000011, hex(magic)
    tn, en = struct.unpack_from('<II', d, 4)
    type_table = d[72:72 + tn * 32]
    eo = 72 + tn * 32
    entries = []
    for k in range(en):
        raw = bytearray(d[eo + k * 80: eo + (k + 1) * 80])
        fid, typ = struct.unpack_from('<QQ', raw, 0)
        tocoff, = struct.unpack_from('<Q', raw, 16)
        gpuoff, = struct.unpack_from('<Q', raw, 32)
        tocsz, = struct.unpack_from('<I', raw, 56)
        gpusz, = struct.unpack_from('<I', raw, 64)
        entries.append(dict(raw=raw, fid=fid, typ=typ,
                            toc=bytearray(d[tocoff:tocoff + tocsz]),
                            gpu=bytearray(gpu[gpuoff:gpuoff + gpusz])))
    return d[:12] + d[12:72], type_table, entries


def write_patch(path, head60, type_table, entries):
    tn = len(type_table) // 32
    en = len(entries)
    header = bytearray(72)
    header[0:12] = head60[0:12]
    struct.pack_into('<II', header, 4, tn, en)
    header[12:72] = head60[12:72]

    toc_off = 72 + tn * 32 + en * 80
    out = bytearray(header) + bytearray(type_table)
    toc_blob = bytearray()
    gpu_blob = bytearray()
    for i, e in enumerate(entries):
        # GPU 载荷按 64 字节对齐（实测原始 patch：564 -> 576，576 = 9*64）
        while len(gpu_blob) % 64:
            gpu_blob += b'\x00'
        raw = bytearray(e['raw'])
        struct.pack_into('<Q', raw, 16, toc_off + len(toc_blob))
        struct.pack_into('<Q', raw, 32, len(gpu_blob))
        struct.pack_into('<I', raw, 56, len(e['toc']))
        struct.pack_into('<I', raw, 64, len(e['gpu']))
        struct.pack_into('<I', raw, 76, i + 1)
        out += raw
        toc_blob += e['toc']
        gpu_blob += e['gpu']
    out += toc_blob
    open(path, 'wb').write(bytes(out))
    open(path + '.gpu_resources', 'wb').write(bytes(gpu_blob))
    return len(out), len(gpu_blob)


def main():
    flat = make_flat_pixels()
    head, type_table, entries = read_patch(PATCH)

    card = next(e for e in entries if e['fid'] == CARD_TEX)
    mat = next(e for e in entries if e['fid'] == MAT_ID)
    assert len(card['gpu']) == len(flat), (len(card['gpu']), len(flat))

    # 1) 新增平贴图条目（TocData 沿用色卡贴图的，格式/尺寸一致）
    entries.append(dict(raw=bytearray(card['raw']), fid=FLAT_TEX, typ=TEX_TYPE,
                        toc=bytearray(card['toc']), gpu=bytearray(flat)))
    struct.pack_into('<Q', entries[-1]['raw'], 0, FLAT_TEX)
    struct.pack_into('<Q', entries[-1]['raw'], 8, TEX_TYPE)

    # 2) 材质槽位重定向
    m = mat['toc']
    ntex, = struct.unpack_from('<I', m, 64)
    key_base, tex_base = 136, 136 + 4 * ntex
    print('材质槽 %d 个：' % ntex)
    for i in range(ntex):
        key, = struct.unpack_from('<I', m, key_base + 4 * i)
        want = CARD_TEX if key == ALBEDO_SLOT_KEY else FLAT_TEX
        struct.pack_into('<Q', m, tex_base + 8 * i, want)
        print('   槽 %2d key=0x%08x -> %s' % (i, key, '色卡' if want == CARD_TEX else '平贴图'))

    n1, n2 = write_patch(PATCH, head, type_table, entries)

    # 写盘后复核：非空 GPU 载荷必须 64 字节对齐、不重叠、不越界。
    # 2026-11-17 忘了对齐 -> 引擎读到错位数据 -> 游戏启动即崩；这条检查就是为此加的。
    d2 = open(PATCH, 'rb').read()
    g2 = open(PATCH + '.gpu_resources', 'rb').read()
    tn2, en2 = struct.unpack_from('<II', d2, 4)
    eo2 = 72 + tn2 * 32
    prev_end = 0
    for k in range(en2):
        o2 = eo2 + k * 80
        go, = struct.unpack_from('<Q', d2, o2 + 32)
        gs, = struct.unpack_from('<I', d2, o2 + 64)
        if gs == 0:
            continue
        if go % 64:
            raise SystemExit('GPU 载荷 gpuOff=%d 未 64 字节对齐' % go)
        if go < prev_end:
            raise SystemExit('GPU 载荷 gpuOff=%d 与上一段重叠' % go)
        if go + gs > len(g2):
            raise SystemExit('GPU 载荷 gpuOff=%d size=%d 越界（侧车 %d）' % (go, gs, len(g2)))
        prev_end = go + gs
    print('对齐 / 重叠 / 越界 复核通过')
    print()
    print('重打包完成：patch %d B，侧车 %d B' % (n1, n2))
    for e in entries:
        kind = {TEX_TYPE: 'texture', MAT_TYPE: 'material', UNIT_TYPE: 'unit'}.get(e['typ'], '?')
        print('   %-9s 0x%016x toc=%-5d gpu=%d' % (kind, e['fid'], len(e['toc']), len(e['gpu'])))


if __name__ == '__main__':
    main()
