# -*- coding: utf-8 -*-
"""没有 luajit.exe 时，用 lupa 自带的 LuaJIT 2.1 跑本目录的 Lua 测试。

    python -m pip install --target <临时目录>/pylib lupa
    python -I HD2RuntimeArmorOverride/scripts/run_lua_test.py <临时目录>/pylib \
        HD2RuntimeArmorOverride/tests/test_kit_instance_hider.lua \
        HD2RuntimeArmorOverride/src/kit_instance_hider.lua

第一个参数是 lupa 所在目录，第二个是测试文件，其余参数作为 Lua 的 arg[1..n]。
从仓库根目录运行（测试里的相对路径按仓库根解析）。
"""
import sys


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    sys.path.insert(0, sys.argv[1])
    try:
        from lupa import luajit21 as lupa
    except ImportError:
        from lupa import luajit20 as lupa
    runtime = lupa.LuaRuntime()
    print(runtime.eval('jit and jit.version or _VERSION'))
    args = runtime.table(*sys.argv[3:])
    args[0] = sys.argv[2]
    runtime.globals().arg = args
    with open(sys.argv[2], encoding='utf-8') as handle:
        source = handle.read()
    try:
        runtime.execute(source)
    except Exception as error:  # Lua 断言失败会以 LuaError 抛出
        print('FAIL:', error)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
