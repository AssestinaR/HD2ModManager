# -*- coding: utf-8 -*-
"""反汇编 game.dll 的护甲消费者，找"三个 bank 的容器"到底是什么形状。

背景：`scripts/analyze_registry_slots.py` 里那条规则（`bank = 0 if type==0 else 1 if type==1
else 2`）是从**一个捕获到的消费者**推出来的，脚本自己写明"aliasing is a testable lead,
not a crash diagnosis"。而实机观察到的 21/22 经验边界必须有代码级解释才算结论。

已知（`notes/slot-audit-20260927.md`）：
  * 函数在 RVA 0x81f470，bank 选择在 0x8201f5；
  * 它读 Piece 的 +0x18/+0x20/+0x28/+0x48/+0x30/+0x40/+0x50（七个辅助字段），
    并在 0x81faa0..0x81fad6 处"七个全零就跳过辅助逻辑"；
  * 它"清掉三个 bank 的旧实例句柄，再按 slot/type 写新句柄"。

这个脚本按 RVA 映射到文件偏移，反汇编一段，并只把**有信息量的行**挑出来：
带 +0x18/+0x20/... 偏移的访存、与常量 3/10/0x60(96) 的比较、以及 3 或 10 的乘法/移位。
"""
import argparse
import struct
import sys

from capstone import Cs, CS_ARCH_X86, CS_MODE_64

GAME_DLL = r'D:\SteamLibrary\steamapps\common\Helldivers 2\data\game\game.dll'

INTERESTING_OFFSETS = ('0x18', '0x20', '0x28', '0x30', '0x40', '0x48', '0x50')
INTERESTING_CONSTS = ('3', '0xa', '0x60', '0x18', '0x30', '0xffff', '0x1000')


def sections(data):
    pe = struct.unpack_from('<I', data, 0x3C)[0]
    assert data[pe:pe + 4] == b'PE\0\0', 'not a PE'
    count = struct.unpack_from('<H', data, pe + 6)[0]
    opt_size = struct.unpack_from('<H', data, pe + 20)[0]
    table = pe + 24 + opt_size
    out = []
    for i in range(count):
        at = table + i * 40
        name = data[at:at + 8].rstrip(b'\0').decode('latin-1')
        vsize, vaddr, raw_size, raw_ptr = struct.unpack_from('<IIII', data, at + 8)
        out.append((name, vaddr, max(vsize, raw_size), raw_ptr))
    return out


def rva_to_offset(secs, rva):
    for _name, vaddr, size, raw_ptr in secs:
        if vaddr <= rva < vaddr + size:
            return raw_ptr + (rva - vaddr)
    raise KeyError('RVA 0x%x 不在任何 section 里' % rva)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--start', default='0x81f470')
    ap.add_argument('--length', type=lambda x: int(x, 0), default=0x0e00)
    ap.add_argument('--head', type=int, default=60, help='开头打印多少条指令')
    ap.add_argument('--image', help='capture_game_image.py 写出的**镜像布局**文件'
                                    '（RVA==文件偏移）；给了它就不用盘上那份壳')
    ap.add_argument('--full', action='store_true', help='打印区间内的每一条指令')
    ap.add_argument('--save', help='把完整清单写到这个文件')
    args = ap.parse_args()

    if args.image:
        data = open(args.image, 'rb').read()
        secs = sections(data)
        print('镜像 %s %d B（RVA==偏移）；section：%s'
              % (args.image, len(data),
                 ', '.join('%s@0x%x+0x%x' % (s[0] or '<unnamed>', s[1], s[2]) for s in secs)))
        start = int(args.start, 0)
        off = start
        if start >= len(data):
            raise SystemExit('RVA 0x%x 超出镜像长度 0x%x' % (start, len(data)))
    else:
        data = open(GAME_DLL, 'rb').read()
        secs = sections(data)
        print('game.dll %d B；section：%s'
              % (len(data), ', '.join('%s@0x%x' % (s[0] or '<unnamed>', s[1]) for s in secs)))
        if len(data) < max(s[1] + s[2] for s in secs):
            print('  ⚠ 盘上文件比 section 表要求的还小：这份是壳，RVA 不可信。'
                  '请用 capture_game_image.py --output 抓镜像后加 --image。')
        start = int(args.start, 0)
        off = rva_to_offset(secs, start)

    code = data[off:off + args.length]
    md = Cs(CS_ARCH_X86, CS_MODE_64)
    md.detail = True
    insns = list(md.disasm(code, start))
    summary = ('从 RVA 0x%x（文件偏移 0x%x）解出 %d 条指令，覆盖到 0x%x'
               % (start, off, len(insns), start + sum(i.size for i in insns)))
    print(summary)

    def fmt(insn):
        return '  %08x: %-32s %s %s' % (insn.address, insn.bytes.hex(),
                                        insn.mnemonic, insn.op_str)

    head = [fmt(i) for i in insns[:args.head]]
    print('\n--- 开头 %d 条 ---' % args.head)
    print('\n'.join(head))

    if args.full:
        print('\n--- 全部指令 ---')
        for insn in insns:
            print(fmt(insn))

    print('\n--- 有信息量的行（辅助字段偏移 / 常量 3、10、0x60 / 乘 3 或 10）---')
    interesting = []
    for insn in insns:
        text = insn.op_str
        hit = []
        for pattern in INTERESTING_OFFSETS:
            if ('+ ' + pattern + ']') in text:
                hit.append('aux+' + pattern)
        if insn.mnemonic == 'cmp' and any((' ' + c) in text or text.endswith(c) for c in INTERESTING_CONSTS):
            hit.append('cmp')
        if insn.mnemonic in ('imul', 'shl', 'lea') and any(
                (', ' + c) in text for c in ('3', '0xa', '0x60')):
            hit.append('index')
        if hit:
            line = ('  %08x: %-32s %s %s    <- %s'
                    % (insn.address, insn.bytes.hex(), insn.mnemonic, insn.op_str, ','.join(hit)))
            print(line)
            interesting.append(line)

    if args.save:
        with open(args.save, 'w', encoding='utf-8') as fh:
            fh.write(summary + '\n\n--- 开头 %d 条 ---\n' % args.head
                     + '\n'.join(head) + '\n\n--- 有信息量的行 ---\n'
                     + '\n'.join(interesting) + '\n')
            if args.full:
                fh.write('\n--- 全部指令 ---\n')
                for insn in insns:
                    fh.write(fmt(insn) + '\n')
        print('\n清单已写到 %s' % args.save)
    return 0


if __name__ == '__main__':
    sys.exit(main())
