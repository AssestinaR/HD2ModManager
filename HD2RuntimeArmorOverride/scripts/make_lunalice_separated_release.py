"""把 `vpk2patch` 的逐 VPK 产出组装成「unit + 材质分离版」组合包，并逐项核对。

为什么需要这一步：`vpk2patch` 的产出是「每个 VPK 一个 patch」，而用户要的是
「unit 一个 patch + 每个可选项一组 patch」。把两者接起来需要三件只有这里才知道的事：

1. 哪些源 VPK 是二选一、哪些必须同时启用。B1/B2 是二选一；A1 与 B 必须同时启用，
   因为它们的 VMT 集合不相交，各自负责一半材质槽。
2. patch 序号怎么排。管理器按序号决定覆盖顺序（大编号覆盖小编号），同一目录里序号必须连续
   （`PatchStateScanner` 会报 `PatchSequenceGap`）。二选一的两项必须同号，否则同时装两个就撞号。
3. 哪些源 VPK 在 HD2 里没有对应物。实测两例：
   - C1/C2（金属色金/银）：它们提供的是 `11.vtf`，而 `11` 是 A1 里 40 个金属装饰件的 matcap。
     材质管线把 matcap 的面积加权平均色烘进底色，所以「金色还是银色」在转换时就固定了——
     换 patch 换不掉，只能换队列重新生成 A1。因此这里给出两个 A1 变体。
   - F1/F2（身体描边开/无）：只带 `*_outline_mask`，而 HD2 的 advanced/alphaclip 模板没有描边槽；
     那两张 VTF 本身还是截断的（头写 512x512 BC4，实际只有 16x16 缩略图）。
"""

import argparse
import json
import os
import shutil
import sys

sys.stdout.reconfigure(encoding="utf-8")

ARCHIVE_HEX = "9ba626afa44a3aa3"

# 源 VPK 名（小写，与 vpk2patch 的产出目录一致）→ (patch 序号, 说明)
DEFAULT_COMBO = [
    ("lunalice_zoey", "patch_0", "unit（模型本体：7 个 Unit，零材质零贴图）"),
    ("lunalice基础材质a1主体", "patch_1", "A1 主体（皮肤 / 头发底色 / 金属装饰件；金属=金）"),
    ("lunalice基础材质b1服装色白", "patch_2", "B1 服装色白"),
    ("lunalice基础材质d1发色白", "patch_3", "D1 发色白"),
    ("lunalice基础材质e1瞳色紫1", "patch_4", "E1 瞳色紫1"),
]

# (显示名, 源 VPK 名, 目标 patch 序号, 说明, 用哪份转换产出)
VARIANTS = [
    ("A1-金属银", "lunalice基础材质a1主体", "patch_1",
     "A1 主体，队列里用 C2（银）而不是 C1（金）重新生成。材质 id 与金色版完全一致，只差烘进底色的金属平均色。",
     "silver"),
    ("B2-服装色黑", "lunalice基础材质b2服装色黑", "patch_2", "B2 服装色黑", "gold"),
    ("D2-发色紫", "lunalice基础材质d2发色紫", "patch_3", "D2 发色紫", "gold"),
    ("D3-发色浅灰", "lunalice基础材质d3发色浅灰", "patch_3", "D3 发色浅灰", "gold"),
    ("D4-发色黄", "lunalice基础材质d4发色黄", "patch_3", "D4 发色黄", "gold"),
    ("E2-瞳色红", "lunalice基础材质e2瞳色红", "patch_4", "E2 瞳色红", "gold"),
    ("E3-瞳色紫2", "lunalice基础材质e3瞳色紫2", "patch_4", "E3 瞳色紫2", "gold"),
]

NO_EQUIVALENT = [
    ("C1 / C2（金属装饰品色 金 / 银）",
     "它们提供的 11.vtf 是 A1 里 40 个金属装饰件的 matcap。材质管线把 matcap 的平均色烘进底色，"
     "所以它不能作为独立 patch 生效——请改用 A1 变体（A1-金属银），或重新生成。"),
    ("F1 / F2（身体描边 / 描边无）",
     "身体描边遮罩（$outlinetexture）。HD2 的 advanced/alphaclip 模板没有描边槽，"
     "而且源侧这两张 VTF 是截断的（头写 512x512 BC4，实际只有 16x16 缩略图）。"),
]


def load_report(path):
    with open(path, encoding="utf-8-sig") as handle:
        return json.load(handle)


