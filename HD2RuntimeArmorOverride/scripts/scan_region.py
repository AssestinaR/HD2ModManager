# -*- coding: utf-8 -*-
"""在镜像的一段 RVA 上反汇编，并按正则挑出关心的行。

用途：护甲消费者 `0x81f470` 的三个 bank 缓冲区是**调用者**的栈局部变量，
所以"谁写这些槽、谁读这些槽"必须到调用者函数里去找。这个脚本把一段
反汇编列出来，并用 `--match` 只保留命中行（前后带 `--around` 条上下文），
避免把几千条无关指令倒进上下文。

镜像布局 RVA==文件偏移（见 capture_game_image.py）。
"""
import argparse
import re
import sys

from capstone import Cs, CS_ARCH_X86, CS_MODE_64


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--image', required=True)
    ap.add_argument('--start', required=True)
    ap.add_argument('--length', default='0x800')
    ap.add_argument('--match', help='正则；不给就全部打印')
    ap.add_argument('--around', type=int, default=4)
    ap.add_argument('--max-hits', type=int, default=60)
    ap.add_argument('--save', help='把完整反汇编写到这个文件')
    args = ap.parse_args()

    data = open(args.image, 'rb').read()
    start = int(args.start, 0)
    length = int(args.length, 0)
    code = data[start:start + length]
    md = Cs(CS_ARCH_X86, CS_MODE_64)
    insns = list(md.disasm(code, start))
    print('0x%x+0x%x 解出 %d 条指令，覆盖到 0x%x'
          % (start, length, len(insns), start + sum(i.size for i in insns)))

    lines = ['%08x: %-28s %s %s' % (i.address, i.bytes.hex(), i.mnemonic, i.op_str)
             for i in insns]
    if args.save:
        with open(args.save, 'w', encoding='utf-8') as fh:
            fh.write('\n'.join(lines) + '\n')
        print('完整清单 %s' % args.save)

    if not args.match:
        print('\n'.join(lines))
        return 0

    pattern = re.compile(args.match)
    hits = [n for n, line in enumerate(lines) if pattern.search(line)]
    print('命中 %d 行（正则 %s）' % (len(hits), args.match))
    shown = set()
    for n in hits[:args.max_hits]:
        lo = max(0, n - args.around)
        hi = min(len(lines), n + args.around + 1)
        for k in range(lo, hi):
            if k in shown:
                continue
            shown.add(k)
            mark = ' <<<' if k == n else ''
            print('  ' + lines[k] + mark)
        print('  ---')
    if len(hits) > args.max_hits:
        print('… 另有 %d 处命中未打印' % (len(hits) - args.max_hits))
    return 0


if __name__ == '__main__':
    sys.exit(main())
