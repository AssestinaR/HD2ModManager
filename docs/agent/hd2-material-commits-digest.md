# 材质 / 着色器相关提交汇总（自动生成）

> 由 `.dsh/tmp/git_material_digest.py` 生成；收录判据：提交信息或改动路径命中材质关键词。
> 仓库：`E:\Data\source\repos\WpfApp1`；共 **197** 条。

## 目录
- 其它材质相关（128）
- 材质模板（10）
- 金属 / Metallic（21）
- 半透明 / Opacity（4）
- 镂空 / AlphaClip（25）
- 自发光 / Emissive（3）
- 文档与结论（6）

## 其它材质相关

| 日期 | 提交 | 标题 | 材质相关文件 |
|---|---|---|---|
| 2026-11-13 | `de430b6a` | feat(vpk): UV 整格归位加 A/B 退路（HD2_NO_UV_TILE=1）；补跨整数边界的边界用例 | UvTileNormalizer.cs<br>VpkPatchAssembler.cs |
| 2026-11-13 | `bdc12c66` | fix(vpk): 源模型整格平移过的 UV 归位回 0..1，修复游戏里整片面消失（#23） | UvTileNormalizer.cs<br>VpkPatchAssembler.cs |
| 2026-11-13 | `79dd173f` | fix(vpk): 源模型整格平移过的 UV 归位回 0..1，修复游戏里整片面消失 | UvTileNormalizer.cs<br>VpkPatchAssembler.cs |
| 2026-11-12 | `37c58a3b` | fix(vpk): 灰度贴图解不出来时不留「宽高有值、像素为空」的半成品（#20） | SourceMaterialConverter.cs |
| 2026-11-12 | `fdb5cecb` | feat(physbone): 转换器按件切物理骨 + 运行时对接（2.1.39） | ExtraBoneSubset.cs<br>VpkPatchAssembler.cs |
| 2026-11-11 | `37201a25` | feat(physbone): 离线诊断（容器 / rig / 链 / 锚点）+ 容器读取与合并 | PhysBoneContainerEmbedder.cs |
| 2026-11-10 | `34b80014` | 修正：头盔件必须是 body=Any，否则整份素体注册表读不出来 | VpkPatchAssembler.cs |
| 2026-11-10 | `678fcd3c` | fix(vpk): I8 灰度贴图解不出来时别留下「宽高有值、像素为空」的半成品 | SourceMaterialConverter.cs |
| 2026-11-09 | `aa751673` | W2b-1 白名单闭包：unit → 材质 → 贴图 | — |
| 2026-11-09 | `d402c8b6` | feat(convert): 裙摆离体清空（几何方案 A 的核心，默认关） | SkirtClearance.cs<br>VpkPatchAssembler.cs |
| 2026-11-09 | `5544e7a9` | fix(vtf): 多帧 VTF 每层只暴露第 0 帧，避免贴图被退回导致材质退成兜底灰 | VtfFile.cs |
| 2026-11-09 | `f5eed169` | fix(vrd): driver 骨成为真节点、父骨/控制骨换成导出名 —— 从「骨=8」推进到「父=8」 | PhysBoneDriverRigSidecar.cs |
| 2026-11-09 | `4d1c5c50` | fix(physbone): driver 侧车被「采集」这一步丢掉 —— 容器永远只有一条记录 | PhysBoneContainerEmbedder.cs |
| 2026-11-09 | `849b4a46` | fix(贴图): 支持 VTF 7.0/7.1（depth 字段从 7.2 才有）—— 修「老包整批导入失败」 | SourceMaterialConverter.cs<br>VtfFile.cs |
| 2026-11-09 | `8814d1a7` | feat(physbone): driver rig 侧车 + 容器第二条记录 + 转换期产出 | PhysBoneContainerEmbedder.cs<br>PhysBoneDriverRigSidecar.cs |
| 2026-11-08 | `ae38eaa2` | chore(release): 1.5.4 —— 修真映射改坏护甲值/缺部位，修材质红白过曝 | — |
| 2026-11-08 | `7f0f535e` | fix(材质): lightwarp 烘焙没有世界法线时也要跳过（默认改为关闭）—— 修 Manuka「侧面红斑、正面过白」 | SourceMaterialConverter.cs |
| 2026-11-08 | `0d5b9913` | revert(材质): 材质线退回 2026-11-07 23:00 之前 | Hd2MaterialAsset.cs<br>Hd2MaterialTemplateLibrary.cs<br>Hd2MaterialTemplateTests.cs<br>SourceMaterialConverter.cs |
| 2026-11-08 | `bbe1c4cd` | chore(release): 1.5.2 —— 修复 sectional 重构带来的导入/材质/编译/详情页一批问题 | — |
| 2026-11-08 | `661bf1bc` | feat(materials): 没有显式选择时默认启用全部材质 patch（部署与展示同一口径）；顺手把'唯一写者'的过时注释改成真实情况 | — |
| 2026-11-08 | `ee27857c` | fix(sectional): 材质选项终于有名字 —— 报告要跟着平铺后的编号改，清单要等报告落地再写 | — |
| 2026-11-08 | `0e2b05ac` | fix(sectional): 导入按「一个自洽条目」转、重构建收集全部产出、旧库归拢只搬 VPK + 重构建 | Hd2MaterialAsset.cs |
| 2026-11-07 | `8654042f` | chore(release): 1.5.1 —— 修材质丢失、转换器栈溢出 | — |
| 2026-11-07 | `b2949246` | fix(vpk): 恒等换甲（目标=自己的素体）走物化器 —— 生效层终于带上模型自带的材质 | — |
| 2026-11-07 | `291fe50b` | fix(deploy): 没被物化过的 `Overwrite` 不许独占整层 —— 老库的材质就是这么丢的 | — |
| 2026-11-07 | `c873ce27` | fix(convert): 眼球投影不再在循环里 stackalloc —— 12 万三角形的模型当场栈溢出 | EyeUv.cs |
| 2026-11-07 | `d2332d45` | feat(vpk): 头身合并在**注册表**落地——扫空位、留痕、无空位明确报错（不动 patch） | VpkPatchAssembler.cs |
| 2026-11-07 | `1b60523c` | fix(import): 占位几何的提示重新变得可达 —— 24fb173 收紧 VVD 校验后它永远走不到 | VpkImportPlanner.cs |
| 2026-11-06 | `3bd4fdca` | feat(sectional): 按材质 id 在库里自动拾取配套材质 —— 真库验证通过 | VpkConversionReport.cs |
| 2026-11-06 | `4754eb8e` | fix(material): 去掉自己加的卡片状态行，并接上重新部署 | ModDetailsPageViewModel.Materials.cs |
| 2026-11-06 | `9012a753` | feat(material): 材质选项接上真过滤 | ModDetailsPageViewModel.Materials.cs |
| 2026-11-06 | `282335ab` | feat(material): 材质选项列表——照抄详情页那张 modlist 卡的做法 | ModDetailsPageViewModel.Materials.cs |
| 2026-11-06 | `4b03927b` | revert(details): 撤掉按自己理解做的材质卡片，只留逻辑层（UI 交给专门的会话） | ModDetailsPageViewModel.MaterialOptions.cs |
| 2026-11-06 | `d9f46d04` | feat(cli): `mod material order` + `mod show` 跟着顺序走 —— 顺序影响部署，已实测 | — |
| 2026-11-06 | `a8d9c06e` | feat(details): 详情页加「材质」卡片 —— 勾选、排序、改名、备注、配图、打开文件夹，没有删除 | ModDetailsPageViewModel.MaterialOptions.cs |
| 2026-11-06 | `a0552d1a` | feat(cleanup): 退役材质包的空壳由应用自动收掉，不需要用户自己判断能不能删 | — |
| 2026-11-06 | `415b2a56` | feat(manifest): 老库补一份 patches.json，并修正材质 patch 的角色判定 | — |
| 2026-11-06 | `f962c54e` | fix(material): 改材质选择不再把换甲产物换回未换甲的原版 | — |
| 2026-11-06 | `0c31ccf2` | feat(material): 材质选项可切换了 —— 设置存在 mod 文件夹里，改完立刻重新物化 | — |
| 2026-11-06 | `5705eab3` | feat(deploy): 部署索引改用"会进游戏的文件"判定 —— 未启用的材质不再跟着部署 | — |
| 2026-11-06 | `6c6e0747` | feat(fold): `library material-fold apply` —— 折并 + 物化 + 退役一条命令，真库副本已验证 | — |
| 2026-11-06 | `a929ea05` | refactor(p3): 材质选项改回"多选 + 每个选项一个 patch"，并删掉我自己加的四处防御 | — |
| 2026-11-06 | `93f94753` | fix(overwrite): 生效层必须带上"宿主自己的基底材质" —— 否则恋星milfy 折并后会黑模 | — |
| 2026-11-06 | `2f2dbf83` | feat(cli): `library material-fold plan` —— 折并计划可自查，且"保留哪一份"按生效顺序算 | — |
| 2026-11-06 | `c233c08e` | feat(overwrite): OverwriteMaterializer —— 生效层 = unit patch + 选中的那一份材质 patch | — |
| 2026-11-06 | `f439ba9f` | feat(fold): 材质 Option 折进宿主的只读计划 —— 真库试算 43 个成员、0 个阻断 | — |
| 2026-11-06 | `ed4a1788` | refactor(layout): 存储形态收敛成一处判定 + 基底目录自带 patches.json 清单（P2-1 / P2-4） | VpkConversionReport.cs |
| 2026-11-06 | `a42a08ea` | refactor(layout): vpk2patch/ 改名为 sectional/ —— 读路径同时认新旧两个名字 | PhysBoneContainerEmbedder.cs<br>PhysBoneRigSidecar.cs |
| 2026-11-05 | `2bbb0f67` | diag(unit): 空调色板不再伪装成「jiggle 骨太多」——点名到网格 / LOD / 顶点数 | UnitAuthor.cs |
| 2026-11-05 | `654ade8a` | revert(材质): 撤掉 11-05 下午的四轮外观改动，材质管线回到 3ab77db 基线 | MaterialAppearance.cs<br>SourceMaterialConverter.cs<br>UvNormalMap.cs |
| 2026-11-05 | `3502e11d` | fix(材质): 平铺 UV 也能烘世界法线；材质包没有几何时从解析集合里找模型 | SourceMaterialConverter.cs<br>UvNormalMap.cs |
| 2026-11-05 | `a350c0ad` | fix(材质): 派生贴图按**材质**命名 —— 修「兄弟包共用同一张 F0，引擎只采信其中一个」 | SourceMaterialConverter.cs |
| 2026-11-05 | `c780f826` | feat(材质): 瞳孔锚点 + 逐眼镜像两个开关（都默认关闭，等实机定） | EyeUv.cs<br>SourceMaterialConverter.cs |
| 2026-11-05 | `ccf99a2a` | feat(材质): 接上 $dilation（瞳孔放大） | EyeUv.cs<br>SourceMaterialConverter.cs |
| 2026-11-05 | `db86e971` | fix(材质): 眼球投影的"右/上"改为从骨架推 —— 不再靠"最薄的那一维"赌朝向 | EyeUv.cs<br>VpkPatchAssembler.cs |
| 2026-11-04 | `f1771f3e` | fix(材质): 眼球的"横/竖"轴从数据里推 —— 这个模型的左右是 X，不是 Y | EyeUv.cs |
| 2026-11-04 | `b1d78716` | fix(材质): 眼球 UV 改成各向同性 —— 修掉「眼睛被纵向拉长」 | EyeUv.cs |
| 2026-11-04 | `da69406d` | feat(材质): $no_draw 默认改为「照常显示」—— 藏不藏由用户在 section 编辑器里决定 | SourceMaterialConverter.cs<br>VpkPatchAssembler.cs |
| 2026-11-04 | `6fc710df` | fix(材质): $no_draw 不再连几何一起删 —— 保留几何、材质做成「不显示」 | SourceMaterialConverter.cs<br>VpkPatchAssembler.cs |
| 2026-11-04 | `5895b02c` | fix(材质): 眼球不再是灰球 —— 认 $iris 取色，并按眼球位置重建 UV | EyeUv.cs<br>SourceMaterialConverter.cs<br>VmtDocument.cs<br>VpkPatchAssembler.cs |
| 2026-11-04 | `f7c52f10` | feat(report): 法线的三类结果分开计数 —— 「复原过 / 贴图不在 / 真的不是法线」 | SourceMaterialConverter.cs<br>VpkPatchAssembler.cs |
| 2026-11-04 | `0111e176` | fix(material): 那些"歪掉的法线图"是被压过 gamma 的 —— 按线性→sRGB 复原，而不是丢掉 | SourceMaterialConverter.cs |
| 2026-11-04 | `e2b59abd` | fix(material): 法线图先验"能不能用"—— 均值离 0 太远就按无起伏处理（Chiyu 的暗斑根因） | SourceMaterialConverter.cs |
| 2026-11-04 | `fc1892ce` | revert(material): 撤掉"按文件名判 OpenGL 法线"—— 实机比不翻更奇怪 | SourceMaterialConverter.cs |
| 2026-11-04 | `6d6f7434` | fix(material): lightwarp 改按**卡通阴影曲线**烘进底色 —— 上一版"整片乘 0.30"把身体压成深灰 | MaterialAppearance.cs<br>SourceMaterialConverter.cs |
| 2026-11-04 | `63bde96d` | perf(material): 贴图按内容去重（解码 + BC7 编码），同一包转换 38s → 17.7s | SourceMaterialConverter.cs |
| 2026-11-04 | `3740238e` | feat(material): 接上源法线图（$bumpmap）—— NekoToon 模型不再一律平面法线 | MaterialAppearance.cs<br>SourceMaterialConverter.cs<br>SourceMaterialConverterTests.cs<br>VpkConversionReport.cs |
| 2026-11-03 | `2e1b9749` | fix(rebuild): 材质数守门改成「按 VPK 对 VPK」比较 —— 多 VPK 的旧报告会让某一包永远重建不了 | VpkConversionReport.cs<br>VpkConversionReportTests.cs |
| 2026-11-03 | `761f6e02` | feat(report): 转换报告记下「生成它的转换器构建时间」，并让重构建对话框标出过期的选项 | VpkConversionReport.cs<br>VpkConversionReportTests.cs |
| 2026-11-03 | `02b3393c` | feat(report): 按整包统计「声明了法线图但尚未接入」，把最大的一处保真度损失讲出来 | SourceMaterialConverter.cs<br>VpkPatchAssembler.cs |
| 2026-11-03 | `b346bd53` | feat(report): 材质总账补上「兜底占位」这一类，并让管理器真正读它 | VpkPatchAssembler.cs |
| 2026-11-03 | `8dc08d8d` | fix(vpk): `$no_draw` 的部件连几何一起不写——紫块的直接成因消除 | VpkPatchAssembler.cs |
| 2026-11-03 | `d0e04864` | feat(vpk): 转换报告给出「材质总账」——模型要多少、落地多少、缺的是哪两类 | VpkPatchAssembler.cs |
| 2026-11-03 | `66a9762c` | feat(model): 读 mdl 的材质目录表（cdtextures），给出"模型需要哪些材质"的权威全路径 | — |
| 2026-11-02 | `11af1293` | fix(vpk): 两个都带真几何的模型包不再互相降级成材质包 | VpkImportPlanner.cs |
| 2026-11-02 | `4d6b0e6c` | fix(rebuild): 「重构建」现在会刷新 vpk2patch.json | SourceMaterialConverter.cs |
| 2026-11-02 | `edd0abc4` | fix(material): 底图缺失时的兜底色按贴图名取（black→黑 / white→白 / gray→中灰） | MaterialAppearance.cs<br>SourceMaterialConverter.cs |
| 2026-11-02 | `4505ae66` | fix(material): 底图缺失不再让材质整块失败，退成中性灰并报告 | SourceMaterialConverter.cs |
| 2026-11-02 | `9a41e045` | fix(material): 修好 MourningVeil 暴露的三类通用材质缺陷 | MaterialAppearance.cs<br>SourceMaterialConverter.cs<br>TextureAlpha.cs |
| 2026-11-02 | `bc405837` | feat(dither): 抖动阈值换成交错梯度噪声，消掉"点排得太整齐"的方格感 | DitherPattern.cs<br>SourceMaterialConverter.cs |
| 2026-11-02 | `a7184445` | tune(dither): 点回到 1 像素、新增密度旋钮（默认 0.75），薄纱更透 | SourceMaterialConverter.cs |
| 2026-11-02 | `5bff6b6c` | fix(material): 材质 id 改按**材质名**派生，修好"换颜色变体就品红" | SourceMaterialConverter.cs |
| 2026-11-02 | `31154c95` | feat(vpk): 整合包按"模型主体"分组转换——每主体独立命名空间与解析集合 | VpkImportPlanner.cs |
| 2026-11-02 | `e42598e1` | feat(vpk): 导入时自动把材质包接成模型的选项；转换不再把 include 中间层报成失败 | SourceMaterialConverter.cs |
| 2026-11-02 | `523b2781` | feat(vpk): 重构建按钮、命名空间警告、换甲时的头身合并 | VpkConversionReport.cs<br>VpkConversionReportTests.cs |
| 2026-11-01 | `99629461` | 换甲改外层优先修掉丢手套/丝袜；真映射兼容自定义素体的件表铺法 | Hd2MaterialAsset.cs<br>MaterialAppearance.cs<br>SourceMaterialConverter.cs<br>SourceMaterialConverterTests.cs |
| 2026-11-01 | `641a100a` | feat(physbone): 让「这个 mod 没有物理」在导入报告里说出来 | PhysBoneContainerEmbedder.cs |
| 2026-11-01 | `0416f655` | fix(physbone): 把链根锚点名写进容器，修掉 22 条永远挂不上的死链 | PhysBoneContainerEmbedder.cs<br>PhysBoneRigSidecar.cs |
| 2026-11-01 | `5c2dc76e` | feat(physbone): 把 jiggle 骨物理参数从来源一路送到游戏里的运行时 | PhysBoneContainerEmbedder.cs<br>RetargetedMeshBuilder.cs<br>UnitTransformInfoAppender.cs<br>VpkPatchAssembler.cs |
| 2026-10-30 | `0097c895` | VPK 导入：进度条不再永远停在"解压缩中"，细到"材质 24/57" | VpkPatchAssembler.cs |
| 2026-10-30 | `9a41b43d` | VPK 转换：贴图/Unit 三处并行，同一份八人包从 285 秒降到 118 秒 | SourceMaterialConverter.cs<br>VpkParallel.cs<br>VpkPatchAssembler.cs |
| 2026-10-30 | `25684cdd` | 组件开关：换贴图型的开关单独报出来，不再产出一个空壳 Mod（B6） | VpkImportPlanner.cs |
| 2026-10-30 | `1a954d0f` | 组件开关：section 带上名字、识别 $no_draw 开关包、翻译成掩码（B1–B5） | VpkImportPlanner.cs<br>VpkPatchAssembler.cs |
| 2026-10-30 | `6fb64f60` | VPK 转换：材质/贴图 id 加来源命名空间（A0–A3） | Hd2AssetId.cs<br>SourceMaterialConverter.cs<br>SourceMaterialConverterTests.cs<br>VpkPatchAssembler.cs |
| 2026-10-28 | `795db798` | VPK 导入报告：占位几何不再甩一句 "VVD magic 不是 IDSV" | VpkImportPlanner.cs |
| 2026-10-28 | `42da2c6c` | VPK 转换：修掉三类真实故障（无材质 / 打不开 / 爆栈） | RetargetedMeshBuilder.cs<br>VmtDocument.cs |
| 2026-10-27 | `a0843a4f` | VPK 规划：单独导入材质包时，理由要说清"少了模型包" | VpkImportPlanner.cs |
| 2026-10-27 | `e61961e3` | VPK 导入：坏模型不再让整包失败，产出目录不再被当成来源重转 | VpkImportPlanner.cs |
| 2026-10-25 | `fe11c469` | chore: 删除误入版本库的 tmp/rig-lab 临时文件 | _probe_matcap.py<br>_probe_mourning_matcap.py<br>_probe_mourning_matcap2.py |
| 2026-10-21 | `639e8029` | vpk2patch：把 VPK 来源接进管理器导入流程 | VpkImportPlanner.cs |
| 2026-10-19 | `7f5c3497` | section map: emit the v2 document, and fix a one-slot material offset | — |
| 2026-10-17 | `531a3025` | notes: material is not a section partition; decoration boundaries need an explicit marker | — |
| 2026-10-17 | `f1696cab` | notes: correct the section-toggle claim - sections are material batches, not decorations | — |
| 2026-10-17 | `ba6b654a` | vpk2patch: 一个 VPK 一个 patch，只含它自己的资源 | SourceMaterialConverter.cs<br>SourceMaterialConverterTests.cs<br>SourceMaterialSource.cs<br>VpkPatchAssembler.cs |
| 2026-10-16 | `d389050c` | source: 手臂辅助骨按宿主肢体分类，修掉腕部翻折的那层皮 | ForearmWeightReweight.cs |
| 2026-10-16 | `82e8786a` | source: 把 L4D2 -> HD2 的模型/材质管线移植进 C#（M1/M2/M3） | AlbedoAdjustment.cs<br>Bc7Encoder.cs<br>ForearmWeightReweight.cs<br>Hd2AssetId.cs |
| 2026-10-14 | `2672858b` | r74 cleanup: fix wrist-roll probe (material remap + LOD0 filter), finish library sync | — |
| 2026-10-14 | `064f38cf` | 第72轮：材质冲突/共存机制分析（MATERIAL_CONFLICT_ANALYSIS.md） | — |
| 2026-10-14 | `17b5d67b` | 第72轮：MourningVeil(Zoey) 替换 RE-2310 躯干包 —— 82条目/22单元(2承载+20隐藏) | _probe_mourning_matcap.py<br>_probe_mourning_matcap2.py |
| 2026-10-14 | `f0d3cfcf` | 第71轮：诊断起源贴图在HD2发灰发白（shader不同，非转换器bug）+ 实施albedo压暗实验版(mul 0.65)并装入交付目录 | — |
| 2026-10-14 | `8169c49c` | diag(rig-lab): 第71轮 「起源→HD2 发灰发白」归因 —— 不是转换器 bug，是 shader 不同 | — |
| 2026-10-13 | `036971bd` | feat(rig-lab): 第61轮 逐VPK材质拆分 + 蕾丝普查 + 覆盖生效性核对 | — |
| 2026-10-13 | `1a604ed4` | fix(rig-lab): 第60轮下半 材质贴图表P0修正 + 小臂刚性段重绑（M1已部署，W1离线就绪） | — |
| 2026-08-29 | `ff0d1a30` | reshader插件ini作为mod识别功能 | — |
| 2026-08-29 | `9e838053` | lua、reshader插件作为mod识别功能 | — |
| 2026-08-11 | `12e7250f` | 完善材质布局诊断与绑定修复 | — |
| 2026-08-11 | `6cf2b65a` | 替换护甲材质错位问题修复 | — |
| 2026-08-10 | `2fbb4379` | 替换护甲工作流材质绑定修复 | — |
| 2026-08-03 | `2a840ccf` | 材质顺延功能完善 | — |
| 2026-07-26 | `0bcc9e12` | 优化高级详情轻量材质与资产链路 | — |
| 2026-07-18 | `2bd4ec8c` | refactor: remove unused core material closure resolver | — |
| 2026-07-18 | `647be3c9` | checkpoint: before Core unit binary stack removal | — |
| 2026-07-15 | `01a2f45b` | feat: add material packaging workflows | — |
| 2026-07-12 | `faa171aa` | feat: 实现 TemporaryMaterialCombiner（测试成功） | — |
| 2026-07-12 | `466e9806` | feat: 实现 PatchMaterialDetector（检测器测试成功） | — |
| 2026-07-12 | `83c2147e` | design: 临时内嵌策略 - 材质分离模组更新方案 | — |
| 2026-07-12 | `5671a6b2` | refactor: 提取 MaterialMapper 核心组件 | — |
| 2025-10-03 | `7a6f3674` | 增强功能和用户界面，添加新选项支持 | advanced.material<br>alphaclip+.material<br>alphaclip.material<br>armorlut.material |

