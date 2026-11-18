"""用 C# 的 PatchArchiveWriter 生成"4 条目"的平整色卡 patch。

为什么不手写重排：手写要自己算条目表、偏移、64 字节对齐，任何一处错都是启动崩溃（已踩两次）。
`hd2mod patch compose` 背后就是 PatchArchiveWriter，结构由它负责。

步骤：
  1. 取当前能跑的 patch（3 条目），复制一份，把它的**色卡贴图**改成平贴图像素、
     并把该条目 id 改成平贴图 id  → 得到"只有一张平贴图"的 patch F
  2. compose：unit + 色卡贴图 + 材质（来自好 patch） + 平贴图（来自 F） → 4 条目新 patch
  3. 只做字节级修改：把材质里除 albedo 之外的槽指向平贴图 id
"""
import os
import shutil
import struct
import subprocess
import sys

import cv2
import numpy as np

REPO = r'E:\Data\source\repos\WpfApp1'
EXE = os.path.join(REPO, r'HD2ModCli\bin\Debug\net10.0\hd2mod.exe')
GOOD = r'E:\tmp\patch_2.bak-before-flat'          # 已知可启动的 3 条目 patch
GOOD_GPU = GOOD + '.gpu_resources'
OUT_DIR = r'E:\tmp\flat_compose'
TEXCONV = os.path.join(REPO, r'tools\ref\HD2SDK-CommunityEdition\deps\texconv.exe')

UNIT_TYPE = 0xE0A48D0BE9A7453F
MAT_TYPE = 0xEAC0B497876ADEDF
TEX_TYPE = 0xCD4238C6A0C69E32

UNIT_ID = 0x02E8377209BA0008
CARD_TEX = 0xDEF7DC54E86D6001
MAT_ID = 0xDEF7DC54E86D6002
FLAT_TEX = 0xDEF7DC54E86D6003
ALBEDO_SLOT_KEY = 0xE67AC0C7
FLAT_DIR = r'E:\tmp\flat2048'


def run(args):
    r = subprocess.run(args, capture_output=True, text=True, encoding='utf-8', errors='replace')
    print(((r.stdout or '') + (r.stderr or '')).strip()[:500])
    if r.returncode != 0:
        raise SystemExit('命令失败: %s' % ' '.join(args))


def flat_pixels():
    os.makedirs(FLAT_DIR, exist_ok=True)
    png = os.path.join(FLAT_DIR, 'flat.png')
    img = np.zeros((2048, 2048, 3), np.uint8)
    img[:, :] = (255, 128, 128)                 # BGR -> RGB(128,128,255)
    cv2.imwrite(png, img)
    subprocess.run([TEXCONV, '-nologo', '-f', 'BC7_UNORM', '-m', '1', '-y', '-o', FLAT_DIR, png],
                   capture_output=True, text=True)
    d = open(os.path.join(FLAT_DIR, 'flat.dds'), 'rb').read()
    return d[148:]


def entry_of(path, want_id):
    d = open(path, 'rb').read()
    tn, en = struct.unpack_from('<II', d, 4)
    eo = 72 + tn * 32
    for k in range(en):
        o = eo + k * 80
        fid, = struct.unpack_from('<Q', d, o)
        if fid == want_id:
            to, = struct.unpack_from('<Q', d, o + 16)
            ts, = struct.unpack_from('<I', d, o + 56)
            return o, to, ts
    raise SystemExit('找不到条目 0x%016x' % want_id)


def main():
    for d in (OUT_DIR,):
        if os.path.isdir(d):
            shutil.rmtree(d)
    os.makedirs(OUT_DIR, exist_ok=True)

    # ---- 1) 造 patch F：一张平贴图 ----
    f_patch = os.path.join(OUT_DIR, 'flat.patch_0')
    shutil.copy2(GOOD, f_patch)
    shutil.copy2(GOOD_GPU, f_patch + '.gpu_resources')

    o, _, _ = entry_of(f_patch, CARD_TEX)
    d = bytearray(open(f_patch, 'rb').read())
    struct.pack_into('<Q', d, o, FLAT_TEX)          # 换 id
    open(f_patch, 'wb').write(bytes(d))
    print('patch F: 条目标题 id 改为平贴图 0x%016x' % FLAT_TEX)

    # 用平贴图像素覆盖（此时它已是唯一贴图条目）
    run([sys.executable, '-B', os.path.join(REPO, r'tools\embed_texture_data.py'),
         '--patch', f_patch, '--texture', '0x%016x' % FLAT_TEX, '--dds',
         os.path.join(FLAT_DIR, 'flat.dds')])

    # ---- 2) compose：unit + 色卡 + 材质 + 平贴图 ----
    out_patch = os.path.join(OUT_DIR, 'card4.patch_0')
    run([EXE, 'patch', 'compose', '--header', GOOD,
         '--pick', GOOD, '0x%016x' % UNIT_TYPE, '0x%016x' % UNIT_ID,
         '--pick', GOOD, '0x%016x' % TEX_TYPE, '0x%016x' % CARD_TEX,
         '--pick', GOOD, '0x%016x' % MAT_TYPE, '0x%016x' % MAT_ID,
         '--pick', f_patch, '0x%016x' % TEX_TYPE, '0x%016x' % FLAT_TEX,
         '--output', OUT_DIR + r'\composed'])

    composed = os.path.join(OUT_DIR, 'composed', os.path.basename(GOOD))
    if not os.path.exists(composed):
        for root, _, files in os.walk(OUT_DIR + r'\composed'):
            for fn in files:
                if fn.endswith('.patch_0') or '.patch_' in fn:
                    composed = os.path.join(root, fn)
                    break
    print('compose 产物:', composed)

    # ---- 3) 只改材质槽位字节 ----
    shutil.copy2(composed, out_patch)
    shutil.copy2(composed + '.gpu_resources', out_patch + '.gpu_resources')

    o, to, ts = entry_of(out_patch, MAT_ID)
    d = bytearray(open(out_patch, 'rb').read())
    ntex, = struct.unpack_from('<I', d, to + 64)
    key_base, tex_base = to + 136, to + 136 + 4 * ntex
    print('材质槽 %d 个：' % ntex)
    for i in range(ntex):
        key, = struct.unpack_from('<I', d, key_base + 4 * i)
        want = CARD_TEX if key == ALBEDO_SLOT_KEY else FLAT_TEX
        struct.pack_into('<Q', d, tex_base + 8 * i, want)
    open(out_patch, 'wb').write(bytes(d))
    print('槽位已改（albedo=色卡，其余=平贴图）')

    # ---- 4) 结构自检 ----
    d = open(out_patch, 'rb').read()
    g = open(out_patch + '.gpu_resources', 'rb').read()
    tn, en = struct.unpack_from('<II', d, 4)
    eo = 72 + tn * 32
    prev = 0
    print()
    print('最终：patch %d B，侧车 %d B，条目 %d' % (len(d), len(g), en))
    for k in range(en):
        oo = eo + k * 80
        fid, = struct.unpack_from('<Q', d, oo)
        go, = struct.unpack_from('<Q', d, oo + 32)
        gs, = struct.unpack_from('<I', d, oo + 64)
        if gs:
            assert go % 64 == 0 and go >= prev and go + gs <= len(g), (fid, go, gs)
            prev = go + gs
        print('   0x%016x gpuOff=%-9d size=%d' % (fid, go, gs))
    print('结构自检通过')


if __name__ == '__main__':
    main()
