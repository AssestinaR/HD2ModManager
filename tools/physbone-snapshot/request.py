"""给游戏里的物理运行时写一份「姿态快照请求」。

游戏侧（HD2PhysBoneCompat/src/physbone_runtime.lua 的 snapshot_poll）每 30 帧读一次
`%LOCALAPPDATA%\\hd2physbone-snap\\request.txt`；内容变了就采一次骨架，写出
`snapshot-<seq>-<时间>.json`。这里就是写那个请求文件的一端。

为什么用纯文本 `key=value` 而不是 JSON：少一层转义就少一类静默失败。
一行看不懂只跳过那一行，不会让整份请求作废。

用法：
    python tools/physbone-snapshot/request.py                     # 用 nodes.txt 写一份新请求
    python tools/physbone-snapshot/request.py --frames 6 --stride 3
    python tools/physbone-snapshot/request.py --list              # 只看会写什么，不动盘

`seq` 每次都要不同：游戏侧是按「文件内容变了没有」判断是不是新请求的。
"""

from __future__ import annotations

import argparse
import os
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
DEFAULT_NODES = HERE / "nodes.txt"


def default_directory() -> Path:
    base = os.environ.get("LOCALAPPDATA")
    if base:
        return Path(base) / "hd2physbone-snap"
    return Path.home() / "hd2physbone-snap"


def load_nodes(path: Path) -> list[str]:
    hashes = []
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        if line.lower().startswith("0x"):
            hashes.append(line.lower())
    return hashes


def build_request(frames: int, stride: int, nodes: list[str], seq: str) -> str:
    lines = [
        "# hd2physbone 姿态快照请求（外部工具写、游戏侧只读）",
        f"seq = {seq}",
        f"frames = {frames}",
        f"stride = {stride}",
    ]
    lines += [f"node = {item}" for item in nodes]
    return "\n".join(lines) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dir", default=None, help="快照目录（默认 %%LOCALAPPDATA%%\\hd2physbone-snap）")
    parser.add_argument("--nodes", default=str(DEFAULT_NODES), help="节点哈希清单")
    parser.add_argument("--frames", type=int, default=6, help="采几帧（上限 32）")
    parser.add_argument("--stride", type=int, default=3, help="每几帧采一张（上限 30）")
    parser.add_argument("--seq", default=None, help="请求序号（默认用时间戳）")
    parser.add_argument("--list", action="store_true", help="只打印不写盘")
    args = parser.parse_args()

    nodes = load_nodes(Path(args.nodes))
    if not nodes:
        print(f"节点清单是空的：{args.nodes}", file=sys.stderr)
        return 2

    seq = args.seq or str(int(time.time()))
    text = build_request(max(1, min(32, args.frames)), max(1, min(30, args.stride)), nodes, seq)
    target = Path(args.dir) if args.dir else default_directory()
    path = target / "request.txt"

    print(f"节点 {len(nodes)} 个，帧 {args.frames} × 每 {args.stride} 帧一张，seq={seq}")
    print(f"目标：{path}")
    if args.list:
        print(text[:400] + ("…" if len(text) > 400 else ""))
        return 0

    target.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")
    print("已写入。游戏里帧循环跑到下一轮轮询（≤30 帧）就会开始采。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
