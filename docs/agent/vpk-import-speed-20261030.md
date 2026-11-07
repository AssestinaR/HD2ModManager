# VPK 导入速度：时间都花在哪（实测）

> 本机：Ryzen 9 7950X3D（16C/32T）、63 GB、E: 盘 NVMe。
> 样本：`【Left 4 Dead 2 】影鸢女武神shinano—8人包.zip`（660 MB → 19 个 VPK / 2.45 GB）。
> 用户日志 `manager_20261030.log`：16:07:27 → 16:12:30，`Import.ProcessItem elapsed=299128ms`。

## 1. 整批 299 秒的构成

| 阶段 | 秒 | 备注 |
|---|---:|---|
| 7z 解压（19 个 VPK，2.45 GB） | 8.1 | 一次，可接受 |
| 规划 `vpk2patch --dry-run` | 2.7 | 19 个包，便宜 |
| 转换 5 个（**串行，每包一个进程**） | 285 | 全部成本在这里 |
| 建库 + 索引刷新 | ~4 | |

5 个转换的明细：

| 包 | 秒 | 主导项 |
|---|---:|---|
| bill（模型，21w 面，425k 顶点） | 42 | Unit 作者 18.3 + 材质解析 11.0 + 重定向 8.1 |
| coach（模型） | ~30 | 同上，顶点少一半 |
| **4K 清晰度材质包** | **141–157** | texconv 131.9（248 次调用） |
| **2K 清晰度材质包** | **76** | 同上 |
| 基础材质包 | 3 | 材质少 |

**结论：材质包（4K+2K）占 233 秒，模型占 72 秒。** 一个八人包里 14 个包被去重跳过，真正干活的只有 5 个。

## 2. 模型包 42 秒的明细

| 步骤 | 秒 |
|---|---:|
| 批量索引 19 个 VPK（`SourceMaterialSource.Open`） | 1.3 |
| 读 mdl + 几何 | 0.25 |
| avatar 骨架 + rig | 0.2 |
| **重定向 `SourceRetargetPipeline.Run`** | **8.1** |
| 切分部位 | 0.2 |
| 装备表 + 外壳 Unit | 0.4 |
| **材质 id 解析（57 个材质，一个都不写进 patch）** | **11.0** |
| **7 个 Unit：建网格 2.2 + 写 Unit 18.3** | **20.5** |
| 写 patch（112 MB `.gpu_resources`） | 0.12 |
| sections.json + 素体登记 + JSON | 1.3 |

Unit 作者内部（18.3 秒）：建 raw mesh 3.8 / 骨骼调色板 6.6 / 顶点编码 7.4 / 流契约 1.5 / 重建 0.4。

材质 id 解析的 11.0 秒里，**9.0 秒是 texconv**：57 次 `R8G8B8A8_UNORM` 解码，只为了量 alpha 决定用 `advanced` 还是 `alphaclip` 模板——而模型 patch 一个材质都不写，纯粹是为了"Unit 要引用正确的 id"。

## 3. 材质包 141 秒的明细

- texconv 共 **248 次调用 / 131.9 秒**（占 93%）
- 其中 **124 次是同一批纯色贴图的重复编码**：`shared_flat_normal_`、`shared_pbr_`、`*_metallic`、`*_nar`，
  每个材质各编一遍（57 个材质 × 2~4 张），**27 秒纯浪费**——它们按名字去重后最终只写进包一份。
- 剩下 105 秒是 62 张真贴图 × （解成 RGBA + 编成 BC7）。4K 底色一张约 0.7 s 解 + 2.1 s 编。
- 我们自己的代码只占 ~8 秒（读 VPK、拼 TOC、写 624 MB 侧车）。

**texconv 是单张图单线程**，所以这一段的并行度是现成的：16 核 → 理论 105/16。

## 4. 为什么界面永远显示"解压缩中"

`ShellViewModel.ProcessImportQueueAsync`：

```csharp
task.MarkRunning("正在解压缩");
var created = await import.ImportPathAsync(...);
```

`ImportService` 里 `new ImportService(_libraryService, onInfo: null, ...)` —— **`onInfo` 是 null**，
而 `VpkImportService.ConvertAsync(source, stage => _onInfo?.Invoke(stage), ct)` 的每条进度都从这里丢掉了。
所以除了开头那一句，任务条再也不会变。这跟速度无关，是纯粹的接线缺失。

另外 `TryPrepareAsync`（解压 8 秒）根本没有进度回调，CLI 也没有任何细粒度进度输出。

## 5. 优化方案（按收益排序）

