"""按 F10 扫"手调角度"档位（写进调参文件，运行时 0.25 秒内生效）。

为什么要手调扫档：按"亏损 / 力臂"算出来的角度只有 3~4°（肉眼等于没动），
而力臂只能估。与其推公式，不如给一排档位让你扫一遍，用眼睛定一个
"多数裙子都不难看、又不容易嵌进屁股"的值。

用法：
    python tools/physbone-snapshot/cycle-angle.py
    python tools/physbone-snapshot/cycle-angle.py --key F11
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
LADDER = [10, 15, 20, 25, 30, 35, 40, 45, 50]


def tune_path() -> Path:
    base = os.environ.get("LOCALAPPDATA")
    return Path(base) / "hd2pbx_tune.txt" if base else Path.home() / "hd2pbx_tune.txt"


def read_deg(path: Path) -> float:
    if not path.exists():
        return 0.0
    for line in path.read_text(encoding="utf-8").splitlines():
        match = re.match(r"^\s*skirtDeg\s*=\s*(-?[\d.]+)", line)
        if match:
            return float(match.group(1))
    return 0.0


def write_deg(path: Path, value: float) -> None:
    text = path.read_text(encoding="utf-8") if path.exists() else ""
    lines = text.splitlines()
    for index, line in enumerate(lines):
        if re.match(r"^\s*skirtDeg\s*=", line):
            lines[index] = f"skirtDeg    = {value:g}      # 手调角度（度）"
            break
    else:
        lines.append(f"skirtDeg    = {value:g}")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--key", default="F10")
    parser.add_argument("--dir", default=None)
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
        print(f"注册热键 {name} 失败（错误码 {ctypes.get_last_error()}）", file=sys.stderr)
        return 1

    print(f"已监听 {name}：按一下换一档角度。调参文件：{path}")
    print("档位：" + " → ".join(f"{value:g}°" for value in LADDER))
    print(f"当前 skirtDeg = {read_deg(path):g}°")

    try:
        message = wintypes.MSG()
        while True:
            while user32.PeekMessageW(ctypes.byref(message), None, 0, 0, 1):
                if message.message == WM_HOTKEY:
                    current = read_deg(path)
                    try:
                        nxt = LADDER[LADDER.index(current) + 1]
                    except (ValueError, IndexError):
                        nxt = LADDER[0]
                    write_deg(path, nxt)
                    print(f"[{time.strftime('%H:%M:%S')}] skirtDeg → {nxt:g}°")
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
