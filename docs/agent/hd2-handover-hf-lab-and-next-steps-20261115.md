# 交接：HD2 自定义着色器 —— 现状、HellForge 实验台、待办实验

> 2026-11-15。写给接手的会话/助手。**本文自包含**：不用读对话记录也能继续。
> 上游材料：`hd2-custom-shader-runbook-20261115.md`（零售版实施手册）、
> `hd2-shader-diff-evidence-20261115.md`（技术档案 §9 崩溃链 / §10 配方 / §11 转储更正）、
> `hd2-hellforge-feasibility-20261115.md`（HF 可行性）、`hd2-reply-to-payload-host-20261115.md`（给另一个会话的答复）。

---

## 0. 三句话现状

1. **零售版路线已打通并实机确认**：用我们自己编译的像素着色器替换"家族 A"记录，实现了**贴图 alpha 驱动的逐像素真半透明**（能透过黑丝看到皮肤）。可用变体 `.dsh/variants/famA_alpha_only`，当前就部署在零售 mod 里。
2. **HellForge 实验台已验证可用**：同一份零售产物在 HF（game build **25480438**）上表现完全一致；HF 无 EAC、崩溃有完整报告，适合做**快速 A/B**。
3. **下一步是把零售版路线继续往前推**（镂空+半透明共存、SHEX 长度、可见 pass 归属），这些在 HF 里试错成本几乎为零。具体实验见 §5。

---

## 1. 零售版：已验证的事实（不要重复踩）

| 事实 | 证据 |
|---|---|
| 自带编译的 DXBC 能被引擎执行 | 全身均匀品红（`famA_valid_signed_magenta`） |
| 着色器里能采样贴图并输出 | 明暗渐变自发光 + 可见贴图接缝（`famA_ramp_both`） |
| **贴图 alpha → 逐像素真半透明** | 透过黑丝看到皮肤、左右肢体明暗不同（`famA_alpha_only`） |
| **镂空（`discard`）与半透明能在同一个 pass 共存** | 2026-11-15 实机：全身渐变半透丝袜 + 硬边镂空缺口露出裸腿（`.dsh/variants/famA_cutout_alpha`） |
| 引擎**不校验** DXBC 校验和 | 作者本人记录的存储校验和是旧值且能渲染 |
| **整段替换 SHEX 是允许的** | 上几条都是整段替换 |

### 1.0 两条 pass 的分工（2026-11-15 从 RDEF 绑定表读出，实机未逐一验证）

| | 3 RT 光照 pass（60,504 × 8） | 1 RT 材质 pass（62,652 × 10） |
|---|---|---|
| 绑定的材质贴图 | **只有 `t2 = __tex_roughness_map`** | `__tex_nar` / `__tex_albedo_emissive` / `__tex_illuminate_data` / `__tex_ui_3d_shadows` |
| 绑定的全局资源 | `t8 __tex_sun_shadow_map`、`t16` 光探针、8 个 cbuffer（b1 相机 / b2 太阳方向 / b3 太阳颜色…） | — |
| 已确认能力 | 半透明、镂空（`discard`）、自由写颜色 | 改过三轮**无可见变化**（待 `famA_matpass_cyan` 复判） |

⇒ **金属/自发光这类材质参数只能落在 1 RT 那条 pass 上**；光照 pass 里没有材质贴图，想在那儿做金属只能自己拿 b1/b2/b3 算。

### 1.1 三个"必须"（缺一即失败）

1. **`ret` 之后的填充必须是真 NOP `0x0100003A`**。写成 `0x01000038` 是另一条指令 → 非法着色器 → 整条 pass 被丢。
2. **着色器读取的输入分量必须 ⊆ 记录 ISGN 提供的掩码**。越界读（本例 `v7.z`，ISGN 只给 `v7.xy`）会让 **dxilconv 空指针崩溃**（`mov eax,[rcx+54h]`，`RCX=0`）。检查脚本：`tools/gate_build.py record ...`（门 G3）。
3. 记录头 `u32@20 = 1`；五个 chunk 尺寸与原始逐项一致；校验和复签（只为离线反汇编自检，引擎不校验）。

### 1.2 生产线（一条命令级）

```powershell
# 编译 + 定长 NOP 填充 + 建记录 + 复签 + fxc 验证
python tools\build_signed_ps.py --hlsl <a.hlsl> --payload <原始 patch_1.gpu_resources> `
    --donor-size 60504 --shex-size 51828 --out <out.bin> --stat ours --verify