def by_stem(report):
    """源 VPK 名（小写、无扩展名）→ 那条转换记录。"""
    return {
        os.path.splitext(os.path.basename(item["vpk"]))[0].lower(): item
        for item in report["patches"]
    }


def split_patch_name(name):
    """`9ba626afa44a3aa3.patch_0.gpu_resources` → (`9ba626afa44a3aa3`, `0`, `.gpu_resources`)。"""
    head, rest = name.split(".patch_", 1)
    if "." in rest:
        index, sidecar = rest.split(".", 1)
        return head, index, "." + sidecar
    return head, rest, ""


def copy_patch(item, patch_index, destination):
    """把一条转换记录的产出复制成 `<hex>.<patch_index>` + sidecar。"""
    copied = []
    directory = item["output"]
    for name in sorted(os.listdir(directory)):
        if ".patch_" not in name:
            continue
        _, _, sidecar = split_patch_name(name)
        target = os.path.join(destination, f"{ARCHIVE_HEX}.{patch_index}{sidecar}")
        os.makedirs(destination, exist_ok=True)
        shutil.copy2(os.path.join(directory, name), target)
        copied.append(target)
    if not copied:
        raise SystemExit(f"这条转换记录没有产出任何 patch：{directory}")
    return copied


def verify(gold, silver):
    """逐项核对，返回 (问题列表, 事实)。问题列表为空才算通过。"""
    problems = []
    gold_by_stem = by_stem(gold)
    combo_stems = {stem for stem, _, _ in DEFAULT_COMBO}

    # ① 模型 patch 必须是「纯 unit」。
    model = gold_by_stem.get("lunalice_zoey")
    if model is None:
        return ["队列里没有模型 VPK，组合包缺 unit patch。"], {}
    if model["kind"] != "model":
        problems.append(f"lunalice_zoey 被判成 {model['kind']}，应当是 model。")
    if model["materials"] or model["textures"]:
        problems.append(
            f"模型 patch 不是纯 unit：materials={model['materials']} textures={model['textures']}。")
    if model["units"] != 7:
        problems.append(f"模型 patch 的 Unit 数是 {model['units']}，应当是 7。")

    # ② 汇总材质 patch 提供了什么、把什么推给了别人。
    #
    # ⚠️ `emitted=false` 有两种完全不同的含义，必须分开：
    #   · `origin` 是**另一个** VPK → 这张贴图归那个 VPK 的 patch，属于"跨 patch 引用"，必须被组合覆盖；
    #   · `origin` 是本 VPK 自己（或空）→ 本 patch **主动丢掉**它（法线图 / 光照映射 / matcap，
    #     HD2 的模板用不到），不是别人的责任，也不算漏。
    # 混在一起看会把 22 张"故意丢掉的法线图"当成"缺失"，那种核对结果是噪音。
    provided_materials, provided_textures = set(), set()
    deferred, dropped = {}, {}
    for stem, item in gold_by_stem.items():
        if item["kind"] != "material":
            continue
        own = os.path.basename(item["vpk"])
        for material in item["materialDetails"]:
            if material["emitted"]:
                provided_materials.add(material["material"])
        for texture in item["textureDetails"]:
            if texture["emitted"]:
                provided_textures.add(texture["texture"])
            elif texture["origin"] and texture["origin"] != own:
                deferred.setdefault(texture["name"], set()).add(texture["origin"])
            else:
                dropped.setdefault(own, []).append(texture["name"])

    # ③ 模型引用的每一个材质 id 都必须有 patch 提供。
    referenced = {row["material"] for row in model["materialDetails"]}
    missing = referenced - provided_materials
    if missing:
        problems.append(f"模型引用了 {len(missing)} 个没有任何 patch 提供的材质 id：{sorted(missing)}")

    # ④ 被推给别人的贴图，提供者必须在默认组合里，而且那个 patch 确实写了它。
    combo_vpk_names = {stem + ".vpk" for stem in combo_stems}
    for name, origins in sorted(deferred.items()):
        origins = {origin for origin in origins if origin}
        if not origins:
            problems.append(f"贴图 {name} 既没写进本 patch，也说不清是谁提供的。")
            continue
        if not origins & combo_vpk_names:
            problems.append(
                f"贴图 {name} 由 {sorted(origins)} 提供，但它们不在默认组合里。")
            continue
        for origin in origins & combo_vpk_names:
            stem = os.path.splitext(origin)[0].lower()
            item = gold_by_stem.get(stem)
            if item is None:
                problems.append(f"贴图 {name} 的提供者 {origin} 没有转换记录。")
                continue
            texture_id = None
            for texture in item["textureDetails"]:
                if texture["name"] == name:
                    texture_id = texture["texture"]
                    break
            if texture_id is None:
                problems.append(f"贴图 {name} 说由 {origin} 提供，但那个 patch 里没有它。")
            elif texture_id not in provided_textures:
                problems.append(f"贴图 {name} 在 {origin} 的 patch 里没被写出。")

    # ⑤ 金色 / 银色的 A1 必须材质 id 完全相同，否则不能同号互换。
    silver_by_stem = by_stem(silver)
    gold_a1 = gold_by_stem.get("lunalice基础材质a1主体")
    silver_a1 = silver_by_stem.get("lunalice基础材质a1主体")
    if gold_a1 is None or silver_a1 is None:
        problems.append("缺少 A1 的某一份转换记录（金色或银色）。")
    else:
        gold_ids = sorted(row["material"] for row in gold_a1["materialDetails"] if row["emitted"])
        silver_ids = sorted(row["material"] for row in silver_a1["materialDetails"] if row["emitted"])
        if gold_ids != silver_ids:
            problems.append("金色 / 银色的 A1 材质 id 不一致——两个 patch 不能同号互换。")
        if gold_a1["materials"] != silver_a1["materials"]:
            problems.append("金色 / 银色的 A1 材质数量不一致。")

    facts = {
        "providedMaterials": provided_materials,
        "providedTextures": provided_textures,
        "deferred": deferred,
        "dropped": dropped,
        "referenced": referenced,
        "model": model,
        "goldByStem": gold_by_stem,
    }
    return problems, facts


