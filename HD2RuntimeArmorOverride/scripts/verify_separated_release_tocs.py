"""在**文件层面**独立核对组合包：直接读每个 patch 的 TOC，不看转换时的 JSON 报告。

报告是转换器自己说的，这里读的是最终交付的字节。两者对不上就说明"报告"和"产物"之间
有断点——那是最难查的一类问题。

TOC 布局（`CanonicalPatchWriter`）：72 字节头部前缀 + 类型表（32 字节/项）+ 条目（80 字节/项）。
头部 +0 = magic，+4 = 类型数，+8 = 条目数。

⚠️ 条目里**先 file_id 再 type_id**（`WriteEntry`：+0 FileId、+8 TypeId），与"键 = (type, id)"
的直觉顺序相反——按直觉读会把 file_id 当成类型，于是每个条目都"类型未知"。
而条目总数仍然是对的，所以只看总数不会发现。
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

# 选项目录名 → 期望内容。host 单独检查。
EXPECTED_OPTIONS = {
    "Lunalice基础材质A1主体": {"material": 9, "texture": 12},
    "Lunalice基础材质B1服装色白": {"material": 15, "texture": 18},
    "Lunalice基础材质B2服装色黑": {"material": 15, "texture": 18},
    "Lunalice基础材质C1金属装饰品色金": {"material": 0, "texture": 1},
    "Lunalice基础材质C2金属装饰品色银": {"material": 0, "texture": 1},
    "Lunalice基础材质D1发色白": {"material": 0, "texture": 2},
    "Lunalice基础材质D2发色紫": {"material": 0, "texture": 2},
    "Lunalice基础材质D3发色浅灰": {"material": 0, "texture": 2},
    "Lunalice基础材质D4发色黄": {"material": 0, "texture": 2},
    "Lunalice基础材质E1瞳色紫1": {"material": 0, "texture": 2},
    "Lunalice基础材质E2瞳色红": {"material": 0, "texture": 2},
    "Lunalice基础材质E3瞳色紫2": {"material": 0, "texture": 2},
}


def read_keys(path):
    data = open(path, "rb").read()
    magic, type_count, entry_count = struct.unpack_from("<III", data, 0)
    if magic != 4026531857:
        raise ValueError(f"不是 canonical patch TOC：{path} magic={magic}")
    table = HEADER_PREFIX + type_count * TYPE_SIZE
    return [
        struct.unpack_from("<QQ", data, table + index * ENTRY_SIZE)[::-1]
        for index in range(entry_count)
    ]


def only_patch(directory):
    names = [n for n in os.listdir(directory) if ".patch_" in n and ".gpu_resources" not in n and ".stream" not in n]
    if len(names) != 1:
        raise SystemExit(f"{directory} 里的 patch 本体不是恰好一个：{names}")
    return os.path.join(directory, names[0])


def counts(keys):
    result = {}
    for type_id, _ in keys:
        name = TYPE_NAMES.get(type_id, f"0x{type_id:016x}")
        result[name] = result.get(name, 0) + 1
    return result


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else r"E:\mod\Lunalice-RE2310-Separated\Lunalice-RE2310"
    problems = []

    host = counts(read_keys(only_patch(root)))
    print(f"  host: unit={host.get('unit', 0)} material={host.get('material', 0)} texture={host.get('texture', 0)}")
    # 同键替换 RE-2310：23 个 Unit 键（11 替换 + 12 隐藏占位），零材质零贴图。
    if host != {"unit": 23}:
        problems.append(f"host 不是 RE-2310 的 23 个 Unit 键：{host}")

    material_ids, texture_ids = set(), set()
    for name, expected in EXPECTED_OPTIONS.items():
        directory = os.path.join(root, name)
        if not os.path.isdir(directory):
            problems.append(f"缺少选项目录：{name}")
            continue
        keys = read_keys(only_patch(directory))
        got = counts(keys)
        for type_id, file_id in keys:
            if type_id == MATERIAL_TYPE:
                material_ids.add(file_id)
            elif type_id == TEXTURE_TYPE:
                texture_ids.add(file_id)
        detail = ", ".join(f"{k}={got.get(k, 0)}" for k in ("unit", "material", "texture"))
        print(f"  {name}: {len(keys):3d} 条  {detail}")
        if got.get("unit"):
            problems.append(f"{name} 里出现了 Unit —— 选项不该带 Unit")
        for kind, want in expected.items():
            if got.get(kind, 0) != want:
                problems.append(f"{name} 的 {kind} 数量是 {got.get(kind, 0)}，期望 {want}")
        unknown = {k for k in got if k not in TYPE_NAMES.values()}
        if unknown:
            problems.append(f"{name} 含预期外的类型：{sorted(unknown)}")

    print(f"\n材质资产 {len(material_ids)} 个、贴图资产 {len(texture_ids)} 个（跨选项去重）。")
    if problems:
        print("\n❌ 核对没通过：")
        for problem in problems:
            print("   -", problem)
        return 1
    print("✅ 文件层面的核对通过：host 是纯 unit，每个选项的内容与报告一致。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
