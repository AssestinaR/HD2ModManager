"""按 F10 循环切换裙摆外推的**预设**（写进调参文件，运行时 0.25 秒内生效）。

为什么是"穷举预设"而不是推理：`write_pose` 把 (轴, 角) 当成父空间增量、局部增量还是
绝对旋转，从代码里读不出来；猜错的代价是"画面毫无变化"，而那时根本分不清是轴错了、
帧错了、还是压根没写进去。8 个预设、每个恒定 60 度，按一下换一个，肉眼一眼分对错。

用法：
    python tools/physbone-snapshot/cycle-preset.py            # 监听 F10
    python tools/physbone-snapshot/cycle-preset.py --key F11
"""

from __future__ import annotations

import argparse
import ctypes
import ctypes.wintypes as wintypes
import os
import re
import sys
import time
from pathlib import Path

user32 = ctypes.WinDLL("user32", use_last_error=True)

MOD_NOREPEAT = 0x4000
WM_HOTKEY = 0x0312
WM_QUIT = 0x0012

VK = {"F7": 0x76, "F8": 0x77, "F9": 0x78, "F10": 0x79, "F11": 0x7A, "F12": 0x7B}
PRESET_COUNT = 8

LABELS = [
    "父空间轴 前乘 +60 度",
    "父空间轴 前乘 -60 度",
    "世界轴   前乘 +60 度",
    "世界轴   前乘 -60 度",
    "父空间轴 后乘 +60 度",
    "父空间轴 后乘 -60 度",
    "世界轴   后乘 +60 度",
    "世界轴   后乘 -60 度",
]


def tune_path() -> Path:
    base = os.environ.get("LOCALAPPDATA")
    return Path(base) / "hd2pbx_tune.txt" if base else Path.home() / "hd2pbx_tune.txt"


def read_preset(path: Path) -> int:
    if not path.exists():
        return 0
    for line in path.read_text(encoding="utf-8").splitlines():
        match = re.match(r"^\s*preset\s*=\s*(-?\d+)", line)
        if match:
            return int(match.group(1))
    return 0


def write_preset(path: Path, value: int) -> None:
    text = path.read_text(encoding="utf-8") if path.exists() else ""
    lines = text.splitlines()
    replaced = False
    for index, line in enumerate(lines):
        if re.match(r"^\s*preset\s*=", line):
            lines[index] = f"preset = {value}"
            replaced = True
            break
    if not replaced:
        lines.append(f"preset = {value}")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--key", default="F10")
    parser.add_argument("--dir", default=None, help="调参文件路径（默认 %%LOCALAPPDATA%%\\hd2pbx_tune.txt）")
    args = parser.parse_args()

    name = args.key.upper()
    if name not in VK:
        print(f"不支持的热键 {args.key}；可选：{', '.join(VK)}", file=sys.stderr)
        return 2

    path = Path(args.dir) if args.dir else tune_path()
    if not path.exists():
        print(f"找不到调参文件：{path}", file=sys.stderr)
        return 2

    if not user32.RegisterHotKey(None, 1, MOD_NOREPEAT, VK[name]):
        print(f"注册热键 {name} 失败（错误码 {ctypes.get_last_error()}）——可能被别的程序占了",
              file=sys.stderr)
        return 1

    current = read_preset(path)
    print(f"已监听 {name}：按一下切下一个预设（当前 preset={current}）。调参文件：{path}")
    print("预设含义：")
    for index, label in enumerate(LABELS, 1):
        print(f"  {index}. {label}")

    try:
        message = wintypes.MSG()
        while True:
            while user32.PeekMessageW(ctypes.byref(message), None, 0, 0, 1):
                if message.message == WM_HOTKEY:
                    current = read_preset(path)
                    nxt = current + 1 if 1 <= current < PRESET_COUNT else 1
                    write_preset(path, nxt)
                    print(f"[{time.strftime('%H:%M:%S')}] preset → {nxt}  {LABELS[nxt - 1]}")
                elif message.message == WM_QUIT:
                    raise KeyboardInterrupt
                user32.TranslateMessage(ctypes.byref(message))
                user32.DispatchMessageW(ctypes.byref(message))
            time.sleep(0.05)
    except KeyboardInterrupt:
        print("\n退出。")
    finally:
        user32.UnregisterHotKey(None, 1)
    return 0


if __name__ == "__main__":
    sys.exit(main())
