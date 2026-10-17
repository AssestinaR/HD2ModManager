# -*- coding: utf-8 -*-
"""从内存镜像里按 RVA 读原始数据（qword / dword / 字节），可选解引用一层。

用途：`game.dll` 里像 `add r14, qword ptr [rip + 0x2b90882]` 这种指令说明
"护甲实例数组的基址存在一个全局指针里"。镜像本身就是**运行中的活内存**，
所以那个全局里已经是真实堆地址 —— 直接读出来就能定位结构，不必先进游戏写探针。

只读：纯离线读一个已落盘的文件。
"""
import argparse
import struct
import sys


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--image', required=True)
    ap.add_argument('--rva', required=True)
    ap.add_argument('--count', type=int, default=8)
    ap.add_argument('--width', type=int, default=8, choices=[1, 2, 4, 8])
    ap.add_argument('--deref', type=int, default=0,
                    help='把读到的第一个 qword 当成 RVA/偏移再读（镜像布局下偏移==RVA）')
    ap.add_argument('--deref-count', type=int, default=0,
                    help='解引用后额外再读多少个宽度单位')
    args = ap.parse_args()

    data = open(args.image, 'rb').read()
    rva = int(args.rva, 0)
    fmt = {1: '<B', 2: '<H', 4: '<I', 8: '<Q'}[args.width]
    step = args.width

    print('镜像 %d B；从 RVA 0x%x 读 %d 个 %d 字节' % (len(data), rva, args.count, step))
    for i in range(args.count):
        at = rva + i * step
        if at + step > len(data):
            print('  0x%08x  越界' % at)
            break
        value = struct.unpack_from(fmt, data, at)[0]
        extra = ''
        if step == 8 and 0x10000 <= value < len(data):
            probe = struct.unpack_from('<I', data, value)[0]
            extra = '   -> [0x%x] = 0x%08x' % (value, probe)
        print('  0x%08x = 0x%0*x%s' % (at, step * 2, value, extra))

    if args.deref:
        pointer = struct.unpack_from('<Q', data, rva)[0]
        print('\n解引用 0x%x：' % pointer)
        n = args.deref_count or 8
        for i in range(n):
            at = pointer + i * 4
            if at + 4 > len(data):
                print('  0x%x  越界（不在镜像里，可能是堆）' % at)
                break
            print('  0x%x = 0x%08x' % (at, struct.unpack_from('<I', data, at)[0]))
    return 0


if __name__ == '__main__':
    sys.exit(main())
