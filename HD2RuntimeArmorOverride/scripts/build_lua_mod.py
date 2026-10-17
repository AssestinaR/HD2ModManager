# -*- coding: utf-8 -*-
"""把一段 Lua 源码打包成一个可导入管理器的 Lua mod zip。

布局照抄 `HD2ModCore/Infrastructure/LuaDeploymentService.cs:549-611`
（`BuildArmorRegistryAddonArchive` + `StingrayHash`）：

    <out>.zip
        manifest.json
        Addon\\9ba626afa44a3aa3.patch_0              ← 只有一条 Lua 资源
        Addon\\9ba626afa44a3aa3.patch_0.gpu_resources (0 字节)
        Addon\\9ba626afa44a3aa3.patch_0.stream        (0 字节)

归档头 104 字节 + 1 条 80 字节条目 + 16 对齐的资源载荷。
资源本身是 `u32 长度 + u32 版本(2) + Lua 源码`。

为什么需要这个脚本：`tools/unitprobe` 这类探针必须**在游戏进程内**跑才有意义
（外部改内存不是我们的目标），而在游戏内跑的唯一受支持方式是做一个 Lua mod。
"""

import argparse
import json
import os
import struct
import sys
import zipfile

sys.stdout.reconfigure(encoding='utf-8')

LUA_TYPE_ID = 0xA14E8DFA2CD117E2
ARCHIVE_NAME = '9ba626afa44a3aa3'


def stingray_hash(value):
    """IdString64 哈希，逐行照抄 LuaDeploymentService.StingrayHash。"""
    mix = 0xC6A4A7935BD1E995
    mask = 0xFFFFFFFFFFFFFFFF
    data = value.encode('utf-8')
    h = (len(data) * mix) & mask
    offset = 0
    while offset + 8 <= len(data):
        word = int.from_bytes(data[offset:offset + 8], 'little')
        word = (word * mix) & mask
        word ^= word >> 47
        h = ((h ^ ((word * mix) & mask)) * mix) & mask
        offset += 8
    if offset < len(data):
        tail = 0
        for index, byte in enumerate(data[offset:]):
            tail |= byte << (index * 8)
        h = ((h ^ tail) * mix) & mask
    h ^= h >> 47
    h = (h * mix) & mask
    return h ^ (h >> 47)


def align16(value):
    return (value + 15) & ~15


def build_archive(resource_name, lua_bytes):
    body = lua_bytes
    resource = struct.pack('<II', len(body), 2) + body

    header_length = 104
    entry_length = 80
    payload_offset = align16(header_length + entry_length)
    archive_length = align16(payload_offset + len(resource))

    archive = bytearray(archive_length)
    struct.pack_into('<III', archive, 0, 0xF0000011, 1, 1)
    struct.pack_into('<Q', archive, 32, archive_length)
    # Type table: one record at 72, 32 bytes.
    struct.pack_into('<Q', archive, 72 + 8, LUA_TYPE_ID)
    struct.pack_into('<Q', archive, 72 + 16, 1)
    struct.pack_into('<I', archive, 72 + 24, 16)
    struct.pack_into('<I', archive, 72 + 28, 16)
    # File table: one record at 104, 80 bytes.
    struct.pack_into('<Q', archive, 104, stingray_hash(resource_name))
    struct.pack_into('<Q', archive, 112, LUA_TYPE_ID)
    struct.pack_into('<Q', archive, 120, payload_offset)
    struct.pack_into('<I', archive, 160, len(resource))
    struct.pack_into('<I', archive, 172, 16)
    struct.pack_into('<I', archive, 176, 16)
    archive[payload_offset:payload_offset + len(resource)] = resource
    return bytes(archive)


def build_zip(zip_path, resource_name, lua_source, guid, name, description):
    declaration = '-- HD2-Addon: %s\n' % resource_name
    body = lua_source if lua_source.startswith('-- HD2-Addon:') else declaration + lua_source
    archive = build_archive(resource_name, body.encode('utf-8'))

    manifest = {
        'Version': 1,
        'Guid': guid,
        'Name': name,
        'Description': description,
        'Options': [
            {
                'Name': name,
                'Description': description,
                'Include': ['Addon'],
            }
        ],
    }

    os.makedirs(os.path.dirname(os.path.abspath(zip_path)), exist_ok=True)
    with zipfile.ZipFile(zip_path, 'w', zipfile.ZIP_DEFLATED) as bundle:
        bundle.writestr('manifest.json', json.dumps(manifest, ensure_ascii=False, indent=2))
        bundle.writestr('Addon/%s.patch_0' % ARCHIVE_NAME, archive)
        bundle.writestr('Addon/%s.patch_0.gpu_resources' % ARCHIVE_NAME, b'')
        bundle.writestr('Addon/%s.patch_0.stream' % ARCHIVE_NAME, b'')
    return len(archive)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--lua', required=True, help='Lua 源文件')
    parser.add_argument('--resource', required=True, help='资源名，例如 mods/codex/section_toggle_probe')
    parser.add_argument('--out', required=True, help='输出 zip 路径')
    parser.add_argument('--guid', required=True)
    parser.add_argument('--name', required=True)
    parser.add_argument('--description', default='Requires Bingus Shared Loader v15 or newer / API 1.')
    args = parser.parse_args()

    with open(args.lua, 'r', encoding='utf-8') as handle:
        source = handle.read()
    size = build_zip(args.out, args.resource, source, args.guid, args.name, args.description)
    print('资源名     %s' % args.resource)
    print('资源哈希   0x%016x' % stingray_hash(args.resource))
    print('归档长度   %d 字节' % size)
    print('已写出     %s' % os.path.abspath(args.out))
    return 0


if __name__ == '__main__':
    sys.exit(main())