# 四道门（G1 布局 / G2 校验和 / G3 输入读集 / G4 NOP 与 ret）
python tools\gate_build.py record --record <out.bin>.rec0.dxbc --donor <原始载荷> --donor-size 60504 --ours-asm <out.bin>.rec0.asm
# 切换变体 + 部署校验
python tools\use_variant.py <变体名>      # --list 看全部；baseline 回原始
```

**可见 pass 只有 `60,504 × 8` 那一组**（延迟光照）。`62,652 × 10` 那组在游戏内**不可见**（踩过三轮）。
贴图通道：`t2 = __tex_roughness_map`，采样器 `s0`，UV = `v7.xy`；贴图条目 `0x8354a7fa`（4096²、BC7、13 级 mip、22,369,648 B），来源材质槽 `RoughnessMap = 0xc567338d`。

---

## 2. HellForge 实验台（本机已就绪）

### 2.1 安装与版本

| 项 | 值 |
|---|---|
| 安装目录 | `D:\Program Files\HellForge` |
| 启动器版本 | **0.5.0-nightly.20261114 (ef27f73)**，engine 0.1.48.0，mod SDK 18 |
| 游戏 build | **25480438**（这是 HF 唯一的 build；零售版更新，结论需回零售复核） |
| 设置现状 | `sandboxed no`、**`crash dump off`**（建议在 Settings 里改成 full）、`launch options none` |
| 工具链 | **已装**：`D:\Program Files\HellForge\tools\zig\zig.exe`（Zig **0.17.0**，SHA-256 已按 `app\sdk\tools.json` 校验） |

**`--no-hit`**：用户要求启动时加这个参数，但我在 README、releases、`hooks.md`、`data.md`、以及安装目录全部 `.md/.json/.ini/.txt` 里**都没找到**它。
⚠️ **待核实**，不要假定它生效。

### 2.2 实验台里放了什么

| 路径 | 内容 |
|---|---|
| `D:\Program Files\HellForge\mods\hf-pack-milfy-full\` | **零售产物的完整包**（138 MB）：`manifest.json` + `data\` 下 4 个 patch（3 个 bundle） |
| 包源 | `E:\tmp\hf-pack-milfy-full\`（同一份，改了可重拷） |
| `D:\Program Files\HellForge\mods\hf-probe\` | **仪表 mod**（保留）：`hellforge-mod.json` + `src\hf_probe.c`（v2） |

包里的 patch（与零售 mod 的三个目录逐文件对应）：

```
data\9ba626afa44a3aa3.patch_0 / patch_1        ← 材质（我们的，含自定义着色器 + 渐变贴图）
data\fdd3d3e677e5a3f4.patch_0                  ← 模型
data\ffcad1f7ff9888d7.patch_0                  ← 模型
（每个都带 .gpu_resources 与 0 字节 .stream）
```

零售侧对应的三个目录（**必须都有**，少一个就看不到角色）：

```
E:\mod\【常服】Milfy 替换 CE-07          ← 伞装，无 patch，只有 manifest.json / icon.png
E:\mod\【常服】Milfy 替换 CE-07-模型     ← 两个 bundle 的 patch
E:\mod\【常服】Milfy 替换 CE-07-材质     ← 我们的材质 patch（famA_alpha_only 已部署）
```

### 2.3 怎么用（实测流程）

1. HF 启动器 → **MODS** 页启用 `Milfy CE-07 full (retail payload probe)`（新加的包需要关掉启动器再开，或按 Refresh）
2. **PLAY** → 穿上 **CE-07** → 看腿
3. 期望：和零售版一致 —— 黑丝还在、透明度随贴图渐变

**改包里数据后要重拷**：`Copy-Item E:\tmp\hf-pack-milfy-full 'D:\Program Files\HellForge\mods\' -Recurse -Force`，然后重启启动器。

### 2.4 编译 mod 的工具链操作（不需要 UI 按钮）

`Install mod tools` 按钮**只在有源码 mod 等待编译时才出现**。命令行等价：

```powershell
$I='D:\Program Files\HellForge'
# 1) 装工具链（首次）：会下 Zig 0.17.0 到 $I\tools\zig\。直连慢时可用代理下 zip 放进 $I\tools\downloads\
& "$I\app\cli\HellForge.Cli.exe" mod-tools --install $I
# 2) 编译所有源码 mod
& "$I\app\cli\HellForge.Cli.exe" build-source-mods --install $I
# 3) 离线校验 data spec（不开游戏）
& "$I\app\cli\HellForge.Cli.exe" build-data --mod <mod folder>
```

编译产物在 `$I\temp\mod-builds\<mod id>\<hash>\<dll>`。HF 用 `-Wall -Werror`，**任何警告都编译失败**。

