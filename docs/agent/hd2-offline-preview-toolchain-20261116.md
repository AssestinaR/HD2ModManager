# 离线迭代工具链：哪些能离线、哪些不能（2026-11-16）

> 回答"能不能做一个几乎和引擎一样的离线渲染器，让他们不用上游戏就能高速迭代"。
> 结论：**能，但要认清它取代不了什么。** 另外：**HellForge 给不了渲染器**。

---

## 1. 先把边界划清

一次材质改动会碰到三层，只有第一层能离线：

| 层 | 问题 | 离线能做吗 | 靠什么 |
|---|---|---|---|
| **L1 着色器内容** | 颜色对不对、透明多少、孔多大、高光多强、呼吸快慢 | ✅ **能，且是精确的** | `shader_preview.py`（同一套数学复算） |
| **L2 记录合法性** | 这条 DXBC 引擎收不收、会不会崩 | ✅ **能** | 四道门 + `fxc /dumpbin` |
| **L3 引擎集成** | 这条 pass 到底画不画、混合状态、材质解析、崩溃 | ❌ **不能** | 只能上机（HF 或零售） |

**关键**：你们卡了十几个小时的是 **L3**，不是 L1。离线渲染器**救不了 L3**。
但它能把"L1 反复上机试"这一整块从循环里删掉——而 L1 恰恰是最花时间的部分。

### 为什么 HellForge 帮不上

HellForge 是「离线游戏副本 + mod 加载器 + SDK 文档」，**没有独立的着色器运行时**，
也没有离屏渲染接口。它能让"起游戏"变便宜（无 EAC、build 固定），但仍然是**起一个完整游戏进程**。
真正的离线渲染只能自己做。

---

## 2. 工具栈（按层次）

### T0 · 静态校验（已有，秒级）

```powershell
python tools\gate_build.py record --record <rec.dxbc> --donor <载荷> --donor-size N --ours-asm <asm>
```
四道门：G1 块布局逐项一致 / G2 校验和 / G3 读集 ⊆ ISGN 掩码 / G4 `ret` 唯一且其后全是真 NOP。
**这一步拦掉的是"上机才发现整条 pass 被丢"** —— 我们早期三次失败全在这一层。

### T1 · 离线外观预览（新增，`tools/shader_preview.py`，秒级）

把 HLSL 里那套数学用 numpy 逐像素重算：底色、受光、金属高光、菲涅尔、
贴图四通道（R 镂空 / G,B 底色 / A 透明度）、时间呼吸，**并且做线性 → sRGB 编码**。

```powershell
# 三个呼吸相位拼一张图
python tools\shader_preview.py --hlsl .dsh/tmp/famA_product3.hlsl `
    --texture E:/tmp/hd2-stocking/p2/stocking.png --out E:/tmp/preview/p.png --frames 3

# 扫参对比：同一张图里并排放 METAL_K = 0.05 / 0.2 / 0.6
python tools\shader_preview.py ... --sweep 'METAL_K=0.05,0.2,0.6'

# 临时覆盖常量，不动 HLSL
python tools\shader_preview.py ... --set EXPOSURE=0.35
```

**参数自动同步**：直接从 HLSL 里读 `static const float NAME = …;`，改完 HLSL 不用再改预览器。

它在几何上用一个**圆柱当"腿"**：法线来自圆柱面、UV 按柱面展开、背景是灰阶棋盘
（透明处能看见格子、实心处盖住格子）。足够判断"这块面料看起来对不对"。

⚠️ **它是复算，不是执行 DXBC。** 两点差异要知道：
1. 它不会发现"HLSL 编译不过"——那是 `fxc` 的事（`build_signed_ps.py` 会替你做）。
2. 引擎的混合/色调映射/后处理不在里面，所以它给的是"着色器数学的结果"，不是"引擎的最终画面"。

### T2 · 真执行 DXBC（可选，尚未做）

用 D3D11 起一个全屏 quad，把我们编译出的 DXBC 真的丢给 GPU 跑，读回像素。
价值：能证明"这条 DXBC 在这块 GPU 上确实能跑"。
代价：要写一个小 C#/C++ D3D 宿主；而我们已经有 G1–G4 + `fxc /dumpbin`，
**多花这份力气换来的信息很有限**。建议：**先不做**，等真的出现"门禁全过但引擎不画"再上。

### T3 · 上机（唯一能回答 L3）

只在两种情况下需要：
1. 验证一条新 pass / 新材质结构**会不会被画**；
2. 出最终成品前的验收。

其余时间都应该留在 L1 的循环里。

---

## 3. 建议的迭代循环

```
改 HLSL 常量 / 换贴图
      ↓  ~2 秒
shader_preview.py 扫参 —— 看起来对不对？（不满意就回到上一步，不碰游戏）
      ↓  满意
build_signed_ps.py + gate_build.py —— 结构合法吗？（G1–G4）
      ↓  全绿
