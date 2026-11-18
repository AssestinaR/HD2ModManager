# 材质 pass 的贴图预算：定制着色器到底能用多少通道（2026-11-17）

> 起因：要不要"为每个 mod 定制专用着色器、砍掉用不到的功能换通道"？
> 结论：**先看这份清单再决定——通道根本不紧张，之前的"只有 1 张贴图"是选错了 pass。**

---

## 1. 方法

```powershell
python tools\pass_texture_inventory.py <patch> --material 0x<根材质id> [--json out.json]
python tools\pass_texture_inventory.py <payload.gpu> --tex-only
```

工具做三件事：切 DXBC 记录 → 解 chunk → 解 RDEF 的 SRV 绑定，只列 `__tex_*`（= 材质自己的贴图，
不是引擎全局资源）。

**DXBC 记录布局（实测，别再按 fxc 习惯猜）**

```
+24  u32 totalSize, u32 chunkCount
+32  chunkCount × u32 chunkOffset（相对记录起点）
每个 chunk：4B fourCC + 4B 未知 + 载荷（到下一个 offset 为止）
```

RDEF 的头与 fxc 标准不一致，工具用"在 RDEF 内试多个基址 + 用名字能否解析成合法 ASCII"来定位绑定表。

## 2. 结果（根材质 `ef9b3fc2fc1ba61e`，载荷 2,928,416 B，202 条记录）

| ISGN | OSGN | SHEX | 材质贴图数 | 条数 | 记录尺寸 |
|---|---|---|---|---|---|
| **248** | **44** | **39672** | **18** | 10 | **60412** |
| 272 | 140 | 26736 | 13 | 9 | 35576 |
| 272 | 116 | 18112 | 13 | 3 | 26876 |
| 248 | 44 | 428 | 2 | 9 | 1280 |
| 304/352 系列 | 248/272 | 4.4k~7.7k | 1 | 各 1~4 | 11.6k~16.8k |
| 228 系列 | 248/272 | 4.4k~4.7k | 1 | 各 1~4 | 11.6k~13.4k |

### 2.1 主材质 pass（`ISGN 248 / OSGN 44 / SHEX 39672 / 60,412 B`）的 18 个绑定

下表是**真实寄存器槽号**（不是解析序号）。★ = 由材质槽驱动的逐像素数据，其余是引擎全局资源。

| 槽 | 名字 | 性质 |
|---|---|---|
| t2 | `__tex_detail_normal_porcelain` | ★ 瓷质细节法线 |
| t3 | `__tex_zone_damage_ids` | ★ 分区损伤 ID |
| **t4** | **`__tex_nar`** | ★ **法线(RGB) + 粗糙度(A)** |
| **t5** | **`__tex_illuminate_data`** | ★ **金属度** |
| t6 | `__tex_wounds_512` | ★ 伤痕 |
| t7 | `__tex_detail_normal_leather` | ★ 皮革细节法线 |
| **t8** | **`__tex_albedo_emissive`** | ★ **底色(RGB) + 自发光(A)** |
| t9 | `__tex_wound_normal` | ★ 伤口法线 |
| t10 | `__tex_wounds_256` | ★ 伤痕（低分辨率） |
| t11 | `__tex_wound_data` | ★ 伤口数据 |
| t12 | `__tex_wound_derivative` | ★ 伤口导数 |
| t13 | `__tex_blood_tiler` | ★ 血迹平铺 |
| t15 | `__tex_specular_brdf_lut` | 全局 BRDF LUT |
| t16 | `__tex_ui_diffuse_cubemap` | 全局 UI 环境（漫反射） |
| t17 | `__tex_ui_specular_cubemap` | 全局 UI 环境（高光） |
| t18 | `__tex_ui_3d_shadows` | 全局 UI 阴影 |
| t19 | `__tex_ultra_multicolor` | ★ 多色（涂装配色） |
| t20 | `__tex_lens_cutout_texture` | ★ 镜片镂空 |

⇒ **★ 共 14 张材质数据贴图 = 56 个可自由支配的通道**。

### 2.2 对照：`e577db2c` 那条 3-RT 光照 pass（60,504 B）

| 槽 | 名字 | 性质 |
|---|---|---|
| **t2** | **`__tex_roughness_map`** | ★ **唯一的材质数据** |
| t3 | `__tex_global_diffuse_map` | 全局 |
| t8 | `__tex_sun_shadow_map` | 全局 |
| t9 | `__tex_local_lights_shadow_atlas` | 全局 |
| t10 | `__tex_closest_reflection_map` | 全局 |
| t11 | `__tex_specular_brdf_lut` | 全局 |
| t12 | `__tex_ies_lookup` | 全局 |
| t13~t15 | 体积云 / 体积雾 | 全局 |
| t16 | `__tex_light_probe_space_specular` | 全局 |

