"""按 F9 就往快照目录写一份新请求（全局热键，不需要游戏有焦点）。

为什么不在 Lua 里读键：这个游戏的输入走 Raw Input，Lua 侧没有可信的按键 API
（见 tools/game-input.py 的注释）。所以热键在**桌面这侧**注册，Lua 只轮询请求文件。

用法：
    python tools/physbone-snapshot/listen-f9.py                 # 监听 F9，Ctrl+C 退出
    python tools/physbone-snapshot/listen-f9.py --key F10
    python tools/physbone-snapshot/listen-f9.py --frames 6 --stride 3

每按一次：把 seq 改成当前毫秒时间戳，重写 request.txt（节点清单不变）。
游戏侧看到内容变了就采一次；采完会写 done.txt，这个脚本也会把 done.txt 打出来。
"""

from __future__ import annotations

import argparse
import ctypes
import ctypes.wintypes as wintypes
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from request import build_request, default_directory, load_nodes  # noqa: E402

user32 = ctypes.WinDLL("user32", use_last_error=True)

MOD_ALT = 0x0001
MOD_CONTROL = 0x0002
MOD_SHIFT = 0x0004
MOD_NOREPEAT = 0x4000
WM_HOTKEY = 0x0312
WM_QUIT = 0x0012

VK = {
    "F1": 0x70, "F2": 0x71, "F3": 0x72, "F4": 0x73, "F5": 0x74, "F6": 0x75,
    "F7": 0x76, "F8": 0x77, "F9": 0x78, "F10": 0x79, "F11": 0x7A, "F12": 0x7B,
}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dir", default=None, help="快照目录（默认 %%LOCALAPPDATA%%\\hd2physbone-snap）")
    parser.add_argument("--nodes", default=str(Path(__file__).resolve().parent / "nodes.txt"))
    parser.add_argument("--key", default="F9", help="热键（默认 F9）")
    parser.add_argument("--ctrl", action="store_true", help="需要按住 Ctrl")
    parser.add_argument("--alt", action="store_true", help="需要按住 Alt")
    parser.add_argument("--shift", action="store_true", help="需要按住 Shift")
    parser.add_argument("--frames", type=int, default=6)
    parser.add_argument("--stride", type=int, default=3)
    args = parser.parse_args()

    name = args.key.upper()
    if name not in VK:
        print(f"不支持的热键 {args.key}；可选：{', '.join(VK)}", file=sys.stderr)
        return 2

    nodes = load_nodes(Path(args.nodes))
    if not nodes:
        print(f"节点清单是空的：{args.nodes}", file=sys.stderr)
        return 2

    target = Path(args.dir) if args.dir else default_directory()
    target.mkdir(parents=True, exist_ok=True)
    request_path = target / "request.txt"
    done_path = target / "done.txt"

    modifiers = MOD_NOREPEAT
    if args.ctrl:
        modifiers |= MOD_CONTROL
    if args.alt:
        modifiers |= MOD_ALT
    if args.shift:
        modifiers |= MOD_SHIFT

    if not user32.RegisterHotKey(None, 1, modifiers, VK[name]):
        print(f"注册热键 {name} 失败（错误码 {ctypes.get_last_error()}）——可能被别的程序占了",
              file=sys.stderr)
        return 1

    print(f"已监听 {name}（全局，不需要游戏有焦点）。目录：{target}")
    print(f"节点 {len(nodes)} 个；每按一次采 {args.frames} 帧、每 {args.stride} 帧一张。Ctrl+C 退出。")
    print(f"提醒：这个脚本只能写请求，游戏里得是**带快照功能的运行时**（2.0.2+）才会应答。")

    last_done = None
    try:
        message = wintypes.MSG()
        while True:
            # 有消息就处理，没消息就睡 50 ms —— 顺带轮询 done.txt，按一次能看到回执
            while user32.PeekMessageW(ctypes.byref(message), None, 0, 0, 1):
                if message.message == WM_HOTKEY:
                    seq = str(int(time.time() * 1000))
                    request_path.write_text(
                        build_request(args.frames, args.stride, nodes, seq), encoding="utf-8")
                    print(f"[{time.strftime('%H:%M:%S')}] 按下 {name} ⇒ 写请求 seq={seq}")
                elif message.message == WM_QUIT:
                    raise KeyboardInterrupt
                user32.TranslateMessage(ctypes.byref(message))
                user32.DispatchMessageW(ctypes.byref(message))

            if done_path.exists():
                try:
                    text = done_path.read_text(encoding="utf-8").strip()
                except OSError:
                    text = None
                if text and text != last_done:
                    last_done = text
                    print(f"    ✅ 游戏侧已写出：{text}")
            time.sleep(0.05)
    except KeyboardInterrupt:
        print("\n退出。")
    finally:
        user32.UnregisterHotKey(None, 1)
    return 0


if __name__ == "__main__":
    sys.exit(main())
