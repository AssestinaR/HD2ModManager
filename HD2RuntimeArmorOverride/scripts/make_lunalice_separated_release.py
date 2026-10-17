"""把 `vpk2patch` 的逐 VPK 产出组装成「一个 host + 若干选项」的组合包，并逐项核对。

结构（管理器会这样理解它）：

    Lunalice-RE2310\\                        ← 导入这一个文件夹
        9ba626afa44a3aa3.patch_0 (+gpu)      ← 根目录带 patch ⇒ host（unit）
        Lunalice基础材质A1主体\\             ← 含 patch 的子目录 ⇒ 该 host 的一个 Option
            9ba626afa44a3aa3.patch_0 (+gpu)
        …

依据是 `ModLibraryImporter.PlanHeuristicImport`：根目录里那个"带 patch 的节点"成为
`ModNodeKind.Standard`（host），其余每个含 patch 的子目录成为 `ModNodeKind.Option`，
`HostModGuids = [父节点]`、`OptionOrder` = 同级按路径字典序。**不需要 manifest.json**，
也不需要预先写 `option.json`（那是导入后管理器自己落盘的协议文件）。

为什么每个选项目录里都叫 `patch_0`：`PatchFileNormalizer` 只**按目录**规整编号，
而部署时 `ApplyPlanner` 会**按 hex 全局重排**成连续序号。所以各目录里同号不冲突，
反而让"二选一"的语义一眼可见——用户关掉不想要的那个即可。

组合包不含 F1/F2：它们只带描边遮罩，而 HD2 的 advanced/alphaclip 模板没有描边槽
（详见 `notes/vpk2patch-contract-20261016.md` 第 8.2 节）。
"""

import argparse
import json
import os
import shutil
import struct
import sys

sys.stdout.reconfigure(encoding="utf-8")

ARCHIVE_HEX = "9ba626afa44a3aa3"
MOD_DIR_NAME = "Lunalice-RE2310"
UNIT_PATCH_NAME = "patch_0"
REPO_ROOT = r"E:\Data\source\repos\WpfApp1"

# 源 VPK 名（小写，与 vpk2patch 的产出目录一致）→ 选项显示名。
# 顺序 = 同级字典序，也就是管理器里 OptionOrder 的次序。
OPTIONS = [
    ("lunalice基础材质a1主体", "Lunalice基础材质A1主体"),
    ("lunalice基础材质b1服装色白", "Lunalice基础材质B1服装色白"),
    ("lunalice基础材质b2服装色黑", "Lunalice基础材质B2服装色黑"),
    ("lunalice基础材质c1金属装饰品色金", "Lunalice基础材质C1金属装饰品色金"),
    ("lunalice基础材质c2金属装饰品色银", "Lunalice基础材质C2金属装饰品色银"),
    ("lunalice基础材质d1发色白", "Lunalice基础材质D1发色白"),
    ("lunalice基础材质d2发色紫", "Lunalice基础材质D2发色紫"),
    ("lunalice基础材质d3发色浅灰", "Lunalice基础材质D3发色浅灰"),
    ("lunalice基础材质d4发色黄", "Lunalice基础材质D4发色黄"),
    ("lunalice基础材质e1瞳色紫1", "Lunalice基础材质E1瞳色紫1"),
    ("lunalice基础材质e2瞳色红", "Lunalice基础材质E2瞳色红"),
    ("lunalice基础材质e3瞳色紫2", "Lunalice基础材质E3瞳色紫2"),
]

# 同一组里的选项**互相覆盖同一批资产 id**，所以它们的 TOC 必须逐字节相同
# （只有 .gpu_resources 里的像素不同）。这是"关掉一个、换另一个"能成立的前提。
ALTERNATIVE_GROUPS = [
    ("服装色", ["lunalice基础材质b1服装色白", "lunalice基础材质b2服装色黑"]),
    ("金属色", ["lunalice基础材质c1金属装饰品色金", "lunalice基础材质c2金属装饰品色银"]),
    ("发色", ["lunalice基础材质d1发色白", "lunalice基础材质d2发色紫",
              "lunalice基础材质d3发色浅灰", "lunalice基础材质d4发色黄"]),
    ("瞳色", ["lunalice基础材质e1瞳色紫1", "lunalice基础材质e2瞳色红",
              "lunalice基础材质e3瞳色紫2"]),
]

