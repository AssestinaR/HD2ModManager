# 第 67 轮：小臂「橡皮筋」—— 刚性重绑

**结论先说**：用户反馈「小臂像橡皮筋，不是硬的而是软的」。这是**皮肤权重本身**
的毛病，不是摆姿：皮肤的 hand 权重沿「肘 → 腕」是**一条单调爬升的斜坡、全程没有
刚性段**，导致**整条小臂都听手腕骨的**，手腕一拧整段小臂跟着软扭。
这个问题**第 60 轮已经在 Lunalice 上诊断并修好过**，本轮把同一套修法搬到 Lilium。

修法：`reweight_forearm.py`，**只改运行时权重**（几何 / 拓扑 / UV / 骨架 / 材质一字不动）。
效果：30° 腕转下小臂中段位移 **11.8 / 45.4 mm → 0.0 / 0.0 mm**。

---

## 1. 诊断：为什么是「软」的

健康的绑定里，小臂靠近肘的那一大段应该**完全属于肘骨**（权重 1），
只有贴近腕的一小截才归手腕骨。Lilium 皮肤的实际权重不是这样：

```
hand 权重
 1.0 |                                             ______
     |                                           /
     |                                         /
     |                                       /      <- 单调斜坡，无平台
     |                                     /
 0.0 |____/____________________________________
     肘                                        腕
```

**没有任何一段是「纯肘」**。后果：手腕骨一转，整条小臂被线性拖走
（`_probe_forearm_action.py` 实测中位 11.8 mm、最大 45.4 mm）。

这正是用户看到的「橡皮筋」。Lunalice 第 60 轮是同一个病，同一套药。

---

## 2. 规则（零硬编码）

只对「肘 → 腕」这条轴附近动手，其余顶点一字节不碰：

* **ROI**：到「肘 → 腕」轴距离 **< 10 cm**（`--radius 0.10`）的顶点。
* **刚性小块**：ROI 内某连通块沿该轴的跨度 **< 60 mm**（`--rigid-span-mm 60`）
  ⇒ 判定为刚性件（护腕 / 装甲片），整块取它在斜坡上的**单一比例**，
  只做刚体运动，内部零变形。
* **其余顶点**：按 hand 质量重新分配到 `smoothstep(0.78L, 0.92L, t)`
  （`--a-w 0.78 --b-w 0.92`；`t` = 沿肘→腕轴的归一化位置，`L` = 肘腕距离）
  ⇒ 肘侧大段被钉成纯肘骨，过渡带收窄到 0.78L~0.92L。
* **搬运守恒**：被搬走的权重**只在「手骨子树 ↔ 同侧肘骨」之间搬运**
  （肘不够时借 shoulder），总额守恒，**不引入新的撕裂**。
* **落地**：合并同骨 → 每顶点 1/2048 定点最大余数 → float16 精确闭合。

> ⚠️ **不能按 `hand ≥ 0.95` 排除**：手部皮肤与手指是**同一个连通块**，
> 按阈值切会把皮肤撕开。第 60 轮踩过这个坑，本轮沿用同一处理。

---

## 3. 实测

### 3.1 重绑统计（`reweight_forearm.py` 输出）

```
unit 0xcae35ee15168145f: 4 streams, model streams [0, 1]
  left  L=287.4 mm  near=1583  ramp-vertices=133  rigid-vertices=856  (blend 224..264 mm)
  right L=287.4 mm  near=1690  ramp-vertices=129  rigid-vertices=855  (blend 224..264 mm)
  weights: 1973 vertices changed, 0 dropped an influence, half4 closed=True (worst 0.000e+00)
  wrote ...patch_0 (164404 B) + gpu (84027008 B; source 84027008)
```

* 每侧 ~1600 个近轴顶点里，**~860 个判定为刚性块**、~130 个进过渡带。
* **0 个顶点丢影响**，float16 四元权重**精确闭合**（误差 0）。

### 3.2 刚性验证（`_probe_forearm_action.py`，30° 腕转）

| patch | 侧 | mid med | mid max | mid hand | tip med |
|---|---|---|---|---|---|
| `lilium-final`（第 66 轮） | left | 11.8 | 45.4 | 0.223 | 17.3 |
| `lilium-final`（第 66 轮） | right | 11.6 | 45.5 | 0.218 | 17.5 |
| **本版**（第 67 轮） | left | **0.0** | **0.0** | 0.000 | 17.1 |
| **本版**（第 67 轮） | right | **0.0** | **0.0** | 0.000 | 17.2 |

