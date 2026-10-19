# 衣柜 / section 掩码的离线验证脚本

这五个脚本是 `HD2RuntimeArmorOverride/src/armor_section_*.lua` 与
`armor_wardrobe_masks.lua` 的**可复现证据**。它们不需要游戏，用 LuaJIT 直接跑：

```powershell
$lj = "C:\Users\Administrator\AppData\Local\Programs\LuaJIT\bin\luajit.exe"
Get-ChildItem HD2RuntimeArmorOverride\checks\*.lua | ForEach-Object { & $lj $_.FullName }
```

全部通过时每个脚本都会打印 `checks=N failures=0` 和一行 `*_OK`。当前合计 125 项。

| 脚本 | 覆盖 |
|---|---|
| `wardrobe-masks-check.lua` | `wardrobe.masks` 文法、拒绝项、掩码集/目标语义（与 C# `WardrobeMaskFileTests` 同一组用例） |
| `section-layout-check.lua` | TOC 走查：结构头、mesh 表、节表、材质表、指纹 |
| `section-apply-check.lua` | 写入计划、隐藏、恢复、预检拒绝、写到一半失败后的整体回滚 |
| `section-locate-check.lua` | 按 FileId 定位副本：误命中拒绝、坏副本跳过、缓存复验与失效 |
| `section-publish-check.lua` | 编排：多目标合并、冲突检出、缺失 unit 跳过、目标→unit 推导 |

## 样本依赖

`section-*.lua` 需要一份**真实 unit TOC 切片**作为输入：

```
E:\Data\source\repos\WpfApp1\tmp\deployed-unit.toc
```

它是从当前部署的 patch 里切出来的那个 unit 的 TOC，前 8 字节被填成 unit 的
FileId（用来模拟内存副本与文件唯一的差异）。生成方式见
`notes/section-toc-memory-header-differs-only-in-unit-id-20261019.md` 的"复现"一节；
`tmp/` 是 gitignored 的，所以样本不入库。

样本缺失时这些脚本会直接报错退出——这是有意的：它们验证的是**真实结构**，
用合成数据跑通不能说明任何问题。
