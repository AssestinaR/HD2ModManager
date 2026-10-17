# -*- coding: utf-8 -*-
"""生成并验证"把护甲 bank 从 3 扩到 4"的精确补丁计划。

为什么先出计划再动手：补丁字节必须**逐字节可反汇编验证**，否则写进游戏就是崩溃。
这个脚本不碰游戏进程，只读内存镜像，产出：
  * 每个补丁点的原字节
  * 补丁后的新字节
  * 用 capstone 反汇编新字节，证明它是预期指令
  * trampoline 代码洞地址

设计（4 bank 版本，最小补丁面）：

1. **bank C 指针重定位** —— `0x877c70: lea r8, [r13 + 0x546b9c]`（7 字节）。
   只改其中的 32 位位移（`0x877c73` 起 4 字节），指令长度不变：
       lea r8, [r13 + disp32]      ; disp32 = ourBuffer - r13
   这样 bank C 落在我们自己 VirtualAllocEx 的缓冲里，后面可以连续放 C/D/E/F...

2. **分派 trampoline** —— `0x82024e: mov rcx, [rsp+0x68]`（5 字节）换成
   `jmp cave`，洞里做 `bankC + (type-2)*40` 再写。
   type 从 `[r12+0xc]` 重新读（r12 = Piece 指针，callee-saved，安全），
   因为 `mov rcx,[rsp+0x68]` 会把 ecx 里的 type 覆盖掉。

type 0 / type 1 两条路径仍然 `jmp 0x820253`（共用的写入指令），不受影响。
"""
import argparse
import json
import struct
import sys

from capstone import Cs, CS_ARCH_X86, CS_MODE_64

IMAGE = r'E:\tmp\hd2-mem\game.image'

# 补丁点 1：bank C 的 lea 位移
LEA_RVA = 0x877c70          # lea r8, [r13 + 0x546b9c]
LEA_DISP_RVA = LEA_RVA + 3  # 位移在指令第 4 字节起
LEA_DISP_LEN = 4
LEA_ORIG_DISP = 0x546b9c

# 补丁点 2：分派 trampoline
DISPATCH_RVA = 0x82024e     # mov rcx, [rsp+0x68]
DISPATCH_LEN = 5
RESUME_RVA = 0x820256       # trampoline 结束后跳回这里
SHARED_STORE_RVA = 0x820253  # type0/type1 共用的写入指令

# trampoline 里要读的 Piece 指针寄存器（callee-saved，跨调用安全）
PIECE_REG = 'r12'
TYPE_OFFSET = 0xc

# 从镜像里找出来的最大代码洞（见 feasibility 笔记 §6.1）
CAVE_RVA = 0x1af7673
CAVE_LEN = 93