## 材质模板

| 日期 | 提交 | 标题 | 材质相关文件 |
|---|---|---|---|
| 2026-11-12 | `e4027c2c` | fix(vpk): 裸名 $bumpmap 是模板占位，不再按同名贴图顶替（#19） | SourceMaterialConverter.cs<br>SourceMaterialSource.cs |
| 2026-11-10 | `b3a91d06` | fix(vpk): 裸名 $bumpmap 是模板占位，不再按同名贴图顶替 | SourceMaterialConverter.cs<br>SourceMaterialSource.cs |
| 2026-11-09 | `05fb47d8` | feat(材质): 加一份从正版游戏子材质克隆的完整 advanced 模板（12 槽 / 77 变量） | Hd2MaterialTemplateLibrary.cs<br>Hd2MaterialTemplateTests.cs<br>SourceMaterialConverter.cs<br>advanced-full.material |
| 2026-11-08 | `015fa805` | fix(材质): 关掉 advanced 模板默认的次表面散射 —— 修「身体侧面特别红、正面特别白」 | SourceMaterialConverter.cs |
| 2026-11-08 | `7e36fa0a` | fix(材质): 模板必须带 undat2=0 —— 修「进游戏直接退出、事件查看器无崩溃报告」 | Hd2MaterialTemplateTests.cs<br>cutoutmetal.material |
| 2026-11-04 | `c9e8c233` | fix(material): 模板槽查询的缓存改成并发安全 —— 修掉「整批导入只有材质没有模型」 | Hd2MaterialTemplateLibrary.cs<br>Hd2MaterialTemplateTests.cs |
| 2026-11-03 | `64592040` | feat(material): 报出"源侧声明了但 HD2 表达不了"的特性；父模板 id 表改为可验证 | Hd2MaterialAsset.cs<br>Hd2MaterialTemplateTests.cs<br>MaterialAppearance.cs<br>SourceMaterialConverter.cs |
| 2026-10-28 | `642619e4` | 材质转换：支持 patch 型 VMT、模板内置、理由说真话 | Hd2MaterialTemplateLibrary.cs<br>Hd2MaterialTemplateTests.cs<br>SourceMaterialConverter.cs<br>SourceMaterialSource.cs |
| 2026-10-27 | `64117054` | 发布包补上材质工具链：texconv 与 .material 模板 | Bc7Encoder.cs<br>Hd2MaterialTemplateLibrary.cs |
| 2026-10-13 | `cee7c529` | fix(rig-lab): 第60轮 M4 —— 护甲材质模板错用（真正根因），改用 advanced（已部署） | — |