`mid` = 小臂 t∈[0.25L, 0.70L]、半径 <100 mm 的顶点；`tip` = 指尖。
**小臂中段被拖位移归零，指尖仍 17 mm 跟手（正确）。**
**在最终交付包上复测，结果相同。**

### 3.3 只动权重的逐字节证明（`_probe_weightdiff.py`）

比对「第 66 轮归档的手指版」与「本版交付包」：

```
before 16917432 B, after 16917432 B
differing bytes: 15914 of 16917432 (0.094%)
by field : {'bone_weight': 13272, 'bone_index': 2642}
by stream: {0: 7957, 1: 7957}
bytes outside any declared vertex field: 0
vertices touched: 3946
RESULT: PASS - only weight bytes changed
```

* 变动 **0.094%** 字节，**只落在 `bone_weight` / `bone_index`**；
* 两条模型流都改到（stream 0/1 各 7957 字节），**3946 个顶点被触及**；
* **任何声明过的顶点字段之外 0 字节** ⇒ 几何、UV、法线、切线、颜色全未动。

### 3.4 可复现性

把 `reweight_forearm.py` 对同一输入重跑一遍，产物**逐字节相同**：

```
patch_0        51d0af142202bc6a8fd2922f79d73d01d5018ba653d7ae074efdd863517ae39e
gpu_resources  63bb8e5227eaa45d245c494d7ddadcd2369e884ab41fb7822441dba344c76b9c
```

### 3.5 独立审计（`_verify_normalized.py`）

对**最终交付包**复跑：**27 项 26 通过 / 1 已知**
（唯一失败项 = 管理器生成隐藏占位 unit 时丢「零索引 section」的材质短 id，
第 59 轮起就存在，不在我们代码里，见 README「已知瑕疵」）。

---

## 4. 交付

| 文件 | 大小 | sha256 |
|---|---|---|
| `9ba626afa44a3aa3.patch_0` | 562176 B | `5e4e045c3e1cbc7c30e21bd96b60b334beebb42ac510defd51e826bcabcfc044` |
| `9ba626afa44a3aa3.patch_0.gpu_resources` | 84229248 B | `bb6e3008ac176ca5a65bfffee4ef4df77a3fde6a90f1e646528268c2f0c408d0` |

* **patch 哈希与第 66 轮逐字节相同** —— 因为权重字节在 **GPU 侧载荷**里，
  patch 容器（TocData / 条目表）没变；变的只有 `.gpu_resources`。
* 第 66 轮（手指版）归档：`E:/tmp/round66-fingers-only-archive/`
  （patch `5e4e045c…` / gpu `a8854ba3a84f4a59bb54395e680ea0e908b68443c6368fc6bdb9eb3f01f06ad5`）。

---

## 5. 已知限制（骨架结构，非权重错误）

HD2 这套骨架**没有 twist 骨**，所以小臂的「旋前 / 旋后」（转钥匙）只能集中在
**腕缝**上，拧手腕时腕部过渡会偏生硬。第 60 轮给 Lunalice 做时就已记录。
要根治得往骨架里加 twist 骨 —— 那是改骨架结构，超出「只改权重」的范围。

---

## 6. 复现命令

```
cd tmp/rig-lab

# 重绑（只改权重）
python reweight_forearm.py \
  --src "E:/tmp/lilium-final/9ba626afa44a3aa3.patch_0" \
  --out "E:/tmp/lilium-w" --name lilium-forearm \
  --a-w 0.78 --b-w 0.92 --radius 0.10 --rigid-span-mm 60 --hand-shell 1.01

# 刚性验证（30° 腕转）
python _probe_forearm_action.py <patch>            # 改前 11.8/45.4 → 改后 0.0/0.0

# 只动权重的逐字节证明
python _probe_weightdiff.py <改前patch> <改后patch>  # PASS，0.094%

# 独立审计
python _verify_normalized.py E:/tmp/lilium-w/9ba626afa44a3aa3.patch_0 \
    E:/mod/Lilium-Sancta-RE2310/9ba626afa44a3aa3.patch_0 \
    --our-unit cae35ee15168145f,9841b16544c8fbae \
    --vanilla-dir E:/tmp/hd2-mgroot/re2310-full/unit
```

`_probe_forearm_action.py` / `_probe_weightdiff.py` / `_verify_normalized.py` 全部只读，
不改任何产物。
