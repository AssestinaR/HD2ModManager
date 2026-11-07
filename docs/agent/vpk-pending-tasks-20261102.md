# VPK 待办（2026-11-02 记录，未开工）

> 用户在 2026-11-02 追加的一条任务，记录在这以免丢掉。不是设计稿，是**待查清单**。

## 1. 模型与材质同包的 VPK：要老实译成「unit + 材质同一份 patch」

**用户原话**：来源 vpk 很有可能是材质和 unit 一体的，这种 vpk 需要诚实将 vpk 翻译成
patch 中 unit 和材质一体的。

### 现状（2026-11-02 用真实样本**实测过**）

样本：`D:\SteamLibrary\steamapps\common\Left 4 Dead 2\left4dead2\addons\备份\lime_witch.vpk`
（43 条目：mdl=2 / vvd=2 / vtx=2 / phy=1 / **vmt=15 / vtf=20**，材质与贴图都在
`materials/tpp_witch_dreaming_lime/` 下 —— 正是"模型与材质同包"）。

实测结论：

1. **材质确实被译出来了**，但**不在同一个 patch 文件里**：

   | 文件 | 内容 |
   |---|---|
   | `9ba626afa44a3aa3.patch_0`（242386 B） | **7 个 Unit**，0 材质 0 贴图 |
   | `9ba626afa44a3aa3.patch_1`（18976 B） | **10 个 Material + 14 个 Texture** |

   也就是说：一个 VPK → 一个 Mod（同一文件夹）→ **两份 patch**。装配器的注释写着
   "材质包写进**另一个** patch 文件（同一个存档的下一个 patch 序号）"。
2. **模型用到的 10 个材质全部成功**（`source inspect` 报 `materialCount=10`，与写出的数量一致）。
3. **5 条"合成失败"是噪音**：`basic / part_selfillum / part_emiss / body_phong / face_phong`
   是 `patch` VMT 的 include 链中间层，它们的 `$basetexture` 字面就是占位值 `basetexture`。
   模型不直接引用它们，所以不影响结果——但报告里那 5 行看着像坏了。
   （`VmtDocument` 本身支持 `patch` 的 include/insert/replace 展开，链路是通的。）
4. **报告口径错位**：`kind: model` 那条写着 `materials: 10 / textures: 14`，
   而那 10 个材质实际在 `patch_1` 里。照这份报告会误以为"模型 patch 自己带材质"。

### 待定：要"同 mod 两份 patch"还是"一个 patch 文件"

- **(a) 保持现状**：一个 Mod 里 `patch_0`（Unit）+ `patch_1`（材质贴图）。
  部署、换甲（只覆盖 patch_0，patch_1 仍在 vpk2patch 生效）、重构建（整目录替换）都不受影响。
- **(b) 合成一个 patch 文件**：`patch_0` 里同时有 7 Unit + 10 Material + 14 Texture。
  格式上没问题（游戏自己的 patch 就是多类型共存），但要改装配器：把 units 与
  materials 写进**同一个** `CanonicalPatchSession`，而不是另开一份。

### 另外两件无论如何都该修

- 别把"模型没用到的 include 中间层 VMT"报成"合成失败"（降级成"跳过：中间层"）。
- 报告按**实际文件**报：`patch_0` 报 Unit、`patch_1` 报材质贴图，而不是把后者算到前者头上。

### 待办

1. **先找一个"模型 + 材质同包"的 VPK 实测**：导入后看模型 patch 里有没有 Material 条目、
   Unit 的材质表是否指向它们、游戏里是否正常。
   - 本库唯一的模型包 `serenade_bill.vpk` 只有 `mdl=2 / vmt=0 / vtf=3`，**不是**同包样本；
     需要用 L4D2 addons 里那种自带 `materials/` 的模型包。
   - 扫描脚本：`tmp/vpk_selfcontained.py`（列出 mdl / vmt / vtf 计数，直接标出"模型+材质同包"）。
2. 如果确实没写（或只写了一部分，例如没写它引用的贴图）：修 `ResolveAndEmitMaterials` 的过滤条件，
   保证"本 VPK 带的 VMT + 它们引用的 VTF"全部落到**同一个** patch。
3. 无论结果如何，**把 help / 注释 / 报告口径改成实话**：
   - 现在："含 .mdl → 出 unit patch（零材质零贴图）"
   - 应该是："含 .mdl → 出 unit patch，并**带上这个 VPK 自己带的材质与贴图**；
     材质在别的 VPK 里时这份 patch 就只有 Unit（管理器导入时的常见形态）。"
4. 顺带确认这类包在**重构建**下也成立（`conversion` 参数与命名空间都已记录，理论上不用改）。

## 2. 备忘里其它未开工项（同批记录，方便一起排）

- 导入 vpk 时结构：同批模型作逻辑主体、材质作它的选项（**正在做**）。
- 导入 vpk 时材质：半透明 / 更换镂空材质的做法（需实机 A/B，逐像素半透明在现有 10 个模板里做不到）。
- `PatchArchiveWriter` / `PatchWorkspaceWriter` → `CanonicalPatchWriter` 整理（纯重构）。
- 管理器重命名时输入框自动对焦。
- 让 CLI 能询问在线 GUI 管理器获得写授权（可倒计时自动授权）。