NO_EQUIVALENT = [
    ("F1 / F2（身体描边 / 描边无）",
     "只带 `$outlinetexture` 描边遮罩，而 HD2 的 advanced/alphaclip 模板没有描边槽；"
     "而且源侧那两张 VTF 本身是截断的（头写 512x512 BC4，实际只有 16x16 缩略图）。"
     "「描边开 / 关」在 HD2 里没有对应物，所以不出选项。"),
]

# ── 同键替换 RE-2310 ──────────────────────────────────────────────────────
#
# `vpk2patch` 产出的是**自定义素体**：Unit 拿的是全新的随机 id，要靠运行时注册表把
# 它映射到某个槽位。而"替换某件护甲"是另一件事：**把我们做的 Unit 写到那件护甲的
# Unit 键上**，它剩下的 Unit 用隐藏占位盖掉，否则原版几何会和我们的模型打架。
#
# ⚠️ CLI 的三个 `equipment`/`patch repair` 命令都做不了这件事：它们的来源必须是
# **已经存在于游戏里的** Unit id（`cross-armor` 的 `sourceCandidates` 因此为空，
# `armor-normalize` 的 `armors` 为空，`patch repair` 的 `replacementUnitCount` 为 0）。
# 自定义素体的 Unit 是新资产，没有任何游戏 Unit 与它同键，所以只能自己重写键。
#
# RE-2310 是一个两件套（setId 628829860）：
#   护甲 itemId 820333399   23 个 Piece / 23 个 Unit / 3 行（body 3/0/1 = 7/8/8）
#   头盔 itemId 1745464610   1 个 Piece / 1 个 Unit（af431352d9b95e83）
#
#   替换 11 个 —— 槽 2/3/6/7 的 type=1 各有 body=0 与 body=1 两份，必须两个键写同一份
#                payload，否则另一种体型的玩家会看到空槽；槽 4/5 只有 body=3 一份；头盔一个。
#   隐藏 12 个 —— 每槽的 type=0 / type=2、以及 slot 8/9（MaterialLut/PatternLut）。
#   不动  1 个 —— 3c33cf10a26cbb3e（body=3 slot=1 type=0，全库唯一零辅助字节的 piece），
#                被 101 个 Kit 共用，碰它会让 101 件护甲一起变。
RE2310_REPLACE = {
    1: ["af431352d9b95e83"],                        # Head      -> RE-2310 头盔
    2: ["cae35ee15168145f", "9841b16544c8fbae"],    # Chest     body1 / body0
    3: ["17da843a4908511e", "45d79d84b8933a7f"],    # Hips
    4: ["c9904983657e9fc8"],                        # LeftLeg   body3
    5: ["9eb91a5581707ef1"],                        # RightLeg  body3
    6: ["91eb7be84016ceab", "9a5e6dc74cdb08a3"],    # LeftArm
    7: ["7336c6b662522f0e", "4f7bce4ca49ccda8"],    # RightArm
}

RE2310_HIDE_KEYS = [
    "9a7b05af54f6722f", "226dfb2e738ae56e", "b22e65e604a6ef18", "7cfd3805e70ea7bd",
    "914e7539788418fc", "5de0cf947d0e6520", "7b355519dcfb3085", "3a91f5a56da3201e",
    "3692b4d3a0b8d5d3", "3c09e08493ee44b8", "144d95c1500b73bc", "6533a416930570a5",
]

# 共用披风：只用于断言"没被碰"。
RE2310_SHARED_CAPE = "3c33cf10a26cbb3e"