## 金属 / Metallic

| 日期 | 提交 | 标题 | 材质相关文件 |
|---|---|---|---|
| 2026-11-09 | `f78b42c3` | fix(材质): 「matcap 就是表面」的件底色改用 matcap 自己的平均色 —— 修「纯白像粉笔」 | SourceMaterialConverter.cs |
| 2026-11-09 | `d933476c` | feat(材质): 加 HD2_METAL_TINT_DEBUG 洋红探针 —— 判断某个金属件到底是不是我们的材质在画 | SourceMaterialConverter.cs |
| 2026-11-08 | `8134c941` | fix(材质): 加法 matcap 不再烘进底色 —— 修「脸和眼睛蒙了一层雾」 | MatcapLayerTests.cs<br>MaterialAppearance.cs<br>SourceMaterialConverter.cs |
| 2026-11-05 | `6dad5a2c` | revert(material): 只把 milfy 那条金属修复挑回来（材质名也算证据 + 平涂色不算花纹 + 金属保留自身色相 + 金属不叠 matcap） | MaterialAppearance.cs<br>SourceMaterialConverter.cs |
| 2026-11-05 | `17751d11` | fix(材质): 金属底色只往下压不往上抬 —— 电平端在 F0，不再被 matcap 的暗中心带成灰 | MaterialAppearance.cs |
| 2026-11-05 | `6ca81186` | fix(材质): 金属/宝石不再涂成一片 —— 把 matcap 的图案按 F0 抬成底色（修「白膜 / 灰膜」） | MaterialAppearance.cs<br>SourceMaterialConverter.cs |
| 2026-11-05 | `6ae76c6e` | feat(材质): 金属判定补两条 —— 材质名也算证据、平涂色不算花纹；金属底色不再被 matcap 二次覆盖 | MaterialAppearance.cs<br>SourceMaterialConverter.cs |
| 2026-11-05 | `3ab77db7` | fix(材质): 金属判据补上「matcap 是不是表面本身」；$no_draw 无底色的件不再画成洋红 | MaterialAppearance.cs<br>SourceMaterialConverter.cs<br>VpkPatchAssembler.cs |
| 2026-11-04 | `326176e0` | fix(material): 识别 OpenGL 法线图（作者自己标了的那类）+ half-lambert 光照曲线 + matcap 轴向 | MaterialAppearance.cs<br>SourceMaterialConverter.cs<br>UvNormalMap.cs |
| 2026-11-04 | `0933baec` | feat(material): 把几何烘进 UV 空间拿「世界方向法线」—— matcap / lightwarp 终于有正确的输入 | SourceMaterialConverter.cs<br>UvNormalMap.cs |
| 2026-11-04 | `b070cd09` | feat(material): 第二层 matcap（$MATCAP2）+ lightwarp 的 ×0.30 上限 —— 补齐 NekoToon 的两层观感来源 | MatcapLayerTests.cs<br>MaterialAppearance.cs<br>SourceMaterialConverter.cs |
| 2026-11-04 | `5615907f` | feat(material): matcap 层按 lilToon 公式合进底色（模式 / tint / mask1）—— NekoToon 的亮光不再被拍平 | MatcapLayerTests.cs<br>MaterialAppearance.cs<br>SourceMaterialConverter.cs |
| 2026-11-04 | `b6cbebcf` | fix(material): 没有 matcap 就不再判失败；顺手读 $color 当底色 —— 修掉白模与「银金全变灰」 | MaterialAppearance.cs<br>SourceMaterialConverter.cs<br>VmtDocument.cs |
| 2026-11-03 | `fe8fa59d` | fix(material): 把「模板能力」的判断提到换底色之前 —— 上一版"保留源底色"实际保留的是 matcap 球面图 | SourceMaterialConverter.cs |
| 2026-11-03 | `9febdd5a` | feat(material): 金属判定加第二个证据源 —— matcap 名说不出所以然时看材质名（修好 Lunalice 的 21 个金属件） | MaterialAppearance.cs<br>SourceMaterialConverter.cs |
| 2026-11-02 | `59c386c2` | feat(material): 金属判据改成按 matcap 名字，不再看底色亮度 | MaterialAppearance.cs<br>SourceMaterialConverter.cs |
| 2026-11-02 | `f2c35207` | docs(import): 修正 VpkImportService 里关于 matcap 模式的过时注释 | — |
| 2026-11-02 | `fff9f75e` | fix(metal): 金属色统一成中性常量，不再从 matcap 取色相 | MaterialAppearance.cs<br>SourceMaterialConverter.cs<br>VmtDocument.cs |
| 2026-10-28 | `f9debfd0` | 材质：matcap 判定补上"底色有没有内容"，花瓣不再变成白方块 / 金属色 | MaterialAppearance.cs<br>SourceMaterialConverter.cs |
| 2026-10-17 | `9a5344f1` | vpk2patch: --matcap albedo，交付改成 host + 选项 | SourceMaterialConverter.cs |
| 2026-10-13 | `ecdcc08e` | fix(rig-lab): 第60轮 M8 —— 外观通用化：透明数据驱动 + 金属(matcap)根因修复 | _probe_matcap.py |

