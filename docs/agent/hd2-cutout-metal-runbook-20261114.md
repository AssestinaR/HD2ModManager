# HD2「镂空 + 金属」移植 Runbook（交给下一个 agent）

> ℹ️ **2026-11-16 补注：本文是「父材质路线」（不写自编着色器）。** 11-15 起另有更彻底的一条
> 「自带编译 DXBC」路线（见 `hd2-custom-shader-runbook-20261115.md`），能做逐像素半透明/镂空/金属/动态发光。
> 两条路都有效：只想快速拿到镂空+金属、且目标材质有合适的父材质时，用本文；要更自由的效果时走新路线。

> 2026-11-14 定稿。作者：上一轮 agent。目标读者：要在**另一个模型**上复现「镂空 + 金属」的 agent。
> 前置背景（想看原理再读）：`hd2-payload-carrier-and-dynamic-emissive-20261114.md`。

## 0. 一句话做法

把目标材质改成**子材质**（`parent` = 本体 `alphaclip` 根材质 `0xd84d04634a1e2f60`，
`gpuSize = 0`、`undat2 = 0`），槽位全部指向**该角色自己的贴图**，
再把 `OpacityClipMap` 换成我们生成的图案贴图 ⇒ 实机得到**镂空 + 金属**。

## 1. 已实机验证的成果（别重做）

| 能力 | 状态 | 证据 |
|---|---|---|
| **镂空 + 金属** | ✅ 2026-11-14 实机 | 身体/头盔/衣服出现**圆形孔洞** + 身体呈**金属表面** |
| 自发光（emissive） | ✅ 实机 | 全身皮肤**均匀品红发光**（`EmissiveColor=(1,0,1)`、`EmissiveIntensity=12`） |
| 自发光**动画**（随时间流动） | ⚠️ 未确认 | 观察结果是"均匀"，看不出流动；`EmissiveAnimation=1 / WaveSpeed=4` 已写入但效果待查 |
| 衣服金属 | ❌ 未达成 | 衣服有镂空但**没有金属**（衣服用的不是被我们改的那个材质 id） |

成功存档（可直接回滚到此）：

```
E:\mod\【常服】Milfy 替换 CE-07-材质\.abtest-backup\20261114-1756-CUTOUT-METAL-OK\
    patch_0      3,268 B  sha E43CB5358B0B43A1     ← 身体/头盔/衣服：镂空+金属
    patch_0.gpu 34,603,008 B  sha C80C39F0157556C8
    patch_1      4,748 B  sha 887A0CB598CD5CD9     ← 皮肤（当时是原始）
    patch_1.gpu 30,601,520 B  sha A4B34078DA615493
```

## 2. 环境铁律（每条都是踩出来的，违反代价是一轮重启）

