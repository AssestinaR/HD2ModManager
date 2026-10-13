# HD2ModManager 项目长期记忆

## 约定
- 仓库 WpfApp1；只本地提交不推送。用户非程序员，讲通俗话；用户负责视觉验收，助手负责数值与自动检查。
- 临时脚本放 tmp/rig-lab/（gitignore 只放行 *.py），不放 _diagnostics/。docs/ 忽略，改前备份 docs/_backup/。不删 .workbuddy-ai。故障包与依赖留版本+hash，候选另存目录，不覆盖唯一证据。一律绝对路径。
- 修 bug 必做"破坏关键行"负对照；删代码同步改测试。子代理 = 用户另开线程 + 交流文档（*.md 放 tmp/rig-lab/），不用 Agent 工具。
- 路线：Python 快循环 → 离线 legacy patch 包 → 用户实机 → 确认后上 C#。只借 BatchTool 变换；不依赖 HD2IRG1 动态骨骼；无 Lua 也必须能跑。发图前自己先肉眼看过。

## 环境
- Python `C:/Users/Administrator/.workbuddy-ai/binaries/python/envs/default/Scripts/python.exe`（numpy/Pillow/scipy）；Blender `D:/SteamLibrary/steamapps/common/Blender/blender.exe`；7z `"/c/Program Files/7-Zip/7z.exe"`。
- SDK `docs/HD2SDK-CommunityEdition-3.8.0`（texconv 在 deps/）；游戏 data `D:/SteamLibrary/steamapps/common/Helldivers 2/data`；源素材 `D:/SteamLibrary/steamapps/common/Left 4 Dead 2/left4dead2/addons/`（`备份/` 下有八人包等 7z）；原版抽取 `E:/tmp/hd2-mgroot/`；mod 库 `E:/mod/`。

## 工程
- 管理器 WPF/net10、CLI net10；Core/Adaptation/GameItemAssets/SourceModel/SourceMigration net8。CoreServices 唯一装配点，SourceModel 零项目引用。Nullable/ImplicitUsings/Deterministic、警告当错误（除 NU1901..1904）。legacy 包需无 Lua 可用；运行时无建网格/改权重接口，迁移离线做。
- 自定义数据属模组库，简单配置属 settings.json，可重算缓存属管理器目录。
- **覆盖语义**：patch 序号大的后加载，覆盖同 AssetKey 的较小者。

## 模型与骨骼
- 骨架不动：`S_bind == rest_hd2`；移动/缩放角色只动网格。HD2 +X右 +Y前 +Z上，左肢在 -X，单位米。Unit 存行向量矩阵，平移 flat12..14；`read_hd2_rig` 转列约定（hd2_patch 近期去转置，调用者须核查）。
- 摆姿已全部验收（hand_tilt=3、finger_pose=True）。手指逐节残余摆姿 `Rl=min_arc(s,Q.T@t)`，枢轴须过 post，末节取 HD2 局部 +X；发布 rest_hd2 不变，rest_sym 是工作枢轴。
- 小臂：皮肤 hand 权重是肘→腕单调斜坡无刚性段，**不能按 hand≥0.95 排除**（与皮肤同连通块，会撕皮肤）。规则 = ROI 距轴<10cm；连通块 ROI 内 t 跨度<60mm 取附着点单一比例；其余 smoothstep(0.78L,0.92L)，差值只在手骨子树↔同侧肘骨间搬运。30°腕转中段位移 52.1→0.0mm。只改 unit GPU 0.244% 字节，两条模型流都要改（`vertex_buffer_offset` 是绝对基址）。
- 权重：六区 body_region+附件身份；同侧同指可信三角面重心转移+置信度回退；交界留渐变。`weight_half4.py` 先合并同骨再 2048 格点最大余数，121704 行 half 精确和 1。