# ── 换层变体：把身体写到 **Armor** 层，而不是 Undergarment 层 ──────────────
#
# 为什么值得试：头盔（Armor 层）能显示、身体（Undergarment 层）不能 —— 那么"可见的那层"
# 很可能就是 Armor。这个变体把我们 6 个身体部件写到 12 个 Armor 键里的 8 个（Torso×2、
# Pelvis×2、四肢各 1），肩膀的 4 个 Armor 键直接隐藏（肩膀本来就在手臂网格里，写进去会重叠渲染）。
# 于是 9 个替换 + 14 个隐藏 = 23，与原来一一对应。
RE2310_REPLACE_ARMOR = {
    1: ["af431352d9b95e83"],                        # Head      -> 头盔（Armor 层，本来就能显示）
    2: ["3c09e08493ee44b8", "5de0cf947d0e6520"],    # Chest     -> Torso Armor Slim / Stocky
    3: ["3692b4d3a0b8d5d3", "914e7539788418fc"],    # Hips      -> Pelvis Armor Slim / Stocky
    4: ["b22e65e604a6ef18"],                        # LeftLeg   -> LeftLeg Armor Any
    5: ["226dfb2e738ae56e"],                        # RightLeg  -> RightLeg Armor Any
    6: ["7cfd3805e70ea7bd"],                        # LeftArm   -> LeftArm Armor Any
    7: ["9a7b05af54f6722f"],                        # RightArm  -> RightArm Armor Any
}

RE2310_HIDE_KEYS_ARMOR = [
    # 10 个 Undergarment 键
    "cae35ee15168145f", "9841b16544c8fbae", "17da843a4908511e", "45d79d84b8933a7f",
    "c9904983657e9fc8", "9eb91a5581707ef1", "91eb7be84016ceab", "9a5e6dc74cdb08a3",
    "4f7bce4ca49ccda8", "7336c6b662522f0e",
    # 4 个肩膀 Armor 键（肩膀在手臂网格里，写进去会重叠渲染）
    "144d95c1500b73bc", "7b355519dcfb3085", "3a91f5a56da3201e", "6533a416930570a5",
]

# 隐藏占位的来源：那次"隐藏全部原版护甲 Unit"实验的产物，按原键有 1939 个正确占位。
RE2310_HIDE_TEMPLATE = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    "evidence", "vanilla-hide-ab-20260928", "patch", "9ba626afa44a3aa3.patch_5")


def load_report(path):
    with open(path, encoding="utf-8-sig") as handle:
        return json.load(handle)


def by_stem(report):
    return {
        os.path.splitext(os.path.basename(item["vpk"]))[0].lower(): item
        for item in report["patches"]
    }


def patch_files(item):
    directory = item["output"]
    return sorted(
        os.path.join(directory, name)
        for name in os.listdir(directory)
        if ".patch_" in name
    )


def base_toc(item):
    """这条转换记录产出的 patch 本体（不含 sidecar）。"""
    for path in patch_files(item):
        name = os.path.basename(path)
        if name.endswith(".gpu_resources") or name.endswith(".stream"):
            continue
        return path
    return None


def read_toc_keys(path):
    """读 canonical patch TOC 的 (type_id, file_id) 集合。

    条目里**先 file_id 再 type_id**（`CanonicalPatchWriter.WriteEntry`），与"键 = (type, id)"
    的直觉相反。只取键、不取偏移与长度：同类选项的像素不同，尺寸与偏移本来就会不一样，
    拿整份 TOC 逐字节比会误报；真正决定"是不是同一套材质"的只有键集合。
    """
    data = open(path, "rb").read()
    magic, type_count, entry_count = struct.unpack_from("<III", data, 0)
    if magic != 4026531857:
        raise SystemExit(f"不是 canonical patch TOC：{path}")
    table = 72 + type_count * 32
    keys = set()
    for index in range(entry_count):
        file_id, type_id = struct.unpack_from("<QQ", data, table + index * 80)
        keys.add((type_id, file_id))
    return keys


