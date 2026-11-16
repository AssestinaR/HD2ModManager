#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""启动前硬校验：确认测试 mod 真的被管理器部署，且引擎读到的内容就是我们的产物。

背景（2026-11-14 实测）：
  · `data\\` 下的 patch 全是**符号链接**，指向库里的 mod 目录或 `<库>\\.runtime\\<sha256>\\`
  · **库里的文件夹名不等于部署索引** —— 例如目录 `9ba626afa44a3aa3.patch_73\\`
    实际被分配到 `data\\9ba626afa44a3aa3.patch_69`（管理器按 loadOrder 重新编号）
  · 所以不能按名字推断，只能**反查符号链接的目标路径**

判据：
  1. 至少有一个 data\\<archive>.patch_<idx> 解引用后落在该 mod 目录内
  2. 该 index 之后的索引链连续（引擎只读到断点）
  3. 部署文件的 sha256 与该目录内对应源文件一致

用法：
    python tools/verify_deployed.py --mod "9ba626afa44a3aa3.patch_73"
    python tools/verify_deployed.py --mod _matlab
退出码：0 = DEPLOYED OK；1 = 不满足（不要进游戏）
"""
import argparse
import hashlib
import os
import re
import sys

DEFAULT_DATA = r'D:\SteamLibrary\steamapps\common\Helldivers 2\data'
DEFAULT_LIBRARY = r'E:\mod'
PATCH_RE = re.compile(r'^(?P<archive>[0-9a-fA-F]{16})\.patch_(?P<idx>\d+)(?P<suffix>\.gpu_resources|\.stream)?$')


def sha256_of(path, chunk=1 << 20):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for b in iter(lambda: f.read(chunk), b''):
            h.update(b)
    return h.hexdigest()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--mod', required=True, help='库里的 mod 目录名')
    ap.add_argument('--data', default=DEFAULT_DATA)
    ap.add_argument('--library', default=DEFAULT_LIBRARY)
    args = ap.parse_args()

    mod_dir = os.path.normcase(os.path.realpath(os.path.join(args.library, args.mod)))
    if not os.path.isdir(mod_dir):
        print('FAIL 库目录不存在: %s' % mod_dir)
        return 1
    print('mod 目录: %s' % mod_dir)

    deployed = {}       # (archive, idx) -> {suffix: resolved}
    for name in os.listdir(args.data):
        m = PATCH_RE.match(name)
        if not m:
            continue
        full = os.path.join(args.data, name)
        real = os.path.normcase(os.path.realpath(full))
        if real.startswith(mod_dir + os.sep) or real.startswith(mod_dir):
            key = (m.group('archive'), int(m.group('idx')))
            deployed.setdefault(key, {})[m.group('suffix') or ''] = real

    if not deployed:
        print('FAIL data\\ 里没有任何符号链接指向这个目录 —— 管理器没部署它')
        return 1

    ok = True
    # 库内源文件恒为 patch_0..patch_{N-1}，按部署索引升序对应
    src_index = {}
    for archive in sorted({a for a, _ in deployed}):
        idxs = sorted(i for a, i in deployed if a == archive)
        for pos, i in enumerate(idxs):
            src_index[(archive, i)] = pos

    for (archive, idx), files in sorted(deployed.items()):
        pos = src_index[(archive, idx)]
        print('\n部署索引: %s.patch_%d  ← 库内源 patch_%d' % (archive, idx, pos))
        for suffix, real in sorted(files.items()):
            src = os.path.join(mod_dir, '%s.patch_%d%s' % (archive, pos, suffix))
            if not os.path.isfile(src):
                print('  WARN %-14s 目标 %s（库里找不到同名源文件，跳过比对）' % (suffix or '(toc)', real))
                continue
            a, b = sha256_of(real), sha256_of(src)
            if a != b:
                print('  FAIL %-14s 内容不一致 部署=%s 源=%s' % (suffix or '(toc)', a[:16], b[:16]))
                ok = False
            else:
                print('  OK   %-14s %d B sha256=%s' % (suffix or '(toc)', os.path.getsize(real), a[:16]))

    # 索引链连续性：引擎从 0 顺读，断在哪就只读到哪
    for archive in sorted({a for a, _ in deployed}):
        have = set()
        for name in os.listdir(args.data):
            m = PATCH_RE.match(name)
            if m and m.group('archive').lower() == archive.lower() and (m.group('suffix') or '') == '':
                have.add(int(m.group('idx')))
        top = max(have) if have else -1
        missing = [i for i in range(top + 1) if i not in have]
        if missing:
            print('\nFAIL %s 索引链断裂，缺: %s（断点之后的 patch 引擎不会读）' % (archive, missing[:10]))
            ok = False
        else:
            print('\nOK   %s 索引链 0..%d 连续，共 %d 个' % (archive, top, len(have)))

    print()
    if ok:
        print('DEPLOYED OK —— 引擎会读到我们的内容，可以进游戏')
        return 0
    print('NOT DEPLOYED —— 不要进游戏')
    return 1


if __name__ == '__main__':
    sys.exit(main())
