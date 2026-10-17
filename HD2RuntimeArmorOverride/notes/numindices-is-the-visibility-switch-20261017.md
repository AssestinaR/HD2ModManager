# 实机确认：`NumIndices` 就是可见性开关，粒度是 section（2026-10-17）

## 结论

| 命题 | 状态 | 证据 |
|---|---|---|
| `section.NumIndices = 0` 能隐藏几何 | ✅ **确认** | 全 patch 23 个 unit、683 个 section 清零 → 整个角色模型消失 |
| 粒度是 **section 级**，不是整件 | ✅ **确认** | `0x9841b16544c8fbae` 的 section 0..12 清零 → **胸部躯体消失，其余完好** |
| 渲染器在加载时读 TOC，不是别处缓存 | ✅ **确认** | 改文件（未改内存、未改代码）即生效 |
| 游戏实际使用的躯干条目是 `0x9841b16544c8fbae` | ✅ **确认** | 全清 `0xcae35ee15168145f` 无反应；清 `0x9841b16544c8fbae` 有反应 |

## 实验方法

改的是**已部署 mod 的 patch 文件字节**，不是内存、不是代码：

```
data\9ba626afa44a3aa3.patch_0  ──符号链接──>  E:\mod\1061927f...\9ba626afa44a3aa3.patch_0
```

`NumIndices` 的文件绝对偏移：

```
entry.TocDataOffset + section.Offset + 16
```

（section 记录 24 字节，`numIndices` 在 +16，见 `UnitMeshReader.cs:679-686`。
TOC 数据在 patch 里是**明文存储**，`PatchEntryPayloadReader` 直接按
`TocDataOffset/TocDataSize` 读取，没有解压，所以可以就地改 4 字节。）

## 一个把我坑了两次的陷阱（重要，写下来避免重犯）

`Copy-Item -Force` 覆盖一个**只读**目标文件时，**会保留目标的只读属性**。
随后 `File.WriteAllBytes` 抛 `UnauthorizedAccessException`，而我当时用
`Select-String` 过滤输出，只匹配 `patched|units=|fields=`，
**异常行被过滤掉了**，屏幕上只剩下"逐 unit 写入成功"的假象。

结果：两次"决定性实验"其实**文件一个字节都没改**，我据此得出"机制无效"的错误结论，
是用户指出"你的更改根本没应用到软连接指向的文件"才发现。

**正确做法**（现在固定在流程里）：

```powershell
Copy-Item $orig $lib -Force
[IO.File]::SetAttributes($lib,[IO.FileAttributes]::Archive)   # 必须显式清只读
# ... 改文件 ...
[IO.File]::SetAttributes($lib,[IO.FileAttributes]::ReadOnly)   # 改完恢复
```

并且**每次改完必须验证 SHA256 变化**，不能只看工具自报的写入条数。

## 对整体方案的影响

1. **运行期不需要重建 unit、不需要扩容、不需要改代码段。**
   每个装饰一个 dword，写 0 隐藏、写回原值恢复。
2. 但**"section = 装饰"只对我们自己用 vpk2patch + manifest 构建的包成立**
   （实测 11 个 section 对 11 个 manifest group，三角形数与材质 ID 逐个相等）。
   作者原始 Lunalice 包里是 22 个 section 对 12 个材质，一个材质跨 3–4 个 section，
   装饰边界不可判定。
3. 所以路径是：**用我们的管线出包 → 构建期产出 section 映射（已实现，
   `tools/unitprobe --emit-map`）→ 运行期按映射写 dword。**

## 还剩的未知

**运行期怎么拿到 unit 的 TOC 在内存里的基址。** 映射表里的偏移是 TOC 相对的，
Lua 必须先定位该 unit 资源在内存中的起点。这是下一步要解决的唯一问题。

参考：`armor_registry_runtime.lua` 已经有 `GetCurrentProcess` + `ReadProcessMemory`
+ `VirtualQuery` + 按签名扫描整套原语，可以用来扫 unit TOC 的签名。

## 备份与还原

* 原始 patch：`E:\tmp\hd2-mem\lib-patch_0.orig`（SHA256 `280DC065F3C667F2...`）
* 已还原，库文件恢复只读属性。