1. **`data\` 下的 patch 全是符号链接**，指向库里的 mod 文件。**绝对不要往 `data\` 拷文件**
   —— 管理器重部署时会把你的真文件覆盖成链，载荷从来没被读过。
2. **库里的目录名 ≠ 部署索引**。`E:\mod\9ba626afa44a3aa3.patch_73\` 实际被分配成 `index 69`。
   索引由 `profiles.json` 的 `loadOrder` 动态分配，overlay 固定占最后两位。
   ⇒ 只能**反查符号链接**，不能按名字或"下一个空位"推断。
3. **部署前必须**跑 `tools/verify_deployed.py --mod "<库里的目录名>"`，
   必须看到 `DEPLOYED OK —— 引擎会读到我们的内容` 才进游戏。
4. **子材质的 `undat2`（材质 TocData 的 `+16`）必须为 0**。根材质那里它是"自带载荷字节数"，
   照抄会让 `gpuSize=0` 与"我有 N 字节载荷"自相矛盾 ⇒ 材质加载失败 ⇒ 整片消失。
   （这很可能就是历史「`0xec555165044bb60a` 整族不渲染」的真凶。）
5. **改贴图用"原地等长替换"**，不要改 `gpuSize` / `tocSize` / 侧车长度。
6. **BC7 的 DDS 带 DX10 扩展头（额外 20 字节）**，像素数据从 148 字节开始，不是 128。
7. **一个材质 id 常被多个部件共用**（本项目里 `0x0714f22d9efaca5a` 覆盖身体+头盔+衣服）。
   改它 = 同时改这些部件。

## 3. 根材质清单（已固化在工作区 `.dsh\roots\`，12.6 MB）

每个目录里 `<id>.toc` 是**裸 TocData**、`<id>.gpu` 是它的编译载荷。

| 根材质 id | 槽数 | 载荷 | 槽位 | 实测结论 |
|---|---|---|---|---|
| **`d84d04634a1e2f60`** | 3 | 1.98 MB | Normal / OpacityClip / BaseColorMetal | ✅ **镂空+金属都出来了，首选** |
| **`c6042e3403385d40`** | 4 | 3.00 MB | Normal / EmissiveFStop10Intensity / BaseColorMetal / OpacityClip | ✅ **镂空+金属同样成立**（多一个自发光槽；实机未观察到发光） |
| `ef9b3fc2fc1ba61e` | 12 | 2.93 MB | advanced 全套 | ✅ 渲染正常；**自发光通了** |
| `9fc2c6b713891a3b` | 2 | 3.29 MB | Normal / BaseColorMetal | ✅ 渲染正常，**金属**（无镂空槽） |
| `71ed0820a138df90` | 5 | 109 KB | +EmissiveMap+FlickerSpeed | 未测（同族风险见下） |
| `7b9f6c5638fcb231` | 2 | 216 KB | albedo+opacity / normals | 未测（半透明候选） |
| `25ea2c4516ba68fd` | 16 | 2.13 MB | uber（含半透明+金属） | 未测 |
| `ec555165044bb60a` | 4 | 103 KB | Normal/OpacityClip/BaseColorMetal/Parallax | ⛔ **整片消失**（换亮掩码、纯白掩码都一样） |
| `a991793474328c30` | 7 | 2.43 MB | +Clearcoat+ClothDetail+SpecIri | ⛔ **整片消失**（纯白掩码也一样 ⇒ 不是裁剪） |

> 为什么 alphaclip 行而 ec55/a991 不行，**目前未知**。唯一的判据是实测。

## 4. 移植到另一个模型：五步

### Step 1 — 找到目标材质 id
```powershell
# 该 mod 目录下每个 patch 都列一遍，找 typeId=Material 的条目
python tools/list_patch_entries.py "<mod目录>\9ba626afa44a3aa3.patch_0"
# 详列某个材质的槽位与变量（判断它有哪些能力、槽位键是什么）
python tools/list_patch_entries.py "<同文件>" --detail 0x<材质id>
```
判读：槽里有 `OpacityClipMap(0xcbde381b)` ⇒ 能做镂空；有 `BaseColorMetalMap(0x848ba63b)`
⇒ 能做金属（它的 **alpha 通道就是金属度**）。

### Step 2 — 备份并确认基线
```powershell
$mat = "<mod 目录>"
Copy-Item "$mat\9ba626afa44a3aa3.patch_0*" "<备份目录>\" -Force
```
**先让用户确认未改动前的外观**。基线不对，后面每一次"没变化"都无法归因。

### Step 3 — 生成掩码贴图
```powershell
python tools/make_honeycomb_mask.py --out <临时目录> --radius 30 --pitch 112
```
输出 `honeycomb.png` + `honeycomb.dds`（1024² BC7 无 mip，像素 1,048,576 B，打孔约 27%）。
密度：`--radius` 大 / `--pitch` 小 ⇒ 孔更密。需要方形孔/其它图案就改这个脚本的画法。

### Step 4 — 合成子材质
```powershell
python tools/compose_material.py `
  --base "<mod目录>\9ba626afa44a3aa3.patch_0" `
  --file-id 0x<目标材质id> `
  --root-id 0xd84d04634a1e2f60 --root-dir ".dsh\roots\d84d04634a1e2f60" --child `
  --set-slot 1d57dcf3=0x<该角色 Nar 贴图id> `
  --set-slot 848ba63b=0x<该角色 底色贴图id> `
  --set-slot cbde381b=0x<用来当掩码的贴图条目id> `
  --out <临时目录> --name cut.patch_0 `
  --names-file tools/ref/HD2SDK-CommunityEdition/hashlists/shadervariables.txt