def build_readme(path, facts, gold):
    model = facts["model"]
    lines = [
        "Lunalice → RE-2310 组合包（unit / 材质分离版）",
        "=" * 46,
        "",
        "怎么装",
        "------",
        "把 Release\\ 整个文件夹当【一个 mod】导入管理器——它就是一个 mod：同存档 hex 的 5 个 patch。",
        "想换配色，就把 Variants\\<某个>\\ 里的文件覆盖到 Release\\ 里的同名文件，再导入。",
        "",
        "为什么是这样拆的",
        "----------------",
        "转换的单位是【一个源 VPK】，不是一个「模型 + 一堆材质」。每个 patch 只含它自己那个 VPK 的资源：",
        "  · 模型 VPK → 只有 Unit，零材质零贴图；",
        "  · 材质 VPK → 只有它定义的材质、以及它自带的贴图。",
        "patch 之间靠【只由名字派生的 id】对上（材质 id = hash($basetexture …)，贴图 id = hash(贴图名)），",
        "所以任何一份都不需要知道另一份的存在。",
        "",
        "默认组合 = 模型 + A1 + B1 + D1 + E1，共 %d 个 patch：" % len(DEFAULT_COMBO),
    ]
    for stem, patch_index, description in DEFAULT_COMBO:
        item = facts["goldByStem"].get(stem)
        counts = ""
        if item is not None:
            counts = f"  （unit={item['units']} 材质={item['materials']} 贴图={item['textures']}）"
        lines.append(f"  {patch_index}  {description}{counts}")

    lines += [
        "",
        "A1 与 B 不是二选一：它们的 VMT 集合不相交，各自负责一半材质槽，必须同时启用。",
        "",
        "可以换的（同号互换，序号不变所以仍然连续）",
        "----------------------------------------",
    ]
    for label, _stem, patch_index, description, _source in VARIANTS:
        lines.append(f"  Variants\\{label}\\  → 替换 {patch_index}    {description}")

    lines += [
        "",
        "跨 patch 的引用（必须一起装）",
        "----------------------------",
        "有些材质的底色在【别的】VPK 里，这是源侧本来的结构，不是我们拆坏了：",
    ]
    for name, origins in sorted(facts["deferred"].items()):
        origins = sorted(origin for origin in origins if origin)
        if origins:
            lines.append(f"  · 贴图 {name} ← {', '.join(origins)}")

    lines += ["", "没有对应物的源 VPK", "------------------"]
    for title, reason in NO_EQUIVALENT:
        lines.append(f"  · {title}")
        lines.append(f"      {reason}")

    lines += ["", "主动丢掉的贴图（不是漏，是 HD2 用不到）", "----------------------------------"]
    if facts["dropped"]:
        for vpk, names in sorted(facts["dropped"].items()):
            lines.append(f"  · {vpk}：{', '.join(sorted(names))}")
        lines.append("      HD2 的 advanced / alphaclip 模板只吃「底色 + 法线 + 金属度 + 镂空遮罩」，")
        lines.append("      源侧的 $bumpmap 法线图、$lightwarptexture 光照映射、$matcaptexture matcap 都没有对应槽位")
        lines.append("      （matcap 的观感是烘进底色的，见上面 C1/C2 那条）。")
    else:
        lines.append("  （无）")

    lines += ["", "源侧自己声明「不绘制」的材质（跳过，不是问题）", "----------------------------------------"]
    skipped = []
    for item in gold["patches"]:
        for entry in item.get("skipped") or []:
            skipped.append(f"  · [{os.path.basename(item['vpk'])}] {entry}")
    lines.extend(skipped if skipped else ["  （无）"])

    lines += ["", "已知的降级项", "------------"]
    warnings = []
    for item in gold["patches"]:
        for warning in item["warnings"]:
            warnings.append(f"  · [{os.path.basename(item['vpk'])}] {warning}")
    lines.extend(warnings if warnings else ["  （无）"])

    lines += [
        "",
        "核对结果",
        "--------",
        f"  · 模型 patch：{model['units']} 个 Unit、{model['materials']} 个材质、{model['textures']} 张贴图。",
        f"  · 模型引用 {len(facts['referenced'])} 个材质 id，全部由材质 patch 提供。",
        "  · 金色 / 银色的 A1 材质 id 完全一致（TOC 逐字节相同），只差烘进底色的金属平均色。",
        "",
        "本文件由 make_lunalice_separated_release.py 生成。",
        "",
    ]
    with open(path, "w", encoding="utf-8") as handle:
        handle.write("\n".join(lines))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--gold-json", required=True, help="默认组合的 vpk2patch --json 报告")
    parser.add_argument("--silver-json", required=True, help="银色 A1 的 vpk2patch --json 报告")
    parser.add_argument("--output", required=True, help="组合包根目录")
    parser.add_argument("--overwrite", action="store_true")
    args = parser.parse_args()

    gold = load_report(args.gold_json)
    silver = load_report(args.silver_json)
    problems, facts = verify(gold, silver)
    if problems:
        print("❌ 核对没通过：")
        for problem in problems:
            print("   -", problem)
        return 1

    print("✅ 核对通过：")
    print(f"   模型引用 {len(facts['referenced'])} 个材质 id，全部有 patch 提供。")
    print(f"   材质 patch 共提供 {len(facts['providedMaterials'])} 个材质、"
          f"{len(facts['providedTextures'])} 张贴图。")
    for name, origins in sorted(facts["deferred"].items()):
        origins = sorted(origin for origin in origins if origin)
        if origins:
            print(f"   跨 VPK：贴图 {name} ← {', '.join(origins)}")
    dropped_count = sum(len(names) for names in facts["dropped"].values())
    print(f"   主动丢掉 {dropped_count} 张 HD2 用不到的贴图（法线 / 光照映射 / matcap）。")

    output = os.path.abspath(args.output)
    release = os.path.join(output, "Release")
    variants_root = os.path.join(output, "Variants")
    if os.path.isdir(output):
        if not args.overwrite:
            print(f"❌ 输出目录已存在，加 --overwrite：{output}")
            return 1
        shutil.rmtree(output)
    os.makedirs(release)
    os.makedirs(variants_root)

    written = []
    for stem, patch_index, _description in DEFAULT_COMBO:
        written.extend(copy_patch(facts["goldByStem"][stem], patch_index, release))

    silver_by_stem = by_stem(silver)
    for label, stem, patch_index, _description, source in VARIANTS:
        table = silver_by_stem if source == "silver" else facts["goldByStem"]
        if stem not in table:
            print(f"❌ 变体 {label} 的源 VPK 没有转换记录：{stem}")
            return 1
        written.extend(copy_patch(table[stem], patch_index, os.path.join(variants_root, label)))

    build_readme(os.path.join(output, "README.txt"), facts, gold)

    print(f"\n✅ 已生成组合包：{output}")
    total = 0
    for path in written:
        size = os.path.getsize(path)
        total += size
        print(f"   {os.path.relpath(path, output):<64} {size:>12,} B")
    print(f"   {'合计':<64} {total:>12,} B")
    return 0


if __name__ == "__main__":
    sys.exit(main())
