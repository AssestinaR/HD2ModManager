# 结论：护甲装饰的运行期开关方案成立（2026-10-17）

## 最终链路

```
构建期（离线，一次）
  源模型 ──vpk2patch──> unit 资源 ──tools/unitprobe --emit-map──> section 映射表
                                                                    │
                                              装饰 → [(TOC 偏移, 原 NumIndices)]
                                                                    │
运行期（游戏内，每次切换）                                            ▼
  ① 扫内存定位该 unit 的 TOC 副本（section 记录签名 → 基址）
  ② 按玩家开关把对应 NumIndices 写成 0 或写回原值
  ③ 发布一次注册表 revision → auto_refresh 触发 unit 重载
  ④ 引擎重新解析那份被改过的缓冲 → 几何当场变化
```

## 每一步的证据

| 步骤 | 证据 |
|---|---|
| section 边界 = 装饰边界 | 11 个 section 对 11 个 manifest group，三角形数与材质 ID 逐个相等（`notes/decoration-section-map-20261017.md`） |
| `NumIndices = 0` 隐藏几何 | 改 patch 文件后重启，胸部躯体消失（`notes/numindices-is-the-visibility-switch-20261017.md`） |
| 粒度是 section 级 | 只清 section 0..12 → 胸部消失、其余完好 |
| 内存里有 TOC 副本 | 与文件 99.19% 相同，`基址 + section.Offset + 16` 与文件同值 |
| Lua 能在进程内定位并写入 | 探针 `wrote 52/52 numIndices to zero`，逐项回读确认 |
| 写入必须配一次重载 | 写入后画面不变；换装再换回来 → 胸部消失（`notes/runtime-toc-write-has-no-effect-20261017.md`） |
| 重载通道已存在 | `armor_refresh_route_probe.lua` 的 `auto_refresh.published(revision, changed)` |

重载通道的当前状态（`%LOCALAPPDATA%\CowboyBingus\Helldivers2\ArmorRegistry\refresh-auto.config`）：

```
schema=2
enabled=true
scope=local_equipped_body
confirm=single_player_ship_armory_closed
```

即：自动刷新**已启用**，作用域「本地已装备身体」，触发条件「单人 / 飞船内 / 军械库关闭」。
`refresh-auto.status` 显示 `state=auto_refresh_baseline revision=123`，说明它在等
revision 变化。所以**我们已有的协议 revision 机制本身就是重载触发器**。

## 为什么这个方案满足全部约束

| 约束 | 满足方式 |
|---|---|
| 43 件同时可见 | 7 个槽位 7 个键，远低于 18 的上限；装饰全在一个 unit 里 |
| 不合并多个 unit | 不合并：每个装饰仍是独立 section，只是共处一个 unit |
| 不扩容量、不改代码段 | 完全不需要；只写已可写的堆数据 |
| 切换不重启游戏 | 内存写入 + 重载，重载是换装本来就有的动作 |
| 一键回滚 | 原值就在映射表里，写回即可；重载一次复原 |

## 仍未完成的部分

1. **把探针抽成正式模块**：现在是一次性脚本，硬编码了一个 unit 和一组 section。
   正式版要读构建期产出的映射表、支持多 unit、支持开关双向。
2. **映射表要按装饰分组**：作者原始包是 22 section / 12 材质，一个材质跨多个 section，
   装饰边界不可判定。必须用我们自己的 vpk2patch + manifest 出包，才能拿到 1:1 的
   `装饰 → section` 映射。语义分组（饰带 + 它的金属件）用 `--groups` 显式声明。
3. **基址缓存与失效检测**：重载后 TOC 可能换地址。每次写前用签名复验；
   复验失败就重扫。
4. **性能**：全量扫描 8.9 GB 约需数十秒，只能做一次并缓存。首次定位要放在
   换装/进图时的空闲帧里分帧跑（探针已经这么做：每帧 32 个区域）。
5. **与现有注册表运行时整合**：`NumIndices` 写入应该并入 `apply` 事务，
   和现有的 blob 写入、revision 发布共用一套失败回滚。

## 相关文件

* `tools/unitprobe/` —— 离线读 patch、定位 unit/section、改字段、产出映射
* `scripts/build_lua_mod.py` —— Lua 打包成可导入 mod zip
* `scripts/make_section_toggle_probe.py` —— 生成运行期探针
* `scripts/find_unit_toc.py` —— 外部只读定位 TOC 副本
* 探针部署位置：`%LOCALAPPDATA%\MDL\Helldivers2\Mods\section_toggle_probe\mod.lua`
* 探针日志：`%LOCALAPPDATA%\CowboyBingus\Helldivers2\ArmorRegistry\section-toggle.status`