## 材质（第 60 轮 M4–M8）
- 护甲必须 `advanced`（basic = "trash bag prop"）。advanced 自有槽 **2**=Normal/AO/Roughness（RGB 法线 / Alpha 粗糙度）、**3**=Metallic、**5**=Color/Emission Mask（RGB 底色 / Alpha 自发光，须=0）；其余 8 槽留模板自带游戏贴图。槽位键与 armorlut/basic 零重合，键不对 ⇒ 贴图永远绑不上 ⇒ 全黑。
- 透明 = **材质父类属性**，不是贴图属性。`alphaclip` = "supports an alpha mask"，3 槽 0=Normal/AO/Roughness、1=Alpha Mask、2=BaseColor/Metallic，`OpacityThreshold`=0.5。**白=可见、黑=透明**，无部分透明。遮罩在 **RGB 灰度**里，不在 alpha 通道。`translucent` 无底色槽；`armorlut` 无 alpha 槽。
- 源侧：透明在 **VTF alpha**（DXT5），VMT 用 `$translucent 1` / `$alphatest 1` opt-in。**同一 `$basetexture` 被多个 VMT 共用 ⇒ 必须遍历模型自己的 92 槽，不能只看每组主 VMT**。
- 源侧 92 槽全是 **NekoToon**：`底色×光照 + matcap×$matcaptint`（`$matcap 2`=相加）。装饰件 38 槽底色是 1×1 (15,15,15) 近黑，观感全在 matcap（`11` 82% 亮银 / `6` 14% 宝石 / `Matcap8` 4% 珍珠）。只搬底色 ⇒ 纯黑。
- M8 两条数据驱动规则（零硬编码）：**A** `opts_in`（组内任一 VMT 生效 translucent/alphatest）且 `has_alpha`（VTF alpha 跨度≥3/255 且 <250 像素≥0.5%）→ alphaclip + alpha 遮罩@0.5；否则不动。**B** `max($matcaptint)≥0.9` → 底色 += matcap 三角加权均值×tint，金属度 191、粗糙度 89；<0.9 一字节不改。
- 一个 HD2 材质只能一个底色 ⇒ 组内多 matcap 只能加权平均；Unit 材质表 14 项已实机验证，不能改数量。
- VTF 布局：`[16×16 缩略图 128B][mip N-1]…[mip 0]`，缩略图在链首、mip0 在尾部；`hd2_vtf.VTF.read` 从尾部剥。
- UV：HD2 约定 `row = v*H`（无 V 翻转），游戏采样 **uv0**（uv1/uv2 无关）。判形状必须用 UV 三角覆盖图（`_probe_uvplot2.py` 按三角形整体取整数偏移），包围盒不算。

## 材质变体包（第 61 轮）
- 八人包材质拆成**独立 patch**（`split_materials.py`，内建反证：默认包重跑必须逐字节复现 M8）：7 个已落 `E:\mod\Lunalice-RE2310-MAT-*`（B2服装色黑 / C1金属色金 / D2-D4发色 / E1-E2瞳色），每个含 patch + `.gpu_resources` + manifest + README + preview。
- **覆盖前提已逐条核对**（`_probe_override_ids.py`）：7 包全部条目 id 都在 M8 内、type 一致 ⇒ 纯覆盖，无新 id。
- **同族互斥**（`_probe_mat_overlap.py`）：D2/D3/D4 三选一、E1/E2 二选一；B2 的 16 个 id 与 C1/D*/E* 零重叠 ⇒ 服装色+金属色+发色+瞳色可同时开。
- **入库靠自动发现，不动 library.json**：`ModLibrarySynchronizer` 枚举库根全部顶层文件夹，含 base patch 即成节点；`ShellViewModel.RunStartupChecksAsync:1155` 启动自动同步（另有「✓ 校验 Mod 库」）。实测 `library sync inspect --manager-root E:/tmp/hd2-mgroot` 已列出 7 包。
- **蕾丝普查**：7 组真半透明（源侧全 `$translucent` 无 `$alphatest`），但 `alphaclip` 只能硬裁切、`translucent` 无底色槽 ⇒ 带纹理护甲**无法软透明**，0.5 阈值是近似。
- **材质开关 31 个 VPK 未实现**：仅 1 个可整组表达，29 个 PARTIAL。路①拆 Unit 材质表（根治、动已验收结构）②UV 遮罩涂黑（装饰件 38 槽共用 1×1 贴图，不可行）。