def rekey_host_to_re2310(unit_patch, destination, replace=None, hide_keys=None):
    """把自定义素体的 Unit 重写到 RE-2310 的 23 个键上，返回写出的 host patch 路径。

    为什么必须重写键而不是"注册一下就行"：运行时注册表把新 Unit 映射到槽位，那是
    **自定义素体**的做法——原版那件护甲的 Unit 仍然在，两套几何会打架。同键替换是
    "占住那 23 个键"：11 个写我们的，12 个写隐藏占位。
    """
    sys.path.insert(0, os.path.join(REPO_ROOT, "tmp", "rig-lab"))
    import hd2_patch as HP          # noqa: PLC0415  （实验线工具，延迟导入）
    import hd2_patchw as PW         # noqa: PLC0415
    import hd2_unit as U            # noqa: PLC0415

    replace = replace if replace is not None else RE2310_REPLACE
    hide_keys = hide_keys if hide_keys is not None else RE2310_HIDE_KEYS

    def load(path):
        entries, _ = HP.scan_entries(path)
        found = {}
        for entry in entries:
            if entry["type_id"] != U.UNIT_ID:
                continue
            payload = HP.entry_payload(entry, path)
            found[entry["file_id"]] = (payload["toc"], payload["stream"], payload["gpu"], entry)
        return found

    ours = load(unit_patch)
    hide = load(RE2310_HIDE_TEMPLATE)
    print(f"   我方 Unit {len(ours)} 个；隐藏模板 Unit {len(hide)} 个")

    # Unit id = seed + index（index 从 1 起，按 BodyRegions.All 的槽位顺序）。
    seed = min(ours) - 1
    by_index = {index: seed + index for index in RE2310_REPLACE}
    missing = [index for index, unit in by_index.items() if unit not in ours]
    if missing:
        raise SystemExit(f"我方包缺 Unit（按序号推导 seed=0x{seed:016x}）：{missing}")

    missing = [key for key in hide_keys if int(key, 16) not in hide]
    if missing:
        raise SystemExit(f"隐藏模板缺占位：{missing}")

    entries = []
    replaced = []
    for index, targets in replace.items():
        source = by_index[index]
        toc, stream, gpu, src = ours[source]
        for target in targets:
            entries.append(PW.Entry(U.UNIT_ID, int(target, 16), toc, stream, gpu,
                                    src["unknown1"], src["unknown2"], src["unknown3"],
                                    src["unknown4"]))
            replaced.append(target)
        print(f"   槽 {index}  0x{source:016x} -> {' '.join(targets)}")

    for key in hide_keys:
        toc, stream, gpu, src = hide[int(key, 16)]
        entries.append(PW.Entry(U.UNIT_ID, int(key, 16), toc, stream, gpu,
                                src["unknown1"], src["unknown2"], src["unknown3"],
                                src["unknown4"]))
    print(f"   隐藏 {len(hide_keys)} 个：{' '.join(k[-4:] for k in RE2310_HIDE_KEYS)}")

    # 正式交付必须是 23 个（RE-2310 的 23 个 Piece）。做**探针**时只写一两个键，那是故意的，
    # 所以只在用默认映射时检查。
    if len(entries) != 23 and (replace is RE2310_REPLACE or replace is RE2310_REPLACE_ARMOR):
        raise SystemExit(f"条目数 {len(entries)} != 23（RE-2310 的 23 个 Piece）")

    os.makedirs(destination, exist_ok=True)
    result = PW.write_patch(entries, unit_patch, destination, f"{ARCHIVE_HEX}.{UNIT_PATCH_NAME}")

    # ---- 读回来验：替换键的 payload 必须与源逐字节一致，隐藏占位必须与模板一致 ----
    back = load(result["toc"])
    if len(back) != 23:
        raise SystemExit(f"读回 {len(back)} 个 Unit，期望 23")
    for index, targets in replace.items():
        want = ours[by_index[index]][0]
        for target in targets:
            if back[int(target, 16)][0] != want:
                raise SystemExit(f"{target} 的 payload 与源不一致")
    for key in hide_keys:
        if back[int(key, 16)][0] != hide[int(key, 16)][0]:
            raise SystemExit(f"{key} 的隐藏占位与模板不一致")
    if int(RE2310_SHARED_CAPE, 16) in back:
        raise SystemExit(f"共用披风 {RE2310_SHARED_CAPE} 被写进了包 —— 绝不允许")
    print("   验证通过：23 个 Unit、替换键 payload 与源逐字节一致、隐藏占位一致、共用披风未出现")
    return result["toc"]