⇒ 这条是**延迟光照** pass：只吃"粗糙度"一张材质贴图，其余全从 G-buffer 与全局资源来。
**"只有 1 张"的结论在这里成立，但它不是所有族、所有 pass 的普遍情况。**

### 2.3 两个根的 pass 对照

| 根材质 | 主材质 pass | 光照 pass |
|---|---|---|
| `ef9b3fc2` | 60,412 B · 248/44 · **14 张材质贴图** | —— |
| `e577db2c` | 62,652 B · 248/44 · 16 张（其中材质数据待逐条核） | 60,504 B · 252/92 · **1 张材质贴图** |

## 3. 对"每个 mod 定制专用着色器"的影响

**推翻的假设**：此前 `hd2-custom-shader-milestone-20261115.md` §2.1 记录的"材质贴图只有
`t2 = __tex_roughness_map`"，是 **另一条 3-RT 光照 pass**（60,504 B，属于 `e577db2c` 那一族）的情况，
不是本族的普遍事实。**别把它当作"通道上限"。**

**修正后的判断**：

1. **通道不缺**：主 pass 给 18 张图。做"全功能"着色器在通道层面完全可行。
2. **真正的约束变成三条**：
   - **哪条 pass 在画这个网格**（不同族不一样，必须逐族验证）；
   - **等长替换**：`build_signed_ps.py` 要求记录尺寸、chunk 名/序/长度与 donor 一致，
     所以改的是"这条 60,412 B 的记录"，不是随便换一条；
   - **混合状态是 pass 固有的**：半透明能不能真混合，取决于这条 pass 的 blend state，
     不是我们在 PS 里写 alpha 就能决定。
3. **专用着色器仍然值得做**，但理由不再是"省通道"，而是：
   - PS 更短 → 更容易塞进等长预算、更容易验证；
   - 每个 mod 一个根材质 id → 互不覆盖；
   - 按需少画几路采样 → 更快。

## 4. 建议的下一步

| 步骤 | 内容 | 判据 |
|---|---|---|
| 1 | 在**板子**上把主 pass（60,412 B）换成"纯品红输出"，确认**它就是在画这个网格的那条** | 板子变品红 = 命中；无变化 = 不是它，换下一组 |
| 2 | 验证 blend state：把 `o0.a` 写成 0.5，看是否真半透明 | 能透过板子看到后面 |
| 3 | 用 `t15`（albedo_emissive）+ `t12`（金属）+ `t11`（粗糙度）搭第一版"全功能"着色器 | 色卡量出的颜色/粗糙度符合预期 |
| 4 | 再按 mod 需求裁剪成专用版 | 每个变体都有色卡实测数据 |

## 5. 复现命令

```powershell
# 根材质载荷里所有 pass 的贴图绑定
python tools\pass_texture_inventory.py "E:\mod\9ba626afa44a3aa3.patch_73\9ba626afa44a3aa3.patch_0" `
    --material 0xef9b3fc2fc1ba61e --tex-only --json .dsh\tmp\pass_inventory.json
```

---

## 6. 克隆根材质（每 mod 独立着色器）的必需步骤

实测于 2026-11-17，一次失败换来的：

| 步骤 | 说明 |
|---|---|
| 1 | 复制根材质的 **TocData**（2288 B）与 **载荷**（2,928,416 B） |
| 2 | 新条目 id 用素体命名空间（如 `0x02e8377209ba1000`） |
| 3 | **必须把载荷头部 `+4` 改成新 id 的高 32 位** —— 三个根的实测：`ef9b3fc2`/`e577db2c`/`84e5de1c` 的 `+4` 都等于自身 id 高 32 位 |
| 4 | 材质实例的 `TocData +24`（parent）指向新根 id |
| 5 | 用 `tools/patch_repack.py` 写入（类型表计数 + 64 对齐由它保证） |

**漏掉第 3 步的症状**：材质不报错、也不画任何东西——板子**直接消失**，且不会出现我们设的洋红。

```powershell
# 改载荷头
python -c "import struct;d=bytearray(open('root.gpu','rb').read());struct.pack_into('<I',d,4,0x02e83772);open('root2.gpu','wb').write(bytes(d))"

# 组装 patch（新增条目 + 改 parent）
python tools\patch_repack.py <patch> --out <新patch> `
    --add-entry eac0b497876adedf 02e8377209ba1000 root_toc.bin root2.gpu `
    --set-u64 eac0b497876adedf def7dc54e86d6002 24 02e8377209ba1000
```

工具会自检：类型表计数一致、GPU 64 字节对齐、无重叠、不越界。**手写这些逻辑曾两次导致游戏启动崩溃**。