## 半透明 / Opacity

| 日期 | 提交 | 标题 | 材质相关文件 |
|---|---|---|---|
| 2026-11-09 | `07574ec0` | docs(材质): 记下 SclarFieldOpacity 不吃逐像素遮罩的实机结论（完整模板复验） | SourceMaterialConverter.cs |
| 2026-11-09 | `a84295fd` | feat(材质): 实验 SclarFieldOpacity 逐像素遮罩（advanced），并把未知第 12 槽一起绑上 | SourceMaterialConverter.cs |
| 2026-11-08 | `727fad3c` | chore(release): 1.5.5 —— 新增头部缩放比例；修「脸上一层雾」「整身半透明带噪点」；命令行可用性 | — |
| 2026-11-08 | `ebbf215c` | feat(材质): 半透明判据加两端阈值 —— 「几乎不透明」按不透明、「几乎全透明」走 translucent | SourceMaterialConverter.cs<br>TextureAlpha.cs |

## 镂空 / AlphaClip

| 日期 | 提交 | 标题 | 材质相关文件 |
|---|---|---|---|
| 2026-11-09 | `43310753` | docs(材质): 记下 UseMetallicAsAlpha 的实测结论 —— 它会把镂空取样换去金属图 | SourceMaterialConverter.cs |
| 2026-11-09 | `9f807bcb` | feat(材质): 补一份完整 alphaclip 模板 + 「金属度写进 alpha」的开关实验 | Hd2MaterialTemplateLibrary.cs<br>SourceMaterialConverter.cs<br>alphaclip-full.material |
| 2026-11-09 | `d5baa8af` | docs(材质): 380 个根材质的组合能力矩阵（镂空/金属/半透明/自发光/视角相关） | hd2-material-capability-matrix-20261109.md |
| 2026-11-09 | `3be9d5ca` | fix(材质): 「实质不透明」改看 alpha 有没有结构 —— 修小面积镂空被整件判成不透明 | TextureAlpha.cs |
| 2026-11-09 | `b9a95691` | docs(材质): 「镂空+金属」候选根材质离线筛查 + 查出一处镂空回归 | — |
| 2026-11-08 | `bdfcd844` | docs(材质): 给「镂空+金属」两条实验文档加已废弃标记（代码已回退到 0d5b991） | — |
| 2026-11-08 | `ed754daa` | feat(材质): 第二个「镂空 + 金属」候选 cutoutmetal2（根材质 0x8d49d5aa14036548） | Hd2MaterialAsset.cs<br>Hd2MaterialTemplateLibrary.cs<br>SourceMaterialConverter.cs<br>VpkConversionReport.cs |
| 2026-11-08 | `b5ad7356` | fix(材质): cutoutmetal 的底色 alpha 是**不透明度**，不是 0 —— 修「镂空件整块消失」 | SourceMaterialConverter.cs |
| 2026-11-08 | `81e613cd` | fix(材质): cutoutmetal 模板改为克隆真实子材质 + 第 4 槽必须填 —— 修「镂空件整块不显示」 | SourceMaterialConverter.cs<br>cutoutmetal.material |
| 2026-11-08 | `3e21935c` | feat(材质): cutoutmetal 档位（镂空 + 金属）与派生贴图 id 撞车修复 | Hd2MaterialTemplateLibrary.cs<br>Hd2MaterialTemplateTests.cs<br>SourceMaterialConverter.cs<br>cutoutmetal.material |
| 2026-11-05 | `2cab23ee` | fix(材质): 「金属 + 真镂空」的件把底色写成中性金属色（判据驱动，无白名单） | SourceMaterialConverter.cs |
| 2026-11-05 | `36245586` | feat(材质): 镂空裁剪移到 CLI（分割/重定向之前）+ 出 MourningVeil 测试包 | VpkPatchAssembler.cs |
| 2026-11-05 | `102e2589` | feat(材质): 镂空裁成几何（CutoutMesh）+ --cutout-geometry 白名单开关（默认关闭） | CutoutMesh.cs<br>SourceMaterialConverter.cs<br>VpkPatchAssembler.cs |
| 2026-11-05 | `e2a18489` | fix(材质): 镂空金属件（拿不到金属度）把金属观感画进底色 —— 近黑剪影抬到 F0×0.85 | SourceMaterialConverter.cs |
| 2026-11-04 | `0e5c2fb3` | fix(material): 均匀 alpha 恒为 13（≈5%）不再算「整体半透明」—— 修掉整件布料被裁光 | SourceMaterialConverter.cs<br>TextureAlpha.cs |
| 2026-11-04 | `3b521de7` | feat(material): 接上 $selfillum（源侧"不吃光照的底色"）—— 写进 advanced 槽 5 的 alpha | SourceMaterialConverter.cs |
| 2026-11-03 | `c9bd7d36` | fix(material): 金属做不到时不再涂亮银而是保留源底色；「镂空与金属不能并存」改为结构性验证 | Hd2MaterialTemplateLibrary.cs<br>MaterialAppearance.cs<br>SourceMaterialConverter.cs |
| 2026-11-03 | `1543e842` | fix(material): 遮罩改取源底色 alpha、底图缺失不再强制镂空、tint 解析修正 | MaterialAppearance.cs<br>SourceMaterialConverter.cs<br>VpkPatchAssembler.cs |
| 2026-11-02 | `19ace497` | fix(material): alphaclip 材质关闭脏污/天气（雪地污染的真正根因） | SourceMaterialConverter.cs |
| 2026-11-02 | `a3b2952e` | feat(material): 新增 --cutout-template original —— 镂空件改用本体角色材质的 13 槽模板 | SourceMaterialConverter.cs |
| 2026-11-02 | `6809599f` | fix(material): 撤回"alphaclip 的 alpha 是金属度"的假设，并记录结构性限制 | SourceMaterialConverter.cs |
| 2026-11-02 | `c73c7f79` | feat(material): 半透明改成"抖动透明"——渐变 alpha 抖成点阵，近似还原源侧薄纱 | AlphaExperiment.cs<br>Hd2MaterialAsset.cs<br>SourceMaterialConverter.cs<br>VmtDocument.cs |
| 2026-10-16 | `bddf6970` | source: 修好镂空蕾丝材质（材质身份要含模板），并把配件并进本体出七份 Unit | SourceMaterialConverter.cs<br>SourceMaterialConverterTests.cs |
| 2026-10-14 | `878b60a4` | rig-lab r75b: 左小臂改为刚体平移（去掉中段 alpha 渐变，消除 W 形） | — |
| 2026-10-13 | `ba063ee6` | fix(rig-lab): 第60轮 M7 —— 透明/镂空：换 alphaclip 材质父类 + 源 alpha 遮罩 | — |

