"""解析物理容器（`HD2SRG01` / v2 头），报告骨数、链数与锚点分组。

用途（2026-11-12）：确认"每个 unit 需要哪些链"时的权威清单 —— 容器里的 57 根骨
按 `parentName` 归并成链；锚点（head / chest / hips / spine2 / knee…）决定
"哪些 unit 应该带哪些骨"。

字段布局（与 `HD2PhysBoneCompat/src/physbone_compat.lua` 的 `parse_rig` 一字不差）：
    magic     : 0..7   "HD2SRG01"
    version   : +8     u32（本实现只认 2）
    count     : +12    u32 骨数
    nameOffset: +16    u32 名字表偏移（绝对）
    nameSize  : +20    u32 名字表大小
    recordSize: +24    u32 每根骨的记录大小
    骨记录从 +32 开始，每条 recordSize 字节：
        +0  index(u32)  源骨骼下标（对应骨名里的 source_extra_<n>）
        +4  parent(i32)
        +8  nameLocal(u32)       → 名字表内偏移
        +12 parentNameLocal(u32) → 名字表内偏移

用法：
    python tools/physbone-audit/parse-rig.py "E:\\mod\\Lunalice_Bill\\sectional\\physbone-rig.bin"
    python tools/physbone-audit/parse-rig.py <文件> --dump-names
"""

from __future__ import annotations

import argparse
import struct
from collections import Counter
from pathlib import Path

MAGIC = b"HD2SRG01"
HEADER = 32          # 与 compat 的 RIG_HEADER_SIZE 一致
F_INDEX, F_PARENT, F_NAME, F_PARENTNAME = 0, 4, 8, 12


def read_cstr(buf: bytes, offset: int) -> str:
    end = buf.find(b"\x00", offset)
    if end < 0:
        return ""
    return buf[offset:end].decode("utf-8", "replace")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("file", help="physbone-rig.bin（或任何含该容器的文件）")
    parser.add_argument("--dump-names", action="store_true", help="逐根打印骨名与父骨名")
    args = parser.parse_args()

    path = Path(args.file)
    buf = path.read_bytes()
    start = buf.find(MAGIC)
    if start < 0:
        print(f"没找到 {MAGIC.decode()}：{path}")
        return 2
    if start:
        print(f"（容器不在文件开头，偏移 +{start}）")
    buf = buf[start:]

    version, count, name_off, name_size, rec_size = struct.unpack_from("<IIIII", buf, 8)
    print(f"文件      : {path.name}")
    print(f"版本/骨数 : v{version} / {count} 根")
    print(f"名字表    : offset={name_off} size={name_size}（{len(buf)} 字节容器）")
    print(f"记录大小  : {rec_size} 字节/根")
    if rec_size < 148 or HEADER + count * rec_size > len(buf):
        print("记录大小或长度不合理，可能不是完整容器")
        return 2

    names, parents, source_index = [], [], []
    for index in range(count):
        at = HEADER + index * rec_size
        source_index.append(struct.unpack_from("<I", buf, at + F_INDEX)[0])
        names.append(read_cstr(buf, name_off + struct.unpack_from("<I", buf, at + F_NAME)[0]))
        parents.append(read_cstr(buf, name_off + struct.unpack_from("<I", buf, at + F_PARENTNAME)[0]))

    own = set(names)
    roots = [p for p in parents if p not in own]      # 父骨不是物理骨 ⇒ 这是链的锚点
    print(f"\n链数（锚点不在物理骨集合里）: {len(roots)}")
    print(f"锚点分布                    : {dict(Counter(roots).most_common())}")
    print(f"源骨骼下标范围              : {min(source_index)}..{max(source_index)}")
    print(f"名字里带命名空间前缀的      : {sum(1 for n in names if n.startswith('j') and '/' in n)}/{count}")

    if args.dump_names:
        print("\n骨名 → 锚点（链根）／父物理骨：")
        for index, (name, parent) in enumerate(zip(names, parents), start=1):
            mark = "锚点" if parent not in own else "  链内"
            print(f"  {index:3d}. [{mark}] {name:44s} ← {parent}")
    else:
        print("前 3 根骨:", names[:3])
        print("（逐根清单加 --dump-names）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