上机（T3）—— 只在"结构或 pass 有变化"时才需要
```

判断标准很简单：**参数调整永远不该重启游戏；只有结构变化才值得一次重启。**

---

## 4. 这套东西对 `cutout-metal-real` 的意义

他们的 L3 问题（覆盖本体根材质到底画不画）**离线解决不了**，
所以教学补丁 `E:\tmp\hd2-teach\` 仍然是必须跑的一步。

但一旦那步通过，后面"宝石该多绿、孔多大、要不要呼吸"这类反复，
就全都能在 `shader_preview.py` 里做完 —— 而这正是最容易消耗十几个小时的部分。

---

## 5. 真值抓帧：Windows Graphics Capture 可行（2026-11-16 实测）

> ⚠️⚠️ **整节只适用于 HellForge（离线副本、无 EAC）。**
> 零售版有 EasyAntiCheat：**不要注入、不要挂钩、不要模拟输入**，也不要对着零售版跑抓帧/自动化脚本。
> 零售版只走 `data\*.patch_N` 文件层那条已验证的路。
> 本文提到的 `wgc_capture.py` / `game_capture.py` / `game_click.py` / `hf-nav-probe` 全部是 **HF 专用**，
> 文件头都写了这条警告。
>
> ### 风险不是一个量级（2026-11-16 修正）
>
> | 动作 | 性质 | 风险 |
> |---|---|---|
> | `wgc_capture.py` 抓帧 | 外部**只读**读取窗口合成内容，不碰进程 | 无 |
> | `game_click.py` 模拟点击 | 向游戏进程**投递输入** | **这是 EAC 重点检测的行为类别** |
>
> 所以：**优先人手点一下**（开场"点击继续"就一下）。真要用脚本时，`game_click.py` 内置硬门禁——
> 先解析目标窗口所属进程的 exe 路径，**只有位于 `D:\Program Files\HellForge` 下才允许执行，没有 override**；
> 指向零售版会直接退出（退出码 2）。

`tools/wgc_capture.py`（依赖 `pip install windows-capture`）。

实测结论（零售版，游戏**在后台**、前台是别的窗口）：

| 项 | 结果 |
|---|---|
| 能否抓到 | ✅ 能，1922×1082 |
| 是否要求前台 | ❌ **不要求**（先前"必须前台"的说法是错的，那是游戏卡死造成的假象） |
| 连续抓帧 | ✅ 6/6 帧成功，帧内容各不相同（引擎在后台仍在出帧，约 4 fps） |
| 单帧耗时 | ≈0.25 s |

⚠️ **通道顺序**：WGC 给的是 **BGRA**。按 RGBA 存会 R/B 互换，画面整片发黄
（实测均值 R=50/B=32；修正后 R=34/B=50）。正确写法：

```python
Image.fromarray(buf[:, :, [2, 1, 0]]).save(path)   # BGR(A) -> RGB
```

⚠️ **不要对游戏窗口调 `ShowWindow` / `SetForegroundWindow`**：实测之后窗口会卡在交换链上，游戏假死。

### 5.1 结论：抓帧走零售版，HF 只做材质实验台（2026-11-16 定案）

试过并放弃的：

| 方案 | 为什么放弃 |
|---|---|
| HF code mod 转相机 + 逐 section 显隐 | `UnitSetVisibilityGroup` 在 SDK 18 **没有映射**（日志：`not a mapped function`），逐 section 截图做不到；相机函数虽然都在（`UnitSetLocalPose`/`UnitTeleportLocalPose`/`UpdateUnit`/`CameraWorldToScreen`），但 HF 那次世界渲染成一片灰、关掉多个 mod 也没恢复，没有继续追 |
| 模拟点击过开场 | **向游戏进程投递输入是 EAC 重点检测的行为**，风险与收益完全不匹配（只为一次点击），放弃 |

定案的边界：

- **抓帧：零售版 + 被动 WGC**（`wgc_capture.py`）。只读窗口合成内容、不碰进程；用户确认"拍照流程不会触发反作弊"。
- **不注入、不挂钩、不模拟输入**。HF 里那两个探针（`hf-probe` / `hf-nav-probe`）留着但默认不启用。
- 相机角度由**人**给：人在衣柜/舰内转，我抓。接受"角度和比例不完美"这个现实。
- 真正的快速迭代仍然靠 `shader_preview.py`（参数级），零售截图只用来**校准量级与趋势**，不做逐像素对齐。

实用抓法：

```powershell
# 抓整窗
python tools\wgc_capture.py --title HELLDIVERS --out E:\tmp\shots --count 1
# 只抓人物区域并缩小（先用整窗图量出坐标）
python tools\wgc_capture.py --title HELLDIVERS --out E:\tmp\shots --crop 560,120,1360,1060 --scale 0.6
```

### 5.2 分工

| 谁 | 做什么 | 状态 |
|---|---|---|
| 人 | 在零售版里把角色转到想要的角度，说一声 | 每次几秒 |
| `wgc_capture.py` | 后台抓帧、可裁切缩放 | ✅ 可用 |
| `shader_preview.py` | 参数级快速迭代 | ✅ 可用 |
| ~~HF code mod~~ | ~~转相机 / 逐 section~~ | ❌ 已放弃 |

---

## 6. 已知不足
|---|---|
| 预览几何是圆柱 | 不是真模型。曲率、UV 展开、遮挡都和实物不同 |
| 不含引擎后处理 | 泛光、色调映射、SSAO 都不在，所以"亮不亮"会与游戏有偏差 |
| 不含混合状态的真实值 | 我们假设标准 alpha 混合；如果引擎用的是 premultiplied 或 additive，预览会偏 |
| 没有 L2 之外的崩溃预测 | dxilconv 的坑只能靠 G3 |