## 自发光 / Emissive

| 日期 | 提交 | 标题 | 材质相关文件 |
|---|---|---|---|
| 2026-11-09 | `1511d371` | feat(材质): 自发光测试改成"列表 + 动画旋钮"，并记下 advanced 不做扫光这个实测结论 | SourceMaterialConverter.cs |
| 2026-11-09 | `3667b560` | feat(材质): 自发光诊断钩子补两个旋钮 —— 强度与遮罩形状 | SourceMaterialConverter.cs |
| 2026-11-09 | `2174ae8f` | feat(材质): 自发光诊断钩子 —— 不依赖来源 VPK 就能验证 emissive_* 变量 | SourceMaterialConverter.cs<br>hd2-material-capability-matrix-20261109.md |

## 文档与结论

| 日期 | 提交 | 标题 | 材质相关文件 |
|---|---|---|---|
| 2026-11-08 | `b1fefd5a` | docs(材质): Manuka 红斑复盘的完整排查链与诊断开关（对应修复 7f0f535） | — |
| 2026-11-07 | `1fd7391f` | docs(shaders): 记录根材质与 SDK 模板的变量表差异（advanced 77 vs 42，basic+ 3 vs 10） | — |
| 2026-11-07 | `6ce9bb16` | feat(tools): 建立 HD2 材质能力矩阵（380 个根材质，不是 10 个） | — |
| 2026-11-04 | `d6aa2b4e` | docs(section): 合并预演结果 —— 与 master 无冲突，且与材质方向零文件交集 | — |
| 2026-11-03 | `46cc86a9` | fix(import): 真缺失材质不再删几何、失败粒度降到单部件、CLI 结论必须被尊重 | SourceMaterialConverter.cs<br>VpkPatchAssembler.cs |
| 2026-10-13 | `d53e6868` | docs(rig-lab): 第60轮实机结果收集 —— 小臂权重剖面 + 材质模板错选定位（未改码） | — |