def build_trampoline(cave_rva):
    """构造 trampoline 的机器码，并用 capstone 反汇编自检。"""
    code = bytearray()
    # mov rcx, [rsp+0x68]        ; rcx = bankC
    code += bytes.fromhex('488b4c2468')
    # mov edx, [r12+0xc]         ; edx = piece_type
    code += bytes.fromhex('418b54240c')
    # sub edx, 2                 ; 0 基索引（type>=2 才走到这条路径）
    code += bytes.fromhex('83ea02')
    # cmp edx, 5                 ; 夹到 0..5，避免 type 异常时写出界
    code += bytes.fromhex('83fa05')
    # jbe +4                     ; 没超过就跳过修正
    code += bytes.fromhex('7604')
    # mov edx, 5
    code += bytes.fromhex('ba05000000')
    # imul edx, edx, 40          ; 每个 bank 40 字节
    code += bytes.fromhex('6bd228')
    # add rcx, rdx               ; rcx = 目标 bank
    code += bytes.fromhex('4801d1')
    # mov [rcx+rax*4], edi       ; bank[slot] = handle
    code += bytes.fromhex('893c81')
    # jmp RESUME_RVA             ; 回到主流程
    rel = RESUME_RVA - (cave_rva + len(code) + 5)
    code += b'\xe9' + struct.pack('<i', rel)
    return bytes(code)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--image', default=IMAGE)
    ap.add_argument('--output', default=r'E:\tmp\hd2-mem\bank4-patch-plan.json')
    args = ap.parse_args()

    data = open(args.image, 'rb').read()
    md = Cs(CS_ARCH_X86, CS_MODE_64)

    print('=== 补丁点 1：bank C 指针位移 ===')
    orig = data[LEA_RVA:LEA_RVA + 7]
    print('  原字节 %s' % orig.hex())
    for insn in md.disasm(orig, LEA_RVA):
        print('    %08x: %s %s' % (insn.address, insn.mnemonic, insn.op_str))
    disp_now = struct.unpack_from('<i', data, LEA_DISP_RVA)[0]
    print('  当前位移 0x%x（== 0x546b9c：%s）'
          % (disp_now & 0xFFFFFFFF, disp_now == LEA_ORIG_DISP))
    print('  补丁方式：只覆盖 0x%x 起 4 字节，写入 disp32 = ourBuffer - r13' % LEA_DISP_RVA)
    print('  指令长度不变，后面 0x%x 的 add r8, r12 保持原样' % (LEA_RVA + 7))

    print('\n=== 补丁点 2：分派 trampoline ===')
    orig2 = data[DISPATCH_RVA:DISPATCH_RVA + DISPATCH_LEN]
    print('  原字节 %s' % orig2.hex())
    for insn in md.disasm(orig2, DISPATCH_RVA):
        print('    %08x: %s %s' % (insn.address, insn.mnemonic, insn.op_str))
    cave = data[CAVE_RVA:CAVE_RVA + CAVE_LEN]
    print('  代码洞 0x%x：%d 字节，全 0xCC = %s'
          % (CAVE_RVA, CAVE_LEN, all(b == 0xCC for b in cave)))

    tramp = build_trampoline(CAVE_RVA)
    print('\n  trampoline %d 字节（洞容量 %d）：' % (len(tramp), CAVE_LEN))
    ok = True
    for insn in md.disasm(tramp, CAVE_RVA):
        print('    %08x: %-24s %s %s' % (insn.address, insn.bytes.hex(),
                                         insn.mnemonic, insn.op_str))
    consumed = sum(i.size for i in md.disasm(tramp, CAVE_RVA))
    if consumed != len(tramp):
        ok = False
        print('  ✗ 反汇编只消费了 %d/%d 字节，尾部无法解码' % (consumed, len(tramp)))
    if len(tramp) > CAVE_LEN:
        ok = False
        print('  ✗ trampoline 超出洞容量')
    jump = b'\xe9' + struct.pack('<i', CAVE_RVA - (DISPATCH_RVA + 5))
    print('  补丁点 2 新字节 %s = jmp 0x%x' % (jump.hex(), CAVE_RVA))
    for insn in md.disasm(jump, DISPATCH_RVA):
        print('    %08x: %s %s' % (insn.address, insn.mnemonic, insn.op_str))

    plan = {
        'schema': 1,
        'target': 'armor banks 3 -> 4',
        'cave': {'rva': CAVE_RVA, 'length': CAVE_LEN},
        'patches': [
            {
                'name': 'bankC-pointer',
                'rva': LEA_DISP_RVA,
                'original': data[LEA_DISP_RVA:LEA_DISP_RVA + LEA_DISP_LEN].hex(),
                'length': LEA_DISP_LEN,
                'kind': 'runtime-computed',
                'note': 'disp32 = ourBuffer - *(game.dll+0x3326d20)',
            },
            {
                'name': 'dispatch-jmp',
                'rva': DISPATCH_RVA,
                'original': orig2.hex(),
                'patched': jump.hex(),
                'length': DISPATCH_LEN,
            },
            {
                'name': 'trampoline',
                'rva': CAVE_RVA,
                'original': 'cc' * len(tramp),
                'patched': tramp.hex(),
                'length': len(tramp),
            },
        ],
        'verified': ok,
    }
    with open(args.output, 'w', encoding='utf-8') as fh:
        json.dump(plan, fh, ensure_ascii=False, indent=2)
    print('\n验证结果：%s' % ('通过' if ok else '不通过'))
    print('计划已写入 %s' % args.output)
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
