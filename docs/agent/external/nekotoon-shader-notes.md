# NekoToon 着色器：可核对的事实与取证方法

> 2026-11-14。来源：`NPCodex/L4D2_Dxvk_32to64_Bridge-Nightlybuild`（L4N 的 dxvk 桥接包）。
> 起因：MourningVeil 移植到 HD2 后，若干件（细链、纹身、月牙、耳坠）的镂空与 L4D2 观感不一致，
> 而我们此前只能靠"alpha 统计"去猜，猜不出来。**这份文档记录的是源侧的稳定事实，不是推测。**

## 0. 参考仓库（已克隆）

```
E:\tmp\ref\L4N-dxvk          648 个文件（--depth 1）
  runtime/l4n/payload/left4dead2/shaders/fxc/
    nekotoon.vmt          11,396 B   面向 mod 作者的官方参数文档（3.1）
    neko_toon_ps30.vcs 2,133,568 B   NekoToon 像素着色器库（编译产物）
    neko_toon_vs30.vcs 2,317,244 B   顶点着色器库
    pbr.vmt / pbr_ps30.vcs           pbr 变体
```

## 1. 官方文档里与透明相关的原文（`nekotoon.vmt` 109–116 行）

```
// $AlphaTest               0    // 高性价比的透明渲染
// $AllowAlphaToCoverage    0    // 高质量alphatest，需配合dxvk使用
// $AlphaTestReference      0.5
// $Translucent             1    // 古尔丹说：这是特定情况下，效果最好的透明渲染
// $Alpha                    1.0  // 调整透明度，写入此参数时会强制启用 $translucent
// $Alpha2                   1.0  // 调整透明度，当 $translucent 未启用时候，写入此参数会强制启用
//                               //   $alphatest 和 $allowalphatocoverage
// $Additive                0    // 与后面的物体产生叠加效果
// $Multiply                0    // 与后面的物体产生正片叠底效果
```

由此得到三条**我们此前没有实现**的规格：

1. `$AlphaTestReference` 是可配置的（默认 0.5）⇒ `alphaclip` 的 `OpacityThreshold` 应当读它，
   不能写死 0.5。
2. `$Alpha` 存在 ⇒ 等价于开启 `$translucent`。
3. `$Alpha2` 存在且没有 `$translucent` ⇒ 等价于开启 `$alphatest` + `$allowalphatocoverage`。

## 2. 另一条通道约定（`$OutlineTexture` 那段，102–104 行）

```
推荐格式：512分辨率，无mipmap，ATI1N压缩格式
当格式为 ATI1N 时，使用 R 通道作为描边蒙版，其它格式使用 A 通道
```

⇒ 这个着色器家族**按贴图格式决定取哪个通道**。对底图同样要警惕：单通道格式（ATI1N/BC4）
读 R，多通道读 A。实测 MourningVeil 的三张底图都是 **DXT5**，所以 A 通道是对的；
但这是碰巧，规则本身必须实现。

## 3. `$mask1` / `$mask2` 的通道语义（19 行、29 行）

```
$mask1：R = matcap 蒙版；G = lightwarp 的 UV 的 V 映射；B = matcap2 蒙版；A = AO
$mask2：R = RimLight/RimShade 蒙版；G = 材质染色蒙版；B = $BaseTexture2 的蒙版
```

`$Emission 2` 时 `$EmissionTexture` 是"蒙版模式"（136 行）。

## 4. `.vcs` 容器格式（已解出，可复现）

```
'@LZMA'（5 字节）
   u32 rawSize        解压后字节数
   u32 compSize       压缩字节数
   u8  props = 0x5D   LZMA1，lc=3 lp=0 pb=2
   5 字节 dict size   = 0x01000000（16 MB），紧跟一处 5 字节
   RAW LZMA 流        从 '@LZMA'+18 开始，长度 compSize
```

实测 `neko_toon_ps30.vcs`：**576 个块全部解压成功**，每块内是 DX9 `ps_3_0` 字节码 +
`CTAB` 常量表。解出来的常量名包括 `BaseTextureSampler`、`g_DiffuseModulation`、
`_RimLightParams`、`g_BackFaceShade`、`g_HsvParams`、`_PSREG_EYEPOS_SPEC_EXPONENT` 等。

复现脚本：`E:\tmp\mvscan\vcs_probe.py`（拆块 + 常量表 + opcode 普查）。
注意：脚本里的 opcode 普查还**没有对齐**（`texld` 计数异常），下一步要用规范的 SM3 解码
（version token 之后按 `(length<<24)|opcode` 走）重做，目标是指认
**`texkill`（D3DSIO_TEXKILL = 50）到底作用于哪个寄存器、那个寄存器来自哪个 sampler**。

## 5. 已核实与仍未解决

| 结论 | 依据 |
|---|---|
| 我们的镂空遮罩 = 源 alpha 的逐字节拷贝 | 源码 `BuildOpacityMaskPixels` + 产物两次实测一致 |
| 底图 DXT5，用 A 通道正确 | `milfy tex` 实测格式 + 文档 102 行约定 |
| `alphatest` 语义与 Source 一致（ref 默认 0.5） | 官方文档 109–111 行 |
| **仍未解决**：为什么细链/纹身在 L4D2 里是细的、在我们这里是"胶带/黑块" | 需要第 4 节那一步的反汇编结论 |