## 关键结论摘录（来自仓库既有文档，非本轮推测）

### 1. 现成的材质模板（SDK + 仓库内嵌）

`HD2SourceMigration/VpkConversion/Hd2MaterialTemplateLibrary.cs` 要求这 10 个模板必须存在：

```
advanced, alphaclip, alphaclip+, armorlut, basic, basic+, basic+OLD, emissive, original, translucent
```

模板文件在 `tools/ref/HD2SDK-CommunityEdition/materials/*.material`（同份也在 `docs/HD2SDK-CommunityEdition-3.8.0/materials/`）。
→ **镂空、自发光、半透明、金属 四种基础能力，SDK 模板层面就已经具备**。

### 2. 半透明在仓库里的实现方式：**抖动（Dither）**

- `c73c7f79 feat(material): 半透明改成"抖动透明"——渐变 alpha 抖成点阵，近似还原源侧薄纱`
- `bc405837 feat(dither): 抖动阈值换成交错梯度噪声，消掉"点排得太整齐"的方格感`
- 代码：`HD2SourceMigration/VpkConversion/DitherPattern.cs`、`AlphaExperiment.cs`、`TextureAlpha.cs`

→ 与我们在游戏里独立观察到的"恒定 alpha 无效、只有空间图案才像半透明"**完全一致**。

