# 材质线回退到 2026-11-07 23:00 之前

> 2026-11-08 19:45。用户要求停止「镂空 + 金属」探索，把材质相关改动退回 11-07 23:00 之前。

## 一、提交记录核对（确认时间点）

```
8654042  11-07 22:59  chore(release): 1.5.1 —— 修材质丢失、转换器栈溢出     ← 23:00 前最后一个提交
6ce9bb1  11-07 23:18  feat(tools): 建立 HD2 材质能力矩阵（只加工具与文档）
1fd7391  11-07 23:18  docs(shaders): 根材质与 SDK 模板变量表差异（只改文档）
3e21935  11-08 09:39  feat(材质): cutoutmetal 档位 + 派生贴图 id 撞车修复    ← 第一个改行为的
7e36fa0  11-08 13:41  fix(材质): 模板必须带 undat2=0
81e613c  11-08 13:50  fix(材质): 模板改为克隆真实子材质 + 第 4 槽必须填
b5ad735  11-08 14:01  fix(材质): cutoutmetal 的底色 alpha 当不透明度
ed754da  11-08 14:37  feat(材质): 第二个候选 cutoutmetal2
```

结论：材质线确实从 11-07 23:18 开始（先加工具/文档），**改转换行为的第一个提交是 11-08 09:39 的 `3e21935`**。
用户口径「还原回 11-07 23 点前」= 把这 5 个提交带来的改动全部撤掉。

## 二、回退范围（按文件核对过归属）

| 文件 | 8654042 以来的提交 | 处理 |
|---|---|---|
| `HD2SourceMigration/VpkConversion/SourceMaterialConverter.cs` | 只有材质 4 个提交 | **整文件回到 8654042** |
| `HD2SourceMigration/VpkConversion/Hd2MaterialTemplateLibrary.cs` | 只有材质 2 个提交 | **整文件回到 8654042** |
| `HD2ModCore/Infrastructure/VpkConversionReport.cs` | 只有 `ed754da` | **整文件回到 8654042** |
| `HD2SourceMigration/VpkConversion/Hd2MaterialAsset.cs` | `ed754da` + 别人的 `0e2b05a` | **只删我加的两行 KnownParentIds** |
| `HD2ModCli/CliArguments.cs` | 材质 + 别人的 `249b93f`/`0e2b05a` | **只删 cutoutmetal/cutoutmetal2 两个取值** |
| `HD2ModCli/CliApplication.cs` | 材质 + 别人的 `81a4872`/`249b93f`/`e18e636`/`0e2b05a` | **只删 cutoutmetal2 映射与 metalCutout 回显** |
| `tools/ref/.../materials/cutoutmetal{2}.material` | 未跟踪 | **删除** |
| `tools/ShaderMatrix/Program.cs` | 工作区未提交增量 | **丢弃**（回到已提交版本） |
| `docs/agent/*`（矩阵、AB 文档） | `6ce9bb1`/`1fd7391`/`4afaaf5` 等 | **保留**：纯文档/工具，不影响转换行为 |

## 三、回退之后必须做的一步

代码回去之后，Mod 里的 `sectional/` 与 `Overwrite/` 仍然是新版转换器的产物（含 cutoutmetal2 材质）。
所以回退完要**用 `mod rebuild` 重新生成一次载荷**，让盘上产物和代码一致。

## 四、验收

1. `dotnet build -c Debug` 0 警告 0 错误；
2. 材质父级分布回到：`alphaclip`（镂空件）+ `advanced`（其余），不再出现 `cutoutmetal*`；
3. `RequiredTemplates` 不含 `cutoutmetal*`（否则缺模板会让整批材质失败）；
4. 全量测试：除已知 5 条 `DeploymentCliTests`（改动前就失败）外全绿。