def verify(report):
    problems = []
    stems = by_stem(report)

    # ① host 必须是纯 unit。
    model = stems.get("lunalice_zoey")
    if model is None:
        return ["队列里没有模型 VPK，组合包缺 host 的 unit patch。"], {}
    if model["kind"] != "model":
        problems.append(f"lunalice_zoey 被判成 {model['kind']}，应当是 model。")
    if model["materials"] or model["textures"]:
        problems.append(
            f"host 的 patch 不是纯 unit：materials={model['materials']} textures={model['textures']}。")
    if model["units"] != 7:
        problems.append(f"host 的 Unit 数是 {model['units']}，应当是 7。")

    # ② 每个选项都必须真的产出一个 patch。
    for stem, label in OPTIONS:
        item = stems.get(stem)
        if item is None:
            problems.append(f"选项 {label} 没有转换记录（{stem}）。")
            continue
        if base_toc(item) is None:
            problems.append(f"选项 {label} 一个 patch 都没产出。")
        if item["kind"] != "material":
            problems.append(f"选项 {label} 被判成 {item['kind']}，应当是 material。")

    # ③ 模型引用的材质 id 必须全由选项提供。
    provided_materials, provided_textures = set(), set()
    deferred = {}
    for stem, item in stems.items():
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

    referenced = {row["material"] for row in model["materialDetails"]}
    missing = referenced - provided_materials
    if missing:
        problems.append(f"模型引用了 {len(missing)} 个没有选项提供的材质 id：{sorted(missing)}")

    # ④ 被推给别人的贴图，提供者必须是组合包里的某个选项，而且那个选项真的写了它。
    option_vpk_names = {stem + ".vpk" for stem, _ in OPTIONS}
    for name, origins in sorted(deferred.items()):
        origins = {origin for origin in origins if origin}
        if not origins:
            problems.append(f"贴图 {name} 既没写进本 patch，也说不清是谁提供的。")
            continue
        inside = origins & option_vpk_names
        if not inside:
            problems.append(f"贴图 {name} 由 {sorted(origins)} 提供，但它们不在组合包里。")
            continue
        for origin in inside:
            stem = os.path.splitext(origin)[0].lower()
            item = stems.get(stem)
            if item is None:
                problems.append(f"贴图 {name} 的提供者 {origin} 没有转换记录。")
                continue
            match = [t for t in item["textureDetails"] if t["name"] == name]
            if not match:
                problems.append(f"贴图 {name} 说由 {origin} 提供，但那个 patch 里没有它。")
            elif not any(t["emitted"] for t in match):
                problems.append(f"贴图 {name} 在 {origin} 的 patch 里没被写出。")

    # ⑤ 同组选项引用的资产键集合必须完全相同，否则"关一个换一个"会换出别的 id。
    toc_by_stem = {}
    for stem, item in stems.items():
        path = base_toc(item)
        if path is not None:
            toc_by_stem[stem] = path
    for group_name, members in ALTERNATIVE_GROUPS:
        key_sets = {}
        for stem in members:
            path = toc_by_stem.get(stem)
            if path is None:
                problems.append(f"{group_name} 组里的 {stem} 没有 TOC。")
                continue
            key_sets[stem] = read_toc_keys(path)
        if len(set(frozenset(keys) for keys in key_sets.values())) > 1:
            detail = {stem: len(keys) for stem, keys in key_sets.items()}
            problems.append(
                f"{group_name} 组内各选项引用的资产键不一致 —— 关掉一个换成另一个会换出别的 id：{detail}")

    facts = {
        "model": model,
        "stems": stems,
        "providedMaterials": provided_materials,
        "providedTextures": provided_textures,
        "deferred": deferred,
        "referenced": referenced,
        "tocByStem": toc_by_stem,
    }
    return problems, facts