### 3. 根材质能力矩阵已经把 380 缩到个位数

文档：`docs/agent/hd2-material-capability-matrix-20261109.md`（542 行，提交 `d5baa8af`；
主检出里没有，位于 worktree `.agents/worktrees/cutout-metal-alpha/`）。

| 组合 | 命中数 | 代表根材质 |
|---|---|---|
| 视角相关（Fresnel/虹彩/清漆） | **59** | `0x2d75cc65d25ef565`（741 用例，Clearcoat+布料+绒面）、`0x54ae9ce1a8fafe8b`(`original`，`AlbedoIridescence`)、`0x8d49d5aa14036548`（虹彩膜+流动） |
| **视角相关 + 半透明** | **39** | 上表中带不透明度通路的那些 |
| 半透明 + 金属 | 22 | `advanced` 自己就在里面（但只有**整体**半透明，不是逐像素） |
| 半透明贴图（逐像素） | 9 | `OpacityClipMap` 档 |
| 镂空 + 金属 + 自发光 | **1** | `0x71ed0820a138df90` |
| 镂空 + 视角相关 | 4 | `0x8d49` 族 + `0xa991793474328c30` |

文档给出的排序建议：**`original`（`AlbedoIridescence` 直接是"底色随角度变"）→ `0x8d49` 族（虹彩膜 + 流动）→ `0x2d75cc65d25ef565`（布料 + 清漆 + 绒面）**。

### 4. 已被实机证伪的假设（避免重做）

- **`BaseColorMetalMap.alpha` 不是 alphaclip 的金属度**：2026-11-02 把 26 个金属件该 alpha 从 0 改成 255，实机完全没变化（同补丁另一处改动肉眼可见，作为正面对照）。
- `SclarFieldOpacity` **不吃逐像素遮罩**（`07574ec0`，完整模板复验）。
- 「金属度写进 alpha」会让镂空取样跑到金属图（`43310753`）。
- 11-08 在 `0xec55` 上撞到过「整族不渲染」——换根材质本身有未知风险（矩阵 §六.3）。

### 5. 对本轮工作的直接结论

我们花了很多轮在**游戏内二分 + 改载荷字节**，而仓库里**已经有**：
模板库（10 种）、抖动半透明实现、以及把 380 个根材质筛到个位数的能力矩阵。
正确做法是**在这条既有管线上加一个"视角相关半透明"模板**（起点就用矩阵推荐的 `original`/`0x8d49`），
而不是继续在一位作者的载荷上做盲测。
