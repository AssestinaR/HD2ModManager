"""扫描一个 mod 的 sectional 输出：找出每份 `.gpu_resources` 里有几个物理容器（`HD2SRG01`）。

为什么需要它（2026-11-12）：排查"一个玩家被挂了 7~9 件护甲"时，要先确认
**是"容器被复制了 7 份"还是"一个容器匹配到了 7 个 unit"** —— 这两者的修法完全不同。

用法：
    python tools/physbone-audit/scan-containers.py "E:\\mod\\Lunalice_Bill\\sectional"
    python tools/physbone-audit/scan-containers.py <目录> --magic HD2SRG01
"""

from __future__ import annotations

import argparse
import re
from pathlib import Path

DEFAULT_MAGIC = b"HD2SRG01"


def scan(path: Path, magic: bytes) -> list[int]:
    """返回 magic 在文件里的所有偏移（1 MiB 分块，跨块边界保留 magic-1 字节）。"""
    hits: list[int] = []
    keep = max(len(magic) - 1, 0)
    with path.open("rb") as handle:
        tail = b""
        consumed = 0
        while True:
            chunk = handle.read(1 << 20)
            if not chunk:
                break
            buf = tail + chunk
            start = 0
            while True:
                found = buf.find(magic, start)
                if found < 0:
                    break
                hits.append(consumed - len(tail) + found)
                start = found + 1
            tail = buf[-keep:] if keep else b""
            consumed += len(chunk)
    return hits


def patch_index(path: Path) -> int:
    match = re.search(r"patch_(\d+)", path.name)
    return int(match.group(1)) if match else 10 ** 6


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("dir", help="包含 *.gpu_resources 的目录（sectional 输出目录）")
    parser.add_argument("--magic", default=DEFAULT_MAGIC.decode(), help="容器 magic（默认 HD2SRG01）")
    args = parser.parse_args()

    root = Path(args.dir)
    if not root.is_dir():
        print(f"不是目录：{root}")
        return 2
    magic = args.magic.encode()

    files = sorted(root.glob("*.gpu_resources"), key=patch_index)
    if not files:
        print(f"{root} 下没有 *.gpu_resources")
        return 2

    total = 0
    for path in files:
        hits = scan(path, magic)
        total += len(hits)
        if hits:
            shown = [hex(h) for h in hits[:4]]
            print(f"{path.name:44s} {path.stat().st_size:>12d} B  containers={len(hits)}  at={shown}")
    print(f"\n该 mod 共有 {total} 个 {args.magic} 容器（{len(files)} 个 patch 文件）")
    print("判读：")
    print("  · 1 个 ⇒ 数据只存一份；若运行时挂了多个 unit，问题在『每 unit 都塞了整套骨』。")
    print("  · 多个 ⇒ 容器被复制了，先修复制。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