## 源侧组件开关（第 62 轮，L4N）
- **L4N 的「部件切换」= Source 原生 `$BodyGroup`**（不是贴图伪装）。权威：游戏根 `readme_l4n.txt` + `left4dead2/neko/mdl_extension.qc`（作者示例）。`$BodyGroup "名" { studio "a.smd" blank }`，**N 选一、`blank` 是隐藏项**；`$TextureGroup "skinfamilies"` = 材质预设；`$KeyValues { "left4neko" {...} }` 只管显示名/足 IK/默认缩放（非必需）。平台是客户端 DLL 注入（dxgi.dll），主菜单 `\`。
- **`l4n_survivor` 是另一条线**（八人包→以玩家为单位的模型池，自带 `to_l4n_survivor.exe`），与部件开关无关，别混。
- **源模型实测**（`_probe_bodygroups.py` 解析 studiohdr）：Lunalice 主模型 **32 bodygroup = 30 可切换 + 2 固定（head/Base）**，中英双语名，顶点 121511，**顶点区间两两不相交（0/31 重叠）⇒ 按 bodygroup 拆分干净**。三个 mdl 全 `numskinfamilies=1`（无材质预设）；模型 KeyValues 无 `left4neko` 块。
- **31 个 `XXX关` VPK = 每个 bodygroup 的材质层降级方案**（给没装 L4N 的玩家），名字与 bodygroup 一一对应；因材质槽被多部件共用（`Ornament_metal_Underwear` 被 38 槽引用）必然连带隐藏 ⇒ 这就是 29/32 PARTIAL 的根因。**bodygroup 精确、材质泄漏。**
- **HD2 侧映射已成立**：`armor-registry.applied.json` 的 `models[].pieces[]` = `(body,unit,slot,pieceType,tone,enabled,…)`，`RuntimeArmorComponentProjection` 按 piece 求 enabled ⇒ **按 unit 逐个开关是现成能力**。`CustomModelConversionService.BuildPlan` 已是「一来源 Unit → 一 Unit ID + 一 piece」；`SlotFor`：Head 0 / Torso·Shoulder·Accessory 2 / Pelvis 3 / LeftLeg 4 / RightLeg 5 / LeftArm 6 / RightArm 7；`pieceType` = 同 (slot,body) 层号自增。现存 Asmodeus 自定义模型 = 19 piece 实机可用。
- **唯一未知数 = piece 数量上限**：原生 RE-2310 = 23、RE-1861 = 15、自定义 = 19；「expanded 19 piece」曾启动崩溃但未定因（数量/层号/自定义字段共变）；文档明言「40-piece 解析夹具只验证解码器，不能证明引擎能生成 40 个」。⇒ 拆 32 个属未测区间，**先拆 4~6 个实机验证再逐步加**。
- 详见 `tmp/rig-lab/L4N_COMPONENT_ANALYSIS.md`。

## 交付与部署
- 链路：`hd2mod library sync` 注册新包（只动节点，先备份 library.json）→ `armor-normalize`。`armor-normalize` **只认原生 Unit id**（改名 id 一律 armors:[]）。
- **符号链接铁律**：`New-Item -ItemType SymbolicLink` 本机失败（无权限）且先删后建 ⇒ 一律 `MSYS=winsymlinks:nativestrict ln -s`，先建后删（写 .tmp 再 `mv -f`）。
- **id 公式**（决定覆盖能否生效）：`make_id(prefix,name) = (prefix<<60) | (sha256(name)[:8] 小端 & 0x0FFF_FFFF_FFFF_FFFF)`；0xB 贴图 / 0xA 材质 / 0xD unit。底色 = `make_id(0xB,'tex/base/<basetexture>')`、遮罩 = `tex/mask/<stem>`、装饰件 = `tex/albedo|rough|metal/<stem>`。
- 当前：**M8 已部署**（`E:/mod/Lunalice-RE2310-M8/`，patch 573384 B、gpu 101546624 B），回退目标 M7；回退记录 `E:/tmp/deploy-revert-note.txt`。候选源 `E:/tmp/rig-lab/cand-m8/`。
- 长期工具：`fix_appearance.py`(M8 构建)、`fix_materials.py`、`fix_textures.py`(外科替换范式)、`_probe_matfix/_probe_matchain/_probe_canddiff/_probe_weightdiff`、`_probe_pkg8*/_probe_lace`。
- 待办（暂不做）：军械库渲染模式支持 alphaclip；管理器直接导入 VPK 作一等公民（翻译成 overwrite 里的 patch）。细节见 `tmp/rig-lab/*.md` 与每日日志。
