# 负结果：运行期改内存里的 TOC 副本，画面不变（2026-10-17）

## 实验

用 MDL 加载的 Lua 探针（`mods/codex/section_toggle_probe`），在游戏进程内：

1. 扫私有堆找 section 记录的 48 字节签名（`0x6e6c` 处两个连续 section 记录）
2. 算基址 `命中地址 - 0x6e6c`
3. 用第一个目标的当前值复验（`14304 == 14304`），排除撞巧
4. 把 4 级 LOD × 13 个 section 的 `NumIndices` 写成 0，每次写完回读确认

探针日志（`%LOCALAPPDATA%\CowboyBingus\Helldivers2\ArmorRegistry\section-toggle.status`）：

```
probe start
signature offset 0x6e6c
target count 52
update callback installed
signature hit 0x21369396eec
candidate base 0x21369390080 first target current=14304 expected=14304
wrote 52/52 numIndices to zero
APPLIED
```

游戏未崩溃，进程继续运行。**但画面完全没变化，胸部躯体照常显示。**

## 对照

同样这 52 个字段，**改 patch 文件**（`TocDataOffset + section.Offset + 16`）后重启游戏，
胸部躯体消失。见 `notes/numindices-is-the-visibility-switch-20261017.md`。

## 结论

> **引擎在资源加载时把 section 表解析成自己的绘制结构，之后不再回读 TOC。**
> 内存里那份 TOC 副本（与文件 99.19% 相同）只是原始资源字节，没有代码再读它。

因此「运行期写 `NumIndices`」在 TOC 层面**无效**。

## 仍未排除的一条便宜路径

那份内存副本**已经被我们改过**。如果换装触发的是「重新解析这份缓冲」而不是
「重新从文件读」，那么触发一次重载就能让改动生效。

* 若成立 → 可行路径变成「改内存 + 触发重载」，仍然不需要重启游戏
* 若不成立 → 必须找到引擎自己的绘制结构，难度高一个数量级

## 工程上留下的东西

* `tools/unitprobe` —— 离线读 patch、定位 unit/section、就地改字段、产出 section 映射
* `scripts/find_unit_toc.py` —— 外部只读定位 TOC 副本（含三个扫描器 bug 的修复）
* `scripts/build_lua_mod.py` —— 把 Lua 打包成可导入的 mod zip（照抄 `LuaDeploymentService`）
* `scripts/make_section_toggle_probe.py` —— 生成运行期开关探针
* MDL 作为 Lua 加载器已部署，探针位于
  `%LOCALAPPDATA%\MDL\Helldivers2\Mods\section_toggle_probe\mod.lua`

## MDL 沙箱的两个坑（值得记）

1. **装帧回调必须用普通赋值 `update = fn`**。MDL 用环境代理的 `__newindex` 捕获
   `update`/`shutdown` 的赋值；`rawset` 会绕过代理，MDL 就报
   "installed no update callback"。
2. **MDL 改写了 `rawset`**（`env.rawset` 会标记 `mod.globals`），
   所以 `rawset(_G,...)` 实际写的是真实全局表，可能顶掉别的 addon 的钩子。