### 2.5 仪表 mod 的来龙去脉（含一次真实崩溃，务必看）

**v1 崩溃（我的错，已修）**：在 `HF_EVENT_GAME_LOADED` 里立刻调用 `PackageHasLoaded(id)` → 崩：
`c0000005 at helldivers2.exe+0x32133e`，而 `PackageHasLoaded` 的 rva 是 `0x321330` —— 崩在函数内部第 14 字节，因为那时包系统还没建好。
**教训：不要在 GAME_LOADED 立刻调用依赖引擎状态的函数。**

**v2（现役）**：不调用任何游戏函数，只读驻留表：

```c
HfPackageSystem* sys = hf_global_PackageSystem(mod);   // { void* map @0; uint32_t capacity @8 }
// 条目 16 字节，以包 id（u64，0=空）为键；用 hf->read 遍历，失败只返回 1 不崩
```

日志在 `D:\Program Files\HellForge\logs\sessions\<session>\mod-local.hf-probe.log`，t~2s / 10s / 30s 各报一次。

**v2 的实测结论（重要，避免重复劳动）**：

```
t~2s : 表内 0 条   → 三个包都 absent
t~10s: 表内 4 条   → 模型包 fdd3d3e6 / ffcad1f7 = IN MAP；材质包 9ba626af = absent
t~30s: 表内 17 条  → 同上
```

**材质明明生效却 absent** ⇒ 结论请按这个说法：`9ba626afa44a3aa3` 就是本体存放外观/材质资源的那个 bundle
（强推断：它同时出现在 HF 自带数据、Milfy 材质 mod、MourningVeil、别人的 `patch_73` 四处），
而它不在 `RequestPackages` 的驻留表里 —— 所以**驻留表不是判断"我们的材质数据有没有生效"的正确仪器**。
想证"引擎真建了材质"，应改用 **hook `MaterialReinitializeShader`**（`0x30F420`，`void(void* material)`，可 hook）并读 `material+0x40`（pass flag 位）。
（早期本文写的"合成 bundle id"是错的说法，已更正。）

### 2.6 HF 的取证能力（实测）

