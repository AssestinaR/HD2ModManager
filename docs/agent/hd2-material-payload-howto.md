# HD2 根材质载荷：一页纸操作说明

> 面向"现在就动手改一个根材质"的场景。原理与推导见
> `docs/agent/hd2-custom-material-pipeline-plan-20261114.md`。

## 1. 三个前提事实

1. **材质参数改不动效果**：`.material` 的数值（颜色/粗糙度/parent）对这类效果**无效**，
   真正起作用的是**根材质自带的编译载荷**（`<fileId>.patch_N` 的 `.gpu_resources`）。
2. **只有"等长"编辑可行**：不能加长/缩短着色器（扩容会被 `fxc` 拒绝，容器也没有记录长度表）。
   改立即数、把一条指令换成同样长度的另一条指令，都可以。
3. **`fxc` 只认"没被改过"的 blob**：它校验 DXBC 的 128 位 checksum；改过的 blob 只能用我们自己的
   `sm5disasm.py` 反汇编。**游戏引擎不校验 checksum**，所以改动照样生效。

## 2. 载荷结构速查

| 区域 | 内容 |
|---|---|
| `0x00` | 179 条头部记录（含 32 位变体键） |
| `0x40000` 附近 | 材质变量描述表（20 B/条：`[?][id][offset][elemSize][count]`） |
| `0x40D80` 起 | **272 条 DXBC**（116 VS + 156 PS），记录之间夹 92 / 3780 B 空隙 |
| 每条记录 | `RDEF + ISGN + OSGN + SHEX + STAT`；块表是**偏移数组**（`+28` 块数，`+32` 起） |

材质 TocData：136 B 头 + `N×u32` 键 + `N×u64` 贴图 id + `M×20 B` 变量表。
贴图 TocData：340 B = 12 B id + 15×12 B mip 信息 + 148 B DX10 DDS 头，像素在 `.gpu_resources`。

## 3. 工具

| 命令 | 作用 |
|---|---|
| `python tools/payload_selftest.py <gpu> [--baseline <原始>]` | 体检：记录图 / 分组常量 / 差异字节 |
| `python tools/payload_repackage.py verify <payload.bin>` | 按"记录+空隙"重建，要求与原文件**字节一致** |
| `python tools/payload_curve_edit.py <gpu> --show` | 看分组（46k/60k/mat）与 4 个家族 |
| `python tools/payload_curve_edit.py <gpu> --family A --threshold 0.12 --apply` | 改 N·V 曲线阈值 |
| `python tools/payload_curve_edit.py <gpu> --family A --threshold 0.5 --mult -2 --apply` | 反向（黑边） |
| `python tools/payload_curve_edit.py <gpu> --family A --const 0.5 --apply` | 把曲线结果换成常数（`mul→mov+nop`） |
| `python tools/payload_curve_edit.py <gpu> --restore` | 从最近备份还原 |

`.dsh/tmp/` 里还有：`sm5disasm.py`（SM5 反汇编器）、`famA_dither.py`（改抖动幅度）、
`build_adv_override.py`（造覆盖本体材质的 patch）、`preview_curve.py`（按公式渲染预览）。

## 4.0 两条硬规则（踩过坑，务必遵守）

1. **patch 索引必须从 `_0` 开始连续**：引擎按 `patch_0`、`patch_1`… 顺序读取，
   **缺号之后的都不会被读**。所以要新增一份 patch，索引必须是"当前最大索引 + 1"，
   而不是随便挑一个大数字（`patch_91` 在没有 0–90 的情况下等于不存在）。
2. **`9ba626afa44a3aa3` 是公共档案**：注册到它的条目会覆盖"对应资源"，**与本资源原本属于哪个档案无关**。
   因此自定义材质/覆盖本体材质时，patch 一律以 `9ba626afa44a3aa3` 命名，
   不要按"资源所属档案"命名（本体 `advanced` 虽属 `18235e0c9ec0e636`，也要挂到 9ba 下）。

3. **材质 TocData `+16`（u64）= 载荷字节数**：改动画质载荷长度（变长/变短）时**必须同步这个字段**，
   否则材质加载失败——表现是"用它的模型整片消失"（实测踩过：载荷 +2,240 B 未同步 → 护甲全消失）。
   同时要核对 patch 条目里的 `gpuSize` 与之一致。

## 4. 部署与回退

- 游戏读的是 `data\<bundleId>.patch_<N>`（**符号链接**指向 mod 库文件）；
  `patch_70` 就是本材质：`data\9ba626afa44a3aa3.patch_70 → E:\mod\【常服】Milfy 替换 CE-07-材质\9ba626afa44a3aa3.patch_1`。
- **改 mod 库那份文件即可生效**，无需重装；只改 `.gpu_resources` 时连管理器都不用动。
- 每次写盘前工具都会备份到 `.abtest-backup\<时间戳>-<标签>\`；回退 = 覆盖回去，或 `--restore`。
- 基线（从未改动）在 `.abtest-backup\20261114-140811-alphaonly\`。

## 5. 已确认 / 未确认

**已确认**

- 可见的"黑丝层"由 60 KB 家族的 **A 组**（8 份同一着色器 `4bc0104d`）计算；
- 它算的是 `o0.w`（光照混合权重），曲线为
  `alpha = (1 - t⁴(3-2t))²`, `t = saturate((N·V - T) · M)`；
- `T=0.6` 或 `T=0.0` 都会让黑层消失（变光腿），说明**不是单调旋钮**；
- `alpha ≡ 0 / 0.5 / 1`（常数）**三种都是光腿** → 黑层依赖**空间图案**；
- 作者是**改字节**而不是重编译（checksum 为旧值）。

**未确认**

- 黑层究竟是不是抖动图案（正在测：把抖动幅度 `0.266 → 0`）；
- 若要"稳定的半透明黑丝"，最终应调哪个参数（候选：抖动幅度 / 比较偏置 / 曲线起点）。

## 6. 不要做的事

- 不要试图给记录**加长**（`fxc` 与容器都不接受）；
- 不要用 `powershell.exe`（5.1）改这些文件（会静默破坏 UTF-8）；
- 不要指望改材质 TocData 的变量值产生画面变化（已多次实测无效）。