def build_readme(path, facts, host_mode):
    model = facts["model"]
    stems = facts["stems"]
    lines = [
        "Lunalice → RE-2310 组合包（unit + 材质选项）",
        "=" * 42,
        "",
        "怎么装",
        "------",
        f"把 {MOD_DIR_NAME}\\ 整个文件夹当【一个 mod】导入管理器。",
        "导入后它是一个 host（带 unit 的那个 patch）+ 12 个选项，选项默认全开。",
        "选项之间靠**覆盖同一批资产 id** 生效：同名的材质/贴图由加载顺序靠后的那个赢，",
        "所以全开时看到的永远是某一套完整配色，不会花掉。想固定某个配色，关掉同类其它选项即可。",
        "",
        "结构",
        "----",
        f"  {MOD_DIR_NAME}\\{ARCHIVE_HEX}.{UNIT_PATCH_NAME}      host：RE-2310 的 23 个 Unit 键（11 替换 + 12 隐藏），零材质零贴图",
    ]
    for stem, label in OPTIONS:
        item = stems.get(stem)
        if item is None:
            continue
        lines.append(f"  {MOD_DIR_NAME}\\{label}\\  材质={item['materials']} 贴图={item['textures']}")

    lines += ["", "哪些是同类（互相覆盖，建议只留一个）", "----------------------------------"]
    for group_name, members in ALTERNATIVE_GROUPS:
        labels = [label for stem, label in OPTIONS if stem in members]
        lines.append(f"  {group_name}：{' / '.join(labels)}")

    lines += [
        "",
        "哪些必须一起开",
        "--------------",
        "A1 与 B 不是二选一：它们的 VMT 集合不相交，各自负责一半材质槽，**必须同时开**。",
        "另外 A1 有几个材质的底色在别的选项里（源侧本来就这样）：",
    ]
    for name, origins in sorted(facts["deferred"].items()):
        origins = sorted(origin for origin in origins if origin)
        if origins:
            lines.append(f"  · 贴图 {name} ← {', '.join(origins)}")
    lines.append("  最小完整组合 = host + A1 + B 各一 + C 各一 + D 各一 + E 各一。")

    lines += ["", "没有对应物的源 VPK", "------------------"]
    for title, reason in NO_EQUIVALENT:
        lines.append(f"  · {title}")
        lines.append(f"      {reason}")

    if host_mode == "same-key":
        lines += [
            "",
            "这是一个**同键替换 RE-2310** 的包，不是自定义素体",
            "----------------------------------------------",
            "  host 的 patch 把那件护甲的 23 个 Unit 键全部占住：",
            "    11 个写我们的模型（槽 2/3/6/7 的 body=0 与 body=1 各写同一份 payload，",
            "       否则另一种体型会看到空槽；槽 4/5 只有 body=3 一份；头盔一个）；",
            "    12 个写隐藏占位（每槽的 type=0 / type=2、以及 slot 8/9 的 MaterialLut/PatternLut），",
            "       否则原版几何会和我们的模型打架。",
            "  共用披风 3c33cf10a26cbb3e 被 101 个 Kit 共用，**没有碰**（脚本会断言）。",
            "  因为不新建 Unit，所以**不需要**运行时注册表、也不需要 custom-model.json。",
            "",
            "  ⚠️ 已知缺陷：原版护甲件的 Piece 会指定「用这个 Unit 的第几个网格」——",
            "     Undergarment 要索引 5、Armor/头盔要索引 4。我们的 Unit 只有 5 个网格",
            "     （索引 0..4），且 lod0 的位置跟着外壳 Unit 走（Torso 在索引 3、头盔在 4）。",
            "     结果就是：头盔能显示（要 4，有），身体不显示（要 5，没有）。",
            "     要修得让 Unit 的网格表按护甲件约定排版，不是换键能解决的。",
        ]
    else:
        lines += [
            "",
            "host 用的是**自定义素体**（Unit 是全新 id）",
            "----------------------------------------",
            "  管理器靠运行时注册表把每个 Unit 映射到 body/slot/piece_type，",
            "  网格索引也由注册表那条路自己挑 —— 所以实机上身体是完整的。",
            "  ⚠️ 因此这个包**依赖管理器的护甲注册表**：",
            "     · 素体定义要一起导入（<转换目录>\\lunalice_zoey\\custom-model.json）；",
            "     · 到「护甲映射」页应用一次，由管理器编译成注册协议。",
            "  想要纯 patch、不依赖注册表的版本，用 --host-mode same-key；",
            "  但那版目前身体会缺（见上一段的网格索引问题）。",
        ]

    lines += [
        "",
        "本次转换用的 matcap 策略：albedo",
        "--------------------------------",
        "  金属/宝石件在源侧是「matcap 就是表面」（$matcaptint = 1）。这一版直接把 matcap 当底色",
        "  （贴图 id = hash(matcap 名)），于是 C1/C2 成了真正可换的选项。",
        "  上一版是「把 matcap 的平均色烘进底色」，那样金属色在转换时就固定了，C1/C2 换不动。",
        "  观感差别需要实机看：albedo 保留 matcap 的纹理起伏，bake 是平色。",
        "",
        "  想回到 bake：hd2mod vpk2patch … --matcap bake",
        "",
        "核对结果",
        "--------",
        f"  · host：RE-2310 的 23 个 Unit 键（11 个写我们的模型 + 12 个隐藏占位），0 材质 0 贴图。",
        f"  · 模型引用 {len(facts['referenced'])} 个材质 id，全部由选项提供。",
        f"  · 选项共提供 {len(facts['providedMaterials'])} 个材质、"
        f"{len(facts['providedTextures'])} 张贴图。",
        "  · 每组同类选项引用的资产键完全相同（只差像素），所以关一个换一个是安全的。",
        "",
        "本文件由 make_lunalice_separated_release.py 生成。",
        "",
    ]
    with open(path, "w", encoding="utf-8") as handle:
        handle.write("\n".join(lines))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--json", required=True, help="vpk2patch --json 报告（--matcap albedo）")
    parser.add_argument("--output", required=True, help="组合包根目录")
    parser.add_argument("--overwrite", action="store_true")
    parser.add_argument(
        "--same-key-layer", choices=("undergarment", "armor"), default="undergarment",
        help="同键替换时我们的身体占哪一层。头盔两版都是 Armor。")
    parser.add_argument(
        "--host-mode", choices=("custom-model", "same-key"), default="custom-model",
        help="host 怎么出：custom-model = 原样用 vpk2patch 的 Unit（新 id，靠运行时注册表映射，"
             "实机能看到身体）；same-key = 重写到 RE-2310 的 23 个键上（纯 patch 覆盖，"
             "但当前 Unit 的网格表索引与原版护甲件约定不一致，身体会缺）。")
    args = parser.parse_args()

    report = load_report(args.json)
    problems, facts = verify(report)
    if problems:
        print("❌ 核对没通过：")
        for problem in problems:
            print("   -", problem)
        return 1

    print("✅ 核对通过：")
    print(f"   host 引用 {len(facts['referenced'])} 个材质 id，全部有选项提供。")
    print(f"   选项共提供 {len(facts['providedMaterials'])} 个材质、"
          f"{len(facts['providedTextures'])} 张贴图。")
    for name, origins in sorted(facts["deferred"].items()):
        origins = sorted(origin for origin in origins if origin)
        if origins:
            print(f"   跨选项：贴图 {name} ← {', '.join(origins)}")
    for group_name, members in ALTERNATIVE_GROUPS:
        print(f"   同类（TOC 一致）：{group_name} × {len(members)}")

    output = os.path.abspath(args.output)
    mod_root = os.path.join(output, MOD_DIR_NAME)
    if os.path.isdir(output):
        if not args.overwrite:
            print(f"❌ 输出目录已存在，加 --overwrite：{output}")
            return 1
        shutil.rmtree(output)
    os.makedirs(mod_root)

    written = []

    def copy_into(item, destination):
        os.makedirs(destination, exist_ok=True)
        for path in patch_files(item):
            name = os.path.basename(path)
            suffix = name.split(f"{ARCHIVE_HEX}.", 1)[1] if f"{ARCHIVE_HEX}." in name else name
            target = os.path.join(destination, f"{ARCHIVE_HEX}.{suffix}")
            shutil.copy2(path, target)
            written.append(target)

    if args.host_mode == "same-key":
        print("\n   host：重写到 RE-2310 的 23 个 Unit 键")
        if args.same_key_layer == "armor":
            rekey_host_to_re2310(base_toc(facts["model"]), mod_root,
                                 RE2310_REPLACE_ARMOR, RE2310_HIDE_KEYS_ARMOR)
        else:
            rekey_host_to_re2310(base_toc(facts["model"]), mod_root)
        written.extend(
            os.path.join(mod_root, name) for name in os.listdir(mod_root) if ".patch_" in name
        )
    else:
        print("\n   host：原样用 vpk2patch 的 Unit（自定义素体，靠运行时注册表映射）")
        copy_into(facts["model"], mod_root)

    for stem, label in OPTIONS:
        item = facts["stems"].get(stem)
        if item is None:
            continue
        copy_into(item, os.path.join(mod_root, label))
    build_readme(os.path.join(output, "README.txt"), facts, args.host_mode)

    print(f"\n✅ 已生成组合包：{output}")
    total = 0
    for path in written:
        size = os.path.getsize(path)
        total += size
        print(f"   {os.path.relpath(path, output):<70} {size:>12,} B")
    print(f"   {'合计':<70} {total:>12,} B")
    return 0


if __name__ == "__main__":
    sys.exit(main())
