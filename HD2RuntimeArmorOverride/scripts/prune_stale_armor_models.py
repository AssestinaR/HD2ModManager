"""清掉调试过程中在管理器工作区里留下的素体定义。

为什么会有垃圾：`vpk2patch` 每次运行都会**登记**一个素体（管理器要靠它把 Unit 映射到槽位），
而"复用同一个 key"的前提是输出目录里已有 `custom-model.json`。为验证而在多个临时目录里反复跑，
工作区就会堆出若干条指向**不会发布**的 Unit 的模型定义——上机时它们只是坏条目。

用法（**必须显式写出要保留哪一个**，防止手滑删掉正在用的）：

    python prune_stale_armor_models.py --keep custom-2f74eb12b8ed4590a42da48691dcca88 \\
        --remove custom-6cb04b6b2c094eef810b99aa4c964c45 custom-4ba1b8fc99f7459f8f5e1e1ea87438e1

只删列出的 key，其余内容原样保留；`revision` 自增，让管理器察觉变化；改动前备份原文件。
"""

import argparse
import json
import shutil
import sys
import time

sys.stdout.reconfigure(encoding="utf-8")

WORKSPACE = r"E:\mod\.hd2modmanager\armor-registry.workspace.json"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--keep", action="append", required=True,
                        help="必须保留的模型 key（可重复）。用来确认没删错文件。")
    parser.add_argument("--remove", action="append", default=[],
                        help="要删除的模型 key（可重复）。")
    parser.add_argument("--workspace", default=WORKSPACE)
    args = parser.parse_args()

    with open(args.workspace, encoding="utf-8-sig") as handle:
        document = json.load(handle)

    models = document.get("models") or []
    before = len(models)
    keep_set, remove_set = set(args.keep), set(args.remove)

    missing_keep = keep_set - {model.get("key") for model in models}
    if missing_keep:
        print(f"❌ 要保留的 key 不在工作区里，拒绝改动：{sorted(missing_keep)}")
        return 1

    kept, removed = [], []
    for model in models:
        if model.get("key") in remove_set:
            removed.append((model.get("key"), len(model.get("pieces") or [])))
        else:
            kept.append(model)

    if not removed:
        print("没有需要清理的条目。")
        return 0

    backup = f"{args.workspace}.bak-{time.strftime('%Y%m%d-%H%M%S')}"
    shutil.copy2(args.workspace, backup)
    document["models"] = kept
    document["revision"] = int(document.get("revision") or 0) + 1
    with open(args.workspace, "w", encoding="utf-8") as handle:
        json.dump(document, handle, ensure_ascii=False, indent=2)
        handle.write("\n")

    print(f"素体定义 {before} → {len(kept)}（备份 {backup}）")
    for key, pieces in removed:
        print(f"  已删除 {key}  （{pieces} 条 piece）")
    for key in sorted(keep_set):
        print(f"  已保留 {key}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
