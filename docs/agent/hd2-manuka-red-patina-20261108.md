# Manuka「侧面红斑、正面过白」复盘（2026-11-08）

> 结论先写：**是 lightwarp 烘焙在拿不到世界方向法线时仍按 ramp 压了一遍高光**。
> 已修：`7f0f535`。默认关闭，拿不到法线整段跳过。

## 一、症状与范围

- 只有 **Manuka_LaFiore / Manuka_JABIS** 两款出现；其他模型正常。
- 表现：身体侧面一圈、髋部、大腿外侧出现明显红斑；正面皮肤特别白；换角度不变（静态）。
- 用户记忆：“做法线贴图和 matcap 之前，Manuka 完全正常。”

## 二、排查过程（每一步都留了可复现的判据）

| 怀疑 | 怎么验的 | 结果 |
|---|---|---|
| matcap 叠加 | 扫两款 Manuka 全部 31 个材质 | **一个 `$matcap` 都没有** → 排除 |
| 法线贴图 | `HD2_NO_NORMAL_MAP=1` 重构建 | 画面无变化 → 排除 |
| 模板默认次表面散射 | 读 SDK `advanced.material`：`SubsurfaceIntensity=0.46`、`Diffusion=0.7` | 已置 0 并重构建 → 画面无变化 → 排除 |
| 底色贴图本身 | `HD2_DEBUG_FLAT_ALBEDO=1`（底色换中性灰 128） | **整模型变灰、红斑消失** → 问题在底色数据 |
| 到底是哪一步改了底色 | `HD2MOD_ALBEDO_DEBUG=1` 在"进入 `BuildBaseColorTexture`"和"编码前"各打一次 meanLum | 见下表 |

```
manuka_body     229.0  →  216.2     （被压暗 13）
manuka_face     188.8  →  186.4
manuka_costume  155.3  →  155.3     （越暗的图越不受影响）
```

`HD2_NO_LIGHTWARP=1` 时 body 保持 **229.0** → 锁定 lightwarp 烘焙。

## 三、根因

- Manuka 的 VMT 写了 `$lightwarptexture "…/lightwarp/basewarp"`；
- `LightwarpShadingEnabled` **默认开启**；
- 烘这条曲线需要「世界方向法线」（`UvNormalBake` 产出），Manuka 没烘出来；
- 旧代码在这种情况下按**“正面朝光”**兜底，注释写着“等于不动”——**实测并不等于不动**：
  它仍然按 ramp 的归一化曲线压高光。越亮的底色被压得越多 ⇒ 亮粉皮肤变成“暗部发红、亮部溢出成白”。

其他模型没有 `$lightwarptexture`，或底色本来就暗（155 那一档几乎不受影响），所以看不出来。

## 四、修法

`HD2SourceMigration/VpkConversion/SourceMaterialConverter.cs`：

1. **拿不到世界方向法线就整段跳过**（不再用“正面朝光”兜底）；
2. 该特性**默认关闭**，显式 `HD2_LIGHTWARP=1` 才开；`HD2_NO_LIGHTWARP=1` 仍然认（旧脚本不失效）。

修后实测：`manuka_body 229.0 → 229.0`、`face 188.8 → 188.8`、`costume 155.3 → 155.3`，底色零修改。
两款 Manuka 已重构建，用户实机确认正常。

## 五、留下的诊断开关（env 打开才生效，平时零开销）

- `HD2MOD_ALBEDO_DEBUG=1`：逐材质打印“进入时 / 编码前”的 `meanLum`。能把“暗化发生在哪一步”两步夹住。
- `HD2_DEBUG_FLAT_ALBEDO=1`：底色替换成中性灰 128。用来判定某个观感**来自底色还是光照**。
- `HD2_NO_NORMAL_MAP=1` / `HD2_NO_LIGHTWARP=1`：各自关掉一层，做干净对照。

## 六、教训

“没数据就当无事发生”的兜底分支最危险：它不报错、不改文件名、产物大小也不变，
只让像素悄悄偏一点。以后凡是这类兜底，要么**整段跳过**，要么把偏差打印出来。
