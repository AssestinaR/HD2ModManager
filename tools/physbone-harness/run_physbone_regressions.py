"""跑 HD2PhysBoneCompat/test 下所有 Lua harness，逐个独立进程，输出 PASS/FAIL 汇总。

为什么要有这一步：`dotnet test` 完全覆盖不到 Lua 侧 —— 运行时的活动锚点、限位、分帧、
生命周期这些东西只有 harness 能验。以前靠人手一条条 `luajit xxx.lua`，于是"改了忘跑"
和"不知道总共几条"都发生过。

为什么是独立进程：harness 里有 `os.exit`、也有会写全局状态的用例；同进程连跑会互相污染
（之前踩过：attach-loop 那个复现脚本一 `os.exit`，后面的用例全没跑）。

为什么用 LuaJIT 而不是 lupa：本仓不依赖 Python 扩展。游戏 VM 是 Lua 5.1，LuaJIT 是 5.1
兼容的，够用来验语法与数值；需要**字节级**等价时再用 lupa（见 docs/agent 的移植记录）。

用法：
    python tools/physbone-harness/run_physbone_regressions.py
    python tools/physbone-harness/run_physbone_regressions.py --filter axis --verbose
"""
from __future__ import annotations

import argparse
import re
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TEST_DIR = ROOT / "HD2PhysBoneCompat" / "test"
SRC_DIR = ROOT / "HD2PhysBoneCompat" / "src"

# 需要外部产物/夹具的用例：它们在本仓里一直是红的，不算回归（见移植记录的"未验证"一节）。
KNOWN_EXTERNAL = {"conform-stub-test.lua", "lift-probe-test.lua"}
# 被别的用例 require 的库（不是用例本身）。`mp_harness.lua` 名字像库，其实是一整套
# 多人局场景（它自己 os.exit），以前被错列在这里，于是那 24 项检查从来没跑过。
LIBRARY_FILES: set[str] = set()
# 这几个是驱动 runtime 而不是 compat 的。
RUNTIME_TESTS = {"runtime-harness-test.lua", "log-budget-test.lua", "mp_harness.lua", "snapshot-test.lua"}


def find_lua() -> str:
    for name in ("luajit", "lua51", "lua5.1", "lua"):
        found = shutil.which(name)
        if found:
            return found
    raise SystemExit("找不到 Lua 5.1 解释器（luajit / lua51 / lua5.1 / lua 任一即可）")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--filter", default="", help="只跑文件名包含该子串的用例")
    parser.add_argument("--verbose", action="store_true")
    args = parser.parse_args()

    lua = find_lua()
    tests = sorted(p for p in TEST_DIR.glob("*.lua") if p.name not in LIBRARY_FILES)
    if args.filter:
        tests = [p for p in tests if args.filter in p.name]
    if not tests:
        raise SystemExit("没有匹配的用例")

    rows: list[tuple[str, str, int, int, int]] = []
    for test in tests:
        module = SRC_DIR / ("physbone_runtime.lua" if test.name in RUNTIME_TESTS else "physbone_compat.lua")
        result = subprocess.run(
            [lua, str(test), str(module.relative_to(ROOT))],
            cwd=ROOT,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
        )
        out = (result.stdout or "") + (result.stderr or "")
        passed = len(re.findall(r"(?m)^\s*PASS\s", out))
        failed = len(re.findall(r"(?m)^\s*FAIL\s", out))
        if failed:
            status = "FAIL"
        elif result.returncode == 0:
            status = "PASS"
        elif test.name in KNOWN_EXTERNAL:
            status = "SKIP(缺夹具)"
        else:
            status = "FAIL"
        rows.append((test.name, status, passed, failed, result.returncode))
        if args.verbose or status == "FAIL":
            print(f"--- {test.name} ({status}) ---")
            print(out.strip()[-3000:])

    width = max(len(row[0]) for row in rows)
    for name, status, passed, failed, code in rows:
        print(f"{name:<{width}}  {status:<12} PASS={passed:<4} FAIL={failed:<3} exit={code}")
    total_pass = sum(row[2] for row in rows)
    total_fail = sum(row[3] for row in rows)
    bad = [row[0] for row in rows if row[1] == "FAIL"]
    print(f"\n合计 PASS={total_pass} FAIL={total_fail}；红={len(bad)}")
    if bad:
        print("红的：" + "、".join(bad))
    return 0 if not bad else 1


if __name__ == "__main__":
    sys.exit(main())
