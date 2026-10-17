"""在**文件层面**独立核对组合包：直接读每个 patch 的 TOC，不看转换时的内存对象。

为什么要在 JSON 报告之外再核一遍：报告是转换器自己说的，而这里读的是最终交付的字节。
两者对不上就说明"报告"和"产物"之间有断点——那是最难查的一类问题。

TOC 布局（`CanonicalPatchWriter`）：72 字节头部前缀 + 类型表（32 字节/项）+ 条目（80 字节/项）。
头部 +0 = magic，+4 = 类型数，+8 = 条目数。

⚠️ 条目里**先 file_id 再 type_id**（`WriteEntry`：+0 FileId、+8 TypeId），与"键 = (type, id)"
的直觉顺序相反——按直觉读会把 file_id 当成类型，于是每个条目都"类型未知"。
"""

import os
import struct
import sys

sys.stdout.reconfigure(encoding="utf-8")

UNIT_TYPE = 0xE0A48D0BE9A7453F
MATERIAL_TYPE = 0xEAC0B497876ADEDF
TEXTURE_TYPE = 0xCD4238C6A0C69E32
TYPE_NAMES = {UNIT_TYPE: "unit", MATERIAL_TYPE: "material", TEXTURE_TYPE: "texture"}

HEADER_PREFIX = 72
TYPE_SIZE = 32
ENTRY_SIZE = 80

# 默认组合：patch 序号 → 期望内容（类型 → 个数）。None 表示"不检查个数"。
EXPECTED = {
    0: {"unit": 7, "material": 0, "texture": 0},
    1: {"unit": 0, "material": 9, "texture": 7},
    2: {"unit": 0, "material": 15, "texture": 18},
    3: {"unit": 0, "material": 0, "texture": 2},
    4: {"unit": 0, "material": 0, "texture": 2},
}


def read_toc(path):
    data = open(path, "rb").read()
    magic, type_count, entry_count = struct.unpack_from("<III", data, 0)
    if magic != 4026531857:
        raise ValueError(f"不是 canonical patch TOC：{path} magic={magic}")
    keys = []
    table = HEADER_PREFIX + type_count * TYPE_SIZE
    for index in range(entry_count):
        offset = table + index * ENTRY_SIZE
        file_id, type_id = struct.unpack_from("<QQ", data, offset)
        keys.append((type_id, file_id))
    return keys


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else \
        r"E:\mod\Lunalice-RE2310-Separated\Lunalice-RE2310"
    problems = []
    material_ids, texture_ids = set(), set()

    for index in sorted(EXPECTED):
        path = os.path.join(root, f"9ba626afa44a3aa3.patch_{index}")
        if not os.path.isfile(path):
            problems.append(f"缺少 patch_{index}")
            continue
        keys = read_toc(path)
        counts = {}
        for type_id, file_id in keys:
            name = TYPE_NAMES.get(type_id, f"0x{type_id:016x}")
            counts[name] = counts.get(name, 0) + 1
            if name == "material":
                material_ids.add(file_id)
            elif name == "texture":
                texture_ids.add(file_id)
        expected = EXPECTED[index]
        detail = ", ".join(f"{k}={counts.get(k, 0)}" for k in ("unit", "material", "texture"))
        print(f"  patch_{index}: {len(keys):3d} 条  {detail}")
        for kind, want in expected.items():
            got = counts.get(kind, 0)
            if got != want:
                problems.append(f"patch_{index} 的 {kind} 数量是 {got}，期望 {want}")
        unknown = {name for name in counts if name not in TYPE_NAMES.values()}
        if unknown:
            problems.append(f"patch_{index} 含预期外的类型：{sorted(unknown)}")

    print(f"\n材质资产 {len(material_ids)} 个、贴图资产 {len(texture_ids)} 个（跨 patch 去重）。")
    print("注意：C1/C2 的 `11.vtf` 不在包里——它是 A1 金属件的 matcap，")
    print("      观感在转换时已烘进底色，作为独立 patch 是无效的（见契约文档 8.1）。")
    if problems:
        print("\n❌ 核对没通过：")
        for problem in problems:
            print("   -", problem)
        return 1
    print("✅ 文件层面的核对通过：模型 patch 是纯 unit，材质 patch 的内容与报告一致。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