```

掩码贴图有两种来源：
- **复用该 patch 里已有一条同尺寸贴图**（`gpuSize` 必须等于 1,048,576），第 5 步原地覆盖它的数据；
- 或先用 `tools/add_texture.py --base <patch> --add-texture 0x<新id>=<条目目录>` 追加一条
  （条目目录需含 `toc.bin` + `gpu_resources.bin`，可以从别的 patch 抽出来复用）。

### Step 5 — 写入掩码像素、部署、校验
```powershell
python tools/embed_texture_data.py --patch "<临时目录>\cut.patch_0" `
  --texture 0x<掩码贴图id> --dds "<临时目录>\honeycomb.dds"
# 只改库里的文件（不要碰 data\）
Copy-Item "<临时目录>\cut.patch_0"               "<mod目录>\9ba626afa44a3aa3.patch_0" -Force
Copy-Item "<临时目录>\cut.patch_0.gpu_resources" "<mod目录>\9ba626afa44a3aa3.patch_0.gpu_resources" -Force
python tools/verify_deployed.py --mod "<mod 的库目录名>"
```

## 5. 判据表（上机看什么 → 结论）

| 现象 | 结论 | 下一步 |
|---|---|---|
| 部件在 + 圆形孔洞 + 金属感 | ✅ 成功 | 调密度/贴图收尾 |
| 部件在 + 有孔 + 无金属 | 镂空通了、金属度没生效 | `BaseColorMetalMap` 的 **alpha** 要够亮（≥128） |
| 部件在 + 金属 + 无孔 | 掩码没被读到 | 换掩码贴图；或把图案反相（黑底白孔） |
| **整片消失** | 该根材质这一族在这里不渲染 | 换 `.dsh\roots` 里另一个已验证的根材质；或退回 `9fc2`（只金属） |
| 满身是孔、大片消失 | 掩码极性反了或太黑 | 反相掩码 / 提高白底覆盖率 |

## 6. 已知边界与未解

- **不知道**为什么有的根材质整片消失（`ec55` / `a991`）。只有实测能分辨。
- 自发光**动画**（随时间流动）尚未确认，"均匀品红"说明静态自发光是通的。
- 衣服没拿到金属：衣服用的是**另一个**材质 id，需要单独按本 runbook 做一遍。
- 作者那份自带的编译载荷（`e577db2cf97fcddc` 的原始版本）里**材质变量是编译期折叠的**，
  改 TocData 变量无效 —— 只对"子材质 + 本体根材质"这条路，变量才是活的。

## 7. 回退

```powershell
$mat = "<mod 目录>"; $bk = "<备份目录>"
Copy-Item "$bk\*" $mat -Force
python tools/verify_deployed.py --mod "<mod 的库目录名>"
```

## 8. 工具速查（全部在 `tools/`）

| 工具 | 作用 |
|---|---|
| `make_honeycomb_mask.py` | 生成蜂窝掩码 PNG/DDS（自动处理 DX10 头） |
| `compose_material.py` | 合成材质：换根材质 / `--child` 子材质 / 覆盖槽位与变量 |
| `add_texture.py` | 往 patch 里追加贴图条目（数据接侧车末尾） |
| `embed_texture_data.py` | 原地等长替换某贴图的像素数据 |
| `verify_deployed.py` | 部署硬校验（符号链接目标 + sha256 + 索引链连续性） |
| `list_patch_entries.py` | 列 patch 条目 / 详列某材质的槽位与变量 |
| `who_defines.py` | 查某 fileId 由哪些索引定义（排查被高索引盖掉） |
| `pass_catalog.py` | 扫一批载荷，报每份的材质主 pass 签名与容量（换 pass 前必查） |
| `PayloadDump`（`dotnet run -c Release --project tools/PayloadDump -- --toc <游戏data> <id> <输出目录>`） | 从游戏里取任意根材质的裸 TocData + 载荷 |