| # | 动作 | 省下（整批） | 风险 |
|---|---|---:|---|
| 1 | 纯色贴图记忆化（按名字+颜色缓存 BC7 字节） | ~40 s | 极低 |
| 2 | texconv 并行（按贴图，`Parallel.ForEach`） | ~90 s | 低（编码器无状态） |
| 3 | Unit 作者并行（7 个 Unit 互不相干） | ~15 s | 低（`UnitAuthor` 每线程一个） |
| 4 | 材质 alpha 量测并行 + 同底色去重 | ~8 s | 低 |
| 5 | 重定向并行（`SkinAll` 逐顶点） | ~6 s | 中（涉及多条逐顶点管线） |
| 6 | 跨包并行（管理器同时跑 2~3 个转换进程） | ~20 s | 中（内存 1–2 GB/进程） |

只做 1–4：**299 s → 约 55 s**（模型 42→9，4K 141→18，2K 76→12）。
再加 5–6：约 35 s。

## 6. 进度/体感方案（已实施）

1. `ImportService` 的 `onInfo` 以前传的是 **null**，`VpkImportService` 的每条 stage 都从这里丢掉了。
   现在新增 `onProgress(text, fraction)`，接到 `task.UpdateStage` + `task.UpdateProgress`。
2. `TryPrepareAsync` 加阶段回调：`正在解压 xxx（19 个 VPK）`。
3. CLI 在 stderr 上打结构化进度行 `#progress <阶段键> <已完成> <总数> <文案>`（`VpkProgressLine`，
   两端共用一个类型，格式不会漂）：
   - `queue`：`正在转换 2/5：xxx.vpk`（分母来源）
   - `materials` / `textures` / `units`：`材质 24/57`
4. 管理器把两层计数合成一个百分比：`(队列已完成 + 包内比例) / 队列总数`。
   任务条上有 `MainWindow.xaml` 里那条 3px 进度条（`MessageCenterItem.HasProgress/Progress`），
   文案进 `StatusText · Stage` 那一行。

## 7. 第一轮实施结果（1–4：纯色图缓存 + 三处并行）

| 包 | 之前 | 之后 | 倍数 |
|---|---:|---:|---:|
| bill（模型，425k 顶点） | 42.0 s | 27.5 s | 1.5× |
| coach（模型） | ~30 s | 15.4 s | 2.0× |
| 基础材质包 | 2.9 s | 1.6 s | 1.8× |
| 2K 材质包 | 76.3 s | 18.4 s | 4.1× |
| 4K 材质包 | 141.0 s | 55.5 s | 2.5× |
| **转换合计** | **285 s** | **118.4 s** | **2.4×** |

整批（含解压 8 s + 规划 3 s）：**299 s → 约 130 s**。

**正确性：逐字节相同。** 同一份输入、复用 `custom-model.json` 里的 unit id 之后：
- 4K 材质包：`patch_0` 与 655 MB `.gpu_resources` 哈希一致；
- bill 模型：`patch_0`、112 MB `.gpu_resources`、`custom-model.json`、`sections.json` 四份哈希全一致。

### 做了什么

1. **纯色贴图记忆化**（`SourceMaterialConverter.solidTextures`）：`shared_flat_normal_` / `shared_pbr_` /
   `*_metallic` / `*_nar` 原来是每个材质各编一遍，4K 包 248 次 texconv 里有 124 次是重复的。
   键是"名字 + 四个字节"，命中就复用同一份产物。
2. **材质解析/合成并行**（`VpkPatchAssembler.ResolveAndEmitMaterials`）：按 stem 并行，
   结果按下标收集、按原顺序收口，`skipped` / `warnings` 的次序不变。
3. **Unit 作者并行**：unit id 与层号仍然串行分配（顺序决定可复现性），建网格 + 写 Unit 并行。
4. **纯贴图包并行**：第 ④ 段逐张贴图转换同理。
5. **并行度封顶**（`VpkParallel`）：线程池会因阻塞而不断注入线程，实测跑到 33 个并发 texconv；
   现在默认 `逻辑核/2`（= 物理核），可用 `HD2_VPK_PARALLEL` 覆盖。

### 剩下的墙：texconv 受内存带宽限制

同一张 4K 图单独编 2.45 s；4 路并发时每次 1.82 s、8 路 1.55 s、12 路 1.45 s
——**总吞吐只提升 1.7 倍就封顶了**。所以 4K 包再怎么调并行度都在 50 s 附近
（32 路 51.4 / 16 路 50.2 / 8 路 58.1 / 4 路 63.5）。

想再往下只剩两条路，都不在这次的范围内：
- **自己解 DXT/BC7 → RGBA**（替掉 62 次解码调用，并省掉 64 MB 的中间文件往返）：
  估计 4K 包 55 → 40 s。BC7/DXT 解码是规范精确行为，可与 texconv 逐字节对拍，但工作量不小。
- **换编码器**（GPU 侧 BC7）：数量级的差别，但是另一套工具链。