- 崩溃自动产出 **crash report zip**（`%USERPROFILE%\Desktop\HellForge-crash-report-*.zip`）：`summary.txt`（崩溃模块+偏移、mod 列表、加载器事件）+ `dump/crash-*.dmp` + 全套 session 日志。
- 日志目录：`D:\Program Files\HellForge\logs\sessions\<id>\`（含 `modloader.log`、`hooks.tsv`、`mod-<id>.log`）。
- 详细 API 在 `D:\Program Files\HellForge\app\sdk\`：`hellforge_mod.h`（10 万行级注释）、`hellforge_functions.h`（552 个命名函数）、`hellforge_globals.h`（108 个全局）、`docs\modding\*.md`（22 篇）。
- **`app\sdk\shaders\*.hlsli`** 是免费情报：它把游戏 decal / transparent / refract 三个阶段的确切输入/输出/寄存器/尺寸写死了，还有各宿主的拒绝条件（例如"输入不在该 stage 的寄存器" —— 和我们踩的 G3 同一个坑）。

### 2.7 HF 里**不要**做的事（用户明确要求）

- **不为 HF 做交付型 mod**。HF 只是"没有反作弊的本地实验台"，用来探明**原版游戏**接受什么样的自定义着色。
- 最终交付永远走**零售版的 `data\*.patch_N` 文件层路线**。
- 因此 HF 的 data-spec `shader` 宿主（`decal`/`transparent`/`refract`）**只当参考资料**，不当作交付手段。
  （它还有个硬限制：**不允许自带贴图/常量**，所以也做不了"贴图驱动的逐像素 alpha"。）

---

## 3. 关键路径速查

| 用途 | 路径 |
|---|---|
| 零售 mod 库 | `E:\mod\【常服】Milfy 替换 CE-07{,-模型,-材质}` |
| 零售备份（原始/各阶段） | 各 mod 目录下 `.abtest-backup\` |
| 变体库（每个 = 一套完整 patch） | `.dsh\variants\`（`famA_alpha_only` 是当前成功版） |
| HF 实验台包（源） | `E:\tmp\hf-pack-milfy-full\` |
| HF 安装 | `D:\Program Files\HellForge\` |
| HF 的 mods | `D:\Program Files\HellForge\mods\{hf-pack-milfy-full,hf-probe}` |
| HF 会话日志 | `D:\Program Files\HellForge\logs\sessions\<id>\` |
| 我们克隆的 HF 源码 | `E:\tmp\hellforge-src`（dev 分支，sparse: mappings/docs/examples/native/templates） |
| 崩溃证据（已解压） | `E:\tmp\hf-crash\` |

---

## 4. 零售版与 HF 的边界（不要搞混）

| | 零售版 | HellForge |
|---|---|---|
| 反作弊 | EAC | 无（离线、网络被墙） |
| 游戏 build | 当前新版 | 固定 **25480438** |
| 改数据方式 | `E:\mod\...` → 管理器部署 → `data\*.patch_N` | 同一个文件层（cosmetic pack）**或** HF 自己的 data spec |
| 迭代成本 | 每次要重新部署 + 重启 + 目视 | 同样重启，但**崩溃有完整报告**、可读内存/挂钩 |
| 结论可迁移性 | 金标准 | 需回零售复核（build 不同） |

---

## 5. 待办实验（按价值排序，每条都给判据）

> 共同前提：改的是**零售产物的字节**，在 HF 里跑；通过后再回零售版复核。
> 修改流程：改 `E:\tmp\hf-pack-milfy-full\data\` 里的字节 → 重拷到 HF 的 mods → 重启启动器 → PLAY。

### B（最推荐）· SHEX 长度是否必须等长
- **做法**：把家族 A 那 8 条记录换成**缩短版** SHEX（不做定长填充，或填充到不同长度），其余不动。
- **判据**：崩 / 不崩；以及黑丝是否还在。
- **价值**：如果不等长也能接受，我们的产线可以省掉"定长填充"这一整步（现在是最容易出错的一环）。

### D（最推荐）· 镂空与半透明能否共存
- **做法**：在现有 HLSL 里加 `clip(c - 0.5)`（或 `if (c < 0.5) discard;`），仍在同一 pass。
- **判据**：出现孔洞 **且** 非孔处仍有渐变透明 = 共存成立；只有孔洞无透明 或 只透明无孔洞 = 二者互斥。
- **价值**：这是"镂空+半透明"能否一个材质搞定的直接答案。

### A · 哪些寄存器原版愿意喂给我们
- **做法**：把采样从 `t2` 换成 `t5`（`__tex_illuminate_data`）或 `t8`（`__tex_sun_shadow_map`，我们已知它是单通道），看画面变化。
- **判据**：颜色/图案是否随贴图变化。
- **价值**：扩展可用的材质数据通道（例如想用另一张贴图驱动 alpha）。

### C · 可见 pass 是否只有一组
- **做法**：把同样的 shader 换到 `60,476 × 8`（另一组 60k 记录）上。
- **判据**：是否生效。
- **价值**：确认"唯一可见 pass"这个结论，避免以后又打错组。

### E · 用 hook 证"引擎真的建了我们的材质"
- **做法**：hook `MaterialReinitializeShader`（`0x30F420`），在 detour 里记录 `material` 指针与 `+0x40` pass flag；配合 `CanGetResource` 或材质表反查。
- **判据**：日志里出现与我们的材质对应的重建调用。
- **价值**：替换掉 §2.5 那个"驻留表不对路"的仪器。

---

## 6. 已知未解决 / 需要人

| 项 | 状态 |
|---|---|
| `--no-hit` 参数 | **未核实**（文档里找不到） |
| 完整内存转储 | HF 设置里现在是 **off**，建议开成 full |
| 零售版结论回迁 | 所有 HF 结论都要在零售版复核一次才算数 |
| `9ba626afa44a3aa3.patch_2`（3.15 MB，HF 自带数据同 id） | 未查明来源；目前不影响，但多 mod 并存时注意 id 撞车 |
| MourningVeil 会话的"载荷承载"问题 | 已写答复草稿 `hd2-reply-to-payload-host-20261115.md`；他们真正需要的是"别承载别人的载荷，改自己那份"。若要继续帮他们，可用 §2.5 的仪器思路（但注意驻留表的坑） |

---

## 7. 安全与回退

- **不要动零售游戏本体**（`D:\SteamLibrary\steamapps\common\Helldivers 2`）。零售侧的改动只在 `E:\mod\` 与 `data\` 的符号链接层。
- 零售回退：`python tools\use_variant.py baseline`。
- HF 回退：删 `D:\Program Files\HellForge\mods\hf-pack-milfy-full`（或关掉该包）；`hf-probe` 只是只读仪器，留着无害。
- HF 崩溃后：桌面会生成 `HellForge-crash-report-*.zip`，**先读 `summary.txt` 的 `Crash:` 行**（模块+偏移），再看 `modloader.log` 尾部。
