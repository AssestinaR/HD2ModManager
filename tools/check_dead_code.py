#!/usr/bin/env python3
"""
死代码可达性分析（HD2ModManager）

用途
----
从生产入口出发，沿类型引用做传递闭包，找出"生产上根本走不到"的类型。
判定口径比单纯 grep 计数可靠：**引用是否来自"从生产入口可达"的代码**。

为什么不能只看 grep
------------------
本仓库有大量"看起来有引用、其实已经死了"的代码，引用来自三类非生产位置：
  1) 测试文件（*.Tests 项目）
  2) _diagnostics/ 下的临时诊断工程（不在 WpfApp1.sln 里）
  3) 同一死簇内部的互相引用
纯 grep 会把它们全判成活代码。

原理
----
- 节点 = 每个 .cs 文件声明的类型
- 边   = 文件 A 的源码里出现了 B 声明的类型名（词匹配）
- 根   = CLI 入口(Program.cs / CliApplication.cs) + Manager 入口(App.xaml.cs / MainWindow.xaml.cs)
         + 全部 .xaml（WPF 的 UI 定义，含独立 ResourceDictionary）
- CoreServices 的 CreateXxx 工厂按"被调用才连通"处理：只有当某个可达文件里出现
  `CoreServices.CreateXxx(` 时，才把该工厂体内构造的类型标记为可达。
  这样"工厂存在但没人调用"不会被误判成活代码。

已知局限（结果需人工复核）
------------------------
- 词匹配可能把"命名前缀相同的不同类型"误判为可达（如
  `CanonicalDependencyClosure` class 与 `CanonicalDependencyClosureValidation` enum）
- 反射 / Activator / DI 按名字注册的路径无法识别（本仓库目前没有）
- 需要位置参数才能走到的代码路径不受影响（静态分析不关心参数）

用法
----
    python tools/check_dead_code.py
输出每行： 类型名 <TAB> 声明文件（可能多个）
"""

import os
import re
import sys
from collections import defaultdict, deque

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# 生产项目（ManagedMain 是过时留档项目，HD2RuntimeArmorOverride 不在 sln，均排除）
PROJECTS = ['HD2ModCore', 'HD2ModAdaptation', 'HD2ModCli',
            'HD2ModManager', 'HD2GameItemAssets', 'Decryptor']

# 入口文件（相对仓库根，用后缀匹配）
ROOTS = [
    'HD2ModCli/Program.cs',
    'HD2ModCli/CliApplication.cs',
    'HD2ModManager/App.xaml.cs',
    'HD2ModManager/MainWindow.xaml.cs',
]

# CoreServices 中央装配点
CORE_SERVICES = 'HD2ModCore/Infrastructure/CoreServices.cs'

SKIP_DIRS = {'obj', 'bin', 'artifacts', 'tmp', '__pycache__'}

TYPE_RE = re.compile(
    r'^\s*(?:\[[^\]]*\]\s*)*'
    r'(?:public|internal|private|protected|file)?\s*'
    r'(?:static\s+|sealed\s+|abstract\s+|partial\s+|readonly\s+|unsafe\s+)*'
    r'(class|interface|struct|record|enum)\s+([A-Za-z_][A-Za-z0-9_]*)',
    re.M)

WORD = re.compile(r'[A-Za-z_][A-Za-z0-9_]*')


def read(path):
    try:
        with open(path, encoding='utf-8-sig', errors='replace') as f:
            return f.read()
    except OSError:
        return ''


def walk_sources():
    for project in PROJECTS:
        base = os.path.join(ROOT, project)
        for dirpath, dirnames, filenames in os.walk(base):
            dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
            for fn in filenames:
                yield os.path.join(dirpath, fn)


def main():
    cs_files = [p for p in walk_sources() if p.endswith('.cs')]
    xaml_files = [p for p in walk_sources() if p.endswith('.xaml')]
    if not cs_files:
        sys.exit('未找到 .cs 文件；请在仓库根目录运行。')

    src = {p: read(p) for p in cs_files}

    # XAML 引用并入其代码隐藏文件，这样 <views:SomeView/> 算作 .xaml.cs 的引用
    merged = 0
    for p in cs_files:
        if p.endswith('.xaml.cs'):
            xaml = p[:-3]
            if os.path.exists(xaml):
                src[p] += '\n' + read(xaml)
                merged += 1

    # 独立 ResourceDictionary 等无代码隐藏的 XAML，作为额外根
    for i, x in enumerate(xaml_files):
        src[f'__xaml_root_{i}__'] = read(x)

    # 声明类型 -> 文件
    declared = {}
    for p, text in src.items():
        declared[p] = {m.group(2) for m in TYPE_RE.finditer(text)}

    type_to_files = defaultdict(set)
    for p, names in declared.items():
        for n in names:
            type_to_files[n].add(p)
    all_types = set(type_to_files)

    # CoreServices 工厂体 -> 构造的类型集合
    factory_types = defaultdict(set)
    for part in re.split(r'\n(?=\s*public static )', src.get(
            os.path.join(ROOT, CORE_SERVICES), '')):
        m = re.match(
            r'\s*public static\s+[A-Za-z0-9_<>,\.\?\[\]\s]+?\s+(Create[A-Za-z0-9_]+)\s*\(', part)
        if m:
            factory_types[m.group(1)] |= {w for w in WORD.findall(part) if w in all_types}

    # 文件 -> 它引用到的类型
    refs = {p: set(WORD.findall(t)) & all_types for p, t in src.items()}

    roots = set()
    for suffix in ROOTS:
        hit = next((p for p in cs_files if p.replace('\\', '/').endswith(suffix)), None)
        if hit:
            roots.add(hit)
        else:
            print(f'!! 入口未找到: {suffix}', file=sys.stderr)
    roots |= {k for k in src if k.startswith('__xaml_root_')}

    reachable = set(roots)
    queue = deque(roots)
    while queue:
        cur = queue.popleft()
        targets = set()
        for name in refs[cur]:
            targets |= type_to_files[name]
        for fname in factory_types:
            if f'CoreServices.{fname}' in src[cur]:
                for name in factory_types[fname]:
                    targets |= type_to_files[name]
        for nxt in targets:
            if nxt not in reachable:
                reachable.add(nxt)
                queue.append(nxt)

    unreachable = {}
    for name in all_types:
        owners = type_to_files[name]
        if not (owners & reachable):
            unreachable[name] = sorted(
                os.path.relpath(p, ROOT).replace('\\', '/') for p in owners)

    print(f'# .cs {len(cs_files)} 个 / .xaml 根 {len(xaml_files)} 个'
          f' / 声明类型 {len(all_types)} 个', file=sys.stderr)
    print(f'# 可达文件 {len(reachable & set(cs_files))} / {len(cs_files)}'
          f'，不可达类型 {len(unreachable)} 个', file=sys.stderr)
    for name in sorted(unreachable):
        print(f'{name}\t{",".join(unreachable[name])}')


if __name__ == '__main__':
    main()
