"""一键重建色卡板 patch：原语几何 → 合成材质/贴图 → 覆盖色卡 → 换三个新 id。

约定：
  · 图片保持正向（不翻图，翻了 ArUco 会镜像）
  · V 方向的修正放在几何 UV 里（CanonicalPrimitiveBuilder.FlipV，默认 true）
"""
import os
import shutil
import struct
import subprocess
import sys

REPO = r'E:\Data\source\repos\WpfApp1'
EXE = os.path.join(REPO, r'HD2ModCli\bin\Debug\net10.0\hd2mod.exe')
TEMPLATE = os.path.join(REPO, r'HD2RuntimeArmorOverride\resources\wardrobe_hidden_unit.patch')
P1 = r'E:\mod\Manuka_JABIS_Bill\Overwrite\9ba626afa44a3aa3.patch_1'
OVERWRITE = r'E:\mod\Manuka_JABIS_Bill\Overwrite'

UNIT_TYPE = 0xE0A48D0BE9A7453F
MAT_TYPE = 0xEAC0B497876ADEDF
TEX_TYPE = 0xCD4238C6A0C69E32

SRC_UNIT = 0xDEF7DC54E86D6054
SRC_MAT = 0xA990A3DB10C4318B
SRC_TEX = 0xB990A3DB10C4318B

NEW_UNIT = 0x02E8377209BA0008
NEW_MAT = 0xDEF7DC54E86D6002
NEW_TEX = 0xDEF7DC54E86D6001

CARD_DDS = r'E:\tmp\card2048\card.dds'

SHAPE, SIZE, PLANE, CENTER = 'quad', '1.2,1.2', 'xz', '1.1,0,0.2'


def run(args, **kw):
    r = subprocess.run(args, capture_output=True, text=True, encoding='utf-8', errors='replace', **kw)
    out = (r.stdout or '') + (r.stderr or '')
    print(out.strip()[:600])
    if r.returncode != 0:
        raise SystemExit('命令失败: %s' % ' '.join(args))


def patch_entries(path):
    d = open(path, 'rb').read()
    tn, en = struct.unpack_from('<II', d, 4)
    eo = 72 + tn * 32
    out = []
    for k in range(en):
        o = eo + k * 80
        fid, = struct.unpack_from('<Q', d, o)
        typ, = struct.unpack_from('<Q', d, o + 8)
        tocoff, = struct.unpack_from('<Q', d, o + 16)
        toclen, = struct.unpack_from('<I', d, o + 56)
        out.append((o, fid, typ, tocoff, toclen))
    return d, out


def main():
    a_dir = r'E:\tmp\cardA'
    b_dir = r'E:\tmp\cardB'
    for d in (a_dir, b_dir):
        if os.path.isdir(d):
            shutil.rmtree(d)

    # 1) 原语 unit（FlipV 默认 true，UV 在这里翻）
    run([EXE, 'patch', 'primitive', TEMPLATE, SHAPE, SIZE, PLANE, CENTER,
         'mat=0x%016x' % SRC_MAT, '--keys', '0x%016x:0x%016x' % (UNIT_TYPE, SRC_UNIT),
         '--output', a_dir])
    a_patch = os.path.join(a_dir, 'wardrobe_hidden_unit.patch')

    # 2) 合成 unit + 材质 + 贴图
    run([EXE, 'patch', 'compose', '--header', a_patch,
         '--pick', a_patch, '0x%016x' % UNIT_TYPE, '0x%016x' % SRC_UNIT,
         '--pick', P1, '0x%016x' % MAT_TYPE, '0x%016x' % SRC_MAT,
         '--pick', P1, '0x%016x' % TEX_TYPE, '0x%016x' % SRC_TEX,
         '--output', b_dir])
    b_patch = os.path.join(b_dir, 'wardrobe_hidden_unit.patch')

    # 3) 覆盖贴图像素为色卡（此时 id 还是源 id）
    run([sys.executable, '-B', os.path.join(REPO, r'tools\embed_texture_data.py'),
         '--patch', b_patch, '--texture', '0x%016x' % SRC_TEX, '--dds', CARD_DDS])

    # 4) 换 id：贴图 → 材质内部引用；材质 → 条目 id + unit 内部引用；unit → 条目 id
    d, entries = patch_entries(b_patch)
    d = bytearray(d)
    for o, fid, typ, tocoff, toclen in entries:
        body = bytes(d[tocoff:tocoff + toclen])
        if typ == TEX_TYPE and fid == SRC_TEX:
            struct.pack_into('<Q', d, o, NEW_TEX)
        elif typ == MAT_TYPE and fid == SRC_MAT:
            struct.pack_into('<Q', d, o, NEW_MAT)
            d[tocoff:tocoff + toclen] = body.replace(struct.pack('<Q', SRC_TEX), struct.pack('<Q', NEW_TEX))
        elif typ == UNIT_TYPE and fid == SRC_UNIT:
            struct.pack_into('<Q', d, o, NEW_UNIT)
            d[tocoff:tocoff + toclen] = body.replace(struct.pack('<Q', SRC_MAT), struct.pack('<Q', NEW_MAT))
    open(b_patch, 'wb').write(bytes(d))

    # 5) 部署
    shutil.copy2(b_patch, os.path.join(OVERWRITE, '9ba626afa44a3aa3.patch_2'))
    shutil.copy2(b_patch + '.gpu_resources', os.path.join(OVERWRITE, '9ba626afa44a3aa3.patch_2.gpu_resources'))

    _, entries = patch_entries(b_patch)
    print()
    print('最终条目：')
    for _, fid, typ, _, _ in entries:
        kind = {UNIT_TYPE: 'unit', MAT_TYPE: 'material', TEX_TYPE: 'texture'}.get(typ, '0x%x' % typ)
        print('   %-9s 0x%016x' % (kind, fid))
    print('已部署到 %s\\9ba626afa44a3aa3.patch_2' % OVERWRITE)


if __name__ == '__main__':
    main()
