# -*- coding: utf-8 -*-
"""在内存镜像里找对某个 RVA 的引用：直接 call/jmp、以及数据里的绝对地址。

为什么要单独一个脚本：护甲消费者 `0x81f470` 是**被调用**的一侧，它的三个
bank 缓冲区是参数传进来的。要回答"这些缓冲区属于哪个对象、谁按槽位索引它们"，
必须看调用点传了什么，以及有没有别的消费者直接引用同一批地址。

镜像来自 `capture_game_image.py`，布局是 RVA==文件偏移，所以这里不需要
section 映射。

只读：纯离线分析一个已落盘的文件。
"""
import argparse
import struct
import sys

from capstone import Cs, CS_ARCH_X86, CS_MODE_64


def find_rel32(data, target):
    """扫描 E8/E9 rel32（call/jmp rel32），返回命中的 RVA 列表。"""
    hits = []
    i = 0
    n = len(data)
    while i < n - 5:
        if data[i] in (0xE8, 0xE9):
            rel = struct.unpack_from('<i', data, i + 1)[0]
            if (i + 5 + rel) & 0xFFFFFFFF == target:
                hits.append((i, 'call' if data[i] == 0xE8 else 'jmp'))
        i += 1
    return hits


def find_abs64(data, value):
    """扫描 8 字节小端绝对地址（vtable / 指针表里常见）。"""
    needle = struct.pack('<Q', value)
    hits = []
    at = data.find(needle)
    while at != -1:
        hits.append(at)
        at = data.find(needle, at + 1)
    return hits


def find_abs32(data, value):
    needle = struct.pack('<I', value)
    hits = []
    at = data.find(needle)
    while at != -1:
        hits.append(at)
        at = data.find(needle, at + 1)
    return hits


def find_raw_dword(data, value, window=0x18):
    """扫 4 字节小端立即数/位移，并把所在指令反汇编出来。

    `lea reg, [base + disp32]` 里的 disp32 就是这类目标：护甲的三个 bank
    用 `r13 + 0x546b4c / 0x546b74 / 0x546b9c` 定位，直接扫这三个 dword
    就能列出所有碰它们的代码，不必先知道 r13 是什么。
    """
    md = Cs(CS_ARCH_X86, CS_MODE_64)
    out = []
    for at in find_abs32(data, value):
        start = max(0, at - window)
        code = data[start:at + 4 + window]
        hit = None
        for insn in md.disasm(code, start):
            if insn.address <= at < insn.address + insn.size:
                hit = insn
                break
        out.append((at, hit))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--image', required=True)
    ap.add_argument('--target', action='append', required=True,
                    help='RVA，可多次给；也接受 0x 前缀')
    ap.add_argument('--image-base', default='0x0',
                    help='模块基址（capture_game_image.py 打印的那个），用于扫绝对指针')
    ap.add_argument('--context', type=int, default=12,
                    help='每个命中点前后各反汇编多少条指令')
    ap.add_argument('--dword', action='append', default=[],
                    help='额外扫这个 4 字节立即数/位移（如 0x546b4c）')
    args = ap.parse_args()

    data = open(args.image, 'rb').read()
    image_base = int(args.image_base, 0)
    print('镜像 %d B，ImageBase 0x%x' % (len(data), image_base))

    for raw in args.dword:
        value = int(raw, 0)
        hits = find_raw_dword(data, value)
        print('\n===== 立即数/位移 0x%x：%d 处 =====' % (value, len(hits)))
        for at, insn in hits:
            if insn is None:
                print('    0x%08x  （未落在可解码指令里）' % at)
            else:
                print('    0x%08x  %-24s %s %s'
                      % (insn.address, insn.bytes.hex(), insn.mnemonic, insn.op_str))

    for raw in args.target:
        target = int(raw, 0)
        print('\n===== 目标 RVA 0x%x =====' % target)

        rel = find_rel32(data, target)
        print('  直接 call/jmp：%d 处' % len(rel))
        for rva, kind in rel:
            print('    0x%08x  %s' % (rva, kind))

        abs64 = find_abs64(data, image_base + target) if image_base else []
        print('  绝对 64 位指针（ImageBase+0x%x）：%d 处' % (target, len(abs64)))
        for rva in abs64[:40]:
            print('    0x%08x' % rva)
        if len(abs64) > 40:
            print('    … 另有 %d 处' % (len(abs64) - 40))

        if not rel:
            continue
        md = Cs(CS_ARCH_X86, CS_MODE_64)
        for rva, kind in rel[:6]:
            start = max(0, rva - args.context * 6)
            code = data[start:rva + 6 + args.context * 6]
            insns = list(md.disasm(code, start))
            print('\n  --- 0x%08x 上下文 ---' % rva)
            for insn in insns:
                mark = '  <<<' if insn.address == rva else ''
                print('    %08x: %-28s %s %s%s'
                      % (insn.address, insn.bytes.hex(), insn.mnemonic,
                         insn.op_str, mark))
    return 0


if __name__ == '__main__':
    sys.exit(main())
