# GMod 武器离线实验脚本（2026-11-16）

结果报告：`../gmod-weapon-offline-validation-20261116.md`。全部是一次性实验脚本，**不是**产品代码。

| 文件 | 实验 | 作用 | 依赖 |
|---|---|---|---|
| `scripts/filediver_zz_lab_probe.go` + `filediver_lab_probe_main.go` | V1 | 放进 filediver 源码树（`datalibrary/`、`cmd/lab-probe/`）后构建；按游戏类型库步长导出 `ProjectileWeaponComponent` 原始记录 | filediver 源码、Go ≥ 1.25 |
| `scripts/v1_muzzle_flash_refs.py` | V1 | 用粒子资源哈希匹配记录与实体增量，得出枪口火焰引用与共享度 | `filediver -l -i "*"` 清单、上面的记录、`entity-delta-json-dumper` 输出 |
| `scripts/dlcmp.cs` | V1 | 用仓库 `Decryptor` 解密游戏 `.dl_bin`，与 filediver 内嵌快照逐字节比对 | `dotnet run dlcmp.cs`（.NET 10 文件程序） |
| `scripts/v2_roundtrip.py` | V2 | 动画 / 状态机 / 粒子经 HD2SDK-CE 序列化器往返 | HD2SDK-CE 源码（无许可证，仅本地测试） |
| `scripts/v2_anim_edit.py` | V2 | 改一条动画关键帧后重编码、重解析 | 同上 |
| `scripts/v2_particles_pmod.py` | V2 | 粒子经 hd2-particle-modder 数据类往返与覆盖率 | hd2-particle-modder 源码（MIT） |
| `scripts/unitrt.cs` | V2 | 武器 Unit 经仓库 `UnitMeshReader`/`UnitMeshWriter` 往返 | `HD2ModAdaptation` |
| `scripts/vpkx.cs` | V5 | 用仓库 `VpkArchive` 从 GMod VPK 取样本 | `HD2SourceModel` |
| `scripts/v5_sourceio_decode.py` | V5 | SourceIO 库层解码全部动画到 JSON | SourceIO 源码（MIT）、numpy |
| `scripts/v5_ref_decoder.py` | V5 | 独立解码器（按 SDK 定义自写），与 SourceIO 逐帧比较 | 无 |
| `scripts/v5_inspect_neutral.py`、`v5_attachments.py` | V5 | 查看分歧帧；列挂点与序列事件 | SourceIO |
| `evidence/assault_rifle.bones.json`、`assault_rifle.state_machine.json` | V0 | filediver 导出的 AR 骨骼表与状态机 | — |

外部工具版本：filediver v0.7.67（Release 资产 SHA-256 已核对）、filediver 源码 master（2026-11-14）、HD2SDK-CE `4df125a`（2026-10-28）、hd2-particle-modder `a2f163f`（2026-09-25）、SourceIO `a50cea6`（2026-11-14）、Go 1.27.4（官方 SHA-256 已核对）。
